using System.Numerics;
using Assimp;
using BatchPdfPublisher.BuildingModel.Export;

internal static class FbxPlanarFacesTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Run()
    {
        Mesh Make(Vector3[] points, params int[][] faces)
        {
            var mesh = new Mesh("fixture", PrimitiveType.Triangle);
            mesh.Vertices.AddRange(points);
            mesh.Normals.AddRange(points.Select(_ => Vector3.UnitZ));
            mesh.Faces.AddRange(faces.Select(f => new Face(f)));
            return mesh;
        }
        void Merge(Mesh mesh, string[]? groups = null) => FbxPlanarFaces.Merge(mesh,
            groups ?? Enumerable.Repeat("", mesh.FaceCount).ToArray(), CancellationToken.None);
        var square = new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 4, 0), new Vector3(0, 4, 0) };
        var split = Make(new[] { square[0], square[1], square[2], square[0], square[2], square[3] }, new[] { 0, 1, 2 }, new[] { 3, 4, 5 });
        Merge(split);
        Check(split.FaceCount == 1 && split.Faces[0].IndexCount == 4, "Normal-split vertices prevented coplanar merging.");
        var fan = Make(square.Concat(new[] { new Vector3(2, 2, 0) }).ToArray(),
            new[] { 0, 1, 4 }, new[] { 1, 2, 4 }, new[] { 2, 3, 4 }, new[] { 3, 0, 4 });
        Merge(fan);
        Check(fan.FaceCount == 1 && fan.Faces[0].IndexCount == 4, "Multi-edge merge left an interior fan vertex.");
        // A bent frame cap must retain a cut at the inner corner. Exercise both
        // windings and oblique 3D planes; don't depend on a particular world axis.
        var bent = new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 1, 0),
            new Vector3(1, 1, 0), new Vector3(1, 4, 0), new Vector3(0, 4, 0) };
        foreach (var mirrored in new[] { false, true })
        foreach (var rotated in new[] { false, true })
        {
            var transform = rotated ? Matrix4x4.CreateFromYawPitchRoll(0.43f, -0.65f, 0.27f) : Matrix4x4.Identity;
            var points = bent.Select(p => Vector3.Transform(p, transform)).ToArray();
            var faces = new[] { new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 0, 3, 5 }, new[] { 3, 4, 5 } };
            if (mirrored) foreach (var face in faces) Array.Reverse(face);
            var frame = Make(points, faces);
            Merge(frame);
            Check(frame.FaceCount > 1 && frame.FaceCount < faces.Length,
                "A bent cap either lost its corner cut or retained all triangulation.");
            Check(frame.Faces.Any(f => f.Indices.Any(i => frame.Vertices[i] == points[3])),
                "The inner frame corner was removed.");
            foreach (var face in frame.Faces)
            {
                var loop = face.Indices.Select(i => frame.Vertices[i]).ToArray();
                var normal = Vector3.TransformNormal(mirrored ? -Vector3.UnitZ : Vector3.UnitZ, transform);
                for (var i = 0; i < loop.Length; i++)
                {
                    var a = loop[i] - loop[(i + loop.Length - 1) % loop.Length];
                    var b = loop[(i + 1) % loop.Length] - loop[i];
                    Check(Vector3.Dot(Vector3.Cross(a, b), normal) >= -1e-5,
                        "A reflex corner survived into an exported polygon.");
                }
            }
            var edgeUses = frame.Faces.SelectMany(f => Enumerable.Range(0, f.IndexCount).Select(i =>
                (A: f.Indices[i], B: f.Indices[(i + 1) % f.IndexCount])))
                .GroupBy(e => e.A < e.B ? (e.A, e.B) : (e.B, e.A)).ToArray();
            Check(edgeUses.Any(e => e.Count() == 2 && e.Any(v => frame.Vertices[v.A] == points[3]
                || frame.Vertices[v.B] == points[3])), "The corner cut is not shared/welded by both adjacent faces.");
        }
        var ring = Make(square.Concat(new[] { new Vector3(1, 1, 0), new Vector3(3, 1, 0), new Vector3(3, 3, 0), new Vector3(1, 3, 0) }).ToArray(),
            new[] { 0, 1, 5 }, new[] { 0, 5, 4 }, new[] { 1, 2, 6 }, new[] { 1, 6, 5 }, new[] { 2, 3, 7 }, new[] { 2, 7, 6 }, new[] { 3, 0, 4 }, new[] { 3, 4, 7 });
        Merge(ring);
        Check(ring.FaceCount >= 2 && ring.FaceCount < 8, "Ring did not merge conservatively around its hole.");
        double Area(Mesh mesh) => mesh.Faces.Sum(face =>
        {
            var start = mesh.Vertices[face.Indices[0]]; var area = Vector3.Zero;
            for (var i = 1; i + 1 < face.IndexCount; i++) area += Vector3.Cross(mesh.Vertices[face.Indices[i]] - start, mesh.Vertices[face.Indices[i + 1]] - start);
            return area.Z / 2d;
        });
        Check(Math.Abs(Area(ring) - 12) < 1e-8, "Merged polygons capped or overlapped the hole.");
        foreach (var face in ring.Faces)
        {
            var inside = false; var p = new Vector2(1.4f, 1.6f);
            for (int i = 0, j = face.IndexCount - 1; i < face.IndexCount; j = i++)
            {
                var a = ring.Vertices[face.Indices[i]]; var b = ring.Vertices[face.Indices[j]];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            Check(!inside, "A polygon covers the opening.");
        }
        var sharp = Make(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }, new[] { 0, 1, 2 }, new[] { 1, 0, 3 });
        Merge(sharp); Check(sharp.FaceCount == 2, "A real corner was flattened.");
        var smoothBoundary = Make(square, new[] { 0, 1, 2 }, new[] { 0, 2, 3 });
        Merge(smoothBoundary, new[] { "surface-a", "surface-b" });
        Check(smoothBoundary.FaceCount == 2, "Distinct authored smooth surfaces were merged.");
        var separate = Make(square.Concat(square.Select(v => v + new Vector3(4.001f, 0, 0))).ToArray(),
            new[] { 0, 1, 2 }, new[] { 0, 2, 3 }, new[] { 4, 5, 6 }, new[] { 4, 6, 7 });
        Merge(separate); Check(separate.FaceCount == 2, "Distinct parts were welded across a gap.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { FbxPlanarFaces.Merge(sharp, new[] { "", "" }, cancel.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("FBX_PLANAR_FACES_OK quads fan holes area convexCornerCuts mirroredOblique weldedCuts surfaceBoundaries separatedParts cancellation");
    }
}
