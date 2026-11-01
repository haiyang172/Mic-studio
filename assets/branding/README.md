# Mic Studio 图标

A2「翡翠流线」使用连续、略不对称的圆润声脉，配深青色圆角底板。正式矢量依据用户选定的 `docs/design/selected-logo-a2.png` 重绘，不使用 MS 字母或角色图案。

- `mic-studio.svg`：可编辑的图标主源文件；声脉只有一份路径。
- `mic-studio.png`：512×512、透明外角，用于界面品牌及预览。
- `mic-studio.ico`：16、20、24、32、40、48、64、128、256 像素九个 32 位透明图层，用于 EXE、窗口和任务栏。
- `mic-studio-mark.svg/png`：无底板的独立翡翠色声脉。
- `mic-studio-logo.svg/png`：横向声脉与 Mic Studio 字标。字标已转为轮廓，查看或发布时无需额外字体；生成阶段使用 Windows Segoe UI Semibold。
- `mic-studio-tray-light.svg/png/ico`：适合浅色任务栏的深青色单色声脉。
- `mic-studio-tray-dark.svg/png/ico`：适合深色任务栏的近白色单色声脉。两份托盘 ICO 均含上述九种尺寸，并增加符号占比；程序只读取系统主题/高对比颜色选择图标，不修改系统设置。

在 Windows 中运行 `scripts/Generate-Icon.ps1`，从主 SVG 声脉生成其余矢量、PNG 与 ICO。渲染使用 WPF 矢量图形，无外部图片服务或图片处理依赖。更新主路径后应重新生成整组资产，并检查小尺寸、透明边缘、浅/深色背景及实际应用资源。
