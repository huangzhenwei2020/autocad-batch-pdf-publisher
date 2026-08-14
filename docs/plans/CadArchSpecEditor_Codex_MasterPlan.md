# CAD 建筑专业设计说明编审工具——Codex 开发总计划

> 仓库建议名称：`CadArchSpecEditor`  
> 产品暂定名称：`建筑设计说明助手`  
> 文件用途：交给 Codex 直接读取并按阶段执行。  
> 基准日期：2026-07-28。  
> 当前目标平台：AutoCAD 2022—2026，C#、WPF、WebView2CompositionControl。  
> AutoCAD 2022—2024 使用 .NET Framework 4.8；AutoCAD 2025—2026 使用 .NET 8。共享核心必须采用跨框架架构，各 AutoCAD 主版本分别编译宿主程序集。  
> AutoCAD 2027 使用 .NET 10 且存在二进制兼容变化，作为后续独立宿主适配项目处理，不得与 2022—2026 宿主共用同一个编译产物。

---

# 0. Codex 必须先理解的产品方向

## 0.1 AutoCAD 兼容性总原则

产品从立项开始即支持 AutoCAD 2022—2026，不采用“先只写 2025/2026，后期再强行兼容旧版”的路线。

核心结构：

```text
一套建筑专业领域核心
一套规则引擎
一套网页编辑器
五个薄 AutoCAD 宿主
两个运行时技术代际
```

技术代际：

```text
AutoCAD 2022—2024：.NET Framework 4.8
AutoCAD 2025—2026：.NET 8
```

所有架构、依赖、构建和测试必须围绕这一兼容矩阵建立。

本项目不是通用 CAD 文字编辑器

本项目不是通用 CAD 文字编辑器，也不是 Word 的简化复制品。

本项目只服务于：

```text
建筑专业施工图设计说明
建筑专业专项说明
建筑专业相关技术经济指标表
建筑专业相关材料、构造和审查表格
```

核心目标是：

```text
帮助建筑设计人员编制完整、规范、可追溯、便于审查的建筑专业设计说明，
减少缺项、矛盾、失效规范、编号错误和项目参数不一致问题。
```

产品可以帮助设计人员进行“审图前自检”，但不得对外宣称：

```text
自动保证审图通过
自动替代注册建筑师
自动替代设计、校对、审核、审定
自动替代施工图审查机构
```

是否通过审查取决于：

- 项目所在地；
- 项目报审时间及标准适用时间；
- 建筑类别；
- 建筑高度和规模；
- 使用功能；
- 规划及消防批复；
- 地方标准和地方管理文件；
- 项目实际图纸；
- 审查机构对完整施工图的综合审查。

因此，产品定位必须是：

```text
建筑专业设计说明编制 + 项目参数管理 + 规则化预审 + CAD 排版输出
```

而不是：

```text
AI 自动写说明
```

---

# 1. 调研结论与产品原则

## 1.1 建筑设计说明必须是结构化项目文档

建筑专业施工图设计文件通常包括：

- 图纸目录；
- 设计说明；
- 设计图纸；
- 必要的计算书或计算成果；
- 专项设计说明；
- 主要材料表和技术经济指标表。

建筑设计说明的常见组成至少包括：

1. 设计依据；
2. 项目概况；
3. 主要技术经济指标；
4. 设计标高；
5. 总平面设计；
6. 建筑用料和室内外装修构造；
7. 新技术、新材料和特殊构造；
8. 门窗性能；
9. 幕墙及特殊屋面；
10. 防水设计；
11. 电梯、自动扶梯和自动步道；
12. 无障碍设计；
13. 环保及室内环境控制；
14. 建筑安全设计；
15. 建筑防火设计；
16. 建筑节能设计；
17. 绿色建筑设计；
18. 装配式建筑设计；
19. 专项深化设计责任边界；
20. 其他项目特有说明。

软件必须把这些内容设计成可配置的专业章节，不得只保存成一整段富文本。

## 1.2 审查必须按建筑类型分类

规则至少要支持以下建筑类型配置：

```text
通用
住宅建筑
办公建筑
交通建筑
教育建筑
商业建筑
文体建筑
医疗建筑
工业建筑
其他建筑
```

不同建筑类型必须加载不同的：

- 必填章节；
- 必填参数；
- 专项表格；
- 专项规范；
- 审查规则；
- 风险提示。

不得用一套固定说明模板覆盖所有项目。

## 1.3 审查必须分国家、地方和项目三层

规则层级：

```text
国家法律法规与强制性工程建设规范
    ↓
省级标准、审查要点和管理文件
    ↓
市级标准、规划规则、消防及报审要求
    ↓
项目批复、规划条件、设计任务书及合同
```

项目新建时必须要求用户选择：

- 国家；
- 省；
- 市；
- 区县，可选；
- 报审日期；
- 设计阶段；
- 新建、改建或扩建；
- 建筑类型；
- 建筑规模；
- 是否属于特殊建设工程；
- 是否需要消防设计审查；
- 是否涉及人防；
- 是否涉及绿色建筑；
- 是否涉及装配式建筑；
- 是否涉及超限、高层或其他专项。

若所在地规则包不存在，系统必须明确显示：

```text
当前仅完成国家基础规则检查，未加载项目所在地地方规则。
```

不得静默按其他城市规则代替。

---

# 2. 当前标准基线

下列内容只作为规则库建立时的国家基础索引，不得将规范全文硬编码在代码中。

## 2.1 国家基础规范索引

规则库第一阶段至少建立以下标准元数据：

```text
《建筑工程设计文件编制深度规定（2016年版）》
建质函〔2016〕247号

《民用建筑通用规范》
GB 55031-2022
2023-03-01 实施
强制性工程建设规范

《建筑防火通用规范》
GB 55037-2022
2023-06-01 实施
强制性工程建设规范

《建筑与市政工程无障碍通用规范》
GB 55019-2021
2022-04-01 实施
强制性工程建设规范

《建筑与市政工程防水通用规范》
GB 55030-2022
2023-04-01 实施
强制性工程建设规范

《建筑节能与可再生能源利用通用规范》
GB 55015-2021
2022-04-01 实施
强制性工程建设规范

《建筑环境通用规范》
GB 55016-2021
2022-04-01 实施
强制性工程建设规范

《住宅项目规范》
GB 55038-2025
2025-05-01 实施
强制性工程建设规范
仅在住宅项目规则包中启用
```

还需根据项目类型动态加载：

- 住宅相关标准；
- 办公建筑相关标准；
- 商业建筑相关标准；
- 教育建筑相关标准；
- 医疗建筑相关标准；
- 交通建筑相关标准；
- 文体建筑相关标准；
- 工业建筑相关标准；
- 车库相关标准；
- 老年人、儿童等特定使用人群相关标准；
- 当地节能、绿色建筑、规划管理和消防技术文件。

## 2.2 标准状态管理

每项标准必须保存：

```csharp
public sealed class StandardReference
{
    public required string StandardId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    public DateOnly? PublishedDate { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? RepealedDate { get; init; }

    public StandardStatus Status { get; init; }

    public string JurisdictionCode { get; init; } = "CN";
    public string IssuingAuthority { get; init; } = string.Empty;

    public List<string> ApplicableBuildingTypes { get; init; } = [];
    public List<string> Supersedes { get; init; } = [];
    public List<string> SupersededBy { get; init; } = [];

    public string OfficialSourceUrl { get; init; } = string.Empty;
    public DateTimeOffset LastVerifiedAt { get; init; }
}
```

