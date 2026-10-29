# P2 后台使用与中英文 · 0.3.0 验证记录

验证日期：2026-10-29。环境：Windows 10.0.26200，普通权限，.NET SDK 10.0.403。未安装或修改驱动，未打开实体麦克风、监听或录音。

## 交付范围

1. 工作区 schema 2 迁移：参数、设备、预设、主题、语言、托盘和登录启动偏好。
2. 默认关闭到托盘；托盘打开、暂停/继续、停止全部本地音频、明确退出。
3. 当前用户登录启动，进入托盘；自动开始处理独立控制且默认关闭。
4. 简体中文 / English 全主窗口切换、记忆，内置预设与频段名称随语言显示。
5. 安装器卸载清理 Mic Studio 自建启动项。

## 自动验证结果

| 检查 | 结果 |
| --- | --- |
| `scripts/Build.ps1` | 0 警告，0 错误 |
| `scripts/Test.ps1` | Core 38、APO 20、WindowsAudio 39、Components 24、Setup 40，全部通过 |
| `scripts/Test-UI.ps1` | 43 项控件/事件检查，27 种中文布局，27 种英文布局，英文未翻译汉字 0 |
| 启动命令单元测试 | 引号、`--minimized`、完整路径、非 exe、控制字符均覆盖 |
| HKCU 启动项自测 | 写入后读取匹配，移除后值不存在；无残留 |
| `--minimized` 启动 | 进程保持运行，`MainWindowHandle=0`，无主窗口 |

### WPF 控件检查新增项

- 新工作区默认 `MinimizeToTray=true`。
- 0.2.x schema 1 工作区迁移为 schema 2、默认托盘和受支持语言。
- 登录启动与自动开始处理分别保存、分别恢复。
- 点击英文语言按钮后导航即时重建为 English；语言选择经工作区重载仍保留；再切回中文不改变音频参数。
- 托盘菜单在停止状态显示“继续处理”，并有“打开 Mic Studio”和“退出”。
- 调用 `Close()` 后窗口隐藏但不进入关闭清理；随后可重新显示。

### 布局与英文覆盖

三种 DIP 尺寸：1280×820、900×600、600×500。七个页面及均衡器/效果高级功能展开。中文 27 种、英文 27 种布局均无 `PageContent` 横向溢出且可滚动到底。

英文检查遍历实际 WPF 视觉树：`TextBlock`、按钮内容、`CheckBox` 文本、`Expander` 标题、窗口标题、导航、顶部栏、底栏、模式下拉框和通知栏，排除操作系统提供的设备名称。英文页面截图为 `artifacts/p2-ui-layout/en-*.png`。

发布后的自包含目录 `dist/MicStudio-0.3.0-win-x64` 再次运行同一 `--ui-smoke`：43/43 控件检查、27 种中文布局、27 种英文布局、英文未翻译汉字 0。发布包同时包含 `RELEASE-NOTES.md`。

## 真实启动项验证

运行：

```powershell
MicStudio.exe --startup-smoke <report.json>
```

报告：

```json
{
  "applied": true,
  "removed": true,
  "applyError": null,
  "removeError": null,
  "valueName": "MicStudio.Next",
  "executable": "D:\\Mic-studio\\src\\MicStudio.App\\bin\\Release\\net10.0-windows\\MicStudio.exe"
}
```

测试后查询 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，`MicStudio.Next` 不存在。该自测不启动安装器、不请求 UAC。

## 边界与仍需人工确认

- 自动检查在停止音频状态下验证托盘菜单和关闭隐藏；未在真实讲话中关闭窗口确认声音连续，也未测量虚拟线路接收端。
- 未注销/重启 Windows 验证登录后托盘图标和自动开始处理；启动命令和注册表值已独立验证。
- 未执行安装、覆盖升级和卸载；卸载清理启动项的代码路径未在真实安装上运行。
- 未在运行中的真实音频上切换语言；实现只重建 WPF 控件，不调用音频引擎，仍需一次实机试听确认无中断。
- 未覆盖 200% 系统 DPI 和跨显示器移动；当前为三个 DIP 尺寸的 WPF 布局检查。
- 英文覆盖主窗口与托盘。Windows 设备名称、Equalizer APO 工具输出、VB-Cable 官方安装器和底层系统错误保留系统原语言。
- 未测试睡眠唤醒、拔插和设备重建对后台会话的影响，仍列入后续验收。

上述项目不能由合成测试替代，发行说明中未宣称已完成。
