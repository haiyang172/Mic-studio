// Fixed Windows wire contract; little endian, pack4. See README.md.
#pragma once

#include <cstddef>
#include <cstdint>

#define MICSTUDIO_APO_PARAMS_VERSION 2u
#define MICSTUDIO_APO_MAX_BANDS 8u
#define MICSTUDIO_APO_PARAMS_SIZE 224u

enum MicStudioApoFilterType : uint32_t
{
    MICSTUDIO_APO_FILTER_HIGHPASS = 0,
    MICSTUDIO_APO_FILTER_LOWSHELF = 1,
    MICSTUDIO_APO_FILTER_PEAK = 2,
    MICSTUDIO_APO_FILTER_HIGHSHELF = 3,
    MICSTUDIO_APO_FILTER_LOWPASS = 4
};

#pragma pack(push, 4)
struct MicStudioApoBandParams
{
    uint32_t Enabled;
    uint32_t Type;
    float FrequencyHz;
    float GainDb;
    float Q;
};

// Written only by the non-realtime UI/installer process. The audio thread
// validates Version and Sequence, copies one complete snapshot, and falls back
// to strict passthrough when the snapshot is incomplete or unsupported.
struct MicStudioApoParams
{
    uint32_t Version;
    uint32_t SizeBytes;
    uint32_t Sequence;
    uint32_t Enabled;
    uint32_t SampleRate;
    uint32_t Channels;
    uint32_t EqEnabled;
    uint32_t LimiterEnabled;
    float InputGainDb;
    float OutputGainDb;
    float OutputCeilingDb;
    uint32_t TestMode; // 0 normal, 1 fixed -6 dB without EQ/limiter.
    MicStudioApoBandParams Bands[MICSTUDIO_APO_MAX_BANDS];
    uint64_t HeartbeatTickMs; // GetTickCount64, refreshed by the UI at least each second.
    uint32_t OwnerProcessId;
    uint32_t Reserved;
};
#pragma pack(pop)
static_assert(sizeof(MicStudioApoBandParams) == 20, "Band ABI changed");
static_assert(sizeof(MicStudioApoParams) == MICSTUDIO_APO_PARAMS_SIZE, "Parameter ABI changed");
static_assert(offsetof(MicStudioApoParams, Sequence) == 8, "Sequence ABI changed");
static_assert(offsetof(MicStudioApoParams, Bands) == 48, "Band array ABI changed");
static_assert(offsetof(MicStudioApoParams, HeartbeatTickMs) == 208, "Heartbeat ABI changed");

inline MicStudioApoParams MicStudioDefaultParams() noexcept
{
    MicStudioApoParams p{};
    p.Version = MICSTUDIO_APO_PARAMS_VERSION; p.SizeBytes = sizeof(p);
    p.EqEnabled = p.LimiterEnabled = 1; p.OutputCeilingDb = -1;
    constexpr uint32_t types[8] = { 0, 1, 2, 2, 2, 2, 3, 4 };
    constexpr float frequencies[8] = { 20, 120, 250, 500, 1500, 3500, 7000, 20000 };
    for (uint32_t i = 0; i < 8; ++i) p.Bands[i] = { 1, types[i], frequencies[i], 0, 0.70710678f };
    return p; // Disabled until a complete, explicitly enabled snapshot arrives.
}
