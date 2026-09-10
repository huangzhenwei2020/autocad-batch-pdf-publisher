using System;
using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    public sealed class DrawingTable
    {
        public DrawingTable(
            Point2D position,
            double rowHeight,
            IEnumerable<double> columnWidths,
            IEnumerable<IEnumerable<string>> rows)
        {
            Position = position;
            RowHeight = Math.Max(1.0, rowHeight);
            ColumnWidths = (columnWidths ?? Enumerable.Empty<double>()).ToArray();
            Rows = (rows ?? Enumerable.Empty<IEnumerable<string>>())
                .Select(row => (IReadOnlyList<string>)(row ?? Enumerable.Empty<string>()).ToArray())
                .ToArray();
            if (ColumnWidths.Count == 0 || Rows.Count == 0)
                throw new ArgumentException("A drawing table must contain columns and rows.");
        }

        public Point2D Position { get; }
        public double RowHeight { get; }
        public IReadOnlyList<double> ColumnWidths { get; }
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
    }
}
