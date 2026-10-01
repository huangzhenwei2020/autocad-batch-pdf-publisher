using System;
using System.Collections.Generic;
using System.Linq;
using LibTessDotNet.Double;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Validated outer boundary, voids and double-precision triangulation in millimetres.</summary>
    public sealed class SlabGeometry
    {
        private const double Tolerance = 1e-6d;
        public List<List<PointModel>> Contours { get; private set; }
        public List<List<PointModel>> Triangles { get; private set; }

        public static string Validate(SlabModel slab)
        {
            if (slab == null || !Finite(slab.Thickness) || slab.Thickness <= 0.5d)
                return "楼板板厚必须有效，且大于 0.5 mm。";
            try { Build(slab); return null; }
            catch (ArgumentException ex) { return ex.Message; }
        }

        public static SlabGeometry Build(SlabModel slab)
        {
            if (slab == null || !Finite(slab.Thickness)
                || !Finite(slab.TopElevation) || (slab.TopOffset.HasValue && !Finite(slab.TopOffset.Value)))
                throw new ArgumentException("楼板板厚和标高必须有效，板厚应大于 0.5 mm。");
            var outer = Ring(slab.Outline);
            var contours = new List<List<PointModel>> { outer };
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var opening in slab.Openings ?? new List<SlabOpeningModel>())
            {
                if (opening == null || string.IsNullOrWhiteSpace(opening.Id) || !ids.Add(opening.Id))
                    throw new ArgumentException("楼板洞口必须具有不重复的 ID。");
                var hole = Ring(opening.Outline);
                if (!Inside(hole[0], outer) || Intersects(hole, outer))
                    throw new ArgumentException("楼板洞口必须完全位于板内，不能触碰或跨越外边界。");
                foreach (var other in contours.Skip(1))
                    if (Intersects(hole, other) || Inside(hole[0], other) || Inside(other[0], hole))
                        throw new ArgumentException("楼板洞口不能相交、相接或相互包含。");
                contours.Add(hole);
            }
            if (Area(outer) < 0) outer.Reverse();
            foreach (var hole in contours.Skip(1)) if (Area(hole) > 0) hole.Reverse();
            // Translate before tessellation to retain precision on large CAD coordinates.
            var origin = outer[0];
            var tess = new Tess();
            foreach (var ring in contours)
                tess.AddContour(ring.Select(p => new ContourVertex
                { Position = new Vec3 { X = p.X - origin.X, Y = p.Y - origin.Y, Z = 0 } }).ToArray());
            tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3,
                normal: new Vec3 { X = 0, Y = 0, Z = 1 });
            var triangles = new List<List<PointModel>>();
            for (var i = 0; i < tess.ElementCount; i++)
            {
                var triangle = new List<PointModel>();
                for (var j = 0; j < 3; j++)
                {
                    var index = tess.Elements[i * 3 + j];
                    if (index == Tess.Undef) break;
                    var p = tess.Vertices[index].Position;
                    triangle.Add(new PointModel(p.X + origin.X, p.Y + origin.Y));
                }
                if (triangle.Count != 3 || Math.Abs(Area(triangle)) <= Tolerance * Tolerance) continue;
                if (Area(triangle) < 0) triangle.Reverse();
                triangles.Add(triangle);
            }
            if (triangles.Count == 0) throw new ArgumentException("楼板没有有效的实体面积。");
            return new SlabGeometry { Contours = contours, Triangles = triangles };
        }

        public bool IsConvexWithoutOpenings
        {
            get
            {
                var ring = Contours[0];
                return Contours.Count == 1 && Enumerable.Range(0, ring.Count)
                    .All(i => Cross(ring[i], ring[(i + 1) % ring.Count], ring[(i + 2) % ring.Count]) >= 0);
            }
        }

        private static List<PointModel> Ring(List<PointModel> input)
        {
            if (input == null || input.Any(p => p == null || !Finite(p.X) || !Finite(p.Y)))
                throw new ArgumentException("楼板轮廓包含无效坐标。");
            var ring = input.Select(p => new PointModel(p.X, p.Y)).ToList();
            if (ring.Count > 1 && Near(ring[0], ring[ring.Count - 1])) ring.RemoveAt(ring.Count - 1);
            if (ring.Count < 3 || Math.Abs(Area(ring)) < 0.5d)
                throw new ArgumentException("楼板轮廓至少需要三个点及有效面积。");
            for (var i = 0; i < ring.Count; i++)
            {
                var next = (i + 1) % ring.Count;
                if (Near(ring[i], ring[next])) throw new ArgumentException("楼板轮廓不能包含零长度边。");
                var previous = ring[(i + ring.Count - 1) % ring.Count];
                if (OnSegment(previous, ring[i], ring[next]) || OnSegment(ring[i], ring[next], previous))
                    throw new ArgumentException("楼板轮廓不能包含折返重叠边。");
                for (var j = i + 1; j < ring.Count; j++)
                {
                    var jNext = (j + 1) % ring.Count;
                    if (j == next || jNext == i) continue;
                    if (SegmentsIntersect(ring[i], ring[next], ring[j], ring[jNext]))
                        throw new ArgumentException("楼板轮廓不能自交或自接触。");
                }
            }
            return ring;
        }

        private static double Area(List<PointModel> ring)
        {
            var origin = ring.Count == 0 ? new PointModel() : ring[0];
            double sum = 0;
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                sum += (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y);
            }
            return sum / 2d;
        }

        private static bool Inside(PointModel p, List<PointModel> ring)
        {
            var inside = false;
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                if (OnSegment(a, b, p)) return false;
                if ((a.Y > p.Y) != (b.Y > p.Y)
                    && p.X < a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
            }
            return inside;
        }

        private static bool Intersects(List<PointModel> a, List<PointModel> b)
        {
            for (var i = 0; i < a.Count; i++)
                for (var j = 0; j < b.Count; j++)
                    if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
            return false;
        }

        private static bool SegmentsIntersect(PointModel a, PointModel b, PointModel c, PointModel d)
        {
            if (OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b)) return true;
            return Math.Sign(Cross(a, b, c)) != Math.Sign(Cross(a, b, d))
                && Math.Sign(Cross(c, d, a)) != Math.Sign(Cross(c, d, b));
        }

        private static bool OnSegment(PointModel a, PointModel b, PointModel p)
        {
            var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            return Math.Abs(Cross(a, b, p)) <= Tolerance * length
                && p.X >= Math.Min(a.X, b.X) - Tolerance && p.X <= Math.Max(a.X, b.X) + Tolerance
                && p.Y >= Math.Min(a.Y, b.Y) - Tolerance && p.Y <= Math.Max(a.Y, b.Y) + Tolerance;
        }

        private static double Cross(PointModel a, PointModel b, PointModel c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        private static bool Near(PointModel a, PointModel b)
            => Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
