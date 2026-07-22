using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PdfPublishResult
    {
        public List<string> Files { get; } = new List<string>();
        public int SheetCount { get; set; }
    }

    public sealed class PdfPublishProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string SheetLabel { get; set; }
    }

    public sealed class SheetValidationIssue
    {
        public SheetItem Sheet { get; set; }
        public string Message { get; set; }
    }

    public sealed class PdfPublisherService
    {
        private static readonly string DiagnosticLogPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.publish.log");

        public List<SheetValidationIssue> ValidateAndNormalizeSheets(IEnumerable<SheetItem> sourceSheets)
        {
            var issues = new List<SheetValidationIssue>();
            foreach (var sheet in sourceSheets?.Where(x => x != null) ?? Enumerable.Empty<SheetItem>())
            {
                var width = Math.Abs(sheet.MaxX - sheet.MinX);
                var height = Math.Abs(sheet.MaxY - sheet.MinY);
                if (width < 0.0001d || height < 0.0001d || double.IsNaN(width) || double.IsNaN(height) || double.IsInfinity(width) || double.IsInfinity(height))
                {
                    issues.Add(new SheetValidationIssue { Sheet = sheet, Message = "图框范围无效。建议删除该条目后重新扫描，或重新登记这个图块。" });
                    continue;
                }

                var target = TargetPaperSize(sheet);
                var expected = Math.Max(target[0], target[1]) / Math.Min(target[0], target[1]);
                var actual = Math.Max(width, height) / Math.Min(width, height);
                if (Math.Abs(actual - expected) / expected <= .02d)
                {
                    // The ratio is valid; derive orientation from the geometry
                    // so a frame rotated by 90 degrees needs no manual edit.
                    sheet.PaperOrientation = width >= height ? "横向" : "纵向";
                    continue;
                }

                var guess = FrameSizeDetector.Guess(new Extents3d(
                    new Point3d(sheet.MinX, sheet.MinY, 0d),
                    new Point3d(sheet.MaxX, sheet.MaxY, 0d)), sheet.PrintScale);
                var suggestedFrame = guess.PaperSize + (string.IsNullOrWhiteSpace(guess.Extension) ? string.Empty : "+" + guess.Extension);
                issues.Add(new SheetValidationIssue
                {
                    Sheet = sheet,
                    Message = $"实际长宽比 {actual:0.###} 与登记的 {sheet.FrameDisplay}（{expected:0.###}）不一致。建议把图框规格改为 {suggestedFrame}、方向改为{guess.PaperOrientation}、打印比例检查为 {guess.PrintScale}；如果建议不对，请双击对应图框登记修改纸张或加长倍数。"
                });
            }
            return issues;
        }

        public PdfPublishResult Publish(Document document, IEnumerable<SheetItem> sourceSheets, ProjectProfile project, Action<PdfPublishProgress> progress = null)
        {
            if (document == null) throw new InvalidOperationException("没有打开的图纸。");
            var sheets = sourceSheets?.Where(x => x != null).OrderBy(x => x.Building).ThenBy(PublishPriority).ThenBy(x => x.Order).ToList() ?? new List<SheetItem>();
            if (sheets.Count == 0) throw new InvalidOperationException("图纸列表为空，请先扫描当前图纸。");
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("AutoCAD 正在执行其他打印任务，请稍后再试。");

            var outputRoot = string.IsNullOrWhiteSpace(project?.OutputDirectory) ? @"D:\PDF输出" : project.OutputDirectory;
            var engineeringFolder = Path.Combine(outputRoot, SafeName(project?.Name ?? "默认工程"));
            Directory.CreateDirectory(engineeringFolder);
            var result = new PdfPublishResult();
            var jobs = BuildJobs(sheets, project?.MergeByBuilding ?? true, project?.Name ?? "默认工程", engineeringFolder);
            var completed = 0;
            var previousBackgroundPlot = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("BACKGROUNDPLOT");
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            try
            {
                foreach (var job in jobs)
                {
                    PlotGroup(document, job.Value, job.Key, project?.PlotStyle, project?.MarginMode, sheet =>
                    {
                        completed++;
                        progress?.Invoke(new PdfPublishProgress
                        {
                            Current = completed,
                            Total = sheets.Count,
                            SheetLabel = string.Join(" · ", new[] { sheet.Building, sheet.SheetNumber, sheet.SheetName }.Where(x => !string.IsNullOrWhiteSpace(x)))
                        });
                    });
                    result.Files.Add(job.Key);
                    result.SheetCount += job.Value.Count;
                }
            }
            finally
            {
                try { Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("BACKGROUNDPLOT", previousBackgroundPlot); } catch { }
            }
            return result;
        }

        private static List<KeyValuePair<string, List<SheetItem>>> BuildJobs(List<SheetItem> sheets, bool mergeByBuilding, string projectName, string outputRoot)
        {
            var jobs = new List<KeyValuePair<string, List<SheetItem>>>();
            if (mergeByBuilding)
            {
                foreach (var group in sheets.GroupBy(x => x.Building))
                {
                    var name = SafeName(projectName) + "_" + SafeName(group.Key) + ".pdf";
                    jobs.Add(new KeyValuePair<string, List<SheetItem>>(UniquePath(Path.Combine(outputRoot, name)), group.OrderBy(PublishPriority).ThenBy(x => x.Order).ToList()));
                }
            }
            else
            {
                foreach (var sheet in sheets)
                {
                    var name = string.Join("_", new[]
                    {
                        SafeName(projectName), SafeName(sheet.Building), sheet.Order.ToString("D3"),
                        SafeName(sheet.SheetNumber), SafeName(sheet.SheetName)
                    }.Where(x => !string.IsNullOrWhiteSpace(x))) + ".pdf";
                    jobs.Add(new KeyValuePair<string, List<SheetItem>>(UniquePath(Path.Combine(outputRoot, name)), new List<SheetItem> { sheet }));
                }
            }
            return jobs;
        }

        private static void PlotGroup(Document document, IList<SheetItem> sheets, string outputPath, string defaultPlotStyle, string marginMode, Action<SheetItem> pagePublished)
        {
            var temporaryFiles = new List<string>();
            try
            {
                for (var index = 0; index < sheets.Count; index++)
                {
                    var temporaryPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher_" + Guid.NewGuid().ToString("N") + ".pdf");
                    temporaryFiles.Add(temporaryPath);
                    PlotSinglePage(document, sheets[index], temporaryPath, defaultPlotStyle, marginMode, index);
                    pagePublished?.Invoke(sheets[index]);
                }
                PdfMerger.Merge(temporaryFiles, sheets, outputPath, marginMode);
            }
            finally
            {
                foreach (var file in temporaryFiles)
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        private static void PlotSinglePage(Document document, SheetItem sheet, string outputPath, string defaultPlotStyle, string marginMode, int index)
        {
            if (sheet == null) throw new InvalidOperationException("图纸列表中存在空记录，请重新扫描当前图纸。");
            var label = string.Join(" · ", new[] { sheet.Building, sheet.SheetNumber, sheet.SheetName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var stage = "检查图框尺寸";
            try
            {
                ValidateDeclaredFrameRatio(sheet);
                using (document.LockDocument())
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    stage = "读取当前模型/布局";
                    var currentSpace = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    if (currentSpace == null || currentSpace.LayoutId.IsNull)
                        throw new InvalidOperationException("无法取得当前模型空间或布局，请激活要发布的图纸后重试。");
                    var layout = transaction.GetObject(currentSpace.LayoutId, OpenMode.ForRead) as Layout;
                    if (layout == null)
                        throw new InvalidOperationException("当前空间没有有效的打印布局。");

                    stage = "创建打印设置";
                    using (var settings = CreateSettings(layout, sheet, defaultPlotStyle, marginMode))
                    using (var plotInfo = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings })
                    {
                        stage = "校验打印信息";
                        using (var validator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled })
                            validator.Validate(plotInfo);

                        stage = "创建 PDF 打印引擎";
                        using (var engine = PlotFactory.CreatePublishEngine())
                        {
                            if (engine == null) throw new InvalidOperationException("AutoCAD 未能创建 PDF 打印引擎，请确认没有其他打印任务正在运行。");
                            stage = "开始打印";
                            engine.BeginPlot(null, null);
                            engine.BeginDocument(plotInfo, document.Name, null, 1, true, outputPath);
                            using (var pageInfo = new PlotPageInfo())
                            {
                                stage = "创建 PDF 页面";
                                engine.BeginPage(pageInfo, plotInfo, true, null);
                                stage = "生成页面图形";
                                engine.BeginGenerateGraphics(null);
                                engine.EndGenerateGraphics(null);
                                engine.EndPage(null);
                            }
                            engine.EndDocument(null);
                            engine.EndPlot(null);
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (Exception exception)
            {
                var detail = $"第 {index + 1} 张（{label}）在“{stage}”时失败：{exception.Message}";
                WriteDiagnostic(detail, exception);
                throw new InvalidOperationException(detail, exception);
            }
        }

        private static void WriteDiagnostic(string message, Exception exception)
        {
            try
            {
                File.AppendAllText(DiagnosticLogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine + exception + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        private static class PdfMerger
        {
            public static void Merge(IList<string> files, IList<SheetItem> sheets, string outputPath, string marginMode)
            {
                var marginMillimeters = string.Equals(marginMode, "保留 3 mm 白边", StringComparison.OrdinalIgnoreCase) ? 3d : 0d;
                using (var output = new PdfSharp.Pdf.PdfDocument())
                {
                    for (var index = 0; index < files.Count; index++)
                    using (var input = PdfSharp.Pdf.IO.PdfReader.Open(files[index], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    {
                        var source = input.Pages[0];
                        var target = TargetPaperSize(sheets[index]);
                        var page = output.AddPage();
                        page.Width = PdfSharp.Drawing.XUnit.FromMillimeter(target[0]);
                        page.Height = PdfSharp.Drawing.XUnit.FromMillimeter(target[1]);
                        using (var form = PdfSharp.Drawing.XPdfForm.FromFile(files[index]))
                        using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
                        {
                            form.PageNumber = 1;
                            var crop = CenterCrop(source.Width.Point, source.Height.Point, target[0] / target[1]);
                            var inset = PdfSharp.Drawing.XUnit.FromMillimeter(marginMillimeters).Point;
                            var width = Math.Max(1d, page.Width.Point - inset * 2d);
                            var height = Math.Max(1d, page.Height.Point - inset * 2d);
                            graphics.DrawImage(form, new PdfSharp.Drawing.XRect(inset, inset, width, height), new PdfSharp.Drawing.XRect(crop[0], crop[1], crop[2], crop[3]), PdfSharp.Drawing.XGraphicsUnit.Point);
                        }
                    }
                    output.Save(outputPath);
                }
            }

            private static double[] CenterCrop(double width, double height, double targetRatio)
            {
                if (width / height > targetRatio)
                {
                    var cropWidth = height * targetRatio;
                    return new[] { (width - cropWidth) / 2d, 0d, cropWidth, height };
                }
                var cropHeight = width / targetRatio;
                return new[] { 0d, (height - cropHeight) / 2d, width, cropHeight };
            }
        }

        private static PlotSettings CreateSettings(Layout layout, SheetItem sheet, string defaultPlotStyle, string marginMode)
        {
            var settings = new PlotSettings(layout.ModelType);
            settings.CopyFrom(layout);
            var validator = PlotSettingsValidator.Current;
            var device = ChoosePdfDevice(validator);
            InitializePdfDevice(validator, settings, layout, device);
            var target = TargetPaperSize(sheet);
            var media = ChooseMedia(validator, settings, target[0], target[1], marginMode, !string.IsNullOrWhiteSpace(sheet.Extension));
            if (string.IsNullOrWhiteSpace(media))
                throw new InvalidOperationException($"当前 PDF 绘图仪没有 {PaperSizeCatalog.Describe(sheet.Frame, sheet.Extension, sheet.PaperOrientation)} 的精确纸张。请先关闭 CAD，再双击启动器让它重新部署 BatchPdfPublisher.pc3/pmp；当前进程不会自动刷新绘图仪介质列表。加长图纸不会降级为普通 A1/A0。 ");
            // SetPlotConfigurationName 同时写入设备和有效介质，避免留下一个
            // 设备有效但介质为空的 PlotSettings（AutoCAD 2022 会报 eInvalidPlotInfo）。
            validator.SetPlotConfigurationName(settings, device, media);
            validator.RefreshLists(settings);
            validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
            validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            validator.SetPlotWindowArea(settings, new Extents2d(sheet.MinX, sheet.MinY, sheet.MaxX, sheet.MaxY));
            validator.SetPlotCentered(settings, true);
            // “打印比例”是图纸属性，不能直接作为 CAD 的 PlotScale。若把
            // 1:100 写入 PlotScale，会把 420 mm 的图框缩成 4.2 mm。
            // 始终让选定图框窗口适配页面，才能保证 PDF 与图框比例一致。
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            var mediaSize = ParseMediaSize(media);
            var mediaLandscape = mediaSize != null && mediaSize[0] > mediaSize[1];
            // Derive the requested orientation from the normalized target
            // dimensions, not only from the display text.  This keeps edited
            // rows and custom media (whose canonical name may be portrait)
            // consistent: 1051x594 always requires a 90-degree rotation when
            // the available media is stored as 594x1051.
            var desiredLandscape = target[0] > target[1];
            validator.SetPlotRotation(settings, desiredLandscape == mediaLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
            var style = string.IsNullOrWhiteSpace(sheet.PlotStyle) || string.Equals(sheet.PlotStyle, "使用输出设置", StringComparison.OrdinalIgnoreCase)
                ? defaultPlotStyle
                : sheet.PlotStyle;
            if (!string.IsNullOrWhiteSpace(style))
            {
                try { validator.SetCurrentStyleSheet(settings, style); } catch { }
            }
            settings.ShadePlot = PlotSettingsShadePlotType.AsDisplayed;
            return settings;
        }

        private static void InitializePdfDevice(PlotSettingsValidator validator, PlotSettings settings, Layout layout, string device)
        {
            var candidates = new[]
            {
                layout.CanonicalMediaName,
                "ISO_A3_(420.00_x_297.00_MM)",
                "ISO_A3_(297.00_x_420.00_MM)",
                "ISO_full_bleed_A3_(420.00_x_297.00_MM)",
                "ISO_full_bleed_A3_(297.00_x_420.00_MM)"
            };
            Exception lastError = null;
            foreach (var media in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    validator.SetPlotConfigurationName(settings, device, media);
                    validator.RefreshLists(settings);
                    if (validator.GetCanonicalMediaNameList(settings).Count > 0) return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }
            throw new InvalidOperationException("无法初始化 PDF 打印设备的纸张列表。", lastError);
        }

        private static string ChoosePdfDevice(PlotSettingsValidator validator)
        {
            var devices = validator.GetPlotDeviceList().Cast<string>().ToList();
            return devices.FirstOrDefault(x => string.Equals(x, "BatchPdfPublisher.pc3", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(x => string.Equals(x, "DWG To PDF.pc3", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(x => x.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new InvalidOperationException("当前 CAD 没有可用的 PDF 打印设备。");
        }

        private static string ChooseMedia(PlotSettingsValidator validator, PlotSettings settings, double targetWidth, double targetHeight, string marginMode, bool requireExactSize)
        {
            string best = null;
            var bestScore = double.MaxValue;
            foreach (string media in validator.GetCanonicalMediaNameList(settings))
            {
                var size = ParseMediaSize(media);
                if (size == null) continue;
                var direct = RelativeError(size[0], targetWidth) + RelativeError(size[1], targetHeight);
                var rotated = RelativeError(size[1], targetWidth) + RelativeError(size[0], targetHeight);
                var score = Math.Min(direct, rotated);
                if (requireExactSize && score > .003d) continue;
                var fullBleed = media.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0 || media.IndexOf("expand", StringComparison.OrdinalIgnoreCase) >= 0;
                var bundledMedia = media.IndexOf("BPP_", StringComparison.OrdinalIgnoreCase) >= 0;
                if (string.Equals(marginMode, "无白边（满幅）", StringComparison.OrdinalIgnoreCase) && !fullBleed) score += 0.2d;
                // Prefer the millimetre, zero-margin media shipped with the plug-in.
                // PdfMerger then applies the selected 0 mm or 3 mm edge policy.
                if (bundledMedia) score -= 0.05d;
                if (score < bestScore) { bestScore = score; best = media; }
            }
            return best;
        }

        private static double[] ParseMediaSize(string media)
        {
            if (string.IsNullOrWhiteSpace(media)) return null;
            var normalized = media.Replace('_', ' ');
            var matches = Regex.Matches(normalized, @"(\d+(?:\.\d+)?)\s*[xX]\s*(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
            if (matches.Count == 0) return null;
            var match = matches[matches.Count - 1];
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var first)) return null;
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) return null;
            // Canonical media names can contain either millimetres or inches.
            // Normalize every candidate to millimetres before comparing it
            // with PaperSizeCatalog, whose dimensions are always millimetres.
            if (media.IndexOf("INCH", StringComparison.OrdinalIgnoreCase) >= 0 || media.IndexOf("英寸", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                first *= 25.4d;
                second *= 25.4d;
            }
            return new[] { first, second };
        }

        private static double[] TargetPaperSize(SheetItem sheet)
        {
            return PaperSizeCatalog.GetSize(sheet.Frame, sheet.Extension, sheet.PaperOrientation);
        }

        private static void ValidateDeclaredFrameRatio(SheetItem sheet)
        {
            var target = TargetPaperSize(sheet);
            // Paper orientation is applied later through PlotRotation.  Compare
            // the long/short side ratio here so the same frame rotated by 90
            // degrees is not rejected (for example 1261x594 vs 594x1261).
            var expected = Math.Max(target[0], target[1]) / Math.Min(target[0], target[1]);
            var width = Math.Abs(sheet.MaxX - sheet.MinX);
            var height = Math.Abs(sheet.MaxY - sheet.MinY);
            if (width < 0.0001d || height < 0.0001d) return;
            var actual = Math.Max(width, height) / Math.Min(width, height);
            if (Math.Abs(actual - expected) / expected > .02d)
                throw new InvalidOperationException($"图纸“{sheet.SheetNumber} {sheet.SheetName}”的实际图框比例为 {actual:0.###}，但登记的 {sheet.FrameDisplay} 页面比例为 {expected:0.###}。请在图框登记中改正纸张规格或加长比例后再发布，不能用标准 A1 代替加长图纸。");
        }

        private static int PublishPriority(SheetItem sheet)
        {
            var note = sheet?.FrameNote ?? string.Empty;
            if (note.IndexOf("封面", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (note.IndexOf("目录", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
        }

        private static double ParseExtension(string value)
        {
            return PaperSizeCatalog.ParseExtension(value);
        }

        private static int ParseScale(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var last = value.Trim().Split(':').Last();
            return int.TryParse(last.Trim(), out var scale) && scale > 0 ? scale : 0;
        }

        private static double RelativeError(double actual, double expected)
        {
            return Math.Abs(actual - expected) / Math.Max(expected, 1d);
        }

        private static string SafeName(string value)
        {
            var clean = string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim();
            foreach (var character in Path.GetInvalidFileNameChars()) clean = clean.Replace(character, '_');
            return clean;
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var directory = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var index = 2; index < 10000; index++)
            {
                var candidate = Path.Combine(directory, stem + " (" + index + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(directory, stem + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + extension);
        }
    }
}
