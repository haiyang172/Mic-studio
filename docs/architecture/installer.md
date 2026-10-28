# 当前用户安装器

`MicStudio.Setup.exe` 是原生 WPF 安装器，嵌入便携发行 ZIP；不需要开发 SDK、另装运行时或管理员权限。发行包随附未修改的 VB-CABLE 官方驱动包，但应用安装过程不会安装驱动、自动打开麦克风、自动启动主程序或添加开机启动项。主程序打开由安装完成后的独立按钮触发；驱动在主程序设备页面由用户单独打开官方安装器并确认 UAC。

VB-CABLE 由 [VB-Audio](https://vb-cable.com) 提供，是 Donationware，欢迎用户[捐赠/购买许可](https://vb-audio.com/Services/licensing.htm)。安装器和主程序展示该归属及入口；仅捆绑标准 VB-CABLE，未捆绑 A+B、C+D 等其他产品。官方分发条件及包的具体来源、摘要和签名记录见 THIRD_PARTY.md 与组件说明。

固定安装位置为 `%LOCALAPPDATA%\Programs\MicStudio`。开始菜单建立 `Mic Studio Next` 文件夹；桌面入口是可选的 `Mic Studio Next.lnk`。使用独立名称避免覆盖旧原型的入口，发现同名但指向其他程序的快捷方式时保留该入口并报告问题。卸载入口仅登记在 HKCU，适用于当前用户。

个人设置、预设和录音位于安装目录之外，安装/升级/卸载不会删除 `%LOCALAPPDATA%\MicStudio.Next`，也不会触碰旧版 `mic-studio-desktop`、独立 APO 或 VB-CABLE。卸载主程序不会自动撤销已经写入系统的 APO 配置；卸载前应在主程序使用恢复入口。

## 构建

先发布自包含 Windows x64 主程序，再将主程序的文件直接放在 ZIP 根目录，必须包含 `MicStudio.exe`。主发布脚本会完成此步骤。

```powershell
dotnet publish src/MicStudio.Setup/MicStudio.Setup.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:PayloadZip=D:\absolute\MicStudio-win-x64.zip `
  -o dist\installer
```

普通 `build` 允许没有负载，方便检查界面；`publish` 缺少 ZIP 会失败。安装器不能作为未发布的框架依赖 EXE 分发，卸载时需要它是自包含单文件。

## 文件保护

- 解压前检查 ZIP 路径、大小、文件数、重复名称、符号链接及保留文件。禁止路径越界、替换所有权标记和安装器自身；实际解压长度还要与声明长度相符。Windows 保留设备名称也被拒绝。[Windows 文件命名规则](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file)
- 写入固定 Programs 父目录下的独立暂存目录；已有安装必须带有效 `.micstudio-install.json` 所有权标记。schema2 标记记录每个已安装文件的规范相对路径和 SHA-256，包含安装器自身；完整校验清单后才能清理。
- 升级先移动有效旧安装到同父目录备份，再提交暂存目录。提交失败尝试恢复旧目录；成功后仅删除备份内符合清单且内容未改动的文件。额外文件、用户录音、改动过的文件及目录标记保留在备份，并在结果中告知保留位置。
- 真实安装/备份/暂存目录没有递归整目录删除操作。清理前重新核对绝对路径、固定父目录、精确名称/随机 GUID、所有权标记和所有目录内链接；仅删除摘要相符的已安装文件，目录只在空且属于清单文件的父目录时移除。存在其他文件/目录时保留根目录及标记。
- 旧 schema1 标记没有文件清单时，不猜测哪些文件属于安装器，自动清理会拒绝并保留整个目录；升级后这类旧目录作为备份保留。未来清单版本、越界/重复/保留路径、无效摘要或文件/目录冲突也在删除任何文件前拒绝。
- 已安装主程序正在运行时，安装与卸载会要求先退出。不同目录中的旧原型不会被当成当前安装自动关闭。
- 开始菜单卸载先把自包含安装器复制到系统临时目录再打开卸载界面，从而能够移除安装目录中的程序。复制的临时卸载器可能保留到 Windows 临时文件清理；它不继续后台运行。
- 安装器没有任意目标目录或静默安装接口。`--uninstall` 打开明确的卸载操作界面，不自动删除。

## 已验证与待验证

以下开关用于只读或合成检查，不会安装/卸载软件：

```powershell
MicStudio.Setup.exe --self-test D:\absolute\setup-tests.json
MicStudio.Setup.exe --validate-payload D:\absolute\payload-check.json
MicStudio.Setup.exe --ui-smoke D:\absolute\setup-ui
```

合成测试覆盖路径与固定删除目标保护、ZIP 越界/重复/链接/保留文件、非 PE 主程序、实际解压大小不符，以及临时安装夹具中的清单校验、未知录音/改动文件保护、重复清理、旧/未来版本拒绝和完整验证先于删除。不会安装/卸载真实当前用户程序。嵌入负载检查要求资源存在及根目录的 Windows EXE；任何失败返回非零退出码。界面检查仅打开正常/紧凑安装窗口并导出布局，不会点击安装。

真实安装、升级、Start Menu/Desktop 快捷方式、Windows 应用列表卸载入口及临时卸载器完整流程，仍应在 Windows 测试用户/虚拟机实际验证，不能用上述检查替代。当前发行尚未加入代码签名、自动升级或 MSI 企业部署功能。
