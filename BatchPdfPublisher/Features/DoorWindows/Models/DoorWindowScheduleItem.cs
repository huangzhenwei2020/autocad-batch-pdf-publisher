using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Models
{
    public sealed class DoorWindowScheduleItem
    {
        public bool Selected { get; set; } = true;
        /// <summary>勾选后除普通立面外，再生成一份带消防救援窗口标识和说明的立面。</summary>
        public bool GenerateFireRescueElevation { get; set; }
        /// <summary>仅供本次排版/插入识别展开后的消防版本，不写入用户配置。</summary>
        internal bool IsFireRescueVariant;
        public int Sequence { get; set; }
        public string Code { get; set; }
        public string CadRegistrationCode { get; set; }
        public double? CadRegistrationSill { get; set; }
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
        /// <summary>区分用户在下拉框中的明确选择和旧版本自动推断值。</summary>
        public bool AtlasNameExplicitlySelected { get; set; }
        public string Remarks { get; set; }
        public double SillHeight { get; set; }
        public bool SillHeightFromCadRegistration { get; set; }
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
}
