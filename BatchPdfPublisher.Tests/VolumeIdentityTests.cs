using System;
using System.Diagnostics;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class VolumeIdentityTests
{
    public static void Run()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var known = model.Walls.Select(x => x.Id)
            .Concat(model.Openings.Select(x => x.Id))
            .Concat(model.Columns.Select(x => x.Id))
            .Concat(model.Slabs.Select(x => x.Id))
            .Concat(model.Stairs.Select(x => x.Id))
            .Concat(model.Roofs.Select(x => x.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var volume = BuildingVolumeBuilder.Build(model);
        Assert(volume.Faces.Count > 0, "体量为空");
        Assert(volume.Faces.All(x => !string.IsNullOrWhiteSpace(x.ElementId) && known.Contains(x.ElementId)),
            "体量面缺少有效构件 ID");
        Assert(volume.Faces.Where(x => x.Kind == "frame" || x.Kind == "glass" || x.Kind == "door")
            .All(x => model.Openings.Any(opening => opening.Id == x.ElementId)), "门窗面未绑定洞口 ID");
        Assert(volume.Faces.Where(x => x.Kind == "wall")
            .All(x => model.Walls.Any(wall => wall.Id == x.ElementId)), "墙面未绑定墙 ID");

        var wall = model.Walls.First(x => model.Openings.Any(o => o.HostWallId == x.Id));
        var opening = model.Openings.First(x => x.HostWallId == wall.Id);
        var originalWallId = wall.Id;
        var originalOpeningId = opening.Id;
        wall.X2 += 500d;
        opening.Offset += 100d;
        var updated = BuildingVolumeBuilder.Build(model);
        Assert(updated.Faces.Any(x => x.ElementId == originalWallId), "编辑后墙 ID 丢失");
        Assert(updated.Faces.Any(x => x.ElementId == originalOpeningId), "编辑后洞口 ID 丢失");
        CheckOrthogonalCorner();
        CheckOrthogonalTJoint();
        CheckWallReferencePlacement();
        MeasureJunctionGrid();
        CheckSharedAxesAndCadJunction();
        Console.WriteLine("PASS 三维体量构件身份：样例墙/门窗/板/柱可追溯，参数修改后 ID 稳定");
    }

    private static void CheckOrthogonalCorner()
    {
        var model = SampleModelFactory.CreateEmptyModel("墙角收口");
        model.Walls.Add(new WallModel { Id = "horizontal", StoreyId = "1F",
            X1 = -1000, Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "vertical", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 1000, Thickness = 200 });
        var volume = BuildingVolumeBuilder.Build(model);
        var top = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5).ToArray();
        foreach (var point in new[] { (x: 50d, y: -50d), (x: 10d, y: 10d),
            (x: -510d, y: 10d), (x: 10d, y: 510d) })
        {
            var count = top.Count(f => point.x >= f.Points.Min(p => p.X) - 1e-6
                && point.x <= f.Points.Max(p => p.X) + 1e-6
                && point.y >= f.Points.Min(p => p.Y) - 1e-6
                && point.y <= f.Points.Max(p => p.Y) + 1e-6);
            Assert(count == 1, "正交墙角顶面缺失或重叠：" + point + "，面数 " + count);
        }
        Assert(!volume.Faces.Any(f => f.Kind == "wall" && Math.Abs(f.NormalZ) < 0.5
            && f.Points.All(p => Math.Abs(p.X) < 1e-6)
            && f.Points.Min(p => p.Y) < -49 && f.Points.Max(p => p.Y) > -51),
            "墙角内部仍存在立面重面");
        Assert(volume.Faces.Where(f => f.Kind == "wall")
            .Select(f => f.ElementId).Distinct().Count() == 2, "墙角融合后墙 ID 丢失");
        Console.WriteLine("PASS 正交墙角：缺角填合、顶面无重叠、内部面消除、两道墙可追溯");
    }

    private static void CheckWallReferencePlacement()
    {
        var model = SampleModelFactory.CreateEmptyModel("墙定位轴线");
        model.Walls.Add(new WallModel { Id = "axis-wall", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 1000, Y2 = 0, Thickness = 200,
            AxisPlacement = WallAxisPlacement.LeftFace });
        model.Axes.Add(new AxisModel { Id = "building-axis", Name = "1", Vertical = true, Position = 0 });
        var left = BuildingVolumeBuilder.Build(model);
        Assert(Math.Abs(left.MinY + 200) < 1e-6 && Math.Abs(left.MaxY) < 1e-6,
            "左面定位轴线应落在墙实体的左侧边界");
        Assert(left.GuideLines.Count == 3 && left.GuideLines.Any(g => g.ElementId == "axis-wall"
            && Math.Abs(g.Start.Y) < 1e-6), "墙轴线与建筑轴网未送到三维地面参考线");
        var session = new BuildingModelEditSession(model);
        Assert(session.TrySetWallAxisPlacement("axis-wall", WallAxisPlacement.RightFace, out var error),
            "墙定位轴线切换失败：" + error);
        var right = BuildingVolumeBuilder.Build(session.Model);
        Assert(Math.Abs(right.MinY) < 1e-6 && Math.Abs(right.MaxY - 200) < 1e-6,
            "右面定位轴线应落在墙实体的右侧边界");
        Assert(BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model)).Walls[0].AxisPlacement
            == WallAxisPlacement.RightFace, "墙定位轴线位置保存后丢失");
        Assert(session.Undo() && session.Model.Walls[0].AxisPlacement == WallAxisPlacement.LeftFace,
            "撤销未恢复墙定位轴线位置");
        Console.WriteLine("PASS 墙定位轴线：墙中/左面/右面几何、三维参考线、保存与撤销");
    }

    private static void CheckOrthogonalTJoint()
    {
        var model = SampleModelFactory.CreateEmptyModel("T 形墙交接");
        model.Walls.Add(new WallModel { Id = "host", StoreyId = "1F",
            X1 = -1000, Y1 = 0, X2 = 1000, Y2 = 0, Thickness = 240 });
        model.Walls.Add(new WallModel { Id = "branch", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 1000, Thickness = 160 });
        var volume = BuildingVolumeBuilder.Build(model);
        var top = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5).ToArray();
        foreach (var point in new[] { (x: 70d, y: -100d), (x: 70d, y: 100d),
            (x: 70d, y: 170d), (x: -100d, y: 70d) })
        {
            var count = top.Count(f => point.x > f.Points.Min(p => p.X) + 1e-6
                && point.x < f.Points.Max(p => p.X) - 1e-6
                && point.y > f.Points.Min(p => p.Y) + 1e-6
                && point.y < f.Points.Max(p => p.Y) - 1e-6);
            Assert(count == 1, "T 形交接顶面缺失或重叠：" + point + "，面数 " + count);
        }
        Console.WriteLine("PASS 不同墙厚 T 形交接：墙体顶面无缺角或重面");
    }

    private static void MeasureJunctionGrid()
    {
        var model = SampleModelFactory.CreateEmptyModel("交接性能");
        for (var i = 0; i < 20; i++)
        {
            var coordinate = i * 400d;
            model.Walls.Add(new WallModel { Id = "h" + i, StoreyId = "1F",
                X1 = 0, Y1 = coordinate, X2 = 7600, Y2 = coordinate, Thickness = 200 });
            model.Walls.Add(new WallModel { Id = "v" + i, StoreyId = "1F",
                X1 = coordinate, Y1 = 0, X2 = coordinate, Y2 = 7600, Thickness = 200 });
        }
        var watch = Stopwatch.StartNew();
        var volume = BuildingVolumeBuilder.Build(model);
        Assert(volume.Faces.Count > 0 && volume.Faces.Count < 20000,
            "正交墙网生成了异常数量的面：" + volume.Faces.Count);
        Console.WriteLine("PASS 正交墙网：40 道墙，" + volume.Faces.Count + " 面，建模 "
            + watch.ElapsedMilliseconds + " ms（当前机器）");
    }

    private static void CheckSharedAxesAndCadJunction()
    {
        var model = SampleModelFactory.CreateEmptyModel("轴网与 CAD 墙角");
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600, Height = 3300 });
        model.Walls.Add(new WallModel { Id = "one-horizontal", StoreyId = "1F",
            X1 = -1000, Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "one-vertical", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 1000, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "two-horizontal", StoreyId = "2F",
            X1 = -1000, Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        var axes = BuildingAxisLayout.Resolve(model);
        var shared = axes.Where(a => !a.Vertical && Math.Abs(a.Position) < 1e-6).ToArray();
        Assert(shared.Length == 1 && !string.IsNullOrWhiteSpace(shared[0].Name),
            "跨楼层重合墙轴线应共用一条带编号的轴线");
        ViewDocument Plan(string storey) => OrthographicProjector.ProjectPlan(model,
            new ViewDefinitionModel { Id = "plan-" + storey, Title = storey + "平面",
                Kind = ViewKind.Plan, Scale = 100,
                StoreyIds = new System.Collections.Generic.List<string> { storey } }, null);
        var first = Plan("1F");
        var second = Plan("2F");
        Assert(first.Anchors.Any(a => a.Kind == "axis" && a.ElementId == shared[0].Id)
            && second.Anchors.Any(a => a.Kind == "axis" && a.ElementId == shared[0].Id),
            "CAD 两层平面未引用同一轴线 ID");
        Assert(first.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == shared[0].Name)
            && second.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == shared[0].Name),
            "CAD 两层平面轴号不一致");
        var cut = first.Lines.Where(l => l.Layer == ViewLayers.Cut).ToArray();
        Assert(cut.Any(l => Math.Abs(l.Y1 + first.OriginY + 100) < 1e-6
            && Math.Abs(l.Y2 + first.OriginY + 100) < 1e-6
            && Math.Max(l.X1, l.X2) + first.OriginX >= 99),
            "CAD 平面墙角缺少补齐后的外边界");
        Assert(!cut.Any(l => Math.Abs(l.X1 + first.OriginX) < 1e-6
            && Math.Abs(l.X2 + first.OriginX) < 1e-6
            && Math.Min(l.Y1, l.Y2) + first.OriginY < -49
            && Math.Max(l.Y1, l.Y2) + first.OriginY > -51),
            "CAD 平面仍画出墙角内部接缝");
        Console.WriteLine("PASS 共享轴网与 CAD 收口：跨楼层同 ID 同轴号、L 形外边界无缺角及内部线");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
