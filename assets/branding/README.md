# Mic Studio 图标

M 与 S 使用相连的圆角笔画，M 是 Mic、S 是 Studio；下方的短支架呼应麦克风。薄荷绿标识配合石墨色圆角底板，与桌面界面一致。

- `mic-studio.svg`：可编辑的矢量源文件，无字体或第三方图案依赖。
- `mic-studio.png`：512×512、透明外角，用于界面品牌及预览。
- `mic-studio.ico`：16、20、24、32、40、48、64、128、256 像素九个 32 位透明图层，用于 EXE、窗口及系统托盘。小于 32 像素时省略细小支架，保留 MS 识别度。

在 Windows 中运行 `scripts/Generate-Icon.ps1`，由 SVG 重新生成 PNG 和 ICO。渲染使用 WPF 矢量图形，无外部图片服务或图片处理依赖。
