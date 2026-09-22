# 调研：商业 CAD/BIM 软件如何实现「2D 建筑平面图 → 3D 模型 → 立面图/剖面图投影」

> 调研范围：天正建筑 T20/T30、Autodesk AutoCAD Architecture、BricsCAD BIM、Revit/ArchiCAD、中文生态其它参照（广联达/中望/浩辰/斯维尔等）。
> 用途：作为《建筑模型与立面剖面生成-开发计划》的前置调研存档。
> 说明：事实优先、逐条给出可点击 URL；无法取得一手证据的点明确标注「未找到确证」，未编造 URL。

---

## 核心结论

1. **最关键机制发现**：天正官方教材明确写出这条链路的本质——*「立剖面表现的是建筑三维模型的一个投影视图……天正立面图形是通过平面图构件中的三维信息进行消隐获得的纯粹二维图形」*。流程 = 楼层表（层高/文件）→ 三维组合（按层高叠各层 DWG）→ `GJLM`/`GJPM` 消隐投影出二维。也就是说，天正走的就是「2D 带高度参数对象 → 拼 3D → 消隐投影回 2D」，与我们要做的路线同构。
2. **硬结论一**：天正强制要求「必须是天正构件（墙/柱/楼梯/阳台/坡道/屋顶），纯 CAD 画的图不能生成立面」——自研插件必须先把「带高度参数的墙/门窗/楼梯对象模型」建起来，不能指望解析任意多段线。
3. **硬结论二**：天正、ACA、BricsCAD 三家**都不承诺全自动**：ACA 立面生成后仍需「更新/编辑线条」（对象挂住模型、可更新）；BricsCAD `BIMIFY` 是先有 3D 实体再反向分类、最后才「创建立面视图 + 每层平面剖切」；用户社区反复说「不可能全部都可以生成，还需要人为修改」。产品定位应是「高覆盖率 + 可编辑的投影结果」，而非 100% 自动。
4. **硬结论三**：可直接照搬的工程骨架（天正已验证 20 年）——分离「楼层/标高表」作为唯一竖向数据源，立面/剖面只做消隐投影 + 二维标注叠加，门窗立面参数来自已有门窗表/门窗立面库。天正在立面里保留门窗、阳台为自定义对象（块 + 参数），其余全是基本 AutoCAD 实体，这一点与我们的门窗表复用诉求完全一致。

---

## 1. 天正建筑 T20 / T30

### 1.1 墙体/门窗/柱子本身就是带三维参数的天正自定义对象

- **结论**：立剖面**表现的是建筑三维模型的投影视图**，且立剖面图形是「通过平面图构件中的三维信息进行消隐获得的纯粹二维图形」；除符号/尺寸标注对象以及门窗阳台图块是「天正自定义对象」外，其余图形构成元素都是 AutoCAD 基本对象。
- **出处**：
  - <https://chaoshi.zjtcn.com/classifydetails/1286714.html>（第 13 章 天正建筑绘制立面图）
  - <https://chaoshi.zjtcn.com/classifydetails/1286742.html>（第 14 章 天正建筑绘制剖面图）
- **对我们的启示**：我们已有图层标准与墙/门窗对象，可以把「三维信息」直接挂在现有对象上（高度、底标高、厚度、洞口），无需另建一套建模数据结构。代价：需要给现有对象补一层「三维参数 + 生成体」的求值层。

### 1.2 三维组合 = 按层高把各层平面叠起来

- **结论**：流程为「工程管理 → 新建工程 → 楼层表（填各层『层高』『文件』）→ 三维组合建筑模型 → 输入文件名保存 → 命令行 `3DO` 进入受约束动态观察查看」。即：三维组合确实是**把各层平面按层高叠合**生成的独立 DWG。
- **出处**：
  - <https://www.hxsd.tv/wenda/299/>（天正建筑三维组合怎么做，含截图步骤）
- **对我们的启示**：可照搬「楼层表 + 叠合」模型；楼层表即我们的唯一竖向数据源（层高、底标高、对应图纸文件）。代价小，主要是 UI 与数据校验（层高/标高冲突提示、轴网对位检查）。

### 1.3 立面的生成方式：从三维组合消隐投影，不是从二维另算

- **结论**：`GJLM`（生成立面）需先画立面线/选定方向；`GJPM`（生成剖面）需先在图上画出剖切线（断面线）；两者都依赖楼层表数据。标准作图步骤为「创建楼层表 → 生成立面/剖面图 → 修改深化 → 标注 → 填充」。若无工程或未建楼层表，命令会弹出警告要求先建工程与楼层表。
- **出处**：
  - <https://yunzhi.zjtcn.com/3683179.html>（天正建筑如何用平面生成立面和剖面：`GJLM`、`GJPM` 命令说明）
  - <https://yunzhi.zjtcn.com/11023174.html>（用天正建筑软件怎样自动生成建筑立面：按【工程管理】中的数据库楼层表格数据一次生成多层立面；工程为空时弹出警告对话框）
  - <https://yunzhi.zjtcn.com/2481.html>（在天正建筑里怎样生成立面图剖面图：立、剖图都依靠将平面图导入新建的工程文件、建立关系后才生成）
  - <https://www.zjtcn.com/zhishi/tzjz2017wjbt>（天正建筑 2017 文件布图：列出生成立面/剖面的完整步骤与第 13/14 章 PDF）
- **对我们的启示**：立面/剖面应当**必须有「工程/楼层上下文」才能生成**，这既是天正的约束也是必要前提；我们的插件应把「未建立模型」作为一等错误状态并给出引导。

### 1.4 硬约束：必须使用天正构件，纯 CAD 平面图无法生成立面

