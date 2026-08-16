using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;

namespace BatchPdfPublisher.Services
{
    public sealed class TianzhengDimensionSettings
    {
        public double OuterExtensionLength = 8d;
        public double InnerExtensionLength = 5d;
        public double DimensionSpacing = 8d;
        public double AxisLeaderLength = 40d;
        public bool ApplyDimensionGeometry = true;
        public bool ApplyCadDimensionGeometry = true;
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
                    else if (key == "ApplyCadDimension") x.ApplyCadDimensionGeometry = value == "1";
                    else if (key == "ApplyAxis") x.ApplyAxisLeader = value == "1";
                }
            }
            catch { }
            return x;
        }
        public void Save()
        {
            System.IO.File.WriteAllLines(PathName, new[] { "Outer=" + OuterExtensionLength.ToString(CultureInfo.InvariantCulture), "Inner=" + InnerExtensionLength.ToString(CultureInfo.InvariantCulture), "Spacing=" + DimensionSpacing.ToString(CultureInfo.InvariantCulture), "AxisLeader=" + AxisLeaderLength.ToString(CultureInfo.InvariantCulture), "ApplyDimension=" + (ApplyDimensionGeometry ? "1" : "0"), "ApplyCadDimension=" + (ApplyCadDimensionGeometry ? "1" : "0"), "ApplyAxis=" + (ApplyAxisLeader ? "1" : "0") });
        }
    }

    internal static class TianzhengScaleService
    {
        private const int ScaleDispatchId = 30;
        public sealed class DimensionGeometryTarget
        {
            public double Distance;
            public int Direction;
            public Point3d OriginalSource1;
            public Point3d OriginalSource2;
            public bool UseSharedSourceBaseline;
            public Point3d SharedSourcePoint;
            public bool UseAxisSourceBaseline;
            public double AxisSourceCoordinate;
        }
        private sealed class MeasuredDimension
        {
            public ObjectId Id;
            public double Distance;
            public int Direction;
            public int Side;
            public Point3d Source1;
            public Point3d Source2;
        }
        public sealed class AxisFreeEndpoint
        {
            public Point3d Free;
            public Point3d CircleCenter;
        }
        public static bool IsTianzhengDimension(DBObject value)
        {
            var dxf = DxfName(value);
            return dxf == "TCH_DIMENSION" || dxf == "TCH_DIMENSION2" || dxf == "TCH_RADIUSDIM" || dxf == "TCH_RADUSDIM";
        }
        public static bool IsAxisLabel(DBObject value) { return DxfName(value) == "TCH_AXIS_LABEL"; }
        public static bool IsTianzhengText(DBObject value)
        {
            var dxf = DxfName(value);
            return dxf.StartsWith("TCH_", StringComparison.OrdinalIgnoreCase) &&
                   (dxf.IndexOf("TEXT", StringComparison.OrdinalIgnoreCase) >= 0 || dxf.IndexOf("WORD", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        public static bool IsTianzhengObject(DBObject value) { return DxfName(value).StartsWith("TCH_", StringComparison.OrdinalIgnoreCase); }
        private static bool IsDrawingName(DBObject value)
        {
            if (value == null) return false;
            try
            {
                var rx = value.GetRXClass();
                var className = rx == null ? string.Empty : rx.Name ?? string.Empty;
                var dxf = rx == null ? string.Empty : rx.DxfName ?? string.Empty;
                return className.IndexOf("DrawingName", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       className.IndexOf("SymbDrawingIndex", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       dxf.IndexOf("DRAWINGNAME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       dxf.IndexOf("DRAWING_NAME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       dxf.IndexOf("DWGNAME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       dxf.IndexOf("DRAWING_INDEX", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public static bool Apply(DBObject value, int scale, TianzhengDimensionSettings settings)
        {
            if (!IsTianzhengObject(value)) return false;
            var com = value.AcadObject;
            var changed = TrySetScale(com, Convert.ToDouble(scale, CultureInfo.InvariantCulture));
            if (IsDrawingName(value))
            {
                // TDbDrawingName stores the visible ratio separately from its
                // common output scale. Update both so 1:50 becomes 1:100 rather
                // than merely enlarging the complete annotation.
                changed |= TrySet(com, "DrawScale", true);
                changed |= TrySet(com, "ScaleText", "1:" + scale.ToString(CultureInfo.InvariantCulture));
            }
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

        public static Dictionary<ObjectId, DimensionGeometryTarget> BuildDimensionGeometryPlan(Transaction transaction, ObjectId[] ids, int targetScale, TianzhengDimensionSettings settings, IList<AxisFreeEndpoint> axisFreeEndpoints)
        {
            var measured = new List<MeasuredDimension>();
            foreach (var id in ids ?? new ObjectId[0])
            {
                try
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    double distance; int direction; int side; Point3d source1; Point3d source2;
                    if (entity != null && IsTianzhengDimension(entity) && TryMeasureDimensionGeometry(entity, out distance, out direction, out side, out source1, out source2))
                    {
                        // Preserve the side encoded by the original dimension
                        // itself. Axis labels on all four sides share the same
                        // grid coordinates, so trying to infer the side again
                        // from nearby labels can reassign top/left dimensions to
                        // the right/bottom sets.
                        measured.Add(new MeasuredDimension { Id = id, Distance = distance, Direction = direction, Side = side, Source1 = source1, Source2 = source2 });
                    }
                }
                catch { }
            }
            var result = new Dictionary<ObjectId, DimensionGeometryTarget>();
            foreach (var sideGroup in measured.GroupBy(x => x.Side))
            {
                var group = sideGroup.ToList();
                var innermost = group.OrderBy(x => x.Distance).First();
                var sharedSourcePoint = MidPoint(innermost.Source1, innermost.Source2);
                var tolerance = Math.Max(0.1d, group.Max(x => x.Distance) * 0.02d);
                var levels = new List<double>();
                foreach (var distance in group.Select(x => x.Distance).OrderBy(x => x))
                    if (levels.Count == 0 || Math.Abs(distance - levels[levels.Count - 1]) > tolerance) levels.Add(distance);
                foreach (var item in group)
                {
                    var level = 0; var best = double.MaxValue;
                    for (var i = 0; i < levels.Count; i++) { var difference = Math.Abs(item.Distance - levels[i]); if (difference < best) { best = difference; level = i; } }
                    double axisCoordinate;
                    var hasAxisReference = TryResolveAxisSourceCoordinate(item.Source1, item.Source2, item.Direction, axisFreeEndpoints, out axisCoordinate);
                    result[item.Id] = new DimensionGeometryTarget { Distance = Math.Max(0d, settings.InnerExtensionLength + level * settings.DimensionSpacing) * targetScale, Direction = item.Direction, OriginalSource1 = item.Source1, OriginalSource2 = item.Source2, UseAxisSourceBaseline = hasAxisReference, AxisSourceCoordinate = axisCoordinate, UseSharedSourceBaseline = !hasAxisReference, SharedSourcePoint = sharedSourcePoint };
                }
            }
            return result;
        }

        public static List<AxisFreeEndpoint> CollectAxisFreeEndpoints(Transaction transaction, ObjectId spaceId)
        {
            var result = new List<AxisFreeEndpoint>();
            try
            {
                var space = transaction.GetObject(spaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity == null || !IsAxisLabel(entity)) continue;
                    try
                    {
                        var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                        entity.GetGripPoints(grips, osnap, geometry);
                        // TCH_AXIS_LABEL exposes one triplet per axis: free end,
                        // circle centre and circle tangent.  A final grip (when
                        // present) controls the complete axis set and is ignored.
                        for (var i = 0; i + 2 < grips.Count; i += 3) AddUnique(result, grips[i], grips[i + 1]);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        public static List<AxisFreeEndpoint> CollectAxisFreeEndpoints(Transaction transaction, IEnumerable<ObjectId> ids)
        {
            var result = new List<AxisFreeEndpoint>();
            foreach (var id in ids ?? Enumerable.Empty<ObjectId>())
            {
                try
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !IsAxisLabel(entity)) continue;
                    var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                    entity.GetGripPoints(grips, osnap, geometry);
                    for (var index = 0; index + 2 < grips.Count; index += 3) AddUnique(result, grips[index], grips[index + 1]);
                }
                catch { }
            }
            return result;
        }

        public static bool ApplyCommittedDimensionGeometry(Entity entity, DimensionGeometryTarget target, IList<AxisFreeEndpoint> axisFreeEndpoints, out string error)
        {
            error = string.Empty;
            if (entity == null || !IsTianzhengDimension(entity) || target == null) return false;
            try { return ApplyDimensionGripGeometry(entity, target.Distance, target.Direction, target.OriginalSource1, target.OriginalSource2, target.UseAxisSourceBaseline, target.AxisSourceCoordinate, target.UseSharedSourceBaseline, target.SharedSourcePoint, axisFreeEndpoints); }
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

        public static bool TrySetCurrentScale(int scale, out string error)
        {
            error = string.Empty;
            if (!IsLoaded()) return false;
            try
            {
                // Tianzheng exposes this Lisp API itself and uses it in PAPER.lsp.
                // Application.Invoke executes synchronously on the CAD command
                // thread, so the status-bar scale is updated before object work.
                using (var arguments = new ResultBuffer(
                    new TypedValue((int)LispDataType.Text, "TSetPScale"),
                    new TypedValue((int)LispDataType.Double, Convert.ToDouble(scale, CultureInfo.InvariantCulture))))
                using (var result = Autodesk.AutoCAD.ApplicationServices.Application.Invoke(arguments)) { }
                return true;
            }
            catch (System.Exception exception) { error = exception.Message; return false; }
        }

        public static void QueueDimensionAutoAdjust(Document document, IList<ObjectId> dimensionIds)
        {
            if (document == null || dimensionIds == null || dimensionIds.Count == 0 || !IsLoaded()) return;
            try
            {
                // TDimAdjust is Tianzheng's documented “尺寸自调” command. Keep
                // the current command fast and stable: preselect only the dims
                // changed above, then queue the Tianzheng command so it runs as
                // soon as WLSCALE returns to AutoCAD's command loop.
                QueueCommandForObjects(document, "TDimAdjust", dimensionIds);
            }
            catch (System.Exception exception)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " queue TDimAdjust failed: " + exception + Environment.NewLine); } catch { }
            }
        }

        public static void QueueTextAutoAdjust(Document document, IList<ObjectId> textIds)
        {
            if (document == null || textIds == null || textIds.Count == 0 || !IsLoaded()) return;
            try
            {
                QueueCommandForObjects(document, "TTextAdjust", textIds);
            }
            catch (System.Exception exception)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(UserDataPaths.LogsDirectory, "tianzheng-scale.log"), DateTime.Now.ToString("O") + " queue TTextAdjust failed: " + exception + Environment.NewLine); } catch { }
            }
        }

        private static void QueueCommandForObjects(Document document, string command, IList<ObjectId> ids)
        {
            // Build the selection set from stable handles at execution time.
            // Using SetImpliedSelection for two queued Tianzheng commands makes
            // the second selection overwrite the first before either runs.
            var expression = new System.Text.StringBuilder("(progn(setq WLSS(ssadd))");
            foreach (var id in ids.Where(value => !value.IsNull && value.IsValid))
                expression.Append("(if(setq WLE(handent \"").Append(id.Handle.ToString()).Append("\"))(ssadd WLE WLSS))");
            expression.Append("(if(> (sslength WLSS) 0)(command \"").Append(command).Append("\" WLSS \"\"))(princ))\n");
            document.SendStringToExecute(expression.ToString(), true, false, false);
        }

        private static bool TryMeasureDimensionGeometry(Entity entity, out double distance, out int direction, out int side, out Point3d source1, out Point3d source2)
        {
            distance = 0d; direction = 1; side = 0; source1 = Point3d.Origin; source2 = Point3d.Origin;
            try
            {
                var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                entity.GetGripPoints(grips, osnap, geometry);
                if (grips.Count < 4) return false;
                var tangent = grips[1] - grips[0]; if (tangent.Length < 1e-8) return false; tangent = CanonicalTangent(tangent.GetNormal());
                var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
                Func<Point3d, double> project = p => p.X * normal.X + p.Y * normal.Y + p.Z * normal.Z;
                var sourceIndices = GetDimensionSourceGripIndices(grips, normal);
                if (sourceIndices.Count < 2) return false;
                var dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                var sourceCoordinate = sourceIndices.Average(index => project(grips[index]));
                distance = Math.Abs(sourceCoordinate - dimensionCoordinate);
                direction = Math.Sign(dimensionCoordinate - sourceCoordinate); if (direction == 0) direction = 1;
                side = (Math.Abs(tangent.X) >= Math.Abs(tangent.Y) ? 10 : 20) + (direction > 0 ? 1 : 2);
                // A continuous Tianzheng dimension exposes every measured axis
                // point as a grip on the same source baseline.  Keep the two
                // extreme points for axis-label matching, rather than assuming
                // that grip 2 and grip 3 are the complete measured span.
                var orderedSources = sourceIndices.Select(index => grips[index]).OrderBy(point => Dot(point, tangent)).ToList();
                source1 = orderedSources[0]; source2 = orderedSources[orderedSources.Count - 1];
                return distance > 1e-8;
            }
            catch { return false; }
        }

        private static bool ApplyDimensionGripGeometry(Entity entity, double targetDistance, int originalDirection, Point3d originalSource1, Point3d originalSource2, bool useAxisSourceBaseline, double plannedAxisSourceCoordinate, bool useSharedSourceBaseline, Point3d sharedSourcePoint, IList<AxisFreeEndpoint> axisFreeEndpoints)
        {
            if (entity == null) return false;
            try
            {
                var grips = new Point3dCollection(); var osnap = new IntegerCollection(); var geometry = new IntegerCollection();
                entity.GetGripPoints(grips, osnap, geometry);
                if (grips.Count < 4) return false;
                var tangent = grips[1] - grips[0];
                if (tangent.Length < 1e-8) return false;
                tangent = CanonicalTangent(tangent.GetNormal());
                var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
                Func<Point3d, double> project = p => p.X * normal.X + p.Y * normal.Y + p.Z * normal.Z;
                var sourceGripIndices = GetDimensionSourceGripIndices(grips, normal);
                if (sourceGripIndices.Count < 2) return false;
                var dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                var source = sourceGripIndices.Average(index => project(grips[index]));
                var direction = originalDirection == 0 ? Math.Sign(dimensionCoordinate - source) : originalDirection;
                if (direction == 0) direction = 1;

                var aligned = false;
                double currentAxisSourceCoordinate = plannedAxisSourceCoordinate;
                var axisResolved = useAxisSourceBaseline && TryResolveAxisSourceCoordinate(originalSource1, originalSource2, direction, axisFreeEndpoints, out currentAxisSourceCoordinate);
                if (useAxisSourceBaseline)
                {
                    // When matching axis labels exist, their free endpoints are
                    // the authoritative extension-line origins. Both inner and
                    // outer dimensions move to this same baseline. The stored
                    // coordinate is a fallback for Tianzheng versions that
                    // rebuild axis grips after their Scale property changes.
                    var desiredSource = axisResolved ? currentAxisSourceCoordinate : plannedAxisSourceCoordinate;
                    var sourceDelta = desiredSource - source;
                    if (Math.Abs(sourceDelta) > 0.01d)
                    {
                        var sourceIndices = new IntegerCollection();
                        foreach (var sourceIndex in sourceGripIndices) sourceIndices.Add(sourceIndex);
                        entity.MoveGripPointsAt(sourceIndices, normal * sourceDelta);
                        aligned = true;
                        grips = new Point3dCollection(); osnap = new IntegerCollection(); geometry = new IntegerCollection();
                        entity.GetGripPoints(grips, osnap, geometry);
                        sourceGripIndices = GetDimensionSourceGripIndices(grips, normal);
                        if (sourceGripIndices.Count < 2) return aligned;
                        dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                        source = sourceGripIndices.Average(index => project(grips[index]));
                    }
                }
                else if (useSharedSourceBaseline)
                {
                    // Dimension-only fallback: use the innermost dimension's
                    // extension origin as the common datum. Move the complete
                    // source-grip run together, never individual source points.
                    var desiredSource = project(sharedSourcePoint);
                    var sourceDelta = desiredSource - source;
                    if (Math.Abs(sourceDelta) > 0.01d)
                    {
                        var sourceIndices = new IntegerCollection();
                        foreach (var sourceIndex in sourceGripIndices) sourceIndices.Add(sourceIndex);
                        entity.MoveGripPointsAt(sourceIndices, normal * sourceDelta);
                        aligned = true;
                        grips = new Point3dCollection(); osnap = new IntegerCollection(); geometry = new IntegerCollection();
                        entity.GetGripPoints(grips, osnap, geometry);
                        sourceGripIndices = GetDimensionSourceGripIndices(grips, normal);
                        if (sourceGripIndices.Count < 2) return aligned;
                        dimensionCoordinate = (project(grips[0]) + project(grips[1])) * 0.5d;
                        source = sourceGripIndices.Average(index => project(grips[index]));
                    }
                }
                // The source grips already retain the correct axis locations
                // after Tianzheng changes Scale. Never rematch or move them.
                // Only relocate the dimension-line grips (0/1) along the normal;
                // this keeps the measured span, extension origins and original
                // top/bottom/left/right ownership unchanged.
                var desired = source + direction * Math.Max(0d, targetDistance);
                var delta = desired - dimensionCoordinate;
                if (Math.Abs(delta) <= Math.Max(0.01d, targetDistance * 0.001d)) return aligned;
                var indices = new IntegerCollection(); indices.Add(0); indices.Add(1);
                entity.MoveGripPointsAt(indices, normal * delta);
                return true;
            }
            catch { return false; }
        }

        private static double Dot(Point3d point, Vector3d vector) { return point.X * vector.X + point.Y * vector.Y + point.Z * vector.Z; }
        private static Point3d MidPoint(Point3d first, Point3d second) { return new Point3d((first.X + second.X) * 0.5d, (first.Y + second.Y) * 0.5d, (first.Z + second.Z) * 0.5d); }
        private static bool TryResolveAxisSourceCoordinate(Point3d source1, Point3d source2, int direction, IList<AxisFreeEndpoint> endpoints, out double coordinate)
        {
            coordinate = 0d;
            if (endpoints == null || endpoints.Count == 0) return false;
            var tangentValue = source2 - source1;
            if (tangentValue.Length < 1e-8) return false;
            var tangent = CanonicalTangent(tangentValue.GetNormal());
            var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
            var tolerance = Math.Max(1d, source1.DistanceTo(source2) * 0.002d);
            var matched = new List<AxisFreeEndpoint>();
            foreach (var source in new[] { source1, source2 })
            {
                AxisFreeEndpoint bestEndpoint = null; var best = double.MaxValue;
                foreach (var endpoint in endpoints)
                {
                    var leader = endpoint.CircleCenter - endpoint.Free;
                    if (leader.Length < 1e-8 || Math.Abs(leader.GetNormal().DotProduct(normal)) < 0.9d) continue;
                    var endpointDirection = Math.Sign(leader.DotProduct(normal));
                    if (direction != 0 && endpointDirection != direction) continue;
                    var tangentDelta = Math.Abs(Dot(endpoint.Free, tangent) - Dot(source, tangent));
                    if (tangentDelta <= tolerance && tangentDelta < best) { best = tangentDelta; bestEndpoint = endpoint; }
                }
                if (bestEndpoint == null) return false;
                matched.Add(bestEndpoint);
            }
            coordinate = matched.Average(endpoint => Dot(endpoint.Free, normal));
            return true;
        }
        private static List<int> GetDimensionSourceGripIndices(Point3dCollection grips, Vector3d normal)
        {
            var result = new List<int>();
            if (grips == null || grips.Count < 4) return result;

            // Grip 0/1 define the dimension-line tangent.  Tianzheng then emits
            // a consecutive run beginning at grip 2 for all measured/source
            // points. These points share one normal coordinate. Text grips and
            // auxiliary controls follow and leave that baseline, which gives us
            // a stable boundary for both simple and continuous dimensions.
            var sourceCoordinate = Dot(grips[2], normal);
            var dimensionSpan = grips[0].DistanceTo(grips[1]);
            var tolerance = Math.Max(0.05d, dimensionSpan * 0.00001d);
            for (var index = 2; index < grips.Count; index++)
            {
                if (Math.Abs(Dot(grips[index], normal) - sourceCoordinate) > tolerance) break;
                result.Add(index);
            }
            return result;
        }
        private static Vector3d CanonicalTangent(Vector3d value)
        {
            if (value.X < -1e-9 || (Math.Abs(value.X) <= 1e-9 && value.Y < 0d)) return -value;
            return value;
        }
        private static int ResolveAxisDirection(Point3d source1, Point3d source2, IList<AxisFreeEndpoint> endpoints)
        {
            if (endpoints == null || endpoints.Count == 0) return 0;
            var tangentValue = source2 - source1; if (tangentValue.Length < 1e-8) return 0;
            var tangent = CanonicalTangent(tangentValue.GetNormal());
            var normal = new Vector3d(-tangent.Y, tangent.X, 0d).GetNormal();
            var tolerance = Math.Max(1d, source1.DistanceTo(source2) * 0.002d);
            var best = double.MaxValue; var result = 0;
            foreach (var endpoint in endpoints)
            {
                var tangentDelta = Math.Abs(Dot(endpoint.Free, tangent) - Dot(source1, tangent));
                if (tangentDelta > tolerance) continue;
                var towardAxisBubble = endpoint.CircleCenter - endpoint.Free;
                var side = Math.Sign(towardAxisBubble.DotProduct(normal)); if (side == 0) continue;
                var score = tangentDelta * 20d + endpoint.Free.DistanceTo(source1);
                if (score < best) { best = score; result = side; }
            }
            return result;
        }
        private static void AddUnique(List<AxisFreeEndpoint> points, Point3d value, Point3d circleCenter)
        {
            foreach (var point in points) if (point.Free.DistanceTo(value) <= 0.01d) return;
            points.Add(new AxisFreeEndpoint { Free = value, CircleCenter = circleCenter });
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
            // Setting through IDispatch already reports a missing property.
            // Avoid probing with a separate COM get before every write; on a
            // large selection that doubled the number of Tianzheng COM calls.
            try { Set(value, name, data); return true; } catch { return false; }
        }
        private static bool TrySetScale(object value, double scale)
        {
            if (value == null) return false;
            try
            {
                // Tianzheng registers “出图比例” as COM DISPID 30 for walls,
                // openings, columns and its other custom entities. Invoking the
                // known property directly avoids an IDispatch name lookup and
                // reflection binder allocation for every selected object.
                var dispatch = value as IDispatchNative;
                if (dispatch != null)
                {
                    var argument = new VariantArg { VariantType = 5, DoubleValue = scale }; // VT_R8
                    var namedArgument = -3; // DISPID_PROPERTYPUT
                    var arguments = new DispatchParameters
                    {
                        Arguments = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(VariantArg))),
                        NamedArguments = Marshal.AllocCoTaskMem(sizeof(int)),
                        ArgumentCount = 1,
                        NamedArgumentCount = 1
                    };
                    try
                    {
                        Marshal.StructureToPtr(argument, arguments.Arguments, false);
                        Marshal.WriteInt32(arguments.NamedArguments, namedArgument);
                        var iid = Guid.Empty;
                        var result = dispatch.Invoke(ScaleDispatchId, ref iid, CultureInfo.CurrentCulture.LCID, 4, ref arguments, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // DISPATCH_PROPERTYPUT
                        if (result < 0) Marshal.ThrowExceptionForHR(result);
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(arguments.Arguments);
                        Marshal.FreeCoTaskMem(arguments.NamedArguments);
                    }
                }
            }
            catch { }
            return TrySet(value, "Scale", scale);
        }
        private static void Set(object value, string name, object data) { value.GetType().InvokeMember(name, BindingFlags.SetProperty, null, value, new[] { data }, CultureInfo.CurrentCulture); }

        [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatchNative
        {
            [PreserveSig] int GetTypeInfoCount(out uint count);
            [PreserveSig] int GetTypeInfo(uint index, int localeId, out IntPtr typeInfo);
            [PreserveSig] int GetIdsOfNames(ref Guid iid, IntPtr names, uint count, int localeId, IntPtr dispatchIds);
            [PreserveSig] int Invoke(int dispatchId, ref Guid iid, int localeId, ushort flags, ref DispatchParameters parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argumentError);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatchParameters
        {
            public IntPtr Arguments;
            public IntPtr NamedArguments;
            public uint ArgumentCount;
            public uint NamedArgumentCount;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct VariantArg
        {
            [FieldOffset(0)] public ushort VariantType;
            [FieldOffset(8)] public double DoubleValue;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate double GetPaperScale();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
