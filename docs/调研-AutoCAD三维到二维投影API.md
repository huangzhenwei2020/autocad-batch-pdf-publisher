# 调研：AutoCAD .NET「三维 → 二维立面/剖面」API 查证报告

> 目标场景：C#/.NET 建筑插件（`net48` → R24 / `net8.0` → R25，引用 `acmgd` / `acdbmgd` / `accoremgd`），把用户画的二维平面图生成为三维模型（墙 / 门窗洞口 / 楼板 / 柱 / 楼梯），再把模型正交投影成二维**立面图 / 剖面图**写回同一张 DWG，要求线稿分层、线型线宽符合制图标准、剖面能填图案。
>
> 说明：本文所有 URL 均已实测可访问（HTTP 200）。Autodesk 新版帮助是 JS 单页应用，`help.autodesk.com/view/OARX/...?guid=...` 形式可直接点开；同时给出同内容的 `cloudhelp` 纯 HTML 直链便于抓取。**未确证的内容一律标注"未找到确证"，不做推测性断言。**

---

## 核心结论

1. **FLATSHOT 只能脚本化**：输出是匿名块（非散线），.NET 无等价 API，`SendStringToExecute` 是异步的（当前 .NET 命令结束后才执行），同步需 COM `SendCommand` 或 P/Invoke `acedCommand`/`acedCmd`/`acedInvoke`。
2. **VIEWBASE / VIEWSECTION / VIEWDETAIL 完全不可编程**：官方只有命令清单，没有公开 .NET/ActiveX 创建 API；输出在布局（图纸空间）；依赖 Inventor Interoperability 组件，且视图与 AutoCAD 版本强绑定。
3. **SECTIONPLANE / `Section` 类是唯一可编程的"实体 → 二维线稿"通道**：托管类型为 `Section`、`SectionSettings`、`SectionManager`；核心方法是 `Section.GenerateSectionGeometry`，返回 5 组输出实体。
4. **托管枚举值**：`SectionType { LiveSection=1, Section2d=2, Section3d=4 }`、`SectionState { Plane=1, Boundary=2, Volume=4 }`；`k2dSection/k3dSection/kLiveSection` 是 C++/ActiveX 侧名字，**托管侧没有 `k2dElevation`（未找到确证）**。
5. **只有 `3dSolid` / `Surface` / `Body` / `Region` 能被剖切**；Mesh、AEC 墙、自定义实体都不行。
6. **立面/剖面靠 Section 状态与法向拼出来**：`Section.State = SectionState.Boundary` + 法向水平 = 立面；法向水平 + jogs = 剖面（没有"立面专用"枚举）。
7. **三维实体 API 完备但布尔是风险点**：`Solid3d.CreateBox/CreateExtrudedSolid/CreateSweptSolid/CreateFrom`、`Extrude(Region,height,taper)`、`BooleanOperation(BooleanOperationType, Solid3d)`；开洞走"挤出墙 → 挤出洞口 → `BoolSubtract`"，但官方记载布尔创建的实体在部分机器上会崩溃，另有 84015 建模错误。
8. **`Solid3d.GetSection(Plane) → Region` 是轻量替代口子**：只求交面区域，不依赖 Section 实体，适合快速取得剖切轮廓。
9. **填充走 `Hatch` + `AppendLoop` + `SetHatchPattern`**，自定义 PAT 用 `HatchPatternType.CustomDefined`；多环必须"外环在前、内环完全包含且互不相交"。
10. **性能**：分批事务提交 + 块化（`BlockTableRecord`）+ 选择性 `WblockCloneObjects`；`GenerateSectionGeometry` / FLATSHOT 均支持写入新块或外部文件（`kDestinationNewBlock` / `kDestinationFile`）。
11. **官方样例不覆盖本题**：ADN GitHub 组织与 ObjectARX SDK samples 中**未找到确证**存在"三维→二维工程图"示例；自研投影 + 隐藏线消除**没有可直接引用的 .NET 现成库**。
12. **总体判断**：主链路用原生 `Section`（可编程、可控图层/线型/填充），FLATSHOT 只作快照补充，隐藏线消除若必须完整可控则需自研或走 C++ ObjectARX。

---

## 1. 原生"三维 → 二维"能力

### 1.1 FLATSHOT（平面摄影）

**有没有 .NET API 等价物？没有。**
`Autodesk.AutoCAD.DatabaseServices` 命名空间内**不存在**任何 Flatshot 相关类型或方法（类名检索无结果）。该功能是命令独占（command-only）能力。

**生成的是块还是散线？**
是**匿名块**：命令会创建一个 BlockTableRecord 并插入 BlockReference，块内实体为平面化后的直线/圆弧等。因此"图层/线型是否保留"取决于块内实体自身携带的图形属性；FLATSHOT 对话框（或 `-FLATSHOT` 命令行参数）可分别指定**前景线/隐藏线**的颜色与线型，插入后再统一刷图层是常规做法。

