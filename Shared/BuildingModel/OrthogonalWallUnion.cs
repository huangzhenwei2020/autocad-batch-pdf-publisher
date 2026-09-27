using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// Exact, non-overlapping cell decomposition for straight orthogonal walls on one storey.
    /// Openings are subtracted as 3D intervals before union. Oblique walls continue through
    /// the existing wall builder until their boolean geometry is implemented.
    /// </summary>
    internal static class OrthogonalWallUnion
    {
        private sealed class Box
        {
            public WallModel Wall;
            public double X0, X1, Y0, Y1, Z0, Z1;
        }

        internal static HashSet<string> AddJoinedWalls(BuildingVolume volume, BuildingModelDocument model,
            List<WallModel> walls, ref bool first)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var boxes = new List<Box>();
            var fullBoxes = new List<Box>();
            foreach (var wall in walls)
            {
                if (string.IsNullOrWhiteSpace(wall.Id)) continue;
                var horizontal = Math.Abs(wall.Y2 - wall.Y1) < 0.001d;
                var vertical = Math.Abs(wall.X2 - wall.X1) < 0.001d;
                if (horizontal == vertical) continue;
                var length = horizontal ? Math.Abs(wall.X2 - wall.X1) : Math.Abs(wall.Y2 - wall.Y1);
                var z0 = model.BaseElevationOf(wall);
                var z1 = z0 + model.HeightOf(wall);
                if (length < 1d || z1 - z0 < 1d) continue;
                var half = (wall.Thickness > 0.5d ? wall.Thickness : 200d) / 2d;
                var offset = WallReferenceGeometry.BodyOffset(wall);
                var full = new Box
                {
                    Wall = wall, Z0 = z0, Z1 = z1,
                    X0 = horizontal ? Math.Min(wall.X1, wall.X2) : wall.X1 - half - Math.Sign(wall.Y2 - wall.Y1) * offset,
                    X1 = horizontal ? Math.Max(wall.X1, wall.X2) : wall.X1 + half - Math.Sign(wall.Y2 - wall.Y1) * offset,
                    Y0 = vertical ? Math.Min(wall.Y1, wall.Y2) : wall.Y1 - half + Math.Sign(wall.X2 - wall.X1) * offset,
                    Y1 = vertical ? Math.Max(wall.Y1, wall.Y2) : wall.Y1 + half + Math.Sign(wall.X2 - wall.X1) * offset
                };
                var openings = (model.Openings ?? new List<OpeningModel>())
                    .Where(o => o != null && Same(o.HostWallId, wall.Id)
                        && o.Width > 1d && o.Height > 1d)
                    .ToList();
                // A door/opening through the actual corner needs an explicit junction rule.
                // Keep the existing wall builder for that uncommon ambiguous case.
                if (openings.Any(o => o.Offset - o.Width / 2d < half + 1d
                    || o.Offset + o.Width / 2d > length - half - 1d)) continue;
                fullBoxes.Add(full);
                AddSolidSegments(boxes, full, openings, length, horizontal);
            }
            var seen = new bool[boxes.Count];
            for (var i = 0; i < boxes.Count; i++)
            {
                if (seen[i]) continue;
                var component = new List<Box>();
                var queue = new Queue<int>();
                queue.Enqueue(i); seen[i] = true;
                while (queue.Count > 0)
                {
                    var current = boxes[queue.Dequeue()];
                    component.Add(current);
                    for (var j = 0; j < boxes.Count; j++)
                    {
                        if (seen[j] || !Touch(current, boxes[j])) continue;
                        seen[j] = true; queue.Enqueue(j);
                    }
                }
                if (component.Select(b => b.Wall.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
                var shapes = new List<Box>(component);
                var whole = fullBoxes.Where(b => component.Any(c => Same(c.Wall.Id, b.Wall.Id))).ToList();
                for (var a = 0; a < whole.Count; a++)
                for (var b = a + 1; b < whole.Count; b++)
                {
                    var one = whole[a]; var two = whole[b];
                    if (Math.Min(one.Z1, two.Z1) - Math.Max(one.Z0, two.Z0) < 0.001d
                        || Horizontal(one.Wall) == Horizontal(two.Wall)) continue;
                    foreach (var p in Endpoints(one.Wall))
                    foreach (var q in Endpoints(two.Wall))
                    {
                        if (Math.Abs(p.Item1 - q.Item1) > 0.5d || Math.Abs(p.Item2 - q.Item2) > 0.5d) continue;
                        var horizontal = Horizontal(one.Wall) ? one : two;
                        var vertical = Horizontal(one.Wall) ? two : one;
                        shapes.Add(new Box { Wall = one.Wall,
                            Z0 = Math.Max(one.Z0, two.Z0), Z1 = Math.Min(one.Z1, two.Z1),
                            X0 = vertical.X0, X1 = vertical.X1,
                            Y0 = horizontal.Y0, Y1 = horizontal.Y1 });
                    }
                }
                AddCells(volume, shapes, ref first);
                foreach (var box in component) result.Add(box.Wall.Id);
            }
            return result;
        }

        private static void AddSolidSegments(List<Box> boxes, Box full,
            List<OpeningModel> openings, double length, bool horizontal)
        {
            if (openings.Count == 0) { boxes.Add(full); return; }
            var positions = new[] { 0d, length }.Concat(openings.SelectMany(o =>
                new[] { o.Offset - o.Width / 2d, o.Offset + o.Width / 2d }))
                .Where(v => v >= 0d && v <= length).Distinct().OrderBy(v => v).ToArray();
            var elevations = new[] { full.Z0, full.Z1 }.Concat(openings.SelectMany(o =>
                new[] { full.Z0 + o.Sill, full.Z0 + o.Sill + o.Height }))
                .Where(v => v >= full.Z0 && v <= full.Z1).Distinct().OrderBy(v => v).ToArray();
            var direction = horizontal ? Math.Sign(full.Wall.X2 - full.Wall.X1)
                : Math.Sign(full.Wall.Y2 - full.Wall.Y1);
            var origin = horizontal ? full.Wall.X1 : full.Wall.Y1;
            for (var along = 0; along < positions.Length - 1; along++)
            for (var z = 0; z < elevations.Length - 1; z++)
            {
                var from = positions[along]; var to = positions[along + 1];
                var low = elevations[z]; var high = elevations[z + 1];
                if (to - from < 0.001d || high - low < 0.001d) continue;
                var middle = (from + to) / 2d;
                var midZ = (low + high) / 2d;
                if (openings.Any(o => middle > o.Offset - o.Width / 2d
                    && middle < o.Offset + o.Width / 2d
                    && midZ > full.Z0 + o.Sill && midZ < full.Z0 + o.Sill + o.Height)) continue;
                var first = origin + direction * from;
                var last = origin + direction * to;
                boxes.Add(new Box { Wall = full.Wall, Z0 = low, Z1 = high,
                    X0 = horizontal ? Math.Min(first, last) : full.X0,
                    X1 = horizontal ? Math.Max(first, last) : full.X1,
                    Y0 = horizontal ? full.Y0 : Math.Min(first, last),
                    Y1 = horizontal ? full.Y1 : Math.Max(first, last) });
            }
        }

        private static void AddCells(BuildingVolume volume, List<Box> boxes, ref bool first)
        {
            var xs = boxes.SelectMany(b => new[] { b.X0, b.X1 }).Distinct().OrderBy(x => x).ToArray();
            var ys = boxes.SelectMany(b => new[] { b.Y0, b.Y1 }).Distinct().OrderBy(y => y).ToArray();
            var zs = boxes.SelectMany(b => new[] { b.Z0, b.Z1 }).Distinct().OrderBy(z => z).ToArray();
            var owner = new Box[xs.Length - 1, ys.Length - 1, zs.Length - 1];
            foreach (var box in boxes)
            {
                var fromX = Array.BinarySearch(xs, box.X0);
                var toX = Array.BinarySearch(xs, box.X1);
                var fromY = Array.BinarySearch(ys, box.Y0);
                var toY = Array.BinarySearch(ys, box.Y1);
                var fromZ = Array.BinarySearch(zs, box.Z0);
                var toZ = Array.BinarySearch(zs, box.Z1);
                for (var x = fromX; x < toX; x++)
                for (var y = fromY; y < toY; y++)
                for (var z = fromZ; z < toZ; z++)
                    if (owner[x, y, z] == null) owner[x, y, z] = box;
            }
            for (var x = 0; x < xs.Length - 1; x++)
            for (var y = 0; y < ys.Length - 1; y++)
            for (var z = 0; z < zs.Length - 1; z++)
            {
                var box = owner[x, y, z];
                if (box == null) continue;
                var x0 = xs[x]; var x1 = xs[x + 1]; var y0 = ys[y]; var y1 = ys[y + 1];
                var z0 = zs[z]; var z1 = zs[z + 1];
                if (x1 - x0 < 0.000001d || y1 - y0 < 0.000001d || z1 - z0 < 0.000001d) continue;
                if (z == zs.Length - 2 || owner[x, y, z + 1] == null)
                    AddFace(volume, box, 0, 0, 1, new[] { P(x0,y0,z1), P(x1,y0,z1),
                        P(x1,y1,z1), P(x0,y1,z1) }, ref first);
                if (z == 0 || owner[x, y, z - 1] == null)
                    AddFace(volume, box, 0, 0, -1, new[] { P(x0,y1,z0), P(x1,y1,z0),
                        P(x1,y0,z0), P(x0,y0,z0) }, ref first);
                if (y == 0 || owner[x, y - 1, z] == null)
                    AddFace(volume, box, 0, -1, 0, new[] { P(x0,y0,z0), P(x1,y0,z0),
                        P(x1,y0,z1), P(x0,y0,z1) }, ref first);
                if (x == xs.Length - 2 || owner[x + 1, y, z] == null)
                    AddFace(volume, box, 1, 0, 0, new[] { P(x1,y0,z0), P(x1,y1,z0),
                        P(x1,y1,z1), P(x1,y0,z1) }, ref first);
                if (y == ys.Length - 2 || owner[x, y + 1, z] == null)
                    AddFace(volume, box, 0, 1, 0, new[] { P(x1,y1,z0), P(x0,y1,z0),
                        P(x0,y1,z1), P(x1,y1,z1) }, ref first);
                if (x == 0 || owner[x - 1, y, z] == null)
                    AddFace(volume, box, -1, 0, 0, new[] { P(x0,y1,z0), P(x0,y0,z0),
                        P(x0,y0,z1), P(x0,y1,z1) }, ref first);
            }
        }

        private static Point3DModel P(double x, double y, double z) { return new Point3DModel(x, y, z); }

        private static void AddFace(BuildingVolume volume, Box box, double nx, double ny, double nz,
            Point3DModel[] points, ref bool first)
        {
            volume.Faces.Add(new VolumeFace { Kind = "wall", ElementId = box.Wall.Id,
                StoreyId = box.Wall.StoreyId, NormalX = nx, NormalY = ny, NormalZ = nz,
                Points = points.ToList() });
            foreach (var p in points)
            {
                if (first)
                { volume.MinX = volume.MaxX = p.X; volume.MinY = volume.MaxY = p.Y;
                    volume.MinZ = volume.MaxZ = p.Z; first = false; }
                else
                { volume.MinX = Math.Min(volume.MinX, p.X); volume.MaxX = Math.Max(volume.MaxX, p.X);
                    volume.MinY = Math.Min(volume.MinY, p.Y); volume.MaxY = Math.Max(volume.MaxY, p.Y);
                    volume.MinZ = Math.Min(volume.MinZ, p.Z); volume.MaxZ = Math.Max(volume.MaxZ, p.Z); }
            }
        }

        private static bool Touch(Box a, Box b)
        {
            if (Same(a.Wall.Id, b.Wall.Id)) return true;
            if (!Same(a.Wall.StoreyId, b.Wall.StoreyId)
                || Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0) <= 0.001d)
                return false;
            if (a.X0 <= b.X1 + 0.001d && b.X0 <= a.X1 + 0.001d
                && a.Y0 <= b.Y1 + 0.001d && b.Y0 <= a.Y1 + 0.001d) return true;
            return Horizontal(a.Wall) != Horizontal(b.Wall)
                && Endpoints(a.Wall).Any(p => Endpoints(b.Wall).Any(q =>
                    Math.Abs(p.Item1 - q.Item1) <= 0.5d && Math.Abs(p.Item2 - q.Item2) <= 0.5d));
        }
        private static bool Horizontal(WallModel wall) { return Math.Abs(wall.Y2 - wall.Y1) < 0.001d; }
        private static IEnumerable<Tuple<double,double>> Endpoints(WallModel wall)
        { yield return Tuple.Create(wall.X1, wall.Y1); yield return Tuple.Create(wall.X2, wall.Y2); }
        private static bool Same(string a, string b)
        { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
