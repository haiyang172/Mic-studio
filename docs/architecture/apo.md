# Equalizer APO 适配与验证边界

本适配器把 Mic Studio 的数字增益和八段线性 EQ 转换为 Equalizer APO 白名单配置。原生线路中的频谱降噪、压缩、扩展、采样峰值限幅和创意效果没有伪装成 APO 原生命令。包含这些效果的普通 `Preview` 会拒绝生成可应用计划；界面若提供“仅应用 EQ”，应明确列出被排除的效果，再用去除这些效果的副本生成计划。

## 设备与作用域

Windows 设备选择仍保存完整端点 ID；APO 的 `Device` 参数使用该录音端点 ID 中的完整 GUID（含花括号），不会退化为模糊的设备名称。当前仅接受已知的 `{0.0.1.00000000}.{GUID}` 录音格式；播放端点、格式未知、额外文本和换行一律拒绝。Windows 将端点标识视为不透明标识，因此此提取属于受约束的兼容层；未来格式变动应重新适配，而不是自动扩大匹配范围。

官方 `Device` 语法把各词与设备名称、连接名称和 GUID 组合字符串匹配。使用完整 GUID 限定一个录音端点；`Stage: capture` 进一步限定录音阶段，`Channel: all` 选中该端点全部声道。[Equalizer APO 配置参考：Device、Stage、Channel](https://sourceforge.net/p/equalizerapo/wiki/Configuration%20reference/)

主配置的自有挂接如下（文件名中的 SHA-256 为实际生成内容的摘要）：

```text
# >>> Mic Studio managed hook v1 >>>
Device: all
Stage: capture
Channel: all
Include: MicStudio/eq-<sha256>.txt
Device: all
Stage: post-mix capture
Channel: all
# <<< Mic Studio managed hook v1 <<<
```

独立片段再次设置精确 `Device`、`Stage` 和 `Channel`，然后只输出 `Preamp`、`HPQ`、`LPQ`、`LSC`、`HSC`、`PK`。数值先验证范围、采样率及有限性，再以 invariant culture 格式输出。没有脚本、插件路径、用户提供的 APO 原始指令或网络命令。

不能假定 `Include` 自动保存/恢复作用域。因此挂接前显式初始化作用域，挂接后恢复 APO 文档描述的默认作用域，并要求挂接位于最后一个音频命令之后。已有普通 `Device`/`Stage`/`Channel`/EQ 等行保留原样；外部 `Include`、条件、表达式、插件、未知命令和 Peace 标记会暂停自动写入。挂接后新增外部音频命令也会暂停下一次应用，等待人工检查。默认作用域的恢复不撤销前文已经定义的其他效果。

## 写入与恢复

1. 预览仅读取；限制每个配置文件最大 1 MiB，支持严格 UTF-8、UTF-8 BOM、UTF-16 LE/BE BOM。未知编码拒绝写入，以免破坏用户文件。原有字节序标记和行尾被保留。
2. 记录主配置及现有自有片段的 SHA-256。两个 Mic Studio 进程通过同会话命名 mutex 串行写入；读取快照的句柄不允许同时原地写入。
3. 新片段以内容摘要命名，存在同名不同内容时拒绝；老片段不可变。因此无需在更新片段与主配置之间暴露“主配置仍引用、片段已被原地替换”的中间状态。
4. 写前保存当前主文件快照，重新校验摘要，使用同目录临时文件并刷新后替换主配置。主文件是唯一提交点。失败时移除本次新建且尚未提交的片段，保持旧挂接。
5. 发现外部修改、缺失/损坏的标记、片段内容变化、重解析点或权限不足时停止。需要提权时返回 `NeedsElevation`，交由界面请求真正的 Windows UAC；不会放宽 Program Files 权限。
6. 恢复时重新读取当前文件，只移除结构完整的 Mic Studio 挂接；保留后来增加的其他文本，不把历史整文件快照覆盖回去。APO、VB-CABLE、用户文件和历史片段不会被卸载或删除。

同目录替换使用 .NET `File.Replace`；原文件不存在时使用 `File.Move`。[Microsoft File.Replace 文档](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)

这是带冲突检查的文件提交，不是跨任意外部编辑器的数据库事务。其他软件不遵守 Mic Studio mutex 时，最终摘要检查与系统替换之间仍有极短的竞争窗口；操作期间不应同时在其他编辑器保存 APO 配置。未知复杂配置拒绝自动接管，是目前有意保留的边界。

## 检测状态的含义

检测读取 APO 安装/卸载注册信息、常见安装位置、版本和官方 Configurator/Editor 路径。端点效果的 CLSID 注册仅在对应 COM 组件明确指向 Equalizer APO 时报告“已发现注册”；否则显示未知或待核对。读取注册表不会安装、绑定或修改默认设备。

以下状态不能互相代替：找到 APO ≠ 目标录音端点已绑定；注册存在 ≠ 当前 WASAPI 采集路径经过 APO；配置已保存 ≠ 可听效果已生效；本地采集成功 ≠ Discord 已收到该效果。Windows 端点属性包含系统效果开关和设备格式等信息；单独枚举成功不证明处理链生效。[Microsoft 音频端点属性](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audio-endpoint-properties)

## 可选的真实验证步骤

这些步骤需要用户主动开始采集。当前自动测试不会打开麦克风或修改真实系统 APO 配置。

1. 戴耳机，保持监听关闭；在官方 Configurator 中核对目标录音端点及所需重启步骤。
2. 在 Mic Studio 直接 APO 模式选择同一麦克风，先保存当前设置。确认其他应用没有同步修改 APO 配置。
3. 先用平直 EQ 和 0 dB 增益采集讲话基线，记录真实峰值与 RMS。然后通过预览把数字增益改为 **−12 dB 衰减**，主动应用并再次采集。两次讲话不是同一次表演，音量差只能作路径初筛，不能声称精确的因果测量。
4. 需要可重复的响应验证时，用固定电平、固定位置的外部声源做受控扫频/音调采集，记录所选端点、共享模式、系统效果设置、采样率与采集方法。不要把同一端点的两路采集命名为独立“原声/处理后”。
5. 验证结束立即应用原先参数；中止或异常后也检查并恢复。衰减未恢复会影响继续使用该端点的其他程序。该手动流程不宣称具备尚未实现的自动临时配置恢复。
6. Discord 直接模式选择原 USB 麦克风，在 Discord 内单独测试；如需人工标记，通过后记录设备、模式及时间，设备/模式变化即作废。线路模式使用 CABLE Input 发送、CABLE Output 作为 Discord 输入；避免本机 DSP 与 APO 重复处理。
7. 确认正常讲话、轻声、尾音、打字、停顿及突然较响，再进行连续一小时的真实运行与通话，记录中断、漂移、睡眠唤醒和拔插。真实听感、端到端延迟、Discord 和长时间验收仍必须单独完成。

## 已执行的自动验证

`tests/MicStudio.Apo.Tests` 使用临时目录，覆盖端点注入拒绝、invariant 数值、动态能力差异、非有限/越 Nyquist 参数、只读预览、作用域恢复、UTF-8/UTF-16 与行尾保持、主文件/片段冲突、外部 Include/脚本/Peace 拒绝、重复应用、恢复保留外部编辑、缺失标记、首次创建、大小上限及损坏设置。测试成功仅证明配置层对应行为，不证明真实系统音频、APO、麦克风或 Discord。