- **结论**：*「首先必须使用天正画的平面，其中各种构建必须是天正的构件——墙、柱、楼梯、阳台、坡道、屋顶等等，普通 cad 画的图是不行的。然后进行楼层组装，最后用立面和剖面里面的建筑立面和建筑剖面命令。」* 另有明确回答：*「用 cad 画的平面图在天正中无法生成立面，目前也没有什么工具插件可以生成立面图。」*
- **出处**：
  - <https://yunzhi.zjtcn.com/39810628.html>（天正建筑 cad 如何把平面改为立面）
  - <https://yunzhi.zjtcn.com/4605098.html>（用 cad 画的平面图怎么用天正生成立面？）
- **对我们的启示**：这是最重要的产品边界结论——**「识别任意二维图」不是可行主线**，「让用户在自己的图层标准下把对象补齐/校正为我们认可的建筑构件」才是主线。代价：需要一套「对象识别 + 人工确认」的批量转换流程与检查器。

### 1.5 天正的立面/剖面输出形态与可编辑性

- **结论**：立面/剖面输出为「纯二维图形 + 天正自定义对象（门窗阳台图块、尺寸标注/符号）」的混合体；立面标注对象是整体标注对象，需要「分解（爆炸）」才能单独修改某个标注。用户经验评价偏负面：*「天正的剖面和立面自动生成不好用，一般都是画好平面再画立面剖面的」*。
- **出处**：
  - <https://yunzhi.zjtcn.com/4864655.html>（天正建筑 绘制立面图的问题：标注是整体标注，需分解才能改）
  - <https://yunzhi.zjtcn.com/3020434.html>（关于天正建筑立面图与平面图：「天正的剖面和立面自动生成不好用，一般都是画好平面再画立面剖面的」）
  - <https://yunzhi.zjtcn.com/2171490.html>（天正建筑画立面图的条件：有用户建议立面不要用天正画）
- **对我们的启示**：一是**保留自定义对象形态**（不要全炸成线），二是**必须提供「投影结果的局部编辑/炸开」出口**，三是用户对自动结果的心理预期需要靠「可编辑 + 高覆盖率」而不是「全自动」来满足。

### 1.6 官方产品页与官方帮助

- **结论**：天正官网存在 T30 天正建筑软件产品页（可确认产品在售与定位），但**未获取到天正官方在线命令手册中「三维组合/立面/剖面」的独立条目页**（官网路径返回 404），本章事实主要依据官方教材/出版社教材与用户文档。
- **出处**：
  - <http://www.tangent.com.cn/cpzhongxin/jianzhu/881.html>（T30 天正建筑软件产品页，抓取返回 404，仅作产品存在性线索）
  - <https://or.tangent.com.cn/tztz30jz>（天正软件 T30 天正建筑软件推广页）
  - <https://help.thcad.cn/pages/viewpage.action?pageId=2328635>（天正系帮助中心索引页，未能定位到三维组合/立面命令条目，**未找到确证**）
- **对我们的启示**：无法从官方在线手册拿到消隐算法或数据结构细节；**天正的实现细节属于未公开信息**，我们只能对齐其「行为语义」而不能对齐其「内部算法」。

---

## 2. Autodesk AutoCAD Architecture (ACA)

### 2.1 AEC 对象本身携带三维信息

- **结论**：Wall/Door/Window/Slab/Stair 等 AEC 对象在模型空间即为三维表示；门窗通过「锚定（Anchor）」挂到墙上并随墙自动调整开洞位置。
- **出处**：
  - <https://help.autodesk.com/view/ARCHDESK/2022/ENU/?guid=GUID-DFDD921A-819E-45C3-8556-CFC303CF00C4>（To Anchor an Object to a Wall）
  - <https://help.autodesk.com/view/ARCHDESK/2025/FRA/?guid=GUID-59EA4F84-B135-4CAA-9EC2-2ADFF7687777>（À propos des ouvertures：关于洞口/开口）
  - <http://academics.triton.edu/faculty/fheitzman/Lesson%20on%203D%20in%20AutoCAD%20Architecture%202010.pdf>（ACA 三维教学讲义，说明 AEC 对象即三维模型）
- **对我们的启示**：门窗「锚定到墙 + 随墙开洞」是必须内建的关联关系；我们已有门窗块与门窗表，需要在此之上补「宿主墙 ID + 洞口几何」两个字段，而不是重新定义门窗。代价中等：需要处理门窗跨墙、贴墙偏移、墙厚变化导致的洞口重算。

### 2.2 立面/剖面生成机制：立面线 + 选择集 + 显示集 → 二维剖面/立面对象

- **结论**：工作机制为：①在图形中绘制**立面线**；②选择立面线 → 「生成立面」；③选择输出类型：**二维立面（隐藏线已删除的二维剖面/立面对象）** 或 **三维立面（三维剖面/立面对象）**；④选样式（二维立面用样式控制，三维立面不使用样式）；⑤指定「选择集」与「显示集」（显示集控制立面模式）；⑥指定插入位置生成。生成后**立面被连接至建筑模型，模型修改后可更新立面**。
- **出处**：
  - <https://help.autodesk.com/cloudhelp/2024/CHS/AutoCAD-Architecture/files/GUID-074C9A40-4F35-4638-BBF1-282DC68F53EE.htm>（创建二维或三维立面的步骤，中文）
  - <https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Architecture/files/GUID-76879DE6-874B-4348-8E96-AE1D35786D5E.htm>（About Elevations：2D 立面为隐藏线与重叠线已移除；可由样式与显示特性控制外观；对象被修改后可更新立面）
  - <https://help.autodesk.com/view/ARCHDESK/2024/ENU/?guid=GUID-012EF996-078C-404E-9940-26B56EADF4DA>（To Update a 2D or 3D Elevation）
  - <https://help.autodesk.com/view/ARCHDESK/2025/ENU/?guid=GUID-3FE52AE3-B330-4EE5-9735-996E2A390618>（To Create a 2D or 3D Section）
  - <https://help.autodesk.com/view/ARCHDESK/2018/ENU/?guid=GUID-699D2679-BC8C-4DE5-ADCB-D5554CA1BA33>（To Update a 2D or 3D Section）
  - <https://help.autodesk.com/view/ARCHDESK/2018/ENU/?guid=GUID-074C9A40-4F35-4638-BBF1-282DC68F53EE>（To Create a 2D or 3D Elevation，2018 版）
