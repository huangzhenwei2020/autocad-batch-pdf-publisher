using System;
using System.Collections.Generic;

namespace CadArchSpec.CadTable
{
    public enum CadTextSourceKind
    {
        Standard,
        TianzhengProperty,
        ExplodedClone,
        OcrFallback,
        Unresolved
    }

    public sealed class CadTablePoint
    {
        public CadTablePoint()
        {
        }

        public CadTablePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class CadTableSegment
    {
        public CadTablePoint Start { get; set; } = new CadTablePoint();
        public CadTablePoint End { get; set; } = new CadTablePoint();
        public string SourceHandle { get; set; } = string.Empty;
        public string Layer { get; set; } = string.Empty;
    }

    public sealed class CadTextFragment
    {
        public string Text { get; set; } = string.Empty;
        public string PlainText { get; set; } = string.Empty;
        public CadTablePoint Center { get; set; } = new CadTablePoint();
        public double Width { get; set; }
        public double Height { get; set; }
        public double RotationDegrees { get; set; }
        public double Confidence { get; set; } = 1d;
        public CadTextSourceKind SourceKind { get; set; }
        public string SourceHandle { get; set; } = string.Empty;
        public string SourceDxfName { get; set; } = string.Empty;
    }

    public sealed class CadTableDetectionInput
    {
        public List<CadTableSegment> Segments { get; set; } = new List<CadTableSegment>();
        public List<CadTextFragment> TextFragments { get; set; } = new List<CadTextFragment>();
    }

    public sealed class CadTableEntityReadResult
    {
        public CadTableDetectionInput Input { get; set; } = new CadTableDetectionInput();
        public List<string> Warnings { get; set; } = new List<string>();
        public int SourceEntityCount { get; set; }
        public int ExplodedObjectCount { get; set; }
    }

    public sealed class CadTableDetectionOptions
    {
        public double CoordinateTolerance { get; set; } = 1d;
        public double MaximumBorderGap { get; set; } = 2d;
        public double OrthogonalAngleToleranceDegrees { get; set; } = 1d;
    }

    public sealed class DetectedCadTableCell
    {
        public int RowIndex { get; set; }
        public int ColumnIndex { get; set; }
        public int RowSpan { get; set; } = 1;
        public int ColumnSpan { get; set; } = 1;
        public double Left { get; set; }
        public double Bottom { get; set; }
        public double Right { get; set; }
        public double Top { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<CadTextFragment> TextFragments { get; set; } = new List<CadTextFragment>();
    }

    public sealed class CadTableDetectionResult
    {
        public List<double> ColumnBoundaries { get; set; } = new List<double>();
        public List<double> RowBoundaries { get; set; } = new List<double>();
        public List<DetectedCadTableCell> Cells { get; set; } = new List<DetectedCadTableCell>();
        public List<CadTextFragment> UnassignedText { get; set; } = new List<CadTextFragment>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
