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

    public sealed class PdfPublisherService
    {
        private static readonly Dictionary<string, double[]> PaperSizes = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "A0", new[] { 841d, 1189d } }, { "A1", new[] { 594d, 841d } },
            { "A2", new[] { 420d, 594d } }, { "A3", new[] { 297d, 420d } },
            { "A4", new[] { 210d, 297d } }
        };

        public PdfPublishResult Publish(Document document, IEnumerable<SheetItem> sourceSheets, ProjectProfile project)
        {
            if (document == null) throw new InvalidOperationException("没有打开的图纸。");
            var sheets = sourceSheets?.OrderBy(x => x.Building).ThenBy(x => x.Order).ToList() ?? new List<SheetItem>();
            if (sheets.Count == 0) throw new InvalidOperationException("图纸列表为空，请先扫描当前图纸。");
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("AutoCAD 正在执行其他打印任务，请稍后再试。");

            var outputRoot = string.IsNullOrWhiteSpace(project?.OutputDirectory) ? @"D:\PDF输出" : project.OutputDirectory;
            var engineeringFolder = Path.Combine(outputRoot, SafeName(project?.Name ?? "默认工程"));
            Directory.CreateDirectory(engineeringFolder);
            var result = new PdfPublishResult();
            var jobs = BuildJobs(sheets, project?.MergeByBuilding ?? true, project?.Name ?? "默认工程", engineeringFolder);
            var previousBackgroundPlot = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("BACKGROUNDPLOT");
            Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            try
            {
                foreach (var job in jobs)
                {
                    PlotGroup(document, job.Value, job.Key, project?.PlotStyle, project?.MarginMode);
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
                    jobs.Add(new KeyValuePair<string, List<SheetItem>>(UniquePath(Path.Combine(outputRoot, name)), group.OrderBy(x => x.Order).ToList()));
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

        private static void PlotGroup(Document document, IList<SheetItem> sheets, string outputPath, string defaultPlotStyle, string marginMode)
        {
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            using (var engine = PlotFactory.CreatePublishEngine())
            {
                var layout = (Layout)transaction.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForRead);
                engine.BeginPlot(null, null);
                var documentStarted = false;
                for (var index = 0; index < sheets.Count; index++)
                {
                    var sheet = sheets[index];
                    var stage = "创建打印设置";
                    try
                    {
                        using (var settings = CreateSettings(layout, sheet, defaultPlotStyle, marginMode))
                        using (var plotInfo = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings })
                        {
                            stage = "校验打印信息";
                            using (var validator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled })
                            {
                                try
                                {
                                    validator.Validate(plotInfo);
                                }
                                catch (Autodesk.AutoCAD.Runtime.Exception exception)
                                {
                                    // AutoCAD 2022 rejects window PlotInfo when a DWG carries
                                    // stale/proxy layout metadata. Keep the PDF job usable by
                                    // retrying with the same validated device/media as a layout
                                    // plot; this is also safer than crashing AutoCAD.
                                    if (exception.ErrorStatus != Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidPlotInfo)
                                        throw;
                                    var settingsValidator = PlotSettingsValidator.Current;
                                    settingsValidator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                                    settingsValidator.SetPlotCentered(settings, true);
                                    settingsValidator.SetUseStandardScale(settings, true);
                                    settingsValidator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                                    settingsValidator.SetPlotRotation(settings, PlotRotation.Degrees000);
                                    validator.Validate(plotInfo);
                                }
                            }
                            if (!documentStarted)
                            {
                                stage = "创建 PDF 文档";
                                // The page count is part of PlotEngine's document state.
                                // Declaring one page and then calling BeginPage again makes
                                // AutoCAD 2022 reject page 2 with eInvalidPlotInfo.
                                engine.BeginDocument(plotInfo, document.Name, null, sheets.Count, true, outputPath);
                                documentStarted = true;
                            }
                            using (var pageInfo = new PlotPageInfo())
                            {
                                stage = "创建 PDF 页面";
                                engine.BeginPage(pageInfo, plotInfo, index == sheets.Count - 1, null);
                                stage = "生成页面图形";
                                engine.BeginGenerateGraphics(null);
                                engine.EndGenerateGraphics(null);
                                engine.EndPage(null);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            string.Format("第 {0} 张（{1} / {2}）在“{3}”时失败：{4}", index + 1, sheet.SheetNumber, sheet.SheetName, stage, exception.Message),
                            exception);
                    }
                }
                if (documentStarted) engine.EndDocument(null);
                engine.EndPlot(null);
                transaction.Commit();
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
            var media = ChooseMedia(validator, settings, target[0], target[1], marginMode);
            if (string.IsNullOrWhiteSpace(media))
                throw new InvalidOperationException("PDF 打印设备没有返回可用纸张。请检查 DWG To PDF.pc3 配置。");
            // SetPlotConfigurationName 同时写入设备和有效介质，避免留下一个
            // 设备有效但介质为空的 PlotSettings（AutoCAD 2022 会报 eInvalidPlotInfo）。
            validator.SetPlotConfigurationName(settings, device, media);
            validator.RefreshLists(settings);
            validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
            validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            validator.SetPlotWindowArea(settings, new Extents2d(sheet.MinX, sheet.MinY, sheet.MaxX, sheet.MaxY));
            validator.SetPlotCentered(settings, true);
            var scale = ParseScale(sheet.PrintScale);
            if (scale > 0)
            {
                validator.SetUseStandardScale(settings, false);
                validator.SetCustomPrintScale(settings, new CustomScale(1d, scale));
            }
            else
            {
                validator.SetUseStandardScale(settings, true);
                validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            }
            var mediaSize = ParseMediaSize(media);
            var mediaLandscape = mediaSize != null && mediaSize[0] > mediaSize[1];
            var desiredLandscape = string.Equals(sheet.PaperOrientation, "横向", StringComparison.OrdinalIgnoreCase);
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
            return devices.FirstOrDefault(x => string.Equals(x, "DWG To PDF.pc3", StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(x => x.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new InvalidOperationException("当前 CAD 没有可用的 PDF 打印设备。");
        }

        private static string ChooseMedia(PlotSettingsValidator validator, PlotSettings settings, double targetWidth, double targetHeight, string marginMode)
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
                var fullBleed = media.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0 || media.IndexOf("expand", StringComparison.OrdinalIgnoreCase) >= 0;
                if (string.Equals(marginMode, "无白边（满幅）", StringComparison.OrdinalIgnoreCase) && !fullBleed) score += 0.2d;
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
            return new[] { first, second };
        }

        private static double[] TargetPaperSize(SheetItem sheet)
        {
            if (!PaperSizes.TryGetValue(sheet.Frame ?? string.Empty, out var baseSize)) baseSize = PaperSizes["A3"];
            var factor = 1d + ParseExtension(sheet.Extension);
            var shortSide = baseSize[0];
            var longSide = baseSize[1] * factor;
            return string.Equals(sheet.PaperOrientation, "横向", StringComparison.OrdinalIgnoreCase)
                ? new[] { longSide, shortSide }
                : new[] { shortSide, longSide };
        }

        private static double ParseExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0d;
            var total = 0d;
            foreach (var part in value.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var fraction = part.Split('/');
                if (fraction.Length == 2 && double.TryParse(fraction[0], out var numerator) && double.TryParse(fraction[1], out var denominator) && denominator != 0)
                    total += numerator / denominator;
                else if (double.TryParse(part, out var whole)) total += whole;
            }
            return total;
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
