using System;
using System.Collections.Generic;

namespace CadArchSpec.CadTable
{
    /// <summary>从二值扫描图中提取足够长的水平、垂直表格线。</summary>
    public static class RasterTableGridExtractor
    {
        public static CadTableDetectionInput Extract(int width, int height, IReadOnlyList<bool> foreground)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (foreground == null || foreground.Count != width * height)
                throw new ArgumentException("二值图片像素数量与宽高不一致。", nameof(foreground));

            var result = new CadTableDetectionInput();
            foreach (var band in FindBands(height, i => MeasureRow(i, width, foreground), width))
            {
                var span = RowSpan(band, width, foreground);
                if (span.Item2 - span.Item1 < MinimumStroke(width)) continue;
                result.Segments.Add(new CadTableSegment
                {
                    Start = new CadTablePoint(span.Item1, height - band.Center),
                    End = new CadTablePoint(span.Item2, height - band.Center),
                    Layer = "扫描表格线"
                });
            }
            foreach (var band in FindBands(width, i => MeasureColumn(i, width, height, foreground), height))
            {
                var span = ColumnSpan(band, width, height, foreground);
                if (span.Item2 - span.Item1 < MinimumStroke(height)) continue;
                result.Segments.Add(new CadTableSegment
                {
                    Start = new CadTablePoint(band.Center, height - span.Item1),
                    End = new CadTablePoint(band.Center, height - span.Item2),
                    Layer = "扫描表格线"
                });
            }
            return result;
        }

        private static List<Band> FindBands(int count, Func<int, LineMeasure> measure, int crossLength)
        {
            var minimum = MinimumStroke(crossLength);
            var result = new List<Band>();
            for (var index = 0; index < count; index++)
            {
                var value = measure(index);
                if (value.LongestRun < minimum || value.ForegroundCount < minimum) continue;
                if (result.Count == 0 || index > result[result.Count - 1].End + 1)
                    result.Add(new Band(index));
                else
                    result[result.Count - 1].End = index;
            }
            return result;
        }

        private static int MinimumStroke(int length) => Math.Max(12, (int)Math.Ceiling(length * 0.12d));

        private static LineMeasure MeasureRow(int y, int width, IReadOnlyList<bool> pixels)
        {
            var offset = y * width;
            return Measure(width, x => pixels[offset + x]);
        }

        private static LineMeasure MeasureColumn(int x, int width, int height, IReadOnlyList<bool> pixels) =>
            Measure(height, y => pixels[y * width + x]);

        private static LineMeasure Measure(int count, Func<int, bool> isForeground)
        {
            var foreground = 0;
            var run = 0;
            var longest = 0;
            var gap = 0;
            for (var index = 0; index < count; index++)
            {
                if (isForeground(index))
                {
                    foreground++;
                    run += gap + 1;
                    gap = 0;
                    longest = Math.Max(longest, run);
                }
                else if (run > 0 && gap < 2) gap++;
                else { run = 0; gap = 0; }
            }
            return new LineMeasure(foreground, longest);
        }

        private static Tuple<double, double> RowSpan(Band band, int width, IReadOnlyList<bool> pixels)
        {
            var occupied = new bool[width];
            for (var y = band.Start; y <= band.End; y++)
                for (var x = 0; x < width; x++) occupied[x] |= pixels[y * width + x];
            return Extent(occupied);
        }

        private static Tuple<double, double> ColumnSpan(Band band, int width, int height, IReadOnlyList<bool> pixels)
        {
            var occupied = new bool[height];
            for (var x = band.Start; x <= band.End; x++)
                for (var y = 0; y < height; y++) occupied[y] |= pixels[y * width + x];
            return Extent(occupied);
        }

        private static Tuple<double, double> Extent(IReadOnlyList<bool> occupied)
        {
            var first = -1;
            var last = -1;
            for (var index = 0; index < occupied.Count; index++)
            {
                if (!occupied[index]) continue;
                if (first < 0) first = index;
                last = index;
            }
            return Tuple.Create((double)Math.Max(0, first), (double)Math.Max(0, last));
        }

        private sealed class Band
        {
            public Band(int start) { Start = start; End = start; }
            public int Start { get; }
            public int End { get; set; }
            public double Center => (Start + End) * 0.5d;
        }

        private struct LineMeasure
        {
            public LineMeasure(int count, int longest) { ForegroundCount = count; LongestRun = longest; }
            public int ForegroundCount { get; }
            public int LongestRun { get; }
        }
    }
}
