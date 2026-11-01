# Mic Studio 自有原生 APO

本目录实现独立的 Windows x64 COM APO DLL，文件名 **`MicStudioApo.dll`**。它与第三方配置适配程序集 `MicStudio.Apo.dll` 不同。

状态：原生 COM/DSP、版本化共享参数及离线宿主已实现，Windows CI 已实际编译原生 DLL 并运行基础检查。**尚未完成签名系统部署、`audiodg.exe` 实际加载或目标应用收音验收，不能据此标记整个 P4 完成。** 缺少签名或管理员权限不再作为禁止创建工程及离线测试的条件。

## 构建与离线验证

使用 Visual Studio 2022 C++、Windows SDK 和 CMake，直接实现 SDK 接口，不依赖 WDK 的 `CBaseAudioProcessingObject` 静态库。本原型静态链接 CRT，不自动安装运行库；正式签名驱动包的 CRT、包隔离及其他发行要求需单独验证。

```powershell
cmake -S src/MicStudio.Apo.Native -B artifacts/native/cmake/apo -G "Visual Studio 17 2022" -A x64
cmake --build artifacts/native/cmake/apo --config Release
ctest --test-dir artifacts/native/cmake/apo -C Release --output-on-failure
```

仓库 `scripts/Build-NativeCpp.ps1` 同时构建 APO 和回声组件。上述流程不会注册 COM、绑定端点、提权、打开音频设备或修改系统设置。离线测试只加载明确指定的 DLL；文件映射测试使用本测试进程独有的临时目录和假的端点属性。

## COM 与处理边界

- CLSID：`{6843D3F0-EA67-4B0C-AB63-90A28CF45578}`。
- 接口：`IAudioProcessingObject`、`IAudioProcessingObjectConfiguration`、`IAudioProcessingObjectRT`、`IAudioSystemEffects`。
- 导出：`DllGetClassObject`、`DllCanUnloadNow`、`MicStudioApoGetContract`。没有自动写系统注册表的 `DllRegisterServer`。
- `MicStudioApoGetContract(uint32_t* version, uint32_t* bytes, GUID* clsid)` 返回参数 ABI 2、224 字节及上面的 CLSID。该标识帮助拒绝误放的通用 DLL，不能代替签名、散列和来源验证。
- 注册属性与托管部署器一致：Flags `0x0e`，一进一出，接口表为 `IAudioProcessingObject`，最大实例数 `0xffffffff`。
- 流格式：float32、8–384 kHz、1–8 声道，输入输出格式相同；不支持的格式在协商/锁定阶段明确拒绝。
- 数字处理：输入/输出增益、八段 RBJ EQ、采样峰值限幅；固定 −6 dB 测试模式用于实际频响验证。算法报告缓冲延迟为零，不等于端到端实测延迟。
- 这是增益/EQ APO，不包含 AEC、自动人声分离或变声。也不宣称音量限幅可以消除扬声器与麦克风之间的声学反馈。

## 参数 ABI

`MicStudioApoParams.h` 定义 Pack=4、little-endian 的固定 224 字节结构。字段的偏移如下：

| 偏移 | 字段 | 类型 |
| --- | --- | --- |
| 0 / 4 / 8 | Version=2 / SizeBytes=224 / Sequence | uint32 |
| 12 | Enabled（0 透传、1 处理） | uint32 |
| 16 / 20 | SampleRate / Channels（0 表示使用协商后的流格式） | uint32 |
| 24 / 28 | EqEnabled / LimiterEnabled | uint32 |
| 32 / 36 / 40 | InputGainDb / OutputGainDb / OutputCeilingDb | float32 |
| 44 | TestMode（0 正常、1 固定 −6 dB） | uint32 |
| 48 | 八个 20 字节频段：Enabled、Type、FrequencyHz、GainDb、Q | uint32、uint32、float32×3 |
| 208 | HeartbeatTickMs | uint64 |
| 216 / 220 | OwnerProcessId / Reserved=0 | uint32 |

频段顺序是高通、低架、四段峰值、高架、低通。处理副本按实际流采样率将高频限制到 `0.48 × Fs`，不修改保存的参数。非零 SampleRate/Channels 是严格匹配条件，失配透传；正常端点控制宜使用 0/0，以支持同一麦克风的单声道、立体声及不同流格式。

## 文件映射、租约与实时线程

每个端点使用 `%ProgramData%\MicStudio\APO\Parameters\<endpoint-guid-D>.bin`，GUID 无花括号。托管部署器创建准确 224 字节的文件并配置必要的 ACL；本 DLL 不创建系统目录或修改权限。

`Initialize` 从系统端点属性 `PKEY_AudioEndpoint_GUID` 得到 GUID，在非实时线程打开文件并建立只读映射。文件缺失或大小不符时保持透传，需要先准备参数文件再启动目标音频流。

写入器先发布奇数序号，写完数据并执行内存屏障后发布偶数序号。音频回调每包只尝试读取一次，校验前后序号，不等待写入器。映射读取、参数校验和滤波器/状态切换均使用固定大小存储；回调不创建对象、不等待锁、不访问文件或网络。滤波器仅在 EQ 变化时更新，心跳刷新不会重置滤波历史。

控制器每秒刷新 `HeartbeatTickMs`（Windows `GetTickCount64` / .NET `Environment.TickCount64`）。启用参数必须包含非零所有者 PID 和有效心跳，未来时刻或超过 3000 ms 的租约立即退回透传。关闭、崩溃或 UI 卡住不会让最后的增强无限保留；这不是对实际系统故障恢复的验收替代。

## 参考

- [Microsoft：实现 APO](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects)
- [Microsoft Windows SDK 接口定义](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/audioenginebaseapo.h)
