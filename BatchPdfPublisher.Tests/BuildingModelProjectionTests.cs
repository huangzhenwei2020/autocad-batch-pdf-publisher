using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

/// <summary>
/// 自研正交投影与中间格式的护栏测试（纯逻辑，不需要 AutoCAD）。
///
/// 钉住 P0 的核心承诺：
/// 1. 立面轮廓与门窗位置正确（含"外墙轴线外扩半个墙厚"这类几何事实）；
/// 2. 四个方向的镜像关系正确；
/// 3. 隐藏线消除生效（前面的墙把后面的墙与门窗口遮掉）；
/// 4. 剖面给出"剖到"的墙/板断面与填充；
/// 5. 模型与视图的 JSON 往返不丢数据。
/// </summary>
internal static class BuildingModelProjectionTests
{
    private const double Tolerance = 0.5d;
    /// <summary>样例：轴线 7200×5400，外墙 240 厚 → 投影外轮廓 7440×5640。</summary>
    private const double OuterWidth = 7440d;

    private static void Main()
    {
        try
        {
            var model = SampleModelFactory.CreateTwoStoreyHouse();
            SouthElevationShowsOutlineAndOpenings(model);
            ElevationDirectionsAreMirrored(model);
            HiddenLinesRemoveCoveredEdges(model);
            SectionProducesCutRectsAndHatch(model);
            OffsetCutInsideWallThicknessProducesSection();
            WallAndOpeningEditUpdatesElevationAndSection();
            VolumeIdentityTests.Run();
            BuildingModelEditSessionTests.Run();
            JsonRoundTripsWithoutLoss(model);
            PlanEditingTests.Run();
            OpeningTypeLibraryTests.Run();
            OpeningElevationTests.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            Environment.ExitCode = 1;
        }
    }

    private static void SouthElevationShowsOutlineAndOpenings(BuildingModelDocument model)
    {
        var view = Project(model, ElevationDirection.South, "南立面图");

        var outline = view.Lines.Where(l => l.Layer == ViewLayers.Elevation).ToList();
        Assert(outline.Count > 0, "南立面没有算出轮廓线");
        var width = outline.Max(l => Math.Max(l.X1, l.X2)) - outline.Min(l => Math.Min(l.X1, l.X2));
        Assert(Math.Abs(width - OuterWidth) < Tolerance, "南立面总宽应为 7440（7200 + 两侧各 120），实际 " + width);
        var top = outline.Max(l => Math.Max(l.Y1, l.Y2));
        Assert(Math.Abs(top - 6900d) < Tolerance, "南立面应覆盖到屋面 6900，实际 " + top);

        var openings = view.Lines.Where(l => l.Layer == ViewLayers.Opening).ToList();
        Assert(openings.Count > 0, "南立面没有算出门窗");
        // 南面有两樘 1500 宽的窗（一层 x=2200、二层 x=3600），每樘给出上下两条水平边
        var wideEdges = openings.Count(l => Math.Abs(l.Y2 - l.Y1) < Tolerance
            && Math.Abs(Math.Abs(l.X2 - l.X1) - 1500d) < Tolerance);
        Assert(wideEdges >= 4, "南立面应有 2 樘 1500 宽窗（≥4 条水平边），实际 " + wideEdges);
        // 落地门：高 2100
        var doorEdges = openings.Count(l => Math.Abs(l.X2 - l.X1) < Tolerance
            && Math.Abs(Math.Abs(l.Y2 - l.Y1) - 2100d) < Tolerance);
        Assert(doorEdges >= 2, "南立面应有落地门（高 2100，≥2 条竖直边），实际 " + doorEdges);

        var levels = view.Texts.Where(t => t.Layer == ViewLayers.LevelText).Select(t => t.Text).ToList();
        Assert(levels.Contains("±0.000"), "缺少 ±0.000 标高：" + string.Join("/", levels.ToArray()));
        Assert(levels.Contains("3.600"), "缺少 3.600 标高：" + string.Join("/", levels.ToArray()));
        Assert(view.Texts.Any(t => t.Layer == ViewLayers.Title && t.Text.IndexOf("南立面图", StringComparison.Ordinal) >= 0),
            "缺少图名");
        // 归一化：几何从 (0,0) 起；OriginX/OriginY 记录对应的模型坐标（西墙外皮 -120、地坪 0）
        var uMin = view.Lines.Min(l => Math.Min(l.X1, l.X2));
        var zMin = view.Lines.Min(l => Math.Min(l.Y1, l.Y2));
        Assert(Math.Abs(uMin) < Tolerance && Math.Abs(zMin) < Tolerance, "视图几何应已平移到 (0,0) 起");
        Assert(Math.Abs(view.OriginX - (-120d)) < Tolerance && Math.Abs(view.OriginY) < Tolerance,
            "视图原点应记录对应的模型坐标，实际 (" + view.OriginX + ", " + view.OriginY + ")");

        Console.WriteLine("PASS 南立面：轮廓 " + outline.Count + " 条、门窗 " + openings.Count
            + " 条、" + string.Join("、", levels.ToArray()) + "、" + width + "×" + top);
    }

