using System;
using System.Collections.Generic;
using System.Linq;

namespace Wanluo.LineVision.VectorWorker
{
    internal static class WallRegionDetector
    {
        public static List<VectorWallRegion> Detect(IEnumerable<VectorPolyline> source, double minimumThickness, double maximumThickness)
        {
            var polygons = (source ?? new List<VectorPolyline>()).Where(item => item.Closed && item.Points != null && item.Points.Count >= 3)
                .Select(item => new PolygonInfo { Points = item.Points, Area = Math.Abs(SignedArea(item.Points)), Perimeter = Perimeter(item.Points) })
                .Where(item => item.Area >= 80d && item.Perimeter >= 20d).OrderByDescending(item => item.Area).ToList();
            foreach (var polygon in polygons)
                polygon.Parent = polygons.Where(candidate => candidate.Area > polygon.Area && Contains(candidate.Points, Centroid(polygon.Points))).OrderBy(candidate => candidate.Area).FirstOrDefault();
            var result = new List<VectorWallRegion>();
            foreach (var outer in polygons.Where(item => Depth(item) % 2 == 0))
            {
                var holes = polygons.Where(item => ReferenceEquals(item.Parent, outer)).ToList();
                var netArea = outer.Area - holes.Sum(item => item.Area); var boundary = outer.Perimeter + holes.Sum(item => item.Perimeter);
                if (netArea <= 0d || boundary <= 0d) continue;

                // 用外接矩形来量"多厚"和"多长"。原来用 2*面积/周长，这个式子分不清薄墙和小方块：
                // 一块 5x20px 的家具算出来也是"厚 8px"，于是照样被当成墙。墙的特征是又长又窄，
                // 所以必须同时看厚度和长宽比，否则实心填充会把家具、洁具涂成一坨坨方块。
                var thickness = 2d * netArea / boundary;
                var box = MinimumAreaBox(outer.Points);
                var shortSide = Math.Min(box.Width, box.Height);
                var longSide = Math.Max(box.Width, box.Height);
                if (shortSide > 0d) thickness = 0.5d * (thickness + shortSide);
                if (thickness < minimumThickness || thickness > maximumThickness) continue;
                var elongation = longSide / Math.Max(1e-6d, shortSide);
                if (elongation < MinimumElongation) continue;

                var complexity = Math.Min(1d, (outer.Points.Count + holes.Sum(item => item.Points.Count)) / 20d);
                var confidence = Math.Min(1d, 0.55d + complexity * 0.2d + (holes.Count > 0 ? 0.2d : 0d));
                result.Add(new VectorWallRegion { Outer = outer.Points, Holes = holes.Select(item => item.Points).ToList(), AverageThickness = thickness, Confidence = confidence });
            }
            return result;
        }

        /// <summary>墙至少要这么长条（长边/短边）。低于该值视为方块，多半是家具、洁具或房间轮廓。</summary>
        private const double MinimumElongation = 3d;

        /// <summary>
        /// 最小面积外接矩形（旋转卡壳）。用它的短边作为墙厚、长短边之比作为长条程度，
        /// 比 2*面积/周长 可靠得多。
        /// </summary>
        internal static Box MinimumAreaBox(IList<VectorPoint> points)
        {
            var hull = ConvexHull(points);
            if (hull.Count < 3) return new Box { Width = 0d, Height = 0d };
            var best = new Box { Width = double.MaxValue, Height = double.MaxValue };
            for (var index = 0; index < hull.Count; index++)
            {
                var a = hull[index]; var b = hull[(index + 1) % hull.Count];
                var edgeX = b.X - a.X; var edgeY = b.Y - a.Y;
                var length = Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
                if (length < 1e-9d) continue;
                var ux = edgeX / length; var uy = edgeY / length;
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (var point in hull)
                {
                    var px = point.X - a.X; var py = point.Y - a.Y;
                    var along = px * ux + py * uy;          // 沿边方向
                    var across = -px * uy + py * ux;        // 垂直边方向
                    if (along < minX) minX = along;
                    if (along > maxX) maxX = along;
                    if (across < minY) minY = across;
                    if (across > maxY) maxY = across;
                }
                var width = maxX - minX; var height = maxY - minY;
                if (width * height < best.Width * best.Height) best = new Box { Width = width, Height = height };
            }
            if (best.Width == double.MaxValue) return new Box { Width = 0d, Height = 0d };
            return best;
        }

        /// <summary>Andrew 单调链凸包。</summary>
        internal static List<VectorPoint> ConvexHull(IList<VectorPoint> points)
        {
            var sorted = points.OrderBy(point => point.X).ThenBy(point => point.Y).ToList();
            if (sorted.Count < 3) return sorted;
            var lower = new List<VectorPoint>();
            foreach (var point in sorted)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0d) lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }
            var upper = new List<VectorPoint>();
            for (var index = sorted.Count - 1; index >= 0; index--)
            {
                var point = sorted[index];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0d) upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }
            lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(VectorPoint origin, VectorPoint a, VectorPoint b)
        {
            return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
        }

        internal struct Box { public double Width; public double Height; }

        private static int Depth(PolygonInfo item) { var depth = 0; while (item.Parent != null) { depth++; item = item.Parent; } return depth; }
        private static double SignedArea(IList<VectorPoint> points) { var area = 0d; for (var i = 0; i < points.Count; i++) { var a = points[i]; var b = points[(i + 1) % points.Count]; area += a.X * b.Y - b.X * a.Y; } return area * 0.5d; }
        private static double Perimeter(IList<VectorPoint> points) { var value = 0d; for (var i = 0; i < points.Count; i++) value += Distance(points[i], points[(i + 1) % points.Count]); return value; }
        private static VectorPoint Centroid(IList<VectorPoint> points) { var x = 0d; var y = 0d; foreach (var point in points) { x += point.X; y += point.Y; } return new VectorPoint(x / points.Count, y / points.Count); }
        private static bool Contains(IList<VectorPoint> polygon, VectorPoint point) { var inside = false; for (var i = 0; i < polygon.Count; i++) { var a = polygon[i]; var b = polygon[(i + polygon.Count - 1) % polygon.Count]; if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / Math.Max(1e-12, b.Y - a.Y) + a.X) inside = !inside; } return inside; }
        private static double Distance(VectorPoint a, VectorPoint b) { var x = a.X - b.X; var y = a.Y - b.Y; return Math.Sqrt(x * x + y * y); }
        private sealed class PolygonInfo { public List<VectorPoint> Points; public double Area; public double Perimeter; public PolygonInfo Parent; }
    }
}
