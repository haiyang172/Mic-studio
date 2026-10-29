#include "MicStudioEcho.h"
#include <array>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
struct Echo {
    void* state;
    explicit Echo(int rate) : state(MS_EchoCreate(rate, rate / 100, 200)) { Require(state != nullptr, "create"); }
    ~Echo() { MS_EchoDestroy(state); }
};
void Signals(int rate) {
    const int frame = rate / 100;
    Echo echo(rate);
    std::vector<int16_t> far(frame), mic(frame), out(frame), history(rate * 7);
    uint32_t random = 319u;
    double dryPower = 0, wetPower = 0;
    const int delay = rate / 100; // 10 ms acoustic path, well inside the tested 200 ms tail.
    for (int block = 0; block < 600; ++block) {
        for (int n = 0; n < frame; ++n) {
            random = random * 1664525u + 1013904223u;
            const int index = block * frame + n;
            const auto sample = static_cast<int16_t>((static_cast<int>(random >> 17) - 16384) / 3);
            far[n] = sample; history[index] = sample;
            mic[n] = index >= delay ? static_cast<int16_t>(history[index - delay] * .6) : 0;
        }
        Require(MS_EchoProcess(echo.state, mic.data(), far.data(), out.data()) == 0, "process");
        if (block > 400) for (int n = 0; n < frame; ++n) {
            dryPower += static_cast<double>(mic[n]) * mic[n]; wetPower += static_cast<double>(out[n]) * out[n];
        }
    }
    const double erle = 10 * std::log10(dryPower / std::max(1.0, wetPower));
    std::cout << rate << " Hz aligned synthetic echo attenuation: " << erle << " dB\n";
    Require(erle > 15, "insufficient aligned echo attenuation");
    // Independent near-end speech-band tone while the far-end reference continues.
    double sine = 0, cosine = 0;
    for (int block = 600; block < 700; ++block) {
        for (int n = 0; n < frame; ++n) {
            random = random * 1664525u + 1013904223u;
            const int index = block * frame + n;
            far[n] = static_cast<int16_t>((static_cast<int>(random >> 17) - 16384) / 3);
            history[index] = far[n];
            const double near = 2000 * std::sin(2 * 3.141592653589793 * 733 * index / rate);
            mic[n] = static_cast<int16_t>(near + history[index - delay] * .6);
        }
        Require(MS_EchoProcess(echo.state, mic.data(), far.data(), out.data()) == 0, "double talk");
        for (int n = 0; n < frame; ++n) {
            const auto phase = 2 * 3.141592653589793 * 733 * (block * frame + n) / rate;
            sine += out[n] * std::sin(phase); cosine += out[n] * std::cos(phase);
        }
    }
    const double amplitude = 2 * std::sqrt(sine * sine + cosine * cosine) / rate;
    std::cout << "Near-end tone retained: " << amplitude / 2000 << '\n';
    Require(amplitude > 1400 && amplitude < 2600, "near-end speech attenuated or amplified excessively");
    MS_EchoReset(echo.state);
    std::fill(far.begin(), far.end(), 0); std::fill(mic.begin(), mic.end(), 0);
    for (int block = 0; block < 20; ++block) {
        MS_EchoProcess(echo.state, mic.data(), far.data(), out.data());
        for (auto sample : out) Require(sample == 0, "stale echo history after reset");
    }
}
}
int main() {
    try {
        Require(MS_EchoCreate(1000, 10, 200) == nullptr, "reject rate");
        Require(MS_EchoCreate(48000, 0, 200) == nullptr, "reject frame");
        Require(MS_EchoProcess(nullptr, nullptr, nullptr, nullptr) == -1, "reject nulls");
        MS_EchoReset(nullptr); MS_EchoDestroy(nullptr);
        Signals(16000); Signals(48000);
        std::cout << "PASS native echo synthetic signals; no audio devices opened.\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
