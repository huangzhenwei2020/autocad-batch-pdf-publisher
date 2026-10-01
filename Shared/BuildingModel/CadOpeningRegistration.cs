using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.BuildingModel
{
    // A reviewed parameter queue, deliberately distinct from model.json. Placement
    // and host identity must be verified before these records can create openings.
    public sealed class CadOpeningRegistrationDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string Purpose { get; set; } = "门窗参数登记核对；定位和宿主待登记，不能直接作为模型打开";
        public string DrawingFingerprint { get; set; }
        public string DrawingPath { get; set; }
        public string ProjectName { get; set; }
        public string ReviewedAt { get; set; }
        public string Unit { get; set; } = "mm";
        public List<CadOpeningRegistrationRecord> Openings { get; set; } = new List<CadOpeningRegistrationRecord>();
        public CadFloorRegistrationContext Floor { get; set; }
    }

    public sealed class CadOpeningRegistrationRecord
    {
        public string SourceHandle { get; set; }
        public string Code { get; set; }
        public string Kind { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Sill { get; set; }
        public string SillSource { get; set; }
        public OpeningTypeModel ElevationParameters { get; set; }
        public bool PlacementVerified { get; set; }
        public CadOpeningPlacement Placement { get; set; }
        public CadBuildingProbeEntity SourceGeometry { get; set; }
    }

    public sealed class CadOpeningRegistrationRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _include, _confirmed;
        private string _sourceHandle, _code, _status, _sillSource;
        private double? _width, _height, _sill;
        private int? _typeIndex;
        public CadOpeningPlacement Placement { get; set; }
        public bool Include { get { return _include; } set { Set(ref _include, value, "Include"); } }
        public string SourceHandle { get { return _sourceHandle; } set { Set(ref _sourceHandle, value, "SourceHandle"); } }
        public string Code { get { return _code; } set { Set(ref _code, value, "Code"); } }
        public double? Width { get { return _width; } set { Set(ref _width, value, "Width"); } }
        public double? Height { get { return _height; } set { Set(ref _height, value, "Height"); } }
        // Null means unknown, not a zero-height sill.
        public double? Sill { get { return _sill; } set { Set(ref _sill, value, "Sill"); } }
        public string SillSource { get { return _sillSource; } set { Set(ref _sillSource, value, "SillSource"); } }
        public int? TypeIndex { get { return _typeIndex; } set { Set(ref _typeIndex, value, "TypeIndex"); } }
        public bool Confirmed { get { return _confirmed; } set { Set(ref _confirmed, value, "Confirmed"); } }
        public string Status { get { return _status; } set { Set(ref _status, value, "Status"); } }
        private void Set<T>(ref T field, T value, string property)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value; var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(property));
        }
    }

    public sealed class CadOpeningSillEntry
    {
        public string Code { get; set; }
        public string FloorName { get; set; }
        public double? Height { get; set; }
        public string Source { get; set; }
        public bool FromSchedule { get; set; }
        public int? TypeIndex { get; set; }
    }

    public static class CadOpeningRegistration
    {
        // A floor schedule overrides general settings. Missing/suppressed values
        // and conflicting rows must not fall back to an unrelated saved type.
        public static double? ResolveSill(IEnumerable<CadOpeningSillEntry> entries,
            string code, int typeIndex, string floorName, out string source)
        {
            var all = (entries ?? Enumerable.Empty<CadOpeningSillEntry>()).Where(e => e != null
                && string.Equals((e.Code ?? "").Trim(), (code ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var floorRows = all.Where(e => e.FromSchedule && !string.IsNullOrWhiteSpace(e.FloorName)).ToList();
            if (string.IsNullOrWhiteSpace(floorName) && floorRows.Count > 0)
            { source = "存在分楼层门窗表，请先确定登记楼层"; return null; }
            var matches = floorRows.Where(e => string.Equals(e.FloorName.Trim(), (floorName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) matches = all.Where(e => e.FromSchedule && string.IsNullOrWhiteSpace(e.FloorName)).ToList();
            if (matches.Count == 0) matches = all.Where(e => !e.FromSchedule && e.TypeIndex == typeIndex).ToList();
            if (matches.Count == 0 || matches.Any(e => !e.Height.HasValue || !Finite(e.Height.Value) || e.Height.Value < 0))
            { source = "离地高度未记录或显示为 —，须补充确认"; return null; }
            if (matches.Max(e => e.Height.Value) - matches.Min(e => e.Height.Value) > 0.5)
            { source = "同编号离地高度不一致，须核对门窗表"; return null; }
            source = string.Join("；", matches.Select(e => e.Source).Distinct());
            return matches[0].Height;
        }
        public static List<CadOpeningRegistrationRow> FromProbe(CadBuildingProbeDocument probe)
        {
            return (probe.Entities ?? new List<CadBuildingProbeEntity>())
                .Where(e => e != null && (string.Equals(e.DxfName, "TCH_OPENING", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.ComType, "TDbOpening", StringComparison.OrdinalIgnoreCase)))
                .Select(e => new CadOpeningRegistrationRow { SourceHandle = e.Handle,
                    Width = Number(e, "Width"), Height = Number(e, "Height"), Status = "待选编号和做法、确认窗台高" }).ToList();
        }

        private static double? Number(CadBuildingProbeEntity entity, string name)
        {
            var field = (entity.Fields ?? new List<CadProbeField>()).FirstOrDefault(f => f.Name == name);
            return field != null && field.Number.HasValue && Finite(field.Number.Value) ? field.Number : null;
        }

        public static List<int> Matches(OpeningTypeLibraryDocument library, string code)
        {
            var result = new List<int>();
            if (library == null || library.Types == null || string.IsNullOrWhiteSpace(code)) return result;
            for (var i = 0; i < library.Types.Count; i++)
                if (library.Types[i] != null && string.Equals(library.Types[i].Code == null ? "" : library.Types[i].Code.Trim(),
                    code.Trim(), StringComparison.OrdinalIgnoreCase)) result.Add(i);
            return result;
        }

        public static string Validate(CadOpeningRegistrationRow row, OpeningTypeLibraryDocument library)
        {
            if (row == null || !Positive(row.Width) || !Positive(row.Height)) return "洞口宽高须为有效正数（mm）";
            if (string.IsNullOrWhiteSpace(row.Code)) return "未选择门窗编号";
            var matches = Matches(library, row.Code);
            if (matches.Count == 0) return "编号没有对应立面参数，请先在 MCLM 设置";
            if (!row.TypeIndex.HasValue || !matches.Contains(row.TypeIndex.Value))
                return matches.Count > 1 ? "同编号有多条参数，须选择具体做法" : "须选择立面做法";
            var kind = library.Types[row.TypeIndex.Value].Kind;
            if (string.IsNullOrWhiteSpace(kind)) return "所选做法缺少门窗类别";
            if (!row.Sill.HasValue || !Finite(row.Sill.Value) || row.Sill.Value < 0) return "须确认距楼层的窗台/洞口底高（mm，允许 0）";
            if (!row.Confirmed) return "参数尚未确认";
            return null;
        }

        public static string SizeNote(CadOpeningRegistrationRow row, OpeningTypeModel type)
        {
            if (!Positive(row.Width) || !Positive(row.Height)) return "洞口尺寸待补充";
            return Math.Abs(row.Width.Value - type.Width) > 0.5 || Math.Abs(row.Height.Value - type.Height) > 0.5
                ? "尺寸与做法记录不同；保留 CAD 洞口宽高" : "洞口尺寸与做法记录一致";
        }

        public static CadOpeningRegistrationDocument Build(CadBuildingProbeDocument probe,
            OpeningTypeLibraryDocument library, IEnumerable<CadOpeningRegistrationRow> rows, bool millimetresConfirmed,
            CadFloorRegistrationContext floor = null)
        {
            if (!millimetresConfirmed) throw new InvalidDataException("请先核对图纸尺寸单位为毫米。");
            if (floor != null) floor.ValidateCapture();
            var selected = rows.Where(r => r.Include).ToList();
            if (selected.Count == 0 && (floor == null || floor.WallCandidates.Count == 0)) throw new InvalidDataException("请勾选需要登记的门窗，或框选含墙的楼层平面。");
            if (selected.Select(r => r.SourceHandle).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count
                || selected.Any(r => string.IsNullOrWhiteSpace(r.SourceHandle))) throw new InvalidDataException("来源对象身份重复或缺失。");
            var document = new CadOpeningRegistrationDocument { DrawingFingerprint = probe.DrawingFingerprint,
                DrawingPath = probe.DrawingPath, ProjectName = library.ProjectName, ReviewedAt = DateTimeOffset.Now.ToString("O"), Floor = floor };
            foreach (var row in selected)
            {
                var sourceObjects = probe.Entities.Where(e => e != null && string.Equals(e.Handle, row.SourceHandle, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(e.DxfName, "TCH_OPENING", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.ComType, "TDbOpening", StringComparison.OrdinalIgnoreCase))).ToList();
                if (sourceObjects.Count != 1) throw new InvalidDataException("来源洞口不在本次登记中或身份重复：" + row.SourceHandle);
                var error = Validate(row, library);
                if (error != null) throw new InvalidDataException(row.SourceHandle + "：" + error);
                var type = library.Types[row.TypeIndex.Value];
                var placement = row.Placement == null ? null : row.Placement.Revalidate(floor, row.Width.Value);
                document.Openings.Add(new CadOpeningRegistrationRecord { SourceHandle = row.SourceHandle,
                    Code = row.Code.Trim(), Kind = type.Kind, Width = row.Width.Value, Height = row.Height.Value,
                    Sill = row.Sill.Value, SillSource = row.SillSource ?? "用户输入并确认", ElevationParameters = type,
                    Placement = placement, PlacementVerified = placement != null,
                    SourceGeometry = sourceObjects[0] });
            }
            return document;
        }

        public static void Save(string path, CadOpeningRegistrationDocument document)
        {
            var full = Path.GetFullPath(path); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                using (var stream = File.Create(temporary))
                    new DataContractJsonSerializer(typeof(CadOpeningRegistrationDocument)).WriteObject(stream, document);
                if (File.Exists(full)) File.Replace(temporary, full, full + ".bak"); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool Positive(double? value) { return value.HasValue && Finite(value.Value) && value.Value > 0.5; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
