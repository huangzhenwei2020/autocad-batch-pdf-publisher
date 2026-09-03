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

        public async Task<List<LineVisionPolyline>> VectorizeAsync(string imagePath, Rectangle? region, LineVisionSettings settings, IEnumerable<LineVisionOcrTextRegion> textRegions, int maskExpansion, CancellationToken cancellationToken)
        {
            if (!IsAvailable) throw new FileNotFoundException("未找到矢量化 Worker，请重新运行最新版启动器。", _workerPath);
            var operation = Path.Combine(UserDataPaths.TemporaryDirectory, "linevision-vector-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(operation);
            var input = imagePath; var output = Path.Combine(operation, "result.json");
            try
            {
                if (region.HasValue || textRegions != null) { input = Path.Combine(operation, "prepared.png"); SavePrepared(imagePath, region, textRegions, maskExpansion, input); }
                var mode = settings.VectorMode == LineVisionVectorMode.Outline ? "outline" : settings.VectorMode == LineVisionVectorMode.Hybrid ? "hybrid" : "centerline";
                var start = new ProcessStartInfo { FileName = _workerPath, Arguments = "--input " + Quote(input) + " --output " + Quote(output) + " --mode " + mode + " --threshold " + settings.Threshold, WorkingDirectory = Path.GetDirectoryName(_workerPath), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
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
                    return Convert(dto, settings.VectorMode, settings.OrthogonalToleranceDegrees);
                }
            }
            finally { try { Directory.Delete(operation, true); } catch { } }
        }

        private static List<LineVisionPolyline> Convert(VectorResult dto, LineVisionVectorMode mode, double orthogonalTolerance)
        {
            var result = new List<LineVisionPolyline>(); Add(result, dto.Centerlines, "骨架中心线", true);
            Add(result, dto.Outlines, "VTracer轮廓", mode == LineVisionVectorMode.Outline);
            foreach (var polyline in result) SnapOrthogonal(polyline, orthogonalTolerance);
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
                using (var graphics = Graphics.FromImage(prepared))
                using (var brush = new SolidBrush(Color.White))
                {
                    foreach (var text in textRegions ?? new List<LineVisionOcrTextRegion>())
                    {
                        if (text == null || !text.IsEnabled) continue; var bounds = text.Bounds;
                        graphics.FillRectangle(brush, bounds.Left - expansion, bounds.Top - expansion, bounds.Width + expansion * 2, bounds.Height + expansion * 2);
                    }
                    prepared.Save(output, ImageFormat.Png);
                }
            }
        }
        private static VectorResult Read(string path) { if (!File.Exists(path)) return null; try { var serializer = new DataContractJsonSerializer(typeof(VectorResult)); using (var stream = File.OpenRead(path)) return serializer.ReadObject(stream) as VectorResult; } catch { return null; } }
        private static string Quote(string value) { return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\""; }

        [DataContract] private sealed class VectorResult { [DataMember(Name = "success")] public bool Success { get; set; } [DataMember(Name = "error")] public string Error { get; set; } [DataMember(Name = "centerlines")] public List<VectorPolyline> Centerlines { get; set; } [DataMember(Name = "outlines")] public List<VectorPolyline> Outlines { get; set; } }
        [DataContract] private sealed class VectorPolyline { [DataMember(Name = "points")] public List<VectorPoint> Points { get; set; } [DataMember(Name = "closed")] public bool Closed { get; set; } [DataMember(Name = "confidence")] public double Confidence { get; set; } }
        [DataContract] private sealed class VectorPoint { [DataMember(Name = "x")] public double X { get; set; } [DataMember(Name = "y")] public double Y { get; set; } }
    }
}