- **对我们的启示**：这是与我们最贴近的「同生态（AutoCAD 平台）」参照，可直接照搬这套产品语义：
  - **二维剖面/立面共用一个对象类型与样式**（省一个类型系统）；
  - **选择集 + 显示集**分离「哪些构件参与投影」与「投影后怎么显示」；
  - **生成物与模型保持关联 + 显式 Update 命令**。
  - 代价：我们需自建「投影对象 + 关联关系 + 更新（含全局更新）」机制，以及「隐藏线消除」模块。

### 2.3 二维立面的构成与可编辑性（线条细分/合并）

- **结论**：二维立面由样式控制其显示与「图形细分（graphic subdivisions）」；ACA 提供「编辑和合并二维立面中的线条」「全局更新二维立面」等机制。
- **出处**：
  - <https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Architecture/files/GUID-76879DE6-874B-4348-8E96-AE1D35786D5E.htm>（相关概念列表：About 2D Elevation Styles / Editing and Merging Lines in a 2D Elevation / Globally Updating 2D Elevations）
  - <https://www.manualowl.com/m/Autodesk/00128-051462-9310/Manual/369406?page=1862>（Changing the Display of Graphic Subdivisions in a 2D Elevation，AutoCAD Architecture 用户手册页）
  - <https://www.manualowl.com/m/Autodesk/00128-051462-9310/Manual/369406?page=1831>（Globally Updating 2D Sections/Elevations，用户手册页）
- **对我们的启示**：投影后的线条需要「按材质/构件类型分组、可合并、可全局更新」；这依赖我们的图层标准。代价：需要定义「构件类型 → 输出线型/图层」的映射表，并保留分组的句柄关系。

### 2.4 现在还能不能用 / 订阅情况

- **结论**：**Architecture 工具集（原 AutoCAD Architecture）仍包含在 AutoCAD 订阅中**，官方页面明确写「The Architecture toolset is included with AutoCAD 2026」；即它不是被停售的独立产品，而是并入 AutoCAD / AutoCAD Plus 的工具组合。
- **出处**：
  - <https://www.autodesk.com/hk/products/autocad/included-toolsets/autocad-architecture>（Architecture Toolset in Autodesk AutoCAD：included with AutoCAD 2026）
  - <https://www-int.autodesk.com.cn/products/autocad/included-toolsets/autocad-architecture>（Autodesk AutoCAD 中的 Architecture 工具组合，中文）
  - <https://www.autodesk.com/jp/products/autocad-plus/included-toolsets/autocad-architecture>（Architecture 工具集包含在 AutoCAD Plus 2026 中）
  - <https://www.autodesk.com/latam/products/autocad/included-toolsets/autocad-architecture.takeover>
  - <https://damassets.autodesk.net/content/dam/autodesk/draftr/6974/truth-about-toolsets-single-page-final.pdf>（Autodesk 关于专业工具组合的官方说明 PDF）
- **对我们的启示**：ACA 仍是同平台上的直接竞品/参照，功能语义可对齐；**不必担心平台消失**，但我们无法调用 AEC 内核，只能对齐行为。

### 2.5 未核实项

- **`AECSECOND` 命令、Model Documentation、`VIEWBASE` / `VIEWSECTION` 的官方说明页：未找到确证（未取得可引用的官方页面）。** 本章不对这组命令的实现机制下结论；已知事实仅限于「ACA 有二维/三维剖面与立面对象并可更新」。
- 检索中出现的 <https://help.autodesk.com/view/ARCHDESK/2025/CHT/?guid=GUID-3FE52AE3-B330-4EE5-9735-996E2A390618> 抓取后正文为空（仅剩 Help 壳），不足以作为证据，故不作为引用依据。

---

## 3. BricsCAD BIM

### 3.1 BIMIFY 的输入是 3D 实体，输出是分类 + 空间归属 + 视图

- **结论**（官方帮助原文要点）：*「BIMIFY analyzes a model and automatically classifies **3D solids** to building elements and assigns spatial locations, spaces, buildings, and stories. It also detects and classifies the external and internal walls.」* 可自动判别模型属于建筑/结构/MEP 专业；若柱/梁/杆件的截面与型材库匹配则写入元数据，否则在库中新建。选项包括：
  - **Auto-classification**：自动分类 Solids 与 Block References；
  - **Assign structural/MEP Profiles**：写入型材；
  - **Assign spatial locations**：分配建筑/楼层、探测空间、识别内外墙；
  - **Create spaces**：探测外墙并把 `Wall Common / is External` 置 On，同时探测空间（`BIMSPACE`）；
  - **Create sections**：**「Creates an elevation view and a plan section per floor.」**
