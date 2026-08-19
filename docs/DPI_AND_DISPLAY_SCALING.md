# Windows DPI 与显示缩放适配

万落建筑工具按 Windows 显示缩放设计，目标范围为 100%–250%，支持高分辨率显示器及多显示器切换。

## 主插件窗口

AutoCAD 决定宿主进程的 DPI 感知模式，插件不会修改整个 AutoCAD 进程。所有 WinForms 子窗口统一继承 `DpiAwareForm`：

- 使用 `AutoScaleMode.Dpi`，设计基准为 96 DPI；
- Windows 文本与应用缩放变化时重新检查窗口尺寸；
- 窗口不会超过当前显示器工作区；
- 小屏幕或高缩放下自动降低窗口最小尺寸；
- 固定内容较多的对话框允许调整大小或滚动；
- 使用 `Microsoft YaHei UI`，避免中文字体回退导致控件高度异常。

WPF 图框登记窗口使用设备无关单位，并在加载时限制到当前 Windows 工作区。

## 启动器与独立安装器

独立 EXE 使用应用清单声明 `PerMonitorV2`：

- 在不同缩放比例的显示器之间移动时由 Windows 重新缩放；
- 使用 WinForms DPI 自动缩放；
- 窗口允许在高缩放或小屏幕环境调整大小。

## UI 开发约束

新增 WinForms 窗口必须继承 `DpiAwareForm`，优先使用 `TableLayoutPanel`、`FlowLayoutPanel`、`Dock`、`Anchor` 和 `AutoSize`。避免依赖绝对像素定位；必须固定定位时，应为窗口启用 `AutoScroll`。

建议发布前至少检查：

- 1920×1080：100%、125%、150%；
- 2560×1440：125%、150%；
- 3840×2160：150%、175%、200%；
- 双显示器采用不同缩放比例并来回移动窗口。
