using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.BuildingModel
{
    // Diagnostic protocol only: readable candidate properties are NOT a verified
    // semantic mapping and this snapshot must never directly create model walls.
    public sealed class CadBuildingProbeDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string Purpose { get; set; } = "天正构件只读核验；几何与对照类别不是模型导入映射";
        public string CapturedAt { get; set; }
        public string DrawingPath { get; set; }
        public string DrawingFingerprint { get; set; }
        public string CadVersion { get; set; }
        public string DrawingUnits { get; set; }
        public string TianzhengEnvironment { get; set; }
        public int? DbmodBefore { get; set; }
        public int? DbmodAfter { get; set; }
        public List<CadBuildingProbeEntity> Entities { get; set; } = new List<CadBuildingProbeEntity>();
        public List<CadRoomLabel> RoomLabels { get; set; } = new List<CadRoomLabel>();
    }

    public sealed class CadRoomLabel
    {
        public string SourceHandle { get; set; }
        public string Name { get; set; }
        public PointModel Position { get; set; }
        public double? AreaSquareMetres { get; set; }
    }

    public sealed class CadProbePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class CadBuildingProbeEntity
    {
        public string Handle { get; set; }
        public string DxfName { get; set; }
        public string RxClassName { get; set; }
        public string ManagedType { get; set; }
        public string ComType { get; set; }
        public string Layer { get; set; }
        public string Category { get; set; }
        public string Note { get; set; }
        /// <summary>User-supplied comparison label only; never an import classification.</summary>
        public string ReferenceKind { get; set; }
        public string GeometryStatus { get; set; }
        public int UnsupportedGeometry { get; set; }
        public bool GeometryTruncated { get; set; }
        public List<CadProbeGeometryPath> GeometryPaths { get; set; } = new List<CadProbeGeometryPath>();
        public string CurveStatus { get; set; }
        public CadProbePoint CurveStart { get; set; }
        public CadProbePoint CurveMidpoint { get; set; }
        public CadProbePoint CurveEnd { get; set; }
        public CadProbePoint BoundsMin { get; set; }
        public CadProbePoint BoundsMax { get; set; }
        public bool StraightHorizontalLineVerified { get; set; }
        public bool CircularHorizontalArcVerified { get; set; }
        public double CurveBulge { get; set; }
        public List<CadProbePoint> CurveSamples { get; set; } = new List<CadProbePoint>();
        public double? CurveLength { get; set; }
        public List<CadProbeSegment> DisplaySegments { get; set; } = new List<CadProbeSegment>();
        public List<CadProbeArc> DisplayArcs { get; set; } = new List<CadProbeArc>();
        // Verified closed, straight native rectangle; never inferred from extents.
        public List<PointModel> StructuralOutline { get; set; } = new List<PointModel>();
        public List<CadProbeField> Fields { get; set; } = new List<CadProbeField>();
        public double? CandidateThickness { get; set; }
        public double? CandidateAxisOffset { get; set; }
        public string Caption { get { return Category + " · " + Handle + " · " + DxfName; } }
    }

    /// <summary>Read-only diagnostic geometry. Samples are for preview, not model contours.</summary>
    public sealed class CadProbeGeometryPath
    {
        public string Kind { get; set; }
        public string SourcePath { get; set; }
        public bool Closed { get; set; }
        public List<CadProbePoint> Vertices { get; set; } = new List<CadProbePoint>();
        public List<double> Bulges { get; set; } = new List<double>();
        public CadProbePoint Normal { get; set; }
        /// <summary>Local-to-drawing matrix; raw vertices/bulges/normals remain local.</summary>
        public List<double> SourceTransform { get; set; } = new List<double>();
        public List<CadProbePoint> PreviewPoints { get; set; } = new List<CadProbePoint>();
    }

    public sealed class CadProbeSegment
    {
        public CadProbePoint Start { get; set; }
        public CadProbePoint End { get; set; }
    }
    public sealed class CadProbeArc
    {
        public CadProbePoint Center { get; set; }
        public CadProbePoint Start { get; set; }
        public CadProbePoint End { get; set; }
        public double Radius { get; set; }
        public double Sweep { get; set; }
    }

    public sealed class CadProbeField
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Text { get; set; }
        public double? Number { get; set; }
        public List<double> Numbers { get; set; }
        public string Error { get; set; }
        public string Unit { get; set; } = "原始单位/含义待实图核验";
    }

    public static class CadBuildingProbeRules
    {
        // T20 V9 native samples and a disposable TBalcony fixture checked these
        // scalar getters. A typelib declaration alone is not sufficient evidence.
        public static string[] VerifiedProperties(string dxf)
        {
            if (string.Equals(dxf, "TCH_WALL", StringComparison.OrdinalIgnoreCase))
                return new[] { "ObjectName", "LeftWidth", "RightWidth", "Height", "Elevation" };
            if (string.Equals(dxf, "TCH_OPENING", StringComparison.OrdinalIgnoreCase))
                return new[] { "ObjectName", "Width", "Height" };
            if (string.Equals(dxf, "TCH_BALCONY", StringComparison.OrdinalIgnoreCase))
                return new[] { "ObjectName", "Elevation", "RailWidth", "RailHeight", "SlabThickness", "GirderThickness", "HasGirder" };
            return new string[0];
        }

        public static bool VerifyCircularWall(CadBuildingProbeEntity entity)
        {
            if(entity==null)return false;
            entity.CircularHorizontalArcVerified=false;entity.CurveBulge=0;
            var samples=entity.CurveSamples;
            if(!IsWall(entity.DxfName) || entity.StraightHorizontalLineVerified || samples==null || samples.Count!=9
                || entity.CurveStart==null || entity.CurveEnd==null || !entity.CurveLength.HasValue
                || !CurveGeometry.Finite(entity.CurveLength.Value) || entity.CurveLength.Value<=0
                || samples.Concat(new[]{entity.CurveStart,entity.CurveEnd}).Any(p=>p==null
                    || !CurveGeometry.Finite(p.X) || !CurveGeometry.Finite(p.Y) || !CurveGeometry.Finite(p.Z)))return false;
            var tolerance=Math.Max(1e-8,entity.CurveLength.Value*1e-8);
            try
            {
                var first=samples[0];var last=samples[8];
                if(Distance(first,entity.CurveStart)>tolerance || Distance(last,entity.CurveEnd)>tolerance
                    || samples.Any(p=>Math.Abs(p.Z-first.Z)>tolerance))return false;
                var a=new PointModel(first.X,first.Y);var b=new PointModel(last.X,last.Y);
                var bulge=CurveGeometry.ThroughBulge(a,b,new PointModel(samples[4].X,samples[4].Y));
                var arc=new CurveGeometry(a,b,bulge);
                if(!arc.IsArc || Math.Abs(arc.Length-entity.CurveLength.Value)>tolerance*4)return false;
                // Native samples are taken at equal arc-length stations, not arbitrary curve parameters.
                for(var i=0;i<samples.Count;i++)
                {
                    var expected=arc.At(i/8d);var p=samples[i];
                    if(Math.Sqrt((p.X-expected.X)*(p.X-expected.X)+(p.Y-expected.Y)*(p.Y-expected.Y))>tolerance*4)return false;
                }
                entity.CurveBulge=bulge;entity.CircularHorizontalArcVerified=true;
                entity.CurveStatus="原生水平圆弧定位已核验";
                return true;
            }
            catch(ArgumentException) { return false; }
        }
        private static double Distance(CadProbePoint a,CadProbePoint b)
        {
            if(a==null || b==null)return double.PositiveInfinity;
            return Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y)+(a.Z-b.Z)*(a.Z-b.Z));
        }

        public static bool IsWall(string dxf) => string.Equals(dxf,"TCH_WALL",StringComparison.OrdinalIgnoreCase)
            || string.Equals(dxf,"TCH_CURTAIN_WALL",StringComparison.OrdinalIgnoreCase);
        public static string Classify(string dxf, string com)
        {
            if (string.Equals(dxf, "TCH_BALCONY", StringComparison.OrdinalIgnoreCase)) return "天正阳台候选（做法待核验）";
            if (string.Equals(dxf, "TCH_APRON", StringComparison.OrdinalIgnoreCase)) return "天正散水候选（参数待核验）";
            var identities = new[] { dxf ?? "", com ?? "" };
            if(IsWall(dxf) && string.Equals(dxf,"TCH_CURTAIN_WALL",StringComparison.OrdinalIgnoreCase))return "天正幕墙";
            if (identities.Any(x => x.Equals("TCH_WALL", StringComparison.OrdinalIgnoreCase)
                || x.Equals("TDbWall", StringComparison.OrdinalIgnoreCase))) return "天正墙候选";
            if (identities.Any(x => x.Equals("TCH_OPENING", StringComparison.OrdinalIgnoreCase)
                || x.Equals("TDbOpening", StringComparison.OrdinalIgnoreCase))) return "天正洞口候选（门/窗待核验）";
            return "未适配对象";
        }

        public static CadProbeField Field(string name, bool readable, object value, string error)
        {
            var result = new CadProbeField { Name = name, Error = error };
            if (!readable) { result.Status = "不可读"; result.Text = "—"; return result; }
            if (value == null) { result.Status = "空值"; result.Text = "—"; return result; }
            if (value is string || value is bool)
            {
                result.Status = "已读取（待核验）";
                result.Text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (result.Text.Length > 2048) result.Text = result.Text.Substring(0, 2048) + "…";
                return result;
            }
            double number;
            if (TryNumber(value, out number))
            {
                result.Status = "已读取（待核验）"; result.Number = number;
                result.Text = number.ToString("R", CultureInfo.InvariantCulture); return result;
            }
            var array = value as Array;
            if (array != null && array.Rank == 1 && array.Length > 0 && array.Length <= 16)
            {
                var numbers = new List<double>();
                foreach (var item in array)
                {
                    if (!TryNumber(item, out number)) { numbers.Clear(); break; }
                    numbers.Add(number);
                }
                if (numbers.Count == array.Length)
                {
                    result.Status = "已读取（待核验）"; result.Numbers = numbers;
                    result.Text = string.Join(", ", numbers.Select(n => n.ToString("R", CultureInfo.InvariantCulture)));
                    return result;
                }
            }
            result.Status = "值类型未适配";
            result.Text = value.GetType().FullName;
            result.Error = "不是有限数值、字符串、布尔值或有限数值数组；未猜测转换。";
            return result;
        }

        public static void DeriveCurtainWall(CadBuildingProbeEntity entity,double scale)
        {
            entity.CandidateThickness=null;entity.CandidateAxisOffset=null;
            if(entity.DxfName!="TCH_CURTAIN_WALL" || !entity.StraightHorizontalLineVerified || entity.CurveStart==null || entity.CurveEnd==null
                || entity.BoundsMin==null || entity.BoundsMax==null || scale<=0 || double.IsNaN(scale) || double.IsInfinity(scale))return;
            var a=entity.CurveStart;var b=entity.CurveEnd;var min=entity.BoundsMin;var max=entity.BoundsMax;
            if(new[] { a.X,a.Y,a.Z,b.X,b.Y,b.Z,min.X,min.Y,min.Z,max.X,max.Y,max.Z }.Any(v=>double.IsNaN(v) || double.IsInfinity(v)))return;
            var dx=b.X-a.X;var dy=b.Y-a.Y;var length=Math.Sqrt(dx*dx+dy*dy);
            if(length*scale<1 || Math.Abs(a.Z-b.Z)*scale>.5)return;
            dx/=length;dy/=length;
            // Native axis-aligned curtain walls expose actual 3D body extents.
            // For a rotated or curved body an AABB cannot give its thickness.
            if(Math.Min(Math.Abs(dx),Math.Abs(dy))*length*scale>.25)return;
            var corners=new[] { new PointModel(min.X,min.Y),new PointModel(max.X,min.Y),new PointModel(max.X,max.Y),new PointModel(min.X,max.Y) };
            var along=corners.Select(p=>(p.X-a.X)*dx+(p.Y-a.Y)*dy).ToArray();
            var across=corners.Select(p=>-(p.X-a.X)*dy+(p.Y-a.Y)*dx).ToArray();
            var thickness=across.Max()-across.Min();var height=max.Z-min.Z;
            if(Math.Abs(along.Min())*scale>.5 || Math.Abs(along.Max()-length)*scale>.5
                || thickness*scale<1 || thickness*scale>2000 || height*scale<1 || min.Z>a.Z+.5/scale || max.Z<=a.Z)return;
            entity.CandidateThickness=thickness;entity.CandidateAxisOffset=(across.Min()+across.Max())/2;
            entity.Fields.RemoveAll(f=>f.Name=="Height" || f.Name=="Elevation");
            entity.Fields.Add(Field("Height",true,height,null));entity.Fields.Add(Field("Elevation",true,min.Z-a.Z,null));
            entity.Note="幕墙原生直线定位、三维墙体范围与高度；不读取未经验证的幕墙属性。";
        }
        public static void DeriveWall(CadBuildingProbeEntity entity)
        {
            entity.CandidateThickness = null;
            entity.CandidateAxisOffset = null;
            if (entity.Category != "天正墙候选") return;
            var left = entity.Fields.FirstOrDefault(f => f.Name == "LeftWidth");
            var right = entity.Fields.FirstOrDefault(f => f.Name == "RightWidth");
            if (left == null || right == null || !left.Number.HasValue || !right.Number.HasValue
                || left.Number.Value < 0 || right.Number.Value < 0) return;
            var total = left.Number.Value + right.Number.Value;
            if (total <= 0 || double.IsInfinity(total) || double.IsNaN(total)) return;
            entity.CandidateThickness = total;
            entity.CandidateAxisOffset = (left.Number.Value - right.Number.Value) / 2d;
        }

        private static bool TryNumber(object value, out double number)
        {
            number = 0;
            if (!(value is byte || value is sbyte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong
                || value is float || value is double || value is decimal)) return false;
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }
    }

    public static class CadBuildingProbeFile
    {
        public static void Save(string path, CadBuildingProbeDocument document)
        {
            path = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(CadBuildingProbeDocument)).WriteObject(stream, document);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static CadBuildingProbeDocument Load(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var document = (CadBuildingProbeDocument)new DataContractJsonSerializer(typeof(CadBuildingProbeDocument)).ReadObject(stream);
                if (document == null || document.SchemaVersion != 1 || document.Entities == null)
                    throw new InvalidDataException("不支持的天正核验文件版本或内容。");
                return document;
            }
        }
    }
}
