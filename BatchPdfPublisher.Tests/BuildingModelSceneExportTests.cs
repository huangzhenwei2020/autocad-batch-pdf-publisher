using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BatchPdfPublisher.BuildingModel;

internal static class BuildingModelSceneExportTests
{
    public static void Run()
    {
        CheckScopeIdentityAndMaterials();
        CheckStandardFloorReuse();
        CheckReuseRequiresGeometryAndMaterials();
        CheckLargeCoordinates();
        CheckNumericalContourDuplicates();
        CheckWallHoleAndIndependentWalls();
        CheckSlabHolesAndConcavity();
        CheckCurvesAndStaticPose();
        CheckRejectionsAndCancellation();
        Console.WriteLine("PASS neutral static scene: scope, identity, reuse, origin, holes, winding, curves and cancellation");
    }

    private static void CheckScopeIdentityAndMaterials()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var before = BuildingModelJson.ToJson(model);
        var scene = BuildingExportSceneBuilder.Build(model);
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
        Assert(before == BuildingModelJson.ToJson(model), "Scene export edited the source model or wall codes.");
        Assert(scene.Name == model.Name && scene.Storeys.Count == 2, "Scene/building identity was lost.");
        Assert(scene.Materials.Select(material => material.Name).Distinct().Count() == scene.Materials.Count,
            "Material kinds were duplicated.");
        foreach (var material in scene.Materials)
        {
            var expected = material.Name == "slab" || material.Name == "roof" || material.Name == "bay-cap"
                ? new[] { 0.6, 0.65, 0.7, 1d } : material.Name == "glass" ? new[] { 0.35, 0.65, 0.78, 0.35 }
                : material.Name == "wall" ? new[] { 0.48, 0.66, 0.8, 1d } : new[] { 0.65, 0.7, 0.75, 1d };
            Assert(new[] { material.Red, material.Green, material.Blue, material.Opacity }.SequenceEqual(expected),
                "Material color/alpha differs from the GLB palette.");
        }
        Assert(scene.Materials.Any(material => material.Name == "glass" && material.Opacity == 0.35),
            "Fixture/export lost transparent glass.");
        Assert(scene.Instances.All(instance => instance.SourceId == instance.Id), "Ordinary source IDs changed.");
        Assert(scene.Instances.First(instance => instance.Id == model.Walls[0].Id).Name.Contains("W-"),
            "Export did not generate a display wall code on its private clone.");
        Assert(scene.Instances.First(instance => instance.Id == model.Openings[0].Id).Name == model.Openings[0].Code,
            "Opening display name was lost.");
        Assert(!scene.Instances.Any(instance => model.Axes.Any(axis => axis.Id == instance.Id)), "Guides became geometry.");
        var single = BuildingExportSceneBuilder.Build(model, "2f");
        CheckGeometry(single, BuildingVolumeBuilder.Build(model, "2F"));
        Assert(single.Storeys.Count == 1 && single.Storeys[0].Id == "2F"
            && single.Storeys[0].Elevation == 3600 && single.Instances.All(instance => instance.StoreyId == "2F"),
            "A single-storey export leaked another storey or changed its absolute datum.");
        var count = single.TriangleCount;
        single.Instances.Add(single.Instances[0]);
        Assert(single.TriangleCount == count + single.Meshes[single.Instances[0].MeshIndex].Triangles.Count,
            "Calculated triangle count did not include the new shared-mesh instance.");
        Assert(before == BuildingModelJson.ToJson(model), "Single-storey export edited the source model.");
    }

    private static void CheckStandardFloorReuse()
    {
        var model = Floor();
        model.Walls.Add(new WallModel { Id = "wall", StoreyId = "1F", X2 = 6000, Thickness = 240 });
        model.Slabs.Add(new SlabModel { Id = "slab", StoreyId = "1F", Thickness = 150,
            TopOffset = 0, Outline = Rect(0, 0, 6000, 4000) });
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "Second", Elevation = 3000,
            Height = 3000, TemplateStoreyId = "1F" });
        var before = BuildingModelJson.ToJson(model);
        var scene = BuildingExportSceneBuilder.Build(model);
        Assert(scene.Instances.Count == 4 && scene.Meshes.Count == 2, "Identical standard-floor geometry was not reused.");
        foreach (var original in scene.Instances.Where(instance => instance.StoreyId == "1F"))
        {
            var standard = scene.Instances.Single(instance => instance.StoreyId == "2F" && instance.SourceId == original.Id);
            Assert(standard.Id != original.Id && standard.Id == original.Id + "@STD@2F", "Standard-floor instance ID was lost.");
            Assert(standard.SourceId == StandardStoreyLayout.SourceElementId(model, standard.Id), "Standard source ID is incorrect.");
            Assert(standard.MeshIndex == original.MeshIndex && standard.Translation[2] - original.Translation[2] == 3000,
                "Standard mesh reuse lost the independent floor translation.");
        }
        Assert(scene.TriangleCount == scene.Meshes.Sum(mesh => mesh.Triangles.Count) * 2, "Instanced triangle count is incorrect.");
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
        var single = BuildingExportSceneBuilder.Build(model, "2F");
        Assert(single.Instances.All(instance => instance.Id != instance.SourceId), "Single standard floor lost instance/source identity.");
        CheckGeometry(single, BuildingVolumeBuilder.Build(model, "2F"));

        model.Storeys[1].Height = 3300;
        var changed = BuildingExportSceneBuilder.Build(model);
        Assert(changed.Instances.First(instance => instance.Id == "wall").MeshIndex
            != changed.Instances.First(instance => instance.SourceId == "wall" && instance.StoreyId == "2F").MeshIndex,
            "Same source ID merged different storey-height geometry.");
        model.Storeys[1].Height = 3000;
        Assert(before == BuildingModelJson.ToJson(model), "Standard-floor export modified semantic data.");
    }

    private static void CheckReuseRequiresGeometryAndMaterials()
    {
        var model = Floor();
        model.Columns.Add(new ColumnModel { Id = "column-a", StoreyId = "1F", X = 1000, Y = 1000, Width = 400, Depth = 400 });
        model.Columns.Add(new ColumnModel { Id = "column-b", StoreyId = "1F", X = 5000, Y = 1000, Width = 400, Depth = 400 });
        model.Columns.Add(new ColumnModel { Id = "column-c", StoreyId = "1F", X = 9000, Y = 1000, Width = 400.001, Depth = 400 });
        model.Walls.Add(new WallModel { Id = "wall", StoreyId = "1F", X1 = 12000, X2 = 12400, Y1 = 1000, Y2 = 1000, Thickness = 400 });
        var scene = BuildingExportSceneBuilder.Build(model);
        int Mesh(string id) => scene.Instances.Single(instance => instance.Id == id).MeshIndex;
        Assert(Mesh("column-a") == Mesh("column-b"), "Equal geometry with unrelated IDs was not reused.");
        Assert(Mesh("column-a") != Mesh("column-c"), "Different submillimetre dimensions were merged.");
        Assert(Mesh("column-a") != Mesh("wall"), "Different material kinds were merged.");
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
        var repeated = BuildingExportSceneBuilder.Build(model);
        Assert(scene.Meshes.Select(mesh => mesh.Id).SequenceEqual(repeated.Meshes.Select(mesh => mesh.Id)), "Mesh hashes are unstable.");
        model.Columns.Reverse();
        var reordered = BuildingExportSceneBuilder.Build(model);
        foreach (var instance in scene.Instances)
        {
            var other = reordered.Instances.Single(item => item.Id == instance.Id);
            Assert(scene.Meshes[instance.MeshIndex].Id == reordered.Meshes[other.MeshIndex].Id,
                "Canonical mesh identity depends on element/material enumeration order.");
        }
    }

    private static void CheckLargeCoordinates()
    {
        const double x = 1_000_000_000.125, y = -2_000_000_000.25, z = 123456.125;
        var model = Floor(z);
        model.Columns.Add(new ColumnModel { Id = "column", StoreyId = "1F", X = x, Y = y,
            Width = 400.125, Depth = 300.0625, Height = 2800.03125, BaseOffset = 12.0625 });
        var before = BuildingModelJson.ToJson(model);
        var near = BuildingExportSceneBuilder.Build(model);
        var world = BuildingExportSceneBuilder.Build(model, nearOrigin: false);
        CheckGeometry(near, BuildingVolumeBuilder.Build(model));
        CheckGeometry(world, BuildingVolumeBuilder.Build(model));
        Assert(world.OriginMillimetres.All(value => value == 0), "Original-coordinate mode has an origin offset.");
        Assert(near.OriginMillimetres[0] > 999_000_000 && near.OriginMillimetres[1] < -1_999_000_000,
            "Near-origin mode did not retain the CAD world offset.");
        Assert(near.Meshes[0].Id == world.Meshes[0].Id, "Origin mode changed local mesh geometry.");
        Assert(near.Meshes.SelectMany(mesh => mesh.Vertices).All(point => point.All(value => Math.Abs(value) < 5000)),
            "Large world coordinates reached tessellation/local mesh storage.");
        var vertices = near.Meshes[0].Vertices;
        Close(vertices.Max(point => point[0]) - vertices.Min(point => point[0]), 400.125, 1e-8, "Double-precision width was lost.");
        Close(vertices.Min(point => point[2]), 12.0625, 1e-8, "Element Z anchor is not its storey elevation.");
        Close(vertices.Max(point => point[2]) - vertices.Min(point => point[2]), 2800.03125, 1e-8, "Double-precision height was lost.");
        var shifted = Floor();
        shifted.Columns.Add(new ColumnModel { Id = "column", StoreyId = "1F", Width = 400.125,
            Depth = 300.0625, Height = 2800.03125, BaseOffset = 12.0625 });
        Assert(near.Meshes[0].Id == BuildingExportSceneBuilder.Build(shifted).Meshes[0].Id, "Large offsets changed canonical local geometry.");
        Assert(before == BuildingModelJson.ToJson(model), "Origin handling modified model-world data.");
    }

    private static void CheckNumericalContourDuplicates()
    {
        // Synthetic wall-cap union: CAD intersection arithmetic can create tiny
        // consecutive edges. Test the tessellator without private project data.
        var triangulate = typeof(BuildingExportSceneBuilder).GetMethod("Triangulate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        VolumeFace Cap(params (double X, double Y)[] points) => new VolumeFace
        {
            Kind = "wall", NormalZ = 1,
            Points = points.Select(p => new Point3DModel(p.X, p.Y, 0)).ToList()
        };
        var fixtures = new[]
        {
            new[] { Cap((0, 0), (4000, 0), (4000 + 5e-10, 0), (4000, 3000),
                (0, 3000), (0, 5e-10)) },
            new[] { Cap((0, 0), (2100, 0), (2100, 3000), (0, 3000)),
                Cap((2100 + 5e-10, 0), (4000, 0), (4000, 3000), (2100, 3000)) },
            new[] { Cap((0, 0), (4000.001, 0), (4000.001, 3000), (0, 3000)) }
        };
        foreach (var faces in fixtures)
        {
            var raw = ((IEnumerable<(LibTessDotNet.Double.Vec3 A, LibTessDotNet.Double.Vec3 B,
                LibTessDotNet.Double.Vec3 C)>)triangulate.Invoke(null,
                    new object[] { faces, CancellationToken.None })!).ToArray();
            Assert(raw.Length > 0, "Duplicate contour cleanup removed the wall cap.");
            var triangles = raw.Select(t => new[] { new[] { t.A.X, t.A.Y, t.A.Z },
                new[] { t.B.X, t.B.Y, t.B.Z }, new[] { t.C.X, t.C.Y, t.C.Z } }).ToArray();
            foreach (var t in triangles)
            {
                System.Numerics.Vector3 Single(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);
                Assert(System.Numerics.Vector3.Cross(Single(t[1]) - Single(t[0]),
                    Single(t[2]) - Single(t[0])).LengthSquared() > 0,
                    "Numerical duplicate endpoints survived into a collapsed FBX triangle.");
            }
            var width = faces.Max(face => face.Points.Max(point => point.X));
            Close(triangles.Sum(Area), width * 3000, 0.01, "Cleanup changed wall cap surface area.");
            Close(triangles.SelectMany(t => t).Max(p => p[0]), width, 0.000002,
                "Cleanup changed a real submillimetre dimension.");
        }
    }

    private static void CheckWallHoleAndIndependentWalls()
    {
        var model = Floor();
        model.Walls.Add(new WallModel { Id = "host", StoreyId = "1F", X2 = 6000, Thickness = 240 });
        model.Openings.Add(new OpeningModel { Id = "hole", HostWallId = "host", Kind = "洞口", Code = "hole",
            Offset = 2500, Width = 1800, Height = 1200, Sill = 900 });
        var scene = BuildingExportSceneBuilder.Build(model);
        var front = Triangles(scene, scene.Instances.Single(instance => instance.Id == "host"))
            .Where(triangle => triangle.All(point => Math.Abs(point[1] + 120) < 1e-6)).ToArray();
        Close(front.Sum(Area), 6000d * 3000 - 1800d * 1200, 0.01, "Wall hole area changed.");
        foreach (var point in new[] { new[] { 1700d, 1000d }, new[] { 2500d, 1500d }, new[] { 3300d, 2000d } })
            Assert(!front.Any(triangle => Contains(triangle, point[0], point[1], 0, 2)), "A wall triangle fills the opening.");
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));

        model.Openings.Clear();
        model.Walls.Add(new WallModel { Id = "neighbor", StoreyId = "1F", Y2 = 5000, Thickness = 240 });
        var initial = BuildingExportSceneBuilder.Build(model);
        model.Openings.Add(new OpeningModel { Id = "window", HostWallId = "host", Code = "C1518",
            Offset = 2500, Width = 1500, Height = 1800, Sill = 800 });
        var changed = BuildingExportSceneBuilder.Build(model);
        var oldMesh = initial.Meshes[initial.Instances.Single(instance => instance.Id == "neighbor").MeshIndex];
        var newMesh = changed.Meshes[changed.Instances.Single(instance => instance.Id == "neighbor").MeshIndex];
        Assert(oldMesh.Id == newMesh.Id && oldMesh.Triangles.Count == newMesh.Triangles.Count,
            "An opening in one wall changed an independent neighboring wall's mesh.");
        CheckGeometry(changed, BuildingVolumeBuilder.Build(model));
    }

    private static void CheckSlabHolesAndConcavity()
    {
        var model = Floor(3600);
        model.Slabs.Add(new SlabModel { Id = "slab", StoreyId = "1F", TopOffset = 0, Thickness = 180,
            Outline = Rect(0, 0, 6000, 5000), Openings = new List<SlabOpeningModel> {
                new SlabOpeningModel { Id = "stair", Outline = Rect(1000, 1000, 2500, 3000) },
                new SlabOpeningModel { Id = "lift", Outline = Rect(3500, 1000, 4500, 3000) } } });
        var scene = BuildingExportSceneBuilder.Build(model);
        var top = Triangles(scene, scene.Instances.Single()).Where(triangle => Cross(triangle)[2] > 0).ToArray();
        Close(top.Sum(Area), 25_000_000, 0.01, "Slab holes did not preserve the actual 25 square metre top area.");
        Assert(!top.Any(triangle => Contains(triangle, 1700, 1800, 0, 1)
            || Contains(triangle, 3900, 1800, 0, 1)), "A slab triangle spans a shaft opening.");
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
        model.Slabs[0].Openings.Clear();
        model.Slabs[0].Outline = new List<PointModel> { new PointModel(0, 0), new PointModel(2000, 0),
            new PointModel(2000, 1000), new PointModel(1000, 1000), new PointModel(1000, 2000), new PointModel(0, 2000) };
        scene = BuildingExportSceneBuilder.Build(model);
        top = Triangles(scene, scene.Instances.Single()).Where(triangle => Cross(triangle)[2] > 0).ToArray();
        Close(top.Sum(Area), 3_000_000, 0.01, "Concave slab area changed.");
        Assert(!top.Any(triangle => Contains(triangle, 1500, 1500, 0, 1)), "Concave slab notch was filled.");
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
    }

    private static void CheckCurvesAndStaticPose()
    {
        var model = Floor();
        model.Walls.Add(new WallModel { Id = "arc", StoreyId = "1F", X1 = 1000, X2 = -1000, Bulge = 1, Thickness = 200 });
        model.Slabs.Add(new SlabModel { Id = "round", StoreyId = "1F", Thickness = 150, TopOffset = 150,
            Outline = new List<PointModel> { new PointModel(1100, 0) { Bulge = 1 }, new PointModel(-1100, 0),
                new PointModel(-1100, -1000), new PointModel(1100, -1000) } });
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "Standard", Elevation = 3000,
            Height = 3000, TemplateStoreyId = "1F" });
        var before = BuildingModelJson.ToJson(model);
        var scene = BuildingExportSceneBuilder.Build(model);
        CheckGeometry(scene, BuildingVolumeBuilder.Build(model));
        Assert(scene.Meshes.Count == 2 && scene.Instances.Count == 4, "Curved standard-floor meshes were not reused.");
        Assert(scene.Meshes.All(mesh => mesh.Triangles.Any(triangle => triangle.SmoothSurfaceId != null)),
            "Curved surfaces lost smoothing identities.");
        Assert(scene.Meshes.SelectMany(mesh => mesh.Triangles).Where(triangle => triangle.SmoothSurfaceId != null)
            .All(triangle => !triangle.SmoothSurfaceId.Contains("arc") && !triangle.SmoothSurfaceId.Contains("round")),
            "Reusable smoothing IDs still reference source element IDs.");
        Assert(before == BuildingModelJson.ToJson(model), "Curve export edited the source model.");

        var posed = SampleModelFactory.CreateTwoStoreyHouse();
        var door = posed.Openings.Single(opening => opening.Id == "1F-S-M0921");
        door.OpenIn3D = true; door.PlanOpenAngle = 47; door.PlanFlipAlong = true; door.PlanFlipNormal = true;
        before = BuildingModelJson.ToJson(posed);
        scene = BuildingExportSceneBuilder.Build(posed);
        CheckGeometry(scene, BuildingVolumeBuilder.Build(posed));
        Assert(scene.Instances.Count(instance => instance.SourceId == door.Id) == 1, "Static export split a door into leaf-part instances.");
        var openMeshId = scene.Meshes[scene.Instances.Single(instance => instance.Id == door.Id).MeshIndex].Id;
        Assert(before == BuildingModelJson.ToJson(posed), "Current-pose export edited opening state.");
        door.OpenIn3D = false;
        var closed = BuildingExportSceneBuilder.Build(posed);
        Assert(openMeshId != closed.Meshes[closed.Instances.Single(instance => instance.Id == door.Id).MeshIndex].Id,
            "Current open and closed poses produced the same geometry.");
    }

    private static void CheckRejectionsAndCancellation()
    {
        Expect<ArgumentNullException>(() => BuildingExportSceneBuilder.Build(null));
        var model = Floor();
        Expect<ArgumentException>(() => BuildingExportSceneBuilder.Build(model, "missing"));
        Expect<ArgumentException>(() => BuildingExportSceneBuilder.Build(model, " "));
        Expect<InvalidOperationException>(() => BuildingExportSceneBuilder.Build(model));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Expect<OperationCanceledException>(() => BuildingExportSceneBuilder.Build(model, cancellation: cancellation.Token));
        }
        model.Columns.Add(new ColumnModel { Id = "one", StoreyId = "1F", Width = 400, Depth = 400 });
        model.Storeys.Add(new StoreyModel { Id = "empty", Elevation = 3000, Height = 3000 });
        Expect<InvalidOperationException>(() => BuildingExportSceneBuilder.Build(model, "empty"));
        model.Columns.Add(new ColumnModel { Id = "far", StoreyId = "1F", X = 20_000_000, Width = 400, Depth = 400 });
        Expect<InvalidOperationException>(() => BuildingExportSceneBuilder.Build(model));
        model.Columns.RemoveAt(1);
        model.Columns[0].X = 2_000_000_000_000;
        Expect<InvalidOperationException>(() => BuildingExportSceneBuilder.Build(model));
    }

    private static void CheckGeometry(BuildingExportScene scene, BuildingVolume volume)
    {
        var groups = volume.Faces.GroupBy(face => (face.StoreyId, face.ElementId)).ToArray();
        Assert(scene.Instances.Count == groups.Length, "Independent semantic elements were merged or lost.");
        Assert(scene.Instances.Select(instance => (instance.StoreyId, instance.Id)).Distinct().Count() == groups.Length,
            "Duplicate instance identities.");
        var expectedPoints = volume.Faces.SelectMany(face => face.Points).Select(Point).ToArray();
        var allTriangles = scene.Instances.SelectMany(instance => Triangles(scene, instance)).ToArray();
        var actualPoints = allTriangles.SelectMany(triangle => triangle).ToArray();
        Assert(scene.TriangleCount == allTriangles.Length, "Triangle count differs from instantiated geometry.");
        for (var axis = 0; axis < 3; axis++)
        {
            Close(actualPoints.Min(point => point[axis]), expectedPoints.Min(point => point[axis]), 1e-5, "World minimum/axis changed.");
            Close(actualPoints.Max(point => point[axis]), expectedPoints.Max(point => point[axis]), 1e-5, "World maximum/axis changed.");
        }
        foreach (var group in groups)
        {
            var instance = scene.Instances.Single(item => item.Id == group.Key.ElementId && item.StoreyId == group.Key.StoreyId);
            var triangles = Triangles(scene, instance).ToArray();
            var expectedArea = group.Sum(FaceArea);
            Close(triangles.Sum(Area), expectedArea, Math.Max(0.01, expectedArea * 1e-8), "Element surface area changed: " + instance.Id);
            foreach (var triangle in triangles)
            {
                var cross = Cross(triangle);
                var length = Math.Sqrt(Dot(cross, cross));
                Assert(double.IsFinite(length) && length > 1e-9, "Degenerate or invalid triangle.");
                Assert(group.Any(face => Dot(cross, new[] { face.NormalX, face.NormalY, face.NormalZ }) / length > 0.99999
                    && Math.Abs(Dot(Sub(triangle[0], Point(face.Points[0])), new[] { face.NormalX, face.NormalY, face.NormalZ })) < 0.001),
                    "Triangle winding does not agree with an outward source-face normal: " + instance.Id);
            }
            Assert(scene.Meshes[instance.MeshIndex].Triangles.All(triangle => triangle.MaterialIndex >= 0
                && triangle.MaterialIndex < scene.Materials.Count), "Invalid material reference.");
        }
    }

    private static IEnumerable<double[][]> Triangles(BuildingExportScene scene, BuildingExportInstance instance)
    {
        var mesh = scene.Meshes[instance.MeshIndex];
        double[] World(int index) => mesh.Vertices[index].Select((value, axis) => value
            + instance.Translation[axis] + scene.OriginMillimetres[axis]).ToArray();
        foreach (var triangle in mesh.Triangles) yield return new[] { World(triangle.A), World(triangle.B), World(triangle.C) };
    }

    private static bool Contains(double[][] triangle, double x, double y, int axisX, int axisY)
    {
        double Edge(double[] a, double[] b) => (b[axisX] - a[axisX]) * (y - a[axisY]) - (b[axisY] - a[axisY]) * (x - a[axisX]);
        var signs = new[] { Edge(triangle[0], triangle[1]), Edge(triangle[1], triangle[2]), Edge(triangle[2], triangle[0]) };
        return signs.All(value => value >= -1e-6) || signs.All(value => value <= 1e-6);
    }

    private static double FaceArea(VolumeFace face)
    {
        var origin = Point(face.Points[0]);
        var area = new double[3];
        for (var i = 1; i + 1 < face.Points.Count; i++)
        {
            var cross = Cross(new[] { origin, Point(face.Points[i]), Point(face.Points[i + 1]) });
            for (var axis = 0; axis < 3; axis++) area[axis] += cross[axis];
        }
        return Math.Sqrt(Dot(area, area)) / 2;
    }

    private static BuildingModelDocument Floor(double elevation = 0)
        => new BuildingModelDocument { Name = "Synthetic scene", Storeys = new List<StoreyModel>
            { new StoreyModel { Id = "1F", Name = "First", Elevation = elevation, Height = 3000 } } };
    private static List<PointModel> Rect(double x0, double y0, double x1, double y1)
        => new List<PointModel> { new PointModel(x0, y0), new PointModel(x1, y0), new PointModel(x1, y1), new PointModel(x0, y1) };
    private static double[] Point(Point3DModel point) => new[] { point.X, point.Y, point.Z };
    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Cross(double[][] triangle)
    {
        var a = Sub(triangle[1], triangle[0]); var b = Sub(triangle[2], triangle[0]);
        return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
    private static double Area(double[][] triangle) { var cross = Cross(triangle); return Math.Sqrt(Dot(cross, cross)) / 2; }
    private static void Close(double actual, double expected, double tolerance, string message)
        => Assert(Math.Abs(actual - expected) <= tolerance, message + " (" + actual + " / " + expected + ")");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
