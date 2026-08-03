# AutoCAD 宿主技术验证

## 目标

验证“建筑设计说明助手”能否用同一套 React 页面同时运行在 AutoCAD 2022 和
AutoCAD 2026 中，并确定不会影响 AutoCAD 稳定性的宿主方式。

## 版本矩阵

| AutoCAD 版本 | 宿主目标框架 | 本项目宿主 |
| --- | --- | --- |
| 2022–2024 | .NET Framework 4.8 | `CadArchSpec.Host.AutoCAD2022` |
| 2025–2026 | .NET 8 / Windows | `CadArchSpec.Host.AutoCAD2026` |

共享业务程序集目标框架为 `netstandard2.0`，因此两套宿主共用同一套领域模型、
应用服务、规则引擎、规范注册表、布局引擎和消息协议。

## 稳定宿主方案

采用以下组合：

- AutoCAD 原生 `PaletteSet`；
- `PaletteSet.Add` 加载 WinForms `UserControl`；
- WinForms `Microsoft.Web.WebView2.WinForms.WebView2`；
- React 生产静态资源通过虚拟主机映射加载；
- WebMessage 完成 `editor.ready` / `host.ready` 握手；
- WebView2 初始化失败时只在面板显示错误，不让异常穿透到 AutoCAD。

没有继续采用 WPF `WebView2CompositionControl`，因为它在本机 AutoCAD 2026
的 `PaletteSet.AddVisual` 实机验证中触发了原生崩溃。

## 原生加载器

通过 `NETLOAD` 加载插件时，AutoCAD 的程序集加载上下文不会自动使用 NuGet
生成的 `.deps.json` 解析 WebView2 的 RID 原生资产，因此必须显式解析：

```text
runtimes\win-x64\native\WebView2Loader.dll
```

- .NET 8 宿主使用 `NativeLibrary.SetDllImportResolver`；
- .NET Framework 4.8 宿主在创建 WebView2 控件前使用 `LoadLibrary`。

## 编译

```powershell
tmp\dotnet-sdk-8\dotnet.exe build CadArchSpecEditor\CadArchSpecEditor.sln -c Release
```

也可运行：

```powershell
CadArchSpecEditor\build\Test-HostCompatibility.ps1
```

脚本会检测本机 AutoCAD 2022、AutoCAD 2026 SDK 和 WebView2 Runtime，再分别
编译真实宿主。

## AutoCAD 2026 手工验证

1. 启动 AutoCAD 2026 并创建或打开一个图形。
2. 执行 `NETLOAD`。
3. 选择：

   ```text
   CadArchSpecEditor\src\CadArchSpec.Host.AutoCAD2026\
   bin\x64\Release\net8.0-windows\CadArchSpec.Host.AutoCAD2026.dll
   ```

4. 对本地开发 DLL 选择“加载一次”。
5. 执行 `JZSM`。
6. 面板应显示“建筑设计说明助手”和“AutoCAD 宿主已连接”。
7. 关闭面板，再执行一次 `JZSM`，面板应能正常恢复。

## 当前结果

2026-07-29 已在本机 AutoCAD 2026 实机通过：

- DLL 加载成功；
- `JZSM` 注册成功；
- WinForms PaletteSet 正常显示；
- WebView2 正常启动；
- React 静态资源正常加载；
- CAD/前端握手成功；
- 面板关闭后重新打开成功；
- AutoCAD 无崩溃。

AutoCAD 2022 宿主已完成真实 SDK 编译和静态兼容检查，仍需在安装 AutoCAD 2022
的测试机上执行同样的运行时回归。