    private static void ElevationDirectionsAreMirrored(BuildingModelDocument model)
    {
        var south = Project(model, ElevationDirection.South, "南");
        var north = Project(model, ElevationDirection.North, "北");
        var east = Project(model, ElevationDirection.East, "东");
        var west = Project(model, ElevationDirection.West, "西");

        // 南看：u = x + 120。样例里 Offset 是洞口中心（一层窗中心 x=2200、宽 1500）→ 窗 u=1570..3070
        Assert(Math.Abs(LeftEdgeOfWidth(south, 1500d) - 1570d) < Tolerance,
            "南立面 1500 宽窗左边界应为 1570，实际 " + LeftEdgeOfWidth(south, 1500d));
        // 北看：u = 7320 - x → 北窗（x=2400..3600）左边界 3720
        Assert(Math.Abs(LeftEdgeOfWidth(north, 1200d) - 3720d) < Tolerance,
            "北立面 1200 宽窗左边界应为 3720（镜像），实际 " + LeftEdgeOfWidth(north, 1200d));
        // 南面有落地门，北面没有
        Assert(south.Lines.Any(l => l.Layer == ViewLayers.Opening && Math.Abs(Math.Abs(l.Y2 - l.Y1) - 2100d) < Tolerance),
            "南立面应有落地门");
        Assert(!north.Lines.Any(l => l.Layer == ViewLayers.Opening && Math.Abs(Math.Abs(l.Y2 - l.Y1) - 2100d) < Tolerance),
            "北立面不该有落地门");

        // 东/西看：水平方向是进深 → 总宽 5400 + 240
        Assert(Math.Abs(Width(east) - 5640d) < Tolerance, "东立面总宽应为 5640，实际 " + Width(east));
        Assert(Math.Abs(Width(west) - 5640d) < Tolerance, "西立面总宽应为 5640，实际 " + Width(west));

        Console.WriteLine("PASS 四个方向：南窗左 " + LeftEdgeOfWidth(south, 1500d) + "、北窗左 " + LeftEdgeOfWidth(north, 1200d)
            + "、东/西总宽 " + Width(east) + "（镜像关系正确）");
    }

    private static void HiddenLinesRemoveCoveredEdges(BuildingModelDocument model)
    {
        var baseline = Project(model, ElevationDirection.South, "基线");
        // 二层南面前方 1200 处加一道通长遮挡墙，应把二层南墙与二层窗完全遮住
        var withCover = SampleModelFactory.CreateTwoStoreyHouse();
        withCover.Walls.Add(new WallModel
        {
            Id = "COVER", StoreyId = "2F", X1 = 0d, Y1 = -1200d, X2 = 7200d, Y2 = -1200d, Thickness = 240d
        });
        var covered = Project(withCover, ElevationDirection.South, "遮挡");

        // 二层窗（中心 x=3600、宽 1500 → u=2970..4470）的竖直边在基线里存在，被遮后必须消失
        const double secondFloorWindowEdge = 2970d;
        Assert(baseline.Lines.Any(l => l.Layer == ViewLayers.Opening
                && Math.Abs(l.X1 - secondFloorWindowEdge) < Tolerance && Math.Abs(l.X2 - secondFloorWindowEdge) < Tolerance),
            "前置条件不成立：基线里没有二层窗的竖直边");
        Assert(!covered.Lines.Any(l => l.Layer == ViewLayers.Opening
                && Math.Abs(l.X1 - secondFloorWindowEdge) < Tolerance && Math.Abs(l.X2 - secondFloorWindowEdge) < Tolerance),
            "被遮挡的二层窗仍然画出来了（隐藏线消除没生效）");
        // 一层窗不受影响（遮挡墙只存在于二层）：一层窗左边界 u=1570
        Assert(covered.Lines.Any(l => l.Layer == ViewLayers.Opening && Math.Abs(l.X1 - 1570d) < Tolerance),
            "一层窗不该被二层的遮挡墙影响");

        Console.WriteLine("PASS 隐藏线消除：基线 " + baseline.Lines.Count + " 条线 → 遮挡后 " + covered.Lines.Count
            + " 条线，被遮的窗已消失、一层窗仍在");
    }