- 执行结果：结构树从「只有 3D solids」变为按 building / story / 构件类型归类；立面视图与每层平面剖切**最后创建**。
- **出处**：
  - <https://help.bricsys.com/en-us/document/bricscad-bim/design-assistance/using-bimify>（Using Bimify，官方帮助，V26）
  - <https://bricscad.octave.com/blog/how-to-use-bimify-in-bricscad-bim>（How to use BIMIFY in BricsCAD BIM，Bricsys 官方博客）
  - <https://help.bricsys.com/ja-jp/document/bricscad-bim/design-assistance/using-bimify?version=V26&id=165079160222>（日文版同一页面）
- **对我们的启示**：BricsCAD 的产出清单非常值得照抄——**「每层一张平面剖切 + 立面视图」**，且把它作为 `BIMIFY` 的最后一步。我们可以把「生成视图集」做成一次批量命令，便于验收与回归测试。

### 3.2 「2D 线识别成墙」的算法

- **结论**：**未找到确证。** 官方帮助只说明输入是 3D solids 并做分类/归属；**Bricsys 未公开把二维线自动识别为墙体的算法说明**。检索到 BricsCAD BIM V24 的官方宣传标题「Quickest path to 3D from 2D」，但该页面抓取失败，无法作为一手证据。
- **出处**：
  - <https://www.bricscad.sk/zh-cn/blog/bricscad-bim-v24-quickest-path-to-3d-from-2d>（BricsCAD BIM V24: Quickest path to 3D from 2D，**抓取失败，未确证**）
  - <https://help.bricsys.com/en-us/document/bricscad-bim/design-assistance/using-bimify>（可确证部分：输入为 3D solids）
- **对我们的启示**：不要把「自动识别二维线为墙」当作可对标、可复制的成熟技术；它是整个行业里最难、最少公开的一块。我们应选择天正/ACA 式「对象先行」路线。

---

## 4. Revit / ArchiCAD：立面/剖面与三维模型是同一数据的不同视图

### 4.1 Revit：视图范围（View Range）决定「剖到什么、投影什么、超出什么」

- **结论**：**视图范围**是控制对象在视图中可见性与外观的水平平面集合，由「俯视图 / 剖切面 / 仰视图」构成主要范围，外加**视图深度**（主要范围之外、默认与底剪裁平面重合的附加平面）。显示规则：
  - 与**剖切面相交**的图元 → 用该类别的**剖面线宽**绘制；类别无剖面线宽则不可剖切，用投影线宽；
  - **低于剖切面但高于底剪裁平面** → 投影线宽；
  - **低于底剪裁平面且在视图深度内** → `<超出>` 线样式；
  - **高于剖切面且低于顶剪裁平面** → 默认不显示（窗、橱柜、常规模型例外）；
  - 特例：高度小于 6 ft（约 2 m）的墙即使与剖切面相交也不被截断；楼板/结构楼板/楼梯/坡道有额外放宽范围（比主要范围底部低 4 ft，约 1.22 m）。
- **出处**：
  - <https://help.autodesk.com/cloudhelp/2016/CHS/Revit-DocumentPresent/files/GUID-58711292-AB78-4C8F-BAA1-0855DDB518BF.htm>（关于视图范围，中文，含全部显示规则）
  - <https://help.autodesk.com/cloudhelp/2023/ENU/Revit-DocumentPresent/files/GUID-58711292-AB78-4C8F-BAA1-0855DDB518BF.htm>（About the View Range，英文）
  - <https://help.autodesk.com/cloudhelp/2023/CHS/Revit-DocumentPresent/files/GUID-26445846-A258-42D6-9FD3-25200A78BE7E.htm>（视频：控制视图范围）
  - <http://images.autodesk.com/adsk/files/revit_architecture_2011_user_guide_chs.pdf>（Revit Architecture 2011 中文用户手册：视图范围、剖切面、基线等章节，可核）
- **对我们的启示**：这是「一个模型 → 多视图差异化表达」最可移植的规则化方案。我们应把投影出线分为三类并映射到图层标准：**剖到（粗实线）/ 投影可见（细实线）/ 超出与遮挡（虚线）**；并显式提供「视图深度」参数（对剖面尤其重要）。

### 4.2 Revit：剖面视图靠裁剪区域控制宽度与深度

- **结论**：创建剖面视图时 Revit 设定默认视图深度与宽度，用户通过拖拽裁剪区域控制柄精确调整宽度与深度；剖面视图「可剪切模型」，可在平面/剖面/立面/详图视图中绘制。
- **出处**：
  - <https://help.autodesk.com/cloudhelp/2024/CHS/Revit-DocumentPresent/files/GUID-16FF4EBC-66F0-498E-85F6-DA3274608710.htm>（关于剖面视图的宽度和深度，中文）
  - <https://help.autodesk.com/cloudhelp/2024/CHS/Revit-DocumentPresent/files/GUID-D5CF4013-E3B8-4145-97CD-2C61437B2A50.htm>（剖面视图父主题，由上文页面「父主题」链接给出）
- **对我们的启示**：剖面对象的参数至少要有「剖切线位置 + 视向 + 宽度 + 深度（远/近裁剪）」，这也是我们复用已有图层与视图习惯的接口。

### 4.3 Revit：标注/详图与模型的联动方式