**怎么编程调用？**
只能 `Document.SendStringToExecute("._flatshot ...")`。官方文档明确说明：`SendStringToExecute` 发送的命令是**异步的，要等当前 .NET 命令结束后才会被调用**。需要同步执行时必须改用：
- COM Automation 的 `SendCommand`（通过 .NET COM Interop）；
- P/Invoke 原生 `acedCommand` / `acedCmd`（用于原生 AutoCAD 命令及 ObjectARX/.NET 定义的命令）；
- P/Invoke `acedInvoke`（用于 AutoLISP 定义的命令）。

出处：
- [Access the Command Line (.NET)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-F4A36181-39FB-4923-A2AF-3333945DB289.htm)（`SendStringToExecute` 异步性说明 + 同步替代方案）
- [论坛：How to call FLATSHOT entirely from .NET code?](https://forums.autodesk.com/t5/net/how-to-call-flatshot-entirely-from-net-code/m-p/9944079)
- 旁证（BricsCAD 命令参考，形态一致）：[-FLATSHOT command](https://helpcenter.bricsys.com/en-us/document/command-reference/f/-flatshot-command?version=V26&id=174480370911)、[Flatshot dialog box](https://help.bricsys.com/en-us/document/dialog-boxes/f/flatshot-dialog-box?version=V26&id=165079124957)

**结论：能用，但不可编程控制（只能脚本化且异步），不适合作为插件主链路。**

### 1.2 VIEWBASE / VIEWSECTION / VIEWDETAIL（模型文档 Model Documentation）

**能否用 .NET API 生成？不能。**
官方只提供命令清单，没有公开的创建 API（.NET 与 ActiveX 均无）：
[Commands for Working with Model Documentation Drawing Views](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-DB165B89-5204-48EA-B1DC-454991CB05A4.htm) —— 列出 `VIEWBASE`、`VIEWCOMPONENT`、`VIEWDETAIL`、`VIEWDETAILSTYLE`、`VIEWEDIT`、`VIEWPROJ`、`VIEWSECTION`、`VIEWSECTIONSTYLE`、`VIEWSETPROJ`、`VIEWSKETCHCLOSE`、`VIEWSTD`、`VIEWSYMBOLSKETCH`、`VIEWUPDATE` 及相关系统变量（`ANNOMONITOR`、`CVIEWDETAILSTYLE`、`CVIEWSECTIONSTYLE`、`VIEWSKETCHMODE`、`VIEWUPDATEAUTO` 等）。只能通过 `SendStringToExecute` 驱动对话框式流程，**无法可靠参数化**。

**输出到布局还是模型空间？**
输出位于**布局（图纸空间）**，是"图纸视图"对象体系，不是普通实体。

**依赖什么？**
- 需要 **Inventor Interoperability** 组件（缺组件时打开含 VIEWBASE 视图的图纸会被要求安装）：[Opening a drawing containing views created with VIEWBASE asks to install Inventor Interoperability](https://www.autodesk.com.cn/support/technical/article/caas/sfdcarticles/sfdcarticles/CHS/Opening-a-drawing-containing-views-created-with-VIEWBASE-AutoCAD-asks-to-install-Inventor-Interoperability.html)
- **版本强绑定**，旧版本无法编辑新版创建的视图：[Cannot edit drawing views created in AutoCAD 2016 in older versions of the product](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Cannot-edit-drawing-views-created-in-AutoCAD-2016-in-older-versions-of-the-product.html)

**剖切后能否得到二维线稿？**
人机交互上可以（这就是模型文档的用途），但**插件拿不到这些几何**——没有 API 暴露视图的边线集合。

**结论：不能用（无 API、依赖附加组件、版本绑定重）。**

### 1.3 SECTIONPLANE / LIVESECTION 与 `Section` / `SectionSettings` / `SectionType`

这是本题**唯一可编程**的原生通道，托管类型齐全。

**类与构造**
- [`Autodesk.AutoCAD.DatabaseServices.Section`（: Entity）](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Section.html) —— 包装 `AcDbSection`，表示图纸中的剖切平面实体。
- 构造重载（[Section Constructor](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__OVERLOADED_Section_Autodesk_AutoCAD_DatabaseServices_Section.html)）：`Section()`、`Section(Point3dCollection, Vector3d)`、`Section(Point3dCollection, Vector3d, Vector3d)`。
- [`SectionSettings`（: DBObject）](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SectionSettings.html) —— 包装 `AcDbSectionSettings`，存放剖切几何的生成设置。属性只有 [`CurrentSectionType`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Properties_Autodesk_AutoCAD_DatabaseServices_SectionSettings.html)。
- [`SectionManager`（: DBObject, IEnumerable）](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SectionManager.html) —— 不能直接实例化，用 `Database.GetSectionManager()` 取得；方法 [`GetSection`、`GetUniqueSectionName`、`GetEnumerator`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Methods_Autodesk_AutoCAD_DatabaseServices_SectionManager.html)。

**`Section` 的可用属性**（[Section Properties](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Properties_Autodesk_AutoCAD_DatabaseServices_Section.html)）
`State`、`Settings`、`Vertices`、`NumVertices`、`Normal`、`ViewingDirection`、`VerticalDirection`、`Elevation`、`IsLiveSectionEnabled`、`IsSlice`、`ThicknessDepth`、`Boundary`、`TopPlane`、`BottomPlane`、`HasJogs`、`Name`、`SectionPlaneOffset`、`IndicatorFillColor`、`IndicatorTransparency`。

**`Section` 的可用方法**（[Section Methods](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Methods_Autodesk_AutoCAD_DatabaseServices_Section.html)）
`AddVertex`、`RemoveVertex`、`SetVertex`、`GetVertex`、`GetVertices`、`CreateJog`、`SetHeight`、`Height`、`HitTest`，以及关键的 **`GenerateSectionGeometry`**。

**枚举（重点，会直接用进开发计划）**
- [`SectionType`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SectionType.html)：
  ```csharp
  public enum SectionType { LiveSection = 1, Section2d = 2, Section3d = 4 }
  ```
- [`SectionState`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SectionState.html)：
  ```csharp
  public enum SectionState { Plane = 1, Boundary = 2, Volume = 4 }
  ```
  - `Plane = 1`：平面在各方向无限延伸；
  - `Boundary = 2`：由剖切线 + 两条侧边线 + 后边线围成边界，但上下无限延伸；
  - `Volume = 4`：由前/侧/后/顶/底围成的体。
- **命名纠正**：`kLiveSection / k2dSection / k3dSection` 是 **C++/ActiveX** 枚举名（参见 [AcDbSectionSettings::SectionType](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSectionSettings__SectionType.html)）；**托管侧必须写 `LiveSection / Section2d / Section3d`**。
- **`k2dElevation` 未找到确证**：在托管 `SectionType`、C++ `AcDbSectionSettings::SectionType`、ActiveX `SectionTypeSettings` 的公开枚举中均未出现该名字。**公开枚举里没有"立面专用"类型**，立面效果靠 `Section.State = Boundary` + 法向水平 + 相机方向参数模拟。

**怎么拿到生成的二维几何（最关键的一条）**
[`Section.GenerateSectionGeometry(Entity, out Entity[], out Entity[], out Entity[], out Entity[], out Entity[])`](https://help.autodesk.com/view/OARX/2018/ENU/?guid=OREFNET-Autodesk_AutoCAD_DatabaseServices_Section_GenerateSectionGeometry_Entity_out_Array_out_Array_out_Array_out_Array_out_Array)

托管签名（由官方 URL 的 guid 段与 C++ 同构说明共同确证）：

```csharp
// Autodesk.AutoCAD.DatabaseServices.Section
public void GenerateSectionGeometry(
    Entity     pEnt,
    out Entity[] intBoundaryEnts,     // 剖切交线边界几何
    out Entity[] intFillEnts,         // 剖切交线填充注释几何（剖面填充）
    out Entity[] backgroundEnts,      // 后景几何（被遮挡但仍显示的部分）
    out Entity[] foregroundEnts,      // 前景几何（可见线）
    out Entity[] curveTangencyEnts);  // 曲线相切线
```

5 个输出数组的确切含义（C++ 同构签名逐字对应）：

| 输出数组 | 含义 |
| --- | --- |
| `intBoundaryEnts` | intersection boundary geometry，交线边界几何 |
| `intFillEnts` | intersection fill annotation geometry，交线填充注释几何 |
| `backgroundEnts` | background geometry，背景几何 |
| `foregroundEnts` | foreground geometry，前景几何 |
| `curveTangencyEnts` | curve tangency geometry，曲线相切几何 |

C++ 出处（含返回码与所有权说明）：[`AcDbSection::generateSectionGeometry`](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSection__generateSectionGeometry_AcDbEntity___AcArray_AcDbEntity____AcArray_AcDbEntity____AcArray_AcDbEntity____AcArray_AcDbEntity____AcArray_AcDbEntity____const.html)

> 该页明确两点：
> 1. **可剖切实体只有 `AcDb3dSolid` / `AcDbSurface` / `AcDbBody` / `AcDbRegion`** —— **Mesh、AEC 墙、自定义实体都不在支持列表内**；
> 2. **成功返回 `Acad::eOk`**；调用方对 5 个输出数组中的实体**负全部所有权责任**（要么追加进数据库，要么在使用完毕后删除，否则泄漏）；若平面与实体不相交则返回建模错误。

**制图标准（图层/线型/线宽/填充）怎么预设**
[`SectionSettings` 方法全表](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-__MEMBERTYPE_Methods_Autodesk_AutoCAD_DatabaseServices_SectionSettings.html)：`SetColor`、`SetLayer`、`SetLinetype`、`SetLinetypeScale`、`SetLineWeight`、`SetPlotStyleName`、`SetHatchVisibility`、`SetHatchPatternType`、`SetHatchPatternName`、`SetHatchAngle`、`SetHatchScale`、`SetHatchSpacing`、`SetHiddenLine`、`SetDivisionLines`、`SetEdgeTransparency`、`SetFaceTransparency`、`SetGenerationOptions`、`SetDestinationBlock`、`SetDestinationFile`、`GetSourceObjects`、`Reset`。

这正是"线稿分层 + 线型线宽符合制图标准 + 剖面填充"所需要的全部开关，**可在生成前一次性配置**。

**生成选项与目的地**
[`AcDbSectionSettings::Generation`](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSectionSettings__Generation.html)：
```cpp
enum Generation {
  kSourceAllObjects      = (0x1 << 0),
  kSourceSelectedObjects = (0x1 << 1),
  kDestinationNewBlock     = (0x1 << 4),
  kDestinationReplaceBlock = (0x1 << 5),
  kDestinationFile         = (0x1 << 6)
};
```
另有 `kGenerate2dSection` / `kGenerate3dSection` 属"类型组"。三组各取一个 OR 起来：类型 / 来源 / 目的地。**`kDestinationNewBlock` 与 `kDestinationFile` 是控制主图体积膨胀的官方手段。**

**立面 vs 剖面怎么配（本条会直接用进开发计划）**
- **立面**：`section.State = SectionState.Boundary`，法向取**水平**（`Normal`/`ViewingDirection` 水平，`VerticalDirection` 竖直向上），沿视线方向取"前后边界"作为投影深度范围；得到的是沿该水平视线方向的正交投影线稿。
- **剖面**：同样 `State = Boundary`、法向水平，但通过 `CreateJog` / `AddVertex` / `SetVertex` 构造**带 jogs 的阶梯剖切线**（`HasJogs == true`），并用 `Elevation` / `SectionPlaneOffset` / `ThicknessDepth` 控制切深。
- 关键限制：`GenerateSectionGeometry` 得到的是**该 Section 平面定义的切面/投影几何**，其"完整可见线消除"能力受 Section 自身语义约束，**不是**一个通用的"任意视角完整 HLR"引擎。若需要严格的全模可见线/隐藏线分离，仍需自研 HLR（见第 3 节）。

**相关命令**
`SECTIONPLANE`、`LIVESECTION`（活剖面）与上述托管类型一一对应；活剖面由 `Section.IsLiveSectionEnabled` / `SectionType.LiveSection` 控制。

**结论：能用，且是插件唯一可编程的"实体 → 二维线稿"通道；局限是仅支持 3dSolid / Surface / Body / Region，本质是切面投影而非完整可见线消除引擎。**

---

## 2. 三维实体 API

### 2.1 基元与构造
[`Solid3d` 方法全表](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid3d.html)（: `Entity`，包装 `AcDb3dSolid`，[类页](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid3d.html)）：

- `CreateBox`（质心在 WCS 原点，长宽高轴对齐坐标轴）
- `CreateExtrudedSolid`、`Extrude(Region, double height, double taperAngle)`、`ExtrudeAlongPath`、`ExtrudeFaces`、`ExtrudeFacesAlongPath`
- `CreateSweptSolid`（配合 [`SweepOptions`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SweepOptions.html) + [`SweepOptionsBuilder`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_SweepOptionsBuilder.html) 设置扫掠参数）
- `CreateLoftedSolid`、`CreateRevolvedSolid`、`CreateFrom`、`CreateFrustum`、`CreatePyramid`、`CreateSphere`、`CreateTorus`、`CreateWedge`、`CreateSculptedSolid`
- 局部编辑：`ChamferEdges`、`FilletEdges`、`OffsetBody`、`CleanBody`、`ImprintEntity`、`CheckInterference`、`ConvertToBrepAtSubentPaths`
- 面/边提取：`CopyFace`（平面面 → `Region`）、`CopyEdge`（→ `Line`/`Circle`/`Arc`/`Ellipse` 等）

`Extrude` 的约束（[API 页](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid3d_Extrude_Region_double_double.html)）：
- `taper` 应在 `-π/2 ~ π/2`；`|taper| < 1e-6` 时按 0 处理；
- **锥角非零时，Region 只能含直线、圆、圆弧，且连接处必须相切连续**；
- **Region 不得自相交**；扫掠产生的自相交不会被自动修正。

### 2.2 布尔运算
[`Solid3d.BooleanOperation(BooleanOperationType, Solid3d)`](https://help.autodesk.com/view/OARX/2018/ENU/?guid=OREFNET-Autodesk_AutoCAD_DatabaseServices_Solid3d_BooleanOperation_BooleanOperationType_Solid3d)

```csharp
public enum BooleanOperationType { BoolUnite, BoolIntersect, BoolSubtract }
```
出处：[`BooleanOperationType`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BooleanOperationType.html)

### 2.3 建墙 / 开洞的常规做法
**是**："挤出墙（矩形 Region → `Extrude` 或 `CreateExtrudedSolid`）→ 挤出洞口实体（门窗洞）→ `BooleanOperation(BoolSubtract, openingSolid)`"。洞口实体通常在墙厚方向两侧各多留一点（例如 `1e-5 ~ 1e-3`）以避免**共面/共边**退化。

### 2.4 已知性能与稳定性坑
- **布尔崩溃**：Autodesk 官方博客记载，用 `booleanOper()` 创建的实体在部分 PC 上会导致 AutoCAD 崩溃 —— [Solids created with booleanOper() may crash AutoCAD on some PC's](https://blog.autodesk.io/solids-created-with-booleanoper-may-crash-autocad-on-some-pcs/)
- **建模错误 84015**：修改三维实体时报 "The Boolean operating on solid and/or surface bodies failed. Modeling operation error. Error Code Number is 84015." —— [Autodesk 支持文章](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Modeling-operation-error-Error-Code-Number-is-84015-when-trying-to-modify-3D-solids-in-AutoCAD-Plant-3D.html)
- **REGION 相关**：`Region` 由闭合轮廓 `CreateFromCurves` 生成，自相交/非平面/非流形轮廓会失败；[`AcDbRegion` 类参考](https://docs.dev.graebert.com/html/2026.0.1/frx/d4/d19/class_ac_db_region.html)（第三方兼容实现文档，可作 API 形态旁证）；`Region` 面积属性接口 `AcDbRegion::getAreaProp` 见 [ObjectARX 参考](https://help.autodesk.com/view/OARXMAC/2017/ENU/?guid=OREFMAC-AcDbRegion__getAreaProp_AcGePoint3d__AcGeVector3d__AcGeVector3d__double__double__AcGePoint2d__double_double__double_AcGeVector2d_double_AcGePoint2d__AcGePoint2d__const)。**"REGION 面积上限"这一具体数值未找到确证**（官方文档只描述拓扑合法性要求，未给面积阈值）。
- **建议的工程防御**：一次布尔只作用于两个体；每次布尔后 `catch` 并把失败体降级为多段短墙 / 不做洞；避免大量微小共面；对整栋楼分批（每层、每墙段）执行而非一次性合并。

### 2.5 `Solid3d.GetSection(Plane) → Region`（替代口子）
[`Solid3d.GetSection(Plane plane)`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Solid3d_GetSection_Plane.html)

```csharp
public virtual Region GetSection(Plane plane);
```

创建一个 `Region` 表示平面与实体的交集。**返回 `null` 的情形**：实体没有 ShapeManager 对象、平面与实体不相交、交集不是有效 Region（例如仅点接触、或恰好沿实体的某个边界面相交）。

**适用场景**：只想要"某个剖切面的轮廓"而不需要 Section 实体的完整语义（不需要 background/foreground/相切线分组、不需要 SectionSettings 的图层填充预设）时，这是**更轻、更直接的替代口子**；可逐实体调用后自行合并 `Region` 或提取其边界曲线入库。

**结论：能用（三维实体 API 完备，`GetSection` 提供轻量剖切轮廓）；布尔稳定性需自建降级策略。**

---

## 3. 自己算投影的可行性

### 3.1 官方样例覆盖情况
- ObjectARX SDK 的 samples **按主题分组**，重点集在 `polysamp`；目录说明见 [ObjectARX Directory Tree](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide/files/GUID-4A1AEF78-79CE-4352-9BDC-318E594F00AF.htm)（`samples` 目录"按程序化焦点分组的 ObjectARX 应用示例"）。
- 逐一检查 Autodesk 官方（ADN）GitHub 仓库目录树后，**未找到确证**存在覆盖"三维 → 二维工程图 / 投影 / 隐藏线消除"的示例：
  - [ADN-DevTech/ObjectARXTrainingMaterial](https://github.com/ADN-DevTech/ObjectARXTrainingMaterial)（按年份打包的培训实验 zip）
  - [ADN-DevTech/objectarx-training](https://github.com/ADN-DevTech/objectarx-training)
  - [ADN-DevTech/AutoCADDotnetTrainingMaterial](https://github.com/ADN-DevTech/AutoCADDotnetTrainingMaterial)
  - [ADN-DevTech/AutoCAD-Net-Wizards](https://github.com/ADN-DevTech/AutoCAD-Net-Wizards)
  - [ADN-DevTech/autocad-automation-apps](https://github.com/ADN-DevTech/autocad-automation-apps)（仅 BatchPublish / DumpDwg / TextExtract / XrefTraverser 等应用）
  - [ADN-DevTech/objectarx_sdks](https://github.com/ADN-DevTech/objectarx_sdks)（2000–2012 历史 SDK 归档）

### 3.2 社区算法资源
- 中文论坛讨论"三维实体的投影二维轮廓（算法原理）"：[bbs.mjtd.com 帖](http://bbs.mjtd.com/thread-113655-1-1.html)
- 开源投影管线思路参考（非 AutoCAD、非 .NET）：FreeCAD TechDraw 的投影/消隐实现综述 —— [How FreeCAD Creates 2D Drawings from 3D Models](https://cadshift.com/blog/how-freecad-creates-2d-drawings-from-3d-models/)
- 多边形裁剪基础算法（投影面轮廓布尔/裁剪常用）：Weiler–Atherton 算法说明可参见公开学位论文，例如 [dspace.znu.edu.ua 文档](https://dspace.znu.edu.ua/jspui/bitstream/12345/3988/1/Volkovskiy_D_L.pdf)
- **可直接引用的 .NET HLR 现成库：未找到确证。**

### 3.3 自研正交投影 + 隐藏线消除的最小可行算法链
1. **建体**：把墙/板/柱/楼梯统一为凸体（长方体 / 棱柱 / 圆柱）；
2. **背面剔除**：按视方向 `d`，对面法向 `n`，保留 `n·d < 0` 的面（双面体需同时保留轮廓边）；
3. **投影**：把顶点投影到视平面（正交投影 = 仅取视线垂直的两轴分量），得到 2D 多边形集合；
4. **遮挡判定**：用深度排序（画家算法）或 Z-buffer / 区间扫描，判断每条边被哪些投影面遮挡，切分出可见段与隐藏段；
5. **线段合并**：共线且相邻的可见段合并，减少实体数；
6. **分图层写入**：可见线写 `A-WALL-VIS`（连续粗线），隐藏线写 `A-WALL-HID`（虚线/细线），可再加 `A-WALL-PATT` 承载填充轮廓。DXF/DWG 里"按可见性分层"就是**两套图层 + 不同 Linetype / LineWeight / PlotStyleName**，无需特殊机制。

### 3.4 蓝图空间视口的辅助手段
若只想在**打印**时消隐，可用 `PlotSettings.PlotHidden`（[API 页](https://help.autodesk.com/cloudhelp/2020/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_PlotSettings_PlotHidden.html)）：它控制**图纸空间对象**是否走隐藏线算法；**不影响浮动模型空间视口内的对象**（视口内消隐另有独立开关）。这不能替代几何级别的线稿生成。

**结论：需要自己实现（无官方样例、无成熟可复用的 .NET 库）。**

---

## 4. 填充（剖面图案）

### 4.1 标准做法
1. `new Hatch()`，用 `Hatch.SetDatabaseDefaults()` 归一化属性；
2. 设置图案类型与图案名：[`HatchPatternType`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_HatchPatternType.html)
   - `PreDefined`：从 `acad.pat` / `acadiso.pat` 取图案名；
   - `UserDefined`：用当前线型定义线图案；
   - `CustomDefined`：从其他 PAT 文件取图案名 —— **自定义 PAT 走这一支**；
   参见 [Assign the Hatch Pattern Type and Name (.NET)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-75E9A9E0-338F-4F77-9C3C-0F756AC31BE4.htm)；自定义 PAT 的定义语法见 [About Custom Hatch Patterns and Hatch Pattern Definitions](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-A6F2E6FF-1717-44B6-A476-0CA817ADD77E)（该主题只有 `help.autodesk.com/view/...?guid=` 形式可访问，对应的 `cloudhelp/...files/*.htm` 静态页在 2024/2025/2026 各版本均返回 404，请注意抓取方式）。
3. 追加边界环：[`Hatch.AppendLoop(HatchLoopTypes loopType, ObjectIdCollection dbObjIds)`](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Hatch_AppendLoop_HatchLoopTypes_ObjectIdCollection.html) —— "把新的边界环（路径或多段线）追加到填充实体；**若填充实体是关联的（associative），这些 ObjectId 会被保留**"；
4. `EvaluateHatch()` / `EvaluateHatch(bool)`，再设 `HatchObjectType`、`PatternScale`、`PatternAngle`、`Associative`。

填充创建流程总述：[Create Hatches (.NET)](https://help.autodesk.com/cloudhelp/2016/HUN/AutoCAD-NET/files/GUID-26CEE5F5-F141-4256-B652-859F5D1330B0.htm)（创建后先指定外环、再依次指定内环）。

### 4.2 坑
- **环嵌套规则（硬性）**：来自 [`Hatch` 类说明](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Hatch.html) —— 多个环时，"各环围成的区域必须完全不相交，或一个完全包含另一个"；必须组织成**嵌套结构：外环先构造，随后是其所有内环，按嵌套顺序**；多于一个外环时重复该过程。环必须**简单、闭合、连续**，且起点与终点重合。AutoCAD 对边界**只做有限校验**（为效率考虑），所以插件必须自己保证合法性。
- **内部表示**：环边是 GELIB 2D 几何（`LineSegment2d` / `CircularArc2d` / `EllipticalArc2d` / `NurbCurve2d`），边界是 LWPOLYLINE 时有专门方法构造环。
- **关联填充 `AssociativeHatch`**：可关联的边界实体类型包括 LINE、ARC、CIRCLE、ELLIPSE、SPLINE、POLYLINE、TEXT、MTEXT、ATTDEF、ATTRIB、SHAPE、SOLID、TRACE、TOLERANCE、REGION、VIEWPORT、3D FACE、BLOCK INSERT、XREF、LWPOLYLINE 等（同 `Hatch` 类页）。**关联后边界变更会触发重算，大图上开销明显**；剖面填充属于"生成后基本不变"的内容，建议 **`Associative = false`** 以换取性能与稳定性。
- **面积/密度**：图案比例（`PatternScale`）与角度不设会导致密到卡顿；超大剖面用大比例。**Hatch 的具体面积上限数值未找到确证**（官方文档未给出硬阈值，实际瓶颈是图案渲染行数与环复杂度）。
- **可移植性**：自定义 PAT 必须位于支持文件搜索路径内，否则 `CustomDefined` 找不到图案名。
- **事务**：`Hatch` 及其边界实体必须在**同一事务**内追加到 `BlockTableRecord`，跨事务追加边界会导致关联丢失。

**结论：能用（标准 API 完备），需严格遵守环嵌套规则并警惕关联性开销。**

---

## 5. DWG 存储与性能

### 5.1 事务策略
- 使用 `Database.TransactionManager.StartTransaction()`，**按批次提交**而非"整栋楼一个大事务"；批的粒度建议为"每层 / 每构件类型 / 每 N 个实体"。社区实践与官方论坛讨论：[Managing Transactions for Large Data Sets](https://forums.autodesk.com/t5/net-forum/managing-transactions-for-large-data-sets/m-p/13348687)
- `Database.TransactionManager` 的**事务数量/大小硬性上限未找到确证**（官方未公布阈值）；实际限制来自内存与 Undo 记录膨胀，因此"分批 + 及时 `Commit`/`Dispose`"是通行做法。

### 5.2 块化（强烈建议）
- 把重复构件（标准门窗、柱、楼梯段）写成 `BlockTableRecord` + `BlockReference`，而不是每次展开为独立实体；
- `Section` / FLATSHOT 的原生输出也支持直接落块：`Generation.kDestinationNewBlock`、`kDestinationReplaceBlock`、`kDestinationFile`（见 [Generation 枚举](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSectionSettings__Generation.html)）。

### 5.3 跨图克隆
- `Database.WblockCloneObjects(...)` 用于把构件从"构件库 DWG"复制进目标图；
- **已知问题**：内存未及时释放（讨论帖 [AUGI: WblockCloneObjects memory not disposed](https://forums.augi.com/showthread.php?160201-WblockCloneObjects-memory-not-disposed)）；长时批量操作应分批调用并显式清理中间对象。

### 5.4 其他
- 生成大量线稿后，图层数量与线型/线宽设置应**预先建好**（不要在循环里反复建图层，会触发大量事务写）；
- 对生成结果做 `Extents3d` 缓存与一次性 Zoom，避免逐实体刷新；
- 图纸空间视口 + `PlotHidden` 可作为打印阶段的消隐兜底，但**只对图纸空间对象生效**（见 [`PlotSettings.PlotHidden`](https://help.autodesk.com/cloudhelp/2020/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_PlotSettings_PlotHidden.html)）。

**结论：能用（分批事务 + 块化 + 选择性 WblockClone 即可），具体上限需实测标定。**

---

## 6. `Autodesk.AutoCAD.DatabaseServices` 之外的官方示例资源

### 6.1 ADN（Autodesk Developer Network）官方 GitHub 组织
- 组织主页：[github.com/ADN-DevTech](https://github.com/ADN-DevTech)
- 培训材料：[ObjectARXTrainingMaterial](https://github.com/ADN-DevTech/ObjectARXTrainingMaterial)（含 `ObjectARX_Training_Labs_2026.zip` 等按年份打包）、[objectarx-training](https://github.com/ADN-DevTech/objectarx-training)、[AutoCADDotnetTrainingMaterial](https://github.com/ADN-DevTech/AutoCADDotnetTrainingMaterial)
- 向导/模板：[AutoCAD-Net-Wizards](https://github.com/ADN-DevTech/AutoCAD-Net-Wizards)、[ObjectARX-Wizards](https://github.com/ADN-DevTech/ObjectARX-Wizards)
- 实战样例：[autocad-automation-apps](https://github.com/ADN-DevTech/autocad-automation-apps)、[AcadWebView](https://github.com/ADN-DevTech/AcadWebView)、[MgdDbg](https://github.com/ADN-DevTech/MgdDbg)、[PIOTM-QRCode](https://github.com/ADN-DevTech/PIOTM-QRCode)、[PIOTM-SolidCutSurface](https://github.com/ADN-DevTech/PIOTM-SolidCutSurface)、[Associative-Fillet-sample-application](https://github.com/ADN-DevTech/Associative-Fillet-sample-application)
- 历史 SDK 归档：[objectarx_sdks](https://github.com/ADN-DevTech/objectarx_sdks)
- 官方开发者博客：[blog.autodesk.io](https://blog.autodesk.io/)

### 6.2 ObjectARX SDK 结构
[ObjectARX Directory Tree](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide/files/GUID-4A1AEF78-79CE-4352-9BDC-318E594F00AF.htm)：`classmap`（类层次图 DWG）、`docs`（`arxdoc.chm`）、`inc` / `inc-x64`、`lib-x64`、`Redistrib-x64`、`samples`（按程序化焦点分组，重点 `polysamp`）、`utils`（`brep`、`ObjARXWiz`）。

### 6.3 关于"三维 → 二维工程图"这一块
**未找到确证**：在官方 SDK samples 目录说明与上述 ADN 仓库目录树中，均未发现覆盖"三维实体 → 二维工程图 / 剖面出图 / 隐藏线消除"的样例工程。该主题在官方示例层面处于空白。

### 6.4 相关但不等价的官方参考
- Civil 3D 的 "Using Sections"（横断面，模型不同）：[Using Sections](https://help.autodesk.com/cloudhelp/2026/ENU/Civil3D-DevGuide/files/GUID-E6623FD2-97BC-4CF7-96BD-4F433D20BA31.htm)
- 图纸空间视口创建：[Create Paper Space Viewports (.NET)](https://help.autodesk.com/cloudhelp/2026/ENU/OARX-DevGuide-Managed/files/GUID-61C22902-F63B-4204-86EC-FA37312D1B6E.htm)

**结论：官方样例存在但不覆盖本题；需要自己实现，或下沉到 C++ ObjectARX。**

---

## 推荐的技术路线（按实现代价从低到高）

- **A. 原生 Section 通道（最低代价，首选主链路）**：`Section.State = SectionState.Boundary` + 水平法向做立面、水平法向 + `CreateJog`/`AddVertex` 做剖面；先用 `SectionSettings.Set*` 固化图层/线型/线宽/填充图案/隐藏线开关，再 `GenerateSectionGeometry` 取 5 组实体写回模型空间。**风险**：仅支持 3dSolid/Surface/Body/Region（Mesh、AEC 墙不行），且本质是切面投影，拿不到严格的全模可见线/隐藏线分离。
- **B. FLATSHOT 作为"整模快照"补充**：`SendStringToExecute("._flatshot")` + 事后遍历新匿名块统一刷图层。**代价低；风险**：异步执行、命令参数不可编程、结果受用户当前图层/视图状态污染，不宜作为主链路。
- **C. 自研正交投影 + 隐藏线消除（仅限长方体/棱柱/圆柱等凸体）**：背面剔除 → 投影 → 深度排序/区间扫描遮挡裁剪 → 可见/隐藏段分图层（`A-WALL-VIS` / `A-WALL-HID`）。**可控性与制图标准符合度最好；代价高**（遮挡裁剪、线段合并、性能），且**无现成 .NET 库可引用**。
- **D. C++ ObjectARX 桥接**：用 `AcDbSection` / `AcDbSectionSettings` 全量 API（`setLinetype`、`setLayer`、`setHatchPatternName`、`setCurrentSectionType` 等，见 [setLinetype](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSectionSettings__setLinetype_AcDbSectionSettings__SectionType_AcDbSectionSettings__Geometry_ACHAR_.html)、[setCurrentSectionType](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbSectionSettings__setCurrentSectionType_AcDbSectionSettings__SectionType.html)）与 AcGs/AcGi 的隐藏线能力，通过 C++/CLI 或 native DLL + P/Invoke 暴露给 .NET。**代价高；风险**：需维护双工具链与 ABI/R24-R25 版本绑定。
- **E. 换第三方几何内核 / 生态库自建 DWG 写出**（如 ODA/Teigha 或 IFoxCAD 生态）。**代价最高；风险最大**：许可、R24/R25 双版本适配、与 acad 原生实体的兼容性；仅当 A–D 均不可行时考虑。
