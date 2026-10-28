# VB-CABLE 捆绑、检测与更新

Mic Studio 随发行包提供标准 VB-CABLE 原始 ZIP，并在设备页面提供驱动检测、官网检查、重新下载及主动打开安装器。软件安装过程不会自动修改驱动；已经存在端点时也不会自动重装。VB-CABLE 由 [VB-Audio](https://vb-cable.com) 提供，是 Donationware，欢迎用户[支持原作者及购买许可](https://vb-audio.com/Services/licensing.htm)。原包分发保留官方文件，未捆绑 A+B、C+D 等额外产品。发布包应包含原始来源、摘要及签名记录，详见 THIRD_PARTY.md。

## 真实状态与版本

`DriverManager.Detect` 只读取 Windows 服务登记与实际驱动文件，识别官方 INF 使用的 `VBAudioVACMME` 及兼容服务名。文件版本来自安装路径对应的实际 `.sys` 元数据；缺失、权限不足或无法核对时报告未知。端点完整性由主程序真实枚举结果传入，CABLE Input 是播放发送端，CABLE Output 是录音接收端。[官方 VB-CABLE 产品说明](https://vb-audio.com/Cable/)

驱动文件版本、驱动安装器版本和 `Pack45` 一类 ZIP 编号是不同维度，不能按字符串互相比较。官网检查只判断**本地安装包**相对于官网公布的包编号是否有更新；“本地包编号一致”不等于“当前驱动已是最新”。同编号原包也可能更新，可用“重新下载”刷新。安装器打开后仅报告已打开，安装/更新是否成功须按官方提示操作并在必要重启后重新检测，不能由启动进程成功推断。

## 网络与缓存

- 网络检查只在用户主动点击后进行，读取 `https://vb-audio.com/Cable/`；从网页实际提供的标准 `VBCABLE_Driver_PackNN.zip` HTTPS 链接中选取最大编号，不猜测未来地址。
- 页面与 ZIP 使用独立的固定域名/路径白名单；每次重定向重新检查 HTTPS、端口、用户信息、查询串和目标。页面最大 2 MiB，驱动 ZIP 最大 20 MiB。
- 下载先写随机临时文件，通过 ZIP 检查后原子提交到 `%LOCALAPPDATA%\MicStudio.Next\Components\Downloads`。取消/失败清理该临时文件，已有有效包保留。下载与包校验不触发 UAC。
- 解析包内容前拒绝路径越界、Windows 保留名称、重复名称、符号链接、超过 512 项或超过 100 MiB 的解压总量。标准 x64 安装器必须位于 ZIP 根目录，完整原包保留。
- 安装准备解压到以包 SHA-256 命名的缓存目录；禁止重解析点；逐个核对所有伴随文件字节，并拒绝目录中额外文件。解压临时目录失败清理前校验固定父目录、摘要和随机 GUID。

## 安装信任与权限

仅允许打开 `VBCABLE_Setup_x64.exe`。启动前验证安装包未变，核对完整解压目录，然后用 Windows `WinVerifyTrust` 的 Authenticode 策略验证 EXE，并要求已适配的 VB-Audio/Vincent Burel 发行者名称。只有返回值严格为零才视为 Windows 信任验证通过。[Microsoft WinVerifyTrust 文档](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)

检查使用系统本地信任与缓存，不宣称完成实时在线吊销检查。找不到嵌入签名或无法确认信任时禁止启动，不能把“有 CAT 文件”直接当成验证通过；Windows/官方安装器仍负责驱动包和系统签名策略。发行包当前已核实 x64 官方安装器的有效签名及发行者 `BUREL VINCENT Entrepreneur individuel`。

用户点击打开驱动安装器后才请求 `runas` UAC；取消返回明确状态，保留当前软件配置。Mic Studio 不发送静默安装参数，不替用户确认 UAC，不自动卸载驱动或重启 Windows。用户仍需在官方安装器中选择操作并遵从其重启提示。

文件校验与启动之间存在普通用户可写缓存的短暂竞争窗口；SHA-256 及信任检查不能替代系统文件权限或驱动安装时的 Windows 签名检查。该管理器不提供任意命令、任意 EXE 或任意提权路径。

## 已执行验证

`tests/MicStudio.Components.Tests` 覆盖官网链接选择、无下载链接、非 HTTPS/域名伪装/越界路径/用户信息/查询串重定向拒绝、ZIP 越界/重复/链接/错误安装器/Windows 设备名、SHA-256、包变更冲突。开发环境另执行实际官网检查与强制重新下载，取得原 Pack45 相同摘要，并只准备/验证官方签名 EXE，未启动安装器、请求 UAC 或安装驱动。这些检查不代替真实安装/升级、重启和音频线路验收。
