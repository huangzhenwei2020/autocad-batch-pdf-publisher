# 批量 PDF 发布（AutoCAD 2022 起步版）

在 AutoCAD 中执行 `NETLOAD` 加载 `BatchPdfPublisher.dll`，再执行命令 `BPPUBLISH`。

当前已实现：从当前图纸拾取图框图块并登记、图框库持久化、扫描模型空间图框、读取图块属性（楼栋、图号、图名、比例）、按楼栋筛选、显示真实加长规格（如 A3+1/4）、调整页序与保存图框库。

首次使用顺序：点击“拾取图框登记” → 在 AutoCAD 图中选择图框图块 → 在命令行输入纸张规格（如 `A3`）和加长比例（如 `1/4`；普通图框直接回车）→ 点击“扫描当前图纸”。

默认识别属性标签：`楼栋/BUILDING/栋号`、`图号/SHEETNO/SHEET_NO/DRAWINGNO`、`图名/SHEETNAME/SHEET_NAME/DRAWINGNAME`、`比例/SCALE/PRINTSCALE`。可根据实际图框补充别名。

PDF 合并、跨 DWG 打开和天正对象打印兼容，将在扫描和图框库验证后接入 AutoCAD Plot API。

工程当前使用本机已安装的 .NET Framework 4.8.1 目标包编译；AutoCAD 2022 运行前请确认系统已安装 .NET Framework 4.8.1 或更新版。
