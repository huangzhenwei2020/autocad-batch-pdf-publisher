# 万落建筑模型跨平台 GPU 视口探针

独立 `net8.0` + Avalonia 12.1.3 程序，引用 [`BuildingModel.Core`](../BuildingModel.Core/README.md)。默认从现有 `SampleModelFactory.CreateTwoStoreyHouse()` 建立 276 个体量面，三角化后送入 OpenGL GPU 控件；也可打开项目里的 `model.json`。界面仍是 Blender 式工作区的粗略三栏技术探针，不是最终产品 UI。

```text
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --smoke
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --snapshot <绝对路径.png>
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --model <项目/建筑模型/名称/model.json>
```

`--smoke` 等待 GPU 帧绘制、检查 OpenGL 错误，并从视口中心拾取一个真实构件 ID；`--snapshot` 从 Avalonia 合成器捕获包括 GPU 视口的完整窗口。点击模型或左侧列表会同步选中构件；左键拖动旋转，中键/右键拖动平移，滚轮缩放。选择墙可改墙长，选择门窗可改沿墙定位；修改通过共享模型校验后重建视口，可撤销/重做。

工具栏可打开、保存、另存为模型；未保存修改在标题显示 `*`，关闭或打开另一模型时询问保存。保存沿用共享层的原子替换和 `.bak` 备份。模型保存为项目 `建筑模型/<名称>/model.json` 后，点击“生成 CAD 视图”会在同级 `views/` 写入四立面、剖面、轴测、各层平面、门窗表及图纸 JSON，已有同名文件留 `.bak`。若同级存在 `openings.json`，会使用项目门窗类型库；否则门窗立面只保留洞口轮廓。现有 `StudioLaunch.ListViews` 能识别这些视图，AutoCAD 中的更新/落图仍需用户在 Windows 插件中操作。

仍无视口精确吸附、操纵柄、复杂构件参数编辑、跨平台 CAD 宿主连接；不能作为正式编辑器发布。

Windows 本机截图为 [编辑与保存探针截图](../.artifacts/avalonia-edit-save.png)（`.artifacts` 是本机忽略目录）。本机通过了渲染、中心点构件拾取、截图、模型文件保存/重开、视图生成及 CAD 取件清单识别；macOS/Linux 实机、其他显卡、窗口缩放和大量构件性能仍待验。构件高亮由 GPU uniform 切换，不再因选择而重建/上传网格；点击拾取仍逐三角形在 CPU 上测试，适用于小样例，正式大模型需分块索引或 GPU ID 缓冲。编辑后体量重建放到后台并丢弃过期结果，但模型参数校验仍会在 UI 线程执行，超大模型需要进一步异步化。当前仓库自带 .NET 8 SDK 的编译器比 Avalonia 12.1.3 分析器所用版本旧，因此出现 `CS9057` 构建提示，运行不受影响；正式采用前须统一 SDK 与 Avalonia 版本。
