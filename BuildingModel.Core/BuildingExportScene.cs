using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LibTessDotNet.Double;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>
    /// Static current-pose geometry, in double-precision millimetres with Z up.
    /// Model-world point = mesh vertex + instance Translation + OriginMillimetres.
    /// Storey elevations remain absolute model-world datums. No guides, leaf-part
    /// hierarchy, motion controls, or animation are included.
    /// </summary>
    public sealed class BuildingExportScene
    {
        public string Name { get; set; }
        public double[] OriginMillimetres { get; set; } = new double[] { 0, 0, 0 };
        public List<BuildingExportStorey> Storeys { get; set; } = new List<BuildingExportStorey>();
        public List<BuildingExportMaterial> Materials { get; set; } = new List<BuildingExportMaterial>();
        public List<BuildingExportMesh> Meshes { get; set; } = new List<BuildingExportMesh>();
        public List<BuildingExportInstance> Instances { get; set; } = new List<BuildingExportInstance>();
        /// <summary>Rendered triangle count, including every occurrence of a shared mesh.</summary>
        public int TriangleCount => Instances.Sum(instance => Meshes[instance.MeshIndex].Triangles.Count);
    }

    public sealed class BuildingExportStorey
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double Elevation { get; set; }
    }

    public sealed class BuildingExportMaterial
    {
        public string Name { get; set; }
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }
        public double Opacity { get; set; } = 1d;
    }

    public sealed class BuildingExportMesh
    {
        public string Id { get; set; }
        public List<double[]> Vertices { get; set; } = new List<double[]>();
        public List<BuildingExportTriangle> Triangles { get; set; } = new List<BuildingExportTriangle>();
    }

    public sealed class BuildingExportTriangle
    {
        public int A { get; set; }
        public int B { get; set; }
        public int C { get; set; }
        public int MaterialIndex { get; set; }
        /// <summary>Mesh-local smoothing group; null means a hard planar surface.</summary>
        public string SmoothSurfaceId { get; set; }
    }

    public sealed class BuildingExportInstance
    {
        public string Id { get; set; }
        public string SourceId { get; set; }
        public string StoreyId { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public int MeshIndex { get; set; }
        public double[] Translation { get; set; } = new double[] { 0, 0, 0 };
    }

    public static class BuildingExportSceneBuilder
    {
        // Architectural scene limits, independent of the magnitude of its CAD offset.
        private const double MaximumExtentMillimetres = 10_000_000d;
        private const double MaximumCoordinateMillimetres = 1_000_000_000_000d;
        // Only numerical duplicates, not architectural rounding (one nanometre).
        private const double DuplicatePointToleranceMillimetres = 0.000001d;
        // Cross-contour CAD junction noise, capped at a tenth of a micrometre.
        // Real 0.001 mm details remain ten times larger; never round to a grid.
        private const double WallJunctionToleranceMillimetres = 0.0001d;

        public static BuildingExportScene Build(BuildingModelDocument source, string storeyId = null,
            bool nearOrigin = true, CancellationToken cancellation = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            cancellation.ThrowIfCancellationRequested();
            var model = BuildingModelJson.FromJson(BuildingModelJson.ToJson(source));
            cancellation.ThrowIfCancellationRequested();
            BuildingElementNames.EnsureWallCodes(model);
            if (storeyId != null && model.FindStorey(storeyId) == null)
                throw new ArgumentException("The export storey does not exist.", nameof(storeyId));
            var volume = BuildingVolumeBuilder.Build(model, storeyId);
            cancellation.ThrowIfCancellationRequested();
            if (volume.Faces.Count == 0)
                throw new InvalidOperationException("The selected scope contains no exportable geometry.");
            var bounds = Bounds(volume.Faces, cancellation);
            var scene = new BuildingExportScene { Name = model.Name,
                OriginMillimetres = nearOrigin ? new[] { bounds[0], bounds[1], bounds[2] }
                    : new double[] { 0, 0, 0 } };
            var materials = new Dictionary<string, int>(StringComparer.Ordinal);
            var floors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shared = new Dictionary<string, List<(string Canonical, int Index)>>(StringComparer.Ordinal);

            foreach (var group in volume.Faces.GroupBy(face => (face.StoreyId, face.ElementId)))
            {
                cancellation.ThrowIfCancellationRequested();
                var elementId = group.Key.ElementId;
                if (string.IsNullOrWhiteSpace(elementId))
                    throw new InvalidOperationException("An export element has no identity.");
                var floor = model.FindStorey(group.Key.StoreyId);
                var floorId = floor?.Id ?? group.Key.StoreyId ?? "";
                var elevation = floor?.Elevation ?? 0d;
                CheckCoordinate(elevation);
                if (floors.Add(floorId)) scene.Storeys.Add(new BuildingExportStorey
                    { Id = floorId, Name = floor?.Name ?? floorId, Elevation = elevation });
                var localBounds = Bounds(group, cancellation);
                var anchor = new[] { localBounds[0], localBounds[1], elevation };
                var local = group.Select(face => Localize(face, anchor)).ToList();
                WeldNumericalWallVertices(local, cancellation);
                var mesh = new BuildingExportMesh();
                var vertices = new Dictionary<(double, double, double), int>();
                // Match GLB's per-element wall union; non-wall faces stay independent.
                foreach (var patches in local.GroupBy(face => (face.Kind,
                    Math.Round(face.NormalX, 6), Math.Round(face.NormalY, 6), Math.Round(face.NormalZ, 6),
                    Math.Round((face.Points[0].X * face.NormalX + face.Points[0].Y * face.NormalY
                        + face.Points[0].Z * face.NormalZ) / 1000d, 6),
                    face.SmoothSurfaceId, face.Kind == "wall" ? null : face)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var face = patches.First();
                    var kind = face.Kind ?? "other";
                    if (!materials.TryGetValue(kind, out var materialIndex))
                    {
                        materialIndex = scene.Materials.Count;
                        materials.Add(kind, materialIndex);
                        scene.Materials.Add(Material(kind));
                    }
                    foreach (var triangle in Triangulate(patches, cancellation))
                    {
                        int Vertex(Vec3 point)
                        {
                            var key = (point.X, point.Y, point.Z);
                            if (!vertices.TryGetValue(key, out var index))
                            {
                                index = mesh.Vertices.Count;
                                vertices.Add(key, index);
                                mesh.Vertices.Add(new[] { point.X, point.Y, point.Z });
                            }
                            return index;
                        }
                        mesh.Triangles.Add(new BuildingExportTriangle { A = Vertex(triangle.A),
                            B = Vertex(triangle.B), C = Vertex(triangle.C), MaterialIndex = materialIndex,
                            SmoothSurfaceId = face.SmoothSurfaceId });
                    }
                }
                if (mesh.Triangles.Count == 0)
                    throw new InvalidOperationException("Element contains no valid triangles: " + elementId);
                var canonical = Canonicalize(mesh, scene.Materials, cancellation);
                var hash = Hash(canonical);
                if (!shared.TryGetValue(hash, out var candidates))
                {
                    candidates = new List<(string, int)>();
                    shared.Add(hash, candidates);
                }
                var match = candidates.FindIndex(candidate => candidate.Canonical == canonical);
                int meshIndex;
                if (match >= 0) meshIndex = candidates[match].Index;
                else
                {
                    meshIndex = scene.Meshes.Count;
                    mesh.Id = "mesh-" + hash + (candidates.Count == 0 ? "" : "-" + candidates.Count);
                    scene.Meshes.Add(mesh);
                    candidates.Add((canonical, meshIndex));
                }
                var sourceId = StandardStoreyLayout.SourceElementId(model, elementId);
                scene.Instances.Add(new BuildingExportInstance { Id = elementId, SourceId = sourceId,
                    StoreyId = floorId, MeshIndex = meshIndex, Kind = group.First().Kind,
                    Name = ElementName(model, sourceId),
                    Translation = new[] { anchor[0] - scene.OriginMillimetres[0],
                        anchor[1] - scene.OriginMillimetres[1], anchor[2] - scene.OriginMillimetres[2] } });
            }
            cancellation.ThrowIfCancellationRequested();
            return scene;
        }

        private static string ElementName(BuildingModelDocument model, string id)
        {
            bool IsCode(string code) => !string.IsNullOrWhiteSpace(code) && code != id
                && !code.StartsWith("CAD-", StringComparison.OrdinalIgnoreCase) && !Guid.TryParse(code, out _);
            string Label(string prefix, string code, string letter, int index)
                => prefix + " " + (IsCode(code) ? code.Trim() : letter + "-" + (index + 1).ToString("D2", CultureInfo.InvariantCulture));
            var wall = model.Walls.FindIndex(e => e.Id == id);
            if (wall >= 0) return Label(model.Walls[wall].Purpose == WallPurpose.Parapet ? "女儿墙" : "墙", model.Walls[wall].Code, "W", wall);
            var opening = model.Openings.FirstOrDefault(e => e.Id == id);
            if (opening != null) return IsCode(opening.Code) ? opening.Code.Trim()
                : Label(opening.Kind ?? "门窗", null, "MC", model.Openings.IndexOf(opening));
            var slab = model.Slabs.FindIndex(e => e.Id == id);
            if (slab >= 0) return Label(model.Slabs[slab].Purpose == SlabPurpose.Balcony ? "阳台" : "楼板", model.Slabs[slab].Code, "B", slab);
            var column = model.Columns.FindIndex(e => e.Id == id);
            if (column >= 0) return Label("柱", model.Columns[column].Code, "Z", column);
            var beam = model.Beams.FindIndex(e => e.Id == id);
            if (beam >= 0) return Label("梁", model.Beams[beam].Code, "L", beam);
            var stair = model.Stairs.FindIndex(e => e.Id == id);
            if (stair >= 0) return Label("楼梯", null, "T", stair);
            var roof = model.Roofs.FindIndex(e => e.Id == id);
            if (roof >= 0) return Label("屋面", null, "R", roof);
            return "构件";
        }

        private static double[] Bounds(IEnumerable<VolumeFace> faces, CancellationToken cancellation)
        {
            var result = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
                double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var face in faces)
            {
                cancellation.ThrowIfCancellationRequested();
                if (face.Points == null || face.Points.Count < 3)
                    throw new InvalidOperationException("The model contains an incomplete face.");
                foreach (var point in face.Points)
                {
                    if (point == null) throw new InvalidOperationException("The model contains a missing point.");
                    CheckCoordinate(point.X); CheckCoordinate(point.Y); CheckCoordinate(point.Z);
                    result[0] = Math.Min(result[0], point.X); result[3] = Math.Max(result[3], point.X);
                    result[1] = Math.Min(result[1], point.Y); result[4] = Math.Max(result[4], point.Y);
                    result[2] = Math.Min(result[2], point.Z); result[5] = Math.Max(result[5], point.Z);
                }
            }
            for (var axis = 0; axis < 3; axis++)
                if (!double.IsFinite(result[axis + 3] - result[axis])
                    || result[axis + 3] - result[axis] > MaximumExtentMillimetres)
                    throw new InvalidOperationException("The export bounds exceed the architectural scene limit (10 km).");
            return result;
        }

        private static void CheckCoordinate(double value)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > MaximumCoordinateMillimetres)
                throw new InvalidOperationException("The model contains an invalid or excessive coordinate.");
        }

        private static VolumeFace Localize(VolumeFace face, double[] anchor)
            => new VolumeFace { Kind = face.Kind, SmoothSurfaceId = face.SmoothSurfaceId,
                NormalX = face.NormalX, NormalY = face.NormalY, NormalZ = face.NormalZ,
                Points = face.Points.Select(point => new Point3DModel(point.X - anchor[0],
                    point.Y - anchor[1], point.Z - anchor[2])).ToList() };

        private static void WeldNumericalWallVertices(List<VolumeFace> faces, CancellationToken cancellation)
        {
            // Junction caps can repeat the same endpoint in different contours.
            // Share a bounded representative before union, not after triangulation;
            // this removes numerical slivers without discarding surface triangles.
            var walls = faces.Where(face => face.Kind == "wall").ToArray();
            var buckets = new Dictionary<(long, long, long), List<Point3DModel>>();
            var replacements = new Dictionary<(double, double, double), Point3DModel>();
            const double tolerance = WallJunctionToleranceMillimetres;
            (long X, long Y, long Z) Cell(Point3DModel p) =>
                ((long)Math.Floor(p.X / tolerance), (long)Math.Floor(p.Y / tolerance), (long)Math.Floor(p.Z / tolerance));
            foreach (var key in walls.SelectMany(face => face.Points).Select(p => (p.X, p.Y, p.Z))
                .Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z))
            {
                cancellation.ThrowIfCancellationRequested();
                var point = new Point3DModel(key.X, key.Y, key.Z);
                var cell = Cell(point);
                Point3DModel nearest = null;
                var distanceSquared = tolerance * tolerance;
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    if (buckets.TryGetValue((cell.X + x, cell.Y + y, cell.Z + z), out var candidates))
                        foreach (var candidate in candidates)
                        {
                            var dx = point.X - candidate.X; var dy = point.Y - candidate.Y; var dz = point.Z - candidate.Z;
                            var squared = dx * dx + dy * dy + dz * dz;
                            if (squared <= distanceSquared) { nearest = candidate; distanceSquared = squared; }
                        }
                if (nearest == null)
                {
                    nearest = point;
                    if (!buckets.TryGetValue(cell, out var candidates)) buckets.Add(cell, candidates = new List<Point3DModel>());
                    candidates.Add(point);
                }
                replacements.Add(key, nearest);
            }
            foreach (var face in walls)
                face.Points = face.Points.Select(p => replacements[(p.X, p.Y, p.Z)]).ToList();
        }

        private static IEnumerable<(Vec3 A, Vec3 B, Vec3 C)> Triangulate(
            IEnumerable<VolumeFace> patches, CancellationToken cancellation)
        {
            var face = patches.First();
            var normal = new Vec3 { X = face.NormalX, Y = face.NormalY, Z = face.NormalZ };
            var length = Math.Sqrt(Dot(normal, normal));
            if (!double.IsFinite(length) || length < 1e-6)
                throw new InvalidOperationException("The model contains an invalid face normal.");
            normal = new Vec3 { X = normal.X / length, Y = normal.Y / length, Z = normal.Z / length };
            var union = new Tess();
            foreach (var patch in patches)
            {
                cancellation.ThrowIfCancellationRequested();
                var points = CleanContour(patch.Points.Select(point => new Vec3
                    { X = point.X, Y = point.Y, Z = point.Z }));
                if (points.Length < 3) continue;
                var area = new Vec3();
                for (var i = 1; i + 1 < points.Length; i++)
                {
                    var cross = Cross(Sub(points[i], points[0]), Sub(points[i + 1], points[0]));
                    area.X += cross.X; area.Y += cross.Y; area.Z += cross.Z;
                }
                if (Dot(area, normal) < 0) Array.Reverse(points);
                union.AddContour(points.Select(point => new ContourVertex { Position = point }).ToArray());
            }
            union.Tessellate(WindingRule.NonZero, ElementType.BoundaryContours, normal: normal);
            cancellation.ThrowIfCancellationRequested();
            var tess = new Tess();
            for (var boundary = 0; boundary < union.ElementCount; boundary++)
            {
                cancellation.ThrowIfCancellationRequested();
                var start = union.Elements[boundary * 2];
                var rawCount = union.Elements[boundary * 2 + 1];
                // Union intersections can introduce almost-identical endpoints as
                // well. Weld these before testing collinearity or triangulating.
                var boundaryPoints = CleanContour(Enumerable.Range(0, rawCount)
                    .Select(i => union.Vertices[start + i].Position));
                var count = boundaryPoints.Length;
                var contour = new List<ContourVertex>();
                for (var i = 0; i < count; i++)
                {
                    Vec3 At(int j) => boundaryPoints[(j + count) % count];
                    var incoming = Sub(At(i), At(i - 1));
                    var outgoing = Sub(At(i + 1), At(i));
                    var cross = Cross(incoming, outgoing);
                    if (Dot(incoming, outgoing) >= 0 && Dot(cross, cross)
                        <= 1e-12 * Dot(incoming, incoming) * Dot(outgoing, outgoing)) continue;
                    contour.Add(new ContourVertex { Position = At(i) });
                }
                if (contour.Count >= 3) tess.AddContour(contour.ToArray());
            }
            tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3, normal: normal);
            cancellation.ThrowIfCancellationRequested();
            for (var i = 0; i < tess.ElementCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var ia = tess.Elements[i * 3]; var ib = tess.Elements[i * 3 + 1]; var ic = tess.Elements[i * 3 + 2];
                if (ia == Tess.Undef || ib == Tess.Undef || ic == Tess.Undef)
                    throw new InvalidOperationException("Tessellation returned an incomplete triangle.");
                var a = tess.Vertices[ia].Position; var b = tess.Vertices[ib].Position; var c = tess.Vertices[ic].Position;
                CheckCoordinate(a.X); CheckCoordinate(a.Y); CheckCoordinate(a.Z);
                CheckCoordinate(b.X); CheckCoordinate(b.Y); CheckCoordinate(b.Z);
                CheckCoordinate(c.X); CheckCoordinate(c.Y); CheckCoordinate(c.Z);
                var cross = Cross(Sub(b, a), Sub(c, a));
                if (Dot(cross, cross) < 1e-18) continue;
                if (Dot(cross, normal) < 0) { var swap = b; b = c; c = swap; }
                yield return (a, b, c);
            }
        }

        private static Vec3[] CleanContour(IEnumerable<Vec3> points)
        {
            var result = new List<Vec3>();
            var toleranceSquared = DuplicatePointToleranceMillimetres * DuplicatePointToleranceMillimetres;
            bool Same(Vec3 a, Vec3 b) { var delta = Sub(a, b); return Dot(delta, delta) <= toleranceSquared; }
            foreach (var point in points)
                if (result.Count == 0 || !Same(result[result.Count - 1], point)) result.Add(point);
            if (result.Count > 1 && Same(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            return result.ToArray();
        }

        // Canonical triangle order ignores vertex numbering and cyclic rotations, but
        // retains winding, exact double coordinates, material values and smoothing topology.
        // No coordinate quantization: a missed reuse is safer than merging different sizes.
        private static string Canonicalize(BuildingExportMesh mesh, List<BuildingExportMaterial> materials,
            CancellationToken cancellation)
        {
            var keys = new List<string>(mesh.Triangles.Count);
            foreach (var triangle in mesh.Triangles)
            {
                cancellation.ThrowIfCancellationRequested();
                string Point(int index) => string.Join(",", mesh.Vertices[index].Select(Number));
                var a = Point(triangle.A); var b = Point(triangle.B); var c = Point(triangle.C);
                var rotations = new[] { a + ";" + b + ";" + c, b + ";" + c + ";" + a, c + ";" + a + ";" + b };
                Array.Sort(rotations, StringComparer.Ordinal);
                var material = materials[triangle.MaterialIndex];
                keys.Add(material.Name.Length + ":" + material.Name + ":" + Number(material.Red) + ","
                    + Number(material.Green) + "," + Number(material.Blue) + "," + Number(material.Opacity)
                    + ":" + rotations[0]);
            }
            // Smoothing identities belong to mesh geometry, never to a source element ID.
            var surfaces = mesh.Triangles.Select((triangle, index) => (triangle, index))
                .Where(item => !string.IsNullOrEmpty(item.triangle.SmoothSurfaceId))
                .GroupBy(item => item.triangle.SmoothSurfaceId, StringComparer.Ordinal)
                .Select(group => (Original: group.Key, Signature: string.Join("\n",
                    group.Select(item => keys[item.index]).OrderBy(key => key, StringComparer.Ordinal))))
                .OrderBy(item => item.Signature, StringComparer.Ordinal).ToArray();
            var surfaceIds = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < surfaces.Length; i++)
                surfaceIds.Add(surfaces[i].Original, "surface-" + i.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < mesh.Triangles.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var triangle = mesh.Triangles[i];
                triangle.SmoothSurfaceId = string.IsNullOrEmpty(triangle.SmoothSurfaceId) ? null
                    : surfaceIds[triangle.SmoothSurfaceId];
                keys[i] += ":" + (triangle.SmoothSurfaceId ?? "hard");
            }
            keys.Sort(StringComparer.Ordinal);
            return string.Join("\n", keys);
        }

        private static string Number(double value) => (value == 0d ? 0d : value).ToString("R", CultureInfo.InvariantCulture);
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        private static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3 { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3 { X = a.Y * b.Z - a.Z * b.Y,
            Y = a.Z * b.X - a.X * b.Z, Z = a.X * b.Y - a.Y * b.X };

        private static BuildingExportMaterial Material(string kind)
        {
            var color = kind == "slab" || kind == "bay-cap" || kind == "roof" ? new[] { 0.6d, 0.65d, 0.7d, 1d }
                : kind == "glass" ? new[] { 0.35d, 0.65d, 0.78d, 0.35d }
                : kind == "wall" ? new[] { 0.48d, 0.66d, 0.8d, 1d }
                : new[] { 0.65d, 0.7d, 0.75d, 1d };
            return new BuildingExportMaterial { Name = kind, Red = color[0], Green = color[1], Blue = color[2], Opacity = color[3] };
        }
    }
}
