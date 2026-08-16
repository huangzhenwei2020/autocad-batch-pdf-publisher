using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System.Collections.Generic;
using System;

namespace BatchPdfPublisher.Models
{
    public sealed class DoorWindowScheduleItem
    {
        public bool Selected { get; set; } = true;
        public int Sequence { get; set; }
        public string Code { get; set; }
        public string SourceCategory { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Quantity { get; set; }
        public string SourceNote { get; set; }
        public string Material { get; set; } = "无";
        public string AtlasName { get; set; }
        public string Remarks { get; set; }
        public double SillHeight { get; set; }
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
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
        public string Status { get; set; }
        public int SourceRow { get; set; }

        public string SizeText { get { return Width > 0 && Height > 0 ? Width.ToString("0.##") + " × " + Height.ToString("0.##") : "未识别"; } }
        public string FrameSizeText { get { var gap = HasInstallationGap ? InstallationGap : 0d; return Width > gap * 2 && Height > gap * 2 ? (Width - gap * 2).ToString("0.##") + " × " + (Height - gap * 2).ToString("0.##") : "—"; } }
    }

    public sealed class DoorWindowScheduleReadResult
    {
        public DoorWindowScheduleReadResult() { Items = new List<DoorWindowScheduleItem>(); RawRows = new List<List<string>>(); }
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
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
        public string Material { get; set; }
        public string AtlasName { get; set; }
        public string Remarks { get; set; }
        public double SillHeight { get; set; }
        public bool HasSillHeight { get; set; }
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
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CustomColumnWidths { get; set; }
        public string CustomRowHeights { get; set; }
        public string CustomCellLayout { get; set; }
        public string CellOpeningModes { get; set; }
        public string DoorPlacement { get; set; } = "靠左";
        public double DoorEdgeDistance { get; set; }
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
                CustomColumnRatios = item.CustomColumnRatios,
                CustomRowRatios = item.CustomRowRatios,
                CustomColumnWidths = item.CustomColumnWidths,
                CustomRowHeights = item.CustomRowHeights,
                CustomCellLayout = item.CustomCellLayout,
                CellOpeningModes = item.CellOpeningModes,
                DoorPlacement = item.DoorPlacement,
                DoorEdgeDistance = item.DoorEdgeDistance,
                UpdatedAt = DateTime.Now
            };
        }

        public override string ToString()
        {
            return (Name ?? "未命名模板") + "  ·  " + (ElevationType ?? "—") + " / " + (DivisionPreset ?? "—") + " / " + (OpeningMode ?? "—");
        }
    }
}
