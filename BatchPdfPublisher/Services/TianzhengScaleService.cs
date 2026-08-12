using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;

namespace BatchPdfPublisher.Services
{
    public sealed class TianzhengDimensionSettings
    {
        public double OuterExtensionLength = 8d;
        public double InnerExtensionLength = 5d;
        public double DimensionSpacing = 8d;
        public double AxisLeaderLength = 40d;
        public bool ApplyDimensionGeometry = true;
        public bool ApplyAxisLeader = true;

        private static string PathName { get { return UserDataPaths.SettingsFile("tianzheng-dimension-scale.ini"); } }
        public static TianzhengDimensionSettings Load()
        {
            var x = new TianzhengDimensionSettings();
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(PathName))
                {
                    var p = line.IndexOf('='); if (p <= 0) continue; var key = line.Substring(0, p); var value = line.Substring(p + 1); double d;
                    if (key == "Outer" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) x.OuterExtensionLength = d;
                    else if (key == "Inner" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) x.InnerExtensionLength = d;
                    else if (key == "Spacing" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) x.DimensionSpacing = d;
                    else if (key == "AxisLeader" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) x.AxisLeaderLength = d;
                    else if (key == "ApplyDimension") x.ApplyDimensionGeometry = value == "1";
                    else if (key == "ApplyAxis") x.ApplyAxisLeader = value == "1";
                }
            }
            catch { }
            return x;
        }
        public void Save()
        {
            System.IO.File.WriteAllLines(PathName, new[] { "Outer=" + OuterExtensionLength.ToString(CultureInfo.InvariantCulture), "Inner=" + InnerExtensionLength.ToString(CultureInfo.InvariantCulture), "Spacing=" + DimensionSpacing.ToString(CultureInfo.InvariantCulture), "AxisLeader=" + AxisLeaderLength.ToString(CultureInfo.InvariantCulture), "ApplyDimension=" + (ApplyDimensionGeometry ? "1" : "0"), "ApplyAxis=" + (ApplyAxisLeader ? "1" : "0") });
        }
    }

    internal static class TianzhengScaleService
    {
        public sealed class DimensionGeometryTarget
        {
            public double Distance;
            public int Direction;
        }
        public static bool IsTianzhengDimension(DBObject value)
        {
            var dxf = DxfName(value);
            return dxf == "TCH_DIMENSION" || dxf == "TCH_DIMENSION2" || dxf == "TCH_RADIUSDIM" || dxf == "TCH_RADUSDIM";
        }
        public static bool IsAxisLabel(DBObject value) { return DxfName(value) == "TCH_AXIS_LABEL"; }
        public static bool IsTianzhengObject(DBObject value) { return DxfName(value).StartsWith("TCH_", StringComparison.OrdinalIgnoreCase); }

        public static bool Apply(DBObject value, int scale, TianzhengDimensionSettings settings)
        {
            if (!IsTianzhengObject(value)) return false;
            var com = value.AcadObject;
            var changed = TrySet(com, "Scale", Convert.ToDouble(scale, CultureInfo.InvariantCulture));
            if (IsTianzhengDimension(value) && settings.ApplyDimensionGeometry)
            {
                // Different T20 products expose these optional fields under
                // different names. Only write a property that actually exists.
                // ScaleFactors is intentionally not used: it describes Tianzheng's
                // internal segment factors and an invented string can corrupt a dim.
                // These COM properties are paper-space 1:1 values. Tianzheng's
                // Scale property performs the model-space multiplication itself.
                // Multiplying here again would make 40 become 400000 at 1:100.
            }
            if (IsAxisLabel(value) && settings.ApplyAxisLeader)
            {
                // The settings window stores paper-space 1:1 values. Tianzheng's
                // LeaderLen1/LeaderLen2 are also paper-space values.  Scale is
                // applied internally by Tianzheng; multiplying here once more is
                // what made the axis leader become thousands of drawing units
                // long and also displaced associated dimension source points.
                var length = settings.AxisLeaderLength;
                changed |= TrySet(com, "LeaderLen1", length);
                changed |= TrySet(com, "LeaderLen2", length);
            }
            return changed;
        }

        public static Dictionary<ObjectId, DimensionGeometryTarget> BuildDimensionGeometryPlan(Transaction transaction, ObjectId[] ids, int targetScale, TianzhengDimensionSettings settings)
        {
            var measured = new List<Tuple<ObjectId, double, int>>();
            foreach (var id in ids ?? new ObjectId[0])
            {
                try
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    double distance; int direction;
                    if (entity != null && IsTianzhengDimension(entity) && TryMeasureDimensionGeometry(entity, out distance, out direction)) measured.Add(Tuple.Create(id, distance, direction));
                }
                catch { }
            }
            var tolerance = Math.Max(0.1d, measured.Count == 0 ? 0.1d : measured.Max(x => x.Item2) * 0.02d);
            var levels = new List<double>();
            foreach (var distance in measured.Select(x => x.Item2).OrderBy(x => x))
                if (levels.Count == 0 || Math.Abs(distance - levels[levels.Count - 1]) > tolerance) levels.Add(distance);
            var result = new Dictionary<ObjectId, DimensionGeometryTarget>();
            foreach (var item in measured)
            {
                var level = 0; var best = double.MaxValue;
                for (var i = 0; i < levels.Count; i++) { var delta = Math.Abs(item.Item2 - levels[i]); if (delta < best) { best = delta; level = i; } }
                result[item.Item1] = new DimensionGeometryTarget { Distance = Math.Max(0d, settings.InnerExtensionLength + level * settings.DimensionSpacing) * targetScale, Direction = item.Item3 };
            }
            return result;
        }

        public static bool ApplyCommittedDimensionGeometry(Entity entity, DimensionGeometryTarget target, out string error)
        {
            error = string.Empty;
            if (entity == null || !IsTianzhengDimension(entity) || target == null) return false;
            try { return ApplyDimensionGripGeometry(entity, target.Distance, target.Direction); }
            catch (System.Exception exception) { error = exception.Message; return false; }
        }

        public static bool IsLoaded()
        {
            return GetModuleHandle("tch_kernal.arx") != IntPtr.Zero || GetModuleHandle("tch_initstart.arx") != IntPtr.Zero;
        }

        public static bool TryGetCurrentScale(out int scale)
        {
            scale = 0;
            try
            {
                var module = GetModuleHandle("tch_kernal.arx");
                if (module == IntPtr.Zero) return false;
                var address = GetProcAddress(module, "?DocGetPScale@@YANXZ");
                if (address == IntPtr.Zero) return false;
                var getter = (GetPaperScale)Marshal.GetDelegateForFunctionPointer(address, typeof(GetPaperScale));
                var value = getter();
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d) return false;
                scale = Math.Max(1, (int)Math.Round(value));
                return true;
            }
            catch (System.Exception exception)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " grip geometry failed: " + exception + Environment.NewLine); } catch { }
                return false;
            }
        }

        private static bool TryMeasureDimensionGeometry(Entity entity, out double distance, out int direction)
        {
            distance = 0d; direction = 1;
            try
            {
                var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                entity.GetGripPoints(grips, osnap, geometry);
                if (grips.Count < 4) return false;
                var tangent = grips[1] - grips[0]; if (tangent.Length < 1e-8) return false; tangent = tangent.GetNormal();
                var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
                Func<Point3d, double> project = p => p.X * normal.X + p.Y * normal.Y + p.Z * normal.Z;
                var dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                var sourceCoordinate = (project(grips[2]) + project(grips[3])) * 0.5d;
                distance = Math.Abs(sourceCoordinate - dimensionCoordinate);
                direction = Math.Sign(dimensionCoordinate - sourceCoordinate); if (direction == 0) direction = 1;
                return distance > 1e-8;
            }
            catch { return false; }
        }

        private static bool ApplyDimensionGripGeometry(Entity entity, double targetDistance, int originalDirection)
        {
            if (entity == null) return false;
            try
            {
                var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                entity.GetGripPoints(grips, osnap, geometry);
                if (grips.Count < 4) return false;
                var tangent = grips[1] - grips[0];
                if (tangent.Length < 1e-8) return false;
                tangent = tangent.GetNormal();
                var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
                Func<Point3d, double> project = p => p.X * normal.X + p.Y * normal.Y + p.Z * normal.Z;
                var dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                var source = (project(grips[2]) + project(grips[3])) * 0.5d;
                var direction = originalDirection == 0 ? Math.Sign(dimensionCoordinate - source) : originalDirection;
                if (direction == 0) direction = 1;
                var desired = source + direction * Math.Max(0d, targetDistance);
                var delta = desired - dimensionCoordinate;
                if (Math.Abs(delta) <= Math.Max(0.01d, targetDistance * 0.001d)) return false;
                var indices = new IntegerCollection(); indices.Add(0); indices.Add(1);
                entity.MoveGripPointsAt(indices, normal * delta);
                return true;
            }
            catch { return false; }
        }

        private static string DxfName(DBObject value) { try { return (value == null ? string.Empty : value.GetRXClass().DxfName ?? string.Empty).ToUpperInvariant(); } catch { return string.Empty; } }
        private static bool HasProperty(object value, string name)
        {
            if (value == null) return false;
            try
            {
                // AcadObject is normally System.__ComObject, whose CLR Type does
                // not enumerate IDispatch properties. Probe the property through
                // IDispatch instead of relying on Type.GetProperty().
                value.GetType().InvokeMember(name, BindingFlags.GetProperty, null, value, null, CultureInfo.CurrentCulture);
                return true;
            }
            catch { return false; }
        }
        private static bool TryGetFirstDouble(object value, string[] names, out double data)
        {
            data = 0d;
            foreach (var name in names)
            {
                try
                {
                    var raw = value.GetType().InvokeMember(name, BindingFlags.GetProperty, null, value, null, CultureInfo.CurrentCulture);
                    data = Math.Abs(Convert.ToDouble(raw, CultureInfo.CurrentCulture));
                    if (data > 0d) return true;
                }
                catch { }
            }
            return false;
        }
        private static bool TrySet(object value, string name, object data)
        {
            if (!HasProperty(value, name)) return false;
            try { Set(value, name, data); return true; } catch { return false; }
        }
        private static void Set(object value, string name, object data) { value.GetType().InvokeMember(name, BindingFlags.SetProperty, null, value, new[] { data }, CultureInfo.CurrentCulture); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate double GetPaperScale();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
