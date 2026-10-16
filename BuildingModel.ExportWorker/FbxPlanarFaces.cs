using System.Numerics;
using Assimp;

namespace BatchPdfPublisher.BuildingModel.Export;

/// <summary>
/// Dissolves transport edges within one material/element, not geometric decimation.
/// Only a single convex boundary is accepted; holes and reflex corners keep cuts.
/// Assimp remains responsible for serializing and reading the native FBX polygons.
/// </summary>
internal static class FbxPlanarFaces
{
    private const int MaximumCorners = 512;
    private readonly record struct Edge(int A, int B)
    {
        internal static Edge Of(int a, int b) => a < b ? new(a, b) : new(b, a);
    }

    private sealed record Polygon(int[] Corners, string Surface, V3 Normal, double Area);
    private readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        internal static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        internal double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
        internal double Length => Math.Sqrt(Dot(this));
        internal V3 Unit => new(X / Length, Y / Length, Z / Length);
    }

    internal static int Merge(Mesh mesh, IReadOnlyList<string> surfaces, CancellationToken cancellation)
    {
        if (surfaces.Count != mesh.FaceCount) throw new ArgumentException("Missing planar face identities.");
        var originalCount = mesh.FaceCount;
        // FBX vertices may be split for authored normals. Match exact serialized
        // positions for adjacency without moving/welding any geometric position.
        var positions = new Dictionary<Vector3, int>();
        var canonical = new int[mesh.VertexCount];
        for (var i = 0; i < canonical.Length; i++)
        {
            if (!positions.TryGetValue(mesh.Vertices[i], out var id))
                positions.Add(mesh.Vertices[i], id = positions.Count);
            canonical[i] = id;
        }
        V3 Point(int i) { var p = mesh.Vertices[i]; return new(p.X, p.Y, p.Z); }
        V3 AreaVector(int[] corners)
        {
            var area = new V3(); var start = Point(corners[0]);
            for (var i = 1; i + 1 < corners.Length; i++)
                area += V3.Cross(Point(corners[i]) - start, Point(corners[i + 1]) - start);
            return area;
        }
        var polygons = mesh.Faces.Select((f, i) =>
        {
            var corners = f.Indices.ToArray(); var area = AreaVector(corners);
            return new Polygon(corners, surfaces[i] ?? "", area.Unit, area.Length / 2);
        }).Cast<Polygon?>().ToArray();
        var uses = new Dictionary<Edge, HashSet<int>>();
        var queue = new Queue<Edge>();
        var pending = new HashSet<Edge>();
        IEnumerable<Edge> Edges(Polygon polygon)
        {
            for (var i = 0; i < polygon.Corners.Length; i++)
                yield return Edge.Of(canonical[polygon.Corners[i]], canonical[polygon.Corners[(i + 1) % polygon.Corners.Length]]);
        }
        void Add(int id)
        {
            foreach (var edge in Edges(polygons[id]!))
            {
                if (!uses.TryGetValue(edge, out var faces)) uses.Add(edge, faces = new());
                faces.Add(id);
                if (pending.Add(edge)) queue.Enqueue(edge);
            }
        }
        void Remove(int id)
        {
            foreach (var edge in Edges(polygons[id]!))
                if (uses.TryGetValue(edge, out var faces)) { faces.Remove(id); if (faces.Count == 0) uses.Remove(edge); }
        }
        Polygon? Join(Polygon a, Polygon b)
        {
            if (a.Surface != b.Surface || a.Normal.Dot(b.Normal) < 1 - 1e-10
                || a.Corners.Length + b.Corners.Length > MaximumCorners + 2) return null;
            var plane = Point(a.Corners[0]);
            var extent = a.Corners.Concat(b.Corners).Max(i => (Point(i) - plane).Length);
            var tolerance = Math.Max(0.00001, extent * 2e-7);
            if (b.Corners.Any(i => Math.Abs((Point(i) - plane).Dot(a.Normal)) > tolerance)) return null;
            var boundary = new Dictionary<(int A, int B), int>();
            foreach (var polygon in new[] { a, b })
                for (var i = 0; i < polygon.Corners.Length; i++)
                {
                    var start = polygon.Corners[i]; var end = polygon.Corners[(i + 1) % polygon.Corners.Length];
                    var from = canonical[start]; var to = canonical[end];
                    if (from == to) return null;
                    if (boundary.Remove((to, from))) continue;
                    if (!boundary.TryAdd((from, to), start)) return null;
                }
            if (boundary.Count < 3 || boundary.Count > MaximumCorners) return null;
            var next = new Dictionary<int, (int Vertex, int To)>();
            var incoming = new HashSet<int>();
            foreach (var edge in boundary)
                if (!next.TryAdd(edge.Key.A, (edge.Value, edge.Key.B)) || !incoming.Add(edge.Key.B)) return null;
            var first = boundary.First().Key.A; var current = first;
            var seen = new HashSet<int>(); var loop = new List<int>();
            while (seen.Add(current))
            {
                if (!next.TryGetValue(current, out var step)) return null;
                loop.Add(step.Vertex); current = step.To;
            }
            // Multiple cycles or a repeated vertex imply a hole/self-touch. Keep
            // the existing partition instead of accidentally capping the opening.
            if (current != first || loop.Count != boundary.Count) return null;
            var corners = loop.ToArray(); var area = AreaVector(corners);
            var sum = a.Area + b.Area;
            if (area.Dot(a.Normal) <= 0 || Math.Abs(area.Length / 2 - sum) > Math.Max(0.00001, sum * 1e-8)) return null;
            // A concave L/U-shaped cap leaves triangulation up to the target DCC.
            // Keep a real partition at each reflex corner instead of exporting
            // that fragile n-gon. The cut endpoints keep their shared positions.
            for (var i = 0; i < corners.Length; i++)
            {
                var edgeIn = Point(corners[i]) - Point(corners[(i + corners.Length - 1) % corners.Length]);
                var edgeOut = Point(corners[(i + 1) % corners.Length]) - Point(corners[i]);
                var turn = V3.Cross(edgeIn, edgeOut).Dot(a.Normal);
                var turnTolerance = Math.Max(1e-8, edgeIn.Length * edgeOut.Length * 1e-8);
                if (turn < -turnTolerance
                    || (Math.Abs(turn) <= turnTolerance && edgeIn.Dot(edgeOut) < 0)) return null;
            }
            return new(corners, a.Surface, a.Normal, sum);
        }
        for (var i = 0; i < polygons.Length; i++) Add(i);
        while (queue.TryDequeue(out var edge))
        {
            cancellation.ThrowIfCancellationRequested();
            pending.Remove(edge);
            if (!uses.TryGetValue(edge, out var faces) || faces.Count != 2) continue;
            var ids = faces.OrderBy(i => i).ToArray();
            var joined = Join(polygons[ids[0]]!, polygons[ids[1]]!);
            if (joined == null) continue;
            Remove(ids[0]); Remove(ids[1]);
            polygons[ids[0]] = joined; polygons[ids[1]] = null;
            Add(ids[0]);
        }
        mesh.Faces.Clear();
        foreach (var polygon in polygons)
            if (polygon != null) mesh.Faces.Add(new Face(polygon.Corners));
        // Dissolving a fan can leave its centre unused. Do not serialize loose
        // transport vertices, but retain per-corner normal seams until FBX writes.
        var compact = new Dictionary<int, int>();
        var compactPoints = new List<Vector3>();
        var compactNormals = new List<Vector3>();
        foreach (var face in mesh.Faces)
            for (var i = 0; i < face.IndexCount; i++)
            {
                var original = face.Indices[i];
                if (!compact.TryGetValue(original, out var index))
                {
                    compact.Add(original, index = compactPoints.Count);
                    compactPoints.Add(mesh.Vertices[original]);
                    compactNormals.Add(mesh.Normals[original]);
                }
                face.Indices[i] = index;
            }
        mesh.Vertices.Clear(); mesh.Vertices.AddRange(compactPoints);
        mesh.Normals.Clear(); mesh.Normals.AddRange(compactNormals);
        mesh.PrimitiveType = (mesh.Faces.Any(f => f.IndexCount == 3) ? PrimitiveType.Triangle : 0)
            | (mesh.Faces.Any(f => f.IndexCount > 3) ? PrimitiveType.Polygon : 0);
        return originalCount - mesh.FaceCount;
    }
}
