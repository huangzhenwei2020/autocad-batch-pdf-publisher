using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Collections.Generic;
using System;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    /// <summary>门窗类型的统一排序。所有清单和写入 CAD 的门窗表均通过此规则归组。</summary>
    public static class DoorWindowTypeOrdering
    {
        private static readonly string[] OrderedTypes =
        {
            "普通门", "推拉门", "甲级防火门", "乙级防火门", "丙级防火门", "防火门（等级待确认）",
            "人防门", "百叶门", "门联窗",
            "普通窗", "高窗", "带形窗", "转角窗", "拱形窗", "凸窗", "百叶窗",
            "甲级防火窗", "乙级防火窗", "丙级防火窗", "防火窗（等级待确认）",
            "洞口", "待确认"
        };

        public static int TypeRank(string type)
        {
            var value = (type ?? string.Empty).Trim();
            for (var index = 0; index < OrderedTypes.Length; index++)
                if (string.Equals(OrderedTypes[index], value, StringComparison.Ordinal)) return index;
            // 未列入固定名称的新门型仍须排在所有窗型之前。
            if (value.Contains("门")) return Array.FindIndex(OrderedTypes, x => x == "门联窗");
            if (value.Contains("窗")) return Array.FindLastIndex(OrderedTypes, x => x.Contains("窗"));
            return OrderedTypes.Length;
        }

        public static List<DoorWindowScheduleItem> Sort(IEnumerable<DoorWindowScheduleItem> items)
        {
            return (items ?? Enumerable.Empty<DoorWindowScheduleItem>())
                .Where(x => x != null)
                .OrderBy(x => TypeRank(x.ElevationType))
                .ThenBy(x => x.ElevationType ?? string.Empty, StringComparer.CurrentCulture)
                .ThenBy(x => x.Sequence)
                .ThenBy(x => x.Code ?? string.Empty, StringComparer.CurrentCulture)
                .ToList();
        }

        public static void Renumber(IList<DoorWindowScheduleItem> items)
        {
            if (items == null) return;
            for (var index = 0; index < items.Count; index++) items[index].Sequence = index + 1;
        }
    }

    public sealed class DoorWindowScheduleItem
    {
        public bool Selected { get; set; } = true;
        public int Sequence { get; set; }
        public string Code { get; set; }
        public string SourceCategory { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Quantity { get; set; }
        /// <summary>开启按楼层统计时，各楼层/标准层的单层数量、层数与合计。</summary>
        public List<DoorWindowFloorQuantity> FloorQuantities { get; private set; } = new List<DoorWindowFloorQuantity>();
        public string FloorQuantitySummary
        {
            get { return string.Join("；", FloorQuantities.Select(x => x.FloorName + "=" + x.DisplayText)); }
        }
        public string SourceNote { get; set; }
        public string Material { get; set; } = "无";
        public string AtlasName { get; set; }
        public string Remarks { get; set; }
        public double SillHeight { get; set; }
        /// <summary>用户把离地高度设为"—"时置 true，表示不标注离地高度。</summary>
        public bool SillHeightSuppressed { get; set; }
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public bool HasInstallationGap { get; set; } = true;
        public double InstallationGap { get; set; } = 20d;
        public bool HasOuterFrame { get; set; } = true;
        public double OuterFrameWidth { get; set; } = 50d;
        public bool HasMullion { get; set; } = true;
        public double MullionWidth { get; set; } = 50d;
        public string DoorFrameType { get; set; } = "N型";
        public double DoorFrameWidth { get; set; } = 50d;
        public string DoorFrameWidthDisplay
        {
            get { return DoorFrameWidth <= 0d ? "无" : DoorFrameWidth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); }
            set
            {
                var text = (value ?? string.Empty).Trim();
                if (text == "无" || text == "-" || text == "--" || text == "0") { DoorFrameWidth = 0d; return; }
                double parsed;
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed)) DoorFrameWidth = Math.Max(0d, parsed);
            }
        }
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
        /// <summary>凸窗左、右转折面的做法（墙/窗）及其实际进深，单位 mm。</summary>
        public string BayLeftSide { get; set; } = "墙";
        public string BayRightSide { get; set; } = "墙";
        public double BayLeftDepth { get; set; } = 600d;
        public double BayRightDepth { get; set; } = 600d;
        public string BayLeftCellLayout { get; set; }
        public string BayRightCellLayout { get; set; }
        public string Status { get; set; }
        public int SourceRow { get; set; }
        /// <summary>排版时锁定到第几页（1 起）；0 表示未锁定，按流式排版自动分页。</summary>
        public int LockedPage { get; set; }

        public string SizeText { get { return Width > 0 && Height > 0 ? Width.ToString("0.##") + " × " + Height.ToString("0.##") : "未识别"; } }
        public string FrameSizeText { get { var gap = HasInstallationGap ? InstallationGap : 0d; return Width > gap * 2 && Height > gap * 2 ? (Width - gap * 2).ToString("0.##") + " × " + (Height - gap * 2).ToString("0.##") : "—"; } }

        /// <summary>离地高度列显示值：非窗或用户选择"—"时显示"—"；否则显示数值。</summary>
        public string SillHeightDisplay
        {
            get
            {
                if (SillHeightSuppressed || string.IsNullOrEmpty(ElevationType) || !ElevationType.Contains("窗")) return "—";
                return SillHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
            set
            {
                var text = (value ?? string.Empty).Trim();
                if (text == "—" || text == "-" || text == "--" || text.Length == 0) { SillHeightSuppressed = true; return; }
                double parsed;
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                { SillHeight = Math.Max(0d, parsed); SillHeightSuppressed = false; }
            }
        }
    }

    public sealed class DoorWindowFloorQuantity
    {
        public string FloorName { get; set; }
        public int PerFloorQuantity { get; set; }
        public int FloorCount { get; set; } = 1;
        public int TotalQuantity { get { return Math.Max(0, PerFloorQuantity) * Math.Max(1, FloorCount); } }
        public string DisplayText
        {
            get { return FloorCount > 1 ? PerFloorQuantity + "×" + FloorCount + "=" + TotalQuantity : PerFloorQuantity.ToString(); }
        }
    }

    public sealed class DoorWindowScheduleReadResult
    {
        public DoorWindowScheduleReadResult() { Items = new List<DoorWindowScheduleItem>(); RawRows = new List<List<string>>(); FloorColumns = new List<DoorWindowFloorColumn>(); }
        public ObjectId SourceId { get; set; } = ObjectId.Null;
        public string SourceHandle { get; set; }
        public string SourceDxfName { get; set; }
        public string SourceClassName { get; set; }
        public string Adapter { get; set; }
        public string Diagnostic { get; set; }
        public bool HasExtents { get; set; }
        public Point3d MinPoint { get; set; }
        public Point3d MaxPoint { get; set; }
        public List<DoorWindowScheduleItem> Items { get; private set; }
        public List<List<string>> RawRows { get; private set; }
        public List<DoorWindowFloorColumn> FloorColumns { get; private set; }
        public bool HasFloorStatistics { get { return FloorColumns != null && FloorColumns.Count > 0; } }
    }

    public sealed class DoorWindowFloorColumn
    {
        public string FloorName { get; set; }
        public int ColumnIndex { get; set; }
        public int FloorCount { get; set; } = 1;
    }

    public sealed class DoorWindowElevationPreference
    {
        public string ProjectName { get; set; }
        public string Code { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public bool HasInstallationGap { get; set; } = true;
        public double InstallationGap { get; set; } = 20d;
        public bool HasOuterFrame { get; set; } = true;
        public double OuterFrameWidth { get; set; } = 50d;
        public bool HasMullion { get; set; } = true;
        public double MullionWidth { get; set; } = 50d;
        public string DoorFrameType { get; set; } = "N型";
        public double DoorFrameWidth { get; set; } = 50d;
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
        public string BayLeftSide { get; set; } = "墙";
        public string BayRightSide { get; set; } = "墙";
        public double BayLeftDepth { get; set; } = 600d;
        public double BayRightDepth { get; set; } = 600d;
        public string BayLeftCellLayout { get; set; }
        public string BayRightCellLayout { get; set; }
        public string Material { get; set; }
        public string AtlasName { get; set; }
        public string Remarks { get; set; }
        public double SillHeight { get; set; }
        public bool HasSillHeight { get; set; }
        public bool SillHeightSuppressed { get; set; }
    }

    public sealed class DoorWindowElevationSession
    {
        public string ProjectName { get; set; }
        public bool FloorStatistics { get; set; }
        public string BaseSourceHandle { get; set; }
        public List<DoorWindowScheduleItem> BaseItems { get; set; } = new List<DoorWindowScheduleItem>();
        public List<DoorWindowFloorSourcePreference> FloorSources { get; set; } = new List<DoorWindowFloorSourcePreference>();
    }

    public sealed class DoorWindowFloorSourcePreference
    {
        public string FloorName { get; set; }
        public int FloorCount { get; set; } = 1;
        public string SourceHandle { get; set; }
        public List<DoorWindowScheduleItem> Items { get; set; } = new List<DoorWindowScheduleItem>();
    }

    public sealed class DoorWindowElevationTemplate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public bool HasInstallationGap { get; set; } = true;
        public double InstallationGap { get; set; } = 20d;
        public bool HasOuterFrame { get; set; } = true;
        public double OuterFrameWidth { get; set; } = 50d;
        public bool HasMullion { get; set; } = true;
        public double MullionWidth { get; set; } = 50d;
        public string DoorFrameType { get; set; } = "N型";
        public double DoorFrameWidth { get; set; } = 50d;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
        public string BayLeftSide { get; set; } = "墙";
        public string BayRightSide { get; set; } = "墙";
        public double BayLeftDepth { get; set; } = 600d;
        public double BayRightDepth { get; set; } = 600d;
        public string BayLeftCellLayout { get; set; }
        public string BayRightCellLayout { get; set; }
        public DateTime UpdatedAt { get; set; }

        public void ApplyTo(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            item.ElevationType = ElevationType;
            item.DivisionPreset = DivisionPreset;
            item.OpeningMode = OpeningMode == "推拉" ? "右推拉" : OpeningMode;
            item.HasInstallationGap = HasInstallationGap;
            item.InstallationGap = InstallationGap;
            item.HasOuterFrame = HasOuterFrame;
            item.OuterFrameWidth = OuterFrameWidth;
            item.HasMullion = HasMullion;
            item.MullionWidth = MullionWidth;
            item.DoorFrameType = string.IsNullOrWhiteSpace(DoorFrameType) ? "N型" : DoorFrameType;
            item.DoorFrameWidth = Math.Max(0d, DoorFrameWidth);
            item.CustomColumnRatios = CustomColumnRatios;
            item.CustomRowRatios = CustomRowRatios;
            item.CustomColumnWidths = CustomColumnWidths;
            item.CustomRowHeights = CustomRowHeights;
            var layout = DoorWindowElevationGeometryBuilder.ParseCellLayout(CustomCellLayout);
            if (layout.Count > 0)
            {
                var oldWidth = 0d; var oldHeight = 0d; foreach (var cell in layout) { oldWidth = Math.Max(oldWidth, cell.Right); oldHeight = Math.Max(oldHeight, cell.Top); }
                var gap = HasInstallationGap ? InstallationGap : 0d; var newWidth = item.Width - gap * 2d; var newHeight = item.Height - gap * 2d;
                if (oldWidth > 0 && oldHeight > 0 && newWidth > 0 && newHeight > 0)
                    foreach (var cell in layout) { cell.Left *= newWidth / oldWidth; cell.Right *= newWidth / oldWidth; cell.Bottom *= newHeight / oldHeight; cell.Top *= newHeight / oldHeight; }
                item.CustomCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(layout);
            }
            else item.CustomCellLayout = CustomCellLayout;
            item.CellOpeningModes = CellOpeningModes;
            item.DoorPlacement = DoorPlacement;
            item.DoorEdgeDistance = DoorEdgeDistance;
            item.BayLeftSide = BayLeftSide;
            item.BayRightSide = BayRightSide;
            item.BayLeftDepth = BayLeftDepth;
            item.BayRightDepth = BayRightDepth;
            item.BayLeftCellLayout = ScaleBayLayout(BayLeftCellLayout, BayLeftDepth, item.BayLeftDepth, item.Height);
            item.BayRightCellLayout = ScaleBayLayout(BayRightCellLayout, BayRightDepth, item.BayRightDepth, item.Height);
        }

        public static DoorWindowElevationTemplate FromItem(string name, DoorWindowScheduleItem item)
        {
            return new DoorWindowElevationTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                ElevationType = item.ElevationType,
                DivisionPreset = item.DivisionPreset,
                OpeningMode = item.OpeningMode,
                HasInstallationGap = item.HasInstallationGap,
                InstallationGap = item.InstallationGap,
                HasOuterFrame = item.HasOuterFrame,
                OuterFrameWidth = item.OuterFrameWidth,
                HasMullion = item.HasMullion,
                MullionWidth = item.MullionWidth,
                DoorFrameType = item.DoorFrameType,
                DoorFrameWidth = item.DoorFrameWidth,
                CustomColumnRatios = item.CustomColumnRatios,
                CustomRowRatios = item.CustomRowRatios,
                CustomColumnWidths = item.CustomColumnWidths,
                CustomRowHeights = item.CustomRowHeights,
                CustomCellLayout = item.CustomCellLayout,
                CellOpeningModes = item.CellOpeningModes,
                DoorPlacement = item.DoorPlacement,
                DoorEdgeDistance = item.DoorEdgeDistance,
                BayLeftSide = item.BayLeftSide,
                BayRightSide = item.BayRightSide,
                BayLeftDepth = item.BayLeftDepth,
                BayRightDepth = item.BayRightDepth,
                BayLeftCellLayout = item.BayLeftCellLayout,
                BayRightCellLayout = item.BayRightCellLayout,
                UpdatedAt = DateTime.Now
            };
        }

        private static string ScaleBayLayout(string value, double oldWidth, double newWidth, double height)
        {
            var cells = DoorWindowElevationGeometryBuilder.ParseCellLayout(value);
            if (cells.Count == 0) return value;
            var sourceWidth = oldWidth > 0d ? oldWidth : cells.Max(x => x.Right);
            var sourceHeight = cells.Max(x => x.Top);
            if (sourceWidth <= 0d || sourceHeight <= 0d || newWidth <= 0d || height <= 0d) return value;
            foreach (var cell in cells)
            {
                cell.Left *= newWidth / sourceWidth; cell.Right *= newWidth / sourceWidth;
                cell.Bottom *= height / sourceHeight; cell.Top *= height / sourceHeight;
            }
            return DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells);
        }

        public override string ToString()
        {
            return (Name ?? "未命名模板") + "  ·  " + (ElevationType ?? "—") + " / " + (DivisionPreset ?? "—") + " / " + (OpeningMode ?? "—");
        }
    }
}
