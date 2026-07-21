# 批量 PDF 发布（AutoCAD 2022 起步版）

在 AutoCAD 中执行 `NETLOAD` 加载 `BatchPdfPublisher.dll`，再执行命令 `BPPUBLISH`。

当前已实现：从当前图纸拾取图框图块并登记、双击修改登记、重复图块校验、图框库持久化、A0–A4/加长/方向/打印比例智能建议、属性与手工默认值映射、扫描模型空间图框、按楼栋独立分组和排序、CAD 视图打印范围编号预览，以及图框备注与图块名显示。

首次使用顺序：点击“拾取图框登记” → 在 AutoCAD 图中选择图框图块 → 在登记窗口核对智能检测的纸张、加长、方向和打印比例 → 选择属性标签或填写手工默认值 → 点击“扫描当前图纸”。

默认识别属性标签：`楼栋/BUILDING/栋号`、`图号/SHEETNO/SHEET_NO/DRAWINGNO`、`图名/SHEETNAME/SHEET_NAME/DRAWINGNAME`、`比例/SCALE/PRINTSCALE`。可根据实际图框补充别名。

PDF 合并、跨 DWG 打开和天正对象打印兼容，将在扫描和图框库验证后接入 AutoCAD Plot API。

工程使用 .NET Framework 4.8 编译，与 AutoCAD 2022 的托管插件运行环境保持一致。

## 编译

使用安装了“.NET 桌面生成工具”的 Visual Studio 或 Build Tools 编译启动器项目。它会同时编译插件：

```powershell
msbuild .\BatchPdfPublisherLauncher\BatchPdfPublisherLauncher.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
```

生成文件位于：

```text
启动批量打印插件.exe
BatchPdfPublisher.dll
```

## 项目配置

面板顶部的“当前项目”用于切换配置。先在名称框输入项目名并点击“新建项目”，再配置图框登记和输出参数；点击“保存项目参数”后，图框库、属性映射、纸张/方向/比例默认值、打印样式、白边策略、输出目录和按楼栋合并选项都会保存到该项目。下次启动插件时会自动恢复上次使用的项目。旧版 `BatchPdfPublisher.frames.json` 会在首次运行时迁移为“默认项目”。

项目当前引用 `C:\Program Files\Autodesk\AutoCAD 2022` 下的 AutoCAD 托管程序集，因此构建机需要安装 AutoCAD 2022。AutoCAD 程序集不会复制到输出目录，运行时由 AutoCAD 自身提供。

## 加载和首轮测试

最简单的方式是直接双击项目根目录生成的 `启动批量打印插件.exe`。启动器会先让用户选择 CAD 平台：

1. T20 天正建筑 V9（AutoCAD 2022）
2. AutoCAD 2022
3. AutoCAD 2024

选择后，启动器会把最新版 DLL 安装到当前用户的 `BatchPdfPublisher\\releases` 目录，并写入 AutoCAD 自动加载 bundle。启动 CAD 后插件会自动加载，Ribbon 中出现“批量打印”选项卡；如果当前 CAD 已经运行，启动器还会通过 COM 发送 `NETLOAD` 和 `BPPUBLISH`。T20 必须选择“T20 天正建筑”，不要选择普通 AutoCAD 2022；这样会使用天正自己的 `TGStart.exe` 启动链。

无需再手工执行 `NETLOAD`。每次重新编译插件后，再双击一次启动器即可更新并加载最新版。

发布包内的 `BatchPdfPublisher.pc3` 和 `BatchPdfPublisher.pmp` 是以毫米为单位的加长纸张库。启动器会自动安装到已创建的 AutoCAD `Plotters` / `PMP Files` 用户目录。自定义介质的不可打印边界为 `0 mm`；面板选择“无白边（满幅）”时保持满幅，选择“保留 3 mm 白边”时会在最终 PDF 每页四边精确留出 `3 mm`。

如需手工加载和排查，可按以下步骤操作：

1. 启动 AutoCAD 2022，打开一张包含块图框的 DWG。
2. 执行 `NETLOAD`，选择 `BatchPdfPublisher\bin\Release\BatchPdfPublisher.dll`。
3. 执行 `BPPUBLISH`，确认“批量 PDF 发布”面板正常显示。
4. 点击“拾取图框登记”，选择一个图框块，并输入纸张规格与加长比例。
5. 点击“扫描当前图纸”，检查楼栋、图号、图名、比例和纸张规格是否识别正确。

如 AutoCAD 因安全策略拒绝加载，请把 DLL 所在目录加入 `TRUSTEDPATHS`。使用脚本自动测试时，AutoCAD 2022 对包含中文的脚本路径处理可能异常，可先把 DLL 复制到纯英文目录再执行 `NETLOAD`。
