# Mic Studio audio architecture

## Native application and data ownership

The product is a WPF desktop executable targeting .NET 10 on Windows x64. It does not depend on a web view, browser permissions, an account or an audio upload service. The main application runs at ordinary user privilege. It opens the microphone only after the user starts a measurement, recording or cable session. Closing stops those Mic Studio sessions; Equalizer APO's independent system processing may continue. The release bundles the reviewed original standard VB-CABLE archive and attribution; installing it remains an explicit action through the vendor's signed installer and UAC, and an existing working installation is not automatically replaced.

Core owns portable sound parameters and DSP. WindowsAudio owns endpoint enumeration, capture/render and local WAV files. Apo owns generation and guarded installation of device-scoped Equalizer APO configuration. The application owns view state and explicit user actions. A saved preset, a saved APO file and a verified audio path are distinct states.

## Direct Equalizer APO path

```text
Selected physical recording endpoint
  → Windows system effects / Equalizer APO
  → Any compatible recording, calling or streaming application
```

The generated configuration is restricted to the selected endpoint ID and linear EQ/input-output gain. It does not create a virtual microphone. APO detection or a successful write does not prove that another application's capture path executes the effects. The UI therefore retains an unverified state until the relevant checks are completed.

The local dynamics, denoiser and creative effects are not represented by silently ineffective APO controls. Direct-mode advanced effects require a separately adapted compatible APO plugin; generic VST/VST3 loading is not implemented. Peace-managed and externally changed configuration must not be silently overwritten. Device binding remains the official APO device configuration tool's responsibility.

## Optional cable path and local tests

```text
Selected physical recording endpoint
  → WASAPI shared capture
  → Mic Studio DSP, once when enabled
  → CABLE Input playback endpoint
  → CABLE Output recording endpoint
  → Selected input of a recording, calling or streaming application
```

WASAPI shared capture may already include system processing. A meter before Mic Studio's DSP is therefore called the captured input, not guaranteed raw microphone audio. Likewise, two captures of the same system endpoint do not establish a raw/processed comparison. If both APO and local processing are active, the operator must avoid duplicating the same EQ or gain. The software must not claim automatic verification of the system effect state.

The same processed buffer fans out to cable, optional confirmed headphone monitoring, and recording. DSP is not run separately for each consumer. Monitoring is independent of cable transmission and requires a selected physical headphone output plus explicit confirmation. Other applications' output stays on the user's normal headphones or speakers; it must not be routed into CABLE Input, which could form a loop. Discord is one optional example of a compatible input consumer, not a required integration.

Device selections use endpoint IDs, not friendly names or unstable list positions. On removal, stop the selected session and retain its selection. Do not silently open a different microphone. Actual capture/render errors and buffer reset counts support diagnostics; they do not establish subjective quality or measured end-to-end latency.

## DSP implementation

The local floating-point chain is:

```text
Fixed-floor spectral noise suppression
  → digital input gain
  → high-pass / low shelf / 4 peak EQs / high shelf / low-pass
  → linked compressor
  → optional gentle downward expander with hold/release
  → optional saturation / ring modulation / delay / reverb
  → output compensation
  → linked sample-peak limiter
```

The filters use normalized RBJ biquads. The graph shows only theoretical linear EQ response, excluding gain staging, dynamics and creative effects. Settings validate finite values, band types, parameter ranges and enabled cutoff frequencies against the actual stream sample rate before a new processor starts or settings are accepted.

Noise suppression uses a 512-sample square-root-Hann STFT with 128-sample hops and conservative Wiener-style spectral gain. Its noise reference is a user-selected white-noise RMS floor, not a learned profile. It is not RNNoise, a noise gate, neural denoising, keyboard-source separation or acoustic echo cancellation. Lower-strength settings may preserve consonants better; real speech and background noise need listening acceptance. Enabling it adds 512 samples of algorithm buffering: approximately 10.67 ms at 48 kHz. Capture, render, device, resampling and queue latency are additional and unmeasured.

The limiter uses an instantaneous linked peak reduction and release envelope. Its ceiling applies to sample peaks; no true-peak oversampling or intersample-peak claim is made. Saturation uses a normalized `tanh` transfer. The robot effect is ring modulation, not natural pitch shifting. Delay and reverb are creative effects and default off for calls. No feature is described as AEC.

Settings updates are prepared off the audio callback and adopted at a buffer boundary. Existing spectral overlap, dynamics envelopes and delay/reverb history are retained where compatible; changed EQ is crossfaded over 10 ms and input/output gains ramp over approximately 5 ms. Changing delay time retimes its history. Enabling/disabling a latency-bearing effect such as denoising can still change alignment or produce a short transition. Make major effect changes with capture stopped for critical recordings. This transition policy is separate from real-device dropout or latency validation.

## Persistence and portable presets

Preset JSON uses schema version 1, a bounded file size of 256 KiB, an allowlisted model and strict unknown-property rejection. Portable exports contain name, description and sound parameters; they contain no endpoint IDs, plugin paths, commands, usernames, recordings or app state. An import never executes code or installs components. Unsupported schemas require explicit migration.

Local app state can separately remember endpoint IDs. Writes use a same-directory temporary file, flush and atomic replacement with a backup. Invalid JSON is preserved when possible and recovery is surfaced to the UI. Audio is written only by an explicit local recording operation and is not part of a preset or diagnostic export.

## Verification boundaries

The console suites test deterministic signal processing, serializers, guarded configuration changes and supported platform contracts. They do not prove hardware enumeration under every privilege level, APO execution on a particular stream, cable transmission into another application, subjective noise quality, DPI behavior or one-hour stability. See `docs/validation/test-plan.md` for those separate acceptance steps.
