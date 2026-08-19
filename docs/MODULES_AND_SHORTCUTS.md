# 功能模块与快捷键

主插件源码按功能放在 `BatchPdfPublisher/Features`：

- `Publishing`：工程、扫描、PDF 发布和项目管理
- `Frames`：图框创建、登记、识别与比例属性
- `Catalog`：图纸目录
- `Attributes`：批量属性和属性定义编辑
- `Drafting`：制图标准、图层、文字和比例管理
- `DoorWindows`：门窗表、门窗立面和分格
- `Rooms`：天正房间工具
- `Shortcuts`：统一功能登记表、快捷键配置和设置窗口

`FeatureRegistry.cs` 是用户可见功能的唯一登记表。新增功能时：

1. 在对应 `Features/<模块>` 目录实现功能并注册固定内部命令。
2. 在 `FeatureRegistry` 增加一项。
3. Ribbon、经典菜单和快捷键设置窗口会自动出现该功能。
4. `Build-Release.ps1` 会检查登记的命令是否确实存在，缺失时停止打包。

用户执行 `KJJPZ`（默认快捷键）或固定命令 `WLHOTKEYS` 可打开快捷键设置。快捷键保存到：

`%APPDATA%/WanluoArchitectureTools/Settings/shortcuts.ini`

快捷键通过当前 CAD 会话中的 AutoLISP 命令别名生效，不修改用户 `acad.pgp`，保存后无需重启 CAD。

启动器把以下内容作为一个完整产品安装：

- 主插件（批量发布、图框、目录、属性、制图、门窗、房间）
- PDF 依赖和箭头图块库
- 建筑设计说明助手（支持的 R24/R25）
- 一键楼梯大样（支持的 R24/R25）

启动器内嵌模块统一存放在 `BatchPdfPublisherLauncher/Modules`，建筑说明和楼梯不再散落在不同工程的 `Payload` 目录。

对于 R24/R25，任何必需组件缺失都会停止安装并明确提示缺失模块，不再静默跳过。