    private static void SectionProducesCutRectsAndHatch(BuildingModelDocument model)
    {
        var view = Project(model, SampleModelFactory.CreateDefaultViews(model.Name)[4]);
        var cut = view.Lines.Where(l => l.Layer == ViewLayers.Cut).ToList();
        Assert(cut.Count > 0, "剖面没有算出剖切轮廓");
        Assert(view.Hatches.Count >= 4, "剖切填充应有 2 道墙 + 2 块楼板，实际 " + view.Hatches.Count);

        // 断面尺寸看填充边界（一个断面 = 一个矩形填充）
        var sizes = view.Hatches.Select(SizeOf).ToList();
        var wallCut = sizes.Count(s => Math.Abs(s[0] - 240d) < Tolerance && Math.Abs(s[1] - 3600d) < Tolerance);
        Assert(wallCut > 0, "剖面里没有「240 宽 × 3600 高」的墙断面，实际断面：" + Describe(sizes));
        var slabCut = sizes.Count(s => Math.Abs(s[0] - 5400d) < Tolerance && Math.Abs(s[1] - 120d) < Tolerance);
        Assert(slabCut > 0, "剖面里没有「5400 宽 × 120 厚」的楼板断面，实际断面：" + Describe(sizes));
        // 剖切面穿过一层南墙的窗（中心 2200、宽 1500、窗台 900、高 1800）：
        // 墙断面应在洞口处断开 → 出现「240 宽 × 900 高」（窗台下）与「240 × 900」（窗顶到层顶）
        Assert(sizes.Any(s => Math.Abs(s[0] - 240d) < Tolerance && Math.Abs(s[1] - 900d) < Tolerance),
            "洞口处的墙断面没有断开（应出现 240×900 的窗台下断面），实际断面：" + Describe(sizes));
        var sillLines = view.Lines.Where(l => l.Layer == ViewLayers.Opening
            && Math.Abs(l.Y1 - 900d) < Tolerance && Math.Abs(l.Y2 - 900d) < Tolerance).ToList();
        var headLines = view.Lines.Where(l => l.Layer == ViewLayers.Opening
            && Math.Abs(l.Y1 - 2700d) < Tolerance && Math.Abs(l.Y2 - 2700d) < Tolerance).ToList();
        Assert(sillLines.Count > 0 && headLines.Count > 0, "剖面里缺少窗台线（900）或窗顶线（2700）");
        // 填充是 45° 细线且间距按比例换算，不再用实心
        Assert(view.Hatches.All(h => h.Pattern == "ANSI31" && h.Spacing > 0d && Math.Abs(h.Angle - 45d) < Tolerance),
            "剖切填充应统一为 ANSI31 45° 细线并按比例给间距");
        // 剖切面之后的东墙应作为背景出现
        Assert(view.Lines.Any(l => l.Layer == ViewLayers.Elevation), "剖面缺少背景投影");
        var maxU = cut.Max(l => Math.Max(l.X1, l.X2));
        Assert(maxU < 6000d, "剖切断面的水平范围异常：" + maxU);

        Console.WriteLine("PASS 剖面：剖切线 " + cut.Count + " 条、填充 " + view.Hatches.Count
            + " 块（墙断面 " + wallCut + " 个、板断面 " + slabCut + " 个、洞口处断开 "
            + sizes.Count(s => Math.Abs(s[0] - 240d) < Tolerance && Math.Abs(s[1] - 900d) < Tolerance)
            + " 处，窗台/窗顶线 " + sillLines.Count + "/" + headLines.Count + " 条，间距 "
            + view.Hatches[0].Spacing.ToString("0") + "mm）");
    }

