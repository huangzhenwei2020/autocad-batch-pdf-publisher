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
        CheckOffsetMiterOwnership();
        CheckMovedCornerCloses();
        CheckOrthogonalTJoint();
        CheckUnequalHeightJunction();
        CheckOpeningWallJunction();
        CheckFractionalWallCoordinates();
        CheckNearOrthogonalCorner();
        CheckWindowFrameCorners();
        CheckClosedDoorWindows();
        CheckStoreySettings();
        CheckStandardStoreys();
        CheckWallReferencePlacement();
        MeasureJunctionGrid();
        CheckSharedAxesAndCadJunction();
        Console.WriteLine("PASS 三维体量构件身份：样例墙/门窗/板/柱可追溯，参数修改后 ID 稳定");
    }

    private static void CheckWindowFrameCorners()
    {
        var model = SampleModelFactory.CreateEmptyModel("窗框接角");
        model.Walls.Add(new WallModel { Id = "wall", StoreyId = "1F", X2 = 6000, Thickness = 240 });
        model.Openings.Add(new OpeningModel { Id = "window", HostWallId = "wall", Offset = 3000,
            Width = 1500, Height = 1800, Sill = 900 });
        var volume = BuildingVolumeBuilder.Build(model);
        var front = volume.Faces.Where(f => f.Kind == "frame" && f.NormalY > 0.9).ToArray();
        var area = front.Sum(f => (f.Points.Max(p => p.X)-f.Points.Min(p => p.X))
            * (f.Points.Max(p => p.Z)-f.Points.Min(p => p.Z)));
        var expected = 1500d*1800 - (1500-120d)*(1800-120d);
        Assert(Math.Abs(area-expected) < 0.001, "窗框角部重复面：" + area + " / " + expected);
        Console.WriteLine("PASS 窗框接角：四条框边无重叠外表面");
    }

    private static void CheckClosedDoorWindows()
    {
        foreach(var rise in new[] { 0d, 1200d })
        foreach(var kind in new[] { "门联窗", "门连窗", "门" })
        {
            var model=SampleModelFactory.CreateEmptyModel("门联窗闭合显示");
            var wall=new WallModel { Id="wall",StoreyId="1F",X2=6000,Y2=rise,Thickness=240,AxisOffset=35 };
            model.Walls.Add(wall);
            var opening=new OpeningModel { Id="mlc",HostWallId="wall",Code="MLC3627",Kind=kind,
                Offset=3000,Width=3600,Height=2700,Sill=0 };
            model.Openings.Add(opening);
            var length=Math.Sqrt(wall.X2*wall.X2+wall.Y2*wall.Y2);
            double Normal(double x,double y) => (-wall.Y2*x+wall.X2*y)/length-35;
            var parts=BuildingVolumeBuilder.Build(model).Faces.Where(f=>f.ElementId==opening.Id).ToList();
            Assert(parts.Any(f=>f.Kind=="frame") && parts.Any(f=>f.Kind=="glass") && parts.All(f=>f.Kind!="door"),
                "门联窗必须显示闭合框和玻璃，不能生成打开的门扇");
            Assert(parts.SelectMany(f=>f.Points).All(p=>Math.Abs(Normal(p.X,p.Y))<=120.01),
                "门联窗构件不能突出到墙体外形成开启效果");
            var plan=OrthographicProjector.ProjectPlan(model,new ViewDefinitionModel { Kind=ViewKind.Plan,
                StoreyIds=new System.Collections.Generic.List<string> { "1F" } },null);
            var symbols=plan.Lines.Where(l=>l.Layer==ViewLayers.Opening).ToList();
            Assert(symbols.Count==2 && symbols.All(l=>Math.Abs((-wall.Y2*(l.X2-l.X1)+wall.X2*(l.Y2-l.Y1))/length)<.01
                && Math.Abs(Math.Sqrt(Math.Pow(l.X2-l.X1,2)+Math.Pow(l.Y2-l.Y1,2))-opening.Width)<.01),
                "门联窗平面图也不能画外伸门扇和开启弧");
            opening.Code="M3627"; opening.Kind="门";
            Assert(BuildingVolumeBuilder.Build(model).Faces.Any(f=>f.ElementId==opening.Id && f.Kind=="door"),
                "普通门应继续保留原有开启显示");
        }
        Console.WriteLine("PASS 门联窗闭合显示：正交融合墙、斜墙、名称别字和旧 MLC 门类别均不画开启扇");
    }

    private static void CheckNearOrthogonalCorner()
    {
        foreach (var drift in new[] { 0d, 0.0005d, 0.0035d, -0.0035d, 0.009d })
        {
            var model = SampleModelFactory.CreateEmptyModel("编辑后微偏墙角");
            model.Walls.Add(new WallModel { Id = "H", StoreyId = "1F",
                X1 = 0, Y1 = 0, X2 = 5000, Y2 = drift, Thickness = 240 });
            model.Walls.Add(new WallModel { Id = "V", StoreyId = "1F",
                X1 = 0, Y1 = 0, X2 = drift, Y2 = 4000, Thickness = 240 });
            var volume = BuildingVolumeBuilder.Build(model);
            foreach (var z in new[] { 100d, 1700d, 2900d })
            {
                Assert(volume.Faces.Any(f => f.Kind == "wall" && f.NormalX < -0.9
                    && f.Points.All(p => Math.Abs(p.X + 120) < 0.0001)
                    && f.Points.Min(p => p.Y) < -60 && f.Points.Max(p => p.Y) > -60
                    && f.Points.Min(p => p.Z) < z && f.Points.Max(p => p.Z) > z),
                    "微偏墙角西侧缺面：" + drift + " / " + z);
                Assert(volume.Faces.Any(f => f.Kind == "wall" && f.NormalY < -0.9
                    && f.Points.All(p => Math.Abs(p.Y + 120) < 0.0001)
                    && f.Points.Min(p => p.X) < -60 && f.Points.Max(p => p.X) > -60
                    && f.Points.Min(p => p.Z) < z && f.Points.Max(p => p.Z) > z),
                    "微偏墙角南侧缺面：" + drift + " / " + z);
            }
        }
        Console.WriteLine("PASS 编辑微偏墙：交角两侧全高度闭合");
    }

    private static void CheckFractionalWallCoordinates()
    {
        var model = SampleModelFactory.CreateEmptyModel("小数坐标墙面");
        const double left = -5285.714285714285, right = 2185.7142857142853;
        const double bottom = -1542.8571428571427, top = 2828.5714285714284;
        model.Walls.Add(new WallModel { Id = "east", StoreyId = "1F", X1 = right, X2 = right,
            Y1 = top, Y2 = bottom, Thickness = 240 });
        model.Walls.Add(new WallModel { Id = "south", StoreyId = "1F", X1 = right, X2 = left,
            Y1 = bottom, Y2 = bottom, Thickness = 240 });
        model.Walls.Add(new WallModel { Id = "west", StoreyId = "1F", X1 = left, X2 = left,
            Y1 = bottom, Y2 = top, Thickness = 240 });
        model.Walls.Add(new WallModel { Id = "north", StoreyId = "1F", X1 = left, X2 = right,
            Y1 = top, Y2 = top, Thickness = 240 });
        model.Openings.Add(new OpeningModel { Id = "door", HostWallId = "north",
            Kind = "门", Offset = 1613.4038071516084, Width = 900, Height = 2100 });
        model.Openings.Add(new OpeningModel { Id = "n-window", HostWallId = "north",
            Offset = 4778.874811859895, Width = 1500, Height = 1800, Sill = 900 });
        model.Openings.Add(new OpeningModel { Id = "w-window", HostWallId = "west",
            Offset = 2111.2639945354163, Width = 1500, Height = 1800, Sill = 900 });
        model.Openings.Add(new OpeningModel { Id = "s-window", HostWallId = "south",
            Offset = 3861.3863160152714, Width = 1500, Height = 1800, Sill = 900 });
        double Area(BuildingVolume volume)
        {
            return volume.Faces.Where(f => f.Kind == "wall").Sum(f =>
            {
                double x = 0, y = 0, z = 0;
                for (var i = 1; i + 1 < f.Points.Count; i++)
                {
                    var a = f.Points[0]; var b = f.Points[i]; var c = f.Points[i + 1];
                    x += (b.Y-a.Y)*(c.Z-a.Z)-(b.Z-a.Z)*(c.Y-a.Y);
                    y += (b.Z-a.Z)*(c.X-a.X)-(b.X-a.X)*(c.Z-a.Z);
                    z += (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
                }
                return Math.Sqrt(x*x+y*y+z*z)/2;
            });
        }
        var fractional = Area(BuildingVolumeBuilder.Build(model));
        var analytical = 4 * (right-left+top-bottom) * model.Storeys[0].Height
            + 960 * (right-left+top-bottom)
            - 2 * (900*2100 + 3*1500*1800)
            + 240 * (900+2*2100 + 3*(2*1500+2*1800)) - 900*240;
        Assert(Math.Abs(fractional - analytical) < 1,
            "实际墙表面积不完整：" + fractional + " / " + analytical);
        var rounded = BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));
        foreach (var wall in rounded.Walls)
        {
            wall.X1 = Math.Round(wall.X1, 7); wall.X2 = Math.Round(wall.X2, 7);
            wall.Y1 = Math.Round(wall.Y1, 7); wall.Y2 = Math.Round(wall.Y2, 7);
        }
        var expected = Area(BuildingVolumeBuilder.Build(rounded));
        Assert(Math.Abs(fractional - expected) < 1,
            "小数坐标使墙面丢失：" + fractional + " / " + expected);
        Console.WriteLine("PASS 小数坐标：反向墙与多个洞口不因浮点切片丢面");
    }

    private static void CheckOrthogonalCorner()
    {
        var model = SampleModelFactory.CreateEmptyModel("墙角收口");
        model.Walls.Add(new WallModel { Id = "horizontal", StoreyId = "1F",
            X1 = -1000, Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "vertical", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 1000, Thickness = 200 });
        model.Openings.Add(new OpeningModel { Id = "window", HostWallId = "horizontal",
            Kind = "窗", Offset = 500, Width = 300, Height = 800, Sill = 900 });
        var volume = BuildingVolumeBuilder.Build(model);
        var top = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5
            && f.Points.All(p => Math.Abs(p.Z - model.FindStorey("1F").Height) < 0.001d)).ToArray();
        var bottom = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ < -0.5
            && f.Points.All(p => Math.Abs(p.Z) < 0.001d)).ToArray();
        foreach (var point in new[] { (x: 40d, y: -50d), (x: 10d, y: 10d),
            (x: -510d, y: 10d), (x: 10d, y: 510d) })
        {
            var count = top.Count(f => InsideTopFace(f, point.x, point.y));
            Assert(count == 1, "正交墙角顶面缺失或重叠：" + point + "，面数 " + count);
            Assert(bottom.Count(f => InsideTopFace(f, point.x, point.y)) == 1,
                "带窗墙角底面缺失或重叠：" + point);
        }
        Assert(top.Single(f => InsideTopFace(f, 40, -50)).ElementId
            == bottom.Single(f => InsideTopFace(f, 40, -50)).ElementId,
            "墙角上下表面斜接归属不一致");
        Assert(!volume.Faces.Any(f => f.Kind == "wall" && Math.Abs(f.NormalZ) < 0.5
            && f.Points.All(p => Math.Abs(p.X) < 1e-6)
            && f.Points.Min(p => p.Y) < -49 && f.Points.Max(p => p.Y) > -51),
            "墙角内部仍存在立面重面");
        Assert(volume.Faces.Where(f => f.Kind == "wall")
            .Select(f => f.ElementId).Distinct().Count() == 2, "墙角融合后墙 ID 丢失");
        Console.WriteLine("PASS 正交墙角：缺角填合、顶面无重叠、内部面消除、两道墙可追溯");
    }

    private static void CheckOffsetMiterOwnership()
    {
        var model = SampleModelFactory.CreateEmptyModel("偏心斜接墙角");
        model.Walls.Add(new WallModel { Id = "H", StoreyId = "1F", X1 = 0, Y1 = 0,
            X2 = 1000, Y2 = 0, Thickness = 200, AxisOffset = -30 });
        model.Walls.Add(new WallModel { Id = "V", StoreyId = "1F", X1 = 0, Y1 = 0,
            X2 = 0, Y2 = 1000, Thickness = 200, AxisOffset = 20 });
        var faces = BuildingVolumeBuilder.Build(model).Faces;
        var top = faces
            .Where(f => f.Kind == "wall" && f.NormalZ > 0.5).ToArray();
        Assert(top.Single(f => InsideTopFace(f, 50, -100)).ElementId == "H"
            && top.Single(f => InsideTopFace(f, -80, 50)).ElementId == "V",
            "偏心墙角顶面的斜切归属错误");
        Assert(faces.Any(f => f.Kind == "wall" && f.ElementId == "H"
            && f.NormalY < -0.5 && f.Points.All(p => Math.Abs(p.Y + 130) < 0.001)
            && f.Points.Min(p => p.X) < 50 && f.Points.Max(p => p.X) > 50),
            "偏心斜接的外侧面应与横墙选中高亮一致");
        Assert(faces.Any(f => f.Kind == "wall" && f.ElementId == "V"
            && f.NormalX < -0.5 && f.Points.All(p => Math.Abs(p.X + 120) < 0.001)
            && f.Points.Min(p => p.Y) < 50 && f.Points.Max(p => p.Y) > 50),
            "偏心斜接的外侧面应与竖墙选中高亮一致");
        Console.WriteLine("PASS 偏心墙角：顶面和外侧面沿斜线分属两墙");
    }

    private static void CheckMovedCornerCloses()
    {
        var model = SampleModelFactory.CreateEmptyModel("移动后的墙角");
        model.Walls.Add(new WallModel { Id = "H", StoreyId = "1F", X1 = -1000,
            Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "V", StoreyId = "1F", X1 = 0,
            Y1 = 0, X2 = 0, Y2 = 1000, Thickness = 200 });
        var session = new BuildingModelEditSession(model);
        Assert(session.TryTransformWall("V", 20, 25, 0, false, out _, out var error),
            "移动墙失败：" + error);
        var faces = BuildingVolumeBuilder.Build(session.Model).Faces;
        var top = faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5).ToArray();
        foreach (var point in new[] { (x: -50d, y: -60d), (x: 10d, y: -40d),
            (x: 50d, y: 50d) })
            Assert(top.Count(f => InsideTopFace(f, point.x, point.y)) == 1,
                "移动后墙角出现顶面缺口或重面：" + point + "，面数 "
                    + top.Count(f => InsideTopFace(f, point.x, point.y)));
        Assert(top.Any(f => f.ElementId == "H" && InsideTopFace(f, -50, -50))
            && top.Any(f => f.ElementId == "V" && InsideTopFace(f, 50, 50)),
            "移动后斜接顶面未归属到对应墙体");
        Assert(faces.Any(f => f.Kind == "wall" && f.ElementId == "H"
            && f.NormalY < -0.5 && f.Points.All(p => Math.Abs(p.Y + 100) < 0.001)
            && f.Points.Min(p => p.X) < 50 && f.Points.Max(p => p.X) > 50),
            "移动后墙角外侧漏出横墙端面");
        Assert(faces.Any(f => f.Kind == "wall" && f.ElementId == "V"
            && f.NormalX > 0.5 && f.Points.All(p => Math.Abs(p.X - 120) < 0.001)
            && f.Points.Min(p => p.Y) < 50 && f.Points.Max(p => p.Y) > 50),
            "移动后墙角外侧漏出竖墙端面");
        Console.WriteLine("PASS 墙体移动后端点错开：自动补角、顶面无缺口或重面");
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
            var count = top.Count(f => InsideTopFace(f, point.x, point.y));
            Assert(count == 1, "T 形交接顶面缺失或重叠：" + point + "，面数 " + count);
        }
        Console.WriteLine("PASS 不同墙厚 T 形交接：墙体顶面无缺角或重面");
    }

    private static void CheckUnequalHeightJunction()
    {
        var model = SampleModelFactory.CreateEmptyModel("高低墙交接");
        model.Walls.Add(new WallModel { Id = "tall", StoreyId = "1F",
            X1 = -1000, Y1 = 0, X2 = 1000, Y2 = 0, Thickness = 200, Height = 3000 });
        model.Walls.Add(new WallModel { Id = "low", StoreyId = "1F",
            X1 = 0, Y1 = -1000, X2 = 0, Y2 = 1000, Thickness = 200, Height = 1500 });
        var volume = BuildingVolumeBuilder.Build(model);
        var lowTop = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5
            && Math.Abs(f.Points[0].Z - 1500) < 0.001).ToArray();
        Assert(lowTop.Length > 0 && !lowTop.Any(f => f.Points.Min(p => p.X) < 0
            && f.Points.Max(p => p.X) > 0 && f.Points.Min(p => p.Y) < 0
            && f.Points.Max(p => p.Y) > 0),
            "高低墙重叠区不应出现低墙顶面（内部重面）");
        Console.WriteLine("PASS 高低墙交接：交叠空间无内部低墙顶面");
    }

    private static void CheckOpeningWallJunction()
    {
        var model = SampleModelFactory.CreateEmptyModel("带窗墙角");
        model.Walls.Add(new WallModel { Id = "h-window", StoreyId = "1F",
            X1 = -2000, Y1 = 0, X2 = 0, Y2 = 0, Thickness = 200 });
        model.Walls.Add(new WallModel { Id = "v-wall", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 2000, Thickness = 200 });
        model.Openings.Add(new OpeningModel { Id = "window", HostWallId = "h-window",
            Kind = "窗", Offset = 1000, Width = 600, Sill = 900, Height = 1200 });
        var volume = BuildingVolumeBuilder.Build(model);
        Assert(volume.Faces.Any(f => f.ElementId == "window" && f.Kind == "glass"),
            "墙角融合后窗框玻璃不应消失");
        var top = volume.Faces.Where(f => f.Kind == "wall" && f.NormalZ > 0.5).ToArray();
        Assert(top.Count(f => f.Points.Min(p => p.X) < 50 && f.Points.Max(p => p.X) > 50
            && f.Points.Min(p => p.Y) < 50 && f.Points.Max(p => p.Y) > 50) == 1,
            "带窗墙角顶面应融合为一份实体");
        var plan = OrthographicProjector.ProjectPlan(model,
            new ViewDefinitionModel { Id = "corner-plan", Title = "带窗墙角平面",
                Kind = ViewKind.Plan, StoreyIds = new System.Collections.Generic.List<string> { "1F" }, Scale = 100 }, null);
        Assert(!plan.Lines.Any(l => l.Layer == ViewLayers.Cut
            && Math.Abs(l.Y1 + plan.OriginY + 100) < 0.001
            && Math.Abs(l.Y2 + plan.OriginY + 100) < 0.001
            && l.X1 + plan.OriginX < -1300 && l.X2 + plan.OriginX > -700),
            "CAD 平面窗洞处不应被融合墙体封住");
        Console.WriteLine("PASS 带窗正交墙角：融合实体、洞口玻璃及 CAD 平面洞口并存");
    }

    private static void CheckStoreySettings()
    {
        var model = SampleModelFactory.CreateEmptyModel("楼层设置");
        model.Slabs.Add(new SlabModel { Id = "floor-slab", StoreyId = "2F",
            TopElevation = model.FindStorey("2F").Elevation });
        model.Slabs.Add(new SlabModel { Id = "roof-slab", StoreyId = "2F",
            TopElevation = model.FindStorey("2F").Elevation + model.FindStorey("2F").Height });
        var session = new BuildingModelEditSession(model);
        var oldSecondBase = model.FindStorey("2F").Elevation;
        var oldSecondTop = oldSecondBase + model.FindStorey("2F").Height;
        var floors = model.Storeys.Select(s => new StoreyModel { Id = s.Id,
            Name = s.Name, Elevation = s.Elevation, Height = 3600 }).ToList();
        floors[1].Elevation = floors[0].Elevation + floors[0].Height;
        floors.Add(new StoreyModel { Id = "3F", Name = "三层", Elevation = 7200, Height = 3300 });
        Assert(session.TryReplaceStoreys(floors, out var error) && error == null
            && session.Model.Storeys.Count == 3 && session.Model.Storeys[0].Height == 3600,
            "楼层窗口设置未应用");
        Assert(Math.Abs(session.Model.Slabs.Single(s => s.Id == "floor-slab").TopElevation
                - floors[1].Elevation) < 0.001d
            && Math.Abs(session.Model.Slabs.Single(s => s.Id == "roof-slab").TopElevation
                - (floors[1].Elevation + floors[1].Height)) < 0.001d,
            "楼层标高或层高修改后，所属楼板没有同步移动");
        Assert(session.Undo() && session.Model.Storeys.Count == 2,
            "楼层设置未作为一笔操作撤销");
        Assert(session.Model.Slabs.Single(s => s.Id == "floor-slab").TopElevation == oldSecondBase
            && session.Model.Slabs.Single(s => s.Id == "roof-slab").TopElevation == oldSecondTop,
            "撤销楼层设置未恢复楼板标高");
        var emptyFloor = model.Storeys.Select(s => new StoreyModel { Id = s.Id,
            Name = s.Name, Elevation = s.Elevation, Height = s.Height }).ToList();
        emptyFloor.Add(new StoreyModel { Id = "3F", Name = "三层", Elevation = 6900, Height = 3300 });
        Assert(session.TryReplaceStoreys(emptyFloor, out error), error);
        Assert(session.TryReplaceStoreys(emptyFloor.Where(s => s.Id != "3F"), out error),
            "空楼层应可删除：" + error);
        Assert(!session.TryReplaceStoreys(emptyFloor.Where(s => s.Id != "2F"), out error),
            "已有楼板的楼层不应被直接删除");
        Console.WriteLine("PASS 楼层设置：层高、新增楼层与撤销");
    }

    private static void CheckStandardStoreys()
    {
        var model = SampleModelFactory.CreateEmptyModel("标准层三维验证");
        model.Walls.Add(new WallModel { Id = "source-wall", StoreyId = "1F",
            X1 = 0, Y1 = 0, X2 = 4000, Y2 = 0, Thickness = 200 });
        model.Openings.Add(new OpeningModel { Id = "source-window", HostWallId = "source-wall",
            Kind = "窗", Offset = 1800, Width = 1000, Height = 1200, Sill = 900 });
        model.Slabs.Add(new SlabModel { Id = "source-slab", StoreyId = "1F",
            Thickness = 120, TopElevation = model.FindStorey("1F").Height,
            Outline = new System.Collections.Generic.List<PointModel>
            { new PointModel(0, 0), new PointModel(4000, 0),
              new PointModel(4000, 3000), new PointModel(0, 3000) } });
        var session = new BuildingModelEditSession(model);
        var floors = model.Storeys.Select(s => new StoreyModel { Id = s.Id, Name = s.Name,
            Elevation = s.Elevation, Height = s.Height }).ToList();
        floors.Add(new StoreyModel { Id = "3F", Name = "三层", Elevation = 6900,
            Height = 3300, TemplateStoreyId = "1F" });
        floors.Add(new StoreyModel { Id = "4F", Name = "四层", Elevation = 10200,
            Height = 3000, TemplateStoreyId = "1F" });
        Assert(session.TryReplaceStoreys(floors, out var error), error);
        Assert(session.Model.Walls.Count == 1 && session.Model.Slabs.Count == 1,
            "标准层不应在模型文件中重复存储平面构件");
        var reloaded = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Assert(reloaded.FindStorey("3F").TemplateStoreyId == "1F",
            "标准层引用保存后丢失");
        var physical = StandardStoreyLayout.Materialize(reloaded);
        Assert(physical.Walls.Count == 3 && physical.Openings.Count == 3 && physical.Slabs.Count == 3,
            "标准层没有为每个实际楼层生成墙、门窗和楼板");
        Assert(physical.Walls.Any(w => w.StoreyId == "3F")
            && physical.Openings.Any(o => o.HostWallId == "source-wall@STD@3F")
            && Math.Abs(physical.Slabs.Single(s => s.StoreyId == "3F").TopElevation - 10200d) < 0.001d,
            "标准层构件没有落在目标楼层的标高");
        Assert(Math.Abs(physical.Slabs.Single(s => s.StoreyId == "4F").TopElevation - 13200d) < 0.001d,
            "第二个引用层没有落在自己的标高");
        var volume = BuildingVolumeBuilder.Build(reloaded);
        Assert(volume.Faces.Any(f => f.StoreyId == "1F" && f.Kind == "wall")
            && volume.Faces.Any(f => f.StoreyId == "3F" && f.Kind == "wall")
            && volume.Faces.Any(f => f.StoreyId == "3F" && f.Kind == "slab")
            && volume.Faces.Any(f => f.StoreyId == "4F" && f.Kind == "wall")
            && volume.Faces.Any(f => f.StoreyId == "4F" && f.Kind == "slab")
            && volume.Faces.Where(f => f.StoreyId == "3F" && f.Kind == "wall")
                .SelectMany(f => f.Points).Min(p => p.Z) >= 6900d,
            "三维场景必须显示多层实体，不能只显示一层标准层");
        Assert(BuildingVolumeBuilder.Build(reloaded, "3F").Faces.All(f => f.StoreyId == "3F"),
            "按层显示混入其他楼层");
        var plan = OrthographicProjector.ProjectPlan(reloaded,
            new ViewDefinitionModel { Id = "standard-plan", Kind = ViewKind.Plan,
                StoreyIds = new System.Collections.Generic.List<string> { "3F" }, Scale = 100 }, null);
        Assert(plan.Lines.Count > 0, "标准层平面图缺少共用构件");
        Assert(session.TrySetWallLength("source-wall", 5000, out error), error);
        Assert(StandardStoreyLayout.Materialize(session.Model).Walls
            .Single(w => w.StoreyId == "3F").X2 == 5000,
            "修改来源层后，引用层没有同步更新");
        Assert(StandardStoreyLayout.Materialize(session.Model).Walls
            .Single(w => w.StoreyId == "4F").X2 == 5000,
            "第二个引用层没有同步来源层修改");
        var tooShort = floors.Select(s => new StoreyModel { Id = s.Id, Name = s.Name,
            Elevation = s.Elevation, Height = s.Id == "3F" ? 1900 : s.Height,
            TemplateStoreyId = s.TemplateStoreyId }).ToList();
        Assert(!session.TryReplaceStoreys(tooShort, out error),
            "目标楼层层高不足时应拒绝超高门窗");
        var targetWithOwnWall = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        targetWithOwnWall.Walls.Add(new WallModel { Id = "other", StoreyId = "3F",
            X1 = 0, Y1 = 1000, X2 = 4000, Y2 = 1000, Thickness = 200 });
        var guarded = new BuildingModelEditSession(targetWithOwnWall);
        Assert(!guarded.TryReplaceStoreys(floors, out error),
            "已有独立构件的楼层不能直接改为标准层引用");
        Assert(!session.TryReplaceStoreys(floors.Where(s => s.Id != "1F"), out error),
            "正在被引用的标准层来源不能删除");
        Console.WriteLine("PASS 标准层：共用平面构件，多层三维实体与标高、保存及删除保护");
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
        model.Axes.Add(new AxisModel { Id = "manual-horizontal", Vertical = false,
            Position = 0, Name = "A", StartName = "A-L", EndName = "A-R" });
        axes = BuildingAxisLayout.Resolve(model);
        shared = axes.Where(a => !a.Vertical && Math.Abs(a.Position) < 1e-6).ToArray();
        Assert(shared.Length == 1 && shared[0].Id == "manual-horizontal"
            && shared[0].StartName == "A-L" && shared[0].EndName == "A-R",
            "显式轴网两端轴号应覆盖自动轴号，并跨层共用");
        var guide = BuildingVolumeBuilder.Build(model).GuideLines.Single(g => g.IsBuildingAxis
            && g.ElementId == "manual-horizontal");
        Assert(guide.StartLabel == "A-L" && guide.EndLabel == "A-R",
            "三维地面轴线两端应保留不同轴号");
        ViewDocument Plan(string storey) => OrthographicProjector.ProjectPlan(model,
            new ViewDefinitionModel { Id = "plan-" + storey, Title = storey + "平面",
                Kind = ViewKind.Plan, Scale = 100,
                StoreyIds = new System.Collections.Generic.List<string> { storey } }, null);
        var first = Plan("1F");
        var second = Plan("2F");
        Assert(first.Anchors.Any(a => a.Kind == "axis" && a.ElementId == shared[0].Id)
            && second.Anchors.Any(a => a.Kind == "axis" && a.ElementId == shared[0].Id),
            "CAD 两层平面未引用同一轴线 ID");
        Assert(first.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == "A-L")
            && second.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == "A-L"),
            "CAD 两层平面轴号不一致");
        Assert(first.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == "A-L")
            && first.Texts.Any(t => t.Layer == ViewLayers.Axis && t.Text == "A-R"),
            "CAD 平面应保留轴线两端不同轴号");
        var editing = new BuildingModelEditSession(model);
        var changedAxes = model.Axes.Select(a => new AxisModel { Id = a.Id, Vertical = a.Vertical,
            Position = a.Position, Name = a.Name, StartName = a.StartName,
            EndName = a.EndName }).ToList();
        changedAxes[0].EndName = "A-E";
        Assert(editing.TryReplaceAxes(changedAxes, out var axisError) && axisError == null
            && editing.Model.Axes[0].EndName == "A-E", "轴号窗口修改未提交");
        Assert(editing.Undo() && editing.Model.Axes[0].EndName == "A-R",
            "轴号修改未作为一笔操作撤销");
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

    private static bool InsideTopFace(VolumeFace face, double x, double y)
    {
        var positive = false; var negative = false;
        for (var i = 0; i < face.Points.Count; i++)
        {
            var a = face.Points[i]; var b = face.Points[(i + 1) % face.Points.Count];
            var cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cross > 0.000001d) positive = true;
            if (cross < -0.000001d) negative = true;
            if (positive && negative) return false;
        }
        return true;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
