// Mic Studio native APO parameter contract.
// Status: scaffold only. This header is not yet compiled or deployed; see README.md.
#pragma once

#include <stdint.h>

#define MICSTUDIO_APO_PARAMS_VERSION 1u
#define MICSTUDIO_APO_MAX_BANDS 8u

enum MicStudioApoFilterType : uint32_t
{
    MICSTUDIO_APO_FILTER_HIGHPASS = 0,
    MICSTUDIO_APO_FILTER_LOWSHELF = 1,
    MICSTUDIO_APO_FILTER_PEAK = 2,
    MICSTUDIO_APO_FILTER_HIGHSHELF = 3,
    MICSTUDIO_APO_FILTER_LOWPASS = 4
};

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
    uint32_t Sequence;
    uint32_t Enabled;
    uint32_t SampleRate;
    uint32_t Channels;
    float InputGainDb;
    float OutputCeilingDb;
    MicStudioApoBandParams Bands[MICSTUDIO_APO_MAX_BANDS];
};