- **结论**：Revit 中尺寸标注、文字、详图索引、填充等**作为视图上的独立注释元素存在**，模型变化时视图重算几何、标注由规则或人工维护；剖面/立面视图中可以再画剖面形成「详图视图」，并可「将视图保存到新文件」等（视图是模型的一次求值，不是模型本身的一部分）。
- **出处**：
  - <https://help.autodesk.com/cloudhelp/2016/CHS/Revit-DocumentPresent/files/GUID-58711292-AB78-4C8F-BAA1-0855DDB518BF.htm>（视图范围/线宽/对象样式等注释与显示分离机制）
  - <http://images.autodesk.com/adsk/files/revit_architecture_2011_user_guide_chs.pdf>（Revit Architecture 2011 中文用户手册：详图视图、保存单个视图等章节，可核）
  - <http://images.autodesk.com/adsk/files/revit_structure_2011_user_guide_chs.pdf>（Revit Structure 2011 中文用户手册：剪裁平面与「剪裁时无截面线 / 剪裁时有截面线 / 不剪裁」等远剪裁选项说明，可核）
  - <https://www.autodesk.com.cn/support/technical/article/caas/sfdcarticles/sfdcarticles/CHS/Scope-box-is-greyed-out-in-dependent-elevations-and-section-views-in-Revit.html>（从属立面/剖面视图中范围框灰显——说明视图间存在主从关系）
- **对我们的启示**：**不要试图把标注参数化进模型**。正确做法是「投影对象（可重算）+ 独立标注层（沿用我们现有标注体系）」，这与广联达官方口径一致（见第 5 节）。

### 4.4 ArchiCAD：Elevation 一般「不剖开建筑」，Section 才是剖切

- **结论**：ArchiCAD 有独立的 Elevation 工具：
  - *「Elevations generally do not 'slice through' the structure, but rather create a cross-section view of the structure from a distant point.」*
  - 立面的水平范围**没有 zero depth 选项**；立面标记线与剖面标记线惯用样式不同；
  - **立面线只是屏幕上的标记项（on-screen-only Marker item），不会出现在 Layout 上**（与 Section Line 不同）；
  - 生成流程：在平面图上放置 **source 型立面标记** → 得到 Elevation 视点；视点有 **Model / Drawing 状态**，决定其更新过程；
  - 标记有 **linked 型**（只含参照信息、不创建视点，可放在平面/剖面/立面/内立面/3D Document/详图/Worksheet 窗口）与 **unlinked 型**。
  - 其它方面 Elevation 工具与 Section 工具行为一致；Section/Elevation 视点有水平/垂直范围设置、更新（Rebuild）状态设置、Model Display 面板控制显示。
- **出处**：
  - <https://help.graphisoft.com/AC/23/INT/_AC23_Help/050_ViewsVB/050_ViewsVB-46.htm>（ARCHICAD 23 Help – Elevations）
  - <https://help.graphisoft.com/AC/19/INT/AC19Help/03_2_Views_Virtual_Building/03_2_Views_Virtual_Building-38.htm>（ArchiCAD 19 Help – Sections/Elevations 相关章节）
  - <https://help.graphisoft.com/AC/23/INT/_AC23_Help/050_ViewsVB/050_ViewsVB-35.htm>（Section 视点的水平/垂直范围，由 4.4 页脚链接给出）
  - <https://help.graphisoft.com/AC/23/INT/_AC23_Help/050_ViewsVB/050_ViewsVB-43.htm>（Assign Section Rebuild Status，由 4.4 页脚链接给出）
  - <https://support.graphisoft.com/hc/en-us/articles/33444489218193-Why-are-elevations-sections-not-created-when-placing-elevation-section-markers>（官方支持：为什么放置标记后没有创建立面/剖面——印证 Marker 与 Viewpoint 分离）
- **对我们的启示**：**「立面 ≠ 剖切」这一区分很重要**：立面是从远处看的整体投影（无剖切面），剖面才有剖切面。我们的输出应分成两个命令/两种对象，但共用同一套投影内核与显示规则。另外，「标记（Marker）与视点（Viewpoint）分离、有 Model/Drawing 状态」是可借鉴的更新策略——标记只记住「从哪看、看多宽」，视点内容可重建。

### 4.5 Revit 官方中文帮助的直接定位

- **说明**：Revit 官方帮助为 JS 驱动的动态页面，直接抓取「关于剖面视图」条目经常只返回 Help 壳；上表两条「关于视图范围」「关于剖面视图的宽度和深度」为可直接抓取到正文的中文页面，已足以为证。
- 参考（抓取为空壳，仅作定位线索，**不作为论据**）：<https://help.autodesk.com/view/RVT/2024/CHS/?guid=GUID-4A2B3D5F-6B2E-4F1A-9C3D-8E7F1A2B3C4D>

---

## 5. 中文生态里的其它参照

### 5.1 广联达数维设计（官方口径最明确，也最接近我们的目标形态）

- **结论**（官方原文）：*「其推出的二三维融合设计的工作模式，以三维设计为主，充分发挥三维多视图联动的优势，保证图模一致性的同时，大幅提升设计调改、图纸绘制的整体效率；而对于附属元素和注释，则可进行二维辅助设计，遵循传统设计习惯的同时，使积累的设计资源得以复用，保证最后输出的图纸符合国家及行业标准。」* 产品定位基于自主图形平台、统一数据标准、构件级数据驱动，并强调设计-算量/设计-施工一体化。
- **出处**：
  - <https://www.glodon.com/news/1146.html>（广联达数维设计产品集重磅发布，含「二三维融合设计」原文）
  - <https://www.glodon.com/product/413.html>（数维建筑设计产品页）
  - <https://www.glodon.com/product/411.html>（BIM 设计协同平台产品页）
- **对我们的启示**：这就是我们要走的路线，而且官方替我们把边界说清楚了：
  - **主体构件走三维、注释与附属元素走二维**；
  - 「使积累的设计资源得以复用」= 复用我们已有的图层标准、门窗表/门窗立面参数、楼梯大样参数，**这正是我们的核心卖点**；
  - 「最后输出的图纸符合国家及行业标准」= 出图必须走我们已有的标注/图框/图层体系，而不是 BIM 软件自带样式。

