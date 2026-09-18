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

    internal enum LineVisionVectorMode { Legacy, Centerline, Outline, Hybrid }

    /// <summary>墙体边框内部的填充做法。None 表示只画边框线、不填充。</summary>
    internal enum LineVisionWallFillMode { None, Solid, Pattern }

    internal enum LineVisionOcrMode { Automatic, Paddle, Windows }

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

    internal sealed class LineVisionArc
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Radius { get; set; }
        public double StartAngleDegrees { get; set; }
        public double SweepAngleDegrees { get; set; }
        public double Confidence { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    internal sealed class LineVisionPolyline
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public List<PointF> Points { get; set; } = new List<PointF>();
        public bool IsClosed { get; set; }
        public double Confidence { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string Source { get; set; }
    }

    internal sealed class LineVisionWallRegion
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public List<PointF> Outer { get; set; } = new List<PointF>();
        public List<List<PointF>> Holes { get; set; } = new List<List<PointF>>();
        public double AverageThickness { get; set; }
        public double Confidence { get; set; }
        /// <summary>是否把这块墙的边框线画出来。这是真正的墙体识别结果，默认启用。</summary>
        public bool IsEnabled { get; set; }
        /// <summary>
        /// 是否在边框内填充。与边框线解耦：用户完全可以只要边框、不要填充。
        /// 以前只有一个 IsEnabled 同时管着"画不画"和"填不填"，导致要么两者都没有、要么只能全填。
        /// </summary>
        public LineVisionWallFillMode FillMode { get; set; } = LineVisionWallFillMode.Solid;
        /// <summary>图案填充的图案名（FillMode 为 Pattern 时使用）。</summary>
        public string HatchPatternName { get; set; } = "ANSI31";
        /// <summary>图案填充比例（CAD 单位）。</summary>
        public double HatchPatternScale { get; set; } = 1d;
    }

    internal sealed class LineVisionSettings
    {
        public int Threshold { get; set; }
        public int CloseGapPixels { get; set; } = 2;
        // 实测（1473x976 立面图 / 1448x1086 平面图）：短于 18px 的水平墨迹段分别占 54.5% 与 96.6%。
        // 原来定在 18，等于把一半以上的图当噪声丢掉，是「很多地方没识别到」的主因。
        // 5 是实测曲线的拐点：碎渣（<6px 折线）从 3,677 降到 505，而墨迹总长几乎不降。
        public int MinimumLineLengthPixels { get; set; } = 5;
        public int CollinearTolerancePixels { get; set; } = 3;
        public int MergeGapPixels { get; set; } = 5;
        public bool DetectDiagonals { get; set; } = true;
        public double OrthogonalToleranceDegrees { get; set; } = 2d;
        public bool BuildPolylines { get; set; } = true;
        public LineVisionVectorMode VectorMode { get; set; } = LineVisionVectorMode.Centerline;
        public bool DetectWallFills { get; set; } = true;
        public double MinimumWallThicknessPixels { get; set; } = 3d;
        public double MaximumWallThicknessPixels { get; set; } = 80d;
        /// <summary>
        /// 墙体边框内的填充做法。边框线与该选项无关——边框是识别结果，总是画出来。
        /// None 只画边框、不填充；Solid 实心；Pattern 用 HatchPatternName 指定的图案。
        /// </summary>
        public LineVisionWallFillMode WallFillMode { get; set; } = LineVisionWallFillMode.None;
        public string WallHatchPatternName { get; set; } = "ANSI31";
        public double WallHatchPatternScale { get; set; } = 1d;
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
        public List<LineVisionArc> Arcs { get; set; } = new List<LineVisionArc>();
        public List<LineVisionPolyline> Polylines { get; set; } = new List<LineVisionPolyline>();
        public List<LineVisionWallRegion> WallRegions { get; set; } = new List<LineVisionWallRegion>();
        public List<LineVisionOcrTextRegion> TextRegions { get; set; } = new List<LineVisionOcrTextRegion>();
        public string OcrWarning { get; set; }
        public string OcrEngineId { get; set; }
        public string OcrEngineVersion { get; set; }
        public string VectorWarning { get; set; }

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
        string EngineId { get; }
        string DisplayName { get; }
        bool IsAvailable { get; }
        LineVisionOcrEngineCapabilities Capabilities { get; }
        Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken);
    }

    internal sealed class LineVisionOcrOptions
    {
        public string Language { get; set; } = "zh-Hans-CN";
        public double MinimumConfidence { get; set; } = 0.7d;
        public Rectangle? SourceRegion { get; set; }
        public int TimeoutSeconds { get; set; } = 90;
        public int MaskExpansionPixels { get; set; } = 2;
        public IProgress<LineVisionOcrWorkerProgress> Progress { get; set; }
    }

    internal sealed class LineVisionOcrPageResult
    {
        public int ProtocolVersion { get; set; }
        public string EngineId { get; set; }
        public string EngineVersion { get; set; }
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
        public int ArcCount { get; set; }
        public int PolylineCount { get; set; }
        public int WallFillCount { get; set; }
        /// <summary>画出的墙体边框线条数。与填充数分开统计，因为"只要边框不要填充"是常见选择。</summary>
        public int WallBoundaryCount { get; set; }
        public int TotalCount { get { return LineCount + CircleCount + ArcCount + PolylineCount + WallBoundaryCount + WallFillCount + TextCount; } }
    }

    internal sealed class UnavailableLineVisionOcrEngine : ILineVisionOcrEngine
    {
        public string EngineId { get { return "unavailable"; } }
        public string DisplayName { get { return "本地 OCR Worker（尚未安装）"; } }
        public bool IsAvailable { get { return false; } }
        public LineVisionOcrEngineCapabilities Capabilities { get { return new LineVisionOcrEngineCapabilities { EngineId = EngineId, DisplayName = DisplayName, ProtocolVersion = LineVisionOcrProtocol.CurrentVersion }; } }
        public Task<LineVisionOcrPageResult> RecognizeAsync(string imagePath, LineVisionOcrOptions options, CancellationToken cancellationToken)
        {
            return Task.FromException<LineVisionOcrPageResult>(new InvalidOperationException("本地 OCR Worker 尚未安装。"));
        }
    }
}
