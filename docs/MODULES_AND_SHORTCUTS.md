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

`%APPDATA%\WanluoArchitectureTools\用户配置文件\通用设置\shortcuts.ini`

快捷键通过当前 CAD 会话中的 AutoLISP 命令别名生效，不修改用户 `acad.pgp`，保存后无需重启 CAD。快捷键必须以字母开头，只能包含大写字母、数字、连字符或下划线（例如 `GC`、`WL-GC`），长度 2–16 位，且不能与其他功能重复。

### 动态合成的图层命令

除了上表的固定功能，`FeatureRegistry.All` 还会**动态合成**“每图层直达归层命令”：

- 来源是 `LayerShortcutStore`（`%APPDATA%\WanluoArchitectureTools\用户配置文件\通用设置\layer-shortcuts.ini`），
  在“制图标准（`BZS`）→ 图层标准”表的**「快捷键」列**就地维护（原“图层快捷键”独立页已取消）。
- **只有填了快捷键的图层才会生成命令**，留空即不生成，因此不会与固定功能抢键。
- 所有图层命令共用内部命令 `GL`（登记在 `Commands.cs`）。目标图层由别名现场传给
  `[LispFunction]`，不依赖环境变量（`setenv` 改的是 AutoLISP 自己的环境，到不了 .NET 侧，
  只作为兼容通道保留）。实际生成的别名形如：

  ```lisp
  (progn (if WLSETSELECTION (WLSETSELECTION (ssget "_I")))
         (if WLSETLAYER (WLSETLAYER "<目标图层名>"))
         (setenv "WANLUO_TARGET_LAYER" "<目标图层名>")
         (command "GL"))
  ```

  `(if WLSETLAYER ...)` 是防御：万一函数没注册，别名也不会整条中断。目标值读完即清，
  同一会话里后面的手动 `GL` 照常弹选层对话框。这样无需为每个图层注册 `[CommandMethod]`，
  `Build-Release.ps1` 的命令登记校验依然通过（并额外校验两个 LispFunction 已注册）。
- 图层快捷键与固定功能快捷键**分开校验唯一性**，两套命名空间互不干扰。
- 相应地，`ShortcutSettingsForm` 只列出固定功能；图层键只在 `BZS` 面板设置，避免两处各存一份而不同步。

新增图层时还应同步维护 `Features/Drafting/Services/DraftingLayerRoles.cs`：
登记分组与**使用方**。`DraftingLayerRolesTests` 会断言每个图层都有使用方，
没有接任何功能的“装饰图层”会让测试失败。

启动器把以下内容作为一个完整产品安装：

- 主插件（批量发布、图框、目录、属性、制图、门窗、房间）
- PDF 依赖和箭头图块库
- 建筑设计说明助手（支持的 R24/R25）
- 一键楼梯大样（支持的 R24/R25）

启动器内嵌模块统一存放在 `BatchPdfPublisherLauncher/Modules`，建筑说明和楼梯不再散落在不同工程的 `Payload` 目录。

对于 R24/R25，任何必需组件缺失都会停止安装并明确提示缺失模块，不再静默跳过。
