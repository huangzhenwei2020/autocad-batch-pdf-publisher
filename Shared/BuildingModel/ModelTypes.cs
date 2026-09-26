using System;
using System.Collections.Generic;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// 建筑模型的 P0 数据约定：楼层 + 墙 + 洞口 + 楼板 + 柱。
    ///
    /// 这一层**故意不依赖 AutoCAD**：它同时被两边的程序共用——
    /// 建模程序（读模型、算投影、写视图）与主插件的"落图"命令（读视图、写成实体）。
    /// 用源码链接的方式编进两个工程（见 BatchPdfPublisher*.csproj 与 BuildingModelStudio.csproj），
    /// 因此**改这里会同时影响程序与插件**。
    ///
    /// 单位统一：毫米（mm），双精度；坐标是建筑坐标（平面 X/Y + 竖向 Z=标高）。
    /// </summary>
    public static class BuildingModelSchema
    {
        /// <summary>中间格式版本。程序与插件版本不一致时靠它给出明确提示。</summary>
        public const int Version = 1;
    }

    public sealed class PointModel
    {
        public double X { get; set; }
        public double Y { get; set; }

        public PointModel() { }
        public PointModel(double x, double y) { X = x; Y = y; }
    }

    /// <summary>楼层：立面/剖面的竖向基准，也是"层高"的唯一来源。</summary>
    public sealed class StoreyModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>结构标高（mm）。</summary>
        public double Elevation { get; set; }
        /// <summary>层高（mm）。</summary>
        public double Height { get; set; }
    }

    /// <summary>墙：轴线两端 + 厚度 + 高度（从所属楼层的标高起算）。</summary>
    public sealed class WallModel
    {
        public string Id { get; set; }
        public string StoreyId { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public double Thickness { get; set; } = 200d;
        /// <summary>墙高；0 表示取所属楼层的层高。</summary>
        public double Height { get; set; }
        public string Material { get; set; }
    }

    /// <summary>洞口（门窗）：挂在某道墙上，沿墙轴线的定位 + 宽高 + 窗台高。</summary>
    public sealed class OpeningModel
    {
        public string Id { get; set; }
        public string HostWallId { get; set; }
        /// <summary>门窗编号（对应现有门窗参数库的类型）。</summary>
        public string Code { get; set; }
        /// <summary>窗 / 门 / 洞口。</summary>
        public string Kind { get; set; } = "窗";
        /// <summary>洞口中心沿墙轴线到墙起点的距离（mm）。</summary>
        public double Offset { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        /// <summary>窗台高（相对所属楼层标高，mm）。</summary>
        public double Sill { get; set; }
    }

    /// <summary>楼板：闭合轮廓 + 板厚 + 板顶标高。</summary>
    public sealed class SlabModel
    {
        public string Id { get; set; }
        public string StoreyId { get; set; }
        public List<PointModel> Outline { get; set; } = new List<PointModel>();
        public double Thickness { get; set; } = 120d;
        /// <summary>板顶标高（mm）。</summary>
        public double TopElevation { get; set; }
    }

    /// <summary>柱：平面矩形 + 高度（从所属楼层标高起算）。</summary>
    public sealed class ColumnModel
    {
        public string Id { get; set; }
        public string StoreyId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 400d;
        public double Depth { get; set; } = 400d;
        /// <summary>柱高；0 表示取所属楼层的层高。</summary>
        public double Height { get; set; }
    }

    /// <summary>整个建筑模型（P0 只含体量所必需的构件）。</summary>
    public sealed class BuildingModelDocument
    {
        public int SchemaVersion { get; set; } = BuildingModelSchema.Version;
        public string Name { get; set; }
        public List<StoreyModel> Storeys { get; set; } = new List<StoreyModel>();
        public List<WallModel> Walls { get; set; } = new List<WallModel>();
        public List<OpeningModel> Openings { get; set; } = new List<OpeningModel>();
        public List<SlabModel> Slabs { get; set; } = new List<SlabModel>();
        public List<ColumnModel> Columns { get; set; } = new List<ColumnModel>();

        public StoreyModel FindStorey(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var storey in Storeys)
                if (storey != null && string.Equals(storey.Id, id, StringComparison.OrdinalIgnoreCase)) return storey;
            return null;
        }

        /// <summary>墙的底标高（= 所属楼层标高）；找不到楼层时按 0。</summary>
        public double BaseElevationOf(WallModel wall)
        {
            var storey = wall == null ? null : FindStorey(wall.StoreyId);
            return storey == null ? 0d : storey.Elevation;
        }

        /// <summary>墙高：显式给了就用它，否则取层高，再不行按 3000。</summary>
        public double HeightOf(WallModel wall)
        {
            if (wall == null) return 0d;
            if (wall.Height > 0.5d) return wall.Height;
            var storey = FindStorey(wall.StoreyId);
            if (storey != null && storey.Height > 0.5d) return storey.Height;
            return 3000d;
        }

        public double HeightOf(ColumnModel column)
        {
            if (column == null) return 0d;
            if (column.Height > 0.5d) return column.Height;
            var storey = FindStorey(column.StoreyId);
            if (storey != null && storey.Height > 0.5d) return storey.Height;
            return 3000d;
        }
    }

    // ───────────────────────── 视图（抽象视图的载体） ─────────────────────────

    public enum ViewKind
    {
        Elevation = 0,
        Section = 1,
        /// <summary>表格类视图（门窗表）：同样是"视图产物 = 线 + 文字"，落图命令不用特殊处理。</summary>
        Schedule = 2,
        /// <summary>平面图：水平剖切俯视（墙、门窗、柱），用 <see cref="ViewDefinitionModel.StoreyIds"/> 指定画哪一层。</summary>
        Plan = 3
    }

    /// <summary>立面方向：南 = 从南往北看（默认取"从 -Y 看向 +Y"）。</summary>
    public enum ElevationDirection
    {
        South = 0,
        North = 1,
        East = 2,
        West = 3
    }

    /// <summary>剖切线的走向：沿 Y 的竖直线（切 X=常数）或沿 X 的水平线（切 Y=常数）。</summary>
    public enum SectionAxis
    {
        CutX = 0,
        CutY = 1
    }

    /// <summary>视图定义：从哪看 / 在哪剖 / 比例多少。可重建，不存几何。</summary>
    public sealed class ViewDefinitionModel
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public ViewKind Kind { get; set; } = ViewKind.Elevation;
        /// <summary>视图比例的分母（1:100 → 100）。</summary>
        public int Scale { get; set; } = 100;

        // 立面
        public ElevationDirection Direction { get; set; } = ElevationDirection.South;

        // 剖面
        public SectionAxis CutAxis { get; set; } = SectionAxis.CutX;
        /// <summary>剖切线位置（mm）。</summary>
        public double CutPosition { get; set; }
        /// <summary>剖视方向：+1 表示看向坐标增大的一侧，-1 表示减小。</summary>
        public int ViewSign { get; set; } = 1;
        /// <summary>只投影剖切面以外这个深度内的构件（mm，0 = 不限）。</summary>
        public double ViewDepth { get; set; }
        /// <summary>参与投影的楼层；空 = 全部。</summary>
        public List<string> StoreyIds { get; set; } = new List<string>();
        /// <summary>model = 跟随模型重算；drawing = 已手工深化，不再自动重算。</summary>
        public string State { get; set; } = "model";
    }

    /// <summary>视图里的一条线（已投影到视图平面：U 水平、Z 竖向、单位 mm）。</summary>
    public sealed class ViewLine
    {
        public string Layer { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        /// <summary>可选：线型名（如 HIDDEN）；空 = 随层。</summary>
        public string LineType { get; set; }
    }

    /// <summary>视图里的一行文字（标高、图名、编号等）。</summary>
    public sealed class ViewText
    {
        public string Layer { get; set; }
        public string Text { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>字高（mm，已按出图比例换算到模型空间）。</summary>
        public double Height { get; set; } = 250d;
    }

    /// <summary>视图里的一块填充（剖面剖切填充）。</summary>
    public sealed class ViewHatch
    {
        public string Layer { get; set; }
        /// <summary>填充图案名（CAD 里的 PAT 名；P0 用 ANSI31 = 45° 细线）。</summary>
        public string Pattern { get; set; } = "ANSI31";
        /// <summary>预定义图案的比例（Spacing &gt; 0 时不用它）。</summary>
        public double Scale { get; set; } = 1d;
        public double Angle { get; set; }
        /// <summary>
        /// 模型单位（mm）下的建议线间距；&gt; 0 时插件按"用户定义图案"用这个间距填充。
        /// 用它而不是图案比例，是因为比例依赖图案自身的基准间距，很难一眼算对；
        /// 直接给"图上 1.5mm 左右"的间距更可靠。
        /// </summary>
        public double Spacing { get; set; }
        /// <summary>边界多边形（视图平面坐标，闭合；不必重复首点）。</summary>
        public List<PointModel> Boundary { get; set; } = new List<PointModel>();
    }

    /// <summary>一张视图的产物：线 + 文字 + 填充。插件"落图"命令按图层把它们建成 CAD 实体。</summary>
    public sealed class ViewDocument
    {
        public int SchemaVersion { get; set; } = BuildingModelSchema.Version;
        public string Id { get; set; }
        public string Title { get; set; }
        public int Scale { get; set; } = 100;
        public ViewKind Kind { get; set; }
        /// <summary>视图平面原点对应的模型坐标（插件用它在 DWG 里定位）。</summary>
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public List<ViewLine> Lines { get; set; } = new List<ViewLine>();
        public List<ViewText> Texts { get; set; } = new List<ViewText>();
        public List<ViewHatch> Hatches { get; set; } = new List<ViewHatch>();
        /// <summary>
        /// 图上元素与模型构件的对应关系（视图平面里的矩形范围 + 模型里的构件 id）。
        /// 用途：预览里点选门窗 → 知道是哪一樘；落图后要联动门窗表/改做法也有依据。
        /// 只是"索引"，不影响画出来的几何，插件落图时忽略它。
        /// </summary>
        public List<ViewAnchor> Anchors { get; set; } = new List<ViewAnchor>();
        /// <summary>
        /// 尺寸标注：落图时由插件建成**真的 CAD 标注**（`RotatedDimension`，可拉伸、可改），
        /// 预览里按"界线 + 尺寸线 + 建筑标记 + 数值"画出来。空 = 不标。
        /// </summary>
        public List<ViewDimension> Dimensions { get; set; } = new List<ViewDimension>();
        /// <summary>生成时用到的模型版本，便于判断是否需要重算。</summary>
        public string ModelRevision { get; set; }
        /// <summary>投影过程中的提示（例如斜墙按包围盒近似）。</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 视图里一个可点选元素的定位框：<see cref="ElementId"/> 指向模型里的构件（洞口/墙…），
    /// 坐标是视图平面坐标（与线条同一套，落在 (U, Z) 上）。
    /// </summary>
    public sealed class ViewAnchor
    {
        /// <summary>元素种类：opening（门窗洞口）/ wall（墙）。</summary>
        public string Kind { get; set; } = "opening";
        /// <summary>模型里的构件 id。</summary>
        public string ElementId { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    /// <summary>
    /// 一条尺寸标注。竖直尺寸量 Z（立面层高/洞口定位），水平尺寸量 U（洞口定位）。
    ///
    /// 字段与 CAD 的 <c>RotatedDimension</c> 一一对应：
    /// 两个"被量点"在 (AnchorPosition, From) 与 (AnchorPosition, To)，
    /// 尺寸线画在 LinePosition 处（竖直尺寸给 X、水平尺寸给 Y）。
    /// <see cref="Text"/> 留空 = 让 CAD 自己量出真实数值（推荐）；填了就用填的。
    /// </summary>
    public sealed class ViewDimension
    {
        public string Layer { get; set; }
        /// <summary>true = 竖直尺寸（量 Z），false = 水平尺寸（量 U）。</summary>
        public bool Vertical { get; set; } = true;
        /// <summary>被量范围的起点（竖直：Z；水平：U）。</summary>
        public double From { get; set; }
        /// <summary>被量范围的终点。</summary>
        public double To { get; set; }
        /// <summary>尺寸界线的起点所在的另一坐标（竖直：X；水平：Y）。</summary>
        public double AnchorPosition { get; set; }
        /// <summary>尺寸线位置（竖直：X；水平：Y）。</summary>
        public double LinePosition { get; set; }
        /// <summary>文字覆盖值；空 = 让 CAD 按实际距离量。</summary>
        public string Text { get; set; }
        /// <summary>这条尺寸是什么（层高 / 洞口定位 / 总高…），只用于提示与日志。</summary>
        public string Note { get; set; }
    }

    // ───────────────────────── "提取图纸"的中间格式 ─────────────────────────

    /// <summary>从 DWG 提取出来的一条图元（P0 只取后续识别需要的类型）。</summary>
    public sealed class DrawingEntityModel
    {
        /// <summary>LINE / LWPOLYLINE / POLYLINE / ARC / CIRCLE / INSERT / TEXT / MTEXT。</summary>
        public string Type { get; set; }
        public string Layer { get; set; }
        /// <summary>块参照的块名（INSERT 才有）。</summary>
        public string BlockName { get; set; }
        /// <summary>顶点（LINE 两端、多段线各顶点、弧的起点/终点）。</summary>
        public List<PointModel> Points { get; set; } = new List<PointModel>();
        public double X { get; set; }
        public double Y { get; set; }
        public double Rotation { get; set; }
        public double Scale { get; set; } = 1d;
        /// <summary>半径（ARC/CIRCLE）。</summary>
        public double Radius { get; set; }
        /// <summary>文字内容（TEXT/MTEXT）。</summary>
        public string Text { get; set; }
        public double TextHeight { get; set; }
        /// <summary>图元在图纸上的句柄，便于回写与核对。</summary>
        public string Handle { get; set; }
    }

    public sealed class LayerInfoModel
    {
        public string Name { get; set; }
        public short Color { get; set; }
        public string LineType { get; set; }
    }

    /// <summary>"提取图纸"的产物：图层清单 + 图元清单。识别（P3）在程序侧做。</summary>
    public sealed class DrawingImportDocument
    {
        public int SchemaVersion { get; set; } = BuildingModelSchema.Version;
        public string DrawingName { get; set; }
        public string DrawingPath { get; set; }
        /// <summary>提取时间（本地时间，ISO 8601）。</summary>
        public string ExtractedAt { get; set; }
        public List<LayerInfoModel> Layers { get; set; } = new List<LayerInfoModel>();
        public List<DrawingEntityModel> Entities { get; set; } = new List<DrawingEntityModel>();
    }

    // ───────────────────────── 门窗类型库（从插件导出，程序里放门窗用） ─────────────────────────

    /// <summary>
    /// 一个门窗类型：编号 + 洞口尺寸 + 立面做法。
    /// 来源是插件里已有的项目门窗参数（`DoorWindowElevationPreference`）与立面模板
    /// （`DoorWindowElevationTemplate`），因此**不需要用户重新录一遍**。
    /// </summary>
    public sealed class OpeningTypeModel
    {
        public string Code { get; set; }
        /// <summary>窗 / 门 / 门联窗 / 百叶。</summary>
        public string Kind { get; set; } = "窗";
        public double Width { get; set; } = 1500d;
        public double Height { get; set; } = 1800d;
        public double Sill { get; set; } = 900d;

        // 立面做法（P1.5 先存下来，落图阶段用于画分格与开启线）
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public bool HasOuterFrame { get; set; } = true;
        public double OuterFrameWidth { get; set; } = 50d;
        public bool HasMullion { get; set; } = true;
        public double MullionWidth { get; set; } = 50d;
        public bool HasInstallationGap { get; set; } = true;
        public double InstallationGap { get; set; } = 20d;
        public string DoorFrameType { get; set; }
        public double DoorFrameWidth { get; set; }
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        /// <summary>门联窗里门扇的靠位：靠左 / 靠右 / 居中。</summary>
        public string DoorPlacement { get; set; }
        public double DoorEdgeDistance { get; set; }
        /// <summary>凸窗左右转折面：墙 / 窗，以及进深（mm）。</summary>
        public string BayLeftSide { get; set; }
        public string BayRightSide { get; set; }
        public double BayLeftDepth { get; set; } = 600d;
        public double BayRightDepth { get; set; } = 600d;
        public string BayLeftCellLayout { get; set; }
        public string BayRightCellLayout { get; set; }
        public string Material { get; set; }
        public string AtlasName { get; set; }
        public string Remarks { get; set; }
        /// <summary>来源说明（项目参数 / 手工）。</summary>
        public string Source { get; set; }
    }

    /// <summary>立面做法模板（可复用的分格/开启预设）。</summary>
    public sealed class OpeningTemplateModel
    {
        public string Name { get; set; }
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
    }

    /// <summary>
    /// 门窗类型库：插件导出 → 程序读取。放在模型目录下的 <c>openings.json</c>，随项目一起走；
    /// 没有它程序也能画图（用默认尺寸）。
    /// </summary>
    public sealed class OpeningTypeLibraryDocument
    {
        public int SchemaVersion { get; set; } = BuildingModelSchema.Version;
        public string ProjectName { get; set; }
        public string ExportedAt { get; set; }
        public List<OpeningTypeModel> Types { get; set; } = new List<OpeningTypeModel>();
        public List<OpeningTemplateModel> Templates { get; set; } = new List<OpeningTemplateModel>();

        /// <summary>按编号查类型（忽略大小写与首尾空格）；找不到返回 null。</summary>
        public OpeningTypeModel FindType(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || Types == null) return null;
            var wanted = code.Trim();
            foreach (var type in Types)
            {
                if (type == null || string.IsNullOrWhiteSpace(type.Code)) continue;
                if (string.Equals(type.Code.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return type;
            }
            return null;
        }
    }

    /// <summary>
    /// 视图图层键与默认样式。
    ///
    /// P0 先在这里硬编码（插件"落图"时若图层不存在就按这里的颜色/线型创建）；
    /// P5 会把它接到制图标准（`DraftingStandardService`）上，让用户能改名字和颜色。
    /// </summary>
    public static class ViewLayers
    {
        public const string Cut = "WL-模型-剖到";
        public const string Elevation = "WL-模型-立面";
        public const string Opening = "WL-模型-门窗";
        public const string Ground = "WL-模型-地坪";
        public const string CutHatch = "WL-模型-剖切填充";
        public const string LevelText = "WL-模型-标高";
        public const string Title = "WL-模型-图名";
        /// <summary>尺寸标注（落图时建成真的 CAD 标注）。</summary>
        public const string Dimension = "WL-模型-尺寸";
        /// <summary>门窗表等表格视图的线框与文字。</summary>
        public const string Schedule = "WL-模型-门窗表";

        public sealed class Style
        {
            public string Name;
            public short Color;
            public string LineType;
            /// <summary>线宽（mm×100，CAD 的 LineWeight 用百分之一毫米）。</summary>
            public int LineWeight;
            public string Description;
        }

        public static readonly Style[] All =
        {
            new Style { Name = Cut, Color = 7, LineType = "Continuous", LineWeight = 50, Description = "剖切到的构件轮廓（粗）" },
            new Style { Name = Elevation, Color = 7, LineType = "Continuous", LineWeight = 25, Description = "立面上可见的轮廓（中）" },
            new Style { Name = Opening, Color = 7, LineType = "Continuous", LineWeight = 18, Description = "门窗洞口与分格（细）" },
            new Style { Name = Ground, Color = 7, LineType = "Continuous", LineWeight = 70, Description = "室外地坪线（特粗）" },
            new Style { Name = CutHatch, Color = 8, LineType = "Continuous", LineWeight = 13, Description = "剖切填充" },
            new Style { Name = LevelText, Color = 7, LineType = "Continuous", LineWeight = 13, Description = "标高符号与数值" },
            new Style { Name = Title, Color = 7, LineType = "Continuous", LineWeight = 25, Description = "图名与比例" },
            new Style { Name = Dimension, Color = 7, LineType = "Continuous", LineWeight = 13, Description = "尺寸标注（落图时建成 CAD 标注）" },
            new Style { Name = Schedule, Color = 7, LineType = "Continuous", LineWeight = 18, Description = "门窗表线框与文字" }
        };

        public static Style Find(string name)
        {
            foreach (var style in All)
                if (string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase)) return style;
            return null;
        }
    }
}