标准状态至少包括：

```text
Draft
Active
PartiallySuperseded
Superseded
Repealed
Unknown
```

若标准状态为 `Unknown`，不得用于自动判定“符合”。

## 2.3 版权与许可

不得未经许可把商业出版规范全文、图集全文或付费数据库内容打包进软件。

规则包默认保存：

- 标准名称；
- 标准编号；
- 发布部门；
- 实施日期；
- 废止关系；
- 条文编号；
- 审查规则摘要；
- 程序化检查条件；
- 官方来源链接；
- 用户补充说明。

规范原文功能采用以下方式之一：

1. 跳转官方公开来源；
2. 用户导入其合法取得的文件；
3. 企业内部授权规范库；
4. 获得正式内容授权后再分发。

---

# 3. 产品工作流程

## 3.1 新建项目

用户必须先完成项目向导。

必填信息：

```text
项目名称
建设地点
省、市
建设单位
设计单位
设计阶段
报审日期
项目性质：新建/改建/扩建
建筑类型
单体数量
建筑面积
建筑高度
地上层数
地下层数
设计使用年限
主要结构类型
建筑防火分类
耐火等级
地下室防水等级
屋面防水等级
人防工程类别
绿色建筑目标
装配式要求
规划批复编号
消防相关申报类别
```

不适用字段允许明确标记：

```text
不适用
待确认
由其他专业提供
由专项设计单位提供
```

不得用空字符串混淆这些状态。

## 3.2 自动生成章节树

系统根据以下条件生成章节：

```text
地区规则包
建筑类型
新建/改建/扩建
建筑高度
是否有地下室
是否有幕墙
是否有电梯
是否有人防
是否为住宅
是否为绿色建筑
是否为装配式建筑
是否采用专项深化设计
```

章节状态：

```text
Required        必须编写
Conditional     条件触发
Optional        可选
NotApplicable   不适用
ExternalDesign  由专项单位设计
Pending         待确认
```

## 3.3 编辑与填表

用户在 Word 式界面中编辑，但内容仍绑定到结构化字段。

示例：

```text
建筑高度：{{Project.BuildingHeight}}
地上层数：{{Project.AboveGroundFloors}}
耐火等级：{{Fire.ResistanceRating}}
屋面防水等级：{{Waterproof.RoofGrade}}
```

字段更新后，所有引用位置同步更新。

普通文本和项目字段必须可视化区分。

字段不得被用户误当普通文字删除。

## 3.4 审查前自检

检查顺序：

```text
规则包和标准状态检查
→ 项目基础数据完整性检查
→ 设计说明章节完整性检查
→ 表格完整性检查
→ 项目参数内部一致性检查
→ 说明与表格一致性检查
→ 规范适用性检查
→ 确定性规则检查
→ AI 辅助语言检查
→ 生成预审报告
```

## 3.5 输出 CAD

输出对象：

```text
MText
AutoCAD Table
标题 MText
章节 MText
页码及页标题
必要的字段绑定信息
```

输出方式：

```text
新建设计说明图
更新已有设计说明图
另存为修订版
仅更新指定章节
仅更新指定表格
```

---

# 4. 建筑专业章节模型

## 4.1 设计依据

必须支持：

- 政府批复文件；
- 规划条件；
- 设计任务书；
- 地勘及测绘资料；
- 设计合同；
- 国家规范；
- 地方规范；
- 标准图集；
- 专项审查意见；
- 上一阶段设计文件。

每项依据必须带：

```text
名称
编号
版本或年份
日期
来源
有效状态
是否适用于本项目
备注
```

检查：

- 规范编号是否缺少年号；
- 标准是否已废止或部分废止；
- 项目所在地与地方标准是否匹配；
- 同一标准是否重复；
- 旧版和新版是否同时引用；
- 设计说明中引用的图集是否在标准图集表中登记。

## 4.2 项目概况

结构化字段至少包括：

```text
项目名称
项目地址
周边概况
建设单位
用地性质
项目规模等级
建筑使用性质
建筑面积
建筑基底面积
地上建筑面积
地下建筑面积
建筑高度
地上层数
地下层数
设计使用年限
结构类型
抗震设防烈度
建筑防火分类
耐火等级
人防类别
屋面防水等级
地下室防水等级
主要功能数量
停车数量
```

不同建筑类型增加专用字段。

住宅示例：

```text
住宅套数
套型数量
公共配套
居住人数
最高入户层
```

酒店示例：

```text
客房数
床位数
宴会及会议规模
```

医疗示例：

```text
床位数
门诊量
洁污分区
```

学校示例：

```text
班级数
学生人数
教职工人数
```

## 4.3 主要技术经济指标

采用专业表格，不得只允许普通文字输入。

至少支持：

```text
用地面积
总建筑面积
计容建筑面积
不计容建筑面积
地上建筑面积
地下建筑面积
建筑基底面积
容积率
建筑密度
绿地面积
绿地率或绿化覆盖率
最大层数
建筑高度
机动车停车位
非机动车停车位
分栋建筑面积
分期指标
核增面积
核减面积
```

要求：

- 支持公式；
- 支持单位；
- 支持数值精度；
- 支持来源字段；
- 支持允许误差；
- 支持规划值、设计值、核准值三列；
- 支持差异警告；
- 支持分栋汇总；
- 支持地上、地下汇总；
- 支持计容与不计容校核。

## 4.4 设计标高

字段：

```text
±0.000 相对标高
对应绝对标高
采用高程系统
室内外高差
特殊楼层控制标高
防洪或防潮控制标高
```

检查：

- 未填写绝对标高；
- 多处引用不一致；
- 总图和建筑说明标高冲突；
- 高程系统缺失。

## 4.5 总平面建筑说明

包含：

- 基地概况；
- 规划布局；
- 竖向设计；
- 交通组织；
- 消防总平面；
- 公共空间；
- 配套设施；
- 绿化与景观接口；
- 日照分析结论；
- 无障碍场地流线；
- 垃圾收集设施；
- 分期建设说明。

本工具只负责建筑专业说明和数据，不替代总图专业计算。

## 4.6 建筑用料和装修构造

支持：

- 外墙；
- 内墙；
- 地面；
- 楼面；
- 顶棚；
- 屋面；
- 地下室；
- 踢脚；
- 散水；
- 台阶；
- 坡道；
- 油漆涂料；
- 保温材料；
- 防火封堵；
- 防潮层；
- 防水层。

构造做法必须支持：

```text
做法编号
部位
基层
各构造层
材料名称
厚度
性能要求
耐火要求
防水要求
保温要求
引用图集
备注
```

## 4.7 门窗与幕墙

门窗性能表至少包括：

```text
门窗类型
使用部位
型材
玻璃
抗风压性能
水密性能
气密性能
保温性能
隔声性能
防火性能
安全玻璃要求
五金要求
开启限制
备注
```

幕墙及专项设计说明必须包括：

- 主体设计控制条件；
- 性能目标；
- 防火要求；
- 防水要求；
- 防坠落要求；
- 节能要求；
- 预埋件及结构接口；
- 专项设计责任单位；
- 施工图深化要求；
- 审核和确认流程。

## 4.8 防水设计

