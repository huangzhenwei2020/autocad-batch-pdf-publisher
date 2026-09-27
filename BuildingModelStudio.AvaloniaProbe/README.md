# 万落建筑模型跨平台 GPU 视口探针

独立 `net8.0` + Avalonia 12.1.3 程序，引用 [`BuildingModel.Core`](../BuildingModel.Core/README.md)。当前 CAD 的 `JZMX` 与发布包中的 `建筑模型/万落建筑模型.exe` 已指向此程序。`--project <项目目录> --model <模型名称>` 会打开现有模型，若不存在则创建同名空模型；`--model <model.json 路径>` 仍可直接打开单个文件。无参数时使用内置两层样例。界面仍是 Blender 式工作区的粗略三栏技术探针，不是最终产品 UI。

```text
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --smoke
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --snapshot <绝对路径.png>
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --model <项目/建筑模型/名称/model.json>
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --pick-check
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --bench 50000
dotnet run --project BuildingModelStudio.AvaloniaProbe -c Release -- --gpu-bench 50000
```

`--smoke` 等待 GPU 帧绘制、检查 OpenGL 错误，并从视口中心拾取一个真实构件 ID；`--snapshot` 从 Avalonia 合成器捕获包括 GPU 视口的完整窗口。点击模型或左侧列表会同步选中构件；左键拖动旋转，中键/右键拖动平移，滚轮缩放。选择墙可一次修改墙长、墙厚、墙高（0 表示随楼层）；选择门窗可一次修改沿墙定位、宽、高、窗台高。修改在克隆模型上校验，洞口越出宿主墙、顶部超出墙高、与其他洞口重叠时整笔拒绝；合法修改重建视口，可撤销/重做。同值提交不增加修订。

工具栏可打开、保存、另存为模型；未保存修改在标题显示 `*`，关闭或打开另一模型时询问保存。保存沿用共享层的原子替换和 `.bak` 备份。模型保存为项目 `建筑模型/<名称>/model.json` 后，点击“生成 CAD 视图”会在同级 `views/` 写入四立面、剖面、轴测、各层平面、门窗表及图纸 JSON，已有同名文件留 `.bak`。点击“推到 CAD”会生成视图，并将图纸优先写进现有待落图清单；回到 AutoCAD 执行 `LTTZ` 才会落图。若同级存在 `openings.json`，会使用项目门窗类型库；否则门窗立面只保留洞口轮廓。打开模型的读盘、体量和拾取索引构建在后台执行。

仍无视口精确吸附、操纵柄、新增构件、复杂构件参数编辑及跨平台 CAD 宿主连接。现阶段虽然作为 Windows 发布包的默认建模入口，能力仍小于旧 WinForms 编辑器；请优先用项目副本试用，不把它视为完整编辑器。

Windows 本机截图为 [编辑与保存探针截图](../.artifacts/avalonia-edit-save.png)（`.artifacts` 是本机忽略目录）。本机通过了渲染、中心点构件拾取、截图、模型文件保存/重开、视图生成及 CAD 取件清单识别。2026-09-27 追加了真实窗口鼠标测试：原 GPU 控件直接接收鼠标事件的写法没有响应；在 GPU 画面上叠加透明输入区域后，实际左键拖动会旋转、滚轮会缩放，点击屋面会选中并高亮 `SLAB-ROOF`。中键/右键平移的逻辑已接入，但此次自动化工具没有发出中键/右键拖动，待人工复核。构件高亮由 GPU uniform 切换，不再因选择而重建/上传网格；点击拾取改用 BVH 索引，`--pick-check` 将 3 个视角、1875 个屏幕采样点和逐三角形射线扫描比对。Windows 本机 5 万道无洞口墙（30 万面、60 万三角形）CPU 基线：体量 347 ms、网格加索引 415 ms、200 次拾取 P95 0.009 ms、托管内存约 180 MB。此基线不代表 GPU 首帧、旋转帧率或复杂建筑的真实性能。macOS/Linux 实机、其他显卡、窗口缩放、GPU 大场景仍待验。编辑后体量重建放到后台并丢弃过期结果，但模型参数校验仍会在 UI 线程执行。当前仓库自带 .NET 8 SDK 的编译器比 Avalonia 12.1.3 分析器所用版本旧，因此出现 `CS9057` 构建提示，运行不受影响；正式采用前须统一 SDK 与 Avalonia 版本。

`--gpu-bench` 在真实 Avalonia 窗口中后台准备网格，等待上传后的首帧，再程序化旋转 60 帧。测试模式每帧调用 `glFinish`，记录含驱动提交与 GPU 完成等待的耗时；同时记录从请求到回调被轮询到的响应时间（5 ms 轮询误差）。它不改动正常运行模式。在 RTX 3060 上经 ANGLE/D3D11 的 5 万墙样例：首帧约 26–32 ms、旋转同步帧 P95 约 14.3–14.7 ms、托管内存约 226 MB。这个重复墙阵列不足以代表复杂构件与真实建筑的 GPU 负载，且 14 ms 已接近 60 FPS 帧预算；正式扩展前需要分块与可见性裁剪。

macOS/Linux 实机、其他显卡、100%/150%/200% Windows 缩放和 AutoCAD 实际落图仍待验。编辑后体量重建放到后台并丢弃过期结果，但模型参数校验仍会在 UI 线程执行。当前仓库自带 .NET 8 SDK 的编译器比 Avalonia 12.1.3 分析器所用版本旧，因此出现 `CS9057` 构建提示，运行不受影响；正式采用前须统一 SDK 与 Avalonia 版本。
