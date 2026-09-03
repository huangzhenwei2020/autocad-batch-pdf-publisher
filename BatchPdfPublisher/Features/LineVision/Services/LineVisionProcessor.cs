using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionProcessor
    {
        private const int MaximumAnalysisDimension = 2600;

        public static LineVisionResult Analyze(string path, Rectangle? requestedRegion, LineVisionSettings settings, Action<int, string> progress = null)
        {
            return Analyze(path, requestedRegion, settings, CancellationToken.None, progress);
        }

        public static LineVisionResult Analyze(string path, Rectangle? requestedRegion, LineVisionSettings settings, CancellationToken cancellationToken, Action<int, string> progress = null)
        {
            return Analyze(path, requestedRegion, settings, null, false, 0, cancellationToken, progress);
        }

        public static LineVisionResult Analyze(string path, Rectangle? requestedRegion, LineVisionSettings settings,
            IEnumerable<LineVisionOcrTextRegion> textRegions, bool maskText, int maskExpansionPixels,
            CancellationToken cancellationToken, Action<int, string> progress = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择图片文件。", "path");
            cancellationToken.ThrowIfCancellationRequested();
            settings = settings ?? new LineVisionSettings();
            Report(progress, 3, "正在读取图片……");
            using (var loaded = new Bitmap(path))
            {
                var region = NormalizeRegion(requestedRegion, loaded.Width, loaded.Height);
                using (var cropped = loaded.Clone(region, PixelFormat.Format24bppRgb))
                {
                    var ratio = Math.Max(1d, Math.Max(cropped.Width, cropped.Height) / (double)MaximumAnalysisDimension);
                    var width = Math.Max(1, (int)Math.Round(cropped.Width / ratio));
                    var height = Math.Max(1, (int)Math.Round(cropped.Height / ratio));
                    using (var source = Resize(cropped, width, height))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Report(progress, 15, "正在二值化和去噪……");
                        var dark = BuildBinary(source, settings.Threshold, out var binary);
                        try
                        {
                            var recognizedText = (textRegions ?? Enumerable.Empty<LineVisionOcrTextRegion>()).ToList();
                            if (maskText && recognizedText.Count > 0)
                            {
                                Report(progress, 25, "正在遮罩文字区域……");
                                ApplyTextMasks(dark, binary, width, height, ratio, recognizedText, maskExpansionPixels);
                            }
                            RemoveIsolatedPixels(dark, width, height);
                            Report(progress, 30, "正在识别闭合圆形……");
                            var circles = DetectCircles(dark, width, height, cancellationToken);
                            Report(progress, 33, "正在识别圆弧……");
                            var arcs = DetectArcs(dark, width, height, cancellationToken);
                            var candidates = new List<LineVisionSegment>();
                            if (settings.DetectDiagonals)
                            {
                                Report(progress, 35, "正在提取任意角度直线……");
                                candidates.AddRange(DetectAngledSegments((bool[])dark.Clone(), width, height, settings, cancellationToken));
                            }
                            Report(progress, 48, "正在补充水平线和垂直线……");
                            DetectRows(dark, width, height, settings, candidates, cancellationToken);
                            DetectColumns(dark, width, height, settings, candidates, cancellationToken);
                            candidates = RemoveDirectionalDuplicates(candidates, Math.Max(2d, settings.CollinearTolerancePixels + 1d));
                            cancellationToken.ThrowIfCancellationRequested();
                            Report(progress, 76, "正在聚类并合并共线段……");
                            var merged = MergeSegments(candidates, settings.CollinearTolerancePixels, settings.MergeGapPixels);
                            foreach (var segment in merged)
                            {
                                segment.X1 *= ratio; segment.Y1 *= ratio;
                                segment.X2 *= ratio; segment.Y2 *= ratio;
                            }
                            var polylines = settings.BuildPolylines ? BuildPolylines(merged, Math.Max(1d, settings.MergeGapPixels * ratio)) : new List<LineVisionPolyline>();
                            foreach (var circle in circles)
                            {
                                circle.CenterX *= ratio; circle.CenterY *= ratio; circle.Radius *= ratio;
                            }
                            foreach (var arc in arcs)
                            {
                                arc.CenterX *= ratio; arc.CenterY *= ratio; arc.Radius *= ratio;
                            }
                            Report(progress, 100, "识别完成");
                            var previewWidth = Math.Min(cropped.Width, 1800);
                            var previewHeight = Math.Max(1, (int)Math.Round(cropped.Height * previewWidth / (double)cropped.Width));
                            var result = new LineVisionResult
                            {
                                SourcePath = path,
                                SourceRegion = region,
                                Width = cropped.Width,
                                Height = cropped.Height,
                                SourcePixelsPerAnalysisPixel = ratio,
                                SourcePreviewScale = previewWidth / (double)cropped.Width,
                                SourcePreview = Resize(cropped, previewWidth, previewHeight),
                                BinaryPreview = binary,
                                Segments = merged,
                                Circles = circles,
                                Arcs = arcs,
                                Polylines = polylines,
                                TextRegions = recognizedText
                            };
                            binary = null;
                            return result;
                        }
                        finally { if (binary != null) binary.Dispose(); }
                    }
                }
            }
        }

        internal static List<LineVisionSegment> MergeSegments(IEnumerable<LineVisionSegment> source, double tolerance, double gap)
        {
            var result = new List<LineVisionSegment>();
            foreach (var direction in new[] { LineVisionDirection.Horizontal, LineVisionDirection.Vertical, LineVisionDirection.Diagonal })
            {
                var items = source.Where(x => x.Direction == direction && x.Length > 0.5).OrderBy(NormalCoordinate).ThenBy(AxisStart).ToList();
                foreach (var candidate in items)
                {
                    var match = result.FirstOrDefault(existing => existing.Direction == direction && SameOrientation(existing, candidate) &&
                        Math.Abs(NormalCoordinate(existing) - NormalCoordinate(candidate)) <= tolerance &&
                        IntervalsTouch(existing, candidate, gap));
                    if (match == null)
                    {
                        result.Add(Clone(candidate));
                        continue;
                    }
                    MergeInto(match, candidate);
                }
            }
            result.AddRange(source.Where(x => x.Direction == LineVisionDirection.Angled && x.Length > 0.5).Select(Clone));
            return result.Where(x => x.Length > 0.5).ToList();
        }

        internal static List<LineVisionCircle> DetectCircles(bool[] pixels, int width, int height, CancellationToken cancellationToken)
        {
            var visited = new bool[pixels.Length];
            var circles = new List<LineVisionCircle>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < pixels.Length; seed++)
            {
                if (!pixels[seed] || visited[seed]) continue;
                queue.Clear(); queue.Enqueue(seed); visited[seed] = true;
                var points = new List<Point>(); var minX = width; var minY = height; var maxX = -1; var maxY = -1;
                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = queue.Dequeue(); var x = index % width; var y = index / width;
                    points.Add(new Point(x, y)); minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        var next = ny * width + nx;
                        if (pixels[next] && !visited[next]) { visited[next] = true; queue.Enqueue(next); }
                    }
                }
                var boxWidth = maxX - minX + 1; var boxHeight = maxY - minY + 1;
                if (points.Count < 24 || boxWidth < 8 || boxHeight < 8 || boxWidth > 500 || boxHeight > 500) continue;
                var radius = (boxWidth + boxHeight) * 0.25d;
                var centerX = (minX + maxX) * 0.5d; var centerY = (minY + maxY) * 0.5d;
                if (Math.Abs(boxWidth - boxHeight) > Math.Max(3, radius * 0.22d)) continue;
                var bins = new bool[36]; var onRing = 0;
                foreach (var point in points)
                {
                    var dx = point.X - centerX; var dy = point.Y - centerY;
                    var distance = Math.Sqrt(dx * dx + dy * dy);
                    if (Math.Abs(distance - radius) <= Math.Max(2d, radius * 0.12d))
                    {
                        onRing++;
                        var angle = Math.Atan2(dy, dx) + Math.PI;
                        bins[Math.Min(35, (int)(angle / (Math.PI * 2d) * bins.Length))] = true;
                    }
                }
                var coverage = bins.Count(value => value) / (double)bins.Length;
                var expected = Math.PI * radius * 2d;
                var density = points.Count / Math.Max(1d, expected);
                if (coverage < 0.72d || density < 0.35d || density > 4.5d || onRing < 18) continue;
                circles.Add(new LineVisionCircle { CenterX = centerX, CenterY = centerY, Radius = radius, Confidence = Math.Min(1d, coverage * 0.7d + Math.Max(0d, 1d - Math.Abs(density - 1d) / 3d) * 0.3d) });
            }
            return circles;
        }

        internal static List<LineVisionPolyline> BuildPolylines(IList<LineVisionSegment> segments, double endpointTolerance)
        {
            if (segments == null || segments.Count < 2) return new List<LineVisionPolyline>();
            var result = new List<PolylineBuild>();
            var active = segments.Select((segment, index) => new ChainEdge { Segment = segment, Index = index }).ToList();
            var nodes = new List<ChainNode>();
            foreach (var edge in active)
            {
                edge.First = FindOrAddNode(nodes, new PointF((float)edge.Segment.X1, (float)edge.Segment.Y1), endpointTolerance);
                edge.Second = FindOrAddNode(nodes, new PointF((float)edge.Segment.X2, (float)edge.Segment.Y2), endpointTolerance);
                edge.First.Edges.Add(edge); edge.Second.Edges.Add(edge);
            }
            var used = new HashSet<int>();
            foreach (var start in nodes.Where(node => node.Edges.Count == 1)) TryBuildChain(start, used, result);
            foreach (var edge in active.Where(edge => !used.Contains(edge.Index) && edge.First.Edges.Count == 2 && edge.Second.Edges.Count == 2)) TryBuildChain(edge.First, used, result);
            foreach (var polyline in result)
            {
                foreach (var edge in polyline.SourceEdges) segments[edge].IsEnabled = false;
            }
            return result.Select(item => item.Value).ToList();
        }

        private static void TryBuildChain(ChainNode start, HashSet<int> used, IList<PolylineBuild> result)
        {
            var points = new List<PointF> { start.Point }; var edges = new List<ChainEdge>(); var current = start; var guard = 0;
            while (guard++ < 10000)
            {
                var nextEdge = current.Edges.FirstOrDefault(edge => !used.Contains(edge.Index));
                if (nextEdge == null) break;
                used.Add(nextEdge.Index); edges.Add(nextEdge);
                var next = ReferenceEquals(nextEdge.First, current) ? nextEdge.Second : nextEdge.First; points.Add(next.Point); current = next;
                if (ReferenceEquals(current, start) || current.Edges.Count != 2) break;
            }
            var closed = points.Count > 3 && ReferenceEquals(current, start);
            if (edges.Count < 2 || (!closed && points.Count < 3)) return;
            if (closed) points.RemoveAt(points.Count - 1);
            var confidence = edges.Average(edge => Math.Max(0d, Math.Min(1d, edge.Segment.Confidence)));
            result.Add(new PolylineBuild { SourceEdges = edges.Select(edge => edge.Index).ToList(), Value = new LineVisionPolyline { Points = points, IsClosed = closed, Confidence = confidence } });
        }

        private static ChainNode FindOrAddNode(IList<ChainNode> nodes, PointF point, double tolerance)
        {
            var match = nodes.FirstOrDefault(node => DistanceSquared(node.Point, point) <= tolerance * tolerance);
            if (match != null) return match;
            match = new ChainNode { Point = point }; nodes.Add(match); return match;
        }

        private static double DistanceSquared(PointF first, PointF second) { var dx = first.X - second.X; var dy = first.Y - second.Y; return dx * dx + dy * dy; }
        private sealed class ChainNode { public PointF Point; public List<ChainEdge> Edges = new List<ChainEdge>(); }
        private sealed class ChainEdge { public int Index; public LineVisionSegment Segment; public ChainNode First; public ChainNode Second; }
        private sealed class PolylineBuild { public LineVisionPolyline Value; public List<int> SourceEdges; }

        internal static List<LineVisionArc> DetectArcs(bool[] pixels, int width, int height, CancellationToken cancellationToken)
        {
            var visited = new bool[pixels.Length]; var result = new List<LineVisionArc>(); var queue = new Queue<int>();
            for (var seed = 0; seed < pixels.Length; seed++)
            {
                if (!pixels[seed] || visited[seed]) continue;
                queue.Clear(); queue.Enqueue(seed); visited[seed] = true;
                var points = new List<Point>();
                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = queue.Dequeue(); var x = index % width; var y = index / width; points.Add(new Point(x, y));
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx; var ny = y + dy; if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        var next = ny * width + nx; if (pixels[next] && !visited[next]) { visited[next] = true; queue.Enqueue(next); }
                    }
                }
                LineVisionArc arc;
                if (!TryFitArc(points, out arc)) continue;
                result.Add(arc);
                MarkArcPixels(pixels, width, height, arc, 2);
            }
            return result;
        }

        private static bool TryFitArc(IList<Point> points, out LineVisionArc arc)
        {
            arc = null; if (points == null || points.Count < 24 || points.Count > 5000) return false;
            Point first = points[0], second = points[0], third = points[0]; double farthest = -1d;
            foreach (var point in points) { var value = DistanceSquared(first, point); if (value > farthest) { farthest = value; second = point; } }
            farthest = -1d; foreach (var point in points) { var value = DistanceSquared(second, point); if (value > farthest) { farthest = value; first = point; } }
            var chordX = second.X - first.X; var chordY = second.Y - first.Y; var chordLength = Math.Sqrt(chordX * chordX + chordY * chordY);
            if (chordLength < 10d) return false;
            var maximumDistance = -1d;
            foreach (var point in points)
            {
                var distance = Math.Abs(chordY * point.X - chordX * point.Y + second.X * first.Y - second.Y * first.X) / chordLength;
                if (distance > maximumDistance) { maximumDistance = distance; third = point; }
            }
            if (maximumDistance < 3d || !TryCircleThrough(first, second, third, out var centerX, out var centerY, out var radius)) return false;
            if (radius < 6d || radius > 1000d) return false;
            var angles = new List<double>(); var error = 0d; var support = 0;
            foreach (var point in points)
            {
                var distance = Math.Sqrt((point.X - centerX) * (point.X - centerX) + (point.Y - centerY) * (point.Y - centerY));
                var delta = Math.Abs(distance - radius); error += delta;
                if (delta <= Math.Max(2d, radius * 0.08d)) { support++; angles.Add(Normalize360(Math.Atan2(point.Y - centerY, point.X - centerX) * 180d / Math.PI)); }
            }
            if (support < 20 || support / (double)points.Count < 0.68d || error / points.Count > Math.Max(2.5d, radius * 0.09d)) return false;
            angles.Sort(); var largestGap = -1d; var gapIndex = -1;
            for (var index = 0; index < angles.Count; index++)
            {
                var next = index == angles.Count - 1 ? angles[0] + 360d : angles[index + 1]; var gap = next - angles[index];
                if (gap > largestGap) { largestGap = gap; gapIndex = index; }
            }
            var sweep = 360d - largestGap; if (sweep < 25d || sweep > 330d) return false;
            var start = angles[(gapIndex + 1) % angles.Count];
            var bins = new bool[36]; foreach (var angle in angles) { var relative = Normalize360(angle - start); if (relative <= sweep + 3d) bins[Math.Min(35, (int)(relative / 10d))] = true; }
            var expectedBins = Math.Max(3, (int)Math.Ceiling(sweep / 10d)); var occupied = bins.Count(value => value);
            if (occupied < expectedBins * 0.62d) return false;
            arc = new LineVisionArc { CenterX = centerX, CenterY = centerY, Radius = radius, StartAngleDegrees = start, SweepAngleDegrees = sweep, Confidence = Math.Min(1d, support / (double)points.Count * 0.65d + occupied / (double)expectedBins * 0.35d) };
            return true;
        }

        private static bool TryCircleThrough(Point first, Point second, Point third, out double centerX, out double centerY, out double radius)
        {
            var denominator = 2d * (first.X * (second.Y - third.Y) + second.X * (third.Y - first.Y) + third.X * (first.Y - second.Y));
            if (Math.Abs(denominator) < 1e-6) { centerX = centerY = radius = 0d; return false; }
            var firstSquare = first.X * first.X + first.Y * first.Y; var secondSquare = second.X * second.X + second.Y * second.Y; var thirdSquare = third.X * third.X + third.Y * third.Y;
            centerX = (firstSquare * (second.Y - third.Y) + secondSquare * (third.Y - first.Y) + thirdSquare * (first.Y - second.Y)) / denominator;
            centerY = (firstSquare * (third.X - second.X) + secondSquare * (first.X - third.X) + thirdSquare * (second.X - first.X)) / denominator;
            radius = Math.Sqrt((first.X - centerX) * (first.X - centerX) + (first.Y - centerY) * (first.Y - centerY)); return true;
        }

        private static void MarkArcPixels(bool[] pixels, int width, int height, LineVisionArc arc, int radiusPixels)
        {
            var steps = Math.Max(12, (int)Math.Ceiling(arc.Radius * arc.SweepAngleDegrees * Math.PI / 180d));
            for (var step = 0; step <= steps; step++)
            {
                var angle = (arc.StartAngleDegrees + arc.SweepAngleDegrees * step / steps) * Math.PI / 180d;
                var x = (int)Math.Round(arc.CenterX + arc.Radius * Math.Cos(angle)); var y = (int)Math.Round(arc.CenterY + arc.Radius * Math.Sin(angle));
                for (var yy = -radiusPixels; yy <= radiusPixels; yy++) for (var xx = -radiusPixels; xx <= radiusPixels; xx++)
                { var nx = x + xx; var ny = y + yy; if (nx >= 0 && ny >= 0 && nx < width && ny < height) pixels[ny * width + nx] = false; }
            }
        }

        private static double DistanceSquared(Point first, Point second) { var dx = first.X - second.X; var dy = first.Y - second.Y; return dx * dx + dy * dy; }
        private static double Normalize360(double degrees) { degrees %= 360d; if (degrees < 0d) degrees += 360d; return degrees; }

        internal static List<LineVisionSegment> DetectAngledSegments(bool[] pixels, int width, int height, LineVisionSettings settings, CancellationToken cancellationToken)
        {
            var result = new List<LineVisionSegment>();
            var consumed = new bool[pixels.Length];
            var minimum = Math.Max(6, settings.MinimumLineLengthPixels);
            for (var index = 0; index < pixels.Length; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!pixels[index] || consumed[index]) continue;
                var x = index % width; var y = index / width;
                double angle;
                if (!TryEstimateDirection(pixels, width, height, x, y, Math.Max(8, Math.Min(20, minimum)), out angle)) continue;
                var cos = Math.Cos(angle); var sin = Math.Sin(angle);
                var backward = Support(pixels, width, height, x, y, -cos, -sin, settings.CloseGapPixels);
                var forward = Support(pixels, width, height, x, y, cos, sin, settings.CloseGapPixels);
                var length = backward.Distance + forward.Distance + 1;
                if (length < minimum || backward.Support + forward.Support < length * 0.68d) continue;
                var x1 = x - cos * backward.Distance; var y1 = y - sin * backward.Distance;
                var x2 = x + cos * forward.Distance; var y2 = y + sin * forward.Distance;
                var degrees = NormalizeAngle(Math.Atan2(y2 - y1, x2 - x1) * 180d / Math.PI);
                LineVisionDirection direction;
                if (DistanceToHorizontal(degrees) <= settings.OrthogonalToleranceDegrees)
                {
                    direction = LineVisionDirection.Horizontal; var average = (y1 + y2) * 0.5d; y1 = average; y2 = average;
                }
                else if (Math.Abs(degrees - 90d) <= settings.OrthogonalToleranceDegrees)
                {
                    direction = LineVisionDirection.Vertical; var average = (x1 + x2) * 0.5d; x1 = average; x2 = average;
                }
                else direction = Math.Abs(degrees - 45d) <= 1.5d || Math.Abs(degrees - 135d) <= 1.5d ? LineVisionDirection.Diagonal : LineVisionDirection.Angled;
                result.Add(New(x1, y1, x2, y2, direction, Math.Min(1d, (backward.Support + forward.Support) / Math.Max(1d, length))));
                MarkConsumed(consumed, pixels, width, height, x1, y1, x2, y2, 1);
            }
            return MergeSegments(result, Math.Max(1d, settings.CollinearTolerancePixels), Math.Max(0d, settings.MergeGapPixels));
        }

        internal static List<LineVisionSegment> RemoveDirectionalDuplicates(IList<LineVisionSegment> source, double tolerance)
        {
            var orthogonal = source.Where(item => item.Direction == LineVisionDirection.Horizontal || item.Direction == LineVisionDirection.Vertical).ToList();
            var angled = source.Where(item => item.Direction == LineVisionDirection.Angled || item.Direction == LineVisionDirection.Diagonal).ToList();
            var remove = new HashSet<Guid>();
            foreach (var line in angled)
            {
                var duplicate = orthogonal.Any(axis => axis.Length >= line.Length * 0.8d && SegmentNearAxis(line, axis, tolerance));
                if (duplicate) remove.Add(line.Id);
            }
            foreach (var axis in orthogonal)
            {
                var duplicate = angled.Any(line => !remove.Contains(line.Id) && line.Length >= axis.Length * 2d && SegmentNearLineMidpoint(axis, line, tolerance));
                if (duplicate) remove.Add(axis.Id);
            }
            return source.Where(item => !remove.Contains(item.Id)).ToList();
        }

        private static bool SegmentNearAxis(LineVisionSegment segment, LineVisionSegment axis, double tolerance)
        {
            if (axis.Direction == LineVisionDirection.Horizontal)
                return Math.Abs(segment.Y1 - axis.Y1) <= tolerance && Math.Abs(segment.Y2 - axis.Y1) <= tolerance && IntervalsOverlap(segment.X1, segment.X2, axis.X1, axis.X2, tolerance);
            return Math.Abs(segment.X1 - axis.X1) <= tolerance && Math.Abs(segment.X2 - axis.X1) <= tolerance && IntervalsOverlap(segment.Y1, segment.Y2, axis.Y1, axis.Y2, tolerance);
        }

        private static bool SegmentNearLineMidpoint(LineVisionSegment segment, LineVisionSegment line, double tolerance)
        {
            var x = (segment.X1 + segment.X2) * 0.5d; var y = (segment.Y1 + segment.Y2) * 0.5d;
            var dx = line.X2 - line.X1; var dy = line.Y2 - line.Y1; var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1e-8) return false;
            var t = ((x - line.X1) * dx + (y - line.Y1) * dy) / lengthSquared;
            if (t < -0.02d || t > 1.02d) return false;
            var px = line.X1 + t * dx; var py = line.Y1 + t * dy;
            return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) <= tolerance;
        }

        private static bool IntervalsOverlap(double a1, double a2, double b1, double b2, double tolerance)
        {
            return Math.Min(Math.Max(a1, a2), Math.Max(b1, b2)) + tolerance >= Math.Max(Math.Min(a1, a2), Math.Min(b1, b2));
        }

        private static bool TryEstimateDirection(bool[] pixels, int width, int height, int centerX, int centerY, int radius, out double angle)
        {
            var count = 0; double meanX = 0d; double meanY = 0d;
            for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
                for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
                    if (pixels[y * width + x]) { count++; meanX += x; meanY += y; }
            if (count < 5) { angle = 0d; return false; }
            meanX /= count; meanY /= count;
            double xx = 0d; double yy = 0d; double xy = 0d;
            for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
                for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
                    if (pixels[y * width + x]) { var dx = x - meanX; var dy = y - meanY; xx += dx * dx; yy += dy * dy; xy += dx * dy; }
            var spread = xx + yy; var anisotropy = Math.Sqrt((xx - yy) * (xx - yy) + 4d * xy * xy);
            if (spread < 1d || anisotropy / spread < 0.58d) { angle = 0d; return false; }
            angle = 0.5d * Math.Atan2(2d * xy, xx - yy);
            return true;
        }

        private static LineSupport Support(bool[] pixels, int width, int height, double startX, double startY, double dx, double dy, int allowedGap)
        {
            var support = 0; var lastSupport = 0; var maxDistance = Math.Sqrt(width * width + height * height);
            for (var step = 1; step <= maxDistance; step++)
            {
                var x = (int)Math.Round(startX + dx * step); var y = (int)Math.Round(startY + dy * step);
                if (x < 0 || y < 0 || x >= width || y >= height) break;
                var found = false;
                for (var yy = -1; yy <= 1 && !found; yy++) for (var xx = -1; xx <= 1; xx++)
                {
                    var nx = x + xx; var ny = y + yy;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height && pixels[ny * width + nx]) { found = true; break; }
                }
                if (found) { support++; lastSupport = step; }
                else if (step - lastSupport > Math.Max(1, allowedGap + 1)) break;
            }
            return new LineSupport { Distance = lastSupport, Support = support };
        }

        private static void MarkConsumed(bool[] consumed, bool[] pixels, int width, int height, double x1, double y1, double x2, double y2, int radius)
        {
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1))));
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps; var x = (int)Math.Round(x1 + (x2 - x1) * t); var y = (int)Math.Round(y1 + (y2 - y1) * t);
                for (var yy = -radius; yy <= radius; yy++) for (var xx = -radius; xx <= radius; xx++)
                {
                    var nx = x + xx; var ny = y + yy;
                    if (nx >= 0 && ny >= 0 && nx < width && ny < height) { var index = ny * width + nx; consumed[index] = true; pixels[index] = false; }
                }
            }
        }

        private static double NormalizeAngle(double degrees) { degrees %= 180d; if (degrees < 0d) degrees += 180d; return degrees; }
        private static double DistanceToHorizontal(double degrees) { return Math.Min(degrees, 180d - degrees); }

        private sealed class LineSupport { public int Distance; public int Support; }

        private static Rectangle NormalizeRegion(Rectangle? requested, int width, int height)
        {
            var bounds = new Rectangle(0, 0, width, height);
            if (!requested.HasValue || requested.Value.Width < 2 || requested.Value.Height < 2) return bounds;
            var region = Rectangle.Intersect(bounds, requested.Value);
            if (region.Width < 2 || region.Height < 2) throw new InvalidOperationException("框选范围不在图片内部。请重新框选。");
            return region;
        }

        private static Bitmap Resize(Bitmap source, int width, int height)
        {
            if (source.Width == width && source.Height == height) return new Bitmap(source);
            var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return result;
        }

        private static bool[] BuildBinary(Bitmap source, int requestedThreshold, out Bitmap preview)
        {
            var width = source.Width; var height = source.Height;
            var gray = new byte[width * height];
            var histogram = new int[256];
            var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
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
                            var value = (byte)Math.Max(0, Math.Min(255, (row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8));
                            gray[y * width + x] = value;
                            histogram[value]++;
                        }
                    }
                }
            }
            finally { source.UnlockBits(data); }
            var threshold = requestedThreshold > 0 ? Math.Max(1, Math.Min(254, requestedThreshold)) : Otsu(histogram, gray.Length);
            var dark = new bool[gray.Length];
            var darkCount = 0;
            for (var index = 0; index < gray.Length; index++) if (gray[index] <= threshold) { dark[index] = true; darkCount++; }
            if (darkCount > gray.Length * 0.65)
                for (var index = 0; index < dark.Length; index++) dark[index] = !dark[index];
            preview = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var output = preview.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    var start = (byte*)output.Scan0;
                    for (var y = 0; y < height; y++)
                    {
                        var row = start + y * output.Stride;
                        for (var x = 0; x < width; x++)
                        {
                            var value = dark[y * width + x] ? (byte)0 : (byte)255;
                            row[x * 3] = value; row[x * 3 + 1] = value; row[x * 3 + 2] = value;
                        }
                    }
                }
            }
            finally { preview.UnlockBits(output); }
            return dark;
        }

        private static int Otsu(int[] histogram, int count)
        {
            long total = 0;
            for (var i = 0; i < histogram.Length; i++) total += (long)i * histogram[i];
            long backgroundSum = 0; var background = 0; var best = 127; var maximum = -1d;
            for (var threshold = 0; threshold < 255; threshold++)
            {
                background += histogram[threshold];
                if (background == 0) continue;
                var foreground = count - background;
                if (foreground == 0) break;
                backgroundSum += (long)threshold * histogram[threshold];
                var meanBackground = backgroundSum / (double)background;
                var meanForeground = (total - backgroundSum) / (double)foreground;
                var variance = (double)background * foreground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
                if (variance > maximum) { maximum = variance; best = threshold; }
            }
            return Math.Max(25, Math.Min(230, best));
        }

        private static void RemoveIsolatedPixels(bool[] pixels, int width, int height)
        {
            var remove = new List<int>();
            for (var y = 1; y < height - 1; y++)
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    if (!pixels[index]) continue;
                    var neighbors = 0;
                    for (var yy = -1; yy <= 1; yy++) for (var xx = -1; xx <= 1; xx++) if ((xx != 0 || yy != 0) && pixels[index + yy * width + xx]) neighbors++;
                    if (neighbors == 0) remove.Add(index);
                }
            foreach (var index in remove) pixels[index] = false;
        }

        private static void ApplyTextMasks(bool[] pixels, Bitmap preview, int width, int height, double analysisRatio,
            IEnumerable<LineVisionOcrTextRegion> regions, int expansionPixels)
        {
            using (var graphics = Graphics.FromImage(preview))
            using (var brush = new SolidBrush(Color.White))
            {
                foreach (var region in regions.Where(value => value != null && value.IsEnabled))
                {
                    var bounds = region.Bounds;
                    if (bounds.Width <= 0f || bounds.Height <= 0f) continue;
                    var expansion = Math.Max(0, expansionPixels) / Math.Max(1d, analysisRatio);
                    var left = Math.Max(0, (int)Math.Floor(bounds.Left / analysisRatio - expansion));
                    var top = Math.Max(0, (int)Math.Floor(bounds.Top / analysisRatio - expansion));
                    var right = Math.Min(width, (int)Math.Ceiling(bounds.Right / analysisRatio + expansion));
                    var bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom / analysisRatio + expansion));
                    if (right <= left || bottom <= top) continue;
                    for (var y = top; y < bottom; y++) Array.Clear(pixels, y * width + left, right - left);
                    graphics.FillRectangle(brush, left, top, right - left, bottom - top);
                }
            }
        }

        private static void DetectRows(bool[] pixels, int width, int height, LineVisionSettings settings, IList<LineVisionSegment> target, CancellationToken cancellationToken)
        {
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = y;
                ScanLine(width, i => pixels[row * width + i], settings, (start, end, support) => target.Add(New(start, row, end, row, LineVisionDirection.Horizontal, support)));
            }
        }

        private static void DetectColumns(bool[] pixels, int width, int height, LineVisionSettings settings, IList<LineVisionSegment> target, CancellationToken cancellationToken)
        {
            for (var x = 0; x < width; x++)
            {
                if ((x & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var column = x;
                ScanLine(height, i => pixels[i * width + column], settings, (start, end, support) => target.Add(New(column, start, column, end, LineVisionDirection.Vertical, support)));
            }
        }

        private static void DetectDiagonals(bool[] pixels, int width, int height, LineVisionSettings settings, IList<LineVisionSegment> target, bool descending, CancellationToken cancellationToken)
        {
            var starts = new List<Point>();
            for (var x = 0; x < width; x++) starts.Add(new Point(x, descending ? 0 : height - 1));
            for (var y = 1; y < height; y++) starts.Add(new Point(0, descending ? y : height - 1 - y));
            for (var startIndex = 0; startIndex < starts.Count; startIndex++)
            {
                if ((startIndex & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var startPoint = starts[startIndex];
                var points = new List<Point>();
                var x = startPoint.X; var y = startPoint.Y;
                while (x < width && y >= 0 && y < height) { points.Add(new Point(x, y)); x++; y += descending ? 1 : -1; }
                ScanLine(points.Count, i => pixels[points[i].Y * width + points[i].X], settings, (start, end, support) =>
                {
                    var a = points[start]; var b = points[end];
                    target.Add(New(a.X, a.Y, b.X, b.Y, LineVisionDirection.Diagonal, support));
                });
            }
        }

        private static void ScanLine(int count, Func<int, bool> isDark, LineVisionSettings settings, Action<int, int, double> add)
        {
            var start = -1; var lastDark = -1; var support = 0;
            for (var i = 0; i <= count; i++)
            {
                var dark = i < count && isDark(i);
                if (dark)
                {
                    if (start < 0) start = i;
                    lastDark = i; support++;
                }
                if (start >= 0 && (i == count || i - lastDark > settings.CloseGapPixels))
                {
                    var end = lastDark;
                    if (end - start + 1 >= settings.MinimumLineLengthPixels)
                        add(start, end, support / (double)Math.Max(1, end - start + 1));
                    start = -1; lastDark = -1; support = 0;
                }
            }
        }

        private static LineVisionSegment New(double x1, double y1, double x2, double y2, LineVisionDirection direction, double support)
        {
            return new LineVisionSegment { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Direction = direction, Confidence = Math.Max(0d, Math.Min(1d, support)) };
        }

        private static LineVisionSegment Clone(LineVisionSegment source)
        {
            return new LineVisionSegment { X1 = source.X1, Y1 = source.Y1, X2 = source.X2, Y2 = source.Y2, Direction = source.Direction, Confidence = source.Confidence, IsEnabled = source.IsEnabled };
        }

        private static double NormalCoordinate(LineVisionSegment line)
        {
            if (line.Direction == LineVisionDirection.Horizontal) return (line.Y1 + line.Y2) * 0.5;
            if (line.Direction == LineVisionDirection.Vertical) return (line.X1 + line.X2) * 0.5;
            if (line.Direction == LineVisionDirection.Angled)
            {
                var dx = line.X2 - line.X1; var dy = line.Y2 - line.Y1;
                var length = Math.Max(1e-8, Math.Sqrt(dx * dx + dy * dy));
                return ((line.X1 * -dy + line.Y1 * dx) + (line.X2 * -dy + line.Y2 * dx)) * 0.5 / length;
            }
            return line.Y2 >= line.Y1
                ? ((line.Y1 - line.X1) + (line.Y2 - line.X2)) * 0.5
                : ((line.Y1 + line.X1) + (line.Y2 + line.X2)) * 0.5;
        }

        private static double AxisStart(LineVisionSegment line) { return line.Direction == LineVisionDirection.Vertical ? Math.Min(line.Y1, line.Y2) : Math.Min(line.X1, line.X2); }
        private static double AxisEnd(LineVisionSegment line) { return line.Direction == LineVisionDirection.Vertical ? Math.Max(line.Y1, line.Y2) : Math.Max(line.X1, line.X2); }
        private static bool IntervalsTouch(LineVisionSegment a, LineVisionSegment b, double gap) { return AxisStart(b) <= AxisEnd(a) + gap && AxisStart(a) <= AxisEnd(b) + gap; }
        private static bool SameOrientation(LineVisionSegment a, LineVisionSegment b)
        {
            if (a.Direction == LineVisionDirection.Angled)
            {
                var first = NormalizeAngle(Math.Atan2(a.Y2 - a.Y1, a.X2 - a.X1) * 180d / Math.PI);
                var second = NormalizeAngle(Math.Atan2(b.Y2 - b.Y1, b.X2 - b.X1) * 180d / Math.PI);
                return Math.Abs(first - second) <= 2d;
            }
            return a.Direction != LineVisionDirection.Diagonal || (a.Y2 >= a.Y1) == (b.Y2 >= b.Y1);
        }

        private static void MergeInto(LineVisionSegment target, LineVisionSegment source)
        {
            var start = Math.Min(AxisStart(target), AxisStart(source));
            var end = Math.Max(AxisEnd(target), AxisEnd(source));
            var normal = (NormalCoordinate(target) * target.Length + NormalCoordinate(source) * source.Length) / Math.Max(1d, target.Length + source.Length);
            if (target.Direction == LineVisionDirection.Horizontal) { target.X1 = start; target.X2 = end; target.Y1 = normal; target.Y2 = normal; }
            else if (target.Direction == LineVisionDirection.Vertical) { target.Y1 = start; target.Y2 = end; target.X1 = normal; target.X2 = normal; }
            else
            {
                var descending = target.Y2 >= target.Y1;
                target.X1 = start; target.X2 = end;
                target.Y1 = descending ? start + normal : -start + normal;
                target.Y2 = descending ? end + normal : -end + normal;
            }
            target.Confidence = Math.Max(target.Confidence, source.Confidence);
        }

        private static void Report(Action<int, string> progress, int percent, string message) { if (progress != null) progress(percent, message); }
    }
}
