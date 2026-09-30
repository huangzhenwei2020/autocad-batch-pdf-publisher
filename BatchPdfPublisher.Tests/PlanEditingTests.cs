using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

/// <summary>
/// 平面草图编辑的纯逻辑测试：命中、捕捉、沿墙定位、洞口校验、撤销栈。
/// 这些规则同时被程序画布与（将来的）CAD 侧构件化绘制复用，所以在这里钉住。
/// </summary>
internal static class PlanEditingTests
{
    private const double Tolerance = 0.5d;

    public static void Run()
    {
        SnapToBuildingAxes();
        HitTestPrefersOpeningThenGrips();
        SnapFindsEndpointsAndMidpoints();
        SnapOnlyToObjects();
        SnapIntersectionsAndPerpendiculars();
        ValidateOpeningRejectsOverflowAndOverlap();
        HistoryUndoRedoAndBranchReset();
        DrawRoomThenProjectElevation();
        Console.WriteLine("PASS 平面编辑逻辑：命中 / 捕捉 / 洞口校验 / 撤销栈 / 手画一间房出立面");
    }

    private static void SnapToBuildingAxes()
    {
        var model = SampleModelFactory.CreateEmptyModel("轴网捕捉");
        model.Axes.Add(new AxisModel { Id = "AX-X", Vertical = true, Position = 1000,
            ExtentStart = 0, ExtentEnd = 4000 });
        model.Axes.Add(new AxisModel { Id = "AX-Y", Vertical = false, Position = 2000,
            ExtentStart = 0, ExtentEnd = 3000 });
        var intersection = PlanEditing.Snap(model, "1F", 1020, 2010, 50, false, 0, 0);
        Assert(intersection.Kind == PlanEditing.SnapAxisIntersection
            && intersection.X == 1000 && intersection.Y == 2000,
            "画墙未捕捉到轴线交点");
        var axis = PlanEditing.Snap(model, "1F", 1020, 2800, 50, false, 0, 0);
        Assert(axis.Kind == PlanEditing.SnapAxis && axis.X == 1000 && axis.Y == 2800,
            "画墙未捕捉到轴线");
        model.Walls.Add(new WallModel { Id = "near-axis-wall", StoreyId = "1F",
            X1 = 0, Y1 = 2000.0035, X2 = 3000, Y2 = 2000.0035, Thickness = 240 });
        foreach (var hasFrom in new[] { false, true })
        {
            var occupied = PlanEditing.Snap(model, "1F", 1020, 2001, 50,
                hasFrom, 1000, 1000);
            Assert(occupied.Kind == PlanEditing.SnapAxisIntersection
                && occupied.X == 1000 && occupied.Y == 2000,
                "附近墙身或垂足抢走轴线交点：" + occupied.Kind);
        }
        var excluded = PlanEditing.Snap(model, "1F", 1020, 2001, 50,
            false, 0, 0, "near-axis-wall");
        Assert(excluded.Kind == PlanEditing.SnapAxisIntersection
            && excluded.X == 1000 && excluded.Y == 2000,
            "端点编辑排除当前墙后丢失轴线交点");
        Console.WriteLine("PASS 轴线及轴线交点捕捉");
    }

    private static BuildingModelDocument Model()
    {
        var model = new BuildingModelDocument { Name = "编辑测试" };
        model.Storeys.Add(new StoreyModel { Id = "1F", Name = "一层", Elevation = 0d, Height = 3600d });
        model.Walls.Add(new WallModel { Id = "w1", StoreyId = "1F", X1 = 0d, Y1 = 0d, X2 = 6000d, Y2 = 0d, Thickness = 240d });
        model.Walls.Add(new WallModel { Id = "w2", StoreyId = "1F", X1 = 6000d, Y1 = 0d, X2 = 6000d, Y2 = 4000d, Thickness = 240d });
        model.Openings.Add(PlanEditing.CreateOpening("窗", "w1", 3000d));
        model.Columns.Add(new ColumnModel { Id = "c1", StoreyId = "1F", X = 0d, Y = 4000d, Width = 400d, Depth = 400d });
        return model;
    }

