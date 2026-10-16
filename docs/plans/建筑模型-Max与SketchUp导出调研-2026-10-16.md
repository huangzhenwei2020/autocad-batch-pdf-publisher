# 建筑模型向 3ds Max 与 SketchUp 导出调研

后续实现：用户已授权开发。首批静态 FBX/SKP、统一导出窗口和安全保存已实现，具体测试、限制和安装结果见 [2026-10-16 实现记录](../reviews/建筑模型-Max与SU静态导出-2026-10-16.md)。以下为原调研，不以原“尚未实现”描述覆盖后续记录；原生 MAX、动画和细分部件仍未完成。

日期：2026-10-16。状态：官方资料调研与建议方案，未实现、未实机验收；不改安装，产品仍为 1.5.6。用户本轮要求先网上找方案，随后确认目标为 **3ds Max 2024、SketchUp 2026**。

## 已确认兼容目标

- Max 2024：FBX 为主交换格式，可选本机 Max 2024 生成 MAX；后续测试须实际在 2024 导入、保存及重开，不以新版 Max 接口文档代替验收。[Max 2024 官方 FBX 导入](https://help.autodesk.com/cloudhelp/2024/ENU/3DSMax-Interoperability/files/GUID-70B36C5A-33BB-4BAF-8F0B-FE7526BCF8A1.htm)
- SketchUp 2026：原生 SKP 为目标交付，现有 GLB 路径可先做兼容小样；具体补丁版本/订阅能力与实际组件结构仍须实机核对。DAE 留作兼容后备，不作为当前主路线。
- 本轮只确认兼容范围和更新调研，不启动目标软件、不安装依赖、不执行导出或覆盖安装。

## 结论

建议目标为：3ds Max 使用 FBX 交换并可选由本机 Max 保存 MAX；SketchUp 优先原生 SKP，GLB/DAE 作为经过版本验证的兼容通道。底层使用同一份场景描述，不维护多套建筑解算，也不依赖用户手工经过 Blender 转换。

这不是 CAD/三维图库的重复入库流程。项目导出只是共享模型的输出；CAD 平面与三维资源继续使用现有共享目录及稳定资源 ID，不新建 Max 库或 SU 库。

## 当前代码核对

- `BuildingModel.Core/BuildingModelGlbExporter.cs` 是当前建筑三维文件导出器，依赖 SharpGLTF 与 LibTessDotNet，没有 FBX/SKP 写出器。
- 已有建筑/楼层/构件层级、构件和来源 ID、共享材质及墙面共面片合并。导出克隆模型，不直接修改源项目。
- 每个楼层/构件组写成一个网格，点已转成米制、Y 向上的几何坐标；没有独立活动扇节点、关闭基准/局部合页轴和动画轨道。导出当前开启的几何不等于在目标软件里仍可驱动开闭。
- 材质当前主要按几何类别生成基础颜色及玻璃透明度，不是完整贴图/PBR 或 V-Ray/Corona 材质转换系统。
- 当前整栋网格并没有实现标准层几何定义复用；不能把已有语义标准层等同于已导出网格实例。
- 历史计划记录本机 Max 2024 Batch 曾受授权问题阻挡，本轮未重新启动 Max/SU，不能宣称可用或已验收。

## 官方资料确认

### 3ds Max

1. 官方 FBX 导入器处理几何、动画等场景信息，适合作为主交换格式。[FBX Import](https://help.autodesk.com/cloudhelp/2026/ENU/3DSMax-Interoperability/files/GUID-70B36C5A-33BB-4BAF-8F0B-FE7526BCF8A1.htm)
2. FBX 可导入显示图层；平滑组重算会增加导入工作，特别是大量对象场景。不能无条件开启所有“优化”选项。[Geometry](https://help.autodesk.com/cloudhelp/2026/ENU/3DSMax-Interoperability/files/GUID-146CAC45-1D4B-4F95-8EA2-7B662D6A3B30.htm)
3. MAXScript 可设置 FBX 单位/轴向等导入参数；具体可用参数须在目标版本查询，不能静默忽略不支持项。[FBXImporterGetParam/SetParam](https://help.autodesk.com/cloudhelp/2025/CHS/3DSMax-Interoperability/files/GUID-DCA4B1CB-58D0-4D73-8A28-27A87E1E1755.htm)
4. Max Batch 可运行 MAXScript/Python；`saveMaxFile` 保存原生场景，但同名可能不提示即覆盖。因此推荐在独立任务中生成新文件，不操作用户未保存场景。[Batch](https://help.autodesk.com/cloudhelp/2026/ENU/3DSMax-Batch/files/GUID-48A78515-C24B-4E46-AC5F-884FBCF40D59.htm)、[保存接口](https://help.autodesk.com/cloudhelp/2026/ENU/MAXScript-Help/files/MAXScript-Tools-and-Interaction/File-Access/3ds-Max-Scene-Files-Access/GUID-624D3D05-B15D-4A97-9F15-DA35CDB0DDD2.html)
5. MAXScript 可以批量创建网格并设置对象自定义属性，为无独立 FBX SDK 时的本机适配提供技术路径。[Mesh Methods](https://help.autodesk.com/cloudhelp/2026/ENU/MAXScript-Help/files/3ds-Max-Objects-and-Interfaces/Editable-Meshes-Splines-Patches/Editable_Mesh-and-TriMesh/GUID-D05BCFB5-BD5A-4B17-B053-64841171A8C8.html)、[Node Properties](https://help.autodesk.com/cloudhelp/2026/ENU/MAXScript-Help/files/3ds-Max-Objects-and-Interfaces/Node-MAXWrapper/Node-Common-Properties-Operators/GUID-AF1F51D4-449B-4C4D-9F58-85DB145BC0B9.html)

建议实现分支：

- 无 Max 也能保存 FBX：评估官方 FBX SDK 的独立辅助进程，将共同场景写为 FBX。SDK 获取、目标版本、运行库和随包许可核对是发布前置条件，不手写猜测式 FBX 文件，不先承诺可任意分发 SDK。
- 本机已安装且授权可用：受控 Max 适配器可创建场景，保存 `.max`，也可调用 Max 自带 FBX 导出器。可作为首个可行性验证，不让它变成无 Max 用户的隐性依赖。
- 输出网格和对象，不自动还原为 Max 的 AEC 参数墙/参数门窗，不承诺第三方渲染器材质完全一致。官方亦提示 FBX 色彩空间转换可能影响显示颜色。

### SketchUp

1. C API 可独立创建、读取和保存 SKP；内部几何单位为英寸，需明确由毫米换算。API 调用须在该辅助程序主线程串行进行。[SketchUp C API](https://extensions.sketchup.com/developers/sketchup_c_api/sketchup/index.html)
2. 可以通过组件定义/实例复用几何，通过边的 soft/smooth 属性控制显示。建议保留墙洞边界、折角与轮廓，仅处理内部三角边。[组件与实例](https://extensions.sketchup.com/developers/sketchup_c_api/sketchup/functions_0x63.html)、[边属性](https://extensions.sketchup.com/developers/sketchup_c_api/sketchup/struct_s_u_edge_ref.html)
3. 不使用 SDK 的备选是 SU 内的 Ruby 扩展：组件定义可复用，批量几何有 EntitiesBuilder，后者自 SU 2022 起提供。扩展路线需用户运行兼容的桌面 SU，不等于独立生成 SKP。[ComponentDefinition](https://ruby.sketchup.com/Sketchup/ComponentDefinition.html)、[EntitiesBuilder](https://ruby.sketchup.com/Sketchup/EntitiesBuilder.html)
4. 当前官方文档已有 GLB 导入/导出，但不能推定所有旧版 SU 都具备该能力。[GLTF](https://help.sketchup.com/en/sketchup/working-gltf-files)
5. DAE 是兼容候选，官方使用 COLLADA 1.4，不能代替 CAD 二维图纸或保证所有 SU 原生特性。[COLLADA](https://help.sketchup.com/en/sketchup/importing-and-exporting-collada-files)
6. 官方 FBX 页面描述的是 SU 导出 FBX，不应据此设计“SU 原生导入 FBX”的通用路线。[Exporting FBX](https://help.sketchup.com/en/sketchup/exporting-fbx-files)

建议原生 SKP 独立辅助进程优先；若 SDK 获取或随包许可不能落实，先验证 Ruby 适配器，不把 DAE 更名成 SKP。SDK/文件版本须以目标用户版本进行保存重开验证。部分 SDK 文档直接打开返回 403，本次可读官方搜索索引；开发前还须取得实际 SDK 和许可文件，不视为依赖已就绪。

## 共同导出模型

推荐在现有语义模型和具体文件格式之间补充一个受控场景层，保留已有 GLB 路径的兼容回归，不先进行无边界重构。

- 项目、楼层、构件、部件四级身份，记录项目 ID/构件 ID/来源 ID/资源 ID/修订，不以名称作为唯一键。
- 几何定义与实例变换分离；只在局部几何、规格和材料确实相同时复用，不能只因编号相同就合并。支持标准层重复构件和后续家具。
- 门框/门套固定，各活动扇及其五金为独立运动组；记录局部关闭基准、轴点/轴向或轨道。首版可导出指定静态姿态，交互开闭和关键帧另行实现验收。
- 原始长度保持毫米、双精度；每个格式只在出口转换单位和轴向。大坐标可选项目基点附近导出，原点偏移随文件清单记录，确保能对齐 CAD。
- 共享材质、稳定材质槽及未来贴图相对路径；没有的贴图不伪造。首版基础色/玻璃，后续按目标渲染器分级适配。
- 保留孔洞、镜像绕序和法线；SU 平面尽量使用带洞轮廓或合并共面片，不把共面三角缝全部画成可见线。弧墙和圆角允许受控离散，显示平滑不冒充解析实体。
- 分别存储结构和视觉数据。输出几何不是反向编辑同步；目标软件改动不自动回写万落，动态组件/Max 参数控制器也不自动生成。

## 建议 UI

沿用建筑模型的出图/导出入口，新增一个统一的“三维模型导出”窗口，不在 CAD 功能区再堆多个按钮。

| 区域 | 建议控件 |
|---|---|
| 目标 | Max / SketchUp / Blender 分段选择，显示实际可用格式和目标版本 |
| 范围 | 整栋 / 当前楼层 / 选定构件 |
| 门窗状态 | 当前状态 / 全部关闭 / 指定开启角度，仅对支持机构开放 |
| 组织 | 默认保留楼层和独立构件，支持后续精细部件输出 |
| 精度 | 标准/精细，显示预估面数、实例数、材质数和文件规模 |
| 坐标 | 项目基点/原 CAD 坐标，单位明确显示为毫米 |
| 输出 | 文件路径，导出主按钮；成功后“打开所在文件夹”，主动“在软件中打开” |

不可用格式明确说明缺少哪个适配器/版本/授权，不以“启动了进程”显示导出成功。单纯文件输出不要求目标软件正在运行；原生 MAX 本机保存分支需要可用 Max。

## 实施顺序与验收

1. 按已确认的 Max 2024/SU 2026，核对本机补丁/授权，取得 SDK/许可或选择本机适配分支。先用同一小样验证墙洞、弧墙、楼板洞、双开门、两层重复组件和玻璃。
2. 建立共同场景层并保留 GLB 回归；完成静态 FBX/Max 和 SKP 输出。首版不承诺跨软件继续改宽高和交互开闭。
3. 验证目标软件中模型真实尺寸、楼层高差、原点、组件名称/ID、材质共享、实例复用和无多余对角线；保存后重开再核对，不仅检查文件存在。
4. 大模型验证后再开放自动打开：默认只保存；独立进程、进度、取消、错误日志及规模预检，尽量隔离转换失败，不保证能防止所有显卡/驱动或内存故障。不得后台启动未知渲染器或自动切材质预览。
5. 最后补门扇/抽屉等机构适配与资源修订更新。逐项明确“静态姿态、可驱动开闭、可变尺寸”的支持等级，不把几何导出当成三者全完成。

验证失败不覆盖用户旧文件或未保存场景；通过临时文件、结果校验后提交目标。目标版本已确认，实际版本兼容、SDK 分发与性能尚待开发验证，本调研不作为完成验收。
