using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace Wanluo.LineVision.VectorWorker
{
    internal static class SkeletonVectorizer
    {
        private static readonly int[] NeighborX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] NeighborY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        public static List<VectorPolyline> Vectorize(Bitmap source, int requestedThreshold, int chunkSize)
        {
            var pixels = Binarize(source, requestedThreshold); Thin(pixels, source.Width, source.Height);
            return Trace(pixels, source.Width, source.Height, Math.Max(0.6d, Math.Min(3d, chunkSize / 8d)));
        }

        private static bool[] Binarize(Bitmap source, int requestedThreshold)
        {
            var width = source.Width; var height = source.Height; var gray = new byte[width * height]; var histogram = new int[256];
            using (var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImage(source, 0, 0, width, height);
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        var start = (byte*)data.Scan0;
                        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
                        {
                            var row = start + y * data.Stride; var value = (byte)((row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8);
                            gray[y * width + x] = value; histogram[value]++;
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
            }
            var threshold = requestedThreshold > 0 ? Math.Max(1, Math.Min(254, requestedThreshold)) : Otsu(histogram, gray.Length);
            var result = new bool[gray.Length]; var count = 0;
            for (var index = 0; index < gray.Length; index++) if (gray[index] <= threshold) { result[index] = true; count++; }
            if (count > gray.Length * 0.55d) for (var index = 0; index < result.Length; index++) result[index] = !result[index];
            return result;
        }

        internal static void Thin(bool[] pixels, int width, int height)
        {
            var changed = true; var remove = new List<int>();
            while (changed)
            {
                changed = ThinPass(pixels, width, height, false, remove);
                if (ThinPass(pixels, width, height, true, remove)) changed = true;
            }
        }

        private static bool ThinPass(bool[] pixels, int width, int height, bool second, List<int> remove)
        {
            remove.Clear();
            for (var y = 1; y < height - 1; y++) for (var x = 1; x < width - 1; x++)
            {
                var index = y * width + x; if (!pixels[index]) continue;
                var neighbors = Neighbors(pixels, width, x, y); var count = neighbors.Count(value => value);
                if (count < 2 || count > 6 || Transitions(neighbors) != 1) continue;
                var first = second ? neighbors[0] && neighbors[2] && neighbors[6] : neighbors[0] && neighbors[2] && neighbors[4];
                var secondRule = second ? neighbors[0] && neighbors[4] && neighbors[6] : neighbors[2] && neighbors[4] && neighbors[6];
                if (!first && !secondRule) remove.Add(index);
            }
            foreach (var index in remove) pixels[index] = false; return remove.Count > 0;
        }

        private static List<VectorPolyline> Trace(bool[] pixels, int width, int height, double simplifyTolerance)
        {
            var degree = new byte[pixels.Length];
            for (var index = 0; index < pixels.Length; index++) if (pixels[index]) degree[index] = (byte)Adjacent(pixels, width, height, index).Count;
            var visitedEdges = new HashSet<long>(); var paths = new List<VectorPolyline>();
            foreach (var start in Enumerable.Range(0, pixels.Length).Where(index => pixels[index] && degree[index] != 2))
                foreach (var next in Adjacent(pixels, width, height, start)) if (!Visited(visitedEdges, start, next)) AddPath(paths, Walk(pixels, degree, width, height, start, next, visitedEdges), false, simplifyTolerance);
            foreach (var start in Enumerable.Range(0, pixels.Length).Where(index => pixels[index] && degree[index] == 2))
            {
                var next = Adjacent(pixels, width, height, start).First(); if (Visited(visitedEdges, start, next)) continue;
                var path = Walk(pixels, degree, width, height, start, next, visitedEdges); var closed = path.Count > 2 && path[path.Count - 1] == start;
                AddPath(paths, path, closed, simplifyTolerance);
            }
            return paths.Where(path => path.Points.Count >= 2).ToList();
        }

        private static List<int> Walk(bool[] pixels, byte[] degree, int width, int height, int start, int next, HashSet<long> visited)
        {
            var result = new List<int> { start }; var previous = start; var current = next; var guard = 0;
            while (guard++ < pixels.Length)
            {
                Mark(visited, previous, current); result.Add(current);
                if (current == start || degree[current] != 2) break;
                var following = Adjacent(pixels, width, height, current).Where(index => index != previous && !Visited(visited, current, index)).DefaultIfEmpty(-1).First();
                if (following < 0) break;
                previous = current; current = following;
            }
            return result;
        }

        private static void AddPath(IList<VectorPolyline> target, IList<int> path, bool closed, double tolerance)
        {
            if (path.Count < 2) return;
            var width = _traceWidth; // Set by Adjacent before this method is called.
            var points = path.Select(index => new VectorPoint(index % width, index / width)).ToList();
            if (closed && points.Count > 1 && Same(points[0], points[points.Count - 1])) points.RemoveAt(points.Count - 1);
            points = Simplify(points, tolerance, closed);
            var length = 0d; for (var index = 1; index < points.Count; index++) length += Distance(points[index - 1], points[index]);
            if (points.Count >= 2 && (closed || length >= 4d)) target.Add(new VectorPolyline { Points = points, Closed = closed, Confidence = 0.9d });
        }

        [ThreadStatic] private static int _traceWidth;
        private static List<int> Adjacent(bool[] pixels, int width, int height, int index)
        {
            _traceWidth = width; var x = index % width; var y = index / width; var result = new List<int>();
            for (var direction = 0; direction < 8; direction++)
            {
                var nx = x + NeighborX[direction]; var ny = y + NeighborY[direction]; if (nx < 0 || ny < 0 || nx >= width || ny >= height || !pixels[ny * width + nx]) continue;
                if ((direction & 1) == 1)
                {
                    var horizontal = y * width + nx; var vertical = ny * width + x;
                    if (pixels[horizontal] || pixels[vertical]) continue;
                }
                result.Add(ny * width + nx);
            }
            return result;
        }

        private static List<VectorPoint> Simplify(List<VectorPoint> points, double tolerance, bool closed)
        {
            if (points.Count < 3) return points;
            if (closed) return points.Where((point, index) => index == 0 || index == points.Count - 1 || Distance(points[index - 1], point) >= tolerance).ToList();
            var keep = new bool[points.Count]; keep[0] = keep[points.Count - 1] = true; SimplifyRange(points, 0, points.Count - 1, tolerance * tolerance, keep);
            return points.Where((point, index) => keep[index]).ToList();
        }

        private static void SimplifyRange(IList<VectorPoint> points, int first, int last, double toleranceSquared, bool[] keep)
        {
            if (last <= first + 1) return; var best = 0d; var bestIndex = -1;
            for (var index = first + 1; index < last; index++) { var distance = DistanceToSegmentSquared(points[index], points[first], points[last]); if (distance > best) { best = distance; bestIndex = index; } }
            if (bestIndex < 0 || best <= toleranceSquared) return; keep[bestIndex] = true; SimplifyRange(points, first, bestIndex, toleranceSquared, keep); SimplifyRange(points, bestIndex, last, toleranceSquared, keep);
        }

        private static double DistanceToSegmentSquared(VectorPoint point, VectorPoint first, VectorPoint second)
        {
            var dx = second.X - first.X; var dy = second.Y - first.Y; var length = dx * dx + dy * dy;
            if (length < 1e-8) return DistanceSquared(point, first); var t = Math.Max(0d, Math.Min(1d, ((point.X - first.X) * dx + (point.Y - first.Y) * dy) / length));
            return DistanceSquared(point, new VectorPoint(first.X + t * dx, first.Y + t * dy));
        }

        private static bool[] Neighbors(bool[] pixels, int width, int x, int y) { var result = new bool[8]; for (var i = 0; i < 8; i++) result[i] = pixels[(y + NeighborY[i]) * width + x + NeighborX[i]]; return result; }
        private static int Transitions(bool[] values) { var result = 0; for (var index = 0; index < values.Length; index++) if (!values[index] && values[(index + 1) % values.Length]) result++; return result; }
        private static bool Visited(HashSet<long> visited, int first, int second) { return visited.Contains(Key(first, second)); }
        private static void Mark(HashSet<long> visited, int first, int second) { visited.Add(Key(first, second)); }
        private static long Key(int first, int second) { if (first > second) { var value = first; first = second; second = value; } return ((long)first << 32) | (uint)second; }
        private static bool Same(VectorPoint first, VectorPoint second) { return Math.Abs(first.X - second.X) < 0.01d && Math.Abs(first.Y - second.Y) < 0.01d; }
        private static double Distance(VectorPoint first, VectorPoint second) { return Math.Sqrt(DistanceSquared(first, second)); }
        private static double DistanceSquared(VectorPoint first, VectorPoint second) { var dx = first.X - second.X; var dy = first.Y - second.Y; return dx * dx + dy * dy; }
        private static int Otsu(int[] histogram, int count) { long total = 0; for (var i = 0; i < 256; i++) total += (long)i * histogram[i]; long sum = 0; var background = 0; var best = 127; var maximum = -1d; for (var threshold = 0; threshold < 255; threshold++) { background += histogram[threshold]; if (background == 0) continue; var foreground = count - background; if (foreground == 0) break; sum += (long)threshold * histogram[threshold]; var a = sum / (double)background; var b = (total - sum) / (double)foreground; var score = (double)background * foreground * (a - b) * (a - b); if (score > maximum) { maximum = score; best = threshold; } } return Math.Max(20, Math.Min(235, best)); }
    }
}
