# 建筑模型 Blender 几何探针

这个探针把**现有 `Shared/BuildingModel` 类型和 `BuildingModelJson` 文件格式**生成的同一堵墙/窗改前、改后模型送进 Blender。它只检验第一道墙窗布尔门槛，不是新的建模程序，也不会修改 AutoCAD 或项目模型。

在安装 .NET 8 SDK、Python 3 和 Blender 4.5 的 Windows/macOS/Linux 机器上运行：

```text
python BuildingModel.KernelProbe/run_probe.py --dotnet <dotnet路径> --blender <blender路径> --output <结果json路径>
```

`.NET` 进程生成两个临时 `model.json`；Blender 在一次后台进程内依次读取并做 `EXACT` 网格布尔。校验构件 ID、所有边流形、实体体积相对误差 ≤ 0.01% 和洞口四角位置误差 ≤ 0.1 mm。输出记录每个样例的布尔耗时及进程总耗时。目录下的脚本只使用 Python 标准库、Blender 内置 `bpy/bmesh` 和仓库已有 C# 源码，没有额外 Python 包。

当前探针通过独立的 [`BuildingModel.Core`](../BuildingModel.Core/README.md) `net8.0` 类库获取同一份模型类型。

当前样例仅为水平直墙和单窗，使用局部坐标、毫米到米转换；尚未覆盖异形/曲墙、复杂交接、剖切、隐藏线、大坐标、多个洞口、十万级场景、反复编辑 P95 和进程崩溃恢复。因此通过此探针只说明 Blender 可以处理这个简例，不能作为正式 CAD 几何内核选型结论。跨平台运行也需要在 macOS/Linux 真机分别执行，Windows 本地编译不等于三平台验证。
