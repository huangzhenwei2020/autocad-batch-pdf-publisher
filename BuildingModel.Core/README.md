# 万落建筑模型跨平台核心（试验入口）

`BuildingModel.Core.csproj` 将 `Shared/BuildingModel` 的建筑语义、JSON、投影、体量与编辑逻辑编成 `net8.0` 类库，不引用 WinForms、WPF 或 AutoCAD 程序集。它继续链接原有源码，因此目前 Windows 建模程序和插件的行为不因这个试验入口改变。`BuildingModel.KernelProbe` 已改为引用该类库，验证首个外部几何服务可以消费同一份模型格式和参数。

编译命令：

```text
dotnet build BuildingModel.Core/BuildingModel.Core.csproj -c Release
```

当前仅在 Windows 上完成编译和运行探针。`StudioLaunch.cs` 内仍有 Windows 建模程序的文件名/搜索逻辑，虽然它可编入 `net8.0`，在 macOS/Linux 的行为尚未验收。下一步应把启动查找移到平台适配层，并在三平台分别运行核心测试，不把“能编译”当作“已兼容”。
