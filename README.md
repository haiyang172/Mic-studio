<img src="assets/branding/mic-studio.png" alt="Mic Studio MS 图标" width="72" height="72">

# Mic Studio

Windows 原生麦克风调节软件，使用 C# / WPF / .NET 10。面向可选择输入设备的录音、会议、直播、通话软件。界面以设备、系统音量、数字增益和真实电平为主；高级参数按需展开。

[下载发行版](https://github.com/haiyang172/Mic-studio/releases) · [MS 图标源文件](assets/branding/)

## 使用

双击 `MicStudio.exe`。发行包自带运行时，不需要 SDK。可使用便携目录，也可运行 `MicStudio.Setup.exe` 安装到当前用户。默认不打开麦克风、监听或录音；普通操作无需管理员。

1. 顶部选择真实麦克风。
2. **系统音量**和**静音**直接改变该 Windows 录音端点，影响其他使用该麦克风的软件。
3. 选择处理方式：
   - **虚拟输入 · CABLE**：点击开始处理，将真实采集、DSP 后音频发送到 `CABLE Input`；其他软件输入选 `CABLE Output`。支持全部内置效果。停止或退出会中断本地发送。
   - **系统输入 · APO**：需要独立 Equalizer APO，并在官方工具中绑定目标录音端点；只支持增益和 EQ。其他软件仍选原麦克风，是否经过 APO 需实际验证。退出 Mic Studio 后 APO 可能继续生效。
4. 调整数字增益或效果后点击**应用**。草稿自动保存与应用到音频是两个动作。不要把重复 APO 和本地 EQ 叠加到同一个麦克风。

虚拟输入可用于任意支持选择音频输入的应用，Discord 只是其中一个例子。软件不修改系统默认设备或其他应用的配置。

## 驱动

发行包包含原始完整 **VB-CABLE Pack45** ZIP 和原始文件，来源 [VB-Audio](https://vb-cable.com)。VB-CABLE 是 Donationware，欢迎在[官方页面](https://vb-audio.com/Services/licensing.htm)支持作者。只捆绑标准 VB-CABLE，不包含 A+B / C+D。

**设备**页显示驱动文件版本、两个端点状态及本地包编号；支持检查官网更新、重新下载和打开官方安装器进行安装/更新/重装。联网只发生在主动检查或下载时。安装器签名验证后才发起 UAC；用户确认官方安装步骤，程序不静默重启。包编号与已安装文件版本分别显示，不将两者直接比较。卸载 Mic Studio 保留独立驱动。

Equalizer APO 不属于 VB-CABLE 驱动，不是虚拟输入模式的依赖。需要系统 APO 模式时可从[官方项目](https://sourceforge.net/projects/equalizerapo/)安装。

## 功能

- Core Audio 端点枚举、稳定 ID、拔插通知、断线停止，不自动换麦克风。
- 输入数字增益 −24～+30 dB、输出衰减 −24～0 dB，主动测量峰值/RMS 和讲话增益建议。
- 八段参数 EQ：高通、低架、四段峰值、高架、低通。理论曲线拖动/选点、数字输入、平直、单段复位、撤销/重做、旁路。
- 本地 DSP：固定噪声参考的 STFT Wiener 频谱降噪、压缩、柔和扩展器、采样峰值限幅、饱和、机器人环形调制、回声、混响。
- 平直、自然、清晰、温暖预设；个人新建、复制、重命名、删除、收藏、安全 JSON 导入导出。
- 30 秒本地录音、显式耳机回放、DSP 前源录音及同源离线 A/B；整段 RMS 匹配并保留峰值余量，不等于 LUFS 或真峰值处理。
- 独立耳机监听、普通权限启动、按需 UAC、浅/深主题、可选托盘、脱敏诊断预览。

不包含 RNNoise 人声分离、自然音高移调、通用 VST 宿主或声学回声消除。降噪不保证消除说话中的键盘声或补回遮挡信息。没有将未实现能力放成可用旋钮。

## 开发

SDK 锁定于 `global.json`。本地若有 `.tools/dotnet/dotnet.exe`，脚本优先使用；否则使用系统 SDK。

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1
```

也可双击 `Build.cmd` 或 `Run.cmd`。发行输出在 `dist/`，含便携 ZIP、原生安装器、SHA256。Windows CI 构建并上传包，不自动发布 Release。依赖锁文件纳入版本控制。源码不包含 SDK、私人录音、日志、个人设置或驱动二进制。

## 验证边界

算法、配置保护、离线文件处理、驱动包校验和安装器路径安全由自动测试覆盖。当前电脑普通权限检测到 USB PnP 麦克风及 CABLE 两端。虚拟线路中已用合成信号验证传输与 −6.000 dB 增益变化、快速启停和并发停止。

真实物理麦克风讲话、主观音质、系统 APO 实际生效、外部软件实际接收、一小时连续处理、睡眠唤醒以及完整安装/卸载仍需对应的实际验收，不由合成测试替代。窗口在三个实际 WPF 尺寸检查；未宣称完成所有系统 DPI 与跨显示器切换。

详细记录见 `docs/validation/`；架构与第三方声明见 `docs/architecture/` 和 `THIRD_PARTY.md`。旧桌面原型保留在原目录，没有被新工程覆盖。
