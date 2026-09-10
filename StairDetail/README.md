# 万落建筑

面向 AutoCAD 和天正建筑的建筑制图效率工具。当前首个功能是“一键楼梯大样”。

## 当前迭代

当前已实现与 CAD 宿主解耦的构件化楼梯计算核心：

- 项目统一构造、楼板、楼层段、梯段和休息平台分层模型。
- 每层可使用不同层高，并支持每层任意跑数。
- 每跑独立设置踏步级数、踏步宽、梯段宽、方向和剖切关系。
- 楼板、楼板梁、梯段、平台和平台梁使用唯一构件编号。
- 增减跑数时按层高自动重新分配推荐踏步级数。
- 可配置的几何校验与制图风险提示。
- 与 CAD 无关的构件化剖面线段描述。
- 共享楼板和梁仅生成一次，避免相邻楼层重复覆盖。
- 剖切梯段使用实线，后方梯段使用隐藏线。
- 楼板梁和平台梁在结构侧连接梯段端面。
- 零第三方依赖的核心测试。
- WPF + WebView2 参数窗口，支持图上选择和拖动构件控制点。
- AutoCAD 2022 命令插件，命令名为 `LTDY`。

## 构建与测试

### 一键安装

保存并关闭 AutoCAD/天正后，双击根目录下的：

```text
install-plugin.cmd
```

安装器会自动执行 Release 构建和测试、生成插件包，并安装到当前用户的：

```text
%APPDATA%\Autodesk\ApplicationPlugins\WanLuoArchitecture2022.bundle
```

安装成功后启动 AutoCAD 2022 或对应天正环境，直接输入 `LTDY`，无需再执行 `NETLOAD`。卸载时双击 `uninstall-plugin.cmd`。

### 手动构建

在 PowerShell 中执行：

```powershell
.\scripts\build.ps1
```

脚本会自动定位 Visual Studio Build Tools，构建解决方案并执行核心测试。

生成 AutoCAD 2022 测试包：

```powershell
.\scripts\package-cad2022.ps1
```

随后可在 AutoCAD 2022 或对应的天正建筑环境中执行 `NETLOAD`，加载：

```text
artifacts/WanLuoArchitecture2022.bundle/Contents/Windows/2022/WL.Stair.Cad2022.dll
```

加载后执行 `LTDY`，会弹出“楼梯构件编辑器”。编辑器包含项目资料、楼层与梯段、楼板/楼板梁和统一构造参数页。确认后在图中指定插入点即可生成构件化连续剖面。

执行 AutoCAD 2022 自动冒烟测试：

```powershell
.\scripts\smoke-test-cad2022.ps1
```

测试会在英文临时目录调用无界面的 `WLSTAIRTEST` 命令、生成楼梯、保存 DWG，并将结果复制到 `artifacts/cad2022-smoke-test.dwg`。可通过 `-ArtifactFileName` 另存测试结果，避免覆盖人工基准图。

`LTDY` 支持实时剖面预览。点击预览中的梯段、平台、楼板或梁可定位对应参数；绿色控制点可修改逐跑踏步宽、楼板/平台尺寸和梁高。统一参数中已预留栏杆、墙、门窗配置，后续迭代将生成这些构件及正式剖切填充。

## 目录

```text
docs/                 产品与开发计划
src/WL.Stair.Core/    参数、计算、规则和几何核心
src/WL.Stair.Cad2022/ AutoCAD 2022 命令与原生图元渲染
tests/WL.Stair.Tests/ 离线可运行的核心测试
scripts/              构建和验证脚本
```

当前已在 AutoCAD 2022 环境完成自动烟雾测试和独立窗口可视验证。下一阶段将完善平面图、尺寸链、剖切填充，以及栏杆、墙和门窗的几何生成。
