# 万落建筑模型跨平台 GPU 视口探针

独立 `net8.0` + Avalonia 12.1.3 程序，引用 [`BuildingModel.Core`](../BuildingModel.Core/README.md)。从现有 `SampleModelFactory.CreateTwoStoreyHouse()` 建立 276 个体量面，三角化后送入 OpenGL GPU 控件；界面只提供 Blender 式工作区的粗略三栏占位，用来验证**真实窗口中的模型显示**，不是产品 UI，也不保存或回写 CAD。

```text
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --smoke
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --snapshot <绝对路径.png>
```

`--smoke` 等待 GPU 帧绘制、检查 OpenGL 错误，并从视口中心拾取一个真实构件 ID；`--snapshot` 从 Avalonia 合成器捕获包括 GPU 视口的完整窗口。点击模型或左侧列表会同步选中构件，鼠标左键拖动可旋转样例。选择墙可改墙长，选择门窗可改沿墙定位；修改通过共享模型校验后重建视口，撤销/重做也在内存中生效。当前仍无吸附、缩放、保存或 CAD 连接，不应进入正式发布流程。

Windows 本机截图为 [编辑探针截图](../.artifacts/avalonia-edit-probe.png)（`.artifacts` 是本机忽略目录）。本机通过了渲染、中心点构件拾取和截图，但 macOS/Linux 实机、其他显卡、窗口缩放和大量构件性能仍待验。当前点击拾取逐三角形在 CPU 上测试，选中后重传全部顶点；这适用于小样例，正式大模型需分块索引或 GPU ID 缓冲及局部更新。当前仓库自带 .NET 8 SDK 的编译器比 Avalonia 12.1.3 分析器所用版本旧，因此出现 `CS9057` 构建提示，运行不受影响；正式采用前须统一 SDK 与 Avalonia 版本。