### 5.2 中望 ZWCAD 建筑

- **结论**：官方教程确认中望建筑具备「**自动创建立剖面**」以提高绘图效率的功能。
- **出处**：
  - <https://www.zwsoft.cn/support/68-2154.html>（中望建筑 2017：自动创建立剖面提高绘图效率）
  - <https://www.zwsoft.cn/story/66-936.html>（中望建筑 CAD 教育版页面）
- **对我们的启示**：国产同生态产品同样把「自动创建立剖面」当作卖点，功能名称与我们的目标完全一致；说明这个功能在国产 CAD 插件市场是被验证的需求。

### 5.3 浩辰 GstarCAD 建筑

- **结论**：官方帮助手册可核到建筑专业的楼层/标准层与构件、图元剪裁等命令体系（如「以选定的矩形窗口、封闭曲线或图块边界作参考，对平面图内的浩辰图块和 CAD 二维图元进行剪裁删除」），并含「单击行首对应的标准层楼层框」等楼层组织方式——与天正的楼层表/标准层思路同源。
- **出处**：
  - <https://www.gstarcad.com/api/downloadFile/?fileUrl=https://static.gstarcad.com/attached/pdf/20200114/0a4e2df0-c795-44fc-8e2e-e0ce1e5682b7.pdf&title=%e6%b5%a9%e8%be%b0CAD%e5%bb%ba%e7%ad%912020%e5%b8%ae%e5%8a%a9%e6%89%8b%e5%86%8c>（浩辰 CAD 建筑 2020 帮助手册，官方 PDF）
  - <https://www.gstarcad.com/pdf-viewer/20240402/dab90d04-523b-4b90-a6b2-224c1662cb13/>（浩辰建筑帮助手册在线版，含标准层楼层框说明）
  - <https://www.gstarcad.com/api/downloadFile?fileUrl=https%3A%2F%2Fstatic.gstarcad.com%2Fattached%2Fpdf%2F20171022%2Fe360d38e-1990-47ac-86f7-ebe131833820.pdf&title=%E6%B5%A9%E8%BE%B0CAD%E5%BB%BA%E7%AD%912017%E5%B8%AE%E5%8A%A9%E6%89%8B%E5%86%8C>（浩辰 CAD 建筑 2017 帮助手册，官方 PDF）
  - <https://www.gstarcad.com/cmsDetail/2241/>（浩辰 CAD 绘图功能介绍）
- **对我们的启示**：楼层表/标准层是国产二维建筑插件的通用基础设施，我们的「楼层/标高表」设计应与之保持概念一致，降低用户迁移成本。

### 5.4 斯维尔（Thsvar）

- **结论**：官网存在「斯维尔建模快手」等面向快速建模的产品入口，与「二维算量/建模」相关；但**未取得可引用的一手技术说明页正文**（产品页正文抓取为空）。
- **出处**：
  - <https://www.thsware.com/a2020thswarejm/index.html>（斯维尔建模快手，页面正文抓取为空，仅确认产品存在）
  - <https://www.thsware.com/FrontEndPlugin/Product/Detail?id=0A770712-3F99-4253-AA0E-1B5090BBB116>（斯维尔产品详情页）
- **对我们的启示**：三维算量系的共同点是「先识别图纸 → 建算量模型」，与本项目「生成建筑模型用于出图」目标不同（他们不主要解决立面/剖面出图）。**定位上不必跟随算量路线。**

### 5.5 酷家乐 / 三维家（户型图 → 3D）

- **结论**：酷家乐有「临摹图门窗识别算法」等官方技术宣传（微信公众号文章），但原文页触发微信环境验证、**正文未取得，未找到确证**；其帮助中心存在「识别图片」相关条目索引。
- **出处**：
  - <https://mp.weixin.qq.com/s/2IkNg3BczlMPQwV9DSyaOw>（酷家乐：临摹图门窗识别算法重磅上线——**需验证码，正文未确证**）
  - <https://www.kujiale.cn/hc/article/search?query=%E8%AF%86%E5%88%AB%E5%9B%BE%E7%89%87&ownerBranchId=3FO4K4VYB76J&page=1>（酷家乐帮助中心「识别图片」检索页）
  - <https://baike.baidu.com/item/%E9%85%B7%E5%AE%B6%E4%B9%90AI/68638441>（酷家乐 AI 百科条目）
- **对我们的启示**：家装系产品做的是「识别位图/临摹图 → 生成 3D 户型」，其识别对象是**用户随手拍/扫描的图**，容错要求与我们（有严格图层标准的工程图）完全不同。**技术不可直接照搬，但「门窗识别」这一具体子问题可以印证：门窗是识别环节中最需要专门算法的部分。**

### 5.6 品茗 HiBIM / 其它

- **结论**：存在品茗 HiBIM（机房预制、机电版等）产品页，定位偏施工深化/预制，**未找到其「平面图 → 三维 → 立面剖面」的官方实现要点说明（未找到确证）**。
- **出处**：
  - <https://pinming.cn/product_cont_2720.html>（品茗 HiBIM 机房预制软件）
  - <https://pmddw.com/app/product/jidian.html>（品茗 HiBIM 机电版）
- **对我们的启示**：施工深化类 BIM 工具的立面/剖面不是主战场，**不构成直接竞品**；可忽略。

### 5.7 本节未核实项汇总

- **未找到确证**：品茗 HiBIM 的「平面→三维→立剖面」实现要点；酷家乐门窗识别算法的技术细节；斯维尔「建模快手」的官方技术说明正文；天正官方在线命令手册中「三维组合/立面/剖面」的独立条目页。

