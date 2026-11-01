# Desktop playback-reference echo cancellation

This separate desktop DSP helper wraps the official SpeexDSP 1.2.1 acoustic echo canceller. It is not a system APO and does not register a driver. The pinned original source is fetched by SHA-256, with its BSD-style attribution retained in `SpeexDSP-COPYING.txt` beside build outputs.

The C ABI processes 10 ms signed-16-bit microphone/reference frames synchronously. The caller must provide a physical playback reference with correct timing, serialize each state, manage resampling/drift and handle missing reference explicitly. The helper never starts capture/playback or writes audio files. Passing speech through a gate or limiter is not echo cancellation.

Tests measure attenuation of an aligned synthetic echo path at 16/48 kHz, retention of independent near-end tone during double talk, and reset. They do not establish real-room, USB-clock, nonlinear-speaker or Discord acceptance. Frontend processing remains a preview until those cases are measured.
