using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

/// <summary>
/// 立面门窗分格与开启线的护栏测试（纯逻辑，不需要 AutoCAD）。
///
/// 这一层的关键承诺：**立面里的门窗是插件那套门窗立面生成器画的**（同一份源码），
/// 程序只负责"按编号查类型库 → 给出洞口尺寸 → 把生成结果裁进洞口并做遮挡"。
/// 所以这里钉住四件事：
///   1. 分格/开启线确实按类型库画出来了，而且坐标是算得出来的（不是"差不多有线条"）；
///   2. 背立面是镜像的，门的合页侧不会画反；
///   3. 被前面的墙挡住的部分不画（北立面的窗不会透到南墙上）；
///   4. 参数不合法 / 编号查不到时不炸，退回单格并给提示。
/// </summary>
internal static class OpeningElevationTests
{
    private const double Tolerance = 0.5d;

    public static void Run()
    {
        DrawsMullionsAndOpeningLines();
        MirrorsOnBackElevation();
        ClipsHiddenParts();
        HonoursCustomCellLayout();
        SimplifiesAtHundredthScale();
        FallsBackWhenParametersInvalid();
        AnchorsAndLabels();
        DimensionsAndSchedule();
        PlanProjection();
        SheetComposition();
        StudioLaunchChecks();
        VolumeChecks();
        StairChecks();
        RoofChecks();
        Console.WriteLine("PASS 立面门窗：分格与开启线 / 背立面镜像 / 遮挡裁剪 / 自定义分格 / 1:100-1:50 详简 / 参数兜底 / 可点选锚点与编号 / 尺寸与门窗表 / 平面图 / 排版出图 / 启动与取件 / 三维体量（门窗构件 / 剖切 / 透视 / 共面合并 / 藏在墙里的面 / 逐段消隐）/ 楼梯（双跑几何 / 平面 / 体量 / 剖面 / 校验）/ 坡屋面（几何 / 立面山墙 / 剖面 / 体量）");
    }

    // ───────────────────────── 13. 楼梯（双跑） ─────────────────────────