防水专用表至少包括：

```text
部位
防水等级
设防要求
防水材料
道数
构造层次
细部要求
排水措施
节点索引
施工要求
备注
```

部位：

- 屋面；
- 地下室底板；
- 地下室外墙；
- 地下室顶板；
- 厨房；
- 卫生间；
- 阳台；
- 外墙；
- 水池；
- 种植屋面；
- 设备机房；
- 其他涉水区域。

检查：

- 防水等级缺失；
- 说明和表格等级不一致；
- 有地下室但无地下室防水说明；
- 有种植屋面但无种植屋面防水条目；
- 有涉水房间但无防水构造；
- 仅写材料名称但无性能或构造要求。

## 4.9 电梯和自动扶梯

表格：

```text
编号
类型
服务楼层
额定载重量
额定速度
轿厢尺寸
井道尺寸
开门尺寸
开门方式
底坑深度
顶层高度
机房形式
消防功能
无障碍功能
担架功能
专项单位
备注
```

系统只校核建筑说明和表格完整性，不计算机电选型。

## 4.10 无障碍设计

无障碍章节采用：

```text
设施清单 + 无障碍流线 + 适用条款 + 图纸索引
```

设施清单至少包括：

- 场地出入口；
- 建筑出入口；
- 无障碍通道；
- 无障碍坡道；
- 无障碍电梯；
- 无障碍楼梯；
- 无障碍卫生间；
- 无障碍厕位；
- 轮椅席位；
- 无障碍客房；
- 无障碍停车位；
- 低位服务设施；
- 无障碍标识；
- 其他项目类型专用设施。

不得只生成一句“本工程按无障碍规范设计”。

## 4.11 建筑安全设计

安全专用清单：

- 临空防护；
- 栏杆；
- 窗台；
- 凸窗；
- 儿童活动场所防护；
- 防攀爬；
- 防坠落；
- 玻璃安全；
- 出入口坠物防护；
- 屋面检修；
- 非上人屋面检修；
- 爬梯及检修口；
- 变形缝；
- 防滑；
- 防夹；
- 防撞；
- 外墙装饰和保温系统防脱落；
- 吊顶和构件安全；
- 其他项目风险点。

每一项必须包含：

```text
是否适用
设计措施
关键参数
图纸索引
规范依据
责任专业
确认状态
```

## 4.12 建筑防火设计

防火说明必须结构化，不得只保存为连续文本。

至少包括：

```text
建筑使用性质
建筑高度
建筑层数
建筑防火分类
耐火等级
火灾危险性类别
防火分区
防烟分区
安全出口数量
疏散人数
疏散宽度
疏散距离
疏散楼梯形式
消防电梯
消防救援口
消防车道
消防登高操作场地
防火墙
防火门窗
竖向井道
建筑保温与外墙
屋面
变形缝
中庭和共享空间
避难层或避难间
特殊功能房间
```

提供专用表格：

```text
防火分区汇总表
疏散计算汇总表
防火门窗性能表
消防救援设施表
建筑保温防火性能表
```

注意：

- 确定性规则由规则引擎完成；
- AI 不得判定是否满足强制性条文；
- 疏散计算和面积计算必须保留计算依据和输入来源；
- 无法从项目数据确定时显示“待专业人员确认”。

## 4.13 建筑节能与绿色建筑

节能章节至少管理：

```text
气候分区
建筑分类
围护结构热工性能
窗墙面积比
屋面
外墙
外窗
幕墙
架空楼板
分户墙和分户楼板
热桥措施
气密性
遮阳
可再生能源
能耗分析报告
碳排放分析报告
节能计算书版本
```

绿色建筑章节至少管理：

```text
目标等级
适用地方标准
设计目标
建筑专业控制项
建筑专业评分项
技术措施
图纸索引
专项报告
```

不得由 AI 生成计算结果。

## 4.14 专项深化设计责任边界

专项表至少包括：

```text
专项名称
是否另行委托
主体设计单位控制要求
专项设计输入条件
专项设计输出文件
接口专业
预留条件
审核责任
提交节点
是否需报审
备注
```

专项类型示例：

- 幕墙；
- 钢结构；
- 采光顶；
- 特殊屋面；
- 室内装修；
- 景观；
- 标识；
- 厨房；
- 洁净；
- 声学；
- 夜景照明；
- 泛光照明；
- 电梯；
- 游乐设施；
- 装配式深化。

---

# 5. 专业表格编辑器

本项目不开发无限制 Excel 克隆。

只开发建筑设计说明需要的专业表格。

## 5.1 第一版表格类型

```text
主要技术经济指标表
单体建筑指标表
分期指标表
建筑面积构成表
室内装修做法表
建筑构造做法表
防水设计表
门窗性能表
电梯参数表
无障碍设施表
建筑安全措施表
防火分区汇总表
疏散计算汇总表
建筑节能围护结构表
绿色建筑措施表
专项设计接口表
规范及图集引用表
```

## 5.2 表格基础功能

必须支持：

- 插入和删除行列；
- 合并和拆分单元格；
- 多单元格选择；
- 拖动调整行高和列宽；
- 固定行高和列宽；
- 自动适应内容；
- 平均分布；
- 重复表头；
- 禁止跨页拆行；
- 允许跨页拆表；
- 表格标题；
- 表格编号；
- 公式；
- 单位；
- 小数精度；
- 字段绑定；
- 条件显示；
- 数据验证；
- 复制和粘贴；
- 从 Excel 粘贴；
- 导出 CSV；
- 撤销和重做。

## 5.3 表格公式

公式不得直接执行任意脚本。

只允许白名单表达式：

```text
SUM
MIN
MAX
ROUND
IF
ABS
COUNT
字段引用
行列引用
```

示例：

```text
总建筑面积 = 地上建筑面积 + 地下建筑面积
容积率 = 计容建筑面积 / 用地面积
建筑密度 = 建筑基底面积 / 用地面积 × 100
```

公式结果必须记录：

- 输入值；
- 输入来源；
- 计算时间；
- 公式版本；
- 手动覆盖状态；
- 覆盖原因。

---

# 6. 项目数据中心

所有说明、表格和审查规则必须引用同一套项目数据。

## 6.1 项目模型

```csharp
public sealed class ArchitectureProject
{
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; set; }

    public ProjectLocation Location { get; init; } = new();
    public ProjectLifecycle Lifecycle { get; init; } = new();
    public ProjectClassification Classification { get; init; } = new();

    public PlanningData Planning { get; init; } = new();
    public BuildingData Building { get; init; } = new();
    public FireData Fire { get; init; } = new();
    public AccessibilityData Accessibility { get; init; } = new();
    public WaterproofData Waterproof { get; init; } = new();
    public EnergyData Energy { get; init; } = new();
    public GreenBuildingData GreenBuilding { get; init; } = new();

    public List<BuildingUnit> BuildingUnits { get; init; } = [];
    public List<ApprovalDocument> ApprovalDocuments { get; init; } = [];
    public List<StandardReference> Standards { get; init; } = [];
}
```

## 6.2 字段状态

每个关键字段必须采用：

```csharp
public sealed class ProjectValue<T>
{
    public T? Value { get; set; }

    public ValueState State { get; set; }

    public string Source { get; set; } = string.Empty;
    public string SourceDocumentId { get; set; } = string.Empty;

    public string EnteredBy { get; set; } = string.Empty;
    public DateTimeOffset? ConfirmedAt { get; set; }

    public bool IsManuallyOverridden { get; set; }
    public string OverrideReason { get; set; } = string.Empty;
}
```

