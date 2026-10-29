# Mic Studio 自有 APO · 源码脚手架

状态：**未构建、未注册、未验证。** 本目录只固定参数块与文件边界，避免后续把 UI 对象、锁或文件访问带进音频实时回调。

本机执行 `docs/architecture/apo-own.md` 中的前置检查后，才可创建 Visual Studio/WDK 工程：

```text
MicStudio.Apo.Native/
  MicStudioApoParams.h     参数快照（本文件）
  MicStudioApo.cpp         IAudioProcessingObject 实现
  MicStudioApo.def         导出/CLSID
  MicStudioApo.vcxproj     x64 Release，静态 CRT
  install/RegisterApo.ps1  仅测试机，显式 UAC
```

最低构建/验证条件：

- Windows SDK + WDK，`cl.exe` 与 `signtool.exe` 可用；
- x64 代码签名证书与时间戳服务；
- 管理员测试账号、可回滚的测试录音端点；
- 真实 `audiodg.exe` 加载、透传、−6 dB 固定衰减和非目标端点隔离测试。

未满足前，`MicStudio.Apo` 继续使用独立 Equalizer APO 配置，不把本地 DSP 或第三方接入改名成自有 APO。
