# Mic Studio 自有 APO · 设计与实施边界

状态：**设计/前置验证，未完成，不可标为已交付。** 本文替代把现有本地 DSP 改名成系统 APO 的做法，记录 P4 的真实工程范围与本机阻塞条件。

## 1. 为什么本轮不能完成

本机检查结果：

- `C:\Program Files (x86)\Windows Kits\10` 不存在，找不到 Windows SDK/WDK 头文件、`cl.exe` 或 `signtool.exe`。
- Mic Studio 以普通权限运行；注册 APO、写系统音频端点属性和安装系统组件需要 UAC。
- 没有可用的代码签名证书，也没有可安全回滚的测试机/测试录音端点。
- 不能在未真实加载到 `audiodg.exe` 的情况下宣称“自研 APO 已完成”。

因此本轮只提交设计、参数块脚手架和验证矩阵；`MicStudio.Apo` 仍是第三方 Equalizer APO 的配置接入，不是自有系统 APO。参数块见 `src/MicStudio.Apo.Native/MicStudioApoParams.h`，该目录未加入解决方案、未构建。后续必须在具备 WDK、管理员权限、签名方案和可回滚测试端点的机器上继续。

## 2. 目标组件

```text
Mic Studio WPF
  ├─ 设置/主题/实时参数（非实时线程）
  ├─ MicStudioApoParams 共享参数快照（版本化、无锁读取）
  └─ 安装/诊断/回滚
        │
        ▼
MicStudioApo.dll（原生 C++，音频实时线程）
  ├─ 透传回退
  ├─ 增益与八段 EQ
  ├─ 输出峰值保护
  └─ 目标录音/渲染端点绑定
        │
        ▼
Windows 音频链路 → 接收应用
```

第一版只做**输入端点**的增益、EQ 和峰值保护；不做虚拟声卡替代，不把 VB-CABLE 当成自有驱动。创意效果和降噪先留在本地 DSP，避免在系统实时回调中引入未验证的 STFT 延迟。

## 3. 处理核心约束

- 实现 `IAudioProcessingObject`、`IAudioProcessingObjectConfiguration`、`IAudioProcessingObjectRT`；只使用系统允许的 APO 注册类别，不伪造驱动签名。
- 音频回调内不分配内存、不等待锁、不读写文件、不发网络请求；参数通过固定大小的 `MicStudioApoParams` 共享内存快照读取。
- 参数块使用 `version`、`sequence` 和双缓冲/seqlock：UI 写完递增序号，音频线程只接受完整快照；版本不匹配或未初始化时严格透传。
- 每次 `APOProcess` 校验声道数、采样率和缓冲对齐；不支持的格式保持原样并写入诊断状态。
- 透传是默认安全状态：组件存在但未绑定、参数损坏、安装未完成、版本不匹配时不能改变声音。
- 峰值保护使用采样峰值限制与可测的固定衰减，不以“更响”代替处理正确。

## 4. 注册、绑定与安装

1. `MicStudioApo.dll` 按 x64 构建，依赖静态 CRT 或随包提供运行库；禁止调试版/未签名二进制进入发行。
2. 注册 APO 类别、`FriendlyName`、版权和版本；记录 `FX\...` 属性与 APO 的 CLSID。具体 GUID 和注册位置以目标 Windows 版本的 Microsoft APO 文档与官方样例为准，不能凭猜测写注册表。
3. 用 `IPropertyStore` 只修改用户明确选择的录音端点，读取现有属性并保留外部值；写入前备份，失败/取消时回滚。
4. 安装器请求 UAC 时说明将修改的系统位置；用户取消后不留下半绑定状态。安装后提示重启音频服务或重启 Windows，但不自动重启系统。
5. 卸载移除自有 CLSID、注册项和端点绑定，保留其他 APO、Equalizer APO、VB-CABLE 及个人设置。
6. 支持按版本升级和回滚；检测到未知占用、重复 CLSID 或外部修改时停止并报告，不覆盖不可解释的配置。

## 5. 参数通道

```csharp
struct MicStudioApoParams
{
    uint Version;
    uint Sequence;
    uint Enabled;
    uint SampleRate;
    uint Channels;
    float InputGainDb;
    float OutputCeilingDb;
    BandParams Bands[8]; // enabled, type, frequency, gain, Q
}
```

WPF 侧只在非实时线程校验、序列化和写入；APO 侧只读。序列号采用奇偶或递增校验，避免读写撕裂。诊断只显示“组件存在”“端点已绑定”“Windows 当前路径生效”三个独立事实，不能把安装成功当成声音已生效。

## 6. 实施阶段

| 阶段 | 交付 | 通过条件 |
| --- | --- | --- |
| A 可行性 | 原生最小 APO、指定测试录音端点、透传和固定 −6 dB 衰减 | `audiodg.exe` 实际加载，录音端点测得变化，非目标端点不变 |
| B 处理核心 | 增益、八段 EQ、峰值保护 | 合成扫频/噪声与真实讲话测量通过，回调无分配/阻塞 |
| C 参数通道 | 版本化共享快照、实时更新 | 运行中更新不爆音、不重启音频会话，损坏快照透传 |
| D 安装绑定 | 注册、设备绑定、UAC、升级、卸载、回滚 | 取消 UAC、重复安装、设备重建和卸载均有可恢复结果 |
| E 真实链路 | 目标录音应用实际接收 | 接收应用听到预期变化，其他设备不受影响 |

阶段 A 未通过前，第三方 Equalizer APO 接入保留为可用基线；不会先删除可用处理路线。

## 7. 验收矩阵

- 组件存在、端点绑定、Windows 当前路径生效、接收应用生效分别记录。
- 透传 bit-exact（允许的格式转换除外）、固定衰减、EQ 频响、峰值上限分别测量。
- 目标端点与非目标端点隔离；设备拔插、采样率变化、重启音频服务、睡眠唤醒后恢复。
- 运行中参数切换无长静音、爆音、不可控增益；APO 异常时回退透传。
- 安装/升级/卸载/UAC 取消/回滚；保留个人设置、主题、录音和外部 APO 配置。
- 代码签名、SmartScreen、杀毒软件误报和 Windows 版本兼容性在发行前单独验证。

## 8. 当前可用路线

- **虚拟输入 · CABLE**：本地 DSP 实时处理，全部现有功能。
- **系统输入 · APO**：继续使用独立 Equalizer APO，手动预览/应用增益与 EQ；不是 Mic Studio 自有 APO。
- 本设计不会被描述为“P4 已完成”；在真实 Windows 音频链路验证前，只算未实现的工程计划。
