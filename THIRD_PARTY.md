# Third-party components

Mic Studio's own user interface, preset model, biquad filters, frequency-domain noise suppression and dynamics implementation were written for this project. No Equalizer APO, Peace or VB-CABLE source code is reused. The release includes the unchanged official standard VB-CABLE driver archive as a separately attributed component, at the user's request.

| Component | Version / source | Use and license |
| --- | --- | --- |
| SpeexDSP | 1.2.1, [Xiph.Org source](https://github.com/xiph/speexdsp/tree/SpeexDSP-1.2.1) | Playback-reference acoustic echo cancellation in the separate desktop helper `MicStudioEcho.dll`. BSD-style license retained in `licenses/SpeexDSP-COPYING.txt`; original source archive pinned by SHA-256 in CMake. It is not a system APO or a virtual audio driver. |
| NAudio.Wasapi | 2.3.0, [NAudio source](https://github.com/naudio/NAudio) | Windows Core Audio endpoint enumeration and WASAPI shared capture/render. MIT; copyright Mark Heath and contributors. |
| NAudio.Core | 2.3.0, transitive dependency | Audio buffer, WAV and sample-provider utilities. MIT; copyright Mark Heath and contributors. |
| Microsoft .NET / Windows Desktop runtime | Runtime version selected by the pinned .NET 10 SDK during self-contained publication | Included in the portable Windows x64 package. Microsoft runtime redistribution and third-party notices apply; see package license texts copied into `licenses/` and the [runtime repository](https://github.com/dotnet/runtime), [WPF repository](https://github.com/dotnet/wpf). |
| Equalizer APO | Independently installed; version detected on each user's machine | Optional direct-mode system equalizer. Not redistributed. [Official project](https://sourceforge.net/projects/equalizerapo/). |
| VB-CABLE | Standard Pack45, official October 2024 archive | Bundled unmodified under the vendor's application-bundling terms. Optional virtual audio cable by VB-Audio / Vincent Burel. **Donationware: please support the developer if useful.** [Origin](https://vb-cable.com/), [donate / license](https://vb-audio.com/Services/licensing.htm). A+B/C+D are not bundled. |
| Peace | No runtime dependency | Interaction reference only. Not redistributed. [Official project](https://sourceforge.net/projects/peace-equalizer-apo-extension/). |

The biquads use the publicly documented RBJ audio EQ equations, independently implemented; this is not a reuse of Peace or Equalizer APO code. Noise suppression is an original conservative fixed-noise-floor STFT Wiener-style implementation. It is not RNNoise, a trained model, a third-party VST host, or echo cancellation.

Dependency versions and hashes are locked in the checked-in `packages.lock.json` files; the component archive is pinned by `scripts/components.lock.json`. The SDK is selected by `global.json`. The release contains the application, its runtime and the attributed original VB-CABLE package; it contains no SDK, captured audio, user settings, private credentials or unsigned downloaded plugins. Driver bytes stay out of the source repository, in ignored download caches and generated release artifacts only.

## Bundled VB-CABLE provenance

The official [distribution terms](https://vb-audio.com/Services/licensing.htm) were checked on **2026-10-28**. They permit the standard VB-CABLE package to accompany or be embedded in another application when users can identify VB-Audio and have an opportunity to donate or pay. Mic Studio includes that attribution and donation link in its component notice and installation flow. The additional A+B and C+D products are excluded. Organizational deployments where users cannot identify or pay for the component require the vendor's applicable licenses.

- Original download: [VBCABLE_Driver_Pack45.zip](https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip), **1,318,877 bytes**.
- Archive SHA-256: `b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb`.
- Release paths: `components/VBCABLE_Driver_Pack45.zip` (original bytes), `components/VBCABLE/` (complete original extracted files), and `components/VB-CABLE-NOTICE.txt` (Mic Studio attribution outside the original package).
- Official x64 installer: `VBCABLE_Setup_x64.exe`, file version **2.1.5.8**, SHA-256 `734c35dfa6d98f48782a451633ceb471166ec70d60482fd89a1123d0ee3c4f41`.
- Authenticode checked on this Windows machine on 2026-10-28: x64 installer **Valid**, signer **BUREL VINCENT Entrepreneur individuel**, certificate thumbprint `A77952D93229D0EC36E2543081EEA7D125732B9C`.
- Included Windows 10 x64 driver `vbaudio_cable64_win10.sys` and catalog `vbaudio_cable64_win10.cat`: **Valid**, signer **Microsoft Windows Hardware Compatibility Publisher**, certificate thumbprint `9A4A6CFEE73C6B65945FCAA083DFBCC1E9077E7E`. Their SHA-256 values respectively are `f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5` and `0a921ebadbe39cf3fa7c14ff37d1ca22565a9651244bd3ac177dc810bc99072e`.

The archive SHA-256 pins the reviewed bytes; a signature status is a point-in-time Windows trust result, not a claim that the ZIP itself has an Authenticode signature. Download/build/inspection did not execute or install the driver. Installation requires the user's explicit action and Windows UAC confirmation and may require a later restart. Mic Studio does not automatically replace an existing working cable installation.

## NAudio MIT license

Copyright 2020 Mark Heath

NAudio 2.3.0 package metadata additionally identifies © Mark Heath 2026. License text source: [v2.3.0 license.txt](https://github.com/naudio/NAudio/blob/v2.3.0/license.txt).

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
