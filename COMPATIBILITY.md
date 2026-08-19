# AutoCAD 2021–2026 兼容方案

插件从当前版本起只支持 AutoCAD 2021–2026，按 Autodesk 托管 API 运行时代际提供两套组件：

| 发布目录 | AutoCAD 年份 | 运行时 |
|---|---:|---|
| `CadApi/R24` | 2021–2024 | .NET Framework 4.8 |
| `CadApi/R25` | 2025–2026 | .NET 8 Windows |

启动器识别实际 AutoCAD 年份并只加载匹配代际的 DLL，不会用 R24 DLL 代替 R25，也不会尝试加载 AutoCAD 2020 及更早版本。

R24/R25 安装均会检查主插件、PDF 依赖、箭头库、建筑设计说明助手和楼梯大样组件。任何必需组件缺失都会停止安装并明确提示。

构建命令：

```powershell
./build/Build-Release.ps1 -Bands R24,R25
```

本机缺少某一代 AutoCAD SDK 时，可以只构建已安装的代际；正式发布前应在具备两套 SDK 的构建环境生成完整包。
