using System;
using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingLeader
    {
        public DrawingLeader(IEnumerable<Point2D> vertices, string text, double textHeight)
        {
            Vertices = (vertices ?? Enumerable.Empty<Point2D>()).ToArray();
            if (Vertices.Count < 2) throw new ArgumentException("A leader requires at least two vertices.");
            Text = string.IsNullOrWhiteSpace(text) ? "说明" : text;
            TextHeight = Math.Max(1.0, textHeight);
        }

        public IReadOnlyList<Point2D> Vertices { get; }
        public string Text { get; }
        public double TextHeight { get; }
    }
}
