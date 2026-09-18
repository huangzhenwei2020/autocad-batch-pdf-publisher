using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    internal sealed class LineVisionVectorWorkerClient
    {
        private readonly string _workerPath;
        public LineVisionVectorWorkerClient(string workerPath = null) { _workerPath = string.IsNullOrWhiteSpace(workerPath) ? Path.Combine(Path.GetDirectoryName(typeof(LineVisionVectorWorkerClient).Assembly.Location), "LineVisionVectorWorker.exe") : workerPath; }
        public bool IsAvailable { get { return File.Exists(_workerPath); } }

        public async Task<LineVisionVectorResult> VectorizeAsync(string imagePath, Rectangle? region, LineVisionSettings settings, IEnumerable<LineVisionOcrTextRegion> textRegions, int maskExpansion, CancellationToken cancellationToken)
        {
            if (!IsAvailable) throw new FileNotFoundException("未找到矢量化 Worker，请重新运行最新版启动器。", _workerPath);
            var operation = Path.Combine(UserDataPaths.TemporaryDirectory, "linevision-vector-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(operation);
            var input = imagePath; var output = Path.Combine(operation, "result.json");
            try
            {
                if (region.HasValue || textRegions != null) { input = Path.Combine(operation, "prepared.png"); SavePrepared(imagePath, region, textRegions, maskExpansion, input); }
                var mode = settings.VectorMode == LineVisionVectorMode.Outline ? "outline" : settings.VectorMode == LineVisionVectorMode.Hybrid ? "hybrid" : "centerline";
                // 钩上「识别墙体填充」就要检测墙体，而墙体来自闭合轮廓，必须让内核跑一次 outline 分支。
                // 以前这里把 DetectWallFills 也算成 hybrid，等于只要勾着开关就无条件白跑一遍 VTracer；
                // 现在只有真正需要墙体时才把 mode 提到 hybrid。
                if (settings.DetectWallFills && mode == "centerline") mode = "hybrid";
                // 这些参数以前没有传给内核，于是界面上调“最短线/合并间隙/共线容差”对最终结果毫无影响。
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                var start = new ProcessStartInfo { FileName = _workerPath, Arguments = "--input " + Quote(input) + " --output " + Quote(output) + " --mode " + mode + " --threshold " + settings.Threshold
                    + " --minimum " + settings.MinimumLineLengthPixels.ToString(culture)
                    + " --merge-gap " + settings.MergeGapPixels.ToString(culture)
                    + " --collinear " + settings.CollinearTolerancePixels.ToString(culture)
                    + " --wall-min " + settings.MinimumWallThicknessPixels.ToString(culture) + " --wall-max " + settings.MaximumWallThicknessPixels.ToString(culture)
                    + " --wall-pattern-scale " + settings.WallHatchPatternScale.ToString(culture), WorkingDirectory = Path.GetDirectoryName(_workerPath), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("无法启动矢量化 Worker。"); var started = DateTime.UtcNow;
                    while (!process.HasExited)
                    {
                        if (cancellationToken.IsCancellationRequested || DateTime.UtcNow - started > TimeSpan.FromMinutes(3)) { try { process.Kill(); } catch { } cancellationToken.ThrowIfCancellationRequested(); throw new TimeoutException("矢量化超时，请框选较小范围后重试。"); }
                        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                    }
                    var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false); var dto = Read(output);
                    if (dto == null || !dto.Success) throw new InvalidOperationException(dto == null ? "矢量化 Worker 没有返回结果。" + error : dto.Error);
                    return Convert(dto, settings, settings.OrthogonalToleranceDegrees);
                }
            }
            finally { try { Directory.Delete(operation, true); } catch { } }
        }

        private static LineVisionVectorResult Convert(VectorResult dto, LineVisionSettings settings, double orthogonalTolerance)
        {
            var result = new LineVisionVectorResult();
            // 轮廓是"闭合的形状"，粗黑笔画（墙身、实心构件）只有靠它才能被框住并填实。
            // 骨架给的是中心线——遇到粗黑块会收成一条线，形状对但没填实。
            // 所以只要用户显式选了「保留轮廓」，就必须启用轮廓；此前这里写成"等于 Outline 模式"，
            // 而 DetectWallFills 会把模式提成 hybrid，于是选了保留轮廓也拿不到轮廓。
            var keepOutlines = settings.VectorMode == LineVisionVectorMode.Outline;
            Add(result.Polylines, dto.Centerlines, "骨架中心线", !keepOutlines);
            Add(result.Polylines, dto.Outlines, "VTracer轮廓", keepOutlines);
            foreach (var polyline in result.Polylines) SnapOrthogonal(polyline, orthogonalTolerance);
            // 墙体边框线是识别结果，默认启用；填充做法由 WallFillMode 单独控制，
            // 所以旧的 IsEnabled=false（写死默认关闭）会让边框线也一并消失——实测平面图
            // 认出 84 个墙体区域，用户界面上一个都看不到。现在边框总是启用，填不填由用户选。
            if (settings.DetectWallFills) foreach (var wall in dto.WallRegions ?? new List<VectorWallRegion>())
            {
                var converted = new LineVisionWallRegion
                {
                    AverageThickness = wall.AverageThickness,
                    Confidence = wall.Confidence,
                    IsEnabled = true,
                    FillMode = settings.WallFillMode,
                    HatchPatternName = string.IsNullOrWhiteSpace(settings.WallHatchPatternName) ? "ANSI31" : settings.WallHatchPatternName,
                    HatchPatternScale = wall.PatternScale > 0d ? wall.PatternScale : 1d
                };
                foreach (var point in wall.Outer ?? new List<VectorPoint>()) converted.Outer.Add(new PointF((float)point.X, (float)point.Y));
                foreach (var hole in wall.Holes ?? new List<List<VectorPoint>>()) { var points = new List<PointF>(); foreach (var point in hole) points.Add(new PointF((float)point.X, (float)point.Y)); converted.Holes.Add(points); }
                if (converted.Outer.Count >= 3) result.WallRegions.Add(converted);
            }

            // 墙身内部不再保留骨架中线。墙是一段粗笔画，骨架会在墙身正中画出一条线；
            // 而墙体本身已经有"边框 + 填充"两套几何。两者叠在一起，同一堵墙就被画了两遍，
            // 视觉上就成了一条粗线（用户反馈的"墙线还是变成一条了"）。建筑图里墙身中间
            // 本来也不该有中线。只丢弃落在墙体区域内的中线段，其余骨架照旧保留。
            if (settings.DetectWallFills && settings.DropCenterlinesInsideWalls && result.WallRegions.Count > 0)
                result.Polylines = result.Polylines
                    .Where(polyline => polyline.Source != "骨架中心线" || !LiesInsideAnyWall(polyline, result.WallRegions))
                    .ToList();
            // 轮廓已经完整描出了粗黑块的边界，再叠一层骨架中线只会让它看起来像一条线。
            // 但细线、文字笔画在轮廓里表现很差，绝不能一起丢——所以只丢弃"落在粗黑块轮廓内部"
            // 的中线。判定门槛不能用中位面积：实测轮廓面积中位只有 38px²，那是细线描出来的细长条，
            // 拿它当门槛等于把细线也一起丢。这里要求轮廓面积达到绝对下限，并且明显大于中位。
            if (keepOutlines && settings.DropCenterlinesInsideOutlines)
            {
                var outlines = result.Polylines.Where(value => value.Source == "VTracer轮廓").ToList();
                var areas = outlines.Select(value => Math.Abs(SignedArea(value.Points))).Where(value => value > 0d).OrderBy(value => value).ToList();
                if (areas.Count > 0)
                {
                    var median = areas[areas.Count / 2];
                    var threshold = Math.Max(MinimumSolidOutlineArea, median * 4d);
                    var solid = outlines.Where(value => Math.Abs(SignedArea(value.Points)) >= threshold).ToList();
                    if (solid.Count > 0)
                        result.Polylines = result.Polylines
                            .Where(value => value.Source != "骨架中心线" || !LiesInsideAnyPolygon(value, solid))
                            .ToList();
                }
            }
            return result;
        }

        /// <summary>只有面积达到这个值的轮廓才被视为"粗黑块"。低于它的当作细线轮廓，不参与丢弃中线。</summary>
        private const double MinimumSolidOutlineArea = 400d;

        private static double SignedArea(IList<PointF> points)
        {
            if (points == null || points.Count < 3) return 0d;
            var area = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var a = points[index]; var b = points[(index + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5d;
        }

        private static bool LiesInsideAnyPolygon(LineVisionPolyline polyline, IList<LineVisionPolyline> polygons)
        {
            if (polyline.Points == null || polyline.Points.Count < 2) return false;
            foreach (var polygon in polygons)
            {
                if (polygon.Points.Count < 3) continue;
                var inside = 0;
                foreach (var point in polyline.Points) if (ContainsPoint(polygon.Points, point)) inside++;
                if (inside >= polyline.Points.Count * 0.8d) return true;
            }
            return false;
        }

        /// <summary>整条折线是否落在某个墙体区域内部（按顶点比例判定）。</summary>
        private static bool LiesInsideAnyWall(LineVisionPolyline polyline, IList<LineVisionWallRegion> walls)
        {
            if (polyline.Points == null || polyline.Points.Count < 2) return false;
            foreach (var wall in walls)
            {
                if (wall.Outer.Count < 3) continue;
                var inside = 0;
                foreach (var point in polyline.Points) if (ContainsPoint(wall.Outer, point)) inside++;
                if (inside >= polyline.Points.Count * 0.8d) return true;
            }
            return false;
        }

        /// <summary>射线法判断点是否在多边形内。</summary>
        private static bool ContainsPoint(IList<PointF> polygon, PointF point)
        {
            var inside = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var a = polygon[index];
                var b = polygon[(index + polygon.Count - 1) % polygon.Count];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (Math.Abs(b.Y - a.Y) < 1e-9f ? 1e-9f : b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        internal static void SnapOrthogonal(LineVisionPolyline polyline, double toleranceDegrees)
        {
            if (polyline == null || polyline.Points == null || polyline.Points.Count < 2 || toleranceDegrees <= 0d) return;
            var points = polyline.Points;
            for (var index = 1; index < points.Count; index++) SnapPair(points, index - 1, index, toleranceDegrees);
            if (polyline.IsClosed && points.Count > 2)
            {
                var first = points[0]; var last = points[points.Count - 1]; var dx = first.X - last.X; var dy = first.Y - last.Y;
                var angle = Normalize(Math.Atan2(dy, dx) * 180d / Math.PI);
                if (DistanceToHorizontal(angle) <= toleranceDegrees) points[points.Count - 1] = new PointF(last.X, first.Y);
                else if (Math.Abs(angle - 90d) <= toleranceDegrees) points[points.Count - 1] = new PointF(first.X, last.Y);
            }
        }

        private static void SnapPair(IList<PointF> points, int firstIndex, int secondIndex, double tolerance)
        {
            var first = points[firstIndex]; var second = points[secondIndex]; var angle = Normalize(Math.Atan2(second.Y - first.Y, second.X - first.X) * 180d / Math.PI);
            if (DistanceToHorizontal(angle) <= tolerance) points[secondIndex] = new PointF(second.X, first.Y);
            else if (Math.Abs(angle - 90d) <= tolerance) points[secondIndex] = new PointF(first.X, second.Y);
        }

        private static double Normalize(double degrees) { degrees %= 180d; if (degrees < 0d) degrees += 180d; return degrees; }
        private static double DistanceToHorizontal(double degrees) { return Math.Min(degrees, 180d - degrees); }

        private static void Add(IList<LineVisionPolyline> result, IEnumerable<VectorPolyline> values, string source, bool enabled)
        {
            foreach (var value in values ?? new List<VectorPolyline>())
            {
                var points = new List<PointF>(); foreach (var point in value.Points ?? new List<VectorPoint>()) points.Add(new PointF((float)point.X, (float)point.Y));
                if (points.Count >= 2) result.Add(new LineVisionPolyline { Points = points, IsClosed = value.Closed, Confidence = value.Confidence, IsEnabled = enabled, Source = source });
            }
        }

        private static void SavePrepared(string path, Rectangle? requested, IEnumerable<LineVisionOcrTextRegion> textRegions, int expansion, string output)
        {
            using (var source = new Bitmap(path))
            {
                var region = requested.HasValue ? Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), requested.Value) : new Rectangle(0, 0, source.Width, source.Height);
                using (var prepared = source.Clone(region, PixelFormat.Format24bppRgb))
                {
                    LineVisionTextMasker.Apply(prepared, null, textRegions, 1d, Math.Max(0, expansion));
                    prepared.Save(output, ImageFormat.Png);
                }
            }
        }
        private static VectorResult Read(string path) { if (!File.Exists(path)) return null; try { var serializer = new DataContractJsonSerializer(typeof(VectorResult)); using (var stream = File.OpenRead(path)) return serializer.ReadObject(stream) as VectorResult; } catch { return null; } }
        private static string Quote(string value) { return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\""; }

        [DataContract] private sealed class VectorResult { [DataMember(Name = "success")] public bool Success { get; set; } [DataMember(Name = "error")] public string Error { get; set; } [DataMember(Name = "centerlines")] public List<VectorPolyline> Centerlines { get; set; } [DataMember(Name = "outlines")] public List<VectorPolyline> Outlines { get; set; } [DataMember(Name = "wallRegions")] public List<VectorWallRegion> WallRegions { get; set; } }
        [DataContract] private sealed class VectorPolyline { [DataMember(Name = "points")] public List<VectorPoint> Points { get; set; } [DataMember(Name = "closed")] public bool Closed { get; set; } [DataMember(Name = "confidence")] public double Confidence { get; set; } }
        [DataContract] private sealed class VectorPoint { [DataMember(Name = "x")] public double X { get; set; } [DataMember(Name = "y")] public double Y { get; set; } }
        [DataContract] private sealed class VectorWallRegion { [DataMember(Name = "outer")] public List<VectorPoint> Outer { get; set; } [DataMember(Name = "holes")] public List<List<VectorPoint>> Holes { get; set; } [DataMember(Name = "averageThickness")] public double AverageThickness { get; set; } [DataMember(Name = "confidence")] public double Confidence { get; set; } [DataMember(Name = "patternScale")] public double PatternScale { get; set; } }
    }

    internal sealed class LineVisionVectorResult { public List<LineVisionPolyline> Polylines { get; set; } = new List<LineVisionPolyline>(); public List<LineVisionWallRegion> WallRegions { get; set; } = new List<LineVisionWallRegion>(); }
}
