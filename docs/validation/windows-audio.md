# Windows audio implementation and validation

Validated on 2026-10-28 (Australia/Sydney), using .NET SDK 10.0.403 and pinned
NAudio.Wasapi/NAudio.Core 2.3.0. Newer NAudio major versions exist; 2.3.0 is
deliberately pinned to the established WASAPI API used here. NAudio is MIT licensed:
https://github.com/naudio/NAudio/tree/v2.3.0

## Implemented behavior

- Core Audio enumeration returns stable endpoint IDs, full names, default console
  and communication roles, state, mix sample rate and channel count. UI labels use
  a short ID hash to distinguish matching names. Endpoint notifications trigger a
  refresh. Enumeration does not start a capture stream or change system defaults.
- Windows input endpoint volume and mute can be read or explicitly changed for
  the selected active recording endpoint. Changes affect any application using
  that microphone. No level/mute writes happen at startup or during enumeration.
- Explicit shared-mode WASAPI capture supports IEEE float32 and PCM 8/16/24/32.
  Peak and RMS measurements aggregate all packets between UI updates. They measure
  the selected shared endpoint, which can already contain Windows/APO effects.
- The native DSP is applied once before fanout to a file, CABLE Input and optional
  headphone monitor. It must not be duplicated by an upstream managed APO EQ.
- Output sample rate conversion uses WDL; mono/stereo/multichannel mapping is
  explicit. The output queue is bounded to 300 ms. Queue resets are counted as
  discontinuities; this queue length is **not end-to-end latency**.
- CABLE sending rejects physical playback endpoints. Selecting CABLE Output as
  the source while sending back to CABLE Input is rejected to prevent feedback.
- Monitoring requires a selected physical output and explicit headphone
  confirmation. It has an independent fixed -12 dB trim and sample clamp.
- User-started WAV recording is local, uses CreateNew, and is capped at 1–120
  seconds (default 30). Microphone capture and output are stopped and disposed
  explicitly. Device removal stops the stream without selecting a fallback.
- WAV playback requires an explicit physical output and stopped capture.
- Meter and engine status events carry a session ID, so the UI can discard old
  queued events after stop, device changes or a new audio session.
- Recording can explicitly save the selected endpoint before local DSP. This is
  a same-source comparison reference, not proof of a raw path without system effects.
- Offline WAV A/B opens no audio device. It generates a new reference and processed
  pair from the same performance, compensates the denoiser's fixed buffering and
  matches whole-file RMS by attenuation to a common level below -1 dBFS sample peaks.
  It is not LUFS matching or true-peak limiting. Silence cannot be RMS matched.
  Input is never replaced; output names must be new. Processing streams in bounded
  memory, accepts at most 120 seconds, and supports cancellation. If another process
  takes a final filename during commit, it fails without overwriting that file.

## Reproducible commands

```powershell
.\.tools\dotnet\dotnet.exe build src\MicStudio.WindowsAudio\MicStudio.WindowsAudio.csproj -c Release
.\.tools\dotnet\dotnet.exe run --project tests\MicStudio.WindowsAudio.Tests -c Release -- --tests-only
.\.tools\dotnet\dotnet.exe run --project tests\MicStudio.WindowsAudio.Tests -c Release -- --devices
```

The default invocation runs synthetic/idle tests followed by redacted endpoint
enumeration. `--tests-only` never starts capture or playback. `--devices` only
enumerates. An optional `--capture <endpoint-id-or-short-hash>` deliberately opens
that endpoint for three seconds and prints levels only; **this physical capture
command was not executed during this development session**.

`--cable-smoke` requires a matching installed CABLE Input/Output pair and uses only
that virtual pair. It sends a 997 Hz sine at -36 dBFS for one second, measures the
tone projection without saving files, verifies application AudioEngine native
gain, then exercises concurrent stop and immediate restart. It never opens a
physical microphone, speaker, or monitor and never falls back to another device.

## Actual results

- Build: passed, zero warnings and zero errors.
- 25 synthetic/idle tests: passed. They cover PCM decoding/sign extension,
  nonfinite float sanitization, peak/RMS, silence, channel mapping, monitor trim,
  bounded queue overflow/invalid frames/silence, required explicit input and
  headphone selection, idle concurrent stop/dispose, CABLE direction and input-volume
  validation. Generated temporary WAV tests verify RMS matching using actual output
  samples, DSP gain, denoise alignment, silence, original hash preservation, no
  overwrites, the 120-second bound and cancellation cleanup. System volume/mute
  writes were not executed during development.
- Ordinary-privilege enumeration: succeeded. USB PnP microphone was active at
  48 kHz / two channels and was the default console and communication input.
  CABLE Input and CABLE Output were active at 48 kHz / two channels.
- Read-only endpoint volume query (`--input-volume`) succeeded for the USB PnP
  microphone without capture or writes: scalar 1.0, unmuted, reported range
  0–23.8125 dB in 0.1875 dB steps. These are Windows endpoint-reported values,
  not an independently calibrated analog-gain measurement.
- Virtual CABLE signal smoke: passed. Latest run after session/source-recording
  changes received 63,840 frames, RMS -40.25 dBFS over capture including silence,
  997 Hz projection -38.48 dBFS.
  This proves only the installed virtual path carried a synthetic signal.
- Application AudioEngine capture-to-native-DSP on the virtual CABLE receiver:
  passed; measured -6.000 dB for a requested -6 dB gain change.
- Active virtual capture release: 12 concurrent stop requests and eight immediate
  start/stop iterations passed without a crash or selecting another endpoint.

## Still requires real acceptance

Physical microphone recording/normal speech/quiet tails/keyboard noise/listening,
APO attachment and real processing verification, Discord calls, microphone unplug
and reconnect, sleep/wake, cross-device sample rates, sustained one-hour clock
drift and end-to-end latency are not established by these tests. No perception of
sound quality can be inferred from compilation, synthetic DSP tests or endpoint
enumeration. Idle disposal tests do not prove active hardware release; the virtual
smoke separately exercises active virtual capture release.