`ValueState`：

```text
Unknown
Pending
Confirmed
NotApplicable
ProvidedByOtherDiscipline
ProvidedBySpecialist
Overridden
```

AI 不得把 `Unknown` 自动变成 `Confirmed`。

---

# 7. 文档模型

## 7.1 根文档

```csharp
public sealed class ArchitectureDesignSpecDocument
{
    public required Guid DocumentId { get; init; }
    public int SchemaVersion { get; init; }

    public required Guid ProjectId { get; init; }
    public required string Name { get; set; }

    public List<ArchitectureSection> Sections { get; init; } = [];
    public List<ArchitectureTable> Tables { get; init; } = [];

    public DocumentLayoutProfile Layout { get; init; } = new();
    public CadDocumentBinding? CadBinding { get; set; }

    public DocumentRevision Revision { get; init; } = new();
}
```

## 7.2 章节模型

```csharp
public sealed class ArchitectureSection
{
    public required Guid SectionId { get; init; }
    public required string SectionType { get; init; }
    public required string Title { get; set; }

    public RequirementState RequirementState { get; set; }

    public List<DocumentNode> Content { get; init; } = [];
    public List<string> ApplicableRuleIds { get; init; } = [];
    public List<string> ReferencedProjectFields { get; init; } = [];

    public ReviewState ReviewState { get; set; }
}
```

## 7.3 文档节点

至少包括：

```text
HeadingNode
ParagraphNode
NumberedParagraphNode
BulletListNode
ProjectFieldNode
StandardCitationNode
TableReferenceNode
DrawingReferenceNode
WarningNode
NoteNode
PageBreakNode
KeepTogetherNode
```

每个节点必须有稳定 ID。

---

# 8. 建筑专业预审引擎

## 8.1 原则

规则引擎负责：

```text
是否缺项
数据是否矛盾
公式是否错误
标准是否适用
确定性条件是否满足
```

AI 负责：

```text
语句是否通顺
同义术语是否统一
是否存在重复说明
是否可以改善表达
```

AI 不得替代规则引擎。

## 8.2 检查类别

### A. 标准有效性检查

- 规范已废止；
- 规范部分强条被废止；
- 引用旧版；
- 缺少年号；
- 地方标准地区不匹配；
- 项目报审日期与规范实施日期冲突；
- 同一规范重复引用；
- 项目类型缺少专项标准。

### B. 项目基础数据检查

- 建筑面积缺失；
- 建筑高度缺失；
- 层数缺失；
- 耐火等级缺失；
- 防水等级缺失；
- 设计使用年限缺失；
- 建筑类型不明确；
- 新建、改建、扩建未确定；
- 报审地区不明确。

### C. 章节完整性检查

- 必填章节缺失；
- 条件章节未触发；
- 有地下室但缺地下防水说明；
- 有幕墙但缺幕墙专项要求；
- 有电梯但缺电梯表；
- 公共建筑缺无障碍设施表；
- 住宅缺住宅专项检查；
- 有专项设计但缺责任边界。

### D. 数据一致性检查

至少检查：

```text
项目名称
建设地点
建设单位
建筑面积
建筑高度
层数
建筑防火分类
耐火等级
设计使用年限
防水等级
停车位
容积率
建筑密度
绿地率
±0.000
主要功能数量
```

检查范围：

```text
项目数据中心
设计说明正文
技术经济指标表
专项表格
CAD 输出字段
修订记录
```

### E. 建筑防火检查

规则包驱动检查：

- 防火分类；
- 耐火等级；
- 防火分区；
- 安全出口；
- 疏散人数；
- 疏散宽度；
- 疏散距离；
- 楼梯形式；
- 消防电梯；
- 救援口；
- 消防车道；
- 登高操作场地；
- 保温材料；
- 防火门窗；
- 特殊功能空间。

无法确定时不得自动给出“合格”。

### F. 无障碍检查

- 是否有场地无障碍流线；
- 是否有无障碍入口；
- 是否有无障碍电梯；
- 是否有无障碍卫生间或厕位；
- 是否有无障碍停车位；
- 是否有项目类型专项设施；
- 说明、表格和图纸索引是否齐全。

### G. 建筑安全检查

- 栏杆；
- 窗台；
- 凸窗；
- 防攀爬；
- 安全玻璃；
- 坠物防护；
- 屋面检修；
- 防滑；
- 外墙系统安全；
- 儿童和老年人场所专项防护。

### H. 防水检查

- 防水等级；
- 构造层次；
- 重点部位；
- 排水措施；
- 细部节点；
- 材料性能；
- 说明与表格一致性。

### I. 节能和绿色建筑检查

- 气候分区；
- 设计标准；
- 计算书版本；
- 围护结构参数；
- 门窗参数；
- 气密性；
- 可再生能源；
- 绿色建筑目标；
- 措施和图纸索引。

## 8.3 问题等级

```text
Blocker
强制性问题、关键参数冲突、无法报审的缺项

Error
地方强制要求、重要完整性错误、计算错误

Warning
高概率审查意见、信息不完整、需人工确认

Info
表达、格式和优化建议
```

## 8.4 审查问题模型

```csharp
public sealed class ReviewIssue
{
    public required Guid IssueId { get; init; }
    public required string RuleId { get; init; }

    public ReviewSeverity Severity { get; init; }

    public required string Title { get; init; }
    public required string Message { get; init; }

    public string StandardCode { get; init; } = string.Empty;
    public string ClauseReference { get; init; } = string.Empty;

    public string TargetNodeId { get; init; } = string.Empty;
    public string TargetFieldPath { get; init; } = string.Empty;

    public string Evidence { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;

    public bool RequiresProfessionalConfirmation { get; init; }
}
```

每条问题必须能定位到：

- 章节；
- 段落；
- 表格；
- 单元格；
- 项目字段；
- CAD 对象。

---

# 9. 规则包设计

## 9.1 规则包结构

```text
rules/
├─ CN/
│  ├─ common/
│  ├─ residential/
│  ├─ office/
│  ├─ commercial/
│  ├─ education/
│  ├─ medical/
│  ├─ transportation/
│  ├─ culture-sports/
│  ├─ industrial/
│  └─ other/
├─ CN-GX/
├─ CN-SC/
├─ CN-GD/
├─ CN-SH/
├─ CN-FJ/
└─ schemas/
```

地方规则包后续按用户实际业务地区实施。

第一版不得伪造广西、四川等地方规则。

若未完成正式调研，只能建立空规则包和状态提示。

## 9.2 规则格式

建议采用版本化 JSON 或 YAML。

示例：

```yaml
ruleId: CN-ARCH-COMPLETE-001
version: 1
title: 建筑专业设计说明应包含设计依据
jurisdiction: CN
buildingTypes:
  - common
effectiveFrom: 2017-01-01
severity: Blocker
checkType: requiredSection
target:
  sectionType: DesignBasis
message: 建筑专业设计说明缺少“设计依据”章节。
requiresProfessionalConfirmation: false
references:
  - standardCode: 建质函〔2016〕247号
    clause: 建筑专业施工图设计说明相关要求
```

复杂数值规则不得通过不受控的脚本执行。