---

## 6. 已知的坑：这类工具公认的难点与开发者复盘

### 6.1 社区实测：墙体交接、竖向构件归属、门窗立面参数是长期痛点

- **结论**（天正用户实测反馈）：
  - 按教学视频一步步操作，生成立面后「天面女儿墙还有阳台栏杆的，对不上号」，「标高跟轴号也没显示」；
  - 论坛回复直接质疑自动化可行性：「还以为 autocad 现在可以只画平面，就可以自动生成立面」「要是有这个东西，就不用你来做设计了」；
  - 另一处问答标题即为「天正 CAD 生成立面时，怎么没有窗户跟门的立面啊？」；
  - 综合问答的结论是「不可能全部都可以生成，还需要人为修改」。
- **出处**：
  - <https://bbs.co188.com/thread-2304684-1-1.html>（土木在线：刚学天正，关于立面生成有大大的问题——女儿墙/阳台栏杆对不上、标高轴号不显示，含「以为可以只画平面就自动生成立面」的回复）
  - <https://m.3d66.com/answers/question_505384.html>（天正 CAD 生成立面时，怎么没有窗户跟门的立面啊？）
  - <https://www.zjtcn.com/zhishi/tianzhenglimian>（天正立面知识聚合页，含「天正 CAD 平面图生成立面图出现了问题——不可能全部都可以生成，还需要人为修改」「用 cad 画的平面图在天正中无法生成立面，目前也没有什么工具插件可以生成立面图」等多条实测问答）
- **对我们的启示**：这些现象背后是三类工程问题：
  1. **墙接头清理（T 形/L 形/十字）与厚度归并**——女儿墙、栏杆对不上通常源于构件未参与/未按层高归位；
  2. **竖向构件归属与标高传递**——标高轴线不显示 = 投影对象与标注/轴网体系没打通；
  3. **门窗立面参数缺失**——门窗没立面参数时投影必然空缺，**这正好是我们已有的资产（门窗表/门窗立面参数），是我们的差异化机会**。

### 6.2 学术侧：为什么「2D 图纸 → 3D 模型」难以 100% 自动

- **结论**：Yin、Wonka、Razdan 的综述 *Generating 3D Building Models from Architectural Drawings: A Survey*（IEEE Computer Graphics and Applications, 2009）系统梳理了从建筑图纸重建三维模型的输入歧义与失败模式，是该方向被广泛引用的综述性文献。
- **出处**：
  - <https://doi.org/10.1109/MCG.2009.9>（DOI 正式入口）
  - <https://scholar.archive.org/work/dlmikhm6c5bzroonoprpf5bg7y>（存档页，含 2009 年原 PDF 的归档副本链接）
- **对我们的启示**：学术结论与工程实践一致——**输入歧义不可消除，必须有人工干预闭环**。我们的产品设计应把「自动 + 确认 + 修正」作为一等流程。

### 6.3 国内专利路线：图层筛选 + 多边形融合 + 缝隙修补

- **结论**：CN113642065A「一种基于 DXF 矢量平面图的室内半自动制图与建模方法」的技术路线为：**首先人工完成图层的筛除工作**，然后基于仅包含要素几何信息的数据，**利用三角剖分方法并设计一定的合并规则完成多边形的融合和缝隙线的修补**。——这正是「墙体交接/缝隙清理」的工程化解法（半自动 + 几何修补规则）。
- **出处**：
  - <http://patentimages.storage.googleapis.com/23/c5/ad/092f82e733f885/CN113642065A.pdf>（专利公开文本 PDF）
- **对我们的启示**：**「人工筛图层」是行业公认的必要前置**（我们已有图层标准，天然占优）；几何层面用「多边形融合 + 缝隙修补」而不是逐线求交，工程量更可控。

### 6.4 其它可参考的开发侧素材

- **结论**：存在与 DWG 清理相关的开源/工具型项目与国内期刊对「BIM 墙体轮廓提取及三维重建」的研究（如自适应分块的墙体轮廓提取），可作算法思路参考；但这些不是「2D→3D 全流程」的完整方案。
- **出处**：
  - <https://github.com/yanranyinghua-cloud/autocad-dwg-cleanup-tools>（AutoCAD DWG 清理工具，开源参考）
  - <http://fcst.ceaj.org/EN/abstract/abstract1484.shtml>（Extracting Contour of BIM Wall Based on Adaptive Block and 3D Reconstruction Research）
  - <http://dianda.cqvip.com/Qikan/Article/Detail?id=7000419572>（自适应分块的 BIM 墙体轮廓提取及三维重建研究，被引 12）
- **对我们的启示**：墙体轮廓提取值得作为**独立可测试模块**先做（输入：图层过滤后的线段集合；输出：带厚度的墙体多边形），它是整条链路的最高风险点。

### 6.5 本节未核实项

- **未找到确证**：针对「T 形/L 形/十字墙接头清理」的商业软件官方算法说明；天正/ACA 官方对其接头清理规则的公开文档；任何商业产品声称「100% 自动」的官方表述。

---

## 对自研插件的可借鉴结论

