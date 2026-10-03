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
        // Editing can leave sub-hundredth-millimetre drift on an orthogonal axis.
        private const double AxisTolerance = 0.01d;
        // Use the same tolerance as native orthogonal-wall recognition. Microscopic
        // coordinate drift must not split every wall surface into extra grid strips.
        private const double GridTolerance = AxisTolerance;
        private sealed class Box
        {
            public WallModel Wall;
            public double X0, X1, Y0, Y1, Z0, Z1;
        }

        private sealed class TopMiter
        {
            public double X0, X1, Y0, Y1, Z0, Z;
            public readonly List<Tuple<Box, PointModel[]>> Regions = new List<Tuple<Box, PointModel[]>>();
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
                var horizontal = Horizontal(wall);
                var vertical = Math.Abs(wall.X2 - wall.X1) <= AxisTolerance;
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
                    var horizontal = Horizontal(one.Wall) ? one : two;
                    var vertical = Horizontal(one.Wall) ? two : one;
                    if (!NearJunction(horizontal, vertical)) continue;
                    shapes.Add(new Box { Wall = one.Wall,
                        Z0 = Math.Max(one.Z0, two.Z0), Z1 = Math.Min(one.Z1, two.Z1),
                        X0 = vertical.X0, X1 = vertical.X1,
                        Y0 = horizontal.Y0, Y1 = horizontal.Y1 });
                }
                AddCells(volume, shapes, CreateTopMiters(whole), ref first);
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

        private static List<TopMiter> CreateTopMiters(List<Box> walls)
        {
            var result = new List<TopMiter>();
            var ends = walls.SelectMany(b => new[]
            {
                Tuple.Create(b, b.Wall.X1, b.Wall.Y1, b.Wall.X2, b.Wall.Y2),
                Tuple.Create(b, b.Wall.X2, b.Wall.Y2, b.Wall.X1, b.Wall.Y1)
            });
            foreach (var group in ends.GroupBy(e => Tuple.Create(e.Item1.Wall.StoreyId,
                Math.Round(e.Item2, 1), Math.Round(e.Item3, 1))))
            {
                var at = group.ToArray();
                if (at.Length < 2 || at.Length > 3 || at.Any(e =>
                    Math.Abs(e.Item1.Z0 - at[0].Item1.Z0) > 0.001d
                    || Math.Abs(e.Item1.Z1 - at[0].Item1.Z1) > 0.001d)) continue;
                var horizontal = at.Where(e => Horizontal(e.Item1.Wall)).ToArray();
                var vertical = at.Where(e => !Horizontal(e.Item1.Wall)).ToArray();
                var x = at[0].Item2; var y = at[0].Item3;
                Box h, v;
                if (horizontal.Length == 1 && vertical.Length == 1)
                {
                    h = horizontal[0].Item1; v = vertical[0].Item1;
                    var hx = Math.Sign(horizontal[0].Item4 - x);
                    var vy = Math.Sign(vertical[0].Item5 - y);
                    var m = new TopMiter { X0 = v.X0, X1 = v.X1,
                        Y0 = h.Y0, Y1 = h.Y1, Z0 = h.Z0, Z = h.Z1 };
                    var bl = new PointModel(m.X0, m.Y0); var br = new PointModel(m.X1, m.Y0);
                    var tr = new PointModel(m.X1, m.Y1); var tl = new PointModel(m.X0, m.Y1);
                    PointModel[] hRegion, vRegion;
                    if (hx * vy > 0)
                    {
                        var lowerRight = new[] { bl, br, tr };
                        var upperLeft = new[] { bl, tr, tl };
                        hRegion = hx > 0 ? lowerRight : upperLeft;
                        vRegion = hx > 0 ? upperLeft : lowerRight;
                    }
                    else
                    {
                        var lowerLeft = new[] { bl, br, tl };
                        var upperRight = new[] { br, tr, tl };
                        hRegion = hx > 0 ? upperRight : lowerLeft;
                        vRegion = hx > 0 ? lowerLeft : upperRight;
                    }
                    m.Regions.Add(Tuple.Create(h, hRegion));
                    m.Regions.Add(Tuple.Create(v, vRegion));
                    result.Add(m);
                }
                else if (horizontal.Length == 2 && vertical.Length == 1)
                {
                    var left = horizontal.FirstOrDefault(e => e.Item4 < x);
                    var right = horizontal.FirstOrDefault(e => e.Item4 > x);
                    if (left == null || right == null
                        || Math.Abs(left.Item1.Y0 - right.Item1.Y0) > 0.001d
                        || Math.Abs(left.Item1.Y1 - right.Item1.Y1) > 0.001d) continue;
                    h = left.Item1; v = vertical[0].Item1;
                    var m = new TopMiter { X0 = v.X0, X1 = v.X1,
                        Y0 = h.Y0, Y1 = h.Y1, Z0 = h.Z0, Z = h.Z1 };
                    var near = vertical[0].Item5 > y ? m.Y0 : m.Y1;
                    var far = vertical[0].Item5 > y ? m.Y1 : m.Y0;
                    var tip = new PointModel(x, near);
                    m.Regions.Add(Tuple.Create(left.Item1, new[] {
                        new PointModel(m.X0, near), tip, new PointModel(m.X0, far) }));
                    m.Regions.Add(Tuple.Create(right.Item1, new[] {
                        tip, new PointModel(m.X1, near), new PointModel(m.X1, far) }));
                    m.Regions.Add(Tuple.Create(v, new[] {
                        tip, new PointModel(m.X0, far), new PointModel(m.X1, far) }));
                    result.Add(m);
                }
                else if (vertical.Length == 2 && horizontal.Length == 1)
                {
                    var bottom = vertical.FirstOrDefault(e => e.Item5 < y);
                    var top = vertical.FirstOrDefault(e => e.Item5 > y);
                    if (bottom == null || top == null
                        || Math.Abs(bottom.Item1.X0 - top.Item1.X0) > 0.001d
                        || Math.Abs(bottom.Item1.X1 - top.Item1.X1) > 0.001d) continue;
                    h = horizontal[0].Item1; v = bottom.Item1;
                    var m = new TopMiter { X0 = v.X0, X1 = v.X1,
                        Y0 = h.Y0, Y1 = h.Y1, Z0 = h.Z0, Z = h.Z1 };
                    var near = horizontal[0].Item4 > x ? m.X0 : m.X1;
                    var far = horizontal[0].Item4 > x ? m.X1 : m.X0;
                    var tip = new PointModel(near, y);
                    m.Regions.Add(Tuple.Create(bottom.Item1, new[] {
                        new PointModel(near, m.Y0), new PointModel(far, m.Y0), tip }));
                    m.Regions.Add(Tuple.Create(top.Item1, new[] {
                        tip, new PointModel(far, m.Y1), new PointModel(near, m.Y1) }));
                    m.Regions.Add(Tuple.Create(h, new[] {
                        tip, new PointModel(far, m.Y0), new PointModel(far, m.Y1) }));
                    result.Add(m);
                }
            }
            foreach (var h in walls.Where(b => Horizontal(b.Wall)))
            foreach (var v in walls.Where(b => !Horizontal(b.Wall)))
            {
                if (!Same(h.Wall.StoreyId, v.Wall.StoreyId)
                    || Math.Abs(h.Z0 - v.Z0) > 0.001d
                    || Math.Abs(h.Z1 - v.Z1) > 0.001d
                    || !NearJunction(h, v)
                    || result.Any(m => Math.Abs(m.X0 - v.X0) < 0.001d
                        && Math.Abs(m.Y0 - h.Y0) < 0.001d
                        && Math.Abs(m.Z - h.Z1) < 0.001d)) continue;
                var x = v.Wall.X1; var y = h.Wall.Y1;
                var hAtStart = Math.Abs(h.Wall.X1 - x) <= Math.Abs(h.Wall.X2 - x);
                var vAtStart = Math.Abs(v.Wall.Y1 - y) <= Math.Abs(v.Wall.Y2 - y);
                var hx = Math.Sign(hAtStart ? h.Wall.X2 - h.Wall.X1 : h.Wall.X1 - h.Wall.X2);
                var vy = Math.Sign(vAtStart ? v.Wall.Y2 - v.Wall.Y1 : v.Wall.Y1 - v.Wall.Y2);
                var miter = new TopMiter { X0 = v.X0, X1 = v.X1,
                    Y0 = h.Y0, Y1 = h.Y1, Z0 = h.Z0, Z = h.Z1 };
                var bl = new PointModel(miter.X0, miter.Y0);
                var br = new PointModel(miter.X1, miter.Y0);
                var tr = new PointModel(miter.X1, miter.Y1);
                var tl = new PointModel(miter.X0, miter.Y1);
                PointModel[] hRegion, vRegion;
                if (hx * vy > 0)
                {
                    hRegion = hx > 0 ? new[] { bl, br, tr } : new[] { bl, tr, tl };
                    vRegion = hx > 0 ? new[] { bl, tr, tl } : new[] { bl, br, tr };
                }
                else
                {
                    hRegion = hx > 0 ? new[] { br, tr, tl } : new[] { bl, br, tl };
                    vRegion = hx > 0 ? new[] { bl, br, tl } : new[] { br, tr, tl };
                }
                miter.Regions.Add(Tuple.Create(h, hRegion));
                miter.Regions.Add(Tuple.Create(v, vRegion));
                result.Add(miter);
            }
            return result;
        }

        private static List<PointModel> ClipConvex(PointModel[] subject, PointModel[] clip)
        {
            var output = subject.ToList();
            if (SignedArea(clip) < 0d) clip = clip.Reverse().ToArray();
            for (var i = 0; i < clip.Length; i++)
            {
                var a = clip[i]; var b = clip[(i + 1) % clip.Length];
                var input = output; output = new List<PointModel>();
                if (input.Count == 0) break;
                var previous = input[input.Count - 1];
                var previousSide = Side(a, b, previous);
                foreach (var current in input)
                {
                    var currentSide = Side(a, b, current);
                    if ((previousSide < -0.000001d) != (currentSide < -0.000001d))
                    {
                        var t = previousSide / (previousSide - currentSide);
                        output.Add(new PointModel(previous.X + (current.X - previous.X) * t,
                            previous.Y + (current.Y - previous.Y) * t));
                    }
                    if (currentSide >= -0.000001d) output.Add(current);
                    previous = current; previousSide = currentSide;
                }
            }
            return output;
        }

        private static double Side(PointModel a, PointModel b, PointModel p)
            => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

        private static double SignedArea(IReadOnlyList<PointModel> points)
        {
            var twice = 0d;
            for (var i = 0; i < points.Count; i++)
            {
                var next = points[(i + 1) % points.Count];
                twice += points[i].X * next.Y - next.X * points[i].Y;
            }
            return twice / 2d;
        }

        private static Box SideOwner(Box fallback, List<TopMiter> miters,
            double x, double y, double z)
        {
            foreach (var m in miters)
            {
                if (z < m.Z0 - 0.001d || z > m.Z + 0.001d
                    || x < m.X0 - 0.001d || x > m.X1 + 0.001d
                    || y < m.Y0 - 0.001d || y > m.Y1 + 0.001d) continue;
                foreach (var region in m.Regions)
                {
                    var polygon = region.Item2;
                    var clockwise = SignedArea(polygon) < 0d;
                    var inside = true;
                    for (var i = 0; i < polygon.Length; i++)
                    {
                        var side = Side(polygon[i], polygon[(i + 1) % polygon.Length],
                            new PointModel(x, y));
                        if (clockwise ? side > 0.000001d : side < -0.000001d)
                        { inside = false; break; }
                    }
                    if (inside) return region.Item1;
                }
            }
            return fallback;
        }

        private static void AddCells(BuildingVolume volume, List<Box> boxes,
            List<TopMiter> miters, ref bool first)
        {
            var xs = GridCoordinates(boxes.SelectMany(b => new[] { b.X0, b.X1 }));
            var ys = GridCoordinates(boxes.SelectMany(b => new[] { b.Y0, b.Y1 }));
            var zs = GridCoordinates(boxes.SelectMany(b => new[] { b.Z0, b.Z1 }));
            var owner = new Box[xs.Length - 1, ys.Length - 1, zs.Length - 1];
            foreach (var box in boxes)
            {
                var fromX = GridIndex(xs, box.X0);
                var toX = GridIndex(xs, box.X1);
                var fromY = GridIndex(ys, box.Y0);
                var toY = GridIndex(ys, box.Y1);
                var fromZ = GridIndex(zs, box.Z0);
                var toZ = GridIndex(zs, box.Z1);
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
                {
                    var miter = miters.FirstOrDefault(m => Math.Abs(m.Z - z1) < 0.001d
                        && x0 >= m.X0 - 0.001d && x1 <= m.X1 + 0.001d
                        && y0 >= m.Y0 - 0.001d && y1 <= m.Y1 + 0.001d);
                    if (miter == null)
                        AddFace(volume, box, 0, 0, 1, new[] { P(x0,y0,z1), P(x1,y0,z1),
                            P(x1,y1,z1), P(x0,y1,z1) }, ref first);
                    else
                    {
                        var cell = new[] { new PointModel(x0, y0), new PointModel(x1, y0),
                            new PointModel(x1, y1), new PointModel(x0, y1) };
                        foreach (var region in miter.Regions)
                        {
                            var clipped = ClipConvex(cell, region.Item2);
                            if (clipped.Count < 3 || Math.Abs(SignedArea(clipped)) < 0.000001d) continue;
                            AddFace(volume, region.Item1, 0, 0, 1,
                                clipped.Select(p => P(p.X, p.Y, z1)).ToArray(), ref first);
                        }
                    }
                }
                if (z == 0 || owner[x, y, z - 1] == null)
                {
                    var miter = miters.FirstOrDefault(m => Math.Abs(m.Z0 - z0) < 0.001d
                        && x0 >= m.X0 - 0.001d && x1 <= m.X1 + 0.001d
                        && y0 >= m.Y0 - 0.001d && y1 <= m.Y1 + 0.001d);
                    if (miter == null)
                        AddFace(volume, box, 0, 0, -1, new[] { P(x0,y1,z0), P(x1,y1,z0),
                            P(x1,y0,z0), P(x0,y0,z0) }, ref first);
                    else
                    {
                        var cell = new[] { new PointModel(x0, y0), new PointModel(x1, y0),
                            new PointModel(x1, y1), new PointModel(x0, y1) };
                        foreach (var region in miter.Regions)
                        {
                            var clipped = ClipConvex(cell, region.Item2);
                            if (clipped.Count < 3 || Math.Abs(SignedArea(clipped)) < 0.000001d) continue;
                            AddFace(volume, region.Item1, 0, 0, -1,
                                clipped.AsEnumerable().Reverse().Select(p => P(p.X, p.Y, z0)).ToArray(), ref first);
                        }
                    }
                }
                if (y == 0 || owner[x, y - 1, z] == null)
                    AddFace(volume, SideOwner(box, miters, (x0+x1)/2, y0, (z0+z1)/2), 0, -1, 0, new[] { P(x0,y0,z0), P(x1,y0,z0),
                        P(x1,y0,z1), P(x0,y0,z1) }, ref first);
                if (x == xs.Length - 2 || owner[x + 1, y, z] == null)
                    AddFace(volume, SideOwner(box, miters, x1, (y0+y1)/2, (z0+z1)/2), 1, 0, 0, new[] { P(x1,y0,z0), P(x1,y1,z0),
                        P(x1,y1,z1), P(x1,y0,z1) }, ref first);
                if (y == ys.Length - 2 || owner[x, y + 1, z] == null)
                    AddFace(volume, SideOwner(box, miters, (x0+x1)/2, y1, (z0+z1)/2), 0, 1, 0, new[] { P(x1,y1,z0), P(x0,y1,z0),
                        P(x0,y1,z1), P(x1,y1,z1) }, ref first);
                if (x == 0 || owner[x - 1, y, z] == null)
                    AddFace(volume, SideOwner(box, miters, x0, (y0+y1)/2, (z0+z1)/2), -1, 0, 0, new[] { P(x0,y1,z0), P(x0,y0,z0),
                        P(x0,y0,z1), P(x0,y1,z1) }, ref first);
            }
        }

        private static double[] GridCoordinates(IEnumerable<double> values)
        {
            var result = new List<double>();
            foreach (var value in values.OrderBy(v => v))
                if (result.Count == 0 || value - result[result.Count - 1] > GridTolerance)
                    result.Add(value);
            return result.ToArray();
        }

        private static int GridIndex(double[] coordinates, double value)
        {
            var index = Array.BinarySearch(coordinates, value);
            if (index >= 0) return index;
            index = ~index;
            if (index > 0 && value - coordinates[index - 1] <= GridTolerance) return index - 1;
            throw new InvalidOperationException("Wall grid coordinate was not canonicalized.");
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
                && NearJunction(Horizontal(a.Wall) ? a : b,
                    Horizontal(a.Wall) ? b : a);
        }

        private static bool NearJunction(Box horizontal, Box vertical)
        {
            var x = vertical.Wall.X1;
            var y = horizontal.Wall.Y1;
            // A moved wall may leave its axis endpoints a few millimetres apart while
            // the wall bodies still overlap. Join only within the opposite wall width.
            var horizontalReach = Math.Max(x - vertical.X0, vertical.X1 - x) + 0.5d;
            var verticalReach = Math.Max(y - horizontal.Y0, horizontal.Y1 - y) + 0.5d;
            return Endpoints(horizontal.Wall).Any(p => Math.Abs(p.Item1 - x) <= horizontalReach)
                && Endpoints(vertical.Wall).Any(p => Math.Abs(p.Item2 - y) <= verticalReach)
                && horizontal.X0 <= vertical.X1 + 0.001d
                && vertical.X0 <= horizontal.X1 + 0.001d
                && horizontal.Y0 <= vertical.Y1 + 0.001d
                && vertical.Y0 <= horizontal.Y1 + 0.001d;
        }
        private static bool Horizontal(WallModel wall) { return Math.Abs(wall.Y2 - wall.Y1) <= AxisTolerance; }
        private static IEnumerable<Tuple<double,double>> Endpoints(WallModel wall)
        { yield return Tuple.Create(wall.X1, wall.Y1); yield return Tuple.Create(wall.X2, wall.Y2); }
        private static bool Same(string a, string b)
        { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
