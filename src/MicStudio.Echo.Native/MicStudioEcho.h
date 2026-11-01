#pragma once
#include <stdint.h>
#if defined(_WIN32) && defined(MICSTUDIO_ECHO_BUILD)
#define MS_ECHO_API extern "C" __declspec(dllexport)
#elif defined(_WIN32)
#define MS_ECHO_API extern "C" __declspec(dllimport)
#else
#define MS_ECHO_API extern "C" __attribute__((visibility("default")))
#endif

// Caller serializes access to each handle. No capture, playback, network or file I/O.
// The reference must represent the physical speaker stream and precede its acoustic echo.
MS_ECHO_API void* MS_EchoCreate(int sampleRate, int frameSamples, int tailMs) noexcept;
MS_ECHO_API int MS_EchoProcess(void* handle, const int16_t* microphone,
    const int16_t* reference, int16_t* output) noexcept;
MS_ECHO_API void MS_EchoReset(void* handle) noexcept;
MS_ECHO_API void MS_EchoDestroy(void* handle) noexcept;
