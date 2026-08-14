# 从 GitHub 构建与安装最新版本

本文是万落建筑工具唯一的发布流程。不要运行仓库根目录、`CadApi`、旧安装包目录或聊天测试过程中生成的 EXE/DLL；这些位置可能是旧版本。

## 1. 拉取最新代码

关闭 AutoCAD 后，在仓库目录执行：

```powershell
git pull --ff-only
git status --short
```

正式发布前，`git status --short` 应为空。若有本地修改，先确认、提交或备份，不要直接覆盖。

## 2. 编译环境

需要：

- Visual Studio Build Tools（含 MSBuild）。
- .NET Framework 4.8 或 4.8.1 Targeting Pack。
- 编译 AutoCAD 2025–2026（R25）时需要 .NET 8 SDK。
- 本机至少安装一个 AutoCAD 2014–2026，脚本会从固定磁盘及 Autodesk 注册表查找，包括非默认安装目录。

推荐把 .NET 8 安装为系统 SDK。也可以把便携 SDK 放在仓库的 `.tools\dotnet`；该目录不会提交到 GitHub。

## 3. 唯一发布命令

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Build-Release.ps1
```

脚本默认编译本机能提供 SDK 的全部 AutoCAD API 代际。也可以明确指定：

```powershell
.\build\Build-Release.ps1 -Bands R24,R25
```

版本分组为：

| 目录 | AutoCAD 版本 |
|---|---|
| R19 | 2014（停止更新；常规发布不再生成） |
| R20 | 2015–2016 |
| R21 | 2017 |
| R22 | 2018 |
| R23 | 2019–2020 |
| R24 | 2021–2024 |
| R25 | 2025–2026 |

每次执行都会先删除旧发布目录，再从当前源码重新生成。唯一可安装目录是：

```text
dist\WanLuoArchitectureTools\
```

只能运行其中的：

```text
万落建筑工具启动器.exe
```

## 4. 如何确认是最新版本

发布目录中的 `build-info.json` 记录：

- 构建时间；
- Git 分支和提交号；
- 构建时工作区是否有未提交修改；
- 启动器及各 CAD 代际插件 DLL 的 SHA-256。

用以下命令比较当前提交：

```powershell
git rev-parse HEAD
Get-Content .\dist\WanLuoArchitectureTools\build-info.json
```

正式交付时，两个提交号应一致，且 `GitDirty` 应为 `false`。发布目录中不会包含带 `fix`、日期、`test` 等后缀的历史 DLL；每个 `CadApi\Rxx` 只使用正式的 `BatchPdfPublisher.dll`。

## 5. 安装与更新

1. 关闭全部 AutoCAD/天正进程，避免旧 DLL 被占用。
2. 运行 `dist\WanLuoArchitectureTools\万落建筑工具启动器.exe`。
3. 选择检测到的 AutoCAD 或“天正产品 + AutoCAD”组合。
4. 便携使用时直接从发布目录加载；选择永久安装时，启动器更新 AutoCAD 的 ApplicationPlugins 包。
5. 更新 GitHub 代码后必须重新执行发布命令，再运行新发布目录中的启动器。不要复用旧压缩包或旧安装目录。

天正和 AutoCAD 可以安装在任意磁盘；启动器依据安装信息和实际可执行文件探测，不依赖本机固定路径。

## 6. 清理开发目录

先预览将删除的内容：

```powershell
.\build\Clean-Workspace.ps1 -WhatIf
```

确认后执行：

```powershell
.\build\Clean-Workspace.ps1
```

它只删除仓库内部可再生成的文件，包括旧 `CadApi`、根目录 DLL/EXE/PDB、测试日志、临时脚本、旧安装包、解压出来的重复组件和中间编译目录；不会删除 `dist`、源码、用户工程、DWG 或用户参数。

## 7. 仓库目录约定

```text
BatchPdfPublisher/                 主 CAD 插件源码
BatchPdfPublisherLauncher/         启动器源码及内嵌组件包
CadArchSpecEditor/                 建筑设计说明助手源码与资源
Resources/                         DWG、PC3、PMP 等运行资源
build/                             构建、兼容和清理脚本
docs/                              当前说明、历史记录和规划
dist/                              本机发布结果（不提交）
```

禁止提交：编译输出、PDB、临时 DLL、旧安装包、测试日志、本机 SDK、解压出的重复源码或组件。运行资源若要随安装器发布，应放入对应源码的 `Payload` 或 `Resources` 并由项目文件显式嵌入。

`build\Build-Compatibility.ps1` 是需要完整 Autodesk SDK 矩阵时使用的高级构建入口；日常从 GitHub 拉取后安装，应使用 `build\Build-Release.ps1`。
