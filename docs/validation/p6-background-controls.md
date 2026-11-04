# P6 / 0.6.0 background controls validation

2026-11-04, Windows x64, .NET SDK 10.0.403. Development preview; real microphone, physical keyboard, Discord and sleep/resume acceptance are pending.

## Automated results

- Release solution build: zero warnings/errors, locked dependencies.
- Core 40, APO 20, WindowsAudio 84, Shortcuts 14, Components 24, NativeApo 26 and Setup 40 passed.
- WPF: 80 routed-control/integration checks, 30 Chinese and 30 English layouts at three DIP window sizes, zero visible untranslated English labels. Expanded shortcut editing is included.
- Managed/native echo interoperability: 29.5 dB synthetic echo reduction, 160-sample adapter latency. This is not end-to-end latency or physical-room acceptance.
- All new key registrations, key state and volume accesses used injected fake adapters. The gate tests used synthesized sample buffers. No capture, playback, driver registration, installation or real endpoint writes were performed.

Coverage includes opt-in defaults, background actions and gain bounds, PTT release/required-modifier release, heartbeat failure and fresh press after reset, conflict feedback, stale registration messages, workspace restoration, selected-endpoint lock scope, own-slider coordination, contention limits, disconnects and in-flight disable/dispose. Send gating leaves DSP samples, meters, monitoring and recordings independent.

## Manual acceptance still required

- [ ] Enable/rebind real global keys; test foreground/background, an occupied shortcut, clear/restore, tray mute and explicit exit.
- [ ] With the receiving app on CABLE Output, verify fresh press sends and release closes; manual mute overrides PTT. Stopped capture stays stopped.
- [ ] Hold the talk key across stop/start, reconfiguration, focus change, session lock and sleep/resume. Sending must remain closed until a fresh release/press.
- [ ] Independently verify headphone monitoring and recording remain available while CABLE sending is muted, and feedback protection cannot be bypassed by PTT.
- [ ] Opt in to volume lock on one microphone; verify own slider updates the target, external changes are bounded, persistent contention pauses, reconnect needs retry and selection changes do not transfer the lock.
- [ ] Restart and verify preferences; restored volume lock adopts the device's current volume, not a saved numeric target. Verify actual long-running behavior.

P3/P4/P5 physical audio, trusted native APO signing/deployment and system loading, full installation/upgrade/uninstall and OS display-scale acceptance remain separate pending work.