    private static void OffsetCutInsideWallThicknessProducesSection()
    {
        var model = new BuildingModelDocument();
        model.Storeys.Add(new StoreyModel { Id = "F", Elevation = 0d, Height = 3000d });
        model.Walls.Add(new WallModel
        {
            Id = "W", StoreyId = "F", X1 = 0d, Y1 = 0d, X2 = 5000d, Y2 = 0d,
            Thickness = 200d
        });
        foreach (var offset in new[] { 0d, -50d, 50d })
        {
            var view = OrthographicProjector.Project(model, new ViewDefinitionModel
            {
                Id = "offset-cut", Kind = ViewKind.Section,
                CutAxis = SectionAxis.CutY, CutPosition = offset, ViewSign = 1
            });
            Assert(view.Hatches.Count == 1 && view.Lines.Any(line => line.Layer == ViewLayers.Cut),
                "剖切线 Y=" + offset + " 位于 200mm 墙内，应有墙断面");
        }
        Console.WriteLine("PASS 墙厚内偏心剖切：Y=0/±50 均有断面");
    }

    private static void WallAndOpeningEditUpdatesElevationAndSection()
    {
        // G0 金样：同一构件 ID 的墙加长、加厚，附着的窗沿墙移动。
        // 立面要跟随端点/窗位置，偏心剖面要从未剖到变成剖到。
        var model = new BuildingModelDocument { Name = "墙窗联动金样" };
        model.Storeys.Add(new StoreyModel { Id = "1F", Elevation = 0d, Height = 3000d });
        var wall = new WallModel { Id = "W-1", StoreyId = "1F", X1 = 0d, Y1 = 0d,
            X2 = 5000d, Y2 = 0d, Thickness = 240d };
        var opening = new OpeningModel { Id = "O-1", HostWallId = "W-1", Kind = "窗",
            Offset = 2000d, Width = 1200d, Height = 1500d, Sill = 900d };
        model.Walls.Add(wall);
        model.Openings.Add(opening);
        var elevationDefinition = new ViewDefinitionModel { Id = "golden-south", Title = "南立面",
            Kind = ViewKind.Elevation, Direction = ElevationDirection.South };
        var sectionDefinition = new ViewDefinitionModel { Id = "golden-section", Title = "偏心剖面",
            Kind = ViewKind.Section, CutAxis = SectionAxis.CutY, CutPosition = 140d, ViewSign = 1 };
        var before = OrthographicProjector.Project(model, elevationDefinition);
        var beforeWindow = before.Anchors.Single(a => a.ElementId == "O-1");
        var beforeSection = OrthographicProjector.Project(model, sectionDefinition);
        Assert(beforeSection.Hatches.Count == 0, "金样前置条件：Y=140 应在 240 厚墙以外");

        wall.X2 = 6000d;
        wall.Thickness = 300d;
        opening.Offset = 2400d;
        Assert(PlanEditing.ValidateOpening(model, wall, opening) == null, "编辑后窗仍须在所属墙内");
        var after = OrthographicProjector.Project(model, elevationDefinition);
        var afterWindow = after.Anchors.Single(a => a.ElementId == "O-1");
        var afterSection = OrthographicProjector.Project(model, sectionDefinition);
        Assert(Math.Abs(afterWindow.X1 - beforeWindow.X1 - 400d) < Tolerance,
            "窗沿墙移动 400 后，立面锚点必须同步移动");
        Assert(after.Lines.Max(l => Math.Max(l.X1, l.X2)) > before.Lines.Max(l => Math.Max(l.X1, l.X2)) + 900d,
            "墙加长 1000 后，立面右端必须延伸");
        Assert(afterSection.Hatches.Count == 1 && afterSection.Lines.Any(l => l.Layer == ViewLayers.Cut),
            "墙厚改为 300 后，Y=140 剖面必须出现墙断面");
        Console.WriteLine("PASS 墙窗联动金样：墙加长/加厚、窗平移、立面与偏心剖面同步更新");
    }

