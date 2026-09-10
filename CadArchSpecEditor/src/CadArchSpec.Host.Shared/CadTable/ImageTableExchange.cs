using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using CadArchSpec.CadTable;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal static class ImageTableExchange
    {
        private const int ProtocolVersion = 2;

        public static async Task<JObject> ReadImageTableAsync(IWin32Window owner, CancellationToken cancellationToken)
        {
            string imagePath;
            using (var dialog = new OpenFileDialog
            {
                Title = "选择图片或扫描表格",
                Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return new JObject { ["cancelled"] = true };
                imagePath = dialog.FileName;
            }

            return await Task.Run(() => ReadImageTableCore(imagePath, cancellationToken), cancellationToken);
        }

        private static JObject ReadImageTableCore(string imagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = ReadRasterGrid(imagePath);
            cancellationToken.ThrowIfCancellationRequested();
            var ocr = RunOcr(imagePath, cancellationToken);
            var imageHeight = (int?)ocr["ImageHeight"] ?? 0;
            var regions = ocr["TextRegions"] as JArray ?? new JArray();
            foreach (var token in regions.OfType<JObject>())
            {
                var text = ((string)token["Text"] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var x = (double?)token["X"] ?? 0d;
                var y = (double?)token["Y"] ?? 0d;
                var width = Math.Max(1d, (double?)token["Width"] ?? 1d);
                var height = Math.Max(1d, (double?)token["Height"] ?? 1d);
                input.TextFragments.Add(new CadTextFragment
                {
                    Text = text,
                    PlainText = text,
                    Center = new CadTablePoint(x + width * 0.5d, imageHeight - y - height * 0.5d),
                    Left = x,
                    Right = x + width,
                    Bottom = imageHeight - y - height,
                    Top = imageHeight - y,
                    Width = width,
                    Height = height,
                    HasBounds = true,
                    RotationDegrees = -((double?)token["RotationDegrees"] ?? 0d),
                    Confidence = (double?)token["Confidence"] ?? 0d,
                    SourceKind = CadTextSourceKind.OcrFallback,
                    SourceDxfName = "RASTER_OCR"
                });
            }

            if (input.Segments.Count == 0)
                throw new InvalidOperationException("图片中没有检测到连续的横竖表格线。当前版本先支持有线表格，不能把普通图片误判成表格。");
            if (input.TextFragments.Count == 0)
                throw new InvalidOperationException("图片中没有识别到文字，未生成空白表格。");

            var medianHeight = input.TextFragments.Select(item => item.Height).OrderBy(value => value).ElementAt(input.TextFragments.Count / 2);
            var detected = new OrthogonalCadTableDetector().Detect(input, new CadTableDetectionOptions
            {
                CoordinateTolerance = Math.Max(1.5d, medianHeight * 0.08d),
                MaximumBorderGap = Math.Max(3d, medianHeight * 0.35d),
                OrthogonalAngleToleranceDegrees = 1.5d,
                DetectOverallRotation = false
            });
            if (detected.ColumnBoundaries.Count < 2 || detected.RowBoundaries.Count < 2 || detected.Cells.Count == 0)
                throw new InvalidOperationException("没有从图片中识别到闭合表格。请裁剪到单张、边框清晰且基本水平的有线表格后重试。");

            var lowConfidence = input.TextFragments.Count(item => item.Confidence < 0.8d);
            var warnings = detected.Warnings.ToList();
            if (lowConfidence > 0) warnings.Add(lowConfidence + " 个低置信度文字必须人工确认。");
            if (detected.UnassignedText.Count > 0) warnings.Add(detected.UnassignedText.Count + " 个文字未能可靠归入单元格。");
            warnings.Add("扫描图片识别属于后备流程，所有单元格在导出前均需人工复核。");
            return BuildPayload(imagePath, ocr, detected, warnings.Distinct().ToArray());
        }

        private static CadTableDetectionInput ReadRasterGrid(string imagePath)
        {
            using (var source = new Bitmap(imagePath))
            using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(source, 0, 0);
                var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    var histogram = new int[256];
                    var luminance = new byte[bitmap.Width * bitmap.Height];
                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        var row = data.Stride >= 0 ? y * data.Stride : (bitmap.Height - 1 - y) * -data.Stride;
                        for (var x = 0; x < bitmap.Width; x++)
                        {
                            var offset = row + x * 3;
                            var value = (byte)((bytes[offset + 2] * 299 + bytes[offset + 1] * 587 + bytes[offset] * 114) / 1000);
                            luminance[y * bitmap.Width + x] = value;
                            histogram[value]++;
                        }
                    }
                    var threshold = OtsuThreshold(histogram, luminance.Length);
                    var edgeAverage = EdgeAverage(luminance, bitmap.Width, bitmap.Height);
                    var darkOnLight = edgeAverage >= threshold;
                    var foreground = new bool[luminance.Length];
                    for (var i = 0; i < luminance.Length; i++)
                        foreground[i] = darkOnLight ? luminance[i] <= threshold : luminance[i] >= threshold;
                    return RasterTableGridExtractor.Extract(bitmap.Width, bitmap.Height, foreground);
                }
                finally { bitmap.UnlockBits(data); }
            }
        }

        private static int OtsuThreshold(IReadOnlyList<int> histogram, int total)
        {
            long sum = 0;
            for (var i = 0; i < 256; i++) sum += (long)i * histogram[i];
            long backgroundSum = 0;
            var backgroundWeight = 0;
            var best = 127;
            var maximum = -1d;
            for (var i = 0; i < 256; i++)
            {
                backgroundWeight += histogram[i];
                if (backgroundWeight == 0) continue;
                var foregroundWeight = total - backgroundWeight;
                if (foregroundWeight == 0) break;
                backgroundSum += (long)i * histogram[i];
                var backgroundMean = backgroundSum / (double)backgroundWeight;
                var foregroundMean = (sum - backgroundSum) / (double)foregroundWeight;
                var variance = (double)backgroundWeight * foregroundWeight * Math.Pow(backgroundMean - foregroundMean, 2d);
                if (variance > maximum) { maximum = variance; best = i; }
            }
            return Math.Max(16, Math.Min(239, best));
        }

        private static double EdgeAverage(IReadOnlyList<byte> values, int width, int height)
        {
            long sum = 0;
            long count = 0;
            var edge = Math.Max(1, Math.Min(width, height) / 50);
            for (var y = 0; y < height; y += Math.Max(1, height - edge))
                for (var x = 0; x < width; x++) { sum += values[y * width + x]; count++; }
            for (var x = 0; x < width; x += Math.Max(1, width - edge))
                for (var y = 0; y < height; y++) { sum += values[y * width + x]; count++; }
            return count == 0 ? 255d : sum / (double)count;
        }

        private static JObject RunOcr(string imagePath, CancellationToken cancellationToken)
        {
            var worker = ResolveWorkerPath();
            if (string.IsNullOrWhiteSpace(worker) || !File.Exists(worker))
                throw new FileNotFoundException("未找到 PaddleOCR 增强组件。请使用包含 OCR 的最新版启动器重新安装后再读取扫描表格。", worker);
            var requestId = Guid.NewGuid().ToString("N");
            var tempDirectory = Path.Combine(Path.GetTempPath(), "WanluoArchitectureTools", "ImageTable", requestId);
            Directory.CreateDirectory(tempDirectory);
            var requestPath = Path.Combine(tempDirectory, "request.json");
            var outputPath = Path.Combine(tempDirectory, "result.json");
            try
            {
                File.WriteAllText(requestPath, new JObject
                {
                    ["ProtocolVersion"] = ProtocolVersion,
                    ["RequestId"] = requestId,
                    ["ImagePath"] = imagePath,
                    ["Language"] = "zh-Hans-CN"
                }.ToString(Formatting.None));
                var startInfo = new ProcessStartInfo(worker, "--request \"" + requestPath + "\" --output \"" + outputPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(worker)
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) throw new InvalidOperationException("无法启动 PaddleOCR 表格文字识别组件。");
                    var outputRead = process.StandardOutput.ReadToEndAsync();
                    var errorRead = process.StandardError.ReadToEndAsync();
                    var deadline = DateTime.UtcNow.AddMinutes(5);
                    while (!process.WaitForExit(200))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            try { process.Kill(); } catch { }
                            throw new TimeoutException("扫描表格文字识别超过 5 分钟，已安全停止。");
                        }
                    }
                    Task.WaitAll(outputRead, errorRead);
                    if (!File.Exists(outputPath))
                        throw new InvalidDataException("PaddleOCR 没有返回识别结果：" + errorRead.Result.Trim());
                }
                var result = JObject.Parse(File.ReadAllText(outputPath));
                if ((int?)result["ProtocolVersion"] != ProtocolVersion || (string)result["RequestId"] != requestId)
                    throw new InvalidDataException("PaddleOCR 返回的协议版本或任务编号不匹配，结果未采用。");
                if ((bool?)result["Success"] != true)
                    throw new InvalidDataException("PaddleOCR 识别失败：" + ((string)result["Error"] ?? "未知错误"));
                return result;
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        private static string ResolveWorkerPath()
        {
            var configured = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var candidates = new[]
            {
                Path.Combine(directory, "LineVisionPaddleOcrWorker", "LineVisionPaddleOcrWorker.exe"),
                Path.Combine(directory, "OcrEngine", "LineVisionPaddleOcrWorker", "LineVisionPaddleOcrWorker.exe"),
                Path.GetFullPath(Path.Combine(directory, "..", "..", "OcrEngine", "LineVisionPaddleOcrWorker", "LineVisionPaddleOcrWorker.exe"))
            };
            return candidates.FirstOrDefault(File.Exists) ?? configured ?? candidates[0];
        }

        private static JObject BuildPayload(string imagePath, JObject ocr, CadTableDetectionResult detected, string[] warnings)
        {
            var columnCount = detected.ColumnBoundaries.Count - 1;
            var rowCount = detected.RowBoundaries.Count - 1;
            var widths = CadTableColumnWidthNormalizer.Normalize(Enumerable.Range(0, columnCount)
                .Select(i => Math.Abs(detected.ColumnBoundaries[i + 1] - detected.ColumnBoundaries[i])));
            var columns = new JArray(Enumerable.Range(0, columnCount).Select(i => new JObject
            {
                ["key"] = "column" + (i + 1), ["title"] = ColumnName(i), ["unit"] = string.Empty,
                ["widthMillimeters"] = widths[i], ["decimalPlaces"] = 0, ["required"] = false
            }));
            var rows = new JArray();
            for (var row = 0; row < rowCount; row++)
            {
                var cells = new JArray();
                for (var column = 0; column < columnCount; column++)
                {
                    var anchor = detected.Cells.FirstOrDefault(cell => cell.RowIndex == row && cell.ColumnIndex == column);
                    var covering = anchor ?? detected.Cells.FirstOrDefault(cell => row >= cell.RowIndex && row < cell.RowIndex + cell.RowSpan && column >= cell.ColumnIndex && column < cell.ColumnIndex + cell.ColumnSpan);
                    var value = anchor?.Text ?? string.Empty;
                    double number;
                    cells.Add(new JObject
                    {
                        ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"), ["columnKey"] = "column" + (column + 1),
                        ["displayValue"] = value, ["numericValue"] = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? (JToken)number : JValue.CreateNull(),
                        ["unit"] = string.Empty, ["fieldPath"] = string.Empty, ["formula"] = string.Empty,
                        ["state"] = "pending", ["source"] = "扫描图片 OCR，待人工复核", ["sourceHandles"] = new JArray(),
                        ["rowSpan"] = anchor != null ? anchor.RowSpan : covering != null ? 0 : 1,
                        ["columnSpan"] = anchor != null ? anchor.ColumnSpan : covering != null ? 0 : 1
                    });
                }
                rows.Add(new JObject { ["rowId"] = "row-" + Guid.NewGuid().ToString("N"), ["rowType"] = "Data", ["keepTogether"] = true, ["cells"] = cells });
            }
            return new JObject
            {
                ["rowCount"] = rowCount, ["columnCount"] = columnCount, ["nativeTable"] = false,
                ["imageTable"] = true, ["sourceImagePath"] = imagePath, ["textCount"] = ((JArray)ocr["TextRegions"])?.Count ?? 0,
                ["warnings"] = JArray.FromObject(warnings),
                ["table"] = new JObject
                {
                    ["tableId"] = "table-" + Guid.NewGuid().ToString("N"), ["schemaVersion"] = 1, ["tableType"] = "custom",
                    ["tableNumber"] = string.Empty, ["title"] = Path.GetFileNameWithoutExtension(imagePath) + "－扫描识别表格",
                    ["sourceDrawingPath"] = string.Empty, ["repeatHeader"] = true, ["allowSplitAcrossPages"] = true,
                    ["columns"] = columns, ["rows"] = rows, ["formulaAudits"] = new JArray()
                }
            };
        }

        private static string ColumnName(int index)
        {
            var value = index + 1;
            var name = string.Empty;
            while (value > 0) { value--; name = (char)('A' + value % 26) + name; value /= 26; }
            return name;
        }
    }
}
