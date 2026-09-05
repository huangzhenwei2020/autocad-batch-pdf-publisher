using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    internal sealed class LineVisionOcrWorkerClient : ILineVisionOcrEngine
    {
        private readonly string _workerPath;
        private static string CacheDirectory { get { var path = Path.Combine(UserDataPaths.RootDirectory, "识别缓存", "LineVisionOCR"); Directory.CreateDirectory(path); return path; } }

        public LineVisionOcrWorkerClient(string workerPath = null)
        {
            _workerPath = string.IsNullOrWhiteSpace(workerPath) ? Path.Combine(Path.GetDirectoryName(typeof(LineVisionOcrWorkerClient).Assembly.Location), "LineVisionOcrWorker.exe") : workerPath;
        }

        public string DisplayName { get { return "Windows 本地 OCR（独立进程）"; } }
        public bool IsAvailable { get { return File.Exists(_workerPath); } }

        public async Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken)
        {
            if (!IsAvailable) throw new FileNotFoundException("未找到本地 OCR Worker。请重新运行最新版启动器。", _workerPath);
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) throw new FileNotFoundException("OCR 图片不存在。", imagePath);
            options = options ?? new LineVisionOcrOptions();
            var cachePath = Path.Combine(CacheDirectory, BuildCacheKey(imagePath, options) + ".json");
            var cached = TryRead(cachePath);
            if (cached != null && cached.Success) return Convert(cached, options.MinimumConfidence);

            var operation = Path.Combine(UserDataPaths.TemporaryDirectory, "linevision-ocr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operation);
            var input = imagePath; var output = Path.Combine(operation, "result.json");
            try
            {
                if (options.SourceRegion.HasValue)
                {
                    input = Path.Combine(operation, "crop.png");
                    SaveCrop(imagePath, options.SourceRegion.Value, input);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo
                {
                    FileName = _workerPath,
                    Arguments = "--input " + Quote(input) + " --output " + Quote(output) + " --language " + Quote(options.Language),
                    WorkingDirectory = Path.GetDirectoryName(_workerPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("无法启动本地 OCR Worker。");
                    var started = DateTime.UtcNow;
                    while (!process.HasExited)
                    {
                        if (cancellationToken.IsCancellationRequested || DateTime.UtcNow - started > TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)))
                        {
                            try { process.Kill(); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new TimeoutException("本地 OCR 超时，请裁剪较小范围后重试。");
                        }
                        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                    }
                    var errorText = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    var dto = TryRead(output);
                    if (dto == null) throw new InvalidDataException("OCR Worker 没有返回有效结果。" + (string.IsNullOrWhiteSpace(errorText) ? string.Empty : "\r\n" + errorText.Trim()));
                    if (!dto.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(dto.Error) ? "OCR 识别失败。" : dto.Error);
                    try { File.Copy(output, cachePath, true); } catch { }
                    return Convert(dto, options.MinimumConfidence);
                }
            }
            finally { try { Directory.Delete(operation, true); } catch { } }
        }

        private static LineVisionOcrPageResult Convert(WorkerResult dto, double minimumConfidence)
        {
            var result = new LineVisionOcrPageResult { Language = dto.Language };
            foreach (var item in dto.TextRegions ?? new List<WorkerTextRegion>())
            {
                var text = Normalize(item.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;
                result.TextRegions.Add(new LineVisionOcrTextRegion
                {
                    Text = text, OriginalText = item.Text,
                    Polygon = new[] { new PointF((float)item.X, (float)item.Y), new PointF((float)(item.X + item.Width), (float)item.Y), new PointF((float)(item.X + item.Width), (float)(item.Y + item.Height)), new PointF((float)item.X, (float)(item.Y + item.Height)) },
                    RotationDegrees = item.RotationDegrees, Confidence = item.Confidence,
                    IsEnabled = item.Confidence >= minimumConfidence
                });
            }
            return result;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return string.Join(" ", text.Replace('\u3000', ' ').Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static void SaveCrop(string path, Rectangle requested, string output)
        {
            using (var source = new Bitmap(path))
            {
                var region = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), requested);
                if (region.Width < 2 || region.Height < 2) throw new InvalidOperationException("OCR 框选范围无效。");
                using (var cropped = source.Clone(region, PixelFormat.Format24bppRgb)) cropped.Save(output, ImageFormat.Png);
            }
        }

        private static string BuildCacheKey(string path, LineVisionOcrOptions options)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var fileHash = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty);
                var region = options.SourceRegion.HasValue ? options.SourceRegion.Value.ToString() : "all";
                var value = fileHash + "|" + region + "|" + options.Language + "|" + options.MinimumConfidence.ToString("R", CultureInfo.InvariantCulture);
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
            }
        }

        private static WorkerResult TryRead(string path)
        {
            if (!File.Exists(path)) return null;
            try { var serializer = new DataContractJsonSerializer(typeof(WorkerResult)); using (var stream = File.OpenRead(path)) return serializer.ReadObject(stream) as WorkerResult; }
            catch { return null; }
        }

        private static string Quote(string value) { return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\""; }

        [DataContract]
        private sealed class WorkerResult
        {
            [DataMember(Order = 1)] public bool Success { get; set; }
            [DataMember(Order = 2)] public string Error { get; set; }
            [DataMember(Order = 3)] public string Language { get; set; }
            [DataMember(Order = 4)] public int ImageWidth { get; set; }
            [DataMember(Order = 5)] public int ImageHeight { get; set; }
            [DataMember(Order = 6)] public List<WorkerTextRegion> TextRegions { get; set; }
        }

        [DataContract]
        private sealed class WorkerTextRegion
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
}
