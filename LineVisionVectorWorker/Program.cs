using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Serialization.Json;

namespace Wanluo.LineVision.VectorWorker
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var output = Value(args, "--output");
            try
            {
                var input = Value(args, "--input"); var mode = (Value(args, "--mode") ?? "centerline").ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new FileNotFoundException("输入图片不存在。", input);
                if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("缺少 --output 参数。");
                if (mode != "centerline" && mode != "outline" && mode != "hybrid") throw new ArgumentException("mode 必须为 centerline、outline 或 hybrid。");
                using (var source = new Bitmap(input))
                {
                    var result = new VectorResult { Success = true, Mode = mode, Width = source.Width, Height = source.Height };
                    if (mode == "centerline" || mode == "hybrid") result.Centerlines = SkeletonVectorizer.Vectorize(source,
                        ParseInt(Value(args, "--threshold"), 0), ParseInt(Value(args, "--chunk-size"), 10),
                        ParseDouble(Value(args, "--minimum"), 0d), ParseDouble(Value(args, "--merge-gap"), 0d),
                        ParseDouble(Value(args, "--collinear"), 3d), Value(args, "--dump-mask"));
                    if (mode == "outline" || mode == "hybrid")
                    {
                        result.Outlines = RunVTracer(input, output, args);
                        result.WallRegions = WallRegionDetector.Detect(result.Outlines, ParseDouble(Value(args, "--wall-min"), 3d), ParseDouble(Value(args, "--wall-max"), 80d));
                        var patternScale = ParseDouble(Value(args, "--wall-pattern-scale"), 1d);
                        foreach (var wall in result.WallRegions) wall.PatternScale = patternScale;
                        if (HasFlag(args, "--diagnose-wall-overlap")) ReportWallOverlap(result);
                    }
                    Write(output, result); return 0;
                }
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrWhiteSpace(output)) { try { Write(output, new VectorResult { Success = false, Error = exception.GetBaseException().Message }); } catch { } }
                Console.Error.WriteLine(exception.GetBaseException().Message); return 1;
            }
        }

        private static System.Collections.Generic.List<VectorPolyline> RunVTracer(string input, string output, string[] args)
        {
            var executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vtracer.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("未找到 vtracer.exe。", executable);
            var svg = Path.ChangeExtension(output, ".outline.svg"); var simplify = Value(args, "--simplify") ?? "1.5";
            var start = new ProcessStartInfo(executable, Quote(input) + " " + Quote(svg) + " --preset bw --mode spline --simplify " + simplify)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var process = Process.Start(start))
            {
                process.WaitForExit(); var error = process.StandardError.ReadToEnd();
                if (process.ExitCode != 0 || !File.Exists(svg)) throw new InvalidOperationException("VTracer 失败：" + error.Trim());
            }
            return SvgPathParser.Parse(File.ReadAllText(svg), ParseDouble(Value(args, "--curve-step"), 4d));
        }

        private static void Write(string path, VectorResult result)
        {
            var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            var serializer = new DataContractJsonSerializer(typeof(VectorResult)); using (var stream = File.Create(full)) serializer.WriteObject(stream, result);
        }

        private static string Value(string[] args, string name) { for (var index = 0; index < args.Length - 1; index++) if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1]; return null; }

        /// <summary>开关型参数：只要出现即视为开启（不需要跟值）。</summary>
        private static bool HasFlag(string[] args, string name)
        {
            foreach (var argument in args) if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 诊断用：统计有多少中心线落在墙体区域内——这些会在主程序里被丢弃，
        /// 因为它们和"墙体边框+填充"重复，叠在一起会让同一堵墙看起来成一条粗线。
        /// </summary>
        private static void ReportWallOverlap(VectorResult result)
        {
            if (result.WallRegions.Count == 0) { Console.Error.WriteLine("[overlap] 没有墙体区域"); return; }
            var dropped = 0; var droppedPoints = 0; var outside = 0;
            foreach (var line in result.Centerlines)
            {
                if (line.Points.Count < 2) continue;
                var inside = 0;
                foreach (var point in line.Points)
                    foreach (var wall in result.WallRegions)
                        if (ContainsPoint(wall.Outer, point)) { inside++; break; }
                if (inside >= line.Points.Count * 0.8d) { dropped++; droppedPoints += line.Points.Count; }
                else outside++;
            }
            Console.Error.WriteLine("[overlap] 中心线 " + result.Centerlines.Count + " 条"
                + "；会被丢弃（整条落在墙内）" + dropped + " 条，共 " + droppedPoints + " 个顶点"
                + "；保留 " + outside + " 条；墙体区域 " + result.WallRegions.Count + " 个");
        }

        private static bool ContainsPoint(List<VectorPoint> polygon, VectorPoint point)
        {
            var inside = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var a = polygon[index]; var b = polygon[(index + polygon.Count - 1) % polygon.Count];
                var dy = b.Y - a.Y; if (Math.Abs(dy) < 1e-9d) dy = 1e-9d;
                if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / dy + a.X) inside = !inside;
            }
            return inside;
        }        private static int ParseInt(string value, int fallback) { int result; return int.TryParse(value, out result) ? result : fallback; }
        private static double ParseDouble(string value, double fallback) { double result; return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) ? result : fallback; }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }
}
