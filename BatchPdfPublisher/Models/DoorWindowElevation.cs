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
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public double InstallationGap { get; set; } = 20d;
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CellOpeningModes { get; set; }
        public string Status { get; set; }
        public int SourceRow { get; set; }

        public string SizeText { get { return Width > 0 && Height > 0 ? Width.ToString("0.##") + " × " + Height.ToString("0.##") : "未识别"; } }
        public string FrameSizeText { get { return Width > InstallationGap * 2 && Height > InstallationGap * 2 ? (Width - InstallationGap * 2).ToString("0.##") + " × " + (Height - InstallationGap * 2).ToString("0.##") : "—"; } }
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
        public double InstallationGap { get; set; } = 20d;
        public int DrawingScale { get; set; } = 50;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CellOpeningModes { get; set; }
    }

    public sealed class DoorWindowElevationTemplate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ElevationType { get; set; }
        public string DivisionPreset { get; set; }
        public string OpeningMode { get; set; }
        public double InstallationGap { get; set; } = 20d;
        public string CustomColumnRatios { get; set; }
        public string CustomRowRatios { get; set; }
        public string CellOpeningModes { get; set; }
        public DateTime UpdatedAt { get; set; }

        public void ApplyTo(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            item.ElevationType = ElevationType;
            item.DivisionPreset = DivisionPreset;
            item.OpeningMode = OpeningMode;
            item.InstallationGap = InstallationGap;
            item.CustomColumnRatios = CustomColumnRatios;
            item.CustomRowRatios = CustomRowRatios;
            item.CellOpeningModes = CellOpeningModes;
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
                InstallationGap = item.InstallationGap,
                CustomColumnRatios = item.CustomColumnRatios,
                CustomRowRatios = item.CustomRowRatios,
                CellOpeningModes = item.CellOpeningModes,
                UpdatedAt = DateTime.Now
            };
        }

        public override string ToString()
        {
            return (Name ?? "未命名模板") + "  ·  " + (ElevationType ?? "—") + " / " + (DivisionPreset ?? "—") + " / " + (OpeningMode ?? "—");
        }
    }
}
