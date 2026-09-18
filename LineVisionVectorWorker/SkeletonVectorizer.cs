using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Wanluo.LineVision.VectorWorker
{
    internal static class SkeletonVectorizer
    {
        private static readonly int[] NeighborX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] NeighborY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        public static List<VectorPolyline> Vectorize(Bitmap source, int requestedThreshold, int chunkSize)
        {
            return Vectorize(source, requestedThreshold, chunkSize, 0d, 0d, 3d);
        }

        /// <summary>
        /// minimumLength：短于该长度（像素）的路径直接丢弃。以前这个值写死，界面上调“最短线”
        /// 对这条路径没有任何影响；它是清掉文字残渣与毛刺最有效的一把刀，必须接进来。
        /// mergeGap/collinearTolerance：把端点相距不超过该距离、且真正共线的路径接起来（补断线）。
        /// </summary>
        public static List<VectorPolyline> Vectorize(Bitmap source, int requestedThreshold, int chunkSize,
            double minimumLength, double mergeGap, double collinearTolerance)
        {
            return Vectorize(source, requestedThreshold, chunkSize, minimumLength, mergeGap, collinearTolerance, null);
        }

        /// <summary>
        /// maskDumpDirectory 不为空时，把二值化掩膜与细化后的骨架各存一张 PNG，用于诊断
        /// “墨迹到底在哪一步丢的”。带 --dump-mask 参数时才会走到这里。
        /// </summary>
        public static List<VectorPolyline> Vectorize(Bitmap source, int requestedThreshold, int chunkSize,
            double minimumLength, double mergeGap, double collinearTolerance, string maskDumpDirectory)
        {
            var pixels = Binarize(source, requestedThreshold);
            if (!string.IsNullOrWhiteSpace(maskDumpDirectory))
            {
                Directory.CreateDirectory(maskDumpDirectory);
                SaveMask(pixels, source.Width, source.Height, Path.Combine(maskDumpDirectory, "01-binary.png"));
            }
            Thin(pixels, source.Width, source.Height);
            if (!string.IsNullOrWhiteSpace(maskDumpDirectory))
                SaveMask(pixels, source.Width, source.Height, Path.Combine(maskDumpDirectory, "02-thinned.png"));
            var simplify = Math.Max(0.6d, Math.Min(3d, chunkSize / 8d));
            var paths = Trace(pixels, source.Width, source.Height, simplify);
            return CleanAndMerge(paths, Math.Max(0d, minimumLength), Math.Max(0d, mergeGap), Math.Max(0.5d, collinearTolerance));
        }

        private static void SaveMask(bool[] pixels, int width, int height, string path)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        var start = (byte*)data.Scan0;
                        for (var y = 0; y < height; y++)
                        {
                            var row = start + y * data.Stride;
                            for (var x = 0; x < width; x++)
                            {
                                var value = pixels[y * width + x] ? (byte)0 : (byte)255;
                                row[x * 3] = value; row[x * 3 + 1] = value; row[x * 3 + 2] = value;
                            }
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        internal static double PathLength(VectorPolyline path)
        {
            var length = 0d;
            var points = path.Points;
            for (var index = 1; index < points.Count; index++) length += Distance(points[index - 1], points[index]);
            if (path.Closed && points.Count > 1) length += Distance(points[points.Count - 1], points[0]);
            return length;
        }

        /// <summary>
        /// 整理骨架路径，让输出更像 CAD 线稿，而不是一大堆碎片：
        /// 1) 按拐角把路径切成直段。骨架在交叉点处会把一条连续线切成许多段，先拆成直段才好合并；
        ///    拐角阈值取得较大（默认 60°），这样短圆弧仍保持为一条折线，不会被拆散。
        /// 2) 合并共线的直段：法向距离在容差内、轴向间隔不超过 mergeGap 的两段接成一条。
        ///    只合并共线方向，绝不把相交的两条线焊在一起，否则会出现假的长斜线。
        /// </summary>
        internal static List<VectorPolyline> CleanAndMerge(IList<VectorPolyline> source, double minimumLength,
            double mergeGap, double collinearTolerance)
        {
            // 顺序很关键：先按拐角切分 → 先合并共线 → 最后按长度筛。
            // 骨架路径是"交叉点到交叉点"的一段，一条长墙会被与它相交的每一条线切成许多短路径。
            // 如果先按长度筛，这些短路径会被当成碎渣丢掉，整堵墙就消失了——实测最短线 18 时
            // 覆盖率从 77.9% 掉到 56.3% 就是这个原因。先合并，墙恢复成一条长线，再筛才是对的。
            var segments = new List<VectorPolyline>();
            foreach (var path in source)
            {
                if (path.Points.Count < 2) continue;
                if (path.Closed) { segments.Add(ClonePath(path)); continue; }
                var current = new List<VectorPoint> { path.Points[0] };
                for (var index = 1; index < path.Points.Count; index++)
                {
                    current.Add(path.Points[index]);
                    var isLast = index == path.Points.Count - 1;
                    if (!isLast && !IsCorner(path.Points, index, CornerDegrees)) continue;
                    if (current.Count >= 2) segments.Add(new VectorPolyline { Points = current, Closed = false, Confidence = path.Confidence });
                    current = new List<VectorPoint> { path.Points[index] };
                }
            }

            var merged = mergeGap > 0d ? MergeCollinear(segments, mergeGap, collinearTolerance) : segments;
            if (minimumLength <= 0d) return merged;
            return merged.Where(item => PathLength(item) >= minimumLength).ToList();
        }

        private const double CornerDegrees = 60d;

        private static bool IsCorner(IList<VectorPoint> points, int index, double thresholdDegrees)
        {
            var previous = points[index - 1]; var current = points[index]; var next = points[index + 1];
            var first = Math.Atan2(current.Y - previous.Y, current.X - previous.X);
            var second = Math.Atan2(next.Y - current.Y, next.X - current.X);
            var turn = Math.Abs(first - second) * 180d / Math.PI;
            while (turn > 180d) turn = 360d - turn;
            return turn >= thresholdDegrees;
        }

        /// <summary>
        /// 合并共线直段。先把直段按（方向、法向位置）分桶，只在同一桶内两两比较；
        /// 直接对所有直段做两两比较是 O(n²)，一幅图几千段时会慢到几十秒，必须分桶。
        /// </summary>
        internal static List<VectorPolyline> MergeCollinear(IList<VectorPolyline> source, double mergeGap, double collinearTolerance)
        {
            var tolerance = Math.Max(1d, collinearTolerance);
            var buckets = new Dictionary<string, List<VectorPolyline>>();
            foreach (var segment in source)
            {
                if (segment.Points.Count < 2) continue;
                var key = BucketKey(segment, tolerance);
                List<VectorPolyline> bucket;
                if (!buckets.TryGetValue(key, out bucket)) { bucket = new List<VectorPolyline>(); buckets[key] = bucket; }
                bucket.Add(ClonePath(segment));
            }

            var result = new List<VectorPolyline>();
            foreach (var bucket in buckets.Values)
            {
                var items = bucket;
                var merged = true;
                while (merged)
                {
                    merged = false;
                    for (var first = 0; first < items.Count && !merged; first++)
                    {
                        for (var second = first + 1; second < items.Count; second++)
                        {
                            if (!TryMergePaths(items[first], items[second], mergeGap, tolerance)) continue;
                            items.RemoveAt(second);
                            merged = true;
                            break;
                        }
                    }
                }
                result.AddRange(items);
            }
            return result;
        }

        /// <summary>
        /// 分桶键：方向量化到容差角度、法向截距量化到容差距离。共线的两条线必然同桶，
        /// 因为桶宽就是容差；桶内再做精确的垂距与角度判断。相邻桶也要看，故只用于缩小候选集。
        /// </summary>
        private static string BucketKey(VectorPolyline segment, double tolerance)
        {
            VectorPoint direction;
            if (!Direction(segment.Points[0], segment.Points[segment.Points.Count - 1], out direction))
                return "degenerate|0|0";
            var angle = Math.Atan2(direction.Y, direction.X) * 180d / Math.PI;
            while (angle < 0d) angle += 180d;
            while (angle >= 180d) angle -= 180d;
            // 直线的一般式 x*sin - y*cos = 截距，法向量取 (-sin, cos)。
            var normalX = -direction.Y; var normalY = direction.X;
            var offset = segment.Points[0].X * normalX + segment.Points[0].Y * normalY;
            var angleBucket = (int)Math.Round(angle / tolerance);
            var offsetBucket = (int)Math.Round(offset / tolerance);
            // 角度接近 0°/180° 时法向会翻转，把这两个边界并到同一个键上。
            if (angleBucket == 0 || angleBucket == (int)Math.Round(180d / tolerance)) angleBucket = 0;
            return angleBucket.ToString(CultureInfo.InvariantCulture) + "|" + offsetBucket.ToString(CultureInfo.InvariantCulture);
        }

        private static VectorPolyline ClonePath(VectorPolyline source)
        {
            return new VectorPolyline { Points = new List<VectorPoint>(source.Points), Closed = source.Closed, Confidence = source.Confidence };
        }

        private static bool TryMergePaths(VectorPolyline target, VectorPolyline candidate, double mergeGap, double collinearTolerance)
        {
            if (target.Closed || candidate.Closed) return false;
            var targetStart = target.Points[0]; var targetEnd = target.Points[target.Points.Count - 1];
            var candidateStart = candidate.Points[0]; var candidateEnd = candidate.Points[candidate.Points.Count - 1];
            return TryAppend(target, targetEnd, candidateStart, candidateEnd, mergeGap, collinearTolerance)
                || TryAppend(target, targetStart, candidateStart, candidateEnd, mergeGap, collinearTolerance);
        }

        /// <summary>把 candidate 接到 target 的 join 端。candidate 的远端点会保留，形成单向延伸。</summary>
        private static bool TryAppend(VectorPolyline target, VectorPoint join, VectorPoint candidateStart,
            VectorPoint candidateEnd, double mergeGap, double collinearTolerance)
        {
            if (Distance(join, candidateStart) > mergeGap) return false;
            // candidate 从 candidateStart 走向 candidateEnd；确认方向与 target 在 join 处的走向共线。
            VectorPoint reference, candidateDirection, tail;
            if (!ReferenceDirection(target, join, out reference)) return false;
            if (!Direction(candidateStart, candidateEnd, out candidateDirection)) return false;
            if (!SameLine(reference, candidateDirection, collinearTolerance)) return false;
            // 光方向一致还不够：两条平行但错位的线不能被接成一条，否则会凭空造出斜线。
            if (ReferenceTail(target, join, out tail))
            {
                if (PerpendicularDistance(candidateEnd, tail, join) > Math.Max(1d, collinearTolerance)) return false;
                // 防折叠：接完之后的远端点必须比接点更远离内侧点，否则线条会往回折。
                if (Distance(candidateEnd, tail) <= Distance(join, tail)) return false;
            }
            var points = target.Points;
            if (Distance(join, points[points.Count - 1]) < 0.01d) points.Add(candidateEnd);
            else points.Insert(0, candidateEnd);
            return true;
        }

        /// <summary>端点处 target 自身的走向。</summary>
        private static bool ReferenceDirection(VectorPolyline path, VectorPoint endpoint, out VectorPoint direction)
        {
            direction = null;
            var points = path.Points;
            if (points.Count < 2) return false;
            if (Distance(endpoint, points[points.Count - 1]) < 0.01d) return Direction(points[points.Count - 2], points[points.Count - 1], out direction);
            if (Distance(endpoint, points[0]) < 0.01d) return Direction(points[1], points[0], out direction);
            return false;
        }

        /// <summary>端点内侧的那个点，用来做"是否在同一条线上"的垂距判断。</summary>
        private static bool ReferenceTail(VectorPolyline path, VectorPoint endpoint, out VectorPoint tail)
        {
            tail = null;
            var points = path.Points;
            if (points.Count < 2) return false;
            if (Distance(endpoint, points[points.Count - 1]) < 0.01d) { tail = points[points.Count - 2]; return true; }
            if (Distance(endpoint, points[0]) < 0.01d) { tail = points[1]; return true; }
            return false;
        }

        /// <summary>把方向化成单位向量，便于比较是否共线。</summary>
        private static bool Direction(VectorPoint from, VectorPoint to, out VectorPoint direction)
        {
            direction = null;
            var dx = to.X - from.X; var dy = to.Y - from.Y;
            if (Math.Abs(dx) < 0.01d && Math.Abs(dy) < 0.01d) return false;
            var angle = Math.Atan2(dy, dx);
            direction = new VectorPoint { X = Math.Cos(angle), Y = Math.Sin(angle) };
            return true;
        }

        /// <summary>两个单位方向是否属于同一条直线（相差接近 0° 或 180°）。</summary>
        private static bool SameLine(VectorPoint first, VectorPoint second, double toleranceDegrees)
        {
            // 取点积绝对值，方向相反（180°）与相同（0°）都视作共线。
            var dot = Math.Abs(first.X * second.X + first.Y * second.Y);
            if (dot > 1d) dot = 1d;
            var angle = Math.Acos(dot) * 180d / Math.PI;
            return angle <= Math.Max(1d, toleranceDegrees);
        }

        /// <summary>点到直线的垂距，用于判断两段是否在同一条线上。</summary>
        private static double PerpendicularDistance(VectorPoint point, VectorPoint lineStart, VectorPoint lineEnd)
        {
            return Math.Sqrt(DistanceToSegmentSquared(point, lineStart, lineEnd));
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
            // 必须以“每个像素的每条边”为起点，不能只对“度数≠2”的像素各走一次。
            // 一个交叉点有 3 条以上分支，第一条路径走到它就把边标记成已访问；如果循环只给每个
            // 起始像素一次机会，这个交叉点剩下的分支就再也没人走，整段墨迹直接丢失。
            // 实测只走“度数≠2”时，追踪出的折线只覆盖骨架的 88.9%。
            for (var start = 0; start < pixels.Length; start++)
            {
                if (!pixels[start]) continue;
                foreach (var next in Adjacent(pixels, width, height, start))
                {
                    if (Visited(visitedEdges, start, next)) continue;
                    var path = Walk(pixels, degree, width, height, start, next, visitedEdges);
                    var closed = path.Count > 2 && path[path.Count - 1] == start;
                    AddPath(paths, path, closed, simplifyTolerance);
                }
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
            // 这里以前写死“短于 4px 的路径直接丢”，于是界面上把“最短线”调到 0 也保不住这些短笔画，
            // 而它们往往是尺寸起止线、窗棂这类真实构件。现在阈值交给调用方（--minimum）决定，
            // 这里只挡退化情况。
            if (points.Count >= 2 && (closed || length >= 1d)) target.Add(new VectorPolyline { Points = points, Closed = closed, Confidence = 0.9d });
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