1. 走「**对象自带高度参数 → 楼层表叠合 → 消隐投影**」的天正式路线，不要走「解析任意多段线猜墙」的 BricsCAD 式路线（后者官方亦未公开算法，且其 `BIMIFY` 输入本就是 3D 实体）。
2. 把**楼层/标高表**做成唯一竖向数据源，立面/剖面只读它；这是我们复用已有图层标准与层高信息的最短路径。
3. 立面/剖面输出定位为「**可重算的投影对象**」，保留显式的「更新」命令（对齐 ACA 的 Update、ArchiCAD 的 Model/Drawing 状态），而不是一次性炸开成散线。
4. 投影结果按 Revit 视图范围思路分层出线：**剖到（粗线）/ 投影可见（细线）/ 超出与遮挡（虚线）**，三类线型映射到我们现有图层标准，直接产出符合制图规范的图。
5. **标注与详图不参数化**：继续用现有标注体系叠加在投影结果上（广联达「注释走二维辅助设计」的官方路线），避免把标注绑进模型导致成本失控。
6. 门窗立面**复用已有门窗表/门窗立面参数库**，投影时以「块 + 参数」形式放置为自定义对象（天正即如此），而不是炸成线条。
7. 必须提供**人工干预入口**：图层筛选/确认、接头检查器、参数补录面板——所有商业产品都留了这个口子（专利 CN113642065A 甚至把「人工筛图层」写进方法第一步）。
8. 墙接头清理（T/L/十字）与缝隙修补单独立项，参考「三角剖分 + 合并规则 + 缝隙修补」的专利思路，配单元测试语料。
9. 楼梯/坡道/屋顶**第一期不要追求自动**，先做参数化构件 + 手工指定；天正与 ACA 也都要求这些必须是各自的自定义构件。
10. 产出物固定为「**每层一张平面剖切 + 四面立面视图**」（BricsCAD BIMIFY 的产出清单），便于验收与回归测试，避免范围蔓延到「全自动出全套施工图」。

---

## 附：主要出处索引（按节）

| 节 | 关键出处 |
| --- | --- |
| 1 天正 | [立面消隐原理](https://chaoshi.zjtcn.com/classifydetails/1286714.html) · [剖面消隐原理](https://chaoshi.zjtcn.com/classifydetails/1286742.html) · [三维组合步骤](https://www.hxsd.tv/wenda/299/) · [GJLM/GJPM](https://yunzhi.zjtcn.com/3683179.html) · [必须用天正构件](https://yunzhi.zjtcn.com/39810628.html) · [纯 CAD 平面无法生成立面](https://yunzhi.zjtcn.com/4605098.html) · [立面不好用的实测评价](https://yunzhi.zjtcn.com/3020434.html) |
| 2 ACA | [创建二维/三维立面（中文）](https://help.autodesk.com/cloudhelp/2024/CHS/AutoCAD-Architecture/files/GUID-074C9A40-4F35-4638-BBF1-282DC68F53EE.htm) · [About Elevations](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-Architecture/files/GUID-76879DE6-874B-4348-8E96-AE1D35786D5E.htm) · [更新立面](https://help.autodesk.com/view/ARCHDESK/2024/ENU/?guid=GUID-012EF996-078C-404E-9940-26B56EADF4DA) · [工具集含于 AutoCAD 2026](https://www.autodesk.com/hk/products/autocad/included-toolsets/autocad-architecture) · [锚定到墙](https://help.autodesk.com/view/ARCHDESK/2022/ENU/?guid=GUID-DFDD921A-819E-45C3-8556-CFC303CF00C4) |
| 3 BricsCAD | [Using Bimify（官方）](https://help.bricsys.com/en-us/document/bricscad-bim/design-assistance/using-bimify) · [How to use BIMIFY（官方博客）](https://bricscad.octave.com/blog/how-to-use-bimify-in-bricscad-bim) |
| 4 Revit/ArchiCAD | [关于视图范围（中文）](https://help.autodesk.com/cloudhelp/2016/CHS/Revit-DocumentPresent/files/GUID-58711292-AB78-4C8F-BAA1-0855DDB518BF.htm) · [剖面视图的宽度和深度](https://help.autodesk.com/cloudhelp/2024/CHS/Revit-DocumentPresent/files/GUID-16FF4EBC-66F0-498E-85F6-DA3274608710.htm) · [ARCHICAD Elevations](https://help.graphisoft.com/AC/23/INT/_AC23_Help/050_ViewsVB/050_ViewsVB-46.htm) · [Graphisoft 支持：标记与视点](https://support.graphisoft.com/hc/en-us/articles/33444489218193-Why-are-elevations-sections-not-created-when-placing-elevation-section-markers) |
| 5 中文生态 | [广联达数维设计](https://www.glodon.com/news/1146.html) · [中望自动创建立剖面](https://www.zwsoft.cn/support/68-2154.html) · [浩辰建筑帮助手册](https://www.gstarcad.com/api/downloadFile/?fileUrl=https://static.gstarcad.com/attached/pdf/20200114/0a4e2df0-c795-44fc-8e2e-e0ce1e5682b7.pdf&title=%e6%b5%a9%e8%be%b0CAD%e5%bb%ba%e7%ad%912020%e5%b8%ae%e5%8a%a9%e6%89%8b%e5%86%8c) · [斯维尔建模快手](https://www.thsware.com/a2020thswarejm/index.html) · [酷家乐门窗识别（未确证）](https://mp.weixin.qq.com/s/2IkNg3BczlMPQwV9DSyaOw) |
| 6 已知的坑 | [土木在线实测帖](https://bbs.co188.com/thread-2304684-1-1.html) · [天正立面问答聚合](https://www.zjtcn.com/zhishi/tianzhenglimian) · [2D→3D 综述 DOI](https://doi.org/10.1109/MCG.2009.9) · [专利 CN113642065A](http://patentimages.storage.googleapis.com/23/c5/ad/092f82e733f885/CN113642065A.pdf) · [DWG 清理工具](https://github.com/yanranyinghua-cloud/autocad-dwg-cleanup-tools) |