采用：

- 预定义规则类型；
- 参数表达式；
- 白名单比较运算；
- 单元测试；
- 规则版本签名。

## 9.3 规则包更新

规则包必须独立于插件发布。

支持：

- 离线导入；
- 企业内部规则包；
- 官方规则包；
- 规则签名；
- 更新日志；
- 回滚；
- 项目锁定规则版本。

已报审项目不得在后台自动换成新规则后改变结论。

用户必须主动选择：

```text
继续使用项目锁定规则
更新到最新规则并重新检查
```

---

# 10. WPF + WebView2 专业界面

## 10.1 界面结构

```text
┌───────────────────────────────────────────────────────────────┐
│ 项目  编辑  插入  建筑表格  审查  规范  CAD输出  AI辅助       │
├────────────┬──────────────────────────────┬───────────────────┤
│ 项目导航   │ 设计说明编辑区               │ 属性与审查面板    │
│            │                              │                   │
│ 项目参数   │ 标题                         │ 当前字段          │
│ 章节树     │ 正文                         │ 规范依据          │
│ 专业表格   │ 表格                         │ 问题列表          │
│ 审查问题   │ CAD版面预览                  │ 图纸索引          │
├────────────┴──────────────────────────────┴───────────────────┤
│ 地区规则：国家+四川｜建筑类型：办公｜问题：2阻断 8错误 12警告 │
└───────────────────────────────────────────────────────────────┘
```

## 10.2 WPF 负责

- AutoCAD 宿主；
- PaletteSet；
- WebView2 生命周期；
- 文件与项目管理；
- CAD 事务；
- 规则包加载；
- 标准元数据；
- 安全存储；
- 日志；
- 自动保存；
- 异常恢复。

## 10.3 WebView2 负责

- ProseMirror 文档编辑；
- 专业章节树；
- 表格编辑；
- 项目字段控件；
- 规范引用控件；
- 审查问题定位；
- CAD 版面预览；
- 修订对比；
- 用户交互。

## 10.4 WebView2 控件

使用：

```text
WebView2CompositionControl
```

不得把普通 WebView2 与大量 WPF 浮层混用后再通过临时遮挡方案修补。

---

# 11. CAD 版面与分页

这是本项目的核心功能之一。

设计说明通常跨多张图纸，不能把整份文档写成一个无限长 MText。

## 11.1 版面模板

支持：

```text
A0
A1
A2
A3
自定义图框
```

模板参数：

```text
图幅
横向/竖向
标题栏占用区域
可编辑区域
页边距
分栏数量
栏间距
默认文字样式
默认字高
行距
标题字高
表格字高
页标题
图号规则
```

## 11.2 内部单位

编辑器内部采用：

```text
纸面毫米
```

CAD 输出时根据：

- 模型空间；
- 布局空间；
- 出图比例；
- 注释比例；
- 用户单位；

转换成 CAD 单位。

不得把 CSS 像素直接当 CAD 单位。

## 11.3 自动排版

支持：

- 自动流入下一栏；
- 自动流入下一页；
- 手动分页；
- 标题与下一段保持；
- 表格标题与表格保持；
- 表头跨页重复；
- 表格行禁止拆分；
- 表格整体保持；
- 长表允许拆页；
- 孤行控制；
- 章节起始页规则；
- 用户锁定分页位置。

## 11.4 CAD 输出对象

推荐：

```text
每个连续正文块输出为独立 MText
每个表格输出为独立 AutoCAD Table
标题输出为独立 MText
页面信息输出为独立 MText 或块属性
```

原因：

- 避免超大 MText；
- 方便局部更新；
- 方便定位审查问题；
- 方便表格原位更新；
- 方便分页；
- 降低局部修改对全页的影响。

---

# 12. CAD 绑定与更新

## 12.1 绑定模型

```csharp
public sealed class CadEntityBinding
{
    public required string DocumentFingerprint { get; init; }
    public required string Handle { get; init; }
    public required Guid NodeId { get; init; }

    public required string EntityType { get; init; }
    public string OriginalContentHash { get; set; } = string.Empty;

    public string LayoutProfileId { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public int ColumnIndex { get; set; }

    public CadGeometrySnapshot Geometry { get; set; } = new();
}
```

不得只保存 ObjectId。

## 12.2 更新流程

```text
锁定 CAD 文档
→ 校验 DWG 指纹
→ 解析 Handle
→ 检查对象是否被修改
→ 显示冲突
→ 创建单次撤销组
→ 开启事务
→ 更新 MText 和 Table
→ 写入绑定信息
→ 提交事务
→ 更新项目快照
```

保存失败必须完整回滚。

## 12.3 冲突处理

对象出现以下情况时禁止静默覆盖：

- 已删除；
- 内容被人工修改；
- 位置被移动；
- 图层被更改；
- 文字样式被更改；
- 表格结构被更改；
- 对象来自外部参照；
- 图层锁定；
- 图纸只读；
- 当前 DWG 与绑定项目不一致。

选项：

```text
采用 CAD 内容
采用编辑器内容
保留双方并另存副本
查看差异
取消
```

---

# 13. AI 功能边界

## 13.1 允许的 AI 功能

- 建筑专业语句润色；
- 错别字检查；
- 标点统一；
- 全角半角统一；
- 术语统一；
- 重复条文检测；
- 将用户提供的零散说明整理到对应章节；
- 将用户提供的数据整理成专业表格；
- 生成修改说明；
- 解释规则检查结果；
- 对比两版设计说明。

## 13.2 禁止的 AI 行为

AI 不得：

- 自行确定项目面积；
- 自行确定建筑高度；
- 自行确定防火分类；
- 自行确定耐火等级；
- 自行确定疏散人数；
- 自行确定疏散宽度；
- 自行确定防水等级；
- 自行确定绿色建筑等级；
- 自行确定地方标准；
- 编造规范编号；
- 编造条文；
- 把推测值写成确认值；
- 自动接受修改；
- 自动保存到 CAD；
- 直接运行 CAD 命令；
- 执行任意脚本。

## 13.3 AI 修改流程

```text
用户选择内容
→ 系统附带已确认项目字段
→ AI 返回结构化建议
→ 规则层检查 AI 是否修改关键字段
→ 显示前后差异
→ 用户逐条确认
→ 更新文档
→ 重新运行专业预审
→ 用户主动输出 CAD
```

---

# 14. 技术架构

## 14.1 解决方案结构

