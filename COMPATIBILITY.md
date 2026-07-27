# AutoCAD 2014–2026 兼容方案

插件不能用一份 AutoCAD 托管 DLL 覆盖 2014–2026。发布包按 Autodesk 的运行时/SDK 代际保存七份插件，启动器识别已安装产品后选择对应目录。

| 插件目录 | AutoCAD | 运行时 | 编译 SDK |
|---|---:|---|---|
| `CadApi/R19` | 2014 | .NET Framework 4.0 | ObjectARX 2014 |
| `CadApi/R20` | 2015–2016 | .NET Framework 4.5 | ObjectARX 2015 |
| `CadApi/R21` | 2017 | .NET Framework 4.6 | ObjectARX 2017 |
| `CadApi/R22` | 2018 | .NET Framework 4.6 | ObjectARX 2018 |
| `CadApi/R23` | 2019–2020 | .NET Framework 4.7 | ObjectARX 2019 |
| `CadApi/R24` | 2021–2024 | .NET Framework 4.8 | ObjectARX 2021 |
| `CadApi/R25` | 2025–2026 | .NET 8 | ObjectARX 2025 |

## 构建

1. 将每代 SDK 的 `acmgd.dll`、`acdbmgd.dll`、`accoremgd.dll`、`AdWindows.dll` 放入 `build/AutodeskSdk/Rxx`。
2. 安装对应 .NET Framework Targeting Pack；R25 另需 .NET 8 SDK。
3. 执行：

```powershell
.\build\Build-Compatibility.ps1
```

脚本只会将真实编译成功的文件写入 `CadApi/Rxx`；缺少 SDK 或 Targeting Pack 时会明确列出，不会用其他版本 DLL 冒充。

## 发布前验证

每个 AutoCAD 主版本至少完成：启动器识别、NETLOAD/自动加载、Ribbon、DWG 打开与激活、模型/布局扫描、单页打印、合并 PDF、SBB 属性修改、天正样图显示与发布。天正兼容性必须按实际天正版本和其支持的 AutoCAD 组合另测，不能仅凭 AutoCAD DLL 编译通过认定。
