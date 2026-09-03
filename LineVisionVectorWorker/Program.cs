using System;
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
                    if (mode == "centerline" || mode == "hybrid") result.Centerlines = SkeletonVectorizer.Vectorize(source, ParseInt(Value(args, "--threshold"), 0), ParseInt(Value(args, "--chunk-size"), 10));
                    if (mode == "outline" || mode == "hybrid")
                    {
                        result.Outlines = RunVTracer(input, output, args);
                        result.WallRegions = WallRegionDetector.Detect(result.Outlines, ParseDouble(Value(args, "--wall-min"), 3d), ParseDouble(Value(args, "--wall-max"), 80d));
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
        private static int ParseInt(string value, int fallback) { int result; return int.TryParse(value, out result) ? result : fallback; }
        private static double ParseDouble(string value, double fallback) { double result; return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) ? result : fallback; }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }
}