    /// <summary>填充边界的包围盒尺寸（宽, 高）。</summary>
    private static double[] SizeOf(ViewHatch hatch)
    {
        var us = hatch.Boundary.Select(p => p.X).ToList();
        var zs = hatch.Boundary.Select(p => p.Y).ToList();
        return new[] { us.Max() - us.Min(), zs.Max() - zs.Min() };
    }

    private static string Describe(List<double[]> sizes)
    {
        return string.Join("、", sizes.Select(s => s[0].ToString("0") + "×" + s[1].ToString("0")).ToArray());
    }

    private static void JsonRoundTripsWithoutLoss(BuildingModelDocument model)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoBuildingModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var modelPath = Path.Combine(root, "model.json");
            BuildingModelJson.SaveModel(modelPath, model);
            var reloaded = BuildingModelJson.LoadModel(modelPath);
            Assert(reloaded.Walls.Count == model.Walls.Count, "墙数量在往返后不一致");
            Assert(reloaded.Openings.Count == model.Openings.Count, "洞口数量在往返后不一致");
            Assert(reloaded.Slabs.Count == model.Slabs.Count, "楼板数量在往返后不一致");
            Assert(Math.Abs(reloaded.Storeys[1].Elevation - 3600d) < Tolerance, "楼层标高在往返后不一致");
            Assert(Math.Abs(reloaded.Openings[0].Sill - 900d) < Tolerance, "窗台高在往返后不一致");

            var view = Project(reloaded, SampleModelFactory.CreateDefaultViews(model.Name)[0]);
            var viewPath = Path.Combine(root, "views", "elev-south.json");
            BuildingModelJson.SaveView(viewPath, view);
            var reloadedView = BuildingModelJson.LoadView(viewPath);
            Assert(reloadedView.Lines.Count == view.Lines.Count, "视图线条数量在往返后不一致");
            Assert(reloadedView.Texts.Count == view.Texts.Count, "视图文字数量在往返后不一致");
            Assert(reloadedView.Anchors.Count == view.Anchors.Count, "视图锚点数量在往返后不一致");
            Assert(reloadedView.Dimensions.Count == view.Dimensions.Count, "视图尺寸数量在往返后不一致");
            Assert(reloadedView.Hatches.Count == view.Hatches.Count, "视图填充数量在往返后不一致");
            Assert(reloadedView.SchemaVersion == BuildingModelSchema.Version, "视图版本号不正确");

            Console.WriteLine("PASS 中间格式往返：模型 " + new FileInfo(modelPath).Length + " 字节、视图 "
                + new FileInfo(viewPath).Length + " 字节");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static ViewDocument Project(BuildingModelDocument model, ElevationDirection direction, string title)
    {
        return OrthographicProjector.Project(model, new ViewDefinitionModel
        {
            Id = "test", Title = title, Kind = ViewKind.Elevation, Scale = 100, Direction = direction
        });
    }

    private static ViewDocument Project(BuildingModelDocument model, ViewDefinitionModel definition)
    {
        return OrthographicProjector.Project(model, definition);
    }

    /// <summary>指定宽度的洞口在立面里的左边界（取该宽度的水平边）。</summary>
    private static double LeftEdgeOfWidth(ViewDocument view, double width)
    {
        var matches = view.Lines
            .Where(l => l.Layer == ViewLayers.Opening
                && Math.Abs(l.Y2 - l.Y1) < Tolerance
                && Math.Abs(Math.Abs(l.X2 - l.X1) - width) < Tolerance)
            .Select(l => Math.Min(l.X1, l.X2)).ToList();
        return matches.Count == 0 ? double.NaN : matches.Min();
    }

    private static double Width(ViewDocument view)
    {
        var lines = view.Lines.Where(l => l.Layer == ViewLayers.Elevation).ToList();
        if (lines.Count == 0) return 0d;
        return lines.Max(l => Math.Max(l.X1, l.X2)) - lines.Min(l => Math.Min(l.X1, l.X2));
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
