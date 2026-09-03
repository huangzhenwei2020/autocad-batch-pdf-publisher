using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Models
{
    internal enum LineVisionDirection
    {
        Horizontal,
        Vertical,
        Diagonal,
        Angled,
        Uncertain
    }

    internal sealed class LineVisionSegment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public LineVisionDirection Direction { get; set; }
        public double Confidence { get; set; }
        public bool IsEnabled { get; set; } = true;

        public double Length
        {
            get
            {
                var dx = X2 - X1;
                var dy = Y2 - Y1;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }
    }

    internal sealed class LineVisionCircle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Radius { get; set; }
        public double Confidence { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    internal sealed class LineVisionSettings
    {
        public int Threshold { get; set; }
        public int CloseGapPixels { get; set; } = 2;
        public int MinimumLineLengthPixels { get; set; } = 18;
        public int CollinearTolerancePixels { get; set; } = 3;
        public int MergeGapPixels { get; set; } = 5;
        public bool DetectDiagonals { get; set; } = true;
        public double OrthogonalToleranceDegrees { get; set; } = 2d;
        public double CadUnitsPerPixel { get; set; } = 1d;
    }

    internal sealed class LineVisionResult : IDisposable
    {
        public string SourcePath { get; set; }
        public Rectangle SourceRegion { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double SourcePixelsPerAnalysisPixel { get; set; } = 1d;
        public double SourcePreviewScale { get; set; } = 1d;
        public Bitmap SourcePreview { get; set; }
        public Bitmap BinaryPreview { get; set; }
        public List<LineVisionSegment> Segments { get; set; } = new List<LineVisionSegment>();
        public List<LineVisionCircle> Circles { get; set; } = new List<LineVisionCircle>();
        public List<LineVisionOcrTextRegion> TextRegions { get; set; } = new List<LineVisionOcrTextRegion>();
        public string OcrWarning { get; set; }

        public void Dispose()
        {
            if (SourcePreview != null) SourcePreview.Dispose();
            if (BinaryPreview != null) BinaryPreview.Dispose();
            SourcePreview = null;
            BinaryPreview = null;
        }
    }

    internal interface ILineVisionOcrEngine
    {
        string DisplayName { get; }
        bool IsAvailable { get; }
        Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken);
    }

    internal sealed class LineVisionOcrOptions
    {
        public string Language { get; set; } = "zh-Hans-CN";
        public double MinimumConfidence { get; set; } = 0.7d;
        public Rectangle? SourceRegion { get; set; }
        public int TimeoutSeconds { get; set; } = 90;
        public int MaskExpansionPixels { get; set; } = 2;
    }

    internal sealed class LineVisionOcrPageResult
    {
        public string Language { get; set; }
        public List<LineVisionOcrTextRegion> TextRegions { get; set; } = new List<LineVisionOcrTextRegion>();
    }

    internal sealed class LineVisionOcrTextRegion
    {
        public string Text { get; set; }
        public string OriginalText { get; set; }
        public PointF[] Polygon { get; set; }
        public double RotationDegrees { get; set; }
        public double Confidence { get; set; }
        public bool IsEnabled { get; set; } = true;

        public RectangleF Bounds
        {
            get
            {
                if (Polygon == null || Polygon.Length == 0) return RectangleF.Empty;
                var left = Polygon[0].X; var right = left; var top = Polygon[0].Y; var bottom = top;
                foreach (var point in Polygon) { left = Math.Min(left, point.X); right = Math.Max(right, point.X); top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y); }
                return RectangleF.FromLTRB(left, top, right, bottom);
            }
        }
    }

    internal sealed class LineVisionInsertResult
    {
        public int LineCount { get; set; }
        public int TextCount { get; set; }
        public int CircleCount { get; set; }
        public int TotalCount { get { return LineCount + CircleCount + TextCount; } }
    }

    internal sealed class UnavailableLineVisionOcrEngine : ILineVisionOcrEngine
    {
        public string DisplayName { get { return "本地 OCR Worker（尚未安装）"; } }
        public bool IsAvailable { get { return false; } }
        public Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken)
        {
            return Task.FromException<LineVisionOcrPageResult>(new InvalidOperationException("本地 OCR Worker 尚未安装。"));
        }
    }
}
