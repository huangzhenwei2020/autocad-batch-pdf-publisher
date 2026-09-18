using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
            var result = new LineVisionVectorResult(); Add(result.Polylines, dto.Centerlines, "骨架中心线", true);
            Add(result.Polylines, dto.Outlines, "VTracer轮廓", settings.VectorMode == LineVisionVectorMode.Outline);
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
            return result;
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
