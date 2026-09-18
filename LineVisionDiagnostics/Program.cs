using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace Wanluo.LineVision.Diagnostics
{
    /// <summary>
    /// 图像转 CAD 的诊断工具：拿同一张图分别跑两条识别路径，输出可对比的量化指标。
    /// 识别质量光看代码判断不了，必须落到数字上：识别出多少线、覆盖了多少墨迹、
    /// 有多少是碎渣。用法见 PrintUsage。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var console = new StringBuilder();
            try
            {
                var options = Options.Parse(args, console);
                if (options == null) { Console.Write(console.ToString()); return 1; }
                if (options.ProbeMask) { MaskProbe.Run(options.ImagePath, options.Threshold); return 0; }
                Run(options, console);
                Console.Write(console.ToString());
                if (!string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    File.WriteAllText(options.OutputPath, console.ToString(), new UTF8Encoding(false));
                    Console.WriteLine("已写入报告：" + options.OutputPath);
                }
                return 0;
            }
            catch (Exception exception)
            {
                console.AppendLine("失败：" + exception.GetBaseException().Message);
                Console.Write(console.ToString());
                return 1;
            }
        }

        private static void Run(Options options, StringBuilder report)
        {
            report.AppendLine("图片      : " + options.ImagePath);
            report.AppendLine("二值化阈值: " + (options.Threshold > 0 ? options.Threshold.ToString(CultureInfo.InvariantCulture) : "0（自动 Otsu）"));
            report.AppendLine("矢量内核  : " + options.WorkerPath);
            report.AppendLine("内核可用  : " + (File.Exists(options.WorkerPath) ? "是" : "否（骨架部分将被跳过）"));
            if (!string.IsNullOrWhiteSpace(options.OverlayPath))
                report.AppendLine("漏检可视化: " + options.OverlayPath + "（红=有墨迹但没有任何线覆盖）");
            report.AppendLine();

            // ── 1. 真正的墨迹底图：用与 LineVisionProcessor 相同的二值化规则，作为覆盖率分母。
            var ink = InkMask.Load(options.ImagePath, options.Region, options.Threshold);
            report.AppendLine("=== 墨迹底图（覆盖率的分母） ===");
            report.AppendLine("  分析尺寸 : " + ink.Width + " x " + ink.Height + " px"
                + (ink.Ratio > 1d ? "（原图按 " + ink.Ratio.ToString("0.###", CultureInfo.InvariantCulture) + " 缩放）" : string.Empty));
            report.AppendLine("  墨迹像素 : " + ink.DarkCount.ToString("N0", CultureInfo.InvariantCulture)
                + "（占 " + (100d * ink.DarkCount / (ink.Width * (double)ink.Height)).ToString("0.##", CultureInfo.InvariantCulture) + "%）");
            report.AppendLine();

            // ── 2. Legacy 算法：用界面上的设置，观察 MinimumLineLengthPixels 的真实作用。
            report.AppendLine("=== 旧算法（兼容旧算法，走 LineVisionProcessor） ===");
            report.AppendLine("  最短线阈值  线条数   总长(px)  覆盖率%   <10px碎线   端点悬空数");
            foreach (var minimum in new[] { 0, 6, 12, 18, 30, 45 })
            {
                var settings = new LineVisionSettings
                {
                    Threshold = options.Threshold,
                    MinimumLineLengthPixels = Math.Max(3, minimum),
                    CloseGapPixels = 2,
                    CollinearTolerancePixels = 3,
                    MergeGapPixels = 5,
                    DetectDiagonals = true,
                    OrthogonalToleranceDegrees = 2d,
                    BuildPolylines = false,
                    VectorMode = LineVisionVectorMode.Legacy,
                    DetectWallFills = true
                };
                using (var result = LineVisionProcessor.Analyze(options.ImagePath, options.Region, settings,
                    CancellationToken.None, null))
                {
                    var segments = result.Segments.Where(value => value.IsEnabled && value.Length > 0.5).ToList();
                    var metrics = Metrics.ForSegments(segments, ink);
                    report.AppendLine("  " + Pad(minimum.ToString(CultureInfo.InvariantCulture), 12)
                        + Pad(segments.Count.ToString("N0", CultureInfo.InvariantCulture), 9)
                        + Pad(metrics.TotalLength.ToString("N0", CultureInfo.InvariantCulture), 10)
                        + Pad(metrics.CoveragePercent.ToString("0.0", CultureInfo.InvariantCulture), 9)
                        + Pad(metrics.ShortCount.ToString("N0", CultureInfo.InvariantCulture), 12)
                        + metrics.DanglingEndpoints.ToString("N0", CultureInfo.InvariantCulture));
                }
            }
            report.AppendLine();

            // ── 3. 当前实际生效的路径：骨架矢量内核（界面选“建筑中心线”时用的就是它）。
            // 合并间隙用界面默认的 5px，也就是用户实际会拿到的结果。
            report.AppendLine("=== 当前生效路径（骨架矢量内核，补断线/共线合并按界面默认开启） ===");
            if (!File.Exists(options.WorkerPath))
            {
                report.AppendLine("  内核不存在，跳过。请先跑一次发布构建，或指定 --worker 路径。");
                return;
            }
            report.AppendLine("  最短线阈值  折线数   总长(px)  覆盖率%   <10px碎线   端点悬空数   耗时ms");
            foreach (var minimum in new[] { 0, 6, 12, 18, 30, 45 })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var polylines = RunWorker(options, minimum, 5d);
                watch.Stop();
                var metrics = Metrics.ForPolylines(polylines, ink);
                report.AppendLine("  " + Pad(minimum.ToString(CultureInfo.InvariantCulture), 12)
                    + Pad(polylines.Count.ToString("N0", CultureInfo.InvariantCulture), 9)
                    + Pad(metrics.TotalLength.ToString("N0", CultureInfo.InvariantCulture), 10)
                    + Pad(metrics.CoveragePercent.ToString("0.0", CultureInfo.InvariantCulture), 9)
                    + Pad(metrics.ShortCount.ToString("N0", CultureInfo.InvariantCulture), 12)
                    + Pad(metrics.DanglingEndpoints.ToString("N0", CultureInfo.InvariantCulture), 13)
                    + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
            report.AppendLine();
                report.AppendLine("  参照：同一内核、关掉合并（最短线 0），看合并本身做了多少事：");
            var baselineWatch = System.Diagnostics.Stopwatch.StartNew();
            var rawPolylines = RunWorker(options, 0, 0d);
            baselineWatch.Stop();
            var rawMetrics = Metrics.ForPolylines(rawPolylines, ink);
            report.AppendLine("    折线 " + rawPolylines.Count.ToString("N0", CultureInfo.InvariantCulture)
                + "，总长 " + rawMetrics.TotalLength.ToString("N0", CultureInfo.InvariantCulture)
                + "，覆盖率 " + rawMetrics.CoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)
                + "%，<10px碎线 " + rawMetrics.ShortCount.ToString("N0", CultureInfo.InvariantCulture)
                + "，耗时 " + baselineWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms");

            CompareMasks(options, ink, report);
            if (!string.IsNullOrWhiteSpace(options.OverlayPath)) WriteOverlay(options, ink, report);
            if (!string.IsNullOrWhiteSpace(options.WallOverlayPath)) WriteWallOverlay(options, report);
        }

        /// <summary>
        /// 把"墙体检测"单独画出来：绿=墙体边框(Outer)、青=孔洞、半透明红=会被填充的实心区域。
        /// 用于判断"边框+填充"到底是双线还是实心块——图画法里同一件事用两种画法表达时，
        /// 只看最终 CAD 很难分辨，必须把中间数据摊开看。
        /// </summary>
        private static void WriteWallOverlay(Options options, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("=== 墙体检测可视化 ===");
            try
            {
                var client = new LineVisionVectorWorkerClient(options.WorkerPath);
                var settings = new LineVisionSettings
                {
                    Threshold = options.Threshold,
                    MinimumLineLengthPixels = 5,
                    MergeGapPixels = 5,
                    CollinearTolerancePixels = 3,
                    VectorMode = LineVisionVectorMode.Hybrid,
                    DetectWallFills = true,
                    MinimumWallThicknessPixels = 3d,
                    MaximumWallThicknessPixels = 80d,
                    WallFillMode = LineVisionWallFillMode.Solid
                };
                var vector = client.VectorizeAsync(options.ImagePath, options.Region, settings, null, 0, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var walls = vector.WallRegions.Where(value => value.Outer.Count >= 3).ToList();
                var polylineCount = vector.Polylines.Count;

                using (var source = new Bitmap(options.ImagePath))
                using (var canvas = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(canvas))
                    {
                        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                        // 会被填充的实心区域：半透明红
                        using (var fill = new SolidBrush(Color.FromArgb(90, 255, 0, 0)))
                            foreach (var wall in walls)
                                graphics.FillPolygon(fill, wall.Outer.ToArray());
                        // 边界线：绿=外圈，青=孔洞
                        using (var outerPen = new Pen(Color.FromArgb(255, 0, 200, 0), 2f))
                        using (var holePen = new Pen(Color.FromArgb(255, 0, 200, 255), 2f))
                        {
                            foreach (var wall in walls)
                            {
                                graphics.DrawPolygon(outerPen, wall.Outer.ToArray());
                                foreach (var hole in wall.Holes.Where(value => value.Count >= 3)) graphics.DrawPolygon(holePen, hole.ToArray());
                            }
                        }
                    }
                    canvas.Save(options.WallOverlayPath, ImageFormat.Png);
                }
                report.AppendLine("  绿框=墙体边框，青框=孔洞，半透明红=会被填充的实心区域");
                report.AppendLine("  墙体区域 " + walls.Count.ToString(CultureInfo.InvariantCulture) + " 个，其中带孔洞 "
                    + walls.Count(value => value.Holes.Count > 0).ToString(CultureInfo.InvariantCulture) + " 个");
                report.AppendLine("  同图中心线折线 " + polylineCount.ToString(CultureInfo.InvariantCulture) + " 条");
                report.AppendLine("  已写入: " + options.WallOverlayPath);
            }
            catch (Exception exception) { report.AppendLine("  墙体可视化失败：" + exception.Message); }
        }

        /// <summary>
        /// 把「有墨迹、但没有任何已识别线覆盖」的像素标红，直接看出是哪几条线没出来。
        /// 覆盖率是个总数，看不出位置；这张图能直接指出漏在哪。
        /// </summary>
        private static void WriteOverlay(Options options, InkMask ink, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("=== 漏检可视化 ===");
            try
            {
                var polylines = RunWorker(options, 5, 5d);
                var covered = new bool[ink.Width * ink.Height];
                foreach (var line in polylines)
                {
                    var points = line.Points;
                    for (var index = 1; index < points.Count; index++)
                        RasterizeLine(points[index - 1], points[index], covered, ink.Width, ink.Height, 2);
                    if (line.Closed && points.Count > 1)
                        RasterizeLine(points[points.Count - 1], points[0], covered, ink.Width, ink.Height, 2);
                }
                var missed = 0;
                using (var canvas = new Bitmap(ink.Width, ink.Height, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(canvas)) graphics.Clear(Color.White);
                    var data = canvas.LockBits(new Rectangle(0, 0, ink.Width, ink.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        unsafe
                        {
                            var start = (byte*)data.Scan0;
                            for (var y = 0; y < ink.Height; y++)
                            {
                                var row = start + y * data.Stride;
                                for (var x = 0; x < ink.Width; x++)
                                {
                                    var index = y * ink.Width + x;
                                    byte r, g, b;
                                    if (!ink.Dark[index]) { r = g = b = 255; }
                                    else if (covered[index]) { r = g = b = 225; }          // 已识别的墨迹（含线周边）：很浅
                                    else { r = 230; g = 60; b = 60; missed++; }            // 漏掉的墨迹：红
                                    row[x * 3] = b; row[x * 3 + 1] = g; row[x * 3 + 2] = r;
                                }
                            }
                        }
                    }
                    finally { canvas.UnlockBits(data); }

                    // 把识别出的折线本身画成橙色，直接看清"认出了哪条、漏了哪条"。
                    using (var graphics = Graphics.FromImage(canvas))
                    using (var pen = new Pen(Color.FromArgb(255, 140, 0), 1f))
                    {
                        foreach (var line in polylines)
                        {
                            var points = line.Points.Select(point => new PointF(point.X, point.Y)).ToArray();
                            if (points.Length >= 2) graphics.DrawLines(pen, points);
                            if (line.Closed && points.Length > 2) graphics.DrawLine(pen, points[points.Length - 1], points[0]);
                        }
                    }
                    canvas.Save(options.OverlayPath, ImageFormat.Png);
                }
                report.AppendLine("  橙线 = 已识别出的几何；红色 = 有墨迹但没有任何线经过；浅灰 = 已被线覆盖的墨迹");
                report.AppendLine("  漏掉的墨迹像素: " + missed.ToString("N0", CultureInfo.InvariantCulture)
                    + " / " + ink.DarkCount.ToString("N0", CultureInfo.InvariantCulture)
                    + "（" + (ink.DarkCount == 0 ? "0" : (100d * missed / ink.DarkCount).ToString("0.0", CultureInfo.InvariantCulture)) + "%）");
                report.AppendLine("  注意：实心填黑的墙体内部不会被任何\"线\"覆盖，这部分也会计入红色，属于测量口径而非漏识别。");
                report.AppendLine("  已写入: " + options.OverlayPath);
            }
            catch (Exception exception) { report.AppendLine("  可视化失败：" + exception.Message); }
        }

        /// <summary>
        /// 关键判定：内核的二值化掩膜与主程序是否一致。若两者一致而覆盖率仍低，说明墨迹是
        /// 丢在骨架追踪之后；若内核掩膜本身就少了一截，则问题在二值化那一步。
        /// </summary>
        private static void CompareMasks(Options options, InkMask ink, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("=== 二值化掩膜对比（判定墨迹丢在哪一步） ===");
            var dump = Path.Combine(Path.GetTempPath(), "linevision-mask-" + Guid.NewGuid().ToString("N"));
            try
            {
                var client = new LineVisionVectorWorkerClient(options.WorkerPath);
                var settings = new LineVisionSettings
                {
                    Threshold = options.Threshold,
                    MinimumLineLengthPixels = 3,
                    MergeGapPixels = 0,
                    CollinearTolerancePixels = 3,
                    VectorMode = LineVisionVectorMode.Centerline,
                    DetectWallFills = false
                };
                // 直接调内核并让它导出掩膜：复用客户端不方便，这里自己起进程。
                var output = Path.Combine(dump, "result.json");
                Directory.CreateDirectory(dump);
                var arguments = "--input \"" + options.ImagePath + "\" --output \"" + output
                    + "\" --mode centerline --threshold " + options.Threshold
                    + " --minimum 0 --merge-gap 0 --collinear 3 --dump-mask \"" + dump + "\"";
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = options.WorkerPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    process.WaitForExit();
                }
                var workerMaskPath = Path.Combine(dump, "01-binary.png");
                if (!File.Exists(workerMaskPath)) { report.AppendLine("  内核未导出掩膜，跳过对比。"); return; }
                var workerDark = LoadDarkMask(workerMaskPath);
                var region = ink.Region;
                var overlapWidth = Math.Min(ink.Width, workerDark.GetLength(0) - region.X);
                var overlapHeight = Math.Min(ink.Height, workerDark.GetLength(1) - region.Y);
                var workerCount = 0; var processorOnly = 0; var workerOnly = 0; var both = 0;
                for (var y = 0; y < overlapHeight; y++)
                    for (var x = 0; x < overlapWidth; x++)
                    {
                        var fromWorker = workerDark[region.X + x, region.Y + y];
                        var fromProcessor = ink.Dark[y * ink.Width + x];
                        if (fromWorker) workerCount++;
                        if (fromWorker && fromProcessor) both++;
                        else if (fromProcessor) processorOnly++;
                        else if (fromWorker) workerOnly++;
                    }
                report.AppendLine("  重叠区域    : " + overlapWidth + " x " + overlapHeight + " px");
                report.AppendLine("  主程序墨迹  : " + ink.DarkCount.ToString("N0", CultureInfo.InvariantCulture));
                report.AppendLine("  内核墨迹    : " + workerCount.ToString("N0", CultureInfo.InvariantCulture));
                report.AppendLine("  两者都有    : " + both.ToString("N0", CultureInfo.InvariantCulture));
                report.AppendLine("  仅主程序有  : " + processorOnly.ToString("N0", CultureInfo.InvariantCulture)
                    + "（内核二值化就漏掉的墨迹）");
                report.AppendLine("  仅内核有    : " + workerOnly.ToString("N0", CultureInfo.InvariantCulture)
                    + "（内核多出来的噪点）");

                // 分阶段统计：用内核自己的二值化掩膜做分母，看墨迹是在哪一步消失的。
                var thinnedPath = Path.Combine(dump, "02-thinned.png");
                var workerInkCount = 0;
                for (var y = 0; y < workerDark.GetLength(1); y++)
                    for (var x = 0; x < workerDark.GetLength(0); x++)
                        if (workerDark[x, y]) workerInkCount++;
                report.AppendLine();
                report.AppendLine("  以内核自己的墨迹为分母，逐步看丢在哪：");
                report.AppendLine("    阶段                墨迹/像素数   占比%");
                report.AppendLine("    二值化（内核）      " + Pad(workerInkCount.ToString("N0", CultureInfo.InvariantCulture), 14) + "100.0");
                if (File.Exists(thinnedPath))
                {
                    var thinned = LoadDarkMask(thinnedPath);
                    var thinnedCount = 0;
                    for (var y = 0; y < thinned.GetLength(1); y++)
                        for (var x = 0; x < thinned.GetLength(0); x++)
                            if (thinned[x, y]) thinnedCount++;
                    report.AppendLine("    细化后骨架          " + Pad(thinnedCount.ToString("N0", CultureInfo.InvariantCulture), 14)
                        + (100d * thinnedCount / workerInkCount).ToString("0.0", CultureInfo.InvariantCulture));

                    // 决定性判定：把追踪出的折线栅格化，看骨架像素有多少落在线上。
                    // 接近 100% 说明追踪没有丢线，之前覆盖率低是分母口径（原图反锯齿像素）造成的。
                    var traced = ReplayPolylines(options);
                    var covered = new bool[thinned.GetLength(0) * thinned.GetLength(1)];
                    foreach (var line in traced)
                    {
                        var points = line.Points;
                        for (var index = 1; index < points.Count; index++)
                            RasterizeLine(points[index - 1], points[index], covered, thinned.GetLength(0), thinned.GetLength(1));
                        if (line.Closed && points.Count > 1)
                            RasterizeLine(points[points.Count - 1], points[0], covered, thinned.GetLength(0), thinned.GetLength(1));
                    }
                    var hit = 0;
                    for (var y = 0; y < thinned.GetLength(1); y++)
                        for (var x = 0; x < thinned.GetLength(0); x++)
                            if (thinned[x, y] && covered[y * thinned.GetLength(0) + x]) hit++;
                    report.AppendLine("    追踪出的折线覆盖骨架: "
                        + (thinnedCount == 0 ? "0" : (100d * hit / thinnedCount).ToString("0.0", CultureInfo.InvariantCulture)) + "%"
                        + "（栅格化窗口 ±1px，与简化容差 1.25px 相当，剩下的差额才是真丢线）");

                    // 再用 ±2px 窗口量一次：若明显更高，说明差额只是简化造成的亚像素偏移，不是丢线。
                    var covered2 = new bool[thinned.GetLength(0) * thinned.GetLength(1)];
                    foreach (var line in traced)
                    {
                        var points = line.Points;
                        for (var index = 1; index < points.Count; index++)
                            RasterizeLine(points[index - 1], points[index], covered2, thinned.GetLength(0), thinned.GetLength(1), 2);
                        if (line.Closed && points.Count > 1)
                            RasterizeLine(points[points.Count - 1], points[0], covered2, thinned.GetLength(0), thinned.GetLength(1), 2);
                    }
                    var hit2 = 0;
                    for (var y = 0; y < thinned.GetLength(1); y++)
                        for (var x = 0; x < thinned.GetLength(0); x++)
                            if (thinned[x, y] && covered2[y * thinned.GetLength(0) + x]) hit2++;
                    report.AppendLine("    同上（±2px 窗口）   : "
                        + (thinnedCount == 0 ? "0" : (100d * hit2 / thinnedCount).ToString("0.0", CultureInfo.InvariantCulture)) + "%");
                }
            }
            catch (Exception exception) { report.AppendLine("  掩膜对比失败：" + exception.Message); }
            finally { try { Directory.Delete(dump, true); } catch { } }
        }

        private static bool[,] LoadDarkMask(string path)
        {
            using (var bitmap = new Bitmap(path))
            {
                var result = new bool[bitmap.Width, bitmap.Height];
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        var start = (byte*)data.Scan0;
                        for (var y = 0; y < bitmap.Height; y++)
                        {
                            var row = start + y * data.Stride;
                            for (var x = 0; x < bitmap.Width; x++)
                            {
                                var luminance = (row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8;
                                result[x, y] = luminance <= 128;
                            }
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                return result;
            }
        }

        /// <summary>重新跑一次内核（最小线 0、不合并），只用来拿"追踪结果"，用于和骨架掩膜比对。</summary>
        private static List<Polylines.Line> ReplayPolylines(Options options)
        {
            var client = new LineVisionVectorWorkerClient(options.WorkerPath);
            var settings = new LineVisionSettings
            {
                Threshold = options.Threshold,
                MinimumLineLengthPixels = 3,
                MergeGapPixels = 0,
                CollinearTolerancePixels = 3,
                VectorMode = LineVisionVectorMode.Centerline,
                DetectWallFills = false
            };
            var vector = client.VectorizeAsync(options.ImagePath, null, settings, null, 0, CancellationToken.None)
                .GetAwaiter().GetResult();
            return vector.Polylines.Where(value => value.Points != null && value.Points.Count >= 2)
                .Select(value => new Polylines.Line(value.Points.Select(point => new PointF(point.X, point.Y)).ToList(), value.IsClosed))
                .ToList();
        }

        private static void RasterizeLine(PointF first, PointF second, bool[] target, int width, int height, int radius = 1)
        {
            var dx = second.X - first.X; var dy = second.Y - first.Y;
            var steps = (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) * 2d);
            if (steps <= 0) steps = 1;
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps;
                var x = (int)Math.Round(first.X + dx * t);
                var y = (int)Math.Round(first.Y + dy * t);
                for (var oy = -radius; oy <= radius; oy++)
                    for (var ox = -radius; ox <= radius; ox++)
                    {
                        var px = x + ox; var py = y + oy;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        target[py * width + px] = true;
                    }
            }
        }

        private static List<Polylines.Line> RunWorker(Options options, int minimum, double mergeGap)
        {
            var client = new LineVisionVectorWorkerClient(options.WorkerPath);
            var settings = new LineVisionSettings
            {
                Threshold = options.Threshold,
                MinimumLineLengthPixels = Math.Max(3, minimum),
                MergeGapPixels = (int)Math.Round(mergeGap),
                CollinearTolerancePixels = 3,
                OrthogonalToleranceDegrees = 2d,
                VectorMode = LineVisionVectorMode.Centerline,
                DetectWallFills = false,
                MinimumWallThicknessPixels = 3d,
                MaximumWallThicknessPixels = 80d
            };
            var vector = client.VectorizeAsync(options.ImagePath, options.Region, settings, null, 0, CancellationToken.None)
                .GetAwaiter().GetResult();
            return vector.Polylines.Where(value => value.Points != null && value.Points.Count >= 2)
                .Select(value => new Polylines.Line(value.Points.Select(point => new PointF(point.X, point.Y)).ToList(), value.IsClosed))
                .ToList();
        }

        private static string Pad(string value, int width)
        {
            if (value.Length >= width) return value + " ";
            return value + new string(' ', width - value.Length);
        }
    }

    internal sealed class Options
    {
        public string ImagePath { get; private set; }
        public Rectangle? Region { get; private set; }
        public int Threshold { get; private set; }
        public string WorkerPath { get; private set; }
        public string OutputPath { get; private set; }
        public string OverlayPath { get; private set; }
        public string WallOverlayPath { get; private set; }
        public bool ProbeMask { get; private set; }

        public static Options Parse(string[] args, StringBuilder report)
        {
            if (args == null || args.Length == 0 || args[0] == "-h" || args[0] == "--help")
            {
                report.AppendLine("图像转 CAD 诊断工具");
                report.AppendLine();
                report.AppendLine("  LineVisionDiagnostics --image <图片路径> [选项]");
                report.AppendLine();
                report.AppendLine("  --image    <路径>   要分析的图片（必填）");
                report.AppendLine("  --region   x,y,w,h  只分析指定区域，与界面“框选范围”一致");
                report.AppendLine("  --threshold <0-254> 二值化阈值，0 表示自动（默认 0）");
                report.AppendLine("  --worker   <路径>   矢量内核 exe，默认取 dist 下的 R24 版本");
                report.AppendLine("  --output   <路径>   把报告同时写入该文件");
                report.AppendLine("  --overlay  <路径>   输出漏检可视化 PNG：红=有墨迹但无任何线覆盖");
                report.AppendLine("  --probe-mask        只检查二值化：直方图、Otsu 阈值、反转判据");

                report.AppendLine("  --wall-overlay <路径> 输出墙体检测可视化：绿=边框 青=孔洞 红=被填充区域");
                report.AppendLine();
                report.AppendLine("指标说明：");
                report.AppendLine("  覆盖率%    = 墨迹像素中，落在某条已识别线 2px 邻域内的比例（越高说明漏得越少）");
                report.AppendLine("  <10px碎线  = 长度不足 10px 的线/折线段数（越高说明碎渣越多）");
                report.AppendLine("  端点悬空数 = 端点附近没有其它线接续的端点数（越高说明图形越碎）");
                return null;
            }
            var options = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                var name = args[index];
                var value = index + 1 < args.Length ? args[index + 1] : null;
                switch (name)
                {
                    case "--image": options.ImagePath = value; index++; break;
                    case "--threshold": options.Threshold = ParseInt(value, 0); index++; break;
                    case "--worker": options.WorkerPath = value; index++; break;
                    case "--output": options.OutputPath = value; index++; break;
                    case "--overlay": options.OverlayPath = value; index++; break;
                    case "--wall-overlay": options.WallOverlayPath = value; index++; break;
                    case "--probe-mask": options.ProbeMask = true; break;
                    case "--region":
                        {
                            var parts = (value ?? string.Empty).Split(',');
                            int x, y, w, h;
                            if (parts.Length == 4 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y)
                                && int.TryParse(parts[2], out w) && int.TryParse(parts[3], out h) && w > 0 && h > 0)
                                options.Region = new Rectangle(x, y, w, h);
                            index++;
                            break;
                        }
                    default: throw new ArgumentException("无法识别的参数：" + name);
                }
            }
            if (string.IsNullOrWhiteSpace(options.ImagePath)) throw new ArgumentException("必须用 --image 指定图片。");
            if (!File.Exists(options.ImagePath)) throw new FileNotFoundException("图片不存在。", options.ImagePath);
            if (string.IsNullOrWhiteSpace(options.WorkerPath))
            {
                var root = AppContext.BaseDirectory;
                options.WorkerPath = Path.GetFullPath(Path.Combine(root,
                    @"..\..\..\..\dist\WanLuoArchitectureTools\CadApi\R24\LineVisionVectorWorker.exe"));
            }
            return options;
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
    }

    /// <summary>墨迹底图：与 LineVisionProcessor.BuildBinary 同一套规则，作为覆盖率的分母。</summary>
    internal sealed class InkMask
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Ratio { get; private set; }
        public bool[] Dark { get; private set; }
        public int DarkCount { get; private set; }
        /// <summary>相对原图的裁剪区域，用于把内核输出对齐到同一坐标系。</summary>
        public Rectangle Region { get; private set; }

        public static InkMask Load(string path, Rectangle? requested, int threshold)
        {
            var mask = new InkMask();
            using (var loaded = new Bitmap(path))
            {
                var region = requested.HasValue
                    ? Rectangle.Intersect(new Rectangle(0, 0, loaded.Width, loaded.Height), requested.Value)
                    : new Rectangle(0, 0, loaded.Width, loaded.Height);
                using (var cropped = loaded.Clone(region, PixelFormat.Format24bppRgb))
                {
                    var ratio = Math.Max(1d, Math.Max(cropped.Width, cropped.Height) / 2600d);
                    var width = Math.Max(1, (int)Math.Round(cropped.Width / ratio));
                    var height = Math.Max(1, (int)Math.Round(cropped.Height / ratio));
                    mask.Width = width; mask.Height = height; mask.Ratio = ratio; mask.Region = region;
                    using (var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (var graphics = Graphics.FromImage(scaled))
                        {
                            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(cropped, 0, 0, width, height);
                        }
                        var dark = new bool[width * height];
                        var histogram = new int[256];
                        var data = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            unsafe
                            {
                                var start = (byte*)data.Scan0;
                                for (var y = 0; y < height; y++)
                                {
                                    var row = start + y * data.Stride;
                                    for (var x = 0; x < width; x++)
                                    {
                                        var value = (byte)((row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8);
                                        histogram[value]++;
                                    }
                                }
                            }
                        }
                        finally { scaled.UnlockBits(data); }

                        var cutoff = threshold > 0 ? Math.Max(1, Math.Min(254, threshold)) : Otsu(histogram, width * height);
                        var count = 0;
                        // 重新按灰度填 dark：上面只统计了直方图，这里需要真正的掩膜。
                        var data2 = scaled.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            unsafe
                            {
                                var start = (byte*)data2.Scan0;
                                for (var y = 0; y < height; y++)
                                {
                                    var row = start + y * data2.Stride;
                                    for (var x = 0; x < width; x++)
                                    {
                                        var value = (byte)((row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8);
                                        if (value <= cutoff) { dark[y * width + x] = true; count++; }
                                    }
                                }
                            }
                        }
                        finally { scaled.UnlockBits(data2); }
                        if (count > width * height * 0.55d)
                        {
                            for (var index = 0; index < dark.Length; index++) dark[index] = !dark[index];
                            count = dark.Length - count;
                        }
                        mask.Dark = dark; mask.DarkCount = count;
                    }
                }
            }
            return mask;
        }

        private static int Otsu(int[] histogram, int count)
        {
            long total = 0;
            for (var index = 0; index < 256; index++) total += (long)index * histogram[index];
            long sum = 0; var background = 0; var best = 127; var maximum = -1d;
            for (var threshold = 0; threshold < 255; threshold++)
            {
                background += histogram[threshold];
                if (background == 0) continue;
                var foreground = count - background;
                if (foreground == 0) break;
                sum += (long)threshold * histogram[threshold];
                var a = sum / (double)background;
                var b = (total - sum) / (double)foreground;
                var score = (double)background * foreground * (a - b) * (a - b);
                if (score > maximum) { maximum = score; best = threshold; }
            }
            return Math.Max(20, Math.Min(235, best));
        }
    }

    internal static class Polylines
    {
        internal sealed class Line
        {
            public Line(List<PointF> points, bool closed) { Points = points; Closed = closed; }
            public List<PointF> Points { get; private set; }
            public bool Closed { get; private set; }
        }
    }

    internal sealed class Metrics
    {
        public int Count { get; private set; }
        public double TotalLength { get; private set; }
        public double CoveragePercent { get; private set; }
        public int ShortCount { get; private set; }
        public int DanglingEndpoints { get; private set; }

        /// <summary>墨迹被已识别几何覆盖的比例——衡量“漏了多少”的主要指标。</summary>
        public static Metrics ForSegments(IList<LineVisionSegment> segments, InkMask ink)
        {
            var polylines = segments.Select(value => new Polylines.Line(new List<PointF>
            {
                new PointF((float)(value.X1 / ink.Ratio), (float)(value.Y1 / ink.Ratio)),
                new PointF((float)(value.X2 / ink.Ratio), (float)(value.Y2 / ink.Ratio))
            }, false)).ToList();
            return Compute(polylines, ink);
        }

        public static Metrics ForPolylines(IList<Polylines.Line> polylines, InkMask ink)
        {
            return Compute(polylines, ink);
        }

        private static Metrics Compute(IList<Polylines.Line> polylines, InkMask ink)
        {
            var metrics = new Metrics { Count = polylines.Count };
            var covered = new bool[ink.Width * ink.Height];
            var endpoints = new List<PointF>();
            foreach (var line in polylines)
            {
                var points = line.Points;
                for (var index = 1; index < points.Count; index++)
                {
                    var first = points[index - 1];
                    var second = points[index];
                    var length = Distance(first, second);
                    metrics.TotalLength += length;
                    if (length < 10d) metrics.ShortCount++;
                    Rasterize(first, second, covered, ink.Width, ink.Height, 2);
                }
                if (line.Closed && points.Count > 1)
                {
                    var length = Distance(points[points.Count - 1], points[0]);
                    metrics.TotalLength += length;
                    if (length < 10d) metrics.ShortCount++;
                    Rasterize(points[points.Count - 1], points[0], covered, ink.Width, ink.Height, 2);
                }
                else if (points.Count > 1)
                {
                    endpoints.Add(points[0]);
                    endpoints.Add(points[points.Count - 1]);
                }
            }
            var hit = 0;
            for (var index = 0; index < covered.Length; index++) if (covered[index] && ink.Dark[index]) hit++;
            metrics.CoveragePercent = ink.DarkCount == 0 ? 0d : 100d * hit / ink.DarkCount;
            metrics.DanglingEndpoints = CountDangling(endpoints);
            return metrics;
        }

        /// <summary>端点附近 3px 内没有其它端点或线段穿过，就算悬空。</summary>
        private static int CountDangling(IList<PointF> endpoints)
        {
            var dangling = 0;
            for (var index = 0; index < endpoints.Count; index++)
            {
                var near = 0;
                for (var other = 0; other < endpoints.Count; other++)
                {
                    if (other == index) continue;
                    if (Distance(endpoints[index], endpoints[other]) <= 3d) near++;
                }
                if (near == 0) dangling++;
            }
            return dangling;
        }

        private static void Rasterize(PointF first, PointF second, bool[] target, int width, int height, int radius)
        {
            var steps = (int)Math.Ceiling(Distance(first, second) * 2d);
            if (steps <= 0) steps = 1;
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps;
                var x = first.X + (second.X - first.X) * t;
                var y = first.Y + (second.Y - first.Y) * t;
                for (var dy = -radius; dy <= radius; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var px = (int)Math.Round(x) + dx;
                        var py = (int)Math.Round(y) + dy;
                        if (px < 0 || py < 0 || px >= width || py >= height) continue;
                        target[py * width + px] = true;
                    }
            }
        }

        private static double Distance(PointF first, PointF second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