    private static void HitTestPrefersOpeningThenGrips()
    {
        var model = Model();
        // 洞口中心（3000,0）：点一点在洞口宽度范围内 → 命中洞口
        var hit = PlanEditing.HitTest(model, "1F", 3000d, 0d, 50d);
        Assert(hit != null && hit.Kind == "opening", "洞口没被优先命中：" + (hit == null ? "null" : hit.Kind));
        // 墙起点附近的夹点
        var grip = PlanEditing.HitTest(model, "1F", 20d, 20d, 60d);
        Assert(grip != null && grip.Kind == "wall" && grip.Grip == 0, "墙起点夹点没命中");
        // 墙中点夹点
        var middle = PlanEditing.HitTest(model, "1F", 3000d + 0d, 0d, 40d);
        Assert(middle != null && middle.Kind == "opening", "洞口应优先于墙身");
        var middleGrip = PlanEditing.HitTest(model, "1F", 4500d, 0d, 40d);   // w1 中点附近、避开洞口
        Assert(middleGrip != null && middleGrip.Kind == "wall" && middleGrip.Grip == -1,
            "远离夹点时命中墙身（整体），实际 " + (middleGrip == null ? "null" : middleGrip.Kind + "/" + middleGrip.Grip));
        // 柱
        var column = PlanEditing.HitTest(model, "1F", 0d, 4000d, 30d);
        Assert(column != null && column.Kind == "column", "柱没命中");
        // 空白处
        var none = PlanEditing.HitTest(model, "1F", 30000d, 30000d, 50d);
        Assert(none == null, "空白处不该命中任何东西");
        Console.WriteLine("   命中：洞口/夹点/墙身/柱/空白 —— 全部符合预期");
    }

    private static void SnapFindsEndpointsAndMidpoints()
    {
        var model = Model();
        var endpoint = PlanEditing.Snap(model, "1F", 6030d, 30d, 60d, false, 0d, 0d);
        Assert(endpoint.Snapped && endpoint.Kind == PlanEditing.SnapEndpoint
            && Math.Abs(endpoint.X - 6000d) < Tolerance && Math.Abs(endpoint.Y) < Tolerance,
            "端点捕捉失败：" + endpoint.Kind + " (" + endpoint.X + "," + endpoint.Y + ")");
        var midpoint = PlanEditing.Snap(model, "1F", 3000d, 40d, 60d, false, 0d, 0d);
        Assert(midpoint.Snapped && midpoint.Kind == PlanEditing.SnapMidpoint && Math.Abs(midpoint.Y) < Tolerance,
            "中点捕捉失败：" + midpoint.Kind);
        Console.WriteLine("   捕捉：端点 (6000,0)、中点 (3000,0) 命中");
    }

    private static void SnapOnlyToObjects()
    {
        var model = Model();
        var blank = PlanEditing.Snap(model, "1F", 20030d, 20040d, 60d,
            true, 1000d, 20040d);
        Assert(blank.Kind == PlanEditing.SnapNone && blank.X == 20030d && blank.Y == 20040d,
            "空白处仍被吸到网格或正交方向");
        var nearHorizontal = PlanEditing.Snap(model, "1F", 5000d, 1060d, 40d,
            true, 1000d, 1000d);
        Assert(nearHorizontal.Kind == PlanEditing.SnapNone && nearHorizontal.Y == 1060d,
            "空白处仍被强制拉成水平线");
        var wall = PlanEditing.Snap(model, "1F", 4500d, 40d, 60d,
            false, 0d, 0d);
        Assert(wall.Kind == PlanEditing.SnapNearest
            && Math.Abs(wall.X - 4500d) < Tolerance && Math.Abs(wall.Y) < Tolerance,
            "未吸附到鼠标附近的真实墙身");
        var endpoint = PlanEditing.Snap(model, "1F", 6030d, 30d, 60d,
            false, 0d, 0d);
        Assert(endpoint.Kind == PlanEditing.SnapEndpoint && endpoint.X == 6000d && endpoint.Y == 0d,
            "对象端点捕捉被墙身捕捉覆盖");
        Console.WriteLine("   对象捕捉：空白处保留原坐标，近墙身/端点才吸附");
    }