```text
CadArchSpecEditor/
├─ src/
│  ├─ CadArchSpec.Domain/
│  ├─ CadArchSpec.Application/
│  ├─ CadArchSpec.RuleEngine/
│  ├─ CadArchSpec.RulePackages/
│  ├─ CadArchSpec.StandardRegistry/
│  ├─ CadArchSpec.LayoutEngine/
│  ├─ CadArchSpec.EditorBridge/
│  ├─ CadArchSpec.Infrastructure.AutoCAD.Shared/
│  ├─ CadArchSpec.Host.AutoCAD2022/
│  ├─ CadArchSpec.Host.AutoCAD2023/
│  ├─ CadArchSpec.Host.AutoCAD2024/
│  ├─ CadArchSpec.Host.AutoCAD2025/
│  ├─ CadArchSpec.Host.AutoCAD2026/
│  ├─ CadArchSpec.AI/
│  └─ CadArchSpec.Editor.Web/
├─ tests/
│  ├─ CadArchSpec.Domain.Tests/
│  ├─ CadArchSpec.RuleEngine.Tests/
│  ├─ CadArchSpec.LayoutEngine.Tests/
│  ├─ CadArchSpec.Application.Tests/
│  ├─ CadArchSpec.AutoCAD.Shared.Tests/
│  ├─ CadArchSpec.AutoCAD2022.Tests/
│  ├─ CadArchSpec.AutoCAD2023.Tests/
│  ├─ CadArchSpec.AutoCAD2024.Tests/
│  ├─ CadArchSpec.AutoCAD2025.Tests/
│  ├─ CadArchSpec.AutoCAD2026.Tests/
│  └─ CadArchSpec.Editor.Web.Tests/
├─ rules/
│  ├─ CN/
│  ├─ CN-GX/
│  ├─ CN-SC/
│  └─ schemas/
├─ templates/
│  ├─ common/
│  ├─ residential/
│  ├─ office/
│  ├─ commercial/
│  ├─ education/
│  ├─ medical/
│  ├─ transportation/
│  ├─ culture-sports/
│  ├─ industrial/
│  └─ other/
├─ samples/
│  ├─ projects/
│  ├─ documents/
│  ├─ tables/
│  └─ rule-packages/
├─ docs/
│  ├─ product-scope.md
│  ├─ architecture.md
│  ├─ target-framework-strategy.md
│  ├─ autocad-version-matrix.md
│  ├─ domain-model.md
│  ├─ architecture-sections.md
│  ├─ architecture-tables.md
│  ├─ rule-engine.md
│  ├─ standard-registry.md
│  ├─ cad-layout.md
│  ├─ message-protocol.md
│  ├─ security.md
│  └─ development-log.md
├─ installer/
│  ├─ bundle/
│  └─ manifests/
├─ tools/
├─ Directory.Build.props
├─ Directory.Packages.props
├─ CadArchSpecEditor.sln
├─ README.md
└─ CHANGELOG.md
```

## 14.2 项目依赖规则

```text
Domain
优先目标框架：netstandard2.0
不得引用 AutoCAD、WPF、WebView2、React、AI SDK

Application
优先目标框架：netstandard2.0
只引用 Domain 和抽象接口

RuleEngine
优先目标框架：netstandard2.0
引用 Domain，不引用 AutoCAD

LayoutEngine
优先目标框架：netstandard2.0
引用 Domain，不引用 AutoCAD

StandardRegistry
优先目标框架：netstandard2.0
引用 Domain，不引用 AutoCAD

EditorBridge
优先目标框架：netstandard2.0
只负责 DTO、序列化和消息协议

Infrastructure.AutoCAD.Shared
保存可跨 2022—2026 复用的 CAD 转换、绑定、冲突检测和写回计划逻辑
不得直接依赖单一 AutoCAD 主版本的差异 API

Host.AutoCAD2022
.NET Framework 4.8
引用 AutoCAD 2022 SDK

Host.AutoCAD2023
.NET Framework 4.8
引用 AutoCAD 2023 SDK

Host.AutoCAD2024
.NET Framework 4.8
引用 AutoCAD 2024 SDK

Host.AutoCAD2025
.NET 8 Windows
引用 AutoCAD 2025 SDK

Host.AutoCAD2026
.NET 8 Windows
引用 AutoCAD 2026 SDK

Editor.Web
React + TypeScript + ProseMirror
前端只构建一套静态资源，由五个宿主共同加载
不得引用任何 AutoCAD .NET 类型

AI
只实现 Application 中定义的 AI 接口
不得直接引用 AutoCAD SDK
```

# 15. AutoCAD 版本与目标框架策略

## 15.1 正式支持范围

```text
AutoCAD 2022
AutoCAD 2023
AutoCAD 2024
AutoCAD 2025
AutoCAD 2026
Windows 10 / Windows 11
64 位版本
```

不支持 32 位 AutoCAD。

## 15.2 技术代际

```text
AutoCAD 2022—2024
.NET Framework 4.8
WPF
WebView2CompositionControl

AutoCAD 2025—2026
.NET 8 Windows
WPF
WebView2CompositionControl
```

不得让一个宿主 DLL 同时面向 .NET Framework 4.8 和 .NET 8。

不得让同一个编译产物直接加载到所有 AutoCAD 版本。

## 15.3 必须共享的部分

以下内容必须共享，禁止复制五套：

- Domain；
- Application；
- RuleEngine；
- LayoutEngine；
- StandardRegistry；
- EditorBridge；
- AI 抽象；
- 建筑专业模板；
- 规则包；
- React + ProseMirror 前端；
- JSON Schema；
- 项目文件格式；
- 文档文件格式；
- 表格文件格式。

## 15.4 必须分版本编译的部分

以下内容按 AutoCAD 主版本分别编译：

- AutoCAD 插件入口；
- AcMgd、AcDbMgd、AcCoreMgd 引用；
- PaletteSet 宿主；
- AutoCAD 文档生命周期；
- MText 和 Table API 适配；
- 数据库事务和文档锁；
- Autodesk Bundle 组件声明；
- 版本差异 API。

## 15.5 共享核心目标框架

共享领域层优先采用：

```text
netstandard2.0
```

仅当某模块确实需要更高框架能力时，才允许多目标编译，并必须证明：

```text
.NET Framework 4.8 宿主可以引用
.NET 8 宿主可以引用
序列化结果保持一致
项目文件格式保持一致
```

共享项目不得调用：

- WPF 控件；
- WebView2 控件；
- AutoCAD 类型；
- 某一主版本专用 API；
- 仅存在于 .NET 8 的 API；
- 仅存在于 .NET Framework 的 API。

## 15.6 五个薄宿主

必须建立：

```text
CadArchSpec.Host.AutoCAD2022
CadArchSpec.Host.AutoCAD2023
CadArchSpec.Host.AutoCAD2024
CadArchSpec.Host.AutoCAD2025
CadArchSpec.Host.AutoCAD2026
```

每个宿主项目必须：

1. 引用对应主版本 Autodesk 托管程序集；
2. 使用受控 SDK 路径；
3. 不把 Autodesk DLL 提交到公开仓库；
4. 生成独立插件程序集；
5. 运行独立加载测试；
6. 运行独立 MText 往返测试；
7. 运行独立 Table 往返测试；
8. 运行独立 Ctrl+Z 撤销测试；
9. 运行独立多文档切换测试；
10. 运行独立关闭文档清理测试。

## 15.7 共享 AutoCAD 适配层

建立：

```text
CadArchSpec.Infrastructure.AutoCAD.Shared
```

共享层负责：

- 领域对象与 CAD DTO 转换；
- Handle 绑定；
- 内容 Hash；
- 写回计划；
- 冲突检测；
- CAD 版面 DTO；
- 统一错误模型；
- 统一能力接口。

版本差异只能通过以下方式处理：

- 版本宿主实现接口；
- 薄适配器；
- 受控条件编译；
- 运行时能力检测。

禁止复制五套完整业务逻辑。

## 15.8 WebView2 资源策略

前端只构建一次：

```text
CadArchSpec.Editor.Web/dist/
├─ index.html
├─ assets/
├─ editor.js
└─ editor.css
```

五个 AutoCAD 宿主共同加载同一套前端资源。

每个宿主必须检查：