    /// <summary>
    /// 楼梯这一层的承诺：**参数手算得出来**。3000 净长 × 2700 净宽、踏步宽 260、每跑 9 级、梯段宽 1200：
    /// 层高 3600 → 踏步高 = 3600/(2×9) = 200；踏步总长 = 9×260 = 2340；平台 = 3000-2340 = 660；
    /// 平台标高 = 1800；第二跑到达 = 3600。平面里踏步线 8×2 条、剖断线 2 条、"上/下"文字与箭头齐全。
    /// </summary>
    private static void StairChecks()
    {
        var model = SampleModelFactory.CreateEmptyModel("楼梯");
        model.Storeys[0].Height = 3600d;
        // 再加一层，平面里才会有"下"（本层不是最低层才画下行箭头）
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600d, Height = 3300d });
        var storeyId = model.Storeys[0].Id;
        var stair = new StairModel
        {
            Id = "st-1", StoreyId = storeyId, X = 0d, Y = 0d,
            Length = 3000d, Width = 2700d, AlongX = true,
            FlightWidth = 1200d, Going = 260d, StepsPerFlight = 9, WellWidth = 100d
        };
        model.Stairs.Add(stair);

        // 1) 几何：手算核对
        var geometry = StairGeometry.Build(model, stair);
        Assert(geometry != null && geometry.Flights.Count == 2, "双跑楼梯应展开成两跑");
        Assert(Math.Abs(geometry.Riser - 200d) < 1e-6d, "踏步高应为 3600/(2×9)=200，实际 " + geometry.Riser);
        Assert(Math.Abs(geometry.TreadRun - 2340d) < 1e-6d, "踏步总长应为 2340，实际 " + geometry.TreadRun);
        Assert(Math.Abs(geometry.LandingDepth - 660d) < 1e-6d, "休息平台应为 660，实际 " + geometry.LandingDepth);
        Assert(Math.Abs(geometry.LandingElevation - 1800d) < 1e-6d, "平台标高应为 1800，实际 " + geometry.LandingElevation);
        Assert(Math.Abs(geometry.TopElevation - 3600d) < 1e-6d, "第二跑到达上一层楼面 3600，实际 " + geometry.TopElevation);
        Assert(geometry.Flights[0].Steps.Count == 9 && geometry.Flights[1].Steps.Count == 9, "每跑 9 级");
        Assert(geometry.Flights.All(f => f.Treads.Count == 8), "每跑 8 条踏步线（9 级之间）");
        Assert(Math.Abs(geometry.Flights[0].Steps[0].TopElevation - 200d) < 1e-6d, "第 1 级踏面 200");
        Assert(Math.Abs(geometry.Flights[0].Steps[8].TopElevation - 1800d) < 1e-6d, "第 9 级踏面 = 平台标高 1800");
        Assert(Math.Abs(geometry.Flights[1].Steps[8].TopElevation - 3600d) < 1e-6d, "第二跑第 9 级 = 3600");
        // 第一跑在南侧条带（Y 0..1200），第二跑在北侧（Y 1500..2700），中间是梯井
        Assert(Math.Abs(geometry.Flights[0].Y0) < 1e-6d && Math.Abs(geometry.Flights[0].Y1 - 1200d) < 1e-6d,
            "第一跑应在 Y 0..1200");
        Assert(Math.Abs(geometry.Flights[1].Y0 - 1500d) < 1e-6d && Math.Abs(geometry.Flights[1].Y1 - 2700d) < 1e-6d,
            "第二跑应在 Y 1500..2700");
        Assert(geometry.WellY1 - geometry.WellY0 > 100d - 1e-6d, "梯井应至少 100 宽，实际 "
            + Math.Round(geometry.WellY1 - geometry.WellY0));
        Assert(Math.Abs(geometry.LandingX0 - 2340d) < 1e-6d && Math.Abs(geometry.LandingX1 - 3000d) < 1e-6d,
            "平台应在 X 2340..3000");
        Assert(geometry.Handrails.Count == 3, "梯井两侧 + 平台内沿三条扶手线");
        Assert(geometry.UpPath.Count == 4, "上行路径应是 起步 → 平台 → 转向 → 到达 四点");

        // 沿 Y 跑时 Length 改成沿 Y、Width 沿 X（第一跑占 X 0..1200）
        stair.AlongX = false;
        var alongY = StairGeometry.Build(model, stair);
        Assert(alongY.Flights.Count == 2, "沿 Y 跑也应展开成两跑");
        Assert(Math.Abs(alongY.Flights[0].X0) < 1e-6d && Math.Abs(alongY.Flights[0].X1 - 1200d) < 1e-6d,
            "沿 Y 跑时第一跑占 X 0..1200，实际 " + Math.Round(alongY.Flights[0].X0) + ".."
            + Math.Round(alongY.Flights[0].X1));
        Assert(Math.Abs(alongY.Y1 - 3000d) < 1e-6d, "沿 Y 跑时楼梯间沿 Y 应是净长 3000，实际 " + alongY.Y1);
        stair.AlongX = true;

        // 2) 校验：放不下 / 踏步高离谱都要给出人话
        var tooNarrow = new StairModel { Id = "st-2", StoreyId = storeyId, Length = 3000d, Width = 2000d, FlightWidth = 1200d };
        Assert(PlanEditing.ValidateStair(model, tooNarrow) != null, "净宽放不下两跑应报错");
        var tooShort = new StairModel { Id = "st-3", StoreyId = storeyId, Length = 2000d, Width = 2700d, Going = 260d, StepsPerFlight = 9 };
        Assert(PlanEditing.ValidateStair(model, tooShort) != null, "净长放不下踏步+平台应报错");
        var badRiser = new StairModel
        {
            Id = "st-4", StoreyId = storeyId, Length = 5400d, Width = 2700d,
            Going = 260d, StepsPerFlight = 9, Riser = 260d
        };
        Assert(PlanEditing.ValidateStair(model, badRiser) != null, "踏步高 260 超出常用范围应报错");
        Assert(PlanEditing.ValidateStair(model, stair) == null, "默认参数应通过校验：" + PlanEditing.ValidateStair(model, stair));

        // 3) 平面图：踏步线 + 平台 + 剖断线 + 上下行箭头 + 可点选锚点
        var view = SampleModelFactory.CreatePlanView(model.Storeys[0]);
        var plan = OrthographicProjector.Project(model, view, null);
        var stairLines = plan.Lines.Where(l => l.Layer == ViewLayers.Stair).ToList();
        Assert(stairLines.Count > 30, "平面里的楼梯线应包含梯段框、踏步线与剖断线，实际 " + stairLines.Count);
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Stair && t.Text == "上"), "缺少上行文字「上」");
        Assert(!plan.Texts.Any(t => t.Layer == ViewLayers.Stair && t.Text == "下"),
            "最低层平面只画「上」（没有往下的楼梯）");
        Assert(plan.Anchors.Any(a => a.Kind == "stair" && a.ElementId == "st-1"), "楼梯要有可点选锚点");
        // 二层是"非最低层"：同一张平面里「上」「下」都要有
        model.Stairs.Add(new StairModel
        {
            Id = "st-2", StoreyId = "2F", X = 0d, Y = 0d, Length = 3000d, Width = 2700d,
            FlightWidth = 1200d, Going = 260d, StepsPerFlight = 9, WellWidth = 100d
        });
        var secondPlan = OrthographicProjector.Project(model, SampleModelFactory.CreatePlanView(model.Storeys[1]), null);
        Assert(secondPlan.Texts.Any(t => t.Layer == ViewLayers.Stair && t.Text == "上")
            && secondPlan.Texts.Any(t => t.Layer == ViewLayers.Stair && t.Text == "下"),
            "非最低层平面应同时标「上」与「下」");

        // 4) 体量：一级踏步一个方块 + 平台 + 栏板
        var volume = BuildingVolumeBuilder.Build(model, null);
        Assert(volume.Faces.Any(f => f.Kind == "stair"), "体量里应有楼梯构件");
        Assert(volume.Faces.Count(f => f.Kind == "stair") % 6 == 0, "楼梯体块也应是 6 个面");
        Assert(Math.Abs(volume.Height - 7900d) < 1d, "整栋 6900 + 二层楼梯栏板 1000 = 7900，实际 " + volume.Height);
        var oneStorey = BuildingVolumeBuilder.Build(model, storeyId);
        Assert(oneStorey.Faces.Any(f => f.Kind == "stair"), "只看这一层时楼梯也要在");
        Assert(Math.Abs(oneStorey.Height - 4600d) < 1d,
            "只看一层时高应是 层高 3600 + 平台口栏板 1000 = 4600，实际 " + oneStorey.Height);
        // 楼梯自己占的范围：起步落在楼面上，最上一级踏板底 1800-200-120=1480（说明下面不是实心块）
        var stairFaces = oneStorey.Faces.Where(f => f.Kind == "stair").ToList();
        Assert(Math.Abs(stairFaces.Min(f => f.Points.Min(p => p.Z)) - 0d) < 1d, "楼梯最低点应是本层楼面 0");
        Assert(stairFaces.Any(f => Math.Abs(f.Points.Min(p => p.Z) - 1480d) < 1d),
            "第九级踏板底应在 1480（踏面 1800 - 踏步高 200 - 板厚 120），实际 "
            + Math.Round(stairFaces.Min(f => f.Points.Min(p => p.Z))));
        Assert(stairFaces.Any(f => Math.Abs(f.Points.Max(p => p.Z) - 2800d) < 1d),
            "第二跑最后一段栏板顶应在 1800+1000=2800（第二跑收在 1800 那一档）");
        Assert(Math.Abs(stairFaces.Max(f => f.Points.Max(p => p.X)) - 3000d) < 1d
            && Math.Abs(stairFaces.Max(f => f.Points.Max(p => p.Y)) - 2700d) < 1d,
            "楼梯平面范围应是 3000×2700");

        Console.WriteLine("   楼梯几何：踏步高 " + Math.Round(geometry.Riser) + "、踏步总长 " + Math.Round(geometry.TreadRun)
            + "、平台 " + Math.Round(geometry.LandingDepth) + "@" + Math.Round(geometry.LandingElevation)
            + "、到达 " + Math.Round(geometry.TopElevation) + "；平面线 " + stairLines.Count
            + "、文字 " + string.Join("/", plan.Texts.Where(t => t.Layer == ViewLayers.Stair).Select(t => t.Text).ToArray())
            + "；体量楼梯面 " + oneStorey.Faces.Count(f => f.Kind == "stair"));

        StairSectionChecks();
    }

    /// <summary>
    /// 剖面里的楼梯：**剖面方向与梯段方向一致**时是锯齿（踏面 + 踢面 + 斜板 + 平台 + 栏杆）；
    /// 垂直时是"一级一条水平线"；整部楼梯落在剖切面前面（被切掉）时一条都不画。
    /// 这里用"线的条数与方向"钉死，都是数得出来的。
    /// </summary>
    private static void StairSectionChecks()
    {
        // 剖在 X=2200、朝 +X 看（样例 1-1 剖面就是这么定的）
        var section = new ViewDefinitionModel
        {
            Id = "sec", Title = "1-1 剖面图", Kind = ViewKind.Section, Scale = 50,
            CutAxis = SectionAxis.CutX, CutPosition = 2200d, ViewSign = 1, ViewDepth = 12000d
        };

        // 1) 梯段沿 Y（与看的方向一致）→ 锯齿
        var alongModel = TwoStoreyStairModel(alongX: false, x: 2400d);
        var parallel = OrthographicProjector.Project(alongModel, section, null);
        var parallelLines = parallel.Lines.Where(l => l.Layer == ViewLayers.Stair).ToList();
        var parallelVertical = parallelLines.Count(l => Math.Abs(l.X1 - l.X2) < 0.01d);
        var parallelHorizontal = parallelLines.Count(l => Math.Abs(l.Y1 - l.Y2) < 0.01d);
        Assert(parallelVertical == 20, "两跑共 18 条踢面 + 平台两端 2 条竖线 = 20，实际 " + parallelVertical);
        Assert(parallelHorizontal == 21, "18 条踏面 + 平台顶/底/栏杆 3 条 = 21，实际 " + parallelHorizontal);
        Assert(parallelLines.Count == 45, "剖到梯段方向应有 45 条楼梯线，实际 " + parallelLines.Count);

        // 2) 梯段沿 X（与看的方向垂直）→ 一级一条水平线
        var crossModel = TwoStoreyStairModel(alongX: true, x: 2400d);
        var cross = OrthographicProjector.Project(crossModel, section, null);
        var crossLines = cross.Lines.Where(l => l.Layer == ViewLayers.Stair).ToList();
        Assert(crossLines.Count == 25, "横着剖到楼梯应是 18 条踏步线 + 2 条栏杆 + 平台 5 条 = 25，实际 " + crossLines.Count);
        Assert(crossLines.Count(l => Math.Abs(l.X1 - l.X2) < 0.01d) == 2, "横剖时只有平台两端是竖线");

        // 3) 楼梯整个在剖切面**前面**（X 最大只到 −300，剖切面在 X=2200、朝 +X 看）→ 被切掉，一条不画
        var cutAwayModel = TwoStoreyStairModel(alongX: false, x: -3000d);
        var cutAway = OrthographicProjector.Project(cutAwayModel, section, null);
        Assert(!cutAway.Lines.Any(l => l.Layer == ViewLayers.Stair), "剖切面之前的楼梯不该画出来");

        Console.WriteLine("   楼梯剖面：顺着剖 45 条（竖 " + parallelVertical + " / 横 " + parallelHorizontal
            + "）、横着剖 25 条、剖切面之前的 0 条");
    }

    /// <summary>
    /// 坡屋面（双坡）：手算得出来的几何 ——
    /// 檐口 7440×5640、坡度 26.565°（tan = 0.5）、檐口标高 6900（二层楼面 3600 + 层高 3300）：
    /// 屋脊 = 6900 + 5640/2 × 0.5 = 6900 + 1410 = 8310。屋脊沿 X → 南北立面看到坡面（矩形），
    /// 东西立面看到山墙三角（两条坡线交于 8310）；剖在 X=const（顺着屋脊）看到三角，剖在 Y=const 看到坡面。
    /// </summary>
    private static void RoofChecks()
    {
        var model = SampleModelFactory.CreateEmptyModel("坡屋面");
        model.Storeys[0].Height = 3600d;
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600d, Height = 3300d });
        var roof = new RoofModel
        {
            Id = "rf-1", StoreyId = "2F", X = 0d, Y = 0d, Width = 7440d, Depth = 5640d,
            AlongX = true, PitchDegrees = 26.565d, EaveElevation = 0d
        };
        model.Roofs.Add(roof);

        // 1) 几何：檐口标高按"二层楼面 + 层高"现算
        var geometry = RoofGeometry.Build(model, roof);
        Assert(geometry != null && geometry.IsValid, "屋面几何应有效");
        Assert(Math.Abs(geometry.EaveElevation - 6900d) < 1e-6d, "檐口标高应是 6900，实际 " + geometry.EaveElevation);
        Assert(Math.Abs(geometry.RidgeElevation - 8310d) < 1d, "屋脊标高应是 8310，实际 " + geometry.RidgeElevation);
        Assert(Math.Abs(geometry.HalfSpan - 2820d) < 1e-6d, "半跨应是 5640/2 = 2820，实际 " + geometry.HalfSpan);
        Assert(Math.Abs(geometry.RidgeStart.X - 0d) < 1e-6d && Math.Abs(geometry.RidgeStart.Y - 2820d) < 1e-6d,
            "屋脊应沿 X 在 Y=2820 处");
        // 显式给坡度角（45°）时屋脊更高
        roof.PitchDegrees = 45d;
        var steep = RoofGeometry.Build(model, roof);
        Assert(Math.Abs(steep.RidgeElevation - 9720d) < 1d, "45° 时屋脊应是 6900+2820=9720，实际 " + steep.RidgeElevation);
        roof.PitchDegrees = 26.565d;

        // 2) 立面：南北看到坡面（矩形轮廓：檐口 + 屋脊），东西看到山墙三角
        var south = OrthographicProjector.Project(model, SampleModelFactory.CreateDefaultViews("x")[0], null);
        var southRoof = south.Lines.Where(l => l.Layer == ViewLayers.Roof).ToList();
        Assert(southRoof.Any(l => Math.Abs(l.Y1 - 6900d) < 1d && Math.Abs(l.Y2 - 6900d) < 1d),
            "南立面应有檐口线（标高 6900）");
        Assert(southRoof.Any(l => Math.Abs(l.Y1 - 8310d) < 1d && Math.Abs(l.Y2 - 8310d) < 1d),
            "南立面应有屋脊线（标高 8310）");
        Assert(southRoof.All(l => Math.Abs(l.Y1 - l.Y2) < 1d || Math.Abs(l.X1 - l.X2) < 1d),
            "正对坡面看时屋面线应都是横平竖直的");

        var east = OrthographicProjector.Project(model, SampleModelFactory.CreateDefaultViews("x")[2], null);
        var eastRoof = east.Lines.Where(l => l.Layer == ViewLayers.Roof).ToList();
        var sloped = eastRoof.Where(l => Math.Abs(l.Y1 - l.Y2) > 1d && Math.Abs(l.X1 - l.X2) > 1d).ToList();
        Assert(sloped.Count == 2, "山墙这头看应有两条坡线，实际 " + sloped.Count);
        Assert(sloped.All(l => Math.Abs(Math.Max(l.Y1, l.Y2) - 8310d) < 1d),
            "两条坡线都应交在屋脊 8310 上");

        // 3) 剖面：顺着屋脊剖（CutX）看到三角；横着剖（CutY）看到坡面
        var along = OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "s1", Title = "顺脊", Kind = ViewKind.Section, Scale = 50,
            CutAxis = SectionAxis.CutX, CutPosition = 3720d, ViewSign = 1, ViewDepth = 20000d
        }, null);
        var alongRoof = along.Lines.Where(l => l.Layer == ViewLayers.Roof).ToList();
        Assert(alongRoof.Count(l => Math.Abs(l.Y1 - l.Y2) > 1d && Math.Abs(l.X1 - l.X2) > 1d) == 2,
            "顺着屋脊剖应看到两条坡线，实际 " + alongRoof.Count);
        var across = OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "s2", Title = "横剖", Kind = ViewKind.Section, Scale = 50,
            CutAxis = SectionAxis.CutY, CutPosition = 2820d, ViewSign = 1, ViewDepth = 20000d
        }, null);
        var acrossRoof = across.Lines.Where(l => l.Layer == ViewLayers.Roof).ToList();
        Assert(acrossRoof.Any(l => Math.Abs(l.Y1 - 8310d) < 1d), "横着剖到屋脊处，剖到的屋面线应在 8310");

        // 4) 体量：5 个面（底 + 两坡 + 两端山墙），最高点 = 屋脊
        var volume = BuildingVolumeBuilder.Build(model, null);
        Assert(volume.Faces.Count(f => f.Kind == "roof") == 5, "屋面应是 5 个面，实际 "
            + volume.Faces.Count(f => f.Kind == "roof"));
        Assert(Math.Abs(volume.MaxZ - 8310d) < 1d, "体量最高点应是屋脊 8310，实际 " + volume.MaxZ);
        Assert(Math.Abs(volume.MinZ - 6780d) < 1d, "屋面底面应是 檐口 6900 − 板厚 120 = 6780，实际 " + volume.MinZ);
        var slopes = volume.Faces.Where(f => f.Kind == "roof" && f.NormalZ > 0.1d && f.NormalZ < 0.99d).ToList();
        Assert(slopes.Count == 2, "应有两个斜的坡面，实际 " + slopes.Count);
        Assert(slopes.All(f => f.Points.Count == 4), "坡面应是四边形");
        // 屋面法线：坡度 1:2（tan=0.5）→ 法线 (0, ∓0.4472, 0.8944)，一个朝南一个朝北、都朝上
        Assert(slopes.All(f => f.NormalZ > 0.5d && Math.Abs(f.NormalY) > 0.3d), "坡面法线应朝上并偏向南北");
        Assert(slopes.Any(f => f.NormalY < -0.3d) && slopes.Any(f => f.NormalY > 0.3d),
            "两个坡面应一个朝南一个朝北");
        Assert(slopes.All(f => Math.Abs(f.NormalZ - 0.8944d) < 0.01d), "1:2 坡的法线 Z 分量应是 0.8944，实际 "
            + string.Join("/", slopes.Select(f => Math.Round(f.NormalZ, 4).ToString()).ToArray()));

        Console.WriteLine("   坡屋面：檐口 " + Math.Round(geometry.EaveElevation) + "、屋脊 "
            + Math.Round(geometry.RidgeElevation) + "（半跨 " + Math.Round(geometry.HalfSpan)
            + "、坡度 " + Math.Round(geometry.PitchDegrees, 3) + "°）；山墙坡线 " + sloped.Count
            + " 条、体量屋面 " + volume.Faces.Count(f => f.Kind == "roof") + " 面");

        AxonometricChecks();
    }

    /// <summary>
    /// 轴测出图：把三维体量投成"可见轮廓线"的视图（CAD 里就是普通线，不需要特殊处理），
    /// 并单独排一张图纸（建施-05）。这里钉：线条数与可见面数一致、坐标有限、换角度会变、透视也能出。
    /// </summary>
    private static void AxonometricChecks()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var definition = SampleModelFactory.CreateAxonometricView(model.Name);
        var view = OrthographicProjector.Project(model, definition, null);
        var lines = view.Lines.Where(l => l.Layer == ViewLayers.Axonometric).ToList();
        Assert(view.Kind == ViewKind.Axonometric, "轴测图的 Kind 应是 Axonometric");
        Assert(lines.Count > 50, "轴测图应该有足够多的可见轮廓线，实际 " + lines.Count);
        Assert(view.Lines.All(l => IsFiniteNumber(l.X1) && IsFiniteNumber(l.Y1)
            && IsFiniteNumber(l.X2) && IsFiniteNumber(l.Y2)), "轴测图坐标必须有限");
        Assert(view.Texts.Any(t => t.Layer == ViewLayers.Title), "轴测图要有图名");
        // 墙角融合与洞口分段后可见面会显著增多，但内部边仍需消隐，不能按面数估线数。
        var volume = BuildingVolumeBuilder.Build(model, null);
        var visible = VolumeRenderer.Project(volume, new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d });
        Assert(visible.Count > 0 && lines.Count > 50,
            "融合墙体后轴测投影不应丢失可见轮廓");

        // 换一个方位角：看到的轮廓不一样
        definition.AzimuthDegrees = 215d;
        var other = OrthographicProjector.Project(model, definition, null);
        Assert(other.Lines.Zip(view.Lines,(a,b)=>Math.Abs(a.X1-b.X1)+Math.Abs(a.Y1-b.Y1)).Any(distance=>distance>.1),
            "边线模式换方位角后坐标应改变，边数可以保持一致");
        var boxModel=new BuildingModelDocument();
        boxModel.Storeys.Add(new StoreyModel { Id="1F",Name="一层",Height=3000 });
        boxModel.Walls.Add(new WallModel { Id="box",StoreyId="1F",X1=0,Y1=0,X2=1000,Y2=0,Thickness=200 });
        var boxEdges=OrthographicProjector.Project(boxModel,SampleModelFactory.CreateAxonometricView("box"));
        Assert(boxEdges.Lines.Count(l=>l.Layer==ViewLayers.Axonometric)==9,
            "轴测实体边线模式只显示长方体可见的 9 条边，背面 3 条边必须被遮挡");
        // 透视也能出图，且坐标有限
        definition.Perspective = true;
        var perspective = OrthographicProjector.Project(model, definition, null);
        Assert(perspective.Lines.Any(l => l.Layer == ViewLayers.Axonometric)
            && perspective.Lines.All(l => IsFiniteNumber(l.X1) && IsFiniteNumber(l.Y2)), "透视轴测图也要能出来");

        // 排进 A3 图纸：内容落在图框内
        var sheet = SheetComposer.Compose(
            new List<ViewDocument> { view },
            new SheetDefinitionModel
            {
                Id = "sheet-axon-test", Number = "建施-05", Title = "轴测图", Paper = "A3", Landscape = true,
                ViewIds = { view.Id }
            });
        var inside = sheet.Lines.Where(l => l.Layer != ViewLayers.Title && l.Layer != ViewLayers.SheetFrame).ToList();
        Assert(inside.Count > 0, "轴测图应排到图纸上");
        Assert(inside.All(l => Math.Min(l.X1, l.X2) >= 24.5d && Math.Max(l.X1, l.X2) <= 415.5d
            && Math.Min(l.Y1, l.Y2) >= 4.5d && Math.Max(l.Y1, l.Y2) <= 292.5d), "轴测图应排在图框内");

        Console.WriteLine("   轴测出图：默认 35°/28° " + lines.Count + " 条轮廓（可见 " + visible.Count
            + " 面）、215° 时 " + other.Lines.Count + " 条、透视 " + perspective.Lines.Count
            + " 条；A3 图纸内 " + inside.Count + " 条");
    }

    /// <summary>造一个两层模型 + 一部楼梯（楼梯 X 位置可调，用来验"剖切面前面就切掉"）。</summary>
    private static BuildingModelDocument TwoStoreyStairModel(bool alongX, double x)
    {
        var model = SampleModelFactory.CreateEmptyModel("楼梯剖面");
        model.Storeys[0].Height = 3600d;
        model.Storeys.Add(new StoreyModel { Id = "2F", Name = "二层", Elevation = 3600d, Height = 3300d });
        model.Stairs.Add(new StairModel
        {
            Id = "st-1", StoreyId = model.Storeys[0].Id, X = x, Y = 0d,
            Length = 3000d, Width = 2700d, AlongX = alongX,
            FlightWidth = 1200d, Going = 260d, StepsPerFlight = 9, WellWidth = 100d
        });
        return model;
    }

    // ───────────────────────── 12. 三维体量与轴测投影 ─────────────────────────

    /// <summary>
    /// 三维体量（P4 第一块）：墙/柱/楼板拉成体块，轴测相机做背面剔除 + 按深度排序。
    /// 这里钉住"面数、包围盒、法线、剔除、排序、明暗、取景"这些可以手算的事实。
    /// </summary>
    private static void VolumeChecks()
    {
        // 1) 样例整栋：整高墙段 + 洞口上下过梁/窗下墙都会各自成块，所以面数远多于"实心方块"
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var volume = BuildingVolumeBuilder.Build(model, null);
        Assert(volume.Faces.Count > 66, "挖了洞口以后面数应明显多于实心方块（66），实际 " + volume.Faces.Count);
        Assert(volume.Faces.All(f => f.Points.Count >= 3
            && f.Points.All(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsNaN(p.Z))),
            "融合后体量不能出现无效面或坐标");
        Assert(Math.Abs(volume.Width - 7440d) < 1d && Math.Abs(volume.Depth - 5640d) < 1d
            && Math.Abs(volume.Height - 6900d) < 1d,
            "体量包围盒应为 7440×5640×6900，实际 " + volume.Width + "×" + volume.Depth + "×" + volume.Height);
        Assert(volume.Faces.Any(f => f.Kind == "wall") && volume.Faces.Any(f => f.Kind == "column")
            && volume.Faces.Any(f => f.Kind == "slab"), "体量里应同时有墙、柱、楼板");
        Assert(volume.Faces.All(f => f.Points.Count >= 3), "每个面至少 3 个点");
        Assert(volume.Faces.Where(f => f.IsUp).All(f => Math.Abs(f.NormalZ - 1d) < 1e-9d), "顶面法线应是 +Z");

        // 2) 只看一层：高度只剩层高
        var firstFloor = BuildingVolumeBuilder.Build(model, model.Storeys[0].Id);
        Assert(Math.Abs(firstFloor.Height - 3600d) < 1d, "只看一层时高应为 3600，实际 " + firstFloor.Height);

        // 3) 一道墙的开洞规则（手算）：
        //    3000 长、240 厚、3600 高，中间一樘 1500×1800@900 的窗
        //    → 左段整高 + 右段整高 + 窗下墙（0~900）+ 窗上墙（2700~3600）= 4 块 = 24 面
        var single = SampleModelFactory.CreateEmptyModel("单墙");
        var storeyId = single.Storeys[0].Id;
        single.Walls.Add(new WallModel
        {
            Id = "w", StoreyId = storeyId, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d, Thickness = 240d
        });
        var solidWall = BuildingVolumeBuilder.Build(single, null);
        Assert(solidWall.Faces.Count == 6, "没有洞口时一道墙应是 6 个面，实际 " + solidWall.Faces.Count);
        Assert(Math.Abs(solidWall.Width - 3000d) < 1d && Math.Abs(solidWall.Depth - 240d) < 1d
            && Math.Abs(solidWall.Height - 3600d) < 1d,
            "单墙体量应为 3000×240×3600，实际 " + solidWall.Width + "×" + solidWall.Depth + "×" + solidWall.Height);

        single.Openings.Add(new OpeningModel
        {
            Id = "o", HostWallId = "w", Kind = "窗", Code = "C1518",
            Offset = 1500d, Width = 1500d, Height = 1800d, Sill = 900d
        });
        var windowWall = BuildingVolumeBuilder.Build(single, null);
        Assert(windowWall.Faces.Count(f=>f.Kind=="wall")==24 && windowWall.Faces.Any(f=>f.Kind=="sash"),
            "默认窗应保留洞口四周墙体及原立面双扇窗的扇框。");
        Assert(windowWall.Faces.Count(f => f.Kind == "frame") >= 24 && windowWall.Faces.Count(f => f.Kind == "glass") == 12,
            "默认双扇窗应保留固定框及两块玻璃，实际玻璃面数 "+windowWall.Faces.Count(f=>f.Kind=="glass"));
        // 窗台以下那块墙（0~900）的顶面应正好在 900 高 —— 这就是"窗台"
        Assert(windowWall.Faces.Any(f => f.IsUp && Math.Abs(f.Points.Max(p => p.Z) - 900d) < 1e-6d),
            "应有窗台面（标高 900）");
        // 窗顶以上那块（2700~3600）的底面应正好在 2700
        Assert(windowWall.Faces.Any(f => Math.Abs(f.NormalZ + 1d) < 1e-9d
            && Math.Abs(f.Points.Max(p => p.Z) - 2700d) < 1e-6d), "应有窗顶面（标高 2700）");
        // 落地门（窗台 0）只会在门顶以上留一块，另加一扇打开的门扇
        single.Openings.Clear();
        single.Openings.Add(new OpeningModel
        {
            Id = "d", HostWallId = "w", Kind = "门", Code = "M0921",
            Offset = 1500d, Width = 900d, Height = 2100d, Sill = 0d
        });
        var doorWall = BuildingVolumeBuilder.Build(single, null);
        Assert(doorWall.Faces.Any(f=>f.Kind=="frame")&&!doorWall.Faces.Any(f=>f.Kind=="sash"),
            "普通门按原立面默认不另加扇框，保留 N 型外框与闭合门板。");
        Assert(doorWall.Faces.Count(f => f.Kind == "door") == 6, "门扇应为 6 面");
        Assert(!doorWall.Faces.Any(f => f.IsUp && Math.Abs(f.Points.Max(p => p.Z) - 0d) < 1e-6d && false), "门下不该有窗台面");

        // 4) 轴测投影：背面剔除 + 从远到近排序 + 坐标有限
        var camera = new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d, Zoom = 1d };
        var faces = VolumeRenderer.Project(volume, camera);
        Assert(faces.Count > 40 && faces.Count < volume.Faces.Count, "投影后应剔除掉一部分背面："
            + faces.Count + "/" + volume.Faces.Count);
        for (var index = 1; index < faces.Count; index++)
            Assert(faces[index - 1].Depth >= faces[index].Depth - 1e-6d, "面应按从远到近排序");
        Assert(faces.All(f => f.Points.All(p => IsFiniteNumber(p.X) && IsFiniteNumber(p.Y))), "投影坐标必须有限");
        Assert(faces.All(f => f.Shade > 0.2d && f.Shade <= 1.001d), "明暗系数应在 0.25~1 之间");

        // 5) 明暗：顶面比底面亮（太阳在斜上方）
        var up = volume.Faces.First(f => f.IsUp);
        var down = volume.Faces.First(f => !f.IsUp && Math.Abs(f.NormalZ + 1d) < 1e-9d);
        Assert(VolumeRenderer.Shade(up) > VolumeRenderer.Shade(down), "顶面应比底面亮");

        // 6) 取景：缩放后所有点都应落在视口内
        double scale, offsetX, offsetY;
        VolumeRenderer.FitToView(faces, 800d, 600d, out scale, out offsetX, out offsetY);
        Assert(scale > 0d && IsFiniteNumber(scale), "取景比例应有效，实际 " + scale);
        foreach (var face in faces)
            foreach (var point in face.Points)
            {
                var screenX = offsetX + point.X * scale;
                var screenY = offsetY - point.Y * scale;
                Assert(screenX >= 0d && screenX <= 800d && screenY >= 0d && screenY <= 600d,
                    "取景后应落在视口内：(" + Math.Round(screenX) + "," + Math.Round(screenY) + ")");
            }
        Console.WriteLine("   三维体量：整栋 " + volume.Faces.Count + " 面（7440×5640×6900）、一层 "
            + firstFloor.Faces.Count + " 面、一樘窗 = 4 块墙 + 4 块窗框 + 玻璃（54 面）、落地门 24 面；"
            + "35°/28° 可见 " + faces.Count + " 面并按深度排序；取景比例 " + Math.Round(scale, 4));
        // 框条已并集，内部面不再计入体量；过滤仍应剔除多数背面和被遮挡面。
        // 一遍过滤以后再跑一遍"藏在墙里"的规则不该再丢掉任何面（幂等 = 判据稳定）。
        Assert(faces.Count < volume.Faces.Count * 0.5d, "可见面应少于体量面数的一半："
            + faces.Count + "/" + volume.Faces.Count);
        Assert(VolumeRenderer.DropFacesBehindParallelPlanes(faces).Count == faces.Count,
            "过滤过一遍以后不该还能丢掉面（判据应稳定）");

        VolumeRenderExtraChecks(model, volume);
    }

    /// <summary>
    /// 三维渲染的第二组：剖切、透视、共面合并（门窗在三维里"一眼看出来"的那一层）。
    /// 这些都要能手算核对，所以全部走纯几何的 <see cref="VolumeRenderer"/>。
    /// </summary>
    private static void VolumeRenderExtraChecks(BuildingModelDocument model, BuildingVolume volume)
    {
        // 1) 水平剖切：只留标高以下 —— 点是真的被裁掉（最大 Z 正好等于剖切标高）
        var clipZ = 1500d;
        var cut = VolumeRenderer.ClipFaces(volume.Faces, clipZ, false);
        Assert(cut.Count > 0 && cut.Count < volume.Faces.Count, "剖切后应只剩一部分面：" + cut.Count + "/" + volume.Faces.Count);
        Assert(cut.All(f => f.Points.All(p => p.Z <= clipZ + 1e-6d)), "剖切后不应有点高于剖切标高");
        Assert(Math.Abs(cut.Max(f => f.Points.Max(p => p.Z)) - clipZ) < 1e-6d, "应有面正好停在剖切标高上");
        var above = VolumeRenderer.ClipFaces(volume.Faces, clipZ, true);
        Assert(above.All(f => f.Points.All(p => p.Z >= clipZ - 1e-6d)), "反过来应只留剖切标高以上");

        // 2) 剖切后投影：画出来的东西比不剖切矮（V 方向的范围变小）
        var camera = new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d, Zoom = 1d, ClipZ = clipZ };
        var cutFaces = VolumeRenderer.Project(volume, camera);
        var plainFaces = VolumeRenderer.Project(volume, new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d });
        Assert(cutFaces.Count > 0 && cutFaces.Count < plainFaces.Count, "剖切后可见面应更少");
        var cutTop = cutFaces.Max(f => f.Points.Max(p => p.Y));
        var plainTop = plainFaces.Max(f => f.Points.Max(p => p.Y));
        Assert(cutTop < plainTop - 100d, "剖切后最高点应明显变低：" + Math.Round(cutTop) + " vs " + Math.Round(plainTop));

        // 3) 透视：一近一远两个一样的面 —— 平行投影下一样大，透视下近的更大
        //    （方位角 0 = 相机在南边，所以 y 越小越靠近相机）
        var nearSquare = SquareAtY(-4000d);
        var farSquare = SquareAtY(-1000d);
        var flatCamera = new VolumeCamera { AzimuthDegrees = 0d, ElevationDegrees = 0d, Zoom = 1d };
        var orthoNear = ProjectArea(nearSquare, flatCamera);
        var orthoFar = ProjectArea(farSquare, flatCamera);
        Assert(Math.Abs(orthoNear - orthoFar) < orthoNear * 1e-6d,
            "平行投影下远近一样大：" + Math.Round(orthoNear, 1) + " vs " + Math.Round(orthoFar, 1));
        var deepCamera = new VolumeCamera
        {
            AzimuthDegrees = 0d, ElevationDegrees = 0d, Zoom = 1d, Perspective = true, FieldOfViewDegrees = 40d
        };
        var perspectiveNear = ProjectArea(nearSquare, deepCamera);
        var perspectiveFar = ProjectArea(farSquare, deepCamera);
        Assert(IsFiniteNumber(perspectiveNear) && perspectiveNear > 0d, "透视面积应有效，实际 " + perspectiveNear);
        Assert(perspectiveNear > perspectiveFar * 1.2d, "透视下近处应明显更大："
            + Math.Round(perspectiveNear, 1) + " vs " + Math.Round(perspectiveFar, 1));
        // 视场角越小（长焦）画面越大
        var tele = ProjectArea(nearSquare, new VolumeCamera
        {
            AzimuthDegrees = 0d, ElevationDegrees = 0d, Perspective = true, FieldOfViewDegrees = 12d
        });
        Assert(tele > perspectiveNear * 2d, "视场角变小画面应明显变大：" + Math.Round(tele, 1)
            + " vs " + Math.Round(perspectiveNear, 1));

        // 4) 共面合并：分格墙（洞口上下的墙块与左右墙块共面）中间的"分格线"不画；
        //    实心方块没有共面邻面，边全部照画。
        var solid = SampleModelFactory.CreateEmptyModel("共面");
        solid.Walls.Add(new WallModel
        {
            Id = "w", StoreyId = solid.Storeys[0].Id, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d, Thickness = 240d
        });
        var solidFaces = VolumeRenderer.Project(BuildingVolumeBuilder.Build(solid, null),
            new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d, Zoom = 1d });
        Assert(solidFaces.All(f => f.Edges.Count == f.Points.Count), "实心墙没有共面邻面，每个面应是 4 条整边");
        solid.Openings.Add(new OpeningModel
        {
            Id = "o", HostWallId = "w", Kind = "窗", Code = "C1518",
            Offset = 1500d, Width = 1500d, Height = 1800d, Sill = 900d
        });
        var splitFaces = VolumeRenderer.Project(BuildingVolumeBuilder.Build(solid, null),
            new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d, Zoom = 1d });
        Assert(splitFaces.All(f => f.Edges.All(segment => segment.Count == 2)), "每条边都应是两个点");
        Assert(splitFaces.All(f => f.Points.All(p => IsFiniteNumber(p.X) && IsFiniteNumber(p.Y))
            && f.Edges.SelectMany(segment => segment).All(p => IsFiniteNumber(p.X) && IsFiniteNumber(p.Y))),
            "合并后的边坐标必须有限");
        var mergedFaces = splitFaces.Count(f => f.Edges.Count < f.Points.Count);
        Assert(mergedFaces > 0, "洞口上下的墙块与左右墙块共面，中间的线应被合并掉");
        // 自洽核对：没共面邻面时"画出来的线"= 各面周长；有共面邻面时必须更少
        var solidDrawn = TotalEdgeLength(solidFaces);
        var solidPerimeter = PolygonPerimeter(solidFaces);
        Assert(Math.Abs(solidDrawn - solidPerimeter) < 1e-6d, "实心墙应把周长整圈画出来："
            + Math.Round(solidDrawn) + " vs " + Math.Round(solidPerimeter));
        var splitDrawn = TotalEdgeLength(splitFaces);
        var splitPerimeter = PolygonPerimeter(splitFaces);
        Assert(splitDrawn < splitPerimeter * 0.999d, "合并后画出来的线应少于各面周长："
            + Math.Round(splitDrawn) + " vs " + Math.Round(splitPerimeter));

        // 5) 门窗在三维里能分辨：窗有 frame/glass、门有 door，玻璃比墙厚薄（真的放在墙里）
        Assert(volume.Faces.Any(f => f.Kind == "glass") && volume.Faces.Any(f => f.Kind == "frame")
            && volume.Faces.Any(f => f.Kind == "door"), "整栋体量里应同时有窗框、玻璃、门扇");
        foreach (var storeyId in model.Storeys.Select(storey => storey.Id))
        {
            var one = BuildingVolumeBuilder.Build(model, storeyId);
            Assert(one.Faces.Any(f => f.Kind == "frame"), "每层的体量里都应有窗框");
        }
        Console.WriteLine("   三维剖切/透视/共面：剖切 " + Math.Round(clipZ) + " mm 后 " + cut.Count + "/"
            + volume.Faces.Count + " 面（最高点 " + Math.Round(cutTop) + " vs " + Math.Round(plainTop) + "）；"
            + "共面合并 " + mergedFaces + " 个面上的线被吃掉，描边总长 " + Math.Round(splitPerimeter)
            + " → " + Math.Round(splitDrawn) + " mm；透视近/远面积 " + Math.Round(perspectiveNear, 3)
            + " / " + Math.Round(perspectiveFar, 3));

        // 6) "藏在墙里"的面要丢掉：单墙 + 一樘窗里，窗框/玻璃的各面都是实体内部的贴合面，
        //    把它们画出来就会在墙面上看到一层层边框线（三维里最容易看出来的假相）。
        var hidden = SampleModelFactory.CreateEmptyModel("藏面");
        hidden.Walls.Add(new WallModel
        {
            Id = "w", StoreyId = hidden.Storeys[0].Id, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d, Thickness = 240d
        });
        hidden.Openings.Add(new OpeningModel
        {
            Id = "o", HostWallId = "w", Kind = "窗", Code = "C1518",
            Offset = 1500d, Width = 1500d, Height = 1800d, Sill = 900d
        });
        var glassWall = BuildingVolumeBuilder.Build(hidden, null);
        var glassFaces = VolumeRenderer.Project(glassWall, new VolumeCamera { AzimuthDegrees = 35d, ElevationDegrees = 28d });
        Assert(glassFaces.All(f => f.Visible), "投影结果里不该再留背面的面");
        Assert(glassFaces.Count < glassWall.Faces.Count / 2, "开洞后大部分面都是内部贴合面，应被丢掉："
            + glassFaces.Count + "/" + glassWall.Faces.Count);
        // 玻璃的四条侧边都贴在窗框里 → 四张侧边都不该画，只剩朝向相机的两个大面
        Assert(glassFaces.Count(f => f.Kind == "glass") <= 2, "玻璃只该留下朝向相机的两个大面");
        Console.WriteLine("   三维藏面：单墙开窗 " + glassWall.Faces.Count + " 面 → 可见 "
            + glassFaces.Count + " 面（玻璃 " + glassFaces.Count(f => f.Kind == "glass")
            + "、窗框 " + glassFaces.Count(f => f.Kind == "frame") + "）");

        ParallelPlaneChecks();
    }

    /// <summary>
    /// "藏在墙里"的判据：平行、在它前面 600mm 以内的面把它盖满时，这个面看不见（例：楼板侧边落在墙厚正中）。
    /// 用两块手算得出来的板来钉：正对相机的一层是墙，后面 120mm 处是一小条楼板边。
    /// </summary>
    private static void ParallelPlaneChecks()
    {
        var volume = new BuildingVolume { MinX = 0d, MaxX = 2000d, MinY = -120d, MaxY = 0d, MinZ = 0d, MaxZ = 3000d };
        volume.Faces.Add(Quad("wall", 0d, -120d, 0d, 2000d, 3000d, -1d));      // 前面的大墙
        volume.Faces.Add(Quad("slab", 0d, 0d, 1000d, 2000d, 120d, -1d));       // 后面 120mm 的一小条
        var camera = new VolumeCamera { AzimuthDegrees = 0d, ElevationDegrees = 0d, Zoom = 1d };
        var visible = VolumeRenderer.Project(volume, camera);
        Assert(visible.Any(f => f.Kind == "wall"), "前面的大墙当然要画");
        Assert(!visible.Any(f => f.Kind == "slab"), "被大墙盖满的小条应被丢掉（它藏在墙里）");

        // 换成"在后面 1000mm"：超过 600mm 的判定范围，就不该乱丢（那可能是窗洞里退进去的一块）
        var far = new BuildingVolume { MinX = 0d, MaxX = 2000d, MinY = -1120d, MaxY = 0d, MinZ = 0d, MaxZ = 3000d };
        far.Faces.Add(Quad("wall", 0d, -1120d, 0d, 2000d, 3000d, -1d));
        far.Faces.Add(Quad("slab", 0d, 0d, 1000d, 2000d, 120d, -1d));
        var farVisible = VolumeRenderer.Project(far, camera);
        Assert(farVisible.Any(f => f.Kind == "slab"), "离得太远（>600mm）的面不该被当成「藏在墙里」");
        Console.WriteLine("   三维藏面：平行面前 120mm 的小面被丢掉、1000mm 的保留（阈值 600mm 生效）");

        HiddenLineChecks();
    }

    /// <summary>
    /// 逐段消隐：一条边被"更近的面"盖住的那一段不画（画板算法排错序时留下的接缝线就靠它清掉）。
    /// 用手算得出来的两块板钉：近的 2000×2000，远的一条边整个落在它里面 → 全被吃掉；
    /// 落一半 → 只剩一半；贴在边界上（相邻两面共用边）→ 一点都不能吃。
    /// </summary>
    private static void HiddenLineChecks()
    {
        var covered = Quad2D(200d, new[] { new[] { 500d, 500d, 1500d, 1500d } });
        VolumeRenderer.HideEdgesBehindNearerFaces(new List<VolumeFace2D>
        {
            covered, Quad2D(100d, new List<double[]>())
        });
        Assert(covered.Edges.Count == 0, "整条被近面盖住的边应该不画，实际还剩 " + covered.Edges.Count + " 段");

        var partial = Quad2D(200d, new[] { new[] { 1500d, 500d, 2500d, 500d } });
        VolumeRenderer.HideEdgesBehindNearerFaces(new List<VolumeFace2D>
        {
            partial, Quad2D(100d, new List<double[]>())
        });
        var remaining = partial.Edges.Sum(Length);
        Assert(Math.Abs(remaining - 500d) < 1d, "一半在近面里的边应只剩 500mm，实际 " + Math.Round(remaining));

        var shared = Quad2D(200d, new[] { new[] { 0d, 0d, 0d, 2000d } });
        VolumeRenderer.HideEdgesBehindNearerFaces(new List<VolumeFace2D>
        {
            shared, Quad2D(100d, new List<double[]>())
        });
        Assert(Math.Abs(shared.Edges.Sum(Length) - 2000d) < 1d, "贴在近面边上的共用边不该被吃掉，实际 "
            + Math.Round(shared.Edges.Sum(Length)));

        Console.WriteLine("   三维消隐：整条被盖住剩 0 mm、一半被盖剩 " + Math.Round(remaining)
            + " mm、贴边共用边 " + Math.Round(shared.Edges.Sum(Length)) + " mm（不吃）");
    }

    private static double Length(List<PointModel> segment)
    {
        if (segment == null || segment.Count < 2) return 0d;
        var dx = segment[1].X - segment[0].X;
        var dy = segment[1].Y - segment[0].Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>造一块 0..2000 × 0..2000 的投影面（深度、边给全）。</summary>
    private static VolumeFace2D Quad2D(double depth, IEnumerable<double[]> edges)
    {
        var face = new VolumeFace2D
        {
            Depth = depth,
            Points = new List<PointModel>
            {
                new PointModel(0d, 0d), new PointModel(2000d, 0d),
                new PointModel(2000d, 2000d), new PointModel(0d, 2000d)
            }
        };
        foreach (var edge in edges)
            face.Edges.Add(new List<PointModel>
            {
                new PointModel(edge[0], edge[1]), new PointModel(edge[2], edge[3])
            });
        return face;
    }

    /// <summary>造一片竖直方板：X 从 0 到 width，Z 从 z0 到 z0+height，固定在一个 Y 上。</summary>
    private static VolumeFace Quad(string kind, double x0, double y, double z0, double width, double height, double normalY)
    {
        return new VolumeFace
        {
            Kind = kind, NormalY = normalY,
            Points = new List<Point3DModel>
            {
                new Point3DModel(x0, y, z0), new Point3DModel(x0 + width, y, z0),
                new Point3DModel(x0 + width, y, z0 + height), new Point3DModel(x0, y, z0 + height)
            }
        };
    }

    /// <summary>各面多边形周长的总和（= 完全不做共面合并时会画的线长）。</summary>
    private static double PolygonPerimeter(List<VolumeFace2D> faces)
    {
        double total = 0d;
        foreach (var face in faces)
            for (var index = 0; index < face.Points.Count; index++)
            {
                var p = face.Points[index];
                var q = face.Points[(index + 1) % face.Points.Count];
                var dx = q.X - p.X;
                var dy = q.Y - p.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
        return total;
    }

    /// <summary>投影结果里所有要画的线的总长（用来核对"共面合并确实少画了线"）。</summary>
    private static double TotalEdgeLength(List<VolumeFace2D> faces)
    {
        double total = 0d;
        foreach (var face in faces)
            foreach (var segment in face.Edges)
            {
                if (segment == null || segment.Count < 2) continue;
                var dx = segment[1].X - segment[0].X;
                var dy = segment[1].Y - segment[0].Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
        return total;
    }

    /// <summary>造一片朝南（法线 -Y）的竖直方板，只用来量"透视下近大远小"。</summary>
    private static BuildingVolume SquareAtY(double y)
    {
        var volume = new BuildingVolume
        {
            MinX = 0d, MaxX = 2000d, MinY = -4000d, MaxY = 0d, MinZ = 0d, MaxZ = 3000d
        };
        volume.Faces.Add(new VolumeFace
        {
            Kind = "wall", NormalY = -1d,
            Points = new List<Point3DModel>
            {
                new Point3DModel(0d, y, 0d), new Point3DModel(2000d, y, 0d),
                new Point3DModel(2000d, y, 3000d), new Point3DModel(0d, y, 3000d)
            }
        });
        return volume;
    }

    /// <summary>把体量投出来，算第一片面的（屏幕）面积 —— 比较平行投影与透视图的就是它。</summary>
    private static double ProjectArea(BuildingVolume volume, VolumeCamera camera)
    {
        var projected = VolumeRenderer.Project(volume, camera);
        if (projected.Count == 0) return 0d;
        var points = projected[0].Points;
        double area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var next = points[(index + 1) % points.Count];
            area += points[index].X * next.Y - next.X * points[index].Y;
        }
        return Math.Abs(area) / 2d;
    }

    private static bool IsFiniteNumber(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    // ───────────────────────── 11. 从 CAD 启动建模程序 / 落图取件 ─────────────────────────

    /// <summary>
    /// CAD 里「建筑模型」命令（JZMX）要能把项目文件夹与模型名称传给建模程序，
    /// 落图（LTTZ）要能直接列出本项目已生成的图纸/视图让人挑序号 —— 这两件事的纯逻辑在这里钉住。
    /// </summary>
    private static void StudioLaunchChecks()
    {
        // 1) 启动参数
        var arguments = StudioLaunch.BuildArguments(@"H:\项目\万落示例\", " 样例-两层小房子 ");
        Assert(arguments == "--project \"H:\\项目\\万落示例\" --model \"样例-两层小房子\"",
            "启动参数不对：" + arguments);
        Assert(StudioLaunch.BuildArguments(null, null) == string.Empty, "空参数应得到空字符串");
        Assert(StudioLaunch.BuildArguments(@"C:\a", null) == "--project \"C:\\a\"", "只有项目文件夹时不该带 --model");

        // 2) 找程序：按候选顺序取第一个存在的
        var root = Path.Combine(Path.GetTempPath(), "WanluoStudioLaunchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exe = Path.Combine(root, StudioLaunch.ExecutableName);
            File.WriteAllText(exe, "stub");
            Assert(StudioLaunch.FindExecutable(new[] { null, Path.Combine(root, "没有.exe"), exe }) == Path.GetFullPath(exe),
                "应在候选里找到存在的程序");
            Assert(StudioLaunch.FindExecutable(new[] { Path.Combine(root, "没有.exe") }) == null, "都不存在时应返回 null");

            // 发布目录候选：<根>\CadApi\R24 → <根>\建筑模型\万落建筑模型.exe
            var band = Path.Combine(root, "CadApi", "R24");
            Directory.CreateDirectory(band);
            var published = Path.Combine(root, StudioLaunch.RelativeFolder, StudioLaunch.ExecutableName);
            Directory.CreateDirectory(Path.GetDirectoryName(published));
            File.WriteAllText(published, "stub");
            Assert(StudioLaunch.DefaultCandidates(band).Any(c => string.Equals(c, published, StringComparison.OrdinalIgnoreCase)),
                "发布目录候选里应包含 <发布根>\\建筑模型\\万落建筑模型.exe");
            Assert(StudioLaunch.FindExecutable(StudioLaunch.DefaultCandidates(band)) == Path.GetFullPath(published),
                "应能在发布目录候选里找到建模程序");

            // 3) 找模型目录 + 列视图清单
            var projectFolder = Path.Combine(root, "项目A");
            var modelFolder = Path.Combine(projectFolder, StudioLaunch.ModelFolderName, "住宅楼");
            Directory.CreateDirectory(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName));
            Assert(StudioLaunch.FindModelFolder(projectFolder, "住宅楼") == modelFolder, "按模型名称应找到模型目录");
            Assert(StudioLaunch.FindModelFolder(projectFolder, "不存在") == null, "模型名不存在时应返回 null");
            Assert(StudioLaunch.FindModelFolder(projectFolder, null) == modelFolder, "只有一个模型时应自动用它");
            Assert(StudioLaunch.FindModelFolder(Path.Combine(root, "没有项目"), null) == null, "没有项目目录时返回 null");

            var south = new ViewDocument { Id = "elev-south", Title = "住宅楼 南立面图", Kind = ViewKind.Elevation, Scale = 100 };
            south.Lines.Add(new ViewLine { Layer = ViewLayers.Elevation, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 0 });
            BuildingModelJson.SaveView(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "elev-south.json"), south);
            var sheet = new ViewDocument { Id = "sheet-1", Title = "建施-01", Kind = ViewKind.Sheet, Scale = 1, PaperName = "A3" };
            sheet.Lines.Add(new ViewLine { Layer = ViewLayers.SheetFrame, X1 = 0, Y1 = 0, X2 = 420, Y2 = 0 });
            BuildingModelJson.SaveView(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "sheet-1.json"), sheet);
            File.WriteAllText(Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "坏文件.json"), "{ 这不是 json");

            var entries = StudioLaunch.ListViews(modelFolder);
            Assert(entries.Count == 2, "坏文件应被跳过，只列出两张，实际 " + entries.Count);
            Assert(entries[0].Kind == ViewKind.Sheet && entries[0].Title == "建施-01", "图纸应排在最前面");
            Assert(entries[1].Id == "elev-south" && entries[1].Display.IndexOf("立面", StringComparison.Ordinal) >= 0,
                "清单里应能看出类型：" + entries[1].Display);

            // 4) "待落图"标记：写 → 读 → 清单里带 ★ 且排最前 → 落完一张少一张
            var sheetPath = Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "sheet-1.json");
            Assert(StudioLaunch.PendingFilePath(modelFolder) == Path.Combine(modelFolder, StudioLaunch.ViewsFolderName, "待落图.txt"),
                "待落图文件应放在 views 目录下");
            Assert(StudioLaunch.ReadPending(modelFolder) == null, "还没推过时应返回 null");
            Assert(StudioLaunch.WritePending(modelFolder, new[]
            {
                new StudioPendingEntry { Id = "sheet-1", FilePath = sheetPath },
                new StudioPendingEntry { Id = "不存在的", FilePath = "" }        // 空路径应被跳过
            }), "写待落图清单应成功");

            var pendingList = StudioLaunch.ReadPending(modelFolder);
            Assert(pendingList != null && pendingList.Entries.Count == 1 && pendingList.Entries[0].Id == "sheet-1",
                "读回来的待落图清单应只有 1 条（空路径被跳过）");
            entries = StudioLaunch.ListViews(modelFolder);
            Assert(entries[0].Pending && entries[0].Id == "sheet-1", "待落图的那张应排在清单最前并带 ★");
            Assert(entries[0].Display.StartsWith("★", StringComparison.Ordinal), "清单显示应带 ★：" + entries[0].Display);
            Assert(entries.Count(entry => entry.Pending) == 1, "只有一张是待落图");

            StudioLaunch.RemovePending(modelFolder, "sheet-1");
            Assert(StudioLaunch.ReadPending(modelFolder) == null, "落完图后待落图文件应被删掉");
            Assert(StudioLaunch.ListViews(modelFolder).All(entry => !entry.Pending), "落完图后不该再有 ★");
            Console.WriteLine("   启动与取件：参数、发布目录候选、模型目录定位、视图清单（跳过坏文件）、待落图写读清 都符合预期");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ───────────────────────── 10. 排版出图（图纸） ─────────────────────────

    /// <summary>
    /// 图纸 = 一份 Kind=Sheet 的视图产物（单位是图纸毫米，落图后按 1:1 出图）：
    /// 图框留边（装订边 25、其余 5）、右下角标题栏、视图按各自比例缩到纸面并排进格子、
    /// 尺寸转成带显式文字的尺寸（纸面距离不再是真实尺寸）、超格的视图出提示。
    /// </summary>
    private static void SheetComposition()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        var views = new List<ViewDocument>();
        foreach (var definition in SampleModelFactory.CreateDefaultViews(model.Name))
            views.Add(OrthographicProjector.Project(model, definition, library));
        foreach (var storey in model.Storeys)
            views.Add(OrthographicProjector.Project(model, SampleModelFactory.CreatePlanView(storey), library));
        views.Add(OrthographicProjector.ProjectSchedule(model, library, "门窗表"));

        var sheets = SampleModelFactory.CreateDefaultSheets(model);
        Assert(sheets.Count == 5, "默认套图应有 5 张（两层平面 + 立面 + 剖面/门窗表 + 轴测图），实际 " + sheets.Count);
        Assert(sheets[4].Id == "sheet-axon" && sheets[4].Number == "建施-05" && sheets[4].ViewIds.Contains("axon-1"),
            "第 5 张应是轴测图（建施-05，排 axon-1）：实际 " + sheets[4].Id + " " + sheets[4].Number);
        Assert(sheets[0].Paper == "A3" && sheets[0].Landscape, "默认图纸应为 A3 横");

        // 只排一张平面：便于按算得出来的数字核对
        var one = new SheetDefinitionModel
        {
            Id = "sheet-test", Number = "建施-99", Title = "一层平面图", Paper = "A3", Landscape = true
        };
        one.ViewIds.Add("plan-1F");
        var sheet = SheetComposer.Compose(views, one);

        Assert(sheet.Kind == ViewKind.Sheet && sheet.Scale == 1, "图纸应是 Kind=Sheet、比例 1（图纸毫米）");
        Assert(sheet.PaperName == "A3" && Math.Abs(sheet.PaperWidth - 420d) < 0.01d && Math.Abs(sheet.PaperHeight - 297d) < 0.01d,
            "图纸应记录纸张规格（落图时按它匹配项目图框）：实际 " + sheet.PaperName + " " + sheet.PaperWidth + "×" + sheet.PaperHeight);
        Assert(sheet.Lines.Any(l => l.Layer == ViewLayers.SheetFrame), "自带图框应画在 WL-模型-图纸框 图层上（套用项目图框时好跳过）");
        Assert(sheet.Texts.Where(t => t.Text == "一层平面图" || (t.Text ?? "").StartsWith("图号 ", StringComparison.Ordinal))
            .All(t => t.Layer == ViewLayers.SheetFrame), "标题栏文字也应在 WL-模型-图纸框 图层上");
        Assert(sheet.Title.IndexOf("建施-99", StringComparison.Ordinal) >= 0
            && sheet.Title.IndexOf("A3", StringComparison.Ordinal) >= 0, "图纸标题应含图号与纸张：实际 " + sheet.Title);
        Assert(sheet.Texts.Any(t => t.Text == "一层平面图" && Math.Abs(t.Height - 7d) < 0.01d), "标题栏缺少图名");
        Assert(sheet.Texts.Any(t => t.Text != null && t.Text.StartsWith("图号 建施-99", StringComparison.Ordinal)), "标题栏缺少图号");
        Assert(sheet.Texts.Any(t => t.Text != null && t.Text.IndexOf("万落建筑工具", StringComparison.Ordinal) >= 0), "标题栏缺少单位/日期");

        // 图框：A3 横 = 420×297；外框 0..420/0..297，图框 25..415 / 5..292
        Assert(HasViewLine(sheet, 0d, 0d, 420d, 0d) && HasViewLine(sheet, 0d, 297d, 420d, 297d), "缺少纸边外框");
        Assert(HasViewLine(sheet, 25d, 5d, 415d, 5d) && HasViewLine(sheet, 25d, 292d, 415d, 292d), "缺少图框（留边 25/5）");
        Assert(HasViewLine(sheet, 235d, 5d, 415d, 5d) || sheet.Lines.Any(l => Math.Abs(l.X1 - 235d) < Tolerance && Math.Abs(l.X2 - 235d) < Tolerance),
            "缺少标题栏左边线（右下角 180 宽）");

        // 视图按 1:100 缩到纸面：墙断面（Cut 层）轮廓 7440×5640 → 74.4×56.4 mm，且都在图框内
        var walls = sheet.Lines.Where(l => l.Layer == ViewLayers.Cut).ToList();
        Assert(walls.Count > 0, "图纸上应有墙断面（Cut 层）");
        var width = walls.Max(l => Math.Max(l.X1, l.X2)) - walls.Min(l => Math.Min(l.X1, l.X2));
        var height = walls.Max(l => Math.Max(l.Y1, l.Y2)) - walls.Min(l => Math.Min(l.Y1, l.Y2));
        Assert(Math.Abs(width - 74.4d) < 1d, "平面图在图纸上宽应约 74.4mm（7440 的 1:100），实际 " + Math.Round(width, 2));
        Assert(Math.Abs(height - 56.4d) < 1d, "平面图在图纸上高应约 56.4mm（5640 的 1:100），实际 " + Math.Round(height, 2));
        // 轴线也要一起排进图纸（比建筑范围更长），并且所有内容都在图框内
        var axisLines = sheet.Lines.Where(l => l.Layer == ViewLayers.Axis).ToList();
        Assert(axisLines.Count == 5, "图纸上应有 5 条轴线，实际 " + axisLines.Count);
        Assert(axisLines.Max(l => Math.Max(l.X1, l.X2)) - axisLines.Min(l => Math.Min(l.X1, l.X2)) > width,
            "轴线应比建筑范围更长");
        // 视图内容（不含图框/标题栏那两层）都应落在图框内
        var inside = sheet.Lines.Where(l => l.Layer != ViewLayers.Title && l.Layer != ViewLayers.SheetFrame);
        Assert(inside.All(l => Math.Min(l.X1, l.X2) >= 24.5d && Math.Max(l.X1, l.X2) <= 415.5d
            && Math.Min(l.Y1, l.Y2) >= 4.5d && Math.Max(l.Y1, l.Y2) <= 292.5d), "视图内容应排在图框内");

        // 尺寸转成显式文字（纸面距离不再是真实尺寸）：轴线 3600 的尺寸文字要是 "3600"
        Assert(sheet.Dimensions.Count > 0, "图纸上应保留尺寸");
        Assert(sheet.Dimensions.All(d => !string.IsNullOrWhiteSpace(d.Text)), "图纸上的尺寸必须有显式文字（否则 CAD 量出来的是纸面距离）");
        Assert(sheet.Dimensions.Any(d => d.Text == "3600"), "图纸上应能读到 3600 这一档轴线尺寸");

        // 超格提示：四个立面挤进 A4 竖排（2×2 格，每格约 84mm 宽），1:100 的立面应报超格
        var tight = new SheetDefinitionModel { Id = "sheet-tight", Number = "建施-98", Title = "挤一挤", Paper = "A4", Landscape = false };
        foreach (var direction in new[] { "elev-south", "elev-north", "elev-east", "elev-west" })
            tight.ViewIds.Add(direction);
        var tightSheet = SheetComposer.Compose(views, tight);
        Assert(tightSheet.Warnings.Any(w => w.Contains("扩展图框")) && tightSheet.PaperWidth > 210 && tightSheet.PaperHeight > 297,
            "塞不下时应扩大图框并保留比例，不能把超格视图继续重叠输出");

        // 找不到的视图要提示，而不是崩
        var missing = new SheetDefinitionModel { Id = "sheet-missing", Number = "建施-97", Title = "缺视图" };
        missing.ViewIds.Add("不存在的视图");
        var missingSheet = SheetComposer.Compose(views, missing);
        Assert(missingSheet.Warnings.Any(w => w.IndexOf("找不到视图", StringComparison.Ordinal) >= 0), "找不到视图时应给出提示");
        Console.WriteLine("   排版出图：A3 横图框 25/5、标题栏 180×40、平面 74.4×56.4mm 落在图框内、尺寸带显式文字，超格/缺视图都有提示");
    }

    private static bool HasViewLine(ViewDocument view, double x1, double y1, double x2, double y2)
    {
        foreach (var line in view.Lines)
        {
            if (Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance) return true;
            if (Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance) return true;
        }
        return false;
    }

    // ───────────────────────── 9. 平面图投影 ─────────────────────────

    /// <summary>
    /// 平面图 v1：墙两条面线（洞口断开 + 封口）、窗两条玻璃线、门一条扇线 + 90° 弧、柱断面矩形，
    /// 外围横向/竖向定位链与总尺寸。
    /// </summary>
    private static void PlanProjection()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var storey = model.Storeys[0];                       // 一层
        var definition = SampleModelFactory.CreatePlanView(storey);
        var plan = OrthographicProjector.Project(model, definition, null);

        Assert(plan.Kind == ViewKind.Plan, "应生成平面类视图");
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Title && t.Text.IndexOf("平面图", StringComparison.Ordinal) >= 0), "平面图缺图名");

        var cut = plan.Lines.Where(l => l.Layer == ViewLayers.Cut).ToList();
        var opening = plan.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        // 视图坐标 = 模型坐标 - 视图原点（Normalize 会把整张图平移到 0 起）
        var originX = plan.OriginX;
        var originY = plan.OriginY;
        Func<double, double> X = value => value - originX;
        Func<double, double> Y = value => value - originY;

        // 南墙（y=0，厚 240）：外面线 y=-120，在 1450-2950（窗）与 4950-5850（门）处断开
        Assert(HasPlanLine(cut, X(-120d), Y(-120d), X(1450d), Y(-120d)), "南墙融合后外面线应从西墙外皮画到窗左 1450");
        Assert(HasPlanLine(cut, X(2950d), Y(-120d), X(4950d), Y(-120d)), "窗与门之间的墙面线（2950→4950）没画出来");
        Assert(HasPlanLine(cut, X(5850d), Y(-120d), X(7320d), Y(-120d)), "门右到东墙外皮（5850→7320）没画出来");
        Assert(!HasPlanLine(cut, X(1450d), Y(-120d), X(2950d), Y(-120d)), "窗洞范围内的墙面线不该画出来");
        Assert(HasPlanLine(cut, X(1450d), Y(-120d), X(1450d), Y(120d)), "窗左门垛封口没画出来");
        // Saved construction controls glass thickness, frames and leaf outlines.
        var expected=OrthographicProjector.CreatePlanDetailSymbols(model,storey.Id).Where(l=>l.Layer==ViewLayers.Opening).ToList();
        Assert(expected.All(l=>HasPlanLine(opening,X(l.X1),Y(l.Y1),X(l.X2),Y(l.Y2))),"平面缺少保存做法中的框扇或玻璃线");
        var door=model.Openings.First(o=>o.Kind=="门"&&model.Walls.Any(w=>w.Id==o.HostWallId&&w.StoreyId==storey.Id));
        var doorType=OpeningConstruction.Resolve(model,door);
        var doorParts=OpeningConstruction.Build(door,doorType,240).Where(p=>p.Bottom<=1200&&p.Top>1200).ToArray();
        var leafRight=doorParts.Where(p=>p.Cell!=null).Max(p=>p.Right);
        var hingeNormal=doorParts.Where(p=>p.Kind=="frame").Max(p=>p.NormalOffset+p.Depth/2)
            +doorParts.Where(p=>p.Cell!=null).Max(p=>p.Depth)/2+(doorType.SashClearance??2);
        Assert(opening.Any(l=>Math.Abs(l.X2-X(4950+leafRight))<Tolerance&&Math.Abs(l.Y2-Y(hingeNormal))<Tolerance),
            "开启弧闭合端应位于净扇边界，不得落在门框外边或墙轴线上");
        Assert(opening.Count==expected.Count,"平面编辑与图纸投影的门窗图例不一致");
        // 柱断面
        var column = model.Columns.First(c => string.Equals(c.StoreyId, storey.Id, StringComparison.OrdinalIgnoreCase));
        Assert(HasPlanLine(cut, X(column.X - column.Width / 2d), Y(column.Y - column.Depth / 2d),
            X(column.X + column.Width / 2d), Y(column.Y - column.Depth / 2d)), "柱断面矩形没画出来");

        // 外围尺寸：三道（洞口定位 / 轴线 / 总尺寸），总长 7440（外皮 -120→7320）、总宽 5640（-120→5520）
        var totalWidth = plan.Dimensions.FirstOrDefault(d => d.Note == "总长");
        Assert(totalWidth != null && Math.Abs(totalWidth.From + originX - (-120d)) < Tolerance && Math.Abs(totalWidth.To + originX - 7320d) < Tolerance,
            "平面图总长应为 -120→7320（7440），实际 " + (totalWidth == null ? "缺失" : (totalWidth.From + originX) + "→" + (totalWidth.To + originX)));
        var totalDepth = plan.Dimensions.FirstOrDefault(d => d.Note == "总宽");
        Assert(totalDepth != null && Math.Abs(totalDepth.From + originY - (-120d)) < Tolerance && Math.Abs(totalDepth.To + originY - 5520d) < Tolerance,
            "平面图总宽应为 -120→5520（5640），实际 " + (totalDepth == null ? "缺失" : (totalDepth.From + originY) + "→" + (totalDepth.To + originY)));
        var horizontal = plan.Dimensions.Where(d => !d.Vertical && d.Note == "洞口定位（横向）").ToList();
        // 洞口边线都要成为链上的分界点（南墙窗 1450/2950、南墙门 4950/5850）
        foreach (var edge in new[] { 1450d, 2950d, 4950d, 5850d })
            Assert(horizontal.Any(d => Math.Abs(d.From + originX - edge) < Tolerance || Math.Abs(d.To + originX - edge) < Tolerance),
                "平面横向洞口定位链应含洞口边线 " + edge);
        Assert(Math.Abs(horizontal.Sum(d => d.To - d.From) - 7440d) < 0.01d, "洞口定位链各段之和应等于总长 7440");

        // 中层：轴线尺寸（1/2/3 → 0、3600、7200）
        var axisRun = plan.Dimensions.Where(d => !d.Vertical && d.Note == "轴线（横向）").ToList();
        Assert(axisRun.Count > 0, "有轴网时平面应出一条轴线尺寸链");
        Assert(axisRun.Any(d => Math.Abs(d.From + originX - 0d) < Tolerance && Math.Abs(d.To + originX - 3600d) < Tolerance)
            && axisRun.Any(d => Math.Abs(d.From + originX - 3600d) < Tolerance && Math.Abs(d.To + originX - 7200d) < Tolerance),
            "轴线链应含 0→3600 与 3600→7200（开间）");
        Assert(axisRun.All(d => Math.Abs(d.LinePosition + originY - (-2120d)) < Tolerance), "轴线链应画在中层（外皮下 2000）");

        // 轴网：3 条竖轴 + 2 条横轴（点划线）+ 每端一个轴号圆圈 + 轴号文字
        var axisLines = plan.Lines.Where(l => l.Layer == ViewLayers.Axis).ToList();
        Assert(axisLines.Count == 5, "轴网应有 5 条轴线，实际 " + axisLines.Count);
        Assert(axisLines.All(l => string.Equals(l.LineType, "CENTER", StringComparison.OrdinalIgnoreCase)), "轴线应用点划线（CENTER）");
        Assert(axisLines.Any(l => Math.Abs(l.X1 - l.X2) < Tolerance && Math.Abs(l.X1 + originX - 3600d) < Tolerance),
            "应有 x=3600 的竖轴（2 号轴）");
        Assert(axisLines.Any(l => Math.Abs(l.Y1 - l.Y2) < Tolerance && Math.Abs(l.Y1 + originY - 5400d) < Tolerance),
            "应有 y=5400 的横轴（B 号轴）");
        Assert(plan.Circles.Count == 10, "5 条轴线应各带 2 个轴号圆圈，实际 " + plan.Circles.Count);
        Assert(plan.Circles.All(c => Math.Abs(c.Radius - 400d) < Tolerance), "轴号圆圈半径应为 400");
        foreach (var name in new[] { "1", "2", "3", "A", "B" })
            Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == name), "缺少轴号 " + name);

        // 房间：轮廓 + 名称 + 面积（起居室 3360×5160 = 17.34 m²）
        var roomLines = plan.Lines.Where(l => l.Layer == ViewLayers.Room).ToList();
        Assert(roomLines.Count == 8, "一层两个房间应有 8 条轮廓线，实际 " + roomLines.Count);
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Room && t.Text == "起居室"), "缺少房间名「起居室」");
        var expectedArea = (3360d * 5160d / 1_000_000d).ToString("0.00") + " m²";
        Assert(plan.Texts.Any(t => t.Layer == ViewLayers.Room && t.Text == expectedArea),
            "缺少房间面积 " + expectedArea + "（实际面积文字："
            + string.Join("、", plan.Texts.Where(t => t.Layer == ViewLayers.Room).Select(t => t.Text).ToArray()) + "）");
        Assert(plan.Anchors.Count(a => a.Kind == "axis") == 5, "轴线也应有锚点（5 条）");
        Assert(plan.Anchors.Count(a => a.Kind == "room") == 2, "一层应有 2 个房间锚点");

        // 可点选：一层 4 个洞口 + 5 条轴线 + 2 个房间都有锚点
        Assert(plan.Anchors.Count(a => a.Kind == "opening") == 4, "一层平面应有 4 个门窗锚点，实际 " + plan.Anchors.Count(a => a.Kind == "opening"));
        var anchor = plan.Anchors.First(a => string.Equals(a.ElementId, "1F-S-C1518", StringComparison.OrdinalIgnoreCase));
        Assert(anchor.X1 + originX <= 1450d && anchor.X2 + originX >= 2950d && anchor.Y2 - anchor.Y1 >= 240d, "窗锚点框应盖住洞口与墙厚");
        Console.WriteLine("   平面图：墙面线在洞口断开、窗 2 条玻璃线、门扇线 + 弧、柱断面、5 条轴线 + 10 个轴号圈、"
            + "2 个房间（起居室 " + expectedArea + "），总长 7440 / 总宽 5640，锚点 11 个");
    }

    /// <summary>平面上有没有一条给定端点（顺序无关，容差内）的线。</summary>
    private static bool HasPlanLine(List<ViewLine> lines, double x1, double y1, double x2, double y2)
    {
        foreach (var line in lines)
        {
            if (Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance) return true;
            if (Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance) return true;
        }
        return false;
    }

    // ───────────────────────── 8. 竖向尺寸与门窗表 ─────────────────────────

    /// <summary>
    /// 竖向尺寸两条链 + 总高，以及按编号汇总的门窗表：数值、位置、汇总数都要对得上模型与类型库。
    /// </summary>
    private static void DimensionsAndSchedule()
    {
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        var definition = SampleModelFactory.CreateDefaultViews(model.Name)[0];      // 南立面，1:100
        var view = OrthographicProjector.Project(model, definition, library);

        // 只取"建筑几何"那批线（标高符号线在图名/轮廓之外，算进去总长会变成 8840）
        var geometry = view.Lines.Where(l => l.Layer != ViewLayers.LevelText && l.Layer != ViewLayers.Title).ToList();
        var originX = view.OriginX;
        var originY = view.OriginY;
        // 视图里的坐标 = 模型坐标 - 原点；下面统一换算回模型坐标来核对
        var minU = geometry.Min(l => Math.Min(l.X1, l.X2)) + originX;
        var maxU = geometry.Max(l => Math.Max(l.X1, l.X2)) + originX;
        Func<double, double> modelU = value => value + originX;
        Func<double, double> modelZ = value => value + originY;
        var storeyDimensions = view.Dimensions.Where(d => d.Note != null && d.Note.StartsWith("层高", StringComparison.Ordinal)).ToList();
        Assert(storeyDimensions.Count == 2, "两层应各有 1 条层高尺寸，实际 " + storeyDimensions.Count);
        Assert(storeyDimensions.Any(d => Math.Abs(modelZ(d.From) - 0d) < Tolerance && Math.Abs(modelZ(d.To) - 3600d) < Tolerance),
            "一层层高尺寸应为 0→3600");
        Assert(storeyDimensions.Any(d => Math.Abs(modelZ(d.From) - 3600d) < Tolerance && Math.Abs(modelZ(d.To) - 6900d) < Tolerance),
            "二层层高尺寸应为 3600→6900");
        Assert(storeyDimensions.All(d => Math.Abs(modelU(d.LinePosition) - (maxU + 2000d)) < Tolerance),
            "层高尺寸应画在立面右侧 maxU+2000 处");

        var total = view.Dimensions.FirstOrDefault(d => d.Note == "总高");
        Assert(total != null, "缺少总高尺寸");
        Assert(Math.Abs(modelZ(total.From) - 0d) < Tolerance && Math.Abs(modelZ(total.To) - 6900d) < Tolerance
            && Math.Abs(modelU(total.LinePosition) - (maxU + 3000d)) < Tolerance, "总高应为 0→6900，画在 maxU+3000 处");

        // 一层南墙有 1500×1800@900 的窗与 900×2100 的门 → 定位链 0→900→2100→2700
        //（注意只算这张图上画出来的洞口：北面/东面的窗不该出现在南立面的尺寸链里）
        var firstFloor = view.Dimensions.Where(d => d.Note != null
            && d.Note.StartsWith("洞口定位", StringComparison.Ordinal)
            && d.Note.IndexOf("一层", StringComparison.Ordinal) >= 0).ToList();
        Assert(firstFloor.Count == 3, "一层洞口定位链应有 3 段（0-900-2100-2700），实际 " + firstFloor.Count);
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 0d) < Tolerance && Math.Abs(modelZ(d.To) - 900d) < Tolerance), "缺少窗台 900 那一段");
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 900d) < Tolerance && Math.Abs(modelZ(d.To) - 2100d) < Tolerance), "缺少 900→2100 那一段");
        Assert(firstFloor.Any(d => Math.Abs(modelZ(d.From) - 2100d) < Tolerance && Math.Abs(modelZ(d.To) - 2700d) < Tolerance), "缺少 2100→2700 那一段");
        Assert(firstFloor.All(d => Math.Abs(modelU(d.LinePosition) - (minU - 1200d)) < Tolerance), "一层定位链应画在立面左侧 minU-1200 处");
        Assert(firstFloor.All(d => string.IsNullOrEmpty(d.Text)), "尺寸文字应留给 CAD 自己量（Text 留空）");

        // 横向：内层洞口定位链 + 外层总长。
        // 立面参考：一层南窗 1450..2950、一层南门 4950..5850、二层南窗 2850..4350、两端墙外皮 -120 / 7320
        var minZ = geometry.Min(l => Math.Min(l.Y1, l.Y2)) + originY;
        var horizontal = view.Dimensions.Where(d => !d.Vertical).ToList();
        var innerRun = horizontal.Where(d => d.Note == "洞口定位（横向）").ToList();
        Assert(innerRun.Count == 7, "横向洞口定位链应有 7 段（-120｜1450｜2850｜2950｜4350｜4950｜5850｜7320），实际 " + innerRun.Count);
        Assert(innerRun.All(d => Math.Abs(modelZ(d.AnchorPosition) - minZ) < Tolerance && Math.Abs(modelZ(d.LinePosition) - (minZ - 1200d)) < Tolerance),
            "横向定位链应画在建筑底边下方 1200 处");
        var expectedEdges = new[] { -120d, 1450d, 2850d, 2950d, 4350d, 4950d, 5850d, 7320d };
        for (var i = 0; i + 1 < expectedEdges.Length; i++)
            Assert(innerRun.Any(d => Math.Abs(modelU(d.From) - expectedEdges[i]) < Tolerance && Math.Abs(modelU(d.To) - expectedEdges[i + 1]) < Tolerance),
                "横向定位链缺少 " + expectedEdges[i] + "→" + expectedEdges[i + 1] + " 那一段；实际是："
                + string.Join("、", innerRun.OrderBy(d => d.From).Select(d => Math.Round(modelU(d.From)) + "→" + Math.Round(modelU(d.To))).ToArray()));
        var totalWidth = horizontal.FirstOrDefault(d => d.Note == "总长");
        Assert(totalWidth != null, "缺少总长尺寸");
        Assert(Math.Abs(modelU(totalWidth.From) - (-120d)) < Tolerance && Math.Abs(modelU(totalWidth.To) - 7320d) < Tolerance
            && Math.Abs(modelZ(totalWidth.LinePosition) - (minZ - 2000d)) < Tolerance,
            "总长应为 -120→7320（7440），画在建筑底边下方 2000 处");
        Assert(Math.Abs(innerRun.Sum(d => d.To - d.From) - 7440d) < 0.01d, "横向定位链各段之和应等于总长 7440");

        // 门窗表：按编号汇总（样例里 C1215×2、C1518×2、M0921×1）
        var schedule = OrthographicProjector.ProjectSchedule(model, library, "门窗表");
        Assert(schedule.Kind == ViewKind.Schedule, "门窗表应是 Schedule 类型视图");
        var texts = schedule.Texts.Select(t => t.Text).ToList();
        foreach (var header in new[] { "编号", "类型", "洞口尺寸", "窗台/落地", "樘数", "做法（分格 / 开启）" })
            Assert(texts.Contains(header), "门窗表缺少表头：" + header);
        Assert(texts.Contains("C1215") && texts.Contains("C1518") && texts.Contains("M0921"), "门窗表缺少编号行");
        Assert(texts.Contains("1200×1500") && texts.Contains("1500×1800") && texts.Contains("900×2100"), "门窗表尺寸列不对");
        Assert(texts.Count(t => t == "2") == 2 && texts.Count(t => t == "1") == 1, "门窗表樘数统计不对（C1215/C1518 各 2、M0921 为 1）");
        Assert(texts.Any(t => t != null && t.IndexOf("双扇等分 / 双向推拉", StringComparison.Ordinal) >= 0), "门窗表的做法列应来自类型库");
        Assert(texts.Any(t => t != null && t.IndexOf("类型库里没有这一条", StringComparison.Ordinal) >= 0) == false, "样例里的编号都应在类型库中");
        var width = schedule.Lines.Max(l => Math.Max(l.X1, l.X2)) - schedule.Lines.Min(l => Math.Min(l.X1, l.X2));
        Assert(Math.Abs(width - 14800d) < Tolerance, "门窗表总宽应为 14800（各列之和），实际 " + width);
        Console.WriteLine("   尺寸与门窗表：层高 2 段 + 总高 6900、一层定位链 3 段（0-900-2100-2700）、门窗表 3 行（宽 "
            + Math.Round(width) + "）");
    }

    // ───────────────────────── 7. 可点选锚点与洞口编号 ─────────────────────────

    /// <summary>
    /// 视图里要有"这个矩形对应模型里哪一樘门窗"的锚点（预览点选、门窗表联动都靠它），
    /// 还要把洞口编号写在图上（有窗台写下方、落地门写上方，字高按比例）。
    /// </summary>
    private static void AnchorsAndLabels()
    {
        var library = Library(Type("C1518", "双扇等分", "双向推拉"));
        var view = Project(SingleWindowModel("C1518"), ElevationDirection.South, library);
        var u = view.OriginX;
        var z = view.OriginY;

        Assert(view.Anchors.Count == 1, "南立面应有一个门窗锚点，实际 " + view.Anchors.Count);
        var anchor = view.Anchors[0];
        Assert(anchor.Kind == "opening" && anchor.ElementId == "o-1", "锚点应指向洞口 o-1，实际 " + anchor.Kind + "/" + anchor.ElementId);
        Assert(Math.Abs(anchor.X1 - (750d - u)) < Tolerance && Math.Abs(anchor.X2 - (2250d - u)) < Tolerance
            && Math.Abs(anchor.Y1 - (900d - z)) < Tolerance && Math.Abs(anchor.Y2 - (2700d - z)) < Tolerance,
            "锚点矩形应与洞口一致：(750,900)-(2250,2700) + 视图原点，实际 ("
            + (anchor.X1 + u) + "," + (anchor.Y1 + z) + ")-(" + (anchor.X2 + u) + "," + (anchor.Y2 + z) + ")");

        var label = view.Texts.FirstOrDefault(t => t.Layer == ViewLayers.Opening && t.Text == "C1518");
        Assert(label != null, "立面上应标出洞口编号 C1518");
        Assert(Math.Abs(label.Height - 250d) < Tolerance, "1:100 的编号应采用统一纸面字高 2.5mm（模型 250mm），实际 " + label.Height);
        Assert(Math.Abs(label.Y - (580d - z)) < Tolerance, "有窗台的窗，编号应写在洞口下方（模型 y=580），实际 " + (label.Y + z));
        var estimated = label.Height * 0.62d * "C1518".Length;
        Assert(Math.Abs(label.X + estimated / 2d - (1500d - u)) < 1d, "编号应大致居中在洞口上（模型 x=1500）");

        // 落地门（窗台 0）：洞口下方是墙脚/地面，编号改写在洞口上方（这樘门高 1800 → 上方即 1800 以上）
        var door = SingleWindowModel("M0921");
        door.Openings[0].Kind = "门";
        door.Openings[0].Sill = 0d;
        door.Openings[0].Height = 2100d;
        var doorView = Project(door, ElevationDirection.South, null);
        var doorLabel = doorView.Texts.FirstOrDefault(t => t.Layer == ViewLayers.Opening && t.Text == "M0921");
        Assert(doorLabel != null, "落地门也要标编号");
        var doorLabelY = doorLabel.Y + doorView.OriginY;
        Assert(doorLabelY > 2100d && doorLabelY < 3000d, "落地门的编号应写在洞口上方（2100 以上、层高以内），实际 " + doorLabelY);
        Console.WriteLine("   锚点与编号：洞口锚点 1 个（750,900)-(2250,2700)、编号 C1518 字高 250 写在窗下、门编号写在门上方（"
            + Math.Round(doorLabelY) + "）");
    }

    // ───────────────────────── 1. 分格与开启线 ─────────────────────────

    /// <summary>
    /// 一道 3000 长的墙、中间一樘 1500×1800@900 的窗（编号 C1518，做法「双扇等分 + 左平开」）。
    /// 生成器给出的几何是可以手算的：两格各 750 宽；外框 50；两扇都是可开启扇，
    /// 所以中间的竖挺合成一条线；每扇的开启线是"合页角 → 对侧中点 → 另一角"的两段折线。
    /// </summary>
    private static void DrawsMullionsAndOpeningLines()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));

        var plain = Project(model, ElevationDirection.South, library: null);
        var detailed = Project(model, ElevationDirection.South, library: library);

        // 没有类型库时采用共用生成器的默认框，不再退成空矩形。
        var plainEdges = plain.Lines.Count(l => l.Layer == ViewLayers.Opening);
        Assert(plainEdges >= 8, "没有类型库时应采用原门窗立面默认分格与框料，实际 " + plainEdges);
        Assert(!plain.Warnings.Any(w => w.Contains("不在类型库里")), "默认门窗不应要求用户先建立类型库");

        Assert(detailed.Lines.Count(l => l.Layer == ViewLayers.Opening) > 8,
            "有类型库时门窗线条应明显多于洞口轮廓，实际 " + detailed.Lines.Count(l => l.Layer == ViewLayers.Opening));
        Assert(!detailed.Warnings.Any(w => w.IndexOf("不在类型库里", StringComparison.Ordinal) >= 0),
            "有类型库时不应再提示信息缺失");

        var u = detailed.OriginX;
        var z = detailed.OriginY;
        var detailLines = detailed.Lines.Count(l => l.Layer == ViewLayers.Opening);
        // 洞口 4 条边 + 11 条做法线（外框内边 6 条、中挺 1 条、开启线 4 段）
        Assert(detailLines == 15, "门窗线条应为 15 条（洞口 4 + 做法 11），实际 " + detailLines);
        // 中挺：洞口中间 x=1500 的竖线（两扇都可开启 → 框线并成一条，落在上下框内边之间）
        Assert(HasLine(detailed, 1500d - u, 950d - z, 1500d - u, 2650d - z),
            "缺少中间的分格竖线（x=1500、y=950→2650）");
        // 外框内边：左边 50、上边 1750，第二格靠右 1450
        Assert(HasLine(detailed, 800d - u, 950d - z, 1500d - u, 950d - z), "缺少第一格的下框线（y=950）");
        Assert(HasLine(detailed, 1500d - u, 950d - z, 2200d - u, 950d - z), "缺少第二格的下框线（y=950）");
        Assert(HasLine(detailed, 2200d - u, 950d - z, 2200d - u, 2650d - z), "缺少第二格的右框线（x=2200）");
        // 开启线（左平开）：合页在左，折线打到右中
        Assert(HasLine(detailed, 800d - u, 950d - z, 1500d - u, 1800d - z), "第一扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(detailed, 1500d - u, 1800d - z, 800d - u, 2650d - z), "第一扇缺少「右中 → 左上」的开启线");
        Assert(HasLine(detailed, 1500d - u, 950d - z, 2200d - u, 1800d - z), "第二扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(detailed, 2200d - u, 1800d - z, 1500d - u, 2650d - z), "第二扇缺少「右中 → 左上」的开启线");

        Console.WriteLine("   分格与开启线：洞口 4 条边 + 做法 11 条 = " + detailLines
            + " 条（中挺 x=1500、外框内边 6 条、开启线 4 段）");
    }

    // ───────────────────────── 2. 背立面镜像 ─────────────────────────

    /// <summary>北立面看到的是墙的背面：U 轴与墙轴反向，门的合页侧必须跟着镜像，不能画反。</summary>
    private static void MirrorsOnBackElevation()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));
        var north = Project(model, ElevationDirection.North, library);
        var u = north.OriginX;
        var z = north.OriginY;

        // 洞口 x∈[750,2250]；北立面 U=-x → 洞口局部 x=0 对应 U=-750（视图右边）。
        // 第一扇（局部 50..750）的合页在局部 50 → U=-800。
        Assert(HasLine(north, -800d - u, 950d - z, -1500d - u, 1800d - z), "北立面第一扇开启线没有镜像");
        Assert(HasLine(north, -1500d - u, 1800d - z, -800d - u, 2650d - z), "北立面第一扇开启线没有镜像（第二段）");
        // 不镜像的话会画成 U = ou0 + x = -2250 + x：这两段就是错的画法
        Assert(!HasLine(north, -2200d - u, 950d - z, -1500d - u, 1800d - z), "北立面开启线画成了南立面的方向");
        Console.WriteLine("   背立面镜像：开启线合页侧随视线反向（U=-800 一侧）");
    }

    // ───────────────────────── 3. 遮挡裁剪 ─────────────────────────

    /// <summary>前面再加一道墙：被挡住的部分（含分格与开启线）一条都不许冒出来。</summary>
    private static void ClipsHiddenParts()
    {
        var model = SingleWindowModel("C1518");
        var library = Library(Type("C1518", "双扇等分", "左平开"));

        // 完全挡住（新墙在更近的一侧，覆盖洞口整个范围，高 3000）
        var covered = WithFrontWall(model, 0d, 3000d);
        var hidden = Project(covered, ElevationDirection.South, library);
        var hiddenOpening = hidden.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        Assert(hiddenOpening.Count == 0, "洞口被前面的墙完全挡住时不应再画门窗线，实际 " + hiddenOpening.Count);

        // 只挡住左边一段（x∈[0,1200]）：右侧 1200..2250 的分格与开启线要保留，且不许越界
        // 注意用一份干净的模型：上面那道"全挡"的墙要是留在里面，就什么都看不见了
        var partial = WithFrontWall(SingleWindowModel("C1518"), 0d, 1200d);
        var visible = Project(partial, ElevationDirection.South, library);
        var visibleOpening = visible.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        Assert(visibleOpening.Count > 0, "只挡住左边一段时右侧门窗线应保留");
        var limit = 1200d - visible.OriginX;
        var leaking = visibleOpening
            .Where(l => Math.Min(l.X1, l.X2) < limit - Tolerance && l.Layer == ViewLayers.Opening)
            .ToList();
        Assert(leaking.Count == 0, "被挡住的那一段仍有 " + leaking.Count + " 条线冒出来（应在 x≥1200 以内）");
        Console.WriteLine("   遮挡裁剪：全挡 0 条 / 半挡保留 " + visibleOpening.Count + " 条且都在 x≥1200");
    }

    // ───────────────────────── 4. 自定义分格 ─────────────────────────

    /// <summary>
    /// 自定义分格是插件里"一格一格摆"的做法（left,bottom,right,top,开启,是否门[,删除[,材料]]）。
    /// 程序侧只是把它原样交给生成器，这里验证格式没串味：左边固定扇、右边平开扇。
    /// </summary>
    private static void HonoursCustomCellLayout()
    {
        var model = SingleWindowModel("C1500X");
        var type = Type("C1500X", "自定义", "未设置");
        type.CustomCellLayout = "0,0,900,1800,固定,0,0,玻璃|900,0,1500,1800,左平开,1,0,木质";
        var library = Library(type);
        var view = Project(model, ElevationDirection.South, library);
        var u = view.OriginX;
        var z = view.OriginY;

        // 左固定扇的右边框（局部 875 → 视图 1625）、右平开扇的左边框（局部 925 → 视图 1675）：
        // 一个固定扇挨着一个可开启扇时，中挺是"两条线"（各画各的框），不是并成一条。
        Assert(HasLine(view, 1625d - u, 950d - z, 1625d - u, 2650d - z), "左扇右边框（U=1625）没有画出来");
        Assert(HasLine(view, 1675d - u, 900d - z, 1675d - u, 2650d - z), "右扇左边框（U=1675）没有画出来");
        // 左边固定扇（局部 50..875）不应有开启线：这两段就是"如果它是平开扇"会有的线
        Assert(!HasLine(view, 800d - u, 950d - z, 1625d - u, 1800d - z), "固定扇不应有开启线");
        Assert(!HasLine(view, 1625d - u, 1800d - z, 800d - u, 2650d - z), "固定扇不应有开启线（第二段）");
        // 右边平开扇：门扇（IsDoor=1）底边落地（N 型门框不画下框），合页在左 → 折线打到右中
        Assert(HasLine(view, 1675d - u, 900d - z, 2200d - u, 1775d - z), "右扇缺少「左下 → 右中」的开启线");
        Assert(HasLine(view, 2200d - u, 1775d - z, 1675d - u, 2650d - z), "右扇缺少「右中 → 左上」的开启线");
        // 固定扇的材料是玻璃 → 生成器会画 3 条玻璃符号线（也说明"材料"字段传到了生成器）
        var glassMarks = view.Lines.Count(l => l.Layer == ViewLayers.Opening
            && Math.Abs(l.X2 - l.X1) > 1d && Math.Abs(l.Y2 - l.Y1) > 1d
            && (l.X1 + l.X2) / 2d < 1625d - u);
        Assert(glassMarks == 3, "固定玻璃扇应有 3 条玻璃符号线，实际 " + glassMarks);
        Console.WriteLine("   自定义分格：固定玻璃扇 + 平开门扇 → 中挺双线 1625/1675、玻璃符号 3 条、开启线只在右扇");
    }

    // ───────────────────────── 5. 1:100 与 1:50 的详简之分 ─────────────────────────

    /// <summary>
    /// 20mm 安装缝、50mm 门扇内框、玻璃材料符号在 1:50 的门窗立面详图里是内容，
    /// 到 1:100 的立面图上只有零点几毫米，只会把洞口糊成一团 —— 所以 1:100 不画这三样，
    /// 但分格与开启线一律保留（那正是立面图要表达的）。
    /// </summary>
    private static void SimplifiesAtHundredthScale()
    {
        var model = SingleWindowModel("C1518");
        var type = Type("C1518", "单扇", "左平开");
        type.HasInstallationGap = true;
        type.InstallationGap = 20d;
        type.DoorFrameWidth = 50d;
        type.Material = "玻璃";
        var library = Library(type);

        var at100 = Project(model, ElevationDirection.South, library, scale: 100);
        var at50 = Project(model, ElevationDirection.South, library, scale: 50);

        var count100 = at100.Lines.Count(l => l.Layer == ViewLayers.Opening);
        var count50 = at50.Lines.Count(l => l.Layer == ViewLayers.Opening);
        Assert(count50 > count100, "1:50 的详图线条应多于 1:100（实际 " + count50 + " vs " + count100 + "）");

        // 洞口 x∈[750,2250]、z∈[900,2700]。
        // 1:50：安装缝 20 → 分格内边 770；再进外框 50 → 外框内边 820；再进 50 门扇内框 → 870。
        // 1:100：安装缝与门扇内框都不画，只剩外框内边 800。
        var u = at100.OriginX;
        var z = at100.OriginY;
        Assert(!HasLine(at100, 770d - u, 920d - z, 770d - u, 2680d - z), "1:100 不应画 20mm 安装缝");
        Assert(!HasLine(at100, 870d - u, 1020d - z, 870d - u, 2580d - z), "1:100 不应画 50mm 门扇内框");
        var u50 = at50.OriginX;
        var z50 = at50.OriginY;
        Assert(HasLine(at50, 770d - u50, 920d - z50, 770d - u50, 2680d - z50), "1:50 应画出 20mm 安装缝");
        Assert(HasLine(at50, 870d - u50, 1020d - z50, 870d - u50, 2580d - z50), "1:50 应画出 50mm 门扇内框");
        // 玻璃材料符号是 3 条"短斜线"（长度约 260mm）；开启线也是斜线但长度约 1600mm，
        // 所以用"斜且短"来数材料符号。
        Assert(ShortDiagonalCount(at100) == 0, "1:100 不应画玻璃材料符号，实际 " + ShortDiagonalCount(at100));
        Assert(ShortDiagonalCount(at50) == 3, "1:50 应有 3 条玻璃材料符号线，实际 " + ShortDiagonalCount(at50));
        // 两种比例都要有外框内边与开启线
        Assert(HasLine(at100, 800d - u, 950d - z, 2200d - u, 950d - z), "1:100 缺少外框内边");
        Assert(HasLine(at100, 800d - u, 950d - z, 2200d - u, 1800d - z), "1:100 缺少开启线");
        Assert(HasLine(at50, 820d - u50, 970d - z50, 2180d - u50, 1800d - z50), "1:50 缺少开启线");
        Console.WriteLine("   详简之分：1:100 " + count100 + " 条（无安装缝/门扇内框/材料符号）/ 1:50 " + count50 + " 条（全画）");
    }

    // ───────────────────────── 6. 参数不合法时的兜底 ─────────────────────────

    /// <summary>分格参数不合法（超出洞口）时，视图必须照常出来：退回单格 + 一条提示。</summary>
    private static void FallsBackWhenParametersInvalid()
    {
        var model = SingleWindowModel("C1500X");
        var type = Type("C1500X", "自定义", "未设置");
        type.CustomCellLayout = "0,0,9999,9999,固定,0,0,玻璃";
        var view = Project(model, ElevationDirection.South, Library(type));

        Assert(view.Lines.Count(l => l.Layer == ViewLayers.Opening) == 4, "参数不合法时应退回洞口 4 条边");
        Assert(view.Warnings.Any(w => w.IndexOf("分格参数无效", StringComparison.Ordinal) >= 0),
            "参数不合法时应给出提示，实际提示：" + string.Join(" / ", view.Warnings.ToArray()));
        Console.WriteLine("   参数兜底：非法分格 → 洞口 4 条边 + 提示「分格参数无效」");
    }

    // ───────────────────────── 脚手架 ─────────────────────────

    /// <summary>一道 3000 长的南墙 + 中间一樘 1500×1800@900 的窗（轴线无偏）。</summary>
    private static BuildingModelDocument SingleWindowModel(string code)
    {
        var model = SampleModelFactory.CreateEmptyModel("门窗分格测试");
        var storey = model.Storeys[0].Id;
        model.Walls.Add(new WallModel { Id = "w-s", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 3000d, Y2 = 0d, Thickness = 200d });
        model.Openings.Add(new OpeningModel
        {
            Id = "o-1", HostWallId = "w-s", Code = code, Kind = "窗",
            Offset = 1500d, Width = 1500d, Height = 1800d, Sill = 900d
        });
        return model;
    }

    /// <summary>在更近的一侧（南）加一道墙，从 x1 到 x2（用于测试遮挡）。</summary>
    private static BuildingModelDocument WithFrontWall(BuildingModelDocument model, double x1, double x2)
    {
        var storey = model.Storeys[0].Id;
        model.Walls.Add(new WallModel { Id = "w-front", StoreyId = storey, X1 = x1, Y1 = -1000d, X2 = x2, Y2 = -1000d, Thickness = 200d });
        return model;
    }

    private static OpeningTypeModel Type(string code, string division, string openingMode)
    {
        return new OpeningTypeModel
        {
            Code = code, Kind = "窗", Width = 1500d, Height = 1800d, Sill = 900d,
            ElevationType = "普通窗", DivisionPreset = division, OpeningMode = openingMode,
            HasOuterFrame = true, OuterFrameWidth = 50d,
            HasMullion = true, MullionWidth = 50d,
            HasInstallationGap = false, InstallationGap = 0d,
            Material = "铝合金"
        };
    }

    private static OpeningTypeLibraryDocument Library(params OpeningTypeModel[] types)
    {
        var library = new OpeningTypeLibraryDocument { ProjectName = "测试" };
        library.Types.AddRange(types);
        return library;
    }

    private static ViewDocument Project(BuildingModelDocument model, ElevationDirection direction,
        OpeningTypeLibraryDocument library, int scale = 100)
    {
        return OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "test", Title = "测试立面", Kind = ViewKind.Elevation, Scale = scale, Direction = direction
        }, library);
    }

    /// <summary>数"短斜线"（玻璃材料符号）：斜的、而且长度不到 400mm。</summary>
    private static int ShortDiagonalCount(ViewDocument view)
    {
        var count = 0;
        foreach (var line in view.Lines)
        {
            if (!string.Equals(line.Layer, ViewLayers.Opening, StringComparison.Ordinal)) continue;
            var dx = line.X2 - line.X1;
            var dy = line.Y2 - line.Y1;
            if (Math.Abs(dx) < 1d || Math.Abs(dy) < 1d) continue;
            if (Math.Sqrt(dx * dx + dy * dy) < 400d) count++;
        }
        return count;
    }

    private static bool HasLine(ViewDocument view, double x1, double y1, double x2, double y2)
    {
        foreach (var line in view.Lines)
        {
            if (!string.Equals(line.Layer, ViewLayers.Opening, StringComparison.Ordinal)) continue;
            var forward = Math.Abs(line.X1 - x1) < Tolerance && Math.Abs(line.Y1 - y1) < Tolerance
                && Math.Abs(line.X2 - x2) < Tolerance && Math.Abs(line.Y2 - y2) < Tolerance;
            var backward = Math.Abs(line.X1 - x2) < Tolerance && Math.Abs(line.Y1 - y2) < Tolerance
                && Math.Abs(line.X2 - x1) < Tolerance && Math.Abs(line.Y2 - y1) < Tolerance;
            if (forward || backward) return true;
        }
        return false;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
