# Mic Studio 自有 APO · 设计与实施边界

状态：**0.5.0 开发预览已实现原生 APO、224 字节 v2 参数通道与受控部署预览；完整 P4 尚未验收。** 原生 DLL 已由 Windows CI 实际编译并运行离线检查。用户当前无签名证书，选择先交付开发预览；未签名组件不会自动注册或绑定。系统签名部署、`audiodg.exe` 实际加载和真实硬件/目标应用收音仍待验证。

## 1. 当前实现与验收边界

本轮已实现：

- `src/MicStudio.Apo.Native` 已包含独立 C++ COM APO、CMake 工程及离线宿主检查，不再只有参数头文件。直接实现 Windows SDK 接口，不依赖 WDK 的 `CBaseAudioProcessingObject` 静态库；构建与测试见该目录 README 和 `scripts/Build-NativeCpp.ps1`。
- `src/MicStudio.NativeApo` 提供参数编解码、文件映射写入、控制器租约、组件身份/签名检查、单端点安装计划和受控还原。独立的 `MicStudio.ApoControl` 程序承接用户明确批准的管理员操作。
- 主程序提供“自研 APO（预览）”路线、组件状态、安装预览和受保护操作记录。组件可信、已注册、已绑定、参数文件就绪是发送参数的前提；界面仍明确标记系统音频待验证。
- 原生 `MicStudioApo.dll` 与托管 `MicStudio.Apo.dll` 是不同组件；后者仍是第三方 Equalizer APO 配置适配层。

此前把本机缺少编译工具、管理员环境和签名证书合并为“不能创建工程”的条件过于严格。现在通过具备 C++/Windows SDK 的 Windows CI 完成原生构建及离线测试；管理员权限和可信签名是后续系统部署的条件，不能作为停止代码实现与离线验证的理由。

当前开发预览不执行未签名系统部署，不放宽 Windows 受保护音频策略，不自动重启音频服务，也不以离线宿主加载代替 `audiodg.exe` 实际加载。完整 P4 的剩余条件是可信签名方案、可回滚的目标录音端点，以及系统链路/硬件/接收软件验收。验证记录见 `../validation/p4-development-preview.md`。

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
  └─ 选定录音端点的受控绑定预览
        │
        ▼