- WebView2 Runtime；
- 用户数据目录写入权限；
- 前端资源完整性；
- 初始化异常；
- 页面进程退出；
- CAD 文档关闭后的资源释放。

WebView2 初始化失败不得导致 AutoCAD 崩溃。

## 15.9 构建配置

```text
Debug-AutoCAD2022
Debug-AutoCAD2023
Debug-AutoCAD2024
Debug-AutoCAD2025
Debug-AutoCAD2026

Release-AutoCAD2022
Release-AutoCAD2023
Release-AutoCAD2024
Release-AutoCAD2025
Release-AutoCAD2026
```

构建脚本至少支持：

```text
build-2022
build-2023
build-2024
build-2025
build-2026
build-all
test-shared
package-all
```

## 15.10 安装包结构

```text
CadArchSpecEditor.bundle/
├─ PackageContents.xml
└─ Contents/
   ├─ 2022/
   │  └─ CadArchSpec.Host.AutoCAD2022.dll
   ├─ 2023/
   │  └─ CadArchSpec.Host.AutoCAD2023.dll
   ├─ 2024/
   │  └─ CadArchSpec.Host.AutoCAD2024.dll
   ├─ 2025/
   │  └─ CadArchSpec.Host.AutoCAD2025.dll
   ├─ 2026/
   │  └─ CadArchSpec.Host.AutoCAD2026.dll
   ├─ Shared/
   │  ├─ 共享程序集
   │  ├─ 规则包
   │  └─ 模板
   └─ Web/
      └─ 统一前端资源
```

`PackageContents.xml` 必须根据 AutoCAD 主版本加载正确宿主程序集。

## 15.11 推荐开发顺序

```text
第一批：AutoCAD 2022—2024
第二批：AutoCAD 2025—2026
```

原因：

- 2022—2024 同属 .NET Framework 4.8；
- 可先稳定 MText、Table、事务、撤销和 WebView2 宿主；
- 再迁移到 .NET 8 宿主；
- 共享领域、规则和前端无需重写。

阶段 0—4 必须保持跨框架兼容，不得绑定任一 AutoCAD 主版本。

## 15.12 AutoCAD 2027

AutoCAD 2027 使用 .NET 10，且存在二进制兼容变化。

因此：

- 后续单独建立 `CadArchSpec.Host.AutoCAD2027`；
- 不纳入第一版验收；
- 不得用 2025/2026 宿主 DLL 直接加载；
- Domain、Application、规则、模板和前端继续共享；
- AutoCAD API 适配部分重新编译并测试。

# 16. WebView2 消息协议

所有消息使用版本化 JSON。

```json
{
  "protocolVersion": 1,
  "messageId": "uuid",
  "type": "project.load",
  "payload": {}
}
```

消息至少包括：

```text
editor.ready
project.load
project.changed
document.load
document.patch
document.saveRequest
document.saveResult
table.changed
projectField.changed
review.run
review.result
review.locate
standard.open
cad.previewRequest
cad.previewResult
cad.writeRequest
cad.writeResult
error.report
```

禁止：

- 执行网页端传来的任意 C#；
- 执行网页端传来的任意 JavaScript；
- 执行任意 CAD 命令字符串；
- 使用 `eval`；
- 使用无限制反射分发命令。

---

# 17. 开发阶段

Codex 不得一次性实现全部功能。

每阶段完成后必须：

1. 编译；
2. 测试；
3. 更新开发日志；
4. 汇总新增和修改文件；
5. 说明设计决策；
6. 说明已知问题；
7. 停止并等待下一条指令。

---

## 阶段 0：建立建筑专业领域基础

本阶段不连接 AutoCAD，不实现正式富文本编辑器。

任务：

1. 创建解决方案和目录。
2. 创建 Domain、Application、RuleEngine、LayoutEngine、StandardRegistry、EditorBridge 项目，并保证共享项目可同时被 .NET Framework 4.8 与 .NET 8 宿主引用。
3. 创建前端空项目。
4. 建立建筑项目模型。
5. 建立字段状态模型。
6. 建立建筑设计说明章节模型。
7. 建立建筑专业表格模型。
8. 建立规范元数据模型。
9. 建立规则包 Schema。
10. 建立审查问题模型。
11. 创建一份完整的示例建筑项目 JSON。
12. 创建一份完整的建筑设计说明文档 JSON。
13. 创建主要技术经济指标表示例。
14. 创建防水设计表示例。
15. 创建建筑安全措施表示例。
16. 创建防火分区汇总表示例。
17. 编写 JSON 往返测试。
18. 编写表格公式测试。
19. 编写规则包解析测试。
20. 编写字段状态测试。
21. 完成相关文档。

验收：

```text
dotnet build
dotnet test
npm install
npm run build
npm run test
```

全部通过。

---

## 阶段 1：独立建筑设计说明编辑器原型

任务：

- React + TypeScript；
- ProseMirror；
- 建筑专业章节树；
- 项目字段节点；
- 标准引用节点；
- Word 式文字编辑；
- 多级编号；
- 中文排版；
- 撤销重做；
- 查找替换；
- 示例文档加载和保存。

不得连接 AutoCAD。

---

## 阶段 2：建筑专业表格编辑器

只实现本计划列出的建筑专业表格。

任务：

- 技术经济指标表；
- 防水表；
- 装修做法表；
- 建筑安全措施表；
- 无障碍设施表；
- 防火分区表；
- 疏散计算表；
- 门窗性能表；
- 电梯表；
- 专项接口表；
- Excel 粘贴；
- 公式；
- 单位；
- 数据验证；
- 跨页表头。

---

## 阶段 3：项目数据中心

任务：

- 项目向导；
- 地区；
- 建筑类型；
- 项目分类；
- 项目参数；
- 数据来源；
- 确认状态；
- 变更记录；
- 字段绑定；
- 全文同步；
- 关键字段锁定；
- 参数冲突提示。

---

## 阶段 4：国家基础规则引擎

任务：

- 规则包加载；
- 生效日期；
- 废止状态；
- 必填章节；
- 必填字段；
- 数据一致性；
- 表格公式；
- 标准引用检查；
- 审查问题定位；
- 预审报告。

本阶段只建立已核实的国家基础规则。

不得编造地方规则。

---

## 阶段 5：WPF + WebView2 双框架宿主原型

任务：

- 建立 AutoCAD 2024 / .NET Framework 4.8 宿主原型；
- 建立 AutoCAD 2026 / .NET 8 宿主原型；
- WPF PaletteSet；
- WebView2CompositionControl；
- 统一消息协议；
- 统一前端静态资源；
- 项目打开和保存；
- 自动保存；
- 恢复；
- 错误处理；
- 多文档状态隔离；
- 验证共享核心能同时被两个技术代际引用；
- 验证两个宿主显示相同编辑界面。

本阶段暂不写 MText 和 AutoCAD Table 图元。

## 阶段 6：AutoCAD 2022—2026 读取与输出

任务分两批执行。

第一批：

```text
AutoCAD 2022
AutoCAD 2023
AutoCAD 2024
.NET Framework 4.8
```

第二批：

```text
AutoCAD 2025
AutoCAD 2026
.NET 8
```

每个主版本都必须完成：

