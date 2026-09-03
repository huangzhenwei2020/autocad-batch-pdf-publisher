using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Wanluo.LineVision.OcrWorker
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length > 0 && string.Equals(args[0], "--languages", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var installedLanguage in OcrEngine.AvailableRecognizerLanguages) Console.WriteLine(installedLanguage.LanguageTag + "|" + installedLanguage.DisplayName);
                    return 0;
                }
                var input = Value(args, "--input");
                var output = Value(args, "--output");
                var language = Value(args, "--language") ?? "zh-Hans-CN";
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new FileNotFoundException("OCR 输入图片不存在。", input);
                if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("缺少 --output 参数。");
                var result = RecognizeAsync(input, language).GetAwaiter().GetResult();
                Write(output, result);
                return result.Success ? 0 : 2;
            }
            catch (Exception exception)
            {
                var output = Value(args, "--output");
                var result = new WorkerResult { Success = false, Error = exception.GetBaseException().Message };
                if (!string.IsNullOrWhiteSpace(output)) { try { Write(output, result); } catch { } }
                Console.Error.WriteLine(result.Error);
                return 1;
            }
        }

        private static async Task<WorkerResult> RecognizeAsync(string path, string languageTag)
        {
            var available = OcrEngine.AvailableRecognizerLanguages.ToList();
            var selected = available.FirstOrDefault(value => string.Equals(value.LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault(value => value.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault(value => value.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (selected == null) return new WorkerResult { Success = false, Error = "Windows 没有安装可用的 OCR 语言包。请在系统语言设置中安装中文或英文 OCR。" };
            var engine = OcrEngine.TryCreateFromLanguage(new Language(selected.LanguageTag));
            if (engine == null) return new WorkerResult { Success = false, Error = "无法创建 Windows OCR 引擎：" + selected.LanguageTag };
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using (var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore))
                {
                    var recognized = await engine.RecognizeAsync(bitmap);
                    var result = new WorkerResult { Success = true, Language = selected.LanguageTag, ImageWidth = (int)decoder.PixelWidth, ImageHeight = (int)decoder.PixelHeight };
                    foreach (var line in recognized.Lines)
                    {
                        if (line.Words.Count == 0) continue;
                        var left = line.Words.Min(word => word.BoundingRect.X);
                        var top = line.Words.Min(word => word.BoundingRect.Y);
                        var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                        var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                        result.TextRegions.Add(new WorkerTextRegion
                        {
                            Text = string.Join(" ", line.Words.Select(word => word.Text)),
                            X = left, Y = top, Width = Math.Max(1d, right - left), Height = Math.Max(1d, bottom - top),
                            RotationDegrees = recognized.TextAngle.HasValue ? recognized.TextAngle.Value : 0d,
                            Confidence = 0.85d
                        });
                    }
                    return result;
                }
            }
        }

        private static string Value(string[] args, string name)
        {
            for (var index = 0; index < args.Length - 1; index++) if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }

        private static void Write(string path, WorkerResult result)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)); if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var serializer = new DataContractJsonSerializer(typeof(WorkerResult));
            using (var stream = File.Create(path)) serializer.WriteObject(stream, result);
        }
    }

    [DataContract]
    internal sealed class WorkerResult
    {
        [DataMember(Order = 1)] public bool Success { get; set; }
        [DataMember(Order = 2)] public string Error { get; set; }
        [DataMember(Order = 3)] public string Language { get; set; }
        [DataMember(Order = 4)] public int ImageWidth { get; set; }
        [DataMember(Order = 5)] public int ImageHeight { get; set; }
        [DataMember(Order = 6)] public List<WorkerTextRegion> TextRegions { get; set; } = new List<WorkerTextRegion>();
    }

    [DataContract]
    internal sealed class WorkerTextRegion
    {
        [DataMember(Order = 1)] public string Text { get; set; }
        [DataMember(Order = 2)] public double X { get; set; }
        [DataMember(Order = 3)] public double Y { get; set; }
        [DataMember(Order = 4)] public double Width { get; set; }
        [DataMember(Order = 5)] public double Height { get; set; }
        [DataMember(Order = 6)] public double RotationDegrees { get; set; }
        [DataMember(Order = 7)] public double Confidence { get; set; }
    }
}
