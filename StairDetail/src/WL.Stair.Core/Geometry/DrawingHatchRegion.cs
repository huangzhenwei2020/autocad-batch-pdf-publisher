using System;
using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    /// <summary>
    /// A closed section region.  The CAD renderer owns the CAD-specific
    /// polyline offset and hatch creation, while the core keeps a portable
    /// description that can also be shown in the WebView preview.
    /// </summary>
    public sealed class DrawingHatchRegion
    {
        public DrawingHatchRegion(
            IEnumerable<Point2D> boundary,
            string componentId,
            bool isWall,
            string patternName,
            double patternScale,
            bool bold = true,
            IEnumerable<DrawingLine> openEdges = null)
        {
            var points = (boundary ?? Enumerable.Empty<Point2D>()).ToArray();
            if (points.Length < 3) throw new ArgumentException("A hatch region needs at least three vertices.");
            Boundary = points;
            ComponentId = componentId ?? string.Empty;
            IsWall = isWall;
            PatternName = string.IsNullOrWhiteSpace(patternName) ? "ANSI31" : patternName.Trim();
            PatternScale = Math.Max(0.001, patternScale);
            Bold = bold;
            OpenEdges = (openEdges ?? Enumerable.Empty<DrawingLine>()).ToArray();
        }

        public IReadOnlyList<Point2D> Boundary { get; }
        public string ComponentId { get; }
        public bool IsWall { get; }
        public string PatternName { get; }
        public double PatternScale { get; }
        public bool Bold { get; }
        public IReadOnlyList<DrawingLine> OpenEdges { get; }
    }
}