- 独立宿主编译；
- 对应版本 Autodesk SDK 引用；
- MText 读取和写回；
- AutoCAD Table 读取和写回；
- 图层；
- 文字样式；
- 字高；
- 插入点；
- Handle 绑定；
- 文档锁；
- 数据库事务；
- 单一撤销组；
- 冲突检测；
- 完整回滚；
- 多文档切换；
- 关闭文档清理；
- 独立集成测试。

不得只在一个版本测试后宣称其他版本兼容。

## 阶段 7：CAD 图幅、分栏和分页

任务：

- A0/A1/A2/A3；
- 自定义图框；
- 标题栏避让；
- 纸面毫米；
- 模型空间和布局空间；
- 自动分栏；
- 自动分页；
- 表格跨页；
- 重复表头；
- CAD 预览；
- 局部更新。

---

## 阶段 8：建筑类型模板

依次实现：

```text
通用
住宅
办公
商业
教育
医疗
交通
文体
工业
其他
```

每类必须有：

- 必填字段；
- 必填章节；
- 专用表格；
- 示例项目；
- 单元测试；
- 审查规则测试。

---

## 阶段 9：地方规则包

根据用户真实业务地区逐个实施。

每个地方规则包必须：

1. 来源于官方有效文件；
2. 保存文件名称和文号；
3. 保存发布日期和实施日期；
4. 保存官方来源；
5. 由建筑专业人员复核；
6. 编写测试项目；
7. 标明与国家规则的覆盖关系；
8. 不得由 AI 自动生成后直接发布。

---

## 阶段 10：AI 辅助

基础编辑、表格、规则和 CAD 输出稳定后再实现。

AI 只能提供：

- 语言修改；
- 章节整理；
- 术语统一；
- 修改说明；
- 问题解释。

---

## 阶段 11：发布与质量保证

任务：

- Autodesk Bundle；
- AutoCAD 2022 安装组件；
- AutoCAD 2023 安装组件；
- AutoCAD 2024 安装组件；
- AutoCAD 2025 安装组件；
- AutoCAD 2026 安装组件；
- `PackageContents.xml` 版本匹配；
- WebView2 Runtime 检查；
- 日志；
- 恢复；
- 崩溃隔离；
- 用户手册；
- 规则包更新；
- 项目迁移；
- 企业模板；
- 自动化测试；
- 五个 AutoCAD 主版本的安装、加载、编辑、保存和撤销验收。

# 18. Codex 强制规则

1. 只做建筑专业。
2. 不得扩展到结构、给排水、暖通或电气说明。
3. 可以保存其他专业提供的数据，但不得检查其他专业计算。
4. 不得宣称自动保证审图通过。
5. 不得把 AI 当规范数据库。
6. 不得编造规范、条文或地方政策。
7. 所有确定性审查必须来自可追溯规则。
8. 所有规则必须有来源、版本和生效日期。
9. Domain 不得引用 AutoCAD。
10. Domain 不得引用 WPF。
11. Domain 不得引用 WebView2。
12. 规则引擎不得执行任意脚本。
13. WebView2 消息不得执行任意命令。
14. AI 不得直接修改 CAD。
15. AI 不得自动确认项目参数。
16. 关键字段不得默认为推测值。
17. 保存失败必须完整回滚。
18. CAD 更新必须支持一次 Ctrl+Z 撤销。
19. 不得只用 ObjectId 做持久绑定。
20. 不得把整份说明保存成唯一 HTML。
21. 不得把完整设计说明正文写入普通日志。
22. 不得提交 AutoCAD SDK DLL 到公开仓库。
23. NuGet 和 npm 依赖必须锁定。
24. 编译警告视为错误。
25. 每个阶段完成后必须停止。
26. 不得让 .NET Framework 4.8 宿主引用仅支持 .NET 8 的共享程序集。
27. 不得让 .NET 8 宿主依赖仅在 .NET Framework 中存在的 API。
28. AutoCAD 2022—2026 必须分别编译、分别加载、分别测试。
29. 不得复制五套完整业务逻辑；版本项目只允许保留薄宿主和差异适配。
30. 前端静态资源必须由五个宿主共享，不得维护五套前端。
31. 安装包必须根据 AutoCAD 主版本加载正确宿主程序集。

---

# 19. 第一版完成标准

以下流程稳定运行才算第一版完成：

```text
用户新建建筑项目
→ 选择地区和建筑类型
→ 填写项目基础参数
→ 系统生成建筑专业设计说明章节
→ 用户像 Word 一样编辑
→ 用户填写建筑专业表格
→ 项目字段在全文和表格中同步
→ 系统检查缺项、矛盾、失效规范和确定性规则
→ 用户处理审查问题
→ 系统按 CAD 图框自动分栏分页
→ 输出 MText 和 AutoCAD Table
→ 保留绑定关系
→ 后续可局部更新
→ 整次更新可一次撤销
→ 生成建筑专业预审报告
```

---

# 20. 官方调研来源

以下来源仅供开发团队核实，不代表可把全文直接内置到产品。

## 建筑设计文件编制深度

```text
https://zjj.sz.gov.cn/gkmlpt/content/9/9484/post_9484570.html
https://zjj.sz.gov.cn/attachment/1/1117/1117295/9484570.pdf
```

## 上海建筑、结构施工图技术审查要点 3.0

```text
https://zjw.sh.gov.cn/jsgl/20230328/9329f051ca6c4389b9e420834cc3a8ce.html
```

## 福建施工图技术审查要点 2023

```text
https://zjt.fujian.gov.cn/xxgk/zfxxgkzl/xxgkml/dfxfgzfgzhgfxwj/jskj_3794/202311/t20231124_6307466.htm
```

## 广东分级分类审查要点相关说明

```text
https://www.shanwei.gov.cn/swzfjs/yaowen/tpxw/content/post_1102758.html
```

## 住宅项目规范

```text
https://www.mohurd.gov.cn/gongkai/zc/wjk/art/2025/art_66adac27fa2144bb86f98fe4c297efd6.html
```

## AutoCAD .NET 版本与兼容性参考

```text
https://help.autodesk.com/cloudhelp/2026/KOR/AutoCAD-Customization/files/GUID-A6C680F2-DE2E-418A-A182-E4884073338A.htm
https://help.autodesk.com/cloudhelp/2026/JPN/OARX-DevGuide-Managed/files/GUID-450FD531-B6F6-4BAE-9A8C-8230AAC48CB4.htm
https://help.autodesk.com/view/OARX/2027/DEU/?guid=GUID-450FD531-B6F6-4BAE-9A8C-8230AAC48CB4
https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf
https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf
```

---

# Codex 当前执行指令

完整阅读本文件。

现在只执行：

```text
阶段 0：建立建筑专业领域基础
```

阶段 0 必须同时建立跨框架约束：

```text
共享核心能够被 .NET Framework 4.8 宿主引用
共享核心能够被 .NET 8 宿主引用
共享核心不得使用 AutoCAD、WPF 或 WebView2 类型
项目、文档、表格和规则包 JSON 在两个技术代际中保持一致
```

不得执行阶段 1 及后续阶段。

本阶段完成后输出：

1. 新增文件；
2. 修改文件；
3. 项目引用关系；
4. 领域模型说明；
5. 示例建筑项目说明；
6. 示例专业表格说明；
7. 规则包 Schema 说明；
8. 共享核心目标框架与兼容性说明；
9. 编译结果；
10. 测试结果；
11. 已知问题；
12. 下一阶段建议。

然后停止。
