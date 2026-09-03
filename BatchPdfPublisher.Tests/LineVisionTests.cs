using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

internal static class LineVisionTests
{
    private static int _executed;

    private static void Main()
    {
        Run("RecognizesOrthogonalAndDiagonalLines", RecognizesOrthogonalAndDiagonalLines);
        Run("HonorsCropRegion", HonorsCropRegion);
        Run("MergesSmallCollinearGap", MergesSmallCollinearGap);
        Run("DoesNotMergeOppositeDiagonals", DoesNotMergeOppositeDiagonals);
        Run("PreservesArbitraryLineAngle", PreservesArbitraryLineAngle);
        Run("UsesConfigurableOrthogonalTolerance", UsesConfigurableOrthogonalTolerance);
        Run("RecognizesClosedCircle", RecognizesClosedCircle);
        Run("MasksRecognizedTextBeforeLineDetection", MasksRecognizedTextBeforeLineDetection);
        var worker = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_OCR_WORKER");
        if (!string.IsNullOrWhiteSpace(worker) && File.Exists(worker)) Run("RecognizesTextThroughIsolatedWorker", () => RecognizesTextThroughIsolatedWorker(worker));
        Console.WriteLine("Executed " + _executed + " LineVision tests; 0 failed.");
    }

    private static void RecognizesOrthogonalAndDiagonalLines()
    {
        WithImage(260, 200, graphics =>
        {
            graphics.DrawLine(Pens.Black, 20, 35, 230, 35);
            graphics.DrawLine(Pens.Black, 55, 20, 55, 180);
            graphics.DrawLine(Pens.Black, 80, 170, 190, 60);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Horizontal && x.Length > 180), "未识别主水平线");
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Vertical && x.Length > 130), "未识别主垂直线");
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Diagonal && x.Length > 120), "未识别45度斜线");
            }
        });
    }

    private static void HonorsCropRegion()
    {
        WithImage(300, 160, graphics =>
        {
            graphics.DrawLine(Pens.Black, 10, 25, 130, 25);
            graphics.DrawLine(Pens.Black, 170, 115, 290, 115);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, new Rectangle(150, 80, 145, 70), Settings()))
            {
                Equal(145, result.Width); Equal(70, result.Height);
                True(result.Segments.Any(x => x.Direction == LineVisionDirection.Horizontal && x.Length > 100), "裁剪范围内线段丢失");
            }
        });
    }

    private static void MergesSmallCollinearGap()
    {
        var lines = new[]
        {
            Segment(0, 10, 40, 10, LineVisionDirection.Horizontal),
            Segment(43, 11, 90, 11, LineVisionDirection.Horizontal)
        };
        var merged = LineVisionProcessor.MergeSegments(lines, 2, 4);
        Equal(1, merged.Count); True(merged[0].Length >= 89, "共线线段没有跨越小间隙合并");
        Equal(merged[0].Y1, merged[0].Y2);
    }

    private static void DoesNotMergeOppositeDiagonals()
    {
        var lines = new[]
        {
            Segment(0, 0, 50, 50, LineVisionDirection.Diagonal),
            Segment(0, 50, 50, 0, LineVisionDirection.Diagonal)
        };
        Equal(2, LineVisionProcessor.MergeSegments(lines, 3, 5).Count);
    }

    private static void RecognizesClosedCircle()
    {
        WithImage(180, 180, graphics => graphics.DrawEllipse(Pens.Black, 45, 45, 90, 90), path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                True(result.Circles.Any(circle => Math.Abs(circle.CenterX - 90) < 5 && Math.Abs(circle.CenterY - 90) < 5 && Math.Abs(circle.Radius - 45) < 7), "未识别闭合圆形");
            }
        });
    }

    private static void PreservesArbitraryLineAngle()
    {
        WithImage(260, 180, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawLine(pen, 25, 145, 225, 70);
        }, path =>
        {
            using (var result = LineVisionProcessor.Analyze(path, null, Settings()))
            {
                var line = result.Segments.Where(item => item.Direction == LineVisionDirection.Angled).OrderByDescending(item => item.Length).FirstOrDefault();
                True(line != null && line.Length > 170, "任意角度直线没有保留");
                var angle = Math.Abs(Math.Atan2(line.Y2 - line.Y1, line.X2 - line.X1) * 180d / Math.PI);
                True(Math.Abs(angle - 20.6d) < 3d, "任意角度直线被错误拉成横线、竖线或45度线");
            }
        });
    }

    private static void UsesConfigurableOrthogonalTolerance()
    {
        WithImage(280, 120, graphics =>
        {
            using (var pen = new Pen(Color.Black, 3f)) graphics.DrawLine(pen, 20, 70, 255, 58);
        }, path =>
        {
            var strict = Settings(); strict.OrthogonalToleranceDegrees = 2d;
            using (var result = LineVisionProcessor.Analyze(path, null, strict))
                True(result.Segments.Any(item => item.Direction == LineVisionDirection.Angled && item.Length > 190), "严格容差下斜线被错误吸附为水平线");
            var loose = Settings(); loose.OrthogonalToleranceDegrees = 5d;
            using (var result = LineVisionProcessor.Analyze(path, null, loose))
                True(result.Segments.Any(item => item.Direction == LineVisionDirection.Horizontal && item.Length > 190), "宽松容差没有按设置吸附为水平线");
        });
    }

    private static void MasksRecognizedTextBeforeLineDetection()
    {
        WithImage(240, 100, graphics => graphics.DrawLine(Pens.Black, 10, 50, 230, 50), path =>
        {
            var text = new LineVisionOcrTextRegion
            {
                Text = "3600", Confidence = 0.9, IsEnabled = true,
                Polygon = new[] { new PointF(95, 38), new PointF(145, 38), new PointF(145, 62), new PointF(95, 62) }
            };
            using (var result = LineVisionProcessor.Analyze(path, null, Settings(), new[] { text }, true, 2, CancellationToken.None))
                True(!result.Segments.Any(line => line.Direction == LineVisionDirection.Horizontal && Math.Min(line.X1, line.X2) < 95 && Math.Max(line.X1, line.X2) > 145), "文字遮罩后仍生成了穿过文字的直线");
        });
    }

    private static void RecognizesTextThroughIsolatedWorker(string workerPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoLineVisionOcrTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); UserDataPaths.TestRootDirectory = root;
        try
        {
            WithImage(900, 260, graphics =>
            {
                using (var font = new Font("Arial", 68f, FontStyle.Bold)) graphics.DrawString("ROOM 3600", font, Brushes.Black, 25, 70);
            }, path =>
            {
                var engine = new LineVisionOcrWorkerClient(workerPath);
                var result = engine.RecognizeAsync(path, new LineVisionOcrOptions { Language = "en-US", MinimumConfidence = 0.5 }, CancellationToken.None).GetAwaiter().GetResult();
                True(result.TextRegions.Any(), "独立 OCR Worker 没有返回文字区域");
                True(result.TextRegions.Any(item => (item.Text ?? string.Empty).IndexOf("3600", StringComparison.OrdinalIgnoreCase) >= 0), "独立 OCR Worker 没有识别尺寸数字 3600");
            });
        }
        finally { UserDataPaths.TestRootDirectory = null; try { Directory.Delete(root, true); } catch { } }
    }

    private static LineVisionSettings Settings()
    {
        return new LineVisionSettings { Threshold = 128, MinimumLineLengthPixels = 14, CloseGapPixels = 2, CollinearTolerancePixels = 3, MergeGapPixels = 5, DetectDiagonals = true, OrthogonalToleranceDegrees = 2d };
    }

    private static LineVisionSegment Segment(double x1, double y1, double x2, double y2, LineVisionDirection direction)
    {
        return new LineVisionSegment { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Direction = direction, Confidence = 1d };
    }

    private static void WithImage(int width, int height, Action<Graphics> draw, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "WanluoLineVision-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.None; draw(graphics); bitmap.Save(path, ImageFormat.Png);
            }
            test(path);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void Run(string name, Action test) { test(); _executed++; Console.WriteLine("PASS " + name); }
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected " + expected + ", actual " + actual); }
}
