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
using BatchPdfPublisher.Models;

namespace Wanluo.LineVision.OcrWorker
{
    internal static class Program
    {
        private const string EngineId = "windows-ocr-worker";
        private const string EngineVersion = "2";

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
                var requestPath = Value(args, "--request");
                var output = Value(args, "--output");
                var request = Read<LineVisionOcrWorkerRequest>(requestPath);
                if (request == null) throw new ArgumentException("缺少或无法读取 --request JSON 文件。");
                if (request.ProtocolVersion != LineVisionOcrProtocol.CurrentVersion)
                    throw new InvalidDataException("不支持的 OCR 协议版本：" + request.ProtocolVersion + "。");
                WriteProgress(request.RequestId, 5, "validate", "OCR 请求和图片校验完成");
                var input = request.ImagePath;
                var language = request.Language ?? "zh-Hans-CN";
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new FileNotFoundException("OCR 输入图片不存在。", input);
                if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("缺少 --output 参数。");
                var result = RecognizeAsync(input, language, request.RequestId).GetAwaiter().GetResult();
                Write(output, result);
                return result.Success ? 0 : 2;
            }
            catch (Exception exception)
            {
                var output = Value(args, "--output");
                var request = Read<LineVisionOcrWorkerRequest>(Value(args, "--request"));
                var result = NewResult(request == null ? null : request.RequestId);
                result.Success = false;
                result.Error = exception.GetBaseException().Message;
                if (!string.IsNullOrWhiteSpace(output)) { try { Write(output, result); } catch { } }
                Console.Error.WriteLine(result.Error);
                return 1;
            }
        }

        private static async Task<LineVisionOcrWorkerResult> RecognizeAsync(string path, string languageTag, string requestId)
        {
            WriteProgress(requestId, 12, "language", "正在选择 Windows OCR 语言……");
            var available = OcrEngine.AvailableRecognizerLanguages.ToList();
            var selected = available.FirstOrDefault(value => string.Equals(value.LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault(value => value.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault(value => value.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (selected == null) { var missing = NewResult(requestId); missing.Error = "Windows 没有安装可用的 OCR 语言包。请在系统语言设置中安装中文或英文 OCR。"; return missing; }
            var engine = OcrEngine.TryCreateFromLanguage(new Language(selected.LanguageTag));
            if (engine == null) { var missing = NewResult(requestId); missing.Error = "无法创建 Windows OCR 引擎：" + selected.LanguageTag; return missing; }
            WriteProgress(requestId, 30, "image", "正在读取图片……");
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using (var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore))
                {
                    WriteProgress(requestId, 55, "inference", "正在识别文字……");
                    var recognized = await engine.RecognizeAsync(bitmap);
                    WriteProgress(requestId, 88, "geometry", "正在整理文字位置……");
                    var result = NewResult(requestId);
                    result.Success = true; result.Language = selected.LanguageTag; result.ImageWidth = (int)decoder.PixelWidth; result.ImageHeight = (int)decoder.PixelHeight;
                    foreach (var line in recognized.Lines)
                    {
                        if (line.Words.Count == 0) continue;
                        var left = line.Words.Min(word => word.BoundingRect.X);
                        var top = line.Words.Min(word => word.BoundingRect.Y);
                        var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
                        var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
                        result.TextRegions.Add(new LineVisionOcrWorkerTextRegion
                        {
                            Text = string.Join(" ", line.Words.Select(word => word.Text)),
                            X = left, Y = top, Width = Math.Max(1d, right - left), Height = Math.Max(1d, bottom - top),
                            RotationDegrees = recognized.TextAngle.HasValue ? recognized.TextAngle.Value : 0d,
                            Confidence = 0.85d
                        });
                    }
                    WriteProgress(requestId, 100, "complete", "文字识别完成");
                    return result;
                }
            }
        }

        private static string Value(string[] args, string name)
        {
            for (var index = 0; index < args.Length - 1; index++) if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }

        private static LineVisionOcrWorkerResult NewResult(string requestId)
        {
            return new LineVisionOcrWorkerResult { ProtocolVersion = LineVisionOcrProtocol.CurrentVersion, RequestId = requestId, EngineId = EngineId, EngineVersion = EngineVersion };
        }

        private static T Read<T>(string path) where T : class
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try { var serializer = new DataContractJsonSerializer(typeof(T)); using (var stream = File.OpenRead(path)) return serializer.ReadObject(stream) as T; }
            catch { return null; }
        }

        private static void Write(string path, LineVisionOcrWorkerResult result)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)); if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var serializer = new DataContractJsonSerializer(typeof(LineVisionOcrWorkerResult));
            using (var stream = File.Create(path)) serializer.WriteObject(stream, result);
        }

        private static void WriteProgress(string requestId, int percent, string stage, string message)
        {
            var progress = new LineVisionOcrWorkerProgress
            {
                ProtocolVersion = LineVisionOcrProtocol.CurrentVersion,
                RequestId = requestId,
                Type = "progress",
                Percent = Math.Max(0, Math.Min(100, percent)),
                Stage = stage,
                Message = message
            };
            var serializer = new DataContractJsonSerializer(typeof(LineVisionOcrWorkerProgress));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, progress);
                Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

}
