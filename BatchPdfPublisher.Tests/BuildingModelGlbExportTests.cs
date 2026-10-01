using System.Numerics;
using BatchPdfPublisher.BuildingModel;
using SharpGLTF.Schema2;

internal static class BuildingModelGlbExportTests
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static int Main(string[] args)
    {
        var folder = Path.Combine(Path.GetTempPath(), "WanLuoGlbTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            if (args.Length == 2 && args[0] == "--cad-fixture")
            {
                var plan = new ViewDocument { Id = "qa-plan", Title = "Scale QA", Scale = 100 };
                plan.Lines.Add(new ViewLine { Layer = ViewLayers.Cut, X2 = 6000 });
                plan.Lines.Add(new ViewLine { Layer = ViewLayers.Cut, X1 = 6000, X2 = 6000, Y2 = 4000 });
                plan.Lines.Add(new ViewLine { Layer = ViewLayers.Opening, X1 = 1000, X2 = 2000, Y1 = 500, Y2 = 500 });
                plan.Dimensions.Add(new ViewDimension { Layer = ViewLayers.Dimension,
                    From = 0, To = 6000, LinePosition = -500 });
                BuildingModelJson.SaveView(args[1], SheetComposer.ComposeModelSpace(new[] { plan },
                    new SheetDefinitionModel { Id = "qa-sheet", Paper = "A3", ViewIds = new List<string> { plan.Id } }));
                Console.WriteLine("CAD_FIXTURE_OK " + args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--blender-roundtrip")
            {
                CheckGeometry(ModelRoot.Load(args[1]), BuildingVolumeBuilder.Build(model));
                Console.WriteLine("PASS Blender 导入/重新导出：三维尺寸、表面积和法线保持一致");
                return 0;
            }
            var before = BuildingModelJson.ToJson(model);
            var path = Path.Combine(folder, "house.glb");
            var result = BuildingModelGlbExporter.Export(model, path);
            var glb = ModelRoot.Load(path);
            var volume = BuildingVolumeBuilder.Build(model);
            CheckGeometry(glb, volume);
            Assert(result.TriangleCount < volume.Faces.Sum(f => f.Points.Count - 2) / 2,
                "墙面网格没有显著减少。");
            Console.WriteLine("PASS 网格优化：" + volume.Faces.Sum(f => f.Points.Count - 2)
                + " → " + result.TriangleCount + " 个三角面");
            Assert(result.StoreyCount == 2 && result.ElementCount ==
                volume.Faces.Select(f => (f.StoreyId, f.ElementId)).Distinct().Count(), "楼层或构件数量丢失。");
            Assert(before == BuildingModelJson.ToJson(model), "导出修改了源模型。");
            Assert(glb.LogicalNodes.Where(n => n.Mesh != null).All(n =>
                !string.IsNullOrWhiteSpace(n.Extras?["elementId"]?.GetValue<string>())), "构件 ID 丢失。");
            Assert(glb.LogicalNodes.Where(n => n.Mesh != null).All(n =>
                n.VisualParent?.Extras?["storeyId"] != null), "楼层层级丢失。");
            var old = File.ReadAllBytes(path);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { BuildingModelGlbExporter.Export(model, path, cancellation: cancellation.Token); throw new Exception("取消未生效。"); }
                catch (OperationCanceledException) { }
            }
            Assert(old.SequenceEqual(File.ReadAllBytes(path)), "取消破坏了旧导出文件。");
            try { BuildingModelGlbExporter.Export(model, path, "missing"); throw new Exception("非法楼层未拒绝。"); }
            catch (ArgumentException) { }
            var floor = model.Storeys[0];
            BuildingModelGlbExporter.Export(model, path, floor.Id);
            var single = ModelRoot.Load(path);
            CheckGeometry(single, BuildingVolumeBuilder.Build(model, floor.Id));
            Assert(single.LogicalNodes.Where(n => n.Mesh != null).All(n =>
                n.Extras?["storeyId"]?.GetValue<string>() == floor.Id), "单层导出混入其他楼层。");
            Console.WriteLine("PASS GLB 整栋/单层、米制坐标、法线、身份、原子覆盖与取消");

            model.Storeys.Add(new StoreyModel { Id = "STD3", Name = "标准三层", Elevation = 7200,
                Height = 3600, TemplateStoreyId = floor.Id });
            BuildingModelGlbExporter.Export(model, path, "STD3");
            var standard = ModelRoot.Load(path);
            CheckGeometry(standard, BuildingVolumeBuilder.Build(model, "STD3"));
            Assert(standard.LogicalNodes.Where(n => n.Mesh != null).All(n =>
                n.Extras?["elementId"]?.GetValue<string>() != n.Extras?["sourceElementId"]?.GetValue<string>()),
                "标准层实例与来源 ID 未分开。");
            Console.WriteLine("PASS GLB 标准层独立标高、实例 ID 与来源 ID");

            var concave = new BuildingModelDocument { Name = "L板" };
            concave.Storeys.Add(new StoreyModel { Id = "1F", Elevation = 0, Height = 3000 });
            concave.Slabs.Add(new SlabModel { Id = "L", StoreyId = "1F", TopElevation = 100,
                Thickness = 100, Outline = new List<PointModel> { new(0, 0), new(2000, 0),
                    new(2000, 1000), new(1000, 1000), new(1000, 2000), new(0, 2000) } });
            BuildingModelGlbExporter.Export(concave, path);
            CheckGeometry(ModelRoot.Load(path), BuildingVolumeBuilder.Build(concave));
            var topArea = Triangles(ModelRoot.Load(path)).Where(t => t.Item4.Y > 0.9f)
                .Sum(t => Vector3.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length() / 2);
            Assert(Math.Abs(topArea - 3f) < 0.0001, "凹楼板三角形跨越缺口。");
            Console.WriteLine("PASS GLB 凹楼板三角剖分保持 3 平方米面积");
            CheckSlabOpenings(path);
            CheckIndependentWallMesh(path);
            CheckModelSpaceSheets();
            CheckDisplayCodes();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        finally { Directory.Delete(folder, true); }
    }

    private static void CheckSlabOpenings(string path)
    {
        var model = new BuildingModelDocument { Name = "井道楼板" };
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600, Height = 3300 });
        List<PointModel> Rect(double x0, double y0, double x1, double y1) => new()
            { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };
        model.Slabs.Add(new SlabModel { Id = "slab", StoreyId = "2F", TopOffset = 0, Thickness = 180,
            Outline = Rect(0, 0, 6000, 5000), Openings = new List<SlabOpeningModel> {
                new() { Id = "stair", Outline = Rect(1000, 1000, 2500, 3000) },
                new() { Id = "lift", Outline = Rect(3500, 1000, 4500, 3000) } } });
        BuildingModelGlbExporter.Export(model, path);
        var glb = ModelRoot.Load(path);
        CheckGeometry(glb, BuildingVolumeBuilder.Build(model));
        var top = Triangles(glb).Where(t => t.Item4.Y > 0.9f).ToArray();
        Assert(Math.Abs(top.Sum(t => Vector3.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length() / 2d)
            - 25d) < 0.0001, "导出把井道孔洞填满或丢失板面。");
        float Cross(Vector3 a, Vector3 b, Vector3 p) => (b.X - a.X) * (p.Z - a.Z) - (b.Z - a.Z) * (p.X - a.X);
        foreach (var p in new[] { new Vector3(1.7f, 3.6f, -1.8f), new Vector3(3.9f, 3.6f, -1.8f) })
            Assert(!top.Any(t => {
                var signs = new[] { Cross(t.Item1, t.Item2, p), Cross(t.Item2, t.Item3, p), Cross(t.Item3, t.Item1, p) };
                return signs.All(v => v >= -1e-6) || signs.All(v => v <= 1e-6);
            }), "GLB 三角面跨越井道中心。");
        Directory.CreateDirectory(".artifacts/slab-openings");
        File.Copy(path, ".artifacts/slab-openings/model.glb", true);
        Console.WriteLine("PASS GLB 双井道：25 平方米板面、洞口贯通、外法线与毫米米转换");
    }

    private static void CheckIndependentWallMesh(string path)
    {
        var model = new BuildingModelDocument { Name = "宿主隔离" };
        model.Storeys.Add(new StoreyModel { Id = "1F", Name = "一层", Height = 3000 });
        model.Walls.Add(new WallModel { Id = "host", StoreyId = "1F", X2 = 6000, Thickness = 240 });
        model.Walls.Add(new WallModel { Id = "neighbor", StoreyId = "1F", Y2 = 5000, Thickness = 240 });
        BuildingModelGlbExporter.Export(model, path);
        int Count()
        {
            var node = ModelRoot.Load(path).LogicalNodes.Single(n => n.Mesh != null &&
                n.Extras?["elementId"]?.GetValue<string>() == "neighbor");
            return node.Mesh.Primitives.Sum(p => p.GetIndices().Count / 3);
        }
        var before = Count();
        model.Openings.Add(new OpeningModel { Id = "window", HostWallId = "host", Code = "C1518",
            Offset = 2500, Width = 1500, Height = 1800, Sill = 800 });
        BuildingModelGlbExporter.Export(model, path);
        Assert(Count() == before, "另一道墙开窗增加了邻墙的网格切分。");
        CheckGeometry(ModelRoot.Load(path), BuildingVolumeBuilder.Build(model));
        Console.WriteLine("PASS 宿主隔离：相邻墙开窗前后网格数量保持 " + before);
    }

    private static void CheckModelSpaceSheets()
    {
        var view = new ViewDocument { Id = "plan", Title = "平面", Scale = 100 };
        view.Lines.Add(new ViewLine { Layer = ViewLayers.Cut, X1 = 0, Y1 = 0, X2 = 6000, Y2 = 0 });
        view.Lines.Add(new ViewLine { Layer = ViewLayers.Cut, X1 = 6000, Y1 = 0, X2 = 6000, Y2 = 4000 });
        view.Dimensions.Add(new ViewDimension { From = 0, To = 6000, Text = null });
        foreach (var denominator in new[] { 1, 50, 100, 200 })
        {
            view.Scale = denominator;
            var sheet = SheetComposer.ComposeModelSpace(new[] { view }, new SheetDefinitionModel
                { Id = "sheet", Paper = "A3", ViewIds = new List<string> { view.Id } });
            var wall = sheet.Lines.First(l => l.Layer == ViewLayers.Cut);
            Assert(Math.Abs(wall.X2 - wall.X1 - 6000) < 1e-6, "建筑被比例缩放。");
            var frame = sheet.Lines.First(l => l.Layer == ViewLayers.SheetFrame);
            Assert(Math.Abs(frame.X2 - frame.X1 - 420d * denominator) < 1e-6, "图框放大倍数错误。");
            Assert(sheet.ModelSpaceSheet && sheet.Scale == denominator && sheet.PaperWidth == 420,
                "模型空间和纸张尺寸元数据错误。");
            Assert(Math.Abs(sheet.Dimensions[0].To - sheet.Dimensions[0].From - 6000) < 1e-6
                && string.IsNullOrEmpty(sheet.Dimensions[0].Text), "标注不是实测尺寸。");
        }
        Assert(view.Lines[0].X2 == 6000 && view.Dimensions[0].To == 6000, "排版修改源视图。");
        var other = new ViewDocument { Id = "other", Scale = 50, Lines = view.Lines };
        var mixed = SheetComposer.ComposeModelSpace(new[] { view, other }, new SheetDefinitionModel
            { ViewIds = new List<string> { "plan", "other" } });
        Assert(mixed.Lines.Where(l => l.Layer == ViewLayers.Cut && l.X1 != l.X2)
            .All(l => Math.Abs(l.X2 - l.X1 - 6000) < 1e-6), "混合比例缩放了建筑。");
        Assert(mixed.Warnings.Any(w => w.Contains("统一图框比例")), "混合比例没有提示。");
        Console.WriteLine("PASS CAD 排版：建筑 1:1，1/50/100/200 倍图框、真实尺寸标注和混合比例提示");
    }

    private static void CheckDisplayCodes()
    {
        var source = SampleModelFactory.CreateTwoStoreyHouse();
        var session = new BuildingModelEditSession(source);
        var wall = session.Model.Walls[0];
        var id = wall.Id;
        var code = wall.Code;
        Assert(code.StartsWith("W-") && code.Length < 12, "墙的显示编号过长。");
        Assert(session.TryTransformWall(id, 10000, 10000, 0, true, out var copied, out var error), error);
        Assert(session.Model.Walls.First(w => w.Id == id).Code == code
            && session.Model.Walls.First(w => w.Id == copied).Code != code, "复制使墙编号改变或重复。");
        var restored = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Assert(restored.Walls.First(w => w.Id == id).Code == code, "保存丢失墙编号。");
        Assert(BuildingElementNames.Opening(new OpeningModel { Code = "C1518", Id = "long-id" }) == "C1518",
            "门窗仍显示内部 ID。");
        Console.WriteLine("PASS 显示编号：短编号、复制唯一、保存稳定，内部 ID 不变");
    }

    private static IEnumerable<Tuple<Vector3, Vector3, Vector3, Vector3>> Triangles(ModelRoot glb)
    {
        foreach (var mesh in glb.LogicalMeshes)
        foreach (var primitive in mesh.Primitives)
        {
            var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
            var normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
            var indices = primitive.GetIndices().ToArray();
            for (var i = 0; i < indices.Length; i += 3)
                yield return Tuple.Create(positions[(int)indices[i]], positions[(int)indices[i + 1]],
                    positions[(int)indices[i + 2]], normals[(int)indices[i]]);
        }
    }

    private static void CheckGeometry(ModelRoot glb, BuildingVolume volume)
    {
        var triangles = Triangles(glb).ToArray();
        var points = triangles.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }).ToArray();
        var min = points.Aggregate(Vector3.Min); var max = points.Aggregate(Vector3.Max);
        Assert(Vector3.Distance(min, new Vector3((float)volume.MinX / 1000,
            (float)volume.MinZ / 1000, (float)-volume.MaxY / 1000)) < 0.00001f, "最小坐标或单位错误。");
        Assert(Vector3.Distance(max, new Vector3((float)volume.MaxX / 1000,
            (float)volume.MaxZ / 1000, (float)-volume.MinY / 1000)) < 0.00001f, "最大坐标或单位错误。");
        Assert(triangles.All(t => Vector3.Dot(Vector3.Normalize(Vector3.Cross(t.Item2 - t.Item1,
            t.Item3 - t.Item1)), t.Item4) > 0.99f), "法线与顶点绕序不一致。");
        // Surface area comparison also detects lost wall faces and accidental hole fills.
        var actualArea = triangles.Sum(t => Vector3.Cross(t.Item2 - t.Item1, t.Item3 - t.Item1).Length() / 2d);
        double FaceArea(VolumeFace face)
        {
            var sum = Vector3.Zero;
            for (var i = 0; i < face.Points.Count; i++)
            {
                Vector3 P(Point3DModel p) => new((float)p.X / 1000, (float)p.Y / 1000, (float)p.Z / 1000);
                sum += Vector3.Cross(P(face.Points[i]), P(face.Points[(i + 1) % face.Points.Count]));
            }
            return sum.Length() / 2d;
        }
        var expectedArea = volume.Faces.Sum(FaceArea);
        Assert(Math.Abs(actualArea - expectedArea) < Math.Max(0.001, expectedArea * 0.00001),
            "导出表面积改变：" + actualArea + " / " + expectedArea);
    }
}