Windows 音频链路 → 接收应用
```

第一版只做**输入端点**的增益、EQ 和峰值保护；不做虚拟声卡替代，不把 VB-CABLE 当成自有驱动。创意效果和降噪先留在本地 DSP，避免在系统实时回调中引入未验证的 STFT 延迟。

## 3. 处理核心约束

- 已实现 `IAudioProcessingObject`、`IAudioProcessingObjectConfiguration`、`IAudioProcessingObjectRT`、`IAudioSystemEffects`，并导出 `DllGetClassObject`、`DllCanUnloadNow`、`MicStudioApoGetContract`。没有自动修改系统的 `DllRegisterServer`。
- 音频回调内不分配内存、不等待锁、不读写文件、不发网络请求；参数通过固定大小的 `MicStudioApoParams` 共享内存快照读取。
- 参数块使用 v2 固定布局与奇偶 seqlock：UI 先发布奇数序号，写完数据并执行内存屏障后发布偶数序号；音频线程每包只尝试读取一次，不等待写入器。版本不匹配、未初始化或快照无效时透传。
- 格式协商和 `LockForProcess` 校验 float32、8–384 kHz、1–8 声道及输入输出一致性；不支持的格式明确拒绝。处理器使用实际协商后的流格式，不能假定每个客户端都使用设备混合格式。
- 透传是默认安全状态：组件存在但未绑定、参数损坏、安装未完成、版本不匹配时不能改变系统声音。
- 峰值保护使用采样峰值限制与可测的固定衰减，不以“更响”代替处理正确。

## 4. 注册、绑定与安装

1. `MicStudioApo.dll` 按 x64 Release 构建。0.5.0 可随包附带未签名开发 DLL 供离线检查，但受控安装器会拒绝其系统部署；正式系统分发仍需完成签名、包隔离及兼容性验证。
2. CLSID 固定为 `{6843D3F0-EA67-4B0C-AB63-90A28CF45578}`。安装前核对原生 x64 PE、必要导出、Windows 签名信任及 SHA-256；只有可信组件才会在明确的预览操作中调用 `MicStudioApoGetContract`，核对 ABI 2、224 字节及 CLSID。不在界面中加载未签名 DLL。
3. 当前部署器按官方 SFX 和处理模式属性生成单个录音端点的安装计划，限定 Default/Communications 模式。已有 SFX、模式注册或未知类注册会阻止自动替换。该受控注册机制尚未完成真实 Windows 加载验证，也不等于已完成正式组件化 INF 分发。
4. 预览记录源文件散列、用户 SID、端点、目标路径、注册变更及既有停用参数文件状态，生成审批指纹。用户确认后独立 helper 才请求 UAC；执行前复核指纹，备份后写入，失败时只还原未被外部改动的自有值。不接管端点所有权，不降低系统音频保护，不自动重启音频服务或 Windows。
5. 系统 DLL、备份和目录由管理员持有；参数文件只给指定用户读写、音频服务读取。操作结果写入固定的受保护 GUID 报告路径，主程序核验所有权、ACL、操作编号及用户后读取结果。状态始终区分“组件存在”“注册/绑定”“系统实际生效”。
6. 还原前核对受保护备份及散列，只处理仍与安装记录一致的自有注册；外部变更保留，组件文件、个人设置及独立音频组件不自动删除。重复部署/版本更新需先停用并还原旧绑定，再重新预览；这些真实安装、取消、更新和恢复流程仍需验收。

## 5. 参数通道

```cpp
struct MicStudioApoParams
{
    uint32_t Version;        // 2
    uint32_t SizeBytes;      // 224
    uint32_t Sequence;       // 奇数正在写，偶数完整
    uint32_t Enabled;
    uint32_t SampleRate;     // 0：使用当前协商的流采样率
    uint32_t Channels;       // 0：使用当前协商的流声道数
    uint32_t EqEnabled;
    uint32_t LimiterEnabled;
    float InputGainDb;
    float OutputGainDb;
    float OutputCeilingDb;
    uint32_t TestMode;       // 0 正常，1 固定 −6 dB
    MicStudioApoBandParams Bands[8]; // 每段 20 字节
    uint64_t HeartbeatTickMs;
    uint32_t OwnerProcessId;
    uint32_t Reserved;
};
```

结构采用 little-endian、Pack=4；完整定义见 `src/MicStudio.Apo.Native/MicStudioApoParams.h`。每个端点使用 `%ProgramData%\MicStudio\APO\Parameters\<endpoint-guid-D>.bin`，GUID 不带花括号，文件准确为 224 字节。部署阶段先准备停用的参数文件；APO 在非实时 `Initialize` 中从端点属性取得 GUID，建立只读文件映射，音频回调只读映射页。

WPF 只在非实时线程校验、适配和提交参数。正常写入将 SampleRate/Channels 设为 0/0，允许同一端点的单声道、立体声及不同采样率客户端分别协商；原生处理副本按实际流采样率限制高频，不修改保存的设置。已有有效控制器的租约不能被其他实例静默抢占或停用。

控制器每秒刷新心跳；启用参数要求非零 PID 和不超过 3000 ms 的有效心跳。显式停止写入停用状态；退出、崩溃或 UI 停顿导致租约过期时退回透传。心跳更新不重置滤波历史。该行为已覆盖离线检查，不能代替真实系统加载后的故障恢复验收。

## 6. 实施阶段

| 阶段 | 交付 | 通过条件 |
| --- | --- | --- |
| A 可行性 | 原生 COM DLL、离线透传及固定 −6 dB 检查已实现并构建 | `audiodg.exe` 实际加载、目标端点测得变化及非目标端点隔离仍待验收 |
| B 处理核心 | 增益、八段 EQ、峰值保护及合成验证已实现 | 真实讲话、系统实时线程表现及持续运行仍待验证 |
| C 参数通道 | 224 字节 v2 映射、seqlock、租约和离线检查已实现 | 系统运行中切换、异常恢复及听感仍待验证 |
| D 安装绑定 | 签名/身份检查、审批指纹、UAC helper、备份和受控还原已实现 | 当前无证书，未执行系统部署；签名分发、真实安装/更新/取消/还原仍待验收 |
| E 真实链路 | 保留验证入口与状态边界 | 尚未完成目标录音软件接收和硬件验收 |

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
- **自研 APO（预览）**：已经有独立原生组件和参数控制实现；只有完成可信系统部署与绑定后才允许发送参数，实际声音仍待验证。0.5.0 的未签名开发组件不会自动安装。
- 原生预览目前仅含增益、EQ、限幅，不包含桌面播放参考 AEC 或持续啸叫保护；外放及 Discord 外部监听的相应保护使用本地 DSP / CABLE 路线，并另做实际房间与目标软件验收。
- P4 已从设计进入开发预览，尚未达到完整验收条件，不能标记为“P4 已完成”。P5 的 A2「翡翠流线」图标/Logo 仅已选定，尚未应用；按用户要求在 P4 验收后推进。
