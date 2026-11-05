# 0.6.1 simple controls preview

2026-11-05 (Australia/Sydney), Windows x64. The user selected a simple device/effect panel, selectable voice/preset cards and collapsed advanced controls. A2 branding is retained. This is a UI preview, not completion of physical audio or full P5 acceptance.

## Verified locally

- Locked Release solution build: zero warnings and errors.
- Core 40, APO 20, WindowsAudio 84, Shortcuts 14, Components 24, NativeApo 26 and Setup 40 synthetic checks passed.
- Isolated WPF: 90 control checks; 39 Chinese and 39 English layouts at 1280×820, 900×600 and 600×500 DIP; no horizontal overflow, unreachable scrolling or untranslated visible English labels.
- Checked dark/light screenshots of the main page, effects, presets and settings. Preset titles/descriptions are truncated when long and remain available as tooltips. Selected cards expose an accessible selected state.
- Routed checks cover voice/preset card selection, disabled processing routes, shortcut-driven refresh, manual edits changing selection to Custom, one collapsed overview Advanced section, and the relocated mode selector preserving its expanded state after selection.

Checks use an isolated workspace, fake shortcut/volume adapters and synthetic audio. They do not open capture, monitoring or playback; register system keys; change real microphone levels; install drivers; register an APO; or change OS DPI.

## Pending acceptance

- [ ] Physical keyboard, screen-reader and pointer use on every new card, switch, expander and dialog.
- [ ] Windows scale factors and movement between displays, including long actual device names.
- [ ] Real microphone/Discord audio, background PTT/volume coordination, sleep/resume and long sessions.
- [ ] Full clean installation, update and uninstall; trusted APO signing, loading and physical endpoint response.

Soundpad-style soundboard remains planned under P9. No new mixing engine or soundboard is claimed in this UI release.
