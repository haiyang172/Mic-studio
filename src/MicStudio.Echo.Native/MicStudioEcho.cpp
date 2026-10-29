#include "MicStudioEcho.h"
#include <speex/speex_echo.h>

MS_ECHO_API void* MS_EchoCreate(int sampleRate, int frameSamples, int tailMs) noexcept
{
    if (sampleRate < 8000 || sampleRate > 96000 || frameSamples != sampleRate / 100 ||
        sampleRate % 100 != 0 || tailMs < 20 || tailMs > 500) return nullptr;
    auto* state = speex_echo_state_init(frameSamples, sampleRate * tailMs / 1000);
    if (state == nullptr) return nullptr;
    if (speex_echo_ctl(state, SPEEX_ECHO_SET_SAMPLING_RATE, &sampleRate) != 0)
    {
        speex_echo_state_destroy(state); return nullptr;
    }
    return state;
}

MS_ECHO_API int MS_EchoProcess(void* handle, const int16_t* microphone,
    const int16_t* reference, int16_t* output) noexcept
{
    if (!handle || !microphone || !reference || !output) return -1;
    speex_echo_cancellation(static_cast<SpeexEchoState*>(handle), microphone, reference, output);
    return 0;
}

MS_ECHO_API void MS_EchoReset(void* handle) noexcept
{
    if (handle) speex_echo_state_reset(static_cast<SpeexEchoState*>(handle));
}

MS_ECHO_API void MS_EchoDestroy(void* handle) noexcept
{
    if (handle) speex_echo_state_destroy(static_cast<SpeexEchoState*>(handle));
}