    private static void SnapIntersectionsAndPerpendiculars()
    {
        var model = SampleModelFactory.CreateEmptyModel("捕捉测试");
        model.Walls.Add(new WallModel { Id = "horizontal", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 6000, Y2 = 0 });
        model.Walls.Add(new WallModel { Id = "vertical", StoreyId = "1F",
            X1 = 3000, Y1 = -2000, X2 = 3000, Y2 = 2000 });
        var crossing = PlanEditing.Snap(model, "1F", 3020, 20, 80, false, 0, 0);
        Assert(crossing.Kind == PlanEditing.SnapIntersection
            && Math.Abs(crossing.X - 3000) < Tolerance && Math.Abs(crossing.Y) < Tolerance,
            "墙轴线内部交点捕捉失败：" + crossing.Kind);
        var foot = PlanEditing.Snap(model, "1F", 1020, 30, 80, true, 1000, 1800);
        Assert(foot.Kind == PlanEditing.SnapPerpendicular
            && Math.Abs(foot.X - 1000) < Tolerance && Math.Abs(foot.Y) < Tolerance,
            "垂足捕捉失败：" + foot.Kind);
        var outside = PlanEditing.Snap(model, "1F", 7000, 20, 80, true, 7000, 1800);
        Assert(outside.Kind != PlanEditing.SnapPerpendicular && outside.Kind != PlanEditing.SnapIntersection,
            "墙段外的延长线被当成垂足或交点");
        model.Walls[1].X1 = 7000;
        model.Walls[1].X2 = 7000;
        var extension = PlanEditing.Snap(model, "1F", 7000, 10, 80, false, 0, 0);
        Assert(extension.Kind != PlanEditing.SnapIntersection, "延长线交点被错误捕捉");
        model.Walls[1].X1 = 1000; model.Walls[1].Y1 = 1000;
        model.Walls[1].X2 = 5000; model.Walls[1].Y2 = 1000;
        var parallel = PlanEditing.Snap(model, "1F", 3000, 500, 80, false, 0, 0);
        Assert(parallel.Kind != PlanEditing.SnapIntersection, "平行墙出现假交点");
        Console.WriteLine("   捕捉：内部交点/垂足命中，延长线和平行墙不误吸附");
    }

    private static void ValidateOpeningRejectsOverflowAndOverlap()
    {
        var model = Model();
        var wall = model.Walls.First(w => w.Id == "w1");
        var good = PlanEditing.CreateOpening("门", "w1", 1500d);
        Assert(PlanEditing.ValidateOpening(model, wall, good) == null, "合法门洞被误判：" + PlanEditing.ValidateOpening(model, wall, good));

        var overflow = PlanEditing.CreateOpening("窗", "w1", 20d);
        Assert(PlanEditing.ValidateOpening(model, wall, overflow) != null, "超出墙范围的洞口应被拒绝");

        var overlap = PlanEditing.CreateOpening("窗", "w1", 3100d);        // 与 3000 处的窗重叠
        Assert(PlanEditing.ValidateOpening(model, wall, overlap) != null, "重叠的洞口应被拒绝");

        var tooLong = new WallModel { Id = "w3", StoreyId = "1F", X1 = 0d, Y1 = 0d, X2 = 5d, Y2 = 0d, Thickness = 200d };
        Assert(PlanEditing.ValidateWall(tooLong) != null, "过短的墙应被拒绝");
        Assert(PlanEditing.ValidateWall(wall) == null, "正常墙被误判：" + PlanEditing.ValidateWall(wall));
        Console.WriteLine("   校验：越界/重叠洞口与过短墙均被拒绝");
    }

