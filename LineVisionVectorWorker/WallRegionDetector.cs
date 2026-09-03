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
                var thickness = 2d * netArea / boundary;
                if (thickness < minimumThickness || thickness > maximumThickness) continue;
                var complexity = Math.Min(1d, (outer.Points.Count + holes.Sum(item => item.Points.Count)) / 20d);
                var confidence = Math.Min(1d, 0.55d + complexity * 0.2d + (holes.Count > 0 ? 0.2d : 0d));
                result.Add(new VectorWallRegion { Outer = outer.Points, Holes = holes.Select(item => item.Points).ToList(), AverageThickness = thickness, Confidence = confidence });
            }
            return result;
        }

        private static int Depth(PolygonInfo item) { var depth = 0; while (item.Parent != null) { depth++; item = item.Parent; } return depth; }
        private static double SignedArea(IList<VectorPoint> points) { var area = 0d; for (var i = 0; i < points.Count; i++) { var a = points[i]; var b = points[(i + 1) % points.Count]; area += a.X * b.Y - b.X * a.Y; } return area * 0.5d; }
        private static double Perimeter(IList<VectorPoint> points) { var value = 0d; for (var i = 0; i < points.Count; i++) value += Distance(points[i], points[(i + 1) % points.Count]); return value; }
        private static VectorPoint Centroid(IList<VectorPoint> points) { var x = 0d; var y = 0d; foreach (var point in points) { x += point.X; y += point.Y; } return new VectorPoint(x / points.Count, y / points.Count); }
        private static bool Contains(IList<VectorPoint> polygon, VectorPoint point) { var inside = false; for (var i = 0; i < polygon.Count; i++) { var a = polygon[i]; var b = polygon[(i + polygon.Count - 1) % polygon.Count]; if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / Math.Max(1e-12, b.Y - a.Y) + a.X) inside = !inside; } return inside; }
        private static double Distance(VectorPoint a, VectorPoint b) { var x = a.X - b.X; var y = a.Y - b.Y; return Math.Sqrt(x * x + y * y); }
        private sealed class PolygonInfo { public List<VectorPoint> Points; public double Area; public double Perimeter; public PolygonInfo Parent; }
    }
}
