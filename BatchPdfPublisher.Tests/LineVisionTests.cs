using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

internal static class LineVisionTests
{
    private static int _executed;

    private static void Main()
    {
        Run("RecognizesOrthogonalAndDiagonalLines", RecognizesOrthogonalAndDiagonalLines);
        Run("HonorsCropRegion", HonorsCropRegion);
        Run("MergesSmallCollinearGap", MergesSmallCollinearGap);
        Run("DoesNotMergeOppositeDiagonals", DoesNotMergeOppositeDiagonals);
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

    private static LineVisionSettings Settings()
    {
        return new LineVisionSettings { Threshold = 128, MinimumLineLengthPixels = 14, CloseGapPixels = 2, CollinearTolerancePixels = 3, MergeGapPixels = 5, DetectDiagonals = true };
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