    private static void HistoryUndoRedoAndBranchReset()
    {
        var history = new ModelEditHistory();
        var model = Model();
        history.Reset(model);
        var wallsBefore = model.Walls.Count;

        model.Walls.Add(new WallModel { Id = "w9", StoreyId = "1F", X1 = 0d, Y1 = 4000d, X2 = 6000d, Y2 = 4000d, Thickness = 240d });
        history.Push(model);
        Assert(history.CanUndo, "改动后应能撤销");

        var undone = history.Undo(model);
        Assert(undone.Walls.Count == wallsBefore, "撤销后墙数量应回到 " + wallsBefore + "，实际 " + undone.Walls.Count);
        Assert(history.CanRedo, "撤销后应能重做");
        var redone = history.Redo(undone);
        Assert(redone.Walls.Count == wallsBefore + 1, "重做后墙数量应为 " + (wallsBefore + 1));

        // 新改动应丢弃重做分支
        history.Undo(redone);
        var branch = history.Undo(redone);
        history.Push(branch ?? model);
        Assert(!history.CanRedo, "产生新改动后不应还能重做旧分支");
        _ = branch;

        // 重复 Push 同一状态不应增长历史
        var count = history.Count;
        history.Push(BuildingModelJson.FromJson(BuildingModelJson.ToJson(branch ?? model)));
        Assert(history.Count == count || history.Count == count - 1, "重复状态不该让历史无限增长，实际 " + history.Count);
        Console.WriteLine("   历史：撤销/重做/分支丢弃/幂等 均符合预期（栈深 " + history.Count + "）");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    /// <summary>
    /// 端到端：像画布那样"画四面墙 → 在南墙放一樘窗与一樘门 → 出南立面"。
    /// 这条走通了，说明 P1.5 的"画出来就是模型、模型能出图"在逻辑层成立。
    /// </summary>
    private static void DrawRoomThenProjectElevation()
    {
        var model = SampleModelFactory.CreateEmptyModel("手画一间房");
        var storey = model.Storeys[0].Id;
        var walls = new[]
        {
            new WallModel { Id = "w-s", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 6000d, Y2 = 0d, Thickness = 240d },
            new WallModel { Id = "w-n", StoreyId = storey, X1 = 0d, Y1 = 4000d, X2 = 6000d, Y2 = 4000d, Thickness = 240d },
            new WallModel { Id = "w-w", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 0d, Y2 = 4000d, Thickness = 240d },
            new WallModel { Id = "w-e", StoreyId = storey, X1 = 6000d, Y1 = 0d, X2 = 6000d, Y2 = 4000d, Thickness = 240d }
        };
        foreach (var wall in walls)
        {
            Assert(PlanEditing.ValidateWall(wall) == null, "手画的墙被拒绝：" + PlanEditing.ValidateWall(wall));
            model.Walls.Add(wall);
        }

        var south = walls[0];
        var window = PlanEditing.CreateOpening("窗", south.Id, PlanEditing.ProjectOnWall(south, 1500d, 0d));
        window.Id = "o-win";
        Assert(PlanEditing.ValidateOpening(model, south, window) == null, "窗被拒绝：" + PlanEditing.ValidateOpening(model, south, window));
        model.Openings.Add(window);

        var door = PlanEditing.CreateOpening("门", south.Id, PlanEditing.ProjectOnWall(south, 4500d, 0d));
        door.Id = "o-door";
        Assert(PlanEditing.ValidateOpening(model, south, door) == null, "门被拒绝：" + PlanEditing.ValidateOpening(model, south, door));
        model.Openings.Add(door);

        // 洞口超出墙长时应被拒绝（画布会在状态栏提示，不写入模型）
        var outside = PlanEditing.CreateOpening("窗", south.Id, 5900d);
        Assert(PlanEditing.ValidateOpening(model, south, outside) != null, "超出墙长的洞口应被拒绝");

        var view = OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "elev", Title = "手画模型 南立面图", Kind = ViewKind.Elevation, Scale = 100,
            Direction = ElevationDirection.South
        });
        var width = view.Lines.Where(l => l.Layer == ViewLayers.Elevation)
            .Max(l => Math.Max(l.X1, l.X2)) - view.Lines.Where(l => l.Layer == ViewLayers.Elevation).Min(l => Math.Min(l.X1, l.X2));
        Assert(Math.Abs(width - 6240d) < Tolerance, "手画房间的南立面总宽应为 6240（6000 + 两侧各 120），实际 " + width);

        var openings = view.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        var wide = openings.Count(l => Math.Abs(l.Y2 - l.Y1) < Tolerance && Math.Abs(Math.Abs(l.X2 - l.X1) - 1500d) < Tolerance);
        var doorEdges = openings.Count(l => Math.Abs(l.X2 - l.X1) < Tolerance && Math.Abs(Math.Abs(l.Y2 - l.Y1) - 2100d) < Tolerance);
        Assert(wide >= 2, "南立面里应有 1500 宽的窗（≥2 条水平边），实际 " + wide);
        Assert(doorEdges >= 2, "南立面里应有高 2100 的门（≥2 条竖直边），实际 " + doorEdges);
        Assert(view.Texts.Any(t => t.Layer == ViewLayers.LevelText), "立面缺少楼层标高");

        Console.WriteLine("   端到端：手画 4 道墙 + 1 窗 1 门 → 南立面 " + Math.Round(width) + " 宽、"
            + openings.Count + " 条门窗线、标高 " + view.Texts.Count(t => t.Layer == ViewLayers.LevelText) + " 个");
    }
}
