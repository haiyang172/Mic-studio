#pragma once
#include "MicStudioApoParams.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>

namespace micstudio
{
constexpr double Pi = 3.1415926535897932384626433832795;
inline double Linear(double db) noexcept { return std::pow(10.0, db / 20.0); }
inline bool Range(float v, float lo, float hi) noexcept { return std::isfinite(v) && v >= lo && v <= hi; }
inline bool ValidParams(const MicStudioApoParams& p, uint32_t rate, uint32_t channels) noexcept
{
    if (p.Version != MICSTUDIO_APO_PARAMS_VERSION || p.SizeBytes != sizeof(p) || (p.Sequence & 1u) ||
        p.Enabled > 1 || p.EqEnabled > 1 || p.LimiterEnabled > 1 || p.TestMode > 1 || p.Reserved != 0 ||
        (p.SampleRate != 0 && p.SampleRate != rate) || (p.Channels != 0 && p.Channels != channels) ||
        !Range(p.InputGainDb, -24, 30) || !Range(p.OutputGainDb, -24, 0) || !Range(p.OutputCeilingDb, -12, -.1f)) return false;
    constexpr uint32_t types[8] = { 0, 1, 2, 2, 2, 2, 3, 4 };
    for (uint32_t i = 0; i < 8; ++i)
    {
        const auto& b = p.Bands[i];
        if (b.Enabled > 1 || b.Type != types[i] || !Range(b.FrequencyHz, 20, i == 0 ? 250.f : 20000.f) ||
            !Range(b.GainDb, -12, 12) || !Range(b.Q, .2f, 10)) return false;
    }
    return rate >= 8000 && rate <= 384000 && channels >= 1 && channels <= 8;
}

struct Biquad
{
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1 = 0, z2 = 0;
    double Process(double sample) noexcept
    {
        const double result = sample * b0 + z1;
        z1 = sample * b1 - result * a1 + z2; z2 = sample * b2 - result * a2;
        if (std::abs(z1) < 1e-25) z1 = 0;
        if (std::abs(z2) < 1e-25) z2 = 0;
        return result;
    }
    static Biquad From(const MicStudioApoBandParams& b, uint32_t rate) noexcept
    {
        if (!b.Enabled) return {};
        const double w = 2 * Pi * std::min<double>(b.FrequencyHz, rate * .48) / rate;
        const double c = std::cos(w), alpha = std::sin(w) / (2 * b.Q);
        const double A = std::pow(10.0, b.GainDb / 40.0), sqrtA = std::sqrt(A);
        double n0, n1, n2, d0, d1, d2;
        switch (b.Type)
        {
        case MICSTUDIO_APO_FILTER_HIGHPASS:
            n0 = (1 + c) / 2; n1 = -(1 + c); n2 = n0; d0 = 1 + alpha; d1 = -2 * c; d2 = 1 - alpha; break;
        case MICSTUDIO_APO_FILTER_LOWPASS:
            n0 = (1 - c) / 2; n1 = 1 - c; n2 = n0; d0 = 1 + alpha; d1 = -2 * c; d2 = 1 - alpha; break;
        case MICSTUDIO_APO_FILTER_LOWSHELF:
            n0 = A * ((A + 1) - (A - 1) * c + 2 * sqrtA * alpha);
            n1 = 2 * A * ((A - 1) - (A + 1) * c); n2 = A * ((A + 1) - (A - 1) * c - 2 * sqrtA * alpha);
            d0 = (A + 1) + (A - 1) * c + 2 * sqrtA * alpha;
            d1 = -2 * ((A - 1) + (A + 1) * c); d2 = (A + 1) + (A - 1) * c - 2 * sqrtA * alpha; break;
        case MICSTUDIO_APO_FILTER_HIGHSHELF:
            n0 = A * ((A + 1) + (A - 1) * c + 2 * sqrtA * alpha);
            n1 = -2 * A * ((A - 1) + (A + 1) * c); n2 = A * ((A + 1) + (A - 1) * c - 2 * sqrtA * alpha);
            d0 = (A + 1) - (A - 1) * c + 2 * sqrtA * alpha;
            d1 = 2 * ((A - 1) - (A + 1) * c); d2 = (A + 1) - (A - 1) * c - 2 * sqrtA * alpha; break;
        default:
            n0 = 1 + alpha * A; n1 = -2 * c; n2 = 1 - alpha * A;
            d0 = 1 + alpha / A; d1 = -2 * c; d2 = 1 - alpha / A; break;
        }
        return { n0 / d0, n1 / d0, n2 / d0, d1 / d0, d2 / d0, 0, 0 };
    }
};

// One stream, embedded fixed-size history. No allocation, locks, file access,
// OS calls, or unbounded retry loops in Adopt/Process.
class Dsp
{
    using FilterBank = std::array<std::array<Biquad, 8>, 8>;
    uint32_t rate_ = 48000, channels_ = 1, fadeFrames_ = 480, fadeRemaining_ = 0;
    FilterBank filters_{}, previous_{};
    MicStudioApoParams settings_ = MicStudioDefaultParams();
    bool valid_ = false;
    double gain_ = 1, targetGain_ = 1, gainRamp_ = 0, ceiling_ = 1, limiter_ = 1, limiterRelease_ = 0;
public:
    void Configure(uint32_t rate, uint32_t channels) noexcept
    {
        rate_ = rate; channels_ = channels; fadeFrames_ = std::max(1u, rate / 100);
        gainRamp_ = std::exp(-1.0 / (.005 * rate)); limiterRelease_ = std::exp(-1.0 / (.080 * rate)); Reset();
    }
    void Reset() noexcept
    {
        filters_ = {}; previous_ = {}; fadeRemaining_ = 0; gain_ = targetGain_ = limiter_ = 1;
        valid_ = false; settings_ = MicStudioDefaultParams();
    }
    void Invalidate() noexcept { valid_ = false; }
    bool Adopt(const MicStudioApoParams& p) noexcept
    {
        if (!ValidParams(p, rate_, channels_)) { Invalidate(); return false; }
        if (valid_ && std::memcmp(&p, &settings_, sizeof(p)) == 0) return true;
        const bool wasActive = valid_ && settings_.Enabled;
        const bool changedEq = p.EqEnabled != settings_.EqEnabled || p.TestMode != settings_.TestMode ||
            std::memcmp(p.Bands, settings_.Bands, sizeof(p.Bands)) != 0;
        if (changedEq || !wasActive)
        {
            previous_ = filters_;
            for (uint32_t c = 0; c < channels_; ++c)
                for (uint32_t b = 0; b < 8; ++b)
                    filters_[c][b] = p.EqEnabled && p.TestMode == 0 ? Biquad::From(p.Bands[b], rate_) : Biquad{};
            fadeRemaining_ = wasActive ? fadeFrames_ : 0;
        }
        settings_ = p; valid_ = true;
        targetGain_ = p.TestMode == 1 ? Linear(-6) : Linear(p.InputGainDb + p.OutputGainDb);
        ceiling_ = Linear(p.OutputCeilingDb);
        if (!wasActive) { gain_ = targetGain_; limiter_ = 1; }
        return true;
    }
    bool Active() const noexcept { return valid_ && settings_.Enabled != 0; }
    void Process(const float* input, float* output, uint32_t frames, bool silentInput = false) noexcept
    {
        if (!Active())
        {
            if (silentInput) std::memset(output, 0, static_cast<size_t>(frames) * channels_ * sizeof(float));
            else if (input != output) std::memmove(output, input, static_cast<size_t>(frames) * channels_ * sizeof(float));
            return;
        }
        for (uint32_t frame = 0; frame < frames; ++frame)
        {
            gain_ = targetGain_ + (gain_ - targetGain_) * gainRamp_;
            double values[8]{}; double peak = 0;
            for (uint32_t c = 0; c < channels_; ++c)
            {
                const auto i = static_cast<size_t>(frame) * channels_ + c;
                const double before = (!silentInput && std::isfinite(input[i]) ? input[i] : 0) * gain_;
                double value = before;
                for (auto& filter : filters_[c]) value = filter.Process(value);
                if (fadeRemaining_ != 0)
                {
                    double old = before;
                    for (auto& filter : previous_[c]) old = filter.Process(old);
                    value = old + (value - old) * (1.0 - static_cast<double>(fadeRemaining_) / fadeFrames_);
                }
                values[c] = std::isfinite(value) ? value : 0; peak = std::max(peak, std::abs(values[c]));
            }
            const double target = peak > ceiling_ ? ceiling_ / peak : 1;
            limiter_ = settings_.LimiterEnabled && settings_.TestMode == 0 ?
                (target < limiter_ ? target : limiterRelease_ * limiter_ + (1 - limiterRelease_) * target) : 1;
            for (uint32_t c = 0; c < channels_; ++c)
            {
                const double v = std::clamp(values[c] * limiter_, -static_cast<double>(std::numeric_limits<float>::max()), static_cast<double>(std::numeric_limits<float>::max()));
                output[static_cast<size_t>(frame) * channels_ + c] = static_cast<float>(v);
            }
            if (fadeRemaining_ != 0) --fadeRemaining_;
        }
    }
};
}
