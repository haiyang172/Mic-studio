# Mic Studio verification and acceptance

Date: 2026-10-29 (Australia/Sydney). This file defines the acceptance procedure. An unchecked item is pending, not passed. The app's status indicators and a saved configuration are not substitutes for the manual audio checks below.

## Automated checks

Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1` from the repository. These are console verification executables; a nonzero exit code fails the run. CI invokes the same commands on Windows with the SDK pinned in `global.json`.

- [x] Earlier deterministic verification passed Core, APO and WindowsAudio checks for filter response, dynamics, spectral suppression, state-preserving updates, presets, atomic recovery, guarded APO writes and audio queue/format contracts. No physical microphone was opened by those runs.
- [x] Final functional run passed Core 28, APO 19, WindowsAudio 25 and Components 24, including the pinned component's actual installer signature without launching it. Native Setup's subsequent manifest-preservation changes passed 40 independent checks. Current results are recorded in `release-0.1.0.md`.
- [x] 0.3.0 run passed Core 38, APO 20, WindowsAudio 39, Components 24 and Setup 40; WPF checks passed 43 control events with 27 Chinese and 27 English layouts and zero untranslated visible English text. Results are recorded in `p2-background.md`.
- [x] 0.4.0 run passed Core 40, APO 20, WindowsAudio 39, Components 24 and Setup 40; WPF checks passed 47 control events with 27 Chinese and 27 English layouts and zero untranslated visible English text. Results are recorded in `p3-p5.md`.
- [x] Release WPF build completed with restored locked dependencies, zero compiler warnings and zero errors on this machine.
- [x] Self-contained `win-x64` EXE started on this Windows 11 x64 machine and loaded the bundled hostfxr/coreclr/WPF libraries; no developer SDK is used by the published process. A separate fresh-machine run remains pending.
- [x] Published ZIP/setup checksums match `dist/SHA256SUMS.txt`; package excludes SDK, user audio, settings, logs and credentials. Standard VB-CABLE bytes match the pinned original SHA-256, retain attribution/donation links, and exclude A+B/C+D.

All automated audio samples are synthesized in memory or in temporary test storage. These checks do not open a live microphone unless a distinct user-authorized manual action is performed. Synthetic results establish algorithm and file behavior only.

## Initial state and ordinary permissions

- [ ] Start as an ordinary user: no microphone capture, recording, monitoring or cable transmission starts automatically.
- [ ] Device list includes the actual USB microphone with its full name and default roles; enumerate twice and keep selection by endpoint ID.
- [ ] Record Windows build, app version, endpoint sample rate/channels and actual APO/CABLE component versions. Keep identifiers and usernames private in public reports.
- [ ] Compare ordinary and elevated enumeration results if access fails. Record the actual exception/HRESULT rather than assuming an administrator requirement.
- [ ] Cancel an offered UAC prompt and verify the ordinary UI remains usable. Do not loosen Program Files permissions or make the app permanently require elevation.

## UI and persistence

- [x] Automated WPF checks cover all seven pages at 1280×820, 900×600 and 600×500 DIP, ordinary and expanded advanced controls, no horizontal overflow and reachable scrolling. 43 control/event checks and 27 Chinese plus 27 English layouts pass in 0.3.0.
- [ ] Run every page and dialog; click, type and use Tab/Shift+Tab, keyboard activation and numeric inputs.
- [ ] Check 100%, 125%, 150%, 175% and 200% display scale plus crossing between displays. Record the scales actually tested.
- [ ] Drag/select EQ points and confirm band rows match; edit numbers and confirm the curve matches. Identify it as theoretical EQ response.
- [ ] Exercise flat/reset, the effects switch and microphone mute; distinguish effects bypass from mute and external APO processing.
- [ ] Save, duplicate, rename, favorite, delete and import/export personal presets. Factory presets remain immutable. Restart and verify local state recovery.
- [ ] Reject malformed, oversized, unsupported-version and out-of-range presets without modifying the current draft. Check corrupt local settings warnings and preservation.
- [ ] Verify a 0.2.x workspace migrates without losing device, presets, EQ or theme, and that the new close-to-tray default is visible.

## Background, tray, startup and language

- [x] Automated checks verify the new workspace default close-to-tray, schema-1 migration, separate startup preferences, tray menu entries, closing hides without disposing, language switch/restore and English text coverage.
- [ ] With real audio running, click the window close button: the window hides, the tray icon remains and the receiving app keeps audio. Stop/continue from the tray changes the same processing state.
- [ ] Choose explicit “Exit” from the tray: local audio stops, the tray icon disappears and the process ends. Reopen and confirm no microphone was opened automatically.
- [ ] Enable sign-in launch, sign out/in Windows, and confirm Mic Studio starts in the tray without a main window. Separately enable automatic processing and confirm it starts only when a usable selected microphone exists.
- [ ] Disable sign-in launch and verify the `MicStudio.Next` Run value is removed. Install, upgrade over a running/closed build and uninstall; confirm uninstall removes only Mic Studio's startup value and preserves personal settings, presets and recordings.
- [ ] Switch Chinese/English during real processing: audio must not stop or change, all visible labels follow the choice, and the choice survives restart. Confirm Windows device names and third-party APO/installer text are shown as supplied.

## Voice priority, pitch and creative themes (0.4.0 preview)

- [ ] Speak normally, softly, with consonants and phrase endings while auto noise tracking is on. Confirm the noise estimate does not chase the voice or pump between phrases.
- [ ] Test stationary fan/air-conditioner noise, typing while silent and typing while speaking. Record what the denoiser removes and what it cannot.
- [ ] Select helium/loli/monster and confirm the pitch change is audible without dropouts, excessive grain, long latency or uncontrolled gain. These are delay-line pitch previews, not formant-preserving voice change.
- [ ] Select phone/radio and explosion themes; check band-limited intelligibility, distortion, output ceiling and receiving-app level.
- [ ] Combine a theme with EQ, compression, limiter and cable send; switch themes during running audio and confirm no long silence, pop or stuck state.
- [ ] **AEC and room-reverb suppression are not implemented.** Do not mark them passed; a future build needs playback reference capture, double-talk testing and separate echo/reverb acceptance.


## Direct APO path

- [ ] Detect installed APO separately from binding to the selected recording endpoint. Configure only that endpoint using its official tool; reboot only if explicitly required and user-approved.
- [ ] Preview generated EQ/gain before applying. Speakers and other microphones retain their previous configuration.
- [ ] Verify cancellation, write failure, external edits, Peace ownership and config conflicts do not overwrite unrelated content.
- [ ] Apply a temporary attenuation to a steady repeatable source and measure the expected level change on the same capture path. Restore the original parameters after success, cancel or error. Avoid high positive-gain probes.
- [ ] Repeat in the user's chosen recording, calling or streaming application. Select the physical USB microphone in direct mode. Mark that application's path manually verified only after a real result, and invalidate that confirmation when device, mode or configuration changes. Discord can be tested as an optional example.
- [ ] Close Mic Studio and verify/describe whether independent system APO effects remain active. Removing Mic Studio's own hook does not remove unrelated configuration.

## Local DSP, measurement and cable path

- [ ] Start/stop a level test explicitly. Before capture the meter shows “—”; after stop it returns to “—” and microphone resources release.
- [ ] Speak normally for calibration. Apply a suggested gain manually; document source level, target and actual result. Do not reuse the historical −33.3 dBFS observation as a permanent +24 dB setting.
- [ ] Use physical USB microphone → Mic Studio → CABLE Input. The receiving application's input is CABLE Output; its output remains normal headphones. Check actual transmitted audio, not just device detection. This applies generally to configurable microphone inputs.
- [ ] Check whether capture already contains APO processing; avoid duplicated EQ/gain. Name input/processed meters accurately.
- [ ] Enable each local effect and verify an audible/measurable change. In APO mode unsupported local effects are disabled or explicitly reported rather than silently accepted.
- [ ] For denoising test stationary fan/background noise, typing while silent, typing while speaking, consonants and soft voice. The fixed noise floor may need adjustment; RNNoise/learning/AEC behavior is not claimed.
- [ ] For compression/expansion/limiting test normal speech, soft phrases, phrase endings, silence and abrupt loud sounds. Record pumping, clipped peaks, missing tails or excessive suppression.
- [ ] Adjust gain/EQ while running and listen for discontinuities. Toggle denoising separately because its algorithm latency changes. Check output limiter remains within its sample ceiling.
- [ ] Record a short WAV explicitly, stop, play on explicitly selected headphones and delete/export only by user action. No automatic speaker monitoring.
- [ ] For subjective A/B, record the DSP-before source, stop capture and generate the same-source reference/effect pair. Verify whole-file RMS matching (not LUFS), sample peak headroom and compensated denoiser delay. Listen through explicit headphones. The source can still include upstream Windows effects; sequential processed takes are a separate comparison method.

## Stability, recovery and latency

- [ ] Unplug the selected microphone during capture, reconnect and verify no other microphone is silently substituted.
- [ ] Remove CABLE or headphones, change sample rate, sleep/wake, and test endpoint recreation. Report actual stop/recovery behavior.
- [ ] Run a real cable session for at least one hour and verify input in the user's chosen application. Record elapsed duration, underruns/buffer resets, discontinuities, drift and CPU usage. A fast synthetic loop does not meet this requirement.
- [ ] Measure end-to-end latency using a documented repeatable physical loopback/impulse method. Include capture and render devices, sample rate, buffer settings and repetitions. The 512-sample denoiser delay or a queue duration is not an end-to-end measurement.

## Distribution status

The initial Mic Studio portable executable and native setup are unsigned unless a separate signed release explicitly states otherwise. They include the .NET runtime, not the SDK. The native setup packages the same portable app for per-user installation. The original standard VB-CABLE archive and its signed official installer are bundled with attribution; driver installation requires an explicit action and UAC. The build and payload-validation steps do not install it or automatically replace an existing cable. Equalizer APO remains an independent component. No signing certificate, automatic updater or production GitHub Release is implied by a local package or CI artifact.

Record the final test date, system, tested devices, results and remaining limitations alongside any published release. Do not publish private recordings or unique endpoint IDs.
