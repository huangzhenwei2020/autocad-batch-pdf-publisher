# iOS 风格功能区图标

`approved-concept.png` 是用户确认的设计稿；`ios-icons.png` 是按该稿通过内置 ImageGen 编辑生成的透明图集，包含全部 18 个固定功能。图集内没有按钮文字，中文名称由 CAD 功能区原生绘制。

运行时使用 `RibbonIconAssets.cs` 中测定的切片坐标读取原始像素，再高质量缩小为独立的 32×32 / 16×16、96 DPI 位图，保留圆角、渐变、高光及白色图形，不以程序重画替代素材。不能直接把 258 像素的 CroppedBitmap 交给 Ribbon：CAD 的原生图片框可能不自动缩放，只显示图标一角。两套主插件工程均将图集嵌入 DLL，无须依赖外部图片路径。改变图集尺寸或排布时必须同时调整切片定义。

按钮使用原生 Large / Vertical 排列，显示四字简称；完整名称、当前快捷键及功能说明保留在悬停提示内。全部固定命令保留。CAD 自身控制字体、功能区背景、缩放和窄窗口折叠行为，设计稿中的应用外壳没有替换。

资源检查与预览（Windows PowerShell，STA）：

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File build/Preview-RibbonAssets.ps1 -PluginPath dist/WanLuoArchitectureTools-iOS/CadApi/R24/BatchPdfPublisher.dll
```

检查功能登记与图标一一对应、图集尺寸、透明边角、图标中心及冻结状态，并渲染深浅背景下的实际资源。预览默认保存到 `.artifacts/ribbon-icons-preview.png`，不代表 CAD 实机截图。

## 生成记录

使用内置 ImageGen，输入参考为 `approved-concept.png`。最终提示词：

> Create a production sprite atlas derived from the APPROVED reference, precisely preserve the twelve large iOS squircle icon designs in the bottom half: their glossy bevel, corner shape, gradients, white symbols and colors. Remove all UI, text, labels, modelspace and background. Output ONLY 18 square icons on a fully transparent RGBA background in a perfectly regular SIX COLUMN by THREE ROW grid, canvas aspect 2:1, ideally 1536x768. Each icon centered within its own equal square cell, occupying 80% of cell width/height with 10% transparent padding on every side. No labels, NO TEXT anywhere. Every icon equal size. Row1 exact reference originals left to right: blue printer; cobalt drawing frame; indigo index list; cyan folded document; turquoise staircase; teal window elevation. Row2 exact reference originals left to right: purple drawing layout panels; periwinkle image with vector arrow and node; orange drafting triangle; green stacked layers; amber dimension ruler; sky blue cloud sync. Row3 SIX matching additional icons in precisely same visual family: violet tag with pencil for batch attributes; purple tag with tiny gear for attribute definition; teal spreadsheet grid for table editor; mint room floorplan with pencil for room rename; slate-blue keyboard for shortcut settings; slate-blue three-line menu with small toggle for menu switch. Preserve reference appearance meticulously, not a new style. Opaque colorful icon tile interiors, transparent ONLY outside squircle tiles. Crisp isolated exportable actual icon artwork, no mockup or board. Even six-column by three-row positioning mandatory.

实际返回尺寸为 1774×887；已按该尺寸测定切片并检查全部 18 个图标。
