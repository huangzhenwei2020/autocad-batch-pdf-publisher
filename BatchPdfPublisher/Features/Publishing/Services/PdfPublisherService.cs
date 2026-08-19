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
        public List<string> Failures { get; } = new List<string>();
        public int SheetCount { get; set; }
    }

    public sealed class PdfPublishProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string SheetLabel { get; set; }
    }

    public sealed class PreparedPdfPage
    {
        public SheetItem Sheet { get; set; }
        public string TemporaryPath { get; set; }
    }

    public sealed class SheetValidationIssue
    {
        public SheetItem Sheet { get; set; }
        public string Message { get; set; }
    }

    public sealed class PdfPublisherService
    {
        private static readonly string DiagnosticLogPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher.publish.log");
        private static readonly object DiagnosticLogSync = new object();
        private const long MinimumFreeSpaceBytes = 512L * 1024L * 1024L;

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

        public string CreateEngineeringOutputFolder(ProjectProfile project)
        {
            var outputRoot = string.IsNullOrWhiteSpace(project?.OutputDirectory) ? @"D:\PDF输出" : project.OutputDirectory;

            // 【修复】检查输出目录的磁盘空间
            try
            {
                var outputDrive = Path.GetPathRoot(outputRoot);
                if (!string.IsNullOrWhiteSpace(outputDrive))
                {
                    var driveInfo = new DriveInfo(outputDrive);
                    if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < MinimumFreeSpaceBytes)
                    {
                        WritePublishDiagnostic($"警告：输出目录 {outputRoot} 所在磁盘剩余空间不足 512 MB（当前：{driveInfo.AvailableFreeSpace / 1024 / 1024} MB）");
                    }
                }
            }
            catch (Exception ex)
            {
                WritePublishDiagnostic($"检查输出目录磁盘空间时出错：{ex.Message}");
            }

            var requestedEngineeringFolder = Path.Combine(outputRoot, SafeName(project?.Name ?? "默认工程"));
            var engineeringFolder = UniqueDirectory(requestedEngineeringFolder);
            Directory.CreateDirectory(engineeringFolder);
            return engineeringFolder;
        }

        public List<PreparedPdfPage> PreparePages(Document document, IEnumerable<SheetItem> sourceSheets,
            ProjectProfile project, Action<SheetItem> pagePublished, Action<SheetItem, string> pageFailed)
        {
            if (document == null || document.Database == null) throw new InvalidOperationException("没有打开的图纸。");
            var sheets = sourceSheets?.Where(x => x != null).OrderBy(PublishPriority).ThenBy(x => x.Order).ToList() ?? new List<SheetItem>();
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("AutoCAD 正在执行其他打印任务，请稍后再试。");

            var pages = new List<PreparedPdfPage>();
            var previousBackgroundPlot = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("BACKGROUNDPLOT");
            Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            try
            {
                for (var index = 0; index < sheets.Count; index++)
                {
                    var sheet = sheets[index];
                    var temporaryPath = Path.Combine(Path.GetTempPath(), "BatchPdfPublisher_" + Guid.NewGuid().ToString("N") + ".pdf");
                    var freeSpace = AvailableFreeSpace(temporaryPath);
                    if (freeSpace >= 0 && freeSpace < MinimumFreeSpaceBytes)
                    {
                        var message = "系统临时目录剩余空间不足 512 MB，已停止继续生成单页 PDF，避免产生损坏文件。请清理 "
                            + Path.GetPathRoot(temporaryPath) + " 后重新发布。";
                        for (var remaining = index; remaining < sheets.Count; remaining++)
                            pageFailed?.Invoke(sheets[remaining], message);
                        WritePublishDiagnostic(message);
                        break;
                    }
                    try
                    {
                        PlotSinglePage(document, sheet, temporaryPath, project?.PlotStyle, project?.MarginMode, index);
                        pages.Add(new PreparedPdfPage { Sheet = sheet, TemporaryPath = temporaryPath });
                        pagePublished?.Invoke(sheet);
                    }
                    catch (Exception exception)
                    {
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                        pageFailed?.Invoke(sheet, exception.Message);
                    }
                }
            }
            finally
            {
                try { Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", previousBackgroundPlot); } catch { }
            }
            return pages;
        }

        public PdfPublishResult FinalizePreparedPages(IEnumerable<PreparedPdfPage> sourcePages, ProjectProfile project,
            string engineeringFolder, Action<int, int, SheetItem> progress = null)
        {
            var pages = sourcePages?.Where(x => x?.Sheet != null && !string.IsNullOrWhiteSpace(x.TemporaryPath) && File.Exists(x.TemporaryPath))
                .OrderBy(x => x.Sheet.Building).ThenBy(x => PublishPriority(x.Sheet)).ThenBy(x => x.Sheet.Order).ToList()
                ?? new List<PreparedPdfPage>();
            var result = new PdfPublishResult();
            var completed = 0;
            try
            {
                // "输出到各 CAD 文件同级目录": each DWG's sheets are finalized next
                // to that CAD file instead of the shared engineering folder.
                Func<SheetItem, string> outputRoot = sheet =>
                {
                    if (project?.OutputNextToCadFile == true && !string.IsNullOrWhiteSpace(sheet?.SourceFile))
                    {
                        var directory = Path.GetDirectoryName(sheet.SourceFile);
                        if (!string.IsNullOrWhiteSpace(directory)) return directory;
                    }
                    return engineeringFolder;
                };
                if (project?.MergeByBuilding ?? true)
                {
                    foreach (var group in pages.GroupBy(x => new { x.Sheet.Building, Root = outputRoot(x.Sheet) }))
                    {
                        var root = group.Key.Root;
                        Directory.CreateDirectory(root);
                        var parts = new List<string>();
                        if (project?.IncludeProjectNameInFileName != false) parts.Add(SafeName(project?.Name ?? "默认工程"));
                        if (project?.IncludeBuildingNameInFileName != false) parts.Add(SafeName(group.Key.Building));
                        if (parts.Count == 0) parts.Add("图纸");
                        var requestedPath = Path.Combine(root, string.Join("_", parts) + ".pdf");
                        var outputPath = ResolveOutputPath(requestedPath, project?.OverwriteExistingPdf == true);
                        var groupPages = group.ToList();
                        var completedBeforeGroup = completed;
                        try
                        {
                            PdfMerger.Merge(groupPages.Select(x => x.TemporaryPath).ToList(), groupPages.Select(x => x.Sheet).ToList(), outputPath, project?.MarginMode,
                                sheet => { completed++; progress?.Invoke(completed, pages.Count, sheet); });
                            result.Files.Add(outputPath);
                            result.SheetCount += groupPages.Count;
                        }
                        catch (Exception exception)
                        {
                            result.Failures.Add("子项目“" + (string.IsNullOrWhiteSpace(group.Key.Building) ? "未分组" : group.Key.Building) + "”合并失败：" + exception.Message);
                            CompleteFailedProgress(groupPages, completedBeforeGroup, ref completed, pages.Count, progress);
                        }
                        GC.Collect(1, GCCollectionMode.Optimized);
                    }
                }
                else
                {
                    foreach (var page in pages)
                    {
                        var root = outputRoot(page.Sheet);
                        var folder = Path.Combine(root, SafeName(page.Sheet.Building));
                        Directory.CreateDirectory(folder);
                        var title = string.IsNullOrWhiteSpace(page.Sheet.SheetName) ? page.Sheet.SheetNumber : page.Sheet.SheetName;
                        var fileName = (page.Sheet.Order.ToString("D3") + "_" + (string.IsNullOrWhiteSpace(title) ? "图纸" : SafeName(title))).Trim('_');
                        var outputPath = ResolveOutputPath(Path.Combine(folder, fileName + ".pdf"), project?.OverwriteExistingPdf == true);
                        var completedBeforePage = completed;
                        try
                        {
                            PdfMerger.Merge(new List<string> { page.TemporaryPath }, new List<SheetItem> { page.Sheet }, outputPath, project?.MarginMode,
                                sheet => { completed++; progress?.Invoke(completed, pages.Count, sheet); });
                            result.Files.Add(outputPath);
                            result.SheetCount++;
                        }
                        catch (Exception exception)
                        {
                            result.Failures.Add(SheetLabel(page.Sheet) + "整理失败：" + exception.Message);
                            if (completed == completedBeforePage) { completed++; progress?.Invoke(completed, pages.Count, page.Sheet); }
                        }
                        if (result.SheetCount % 100 == 0) GC.Collect(1, GCCollectionMode.Optimized);
                    }
                }
                return result;
            }
            finally
            {
                CleanupPreparedPages(pages);
            }
        }

        public void CleanupPreparedPages(IEnumerable<PreparedPdfPage> pages)
        {
            foreach (var page in pages ?? Enumerable.Empty<PreparedPdfPage>())
                try { if (!string.IsNullOrWhiteSpace(page?.TemporaryPath) && File.Exists(page.TemporaryPath)) File.Delete(page.TemporaryPath); } catch { }
        }

        private static void CompleteFailedProgress(IList<PreparedPdfPage> groupPages, int completedBeforeGroup,
            ref int completed, int total, Action<int, int, SheetItem> progress)
        {
            var alreadyReported = Math.Max(0, completed - completedBeforeGroup);
            for (var index = alreadyReported; index < groupPages.Count; index++)
            {
                completed++;
                progress?.Invoke(completed, total, groupPages[index].Sheet);
            }
        }

        private static string SheetLabel(SheetItem sheet) => string.Join(" · ", new[]
        {
            Path.GetFileName(sheet?.SourceFile), sheet?.Building, sheet?.SheetNumber, sheet?.SheetName
        }.Where(x => !string.IsNullOrWhiteSpace(x))) + "：";

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        public PdfPublishResult PublishMerged(Document document, IEnumerable<SheetItem> sourceSheets, string requestedOutputPath,
            string defaultPlotStyle, string marginMode, bool overwrite, Action<PdfPublishProgress> progress = null)
        {
            if (document == null || document.Database == null) throw new InvalidOperationException("没有打开的图纸。");
            var sheets = sourceSheets?.Where(x => x != null).OrderBy(PublishPriority).ThenBy(x => x.Order).ToList() ?? new List<SheetItem>();
            if (sheets.Count == 0) throw new InvalidOperationException("尚未框选有效的已登记图框。");
            if (string.IsNullOrWhiteSpace(requestedOutputPath)) throw new InvalidOperationException("请设置 PDF 保存位置。");
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("AutoCAD 正在执行其他打印任务，请稍后再试。");

            var folder = Path.GetDirectoryName(requestedOutputPath);
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("PDF 保存目录无效。");
            Directory.CreateDirectory(folder);
            var outputPath = overwrite ? requestedOutputPath : UniquePath(requestedOutputPath);
            var completed = 0;
            var previousBackgroundPlot = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("BACKGROUNDPLOT");
            Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            try
            {
                PlotGroup(document, sheets, outputPath, defaultPlotStyle, marginMode, sheet =>
                {
                    completed++;
                    progress?.Invoke(new PdfPublishProgress
                    {
                        Current = completed,
                        Total = sheets.Count,
                        SheetLabel = string.Join(" · ", new[] { sheet.SheetNumber, sheet.SheetName }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    });
                });
            }
            finally
            {
                try { Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("BACKGROUNDPLOT", previousBackgroundPlot); } catch { }
            }
            var result = new PdfPublishResult { SheetCount = sheets.Count };
            result.Files.Add(outputPath);
            return result;
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
                PdfMerger.Merge(temporaryFiles, sheets, outputPath, marginMode, null);
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
            var label = string.Join(" · ", new[] { Path.GetFileName(sheet.SourceFile), sheet.Building, sheet.SheetNumber, sheet.SheetName }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var stage = "检查图框尺寸";
            Database previousWorkingDatabase = null;
            try
            {
                ValidateDeclaredFrameRatio(sheet);
                // PlotInfoValidator requires the PlotInfo layout to belong to
                // the active MDI document. This is especially important when
                // one publish operation contains several DWG files.
                if (!ReferenceEquals(Application.DocumentManager.MdiActiveDocument, document))
                    Application.DocumentManager.MdiActiveDocument = document;
                previousWorkingDatabase = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = document.Database;
                using (document.LockDocument())
                {
                    stage = "读取当前模型/布局";
                    var requestedLayout = string.Equals(sheet.SourceLayout, "模型空间", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(sheet.SourceLayout, "Model", StringComparison.OrdinalIgnoreCase) ? "Model" : sheet.SourceLayout;
                    var layoutManager = LayoutManager.Current;
                    using (var transaction = document.Database.TransactionManager.StartTransaction())
                    {
                    var layouts = transaction.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (layouts == null) throw new InvalidOperationException("无法读取当前图纸的布局字典。");
                    ObjectId requestedLayoutId = ObjectId.Null;
                    foreach (DBDictionaryEntry entry in layouts)
                        if (string.Equals(entry.Key, requestedLayout, StringComparison.OrdinalIgnoreCase)) { requestedLayoutId = entry.Value; break; }
                    if (requestedLayoutId.IsNull)
                        throw new InvalidOperationException("图纸中找不到扫描记录对应的空间：" + requestedLayout);

                    // Setting only CurrentLayout by name is not sufficient in an
                    // MDI batch. SetCurrentLayoutId synchronizes the native layout
                    // object used by PlotInfoValidator and prevents
                    // eLayoutNotCurrent.
#if ACAD_R19
                    layoutManager.CurrentLayout = requestedLayout;
#else
                    layoutManager.SetCurrentLayoutId(requestedLayoutId);
#endif
                    var currentLayoutId = layoutManager.GetLayoutId(layoutManager.CurrentLayout);
                    var layout = transaction.GetObject(currentLayoutId, OpenMode.ForRead) as Layout;
                    if (layout == null)
                        throw new InvalidOperationException("当前空间没有有效的打印布局。");
                    WriteDiagnosticState(document, sheet, requestedLayoutId, currentLayoutId, layout);

                    stage = "创建打印设置";
                    using (var settings = CreateSettings(layout, sheet, defaultPlotStyle, marginMode))
                    using (var plotInfo = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = settings })
                    {
                        stage = "校验打印信息";
                        using (var validator = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled })
                            validator.Validate(plotInfo);
                        WritePlotConfiguration(document, sheet, index, outputPath, settings);

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
                ValidateTemporaryPage(outputPath, sheet, index);
            }
            catch (Exception exception)
            {
                var detail = $"第 {index + 1} 张（{label}）在“{stage}”时失败：{exception.Message}";
                WriteDiagnostic(detail, exception);
                throw new InvalidOperationException(detail, exception);
            }
            finally
            {
                if (previousWorkingDatabase != null)
                    try { HostApplicationServices.WorkingDatabase = previousWorkingDatabase; } catch { }
            }
        }

        private static void WriteDiagnostic(string message, Exception exception)
        {
            WritePublishDiagnostic(message + Environment.NewLine + exception + Environment.NewLine);
        }

        private static void WriteDiagnosticState(Document document, SheetItem sheet, ObjectId requestedLayoutId, ObjectId currentLayoutId, Layout layout)
        {
            WritePublishDiagnostic("打印状态：DWG=" + document.Database.Filename
                + "；来源空间=" + sheet.SourceLayout
                + "；当前布局=" + layout.LayoutName
                + "；图号=" + sheet.SheetNumber
                + "；图名=" + sheet.SheetName
                + "；图框句柄=" + sheet.BlockHandle
                + "；请求ID=" + requestedLayoutId.Handle
                + "；当前ID=" + currentLayoutId.Handle
                + "；WorkingDb匹配=" + (HostApplicationServices.WorkingDatabase != null
                    && HostApplicationServices.WorkingDatabase.UnmanagedObject == document.Database.UnmanagedObject)
                + "；活动文档匹配=" + ReferenceEquals(Application.DocumentManager.MdiActiveDocument, document));
        }

        private static void WritePlotConfiguration(Document document, SheetItem sheet, int index, string temporaryPath,
            PlotSettings settings)
        {
            try
            {
                var target = TargetPaperSize(sheet);
                var paper = settings.PlotPaperSize;
                var plotWindow = settings.PlotWindowArea;
                WritePublishDiagnostic("单页打印参数：发布序号=" + (index + 1)
                    + "；子项目=" + sheet.Building
                    + "；图号=" + sheet.SheetNumber
                    + "；图名=" + sheet.SheetName
                    + "；DWG=" + document.Database.Filename
                    + "；布局=" + sheet.SourceLayout
                    + "；图框句柄=" + sheet.BlockHandle
                    + "；WCS窗口=[" + FormatNumber(sheet.MinX) + "," + FormatNumber(sheet.MinY) + "]-["
                        + FormatNumber(sheet.MaxX) + "," + FormatNumber(sheet.MaxY) + "]"
                    + "；DCS窗口=[" + FormatNumber(plotWindow.MinPoint.X) + "," + FormatNumber(plotWindow.MinPoint.Y) + "]-["
                        + FormatNumber(plotWindow.MaxPoint.X) + "," + FormatNumber(plotWindow.MaxPoint.Y) + "]"
                    + "；登记纸张=" + sheet.FrameDisplay + " " + sheet.PaperOrientation
                    + "；目标毫米=" + FormatMillimeters(target[0], target[1])
                    + "；图纸标注比例=" + sheet.PrintScale
                    + "；介质=" + settings.CanonicalMediaName
                    + "；驱动纸张毫米=" + FormatMillimeters(paper.X, paper.Y)
                    + "；旋转=" + settings.PlotRotation
                    + "；适合纸张=" + settings.UseStandardScale + "/" + settings.StdScaleType
                    + "；居中=" + settings.PlotCentered
                    + "；临时PDF=" + temporaryPath);
            }
            catch (Exception exception)
            {
                WritePublishDiagnostic("记录单页打印参数失败，但不影响发布：" + exception.Message);
            }
        }

        private static void ValidateTemporaryPage(string path, SheetItem sheet, int index)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new InvalidOperationException("CAD 没有生成有效的临时单页 PDF。");
            using (var input = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
            {
                if (input.PageCount != 1)
                    throw new InvalidOperationException("临时 PDF 应为 1 页，实际为 " + input.PageCount + " 页。");
                var page = input.Pages[0];
                var width = PointsToMillimeters(page.Width.Point);
                var height = PointsToMillimeters(page.Height.Point);
                var target = TargetPaperSize(sheet);
                if (!SamePaperSize(width, height, target[0], target[1]))
                    throw new InvalidOperationException("临时 PDF 实际纸张为 " + FormatMillimeters(width, height)
                        + " mm，与登记目标 " + FormatMillimeters(target[0], target[1])
                        + " mm 不一致。请检查 PC3/PMP 介质配置，插件已阻止错误页面进入合并文件。");
                WritePublishDiagnostic("单页PDF完成：发布序号=" + (index + 1)
                    + "；子项目=" + sheet.Building
                    + "；图号=" + sheet.SheetNumber
                    + "；图名=" + sheet.SheetName
                    + "；实际毫米=" + FormatMillimeters(width, height)
                    + "；字节=" + new FileInfo(path).Length
                    + "；临时PDF=" + path);
            }
        }

        internal static void WritePublishDiagnostic(string message)
        {
            try
            {
                lock (DiagnosticLogSync)
                {
                    if (File.Exists(DiagnosticLogPath) && new FileInfo(DiagnosticLogPath).Length > 10L * 1024L * 1024L)
                    {
                        var previous = DiagnosticLogPath + ".previous.log";
                        TryDelete(previous);
                        File.Move(DiagnosticLogPath, previous);
                    }
                    File.AppendAllText(DiagnosticLogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        private static class PdfMerger
        {
            public static void Merge(IList<string> files, IList<SheetItem> sheets, string outputPath, string marginMode, Action<SheetItem> pageMerged = null)
            {
                if (files == null || sheets == null || files.Count != sheets.Count || files.Count == 0)
                    throw new InvalidOperationException("待合并的临时 PDF 与图纸记录数量不一致。");
                var sourceBytes = files.Sum(path => File.Exists(path) ? new FileInfo(path).Length : 0L);
                EnsureOutputStorageCapacity(outputPath, sourceBytes);
                var marginMillimeters = string.Equals(marginMode, "保留 3 mm 白边", StringComparison.OrdinalIgnoreCase) ? 3d : 0d;
                WritePublishDiagnostic("开始合并PDF：输出=" + outputPath + "；页数=" + files.Count
                    + "；临时文件总字节=" + sourceBytes + "；托管内存字节=" + GC.GetTotalMemory(false));
                using (var output = new PdfSharp.Pdf.PdfDocument())
                {
                    for (var index = 0; index < files.Count; index++)
                    using (var input = PdfSharp.Pdf.IO.PdfReader.Open(files[index], PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    {
                        if (input.PageCount != 1)
                            throw new InvalidOperationException("第 " + (index + 1) + " 个临时 PDF 不是单页文件：" + files[index]);
                        var source = input.Pages[0];
                        var target = TargetPaperSize(sheets[index]);
                        var sourceWidth = PointsToMillimeters(source.Width.Point);
                        var sourceHeight = PointsToMillimeters(source.Height.Point);
                        if (!SamePaperSize(sourceWidth, sourceHeight, target[0], target[1]))
                            throw new InvalidOperationException("第 " + (index + 1) + " 页“" + sheets[index].SheetNumber
                                + " " + sheets[index].SheetName + "”的临时 PDF 纸张 "
                                + FormatMillimeters(sourceWidth, sourceHeight) + " mm 与目标 "
                                + FormatMillimeters(target[0], target[1]) + " mm 不一致。");
                        var canImportOriginal = marginMillimeters <= 0d
                            && Math.Abs(sourceWidth - target[0]) <= .2d
                            && Math.Abs(sourceHeight - target[1]) <= .2d;
                        if (canImportOriginal)
                        {
                            // Zero-margin pages already have the exact requested
                            // media. Importing the page directly avoids a second
                            // drawing transform, preserves CAD vector coordinates,
                            // and uses less memory for very large projects.
                            output.AddPage(source);
                        }
                        else
                        {
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
                        var finalPage = output.Pages[output.PageCount - 1];
                        var finalWidth = PointsToMillimeters(finalPage.Width.Point);
                        var finalHeight = PointsToMillimeters(finalPage.Height.Point);
                        WritePublishDiagnostic("合并页完成：最终页码=" + (index + 1)
                            + "；子项目=" + sheets[index].Building
                            + "；图号=" + sheets[index].SheetNumber
                            + "；图名=" + sheets[index].SheetName
                            + "；DWG=" + sheets[index].SourceFile
                            + "；布局=" + sheets[index].SourceLayout
                            + "；图框句柄=" + sheets[index].BlockHandle
                            + "；窗口=[" + FormatNumber(sheets[index].MinX) + "," + FormatNumber(sheets[index].MinY) + "]-["
                                + FormatNumber(sheets[index].MaxX) + "," + FormatNumber(sheets[index].MaxY) + "]"
                            + "；源PDF毫米=" + FormatMillimeters(sourceWidth, sourceHeight)
                            + "；最终PDF毫米=" + FormatMillimeters(finalWidth, finalHeight)
                            + "；合并方式=" + (canImportOriginal ? "原页直接导入" : "标准化重绘")
                            + "；介质策略=" + marginMode);
                        pageMerged?.Invoke(sheets[index]);
                        if ((index + 1) % 100 == 0)
                            WritePublishDiagnostic("合并资源：已完成=" + (index + 1) + "/" + files.Count
                                + "；托管内存字节=" + GC.GetTotalMemory(false));
                    }
                    if (output.PageCount != sheets.Count)
                        throw new InvalidOperationException("合并文档页数校验失败：应为 " + sheets.Count + " 页，实际为 " + output.PageCount + " 页。");
                    SavePdfAtomically(output, outputPath);
                }
                WritePublishDiagnostic("PDF合并完成：输出=" + outputPath + "；页数=" + sheets.Count
                    + "；最终字节=" + new FileInfo(outputPath).Length);
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
            // Some AutoCAD 2022/TArch drawings return eInvalidInput when
            // SetPlotType(Window) is called before the window has a valid
            // value. The native API accepts the reverse order. Coordinates
            // must be expressed in DCS rather than the scanned WCS extents.
            var plotWindow = GetPlotWindowInDcs(sheet);
            ApplyPlotStep("预设图框范围", () => validator.SetPlotWindowArea(settings, plotWindow));
            ApplyPlotStep("设置窗口打印类型", () => validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window));
            ApplyPlotStep("居中打印", () => validator.SetPlotCentered(settings, true));
            // “打印比例”是图纸属性，不能直接作为 CAD 的 PlotScale。若把
            // 1:100 写入 PlotScale，会把 420 mm 的图框缩成 4.2 mm。
            // 始终让选定图框窗口适配页面，才能保证 PDF 与图框比例一致。
            ApplyPlotStep("设置适合纸张比例", () => validator.SetUseStandardScale(settings, true));
            ApplyPlotStep("设置缩放类型", () => validator.SetStdScaleType(settings, StdScaleType.ScaleToFit));
            var device = ChoosePdfDevice(validator);
            InitializePdfDevice(validator, settings, layout, device);
            // AutoCAD 2022 validates custom metric media against the paper-unit
            // mode currently held by PlotSettings.  A layout copied from a
            // TArch drawing can still be in inches, which makes the otherwise
            // valid BPP_* media name fail with eInvalidInput.  Set the unit
            // mode before asking for, and applying, the custom media.
            ApplyPlotStep("初始化毫米单位", () => validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters));
            var target = TargetPaperSize(sheet);
            var media = ChooseMedia(validator, settings, target[0], target[1], marginMode, !string.IsNullOrWhiteSpace(sheet.Extension));
            if (string.IsNullOrWhiteSpace(media))
                throw new InvalidOperationException($"当前 PDF 绘图仪没有 {PaperSizeCatalog.Describe(sheet.Frame, sheet.Extension, sheet.PaperOrientation)} 的精确纸张。请先关闭 CAD，再双击启动器让它重新部署 BatchPdfPublisher.pc3/pmp；当前进程不会自动刷新绘图仪介质列表。加长图纸不会降级为普通 A1/A0。 ");
            // SetPlotConfigurationName 同时写入设备和有效介质，避免留下一个
            // 设备有效但介质为空的 PlotSettings（AutoCAD 2022 会报 eInvalidPlotInfo）。
            try
            {
                validator.SetPlotConfigurationName(settings, device, media);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // The plotter cache can be stale immediately after the
                // launcher deploys a PMP. Refresh once and retry the exact
                // canonical name before surfacing the useful error.
                validator.RefreshLists(settings);
                try { validator.SetPlotConfigurationName(settings, device, media); }
                catch (Autodesk.AutoCAD.Runtime.Exception retry)
                {
                    throw new InvalidOperationException($"无法应用 PDF 纸张“{media}”（目标 {target[0]:0.#} × {target[1]:0.#} mm）：{retry.Message}", retry);
                }
            }
            validator.RefreshLists(settings);
            ApplyPlotStep("设置毫米单位", () => validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters));
            var mediaSize = ParseMediaSize(media);
            var mediaLandscape = mediaSize != null && mediaSize[0] > mediaSize[1];
            // Derive the requested orientation from the normalized target
            // dimensions, not only from the display text.  This keeps edited
            // rows and custom media (whose canonical name may be portrait)
            // consistent: 1051x594 always requires a 90-degree rotation when
            // the available media is stored as 594x1051.
            var desiredLandscape = target[0] > target[1];
            try
            {
                validator.SetPlotRotation(settings, desiredLandscape == mediaLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception rotationError)
            {
                // Some AutoCAD/TArch PDF drivers reject a rotation on a
                // user-defined PMP medium even though the medium itself is
                // valid. Keep the page and let ScaleToFit fit the window;
                // the merger later restores the requested paper orientation.
                WriteDiagnostic($"打印纸张 {media} 拒绝方向旋转，已使用默认方向：{rotationError.Message}", rotationError);
            }
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

        private static void ApplyPlotStep(string name, Action action)
        {
            try { action(); }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
            {
                throw new InvalidOperationException($"打印设置步骤“{name}”失败：{exception.Message}", exception);
            }
        }

        private static Extents2d GetPlotWindowInDcs(SheetItem sheet)
        {
            var extents = new Extents3d(
                new Point3d(sheet.MinX, sheet.MinY, 0d),
                new Point3d(sheet.MaxX, sheet.MaxY, 0d));
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null) return new Extents2d(sheet.MinX, sheet.MinY, sheet.MaxX, sheet.MaxY);
            using (var view = document.Editor.GetCurrentView())
            {
                var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
                wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                extents.TransformBy(wcsToDcs.Inverse());
            }
            return new Extents2d(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y);
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
            if (note.IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0 || (sheet?.SheetNumber ?? string.Empty).IndexOf("总平图", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
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

        private static void EnsureOutputStorageCapacity(string outputPath, long sourceBytes)
        {
            var freeSpace = AvailableFreeSpace(outputPath);
            var required = Math.Max(MinimumFreeSpaceBytes, sourceBytes + 256L * 1024L * 1024L);
            if (freeSpace >= 0 && freeSpace < required)
                throw new IOException("输出磁盘剩余空间不足。合并前至少需要 "
                    + Math.Ceiling(required / 1024d / 1024d) + " MB，当前约 "
                    + Math.Floor(freeSpace / 1024d / 1024d) + " MB。原有 PDF 未被覆盖。");
        }

        private static long AvailableFreeSpace(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                return string.IsNullOrWhiteSpace(root) ? -1L : new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1L; }
        }

        private static void SavePdfAtomically(PdfSharp.Pdf.PdfDocument document, string outputPath)
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("PDF 输出目录无效。");
            Directory.CreateDirectory(directory);
            var stagingPath = Path.Combine(directory, "." + Path.GetFileName(outputPath) + "."
                + Guid.NewGuid().ToString("N") + ".bpp.tmp");
            try
            {
                document.Save(stagingPath);
                if (!File.Exists(stagingPath) || new FileInfo(stagingPath).Length <= 0)
                    throw new IOException("PDF 合并程序没有生成有效的暂存文件。");
                CommitStagedFile(stagingPath, outputPath);
            }
            finally
            {
                TryDelete(stagingPath);
            }
        }

        private static void CommitStagedFile(string stagingPath, string outputPath)
        {
            if (!File.Exists(outputPath))
            {
                File.Move(stagingPath, outputPath);
                return;
            }

            try
            {
                File.Replace(stagingPath, outputPath, null, true);
                return;
            }
            catch (Exception replaceError)
            {
                var backupPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".bpp.backup";
                try
                {
                    File.Move(outputPath, backupPath);
                    try
                    {
                        File.Move(stagingPath, outputPath);
                        TryDelete(backupPath);
                        return;
                    }
                    catch (Exception moveError)
                    {
                        TryDelete(outputPath);
                        try
                        {
                            if (File.Exists(backupPath)) File.Move(backupPath, outputPath);
                        }
                        catch
                        {
                            // Keep the backup file rather than deleting the
                            // user's previous valid PDF when restoration fails.
                        }
                        throw new IOException("备用替换失败：" + moveError.Message
                            + (File.Exists(backupPath) ? "；旧 PDF 备份保留在 " + backupPath : string.Empty), moveError);
                    }
                }
                catch (Exception fallbackError)
                {
                    throw new IOException("无法安全替换原 PDF，旧文件已尽量保留。File.Replace："
                        + replaceError.Message + "；备用替换：" + fallbackError.Message, fallbackError);
                }
            }
        }

        private static bool SamePaperSize(double actualWidth, double actualHeight, double expectedWidth, double expectedHeight)
        {
            return (SameDimension(actualWidth, expectedWidth) && SameDimension(actualHeight, expectedHeight))
                || (SameDimension(actualWidth, expectedHeight) && SameDimension(actualHeight, expectedWidth));
        }

        private static bool SameDimension(double actual, double expected)
        {
            return Math.Abs(actual - expected) <= Math.Max(3d, expected * .01d);
        }

        private static double PointsToMillimeters(double points) => points * 25.4d / 72d;
        private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string FormatMillimeters(double width, double height) =>
            FormatNumber(width) + "×" + FormatNumber(height);

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

        private static string UniqueDirectory(string requested)
        {
            if (!Directory.Exists(requested)) return requested;
            var day = requested + " " + DateTime.Now.ToString("yyyyMMdd");
            if (!Directory.Exists(day)) return day;
            var minute = day + "-" + DateTime.Now.ToString("HHmm");
            if (!Directory.Exists(minute)) return minute;
            return minute + "-" + DateTime.Now.ToString("ss");
        }

        private static string ResolveOutputPath(string path, bool overwrite)
        {
            if (overwrite && File.Exists(path)) File.Delete(path);
            return overwrite ? path : UniquePath(path);
        }
    }
}
