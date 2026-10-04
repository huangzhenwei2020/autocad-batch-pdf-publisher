using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;

internal static class SlabGeometryTests
{
    public static void Run()
    {
        var model = SampleModelFactory.CreateEmptyModel("slab-openings");
        model.Storeys.RemoveAll(s => s.Id == "1F");
        model.Slabs.Clear();
        var slab = new SlabModel { StoreyId = "2F", Thickness = 180, Outline = Rect(0, 0, 6000, 5000),
            Openings = new List<SlabOpeningModel> {
                new SlabOpeningModel { Id = "stair", Name = "楼梯井", Outline = Rect(1000, 1000, 2500, 3000) },
                new SlabOpeningModel { Id = "lift", Name = "电梯井", Outline = Rect(3500, 1000, 4500, 3000) } } };
        var session = new BuildingModelEditSession(model);
        Assert(session.TryUpsertSlab(slab, out var id, out var error), error);
        Assert(session.Model.Slabs[0].TopOffset == 0
            && session.Model.TopElevationOf(session.Model.Slabs[0]) == 3600, "新板顶未默认齐所属楼层");
        // Caller mutation must not alter the committed geometry.
        slab.Outline[0].X = -1000;
        Assert(session.Model.Slabs[0].Outline[0].X == 0, "编辑事务保留了调用方可变引用");
        var committed = session.Model.Slabs[0];
        var geometry = SlabGeometry.Build(committed);
        Assert(Math.Abs(geometry.Triangles.Sum(Area) - 25000000) < 0.01, "带两孔楼板面积不正确");
        Assert(!geometry.Triangles.Any(t => Contains(t, new PointModel(1700, 1800))
            || Contains(t, new PointModel(3900, 1800))), "板顶仍盖住井道");
        var volume = BuildingVolumeBuilder.Build(session.Model);
        Assert(volume.Faces.All(f => f.ElementId == id && f.StoreyId == "2F"), "楼板面身份丢失");
        Assert(volume.MinZ == 3420 && volume.MaxZ == 3600, "板厚方向错误");
        Assert(volume.Faces.Count(f => Math.Abs(f.NormalZ) < 0.5) == 12, "外圈或井道侧壁缺失");
        string PointKey(Point3DModel p) => string.Join(",", new[] { p.X, p.Y, p.Z }
            .Select(v => Math.Round(v, 6).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var edges = new Dictionary<string, int>();
        foreach (var face in volume.Faces)
            for (var i = 0; i < face.Points.Count; i++)
            {
                var a = PointKey(face.Points[i]); var b = PointKey(face.Points[(i + 1) % face.Points.Count]);
                var key = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                edges[key] = edges.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        Assert(edges.Values.All(count => count == 2), "楼板不是闭合实体，存在缺边或非流形边");
        var plan = OrthographicProjector.ProjectPlan(session.Model, new ViewDefinitionModel
            { Id = "plan", Kind = ViewKind.Plan, StoreyIds = new List<string> { "2F" } }, null);
        Assert(plan.Lines.Count(l => l.Layer == ViewLayers.Slab) == 12, "CAD 平面缺少孔洞或板边界");
        Assert(ViewLayers.Find(ViewLayers.Slab) != null, "楼板图层没有注册");
        var section = OrthographicProjector.Project(session.Model, new ViewDefinitionModel
            { Id = "cut", Kind = ViewKind.Section, CutAxis = SectionAxis.CutY,
                CutPosition = 2000, StoreyIds = new List<string> { "2F" } });
        var hatchWidths = section.Hatches.Select(h => h.Boundary.Max(p => p.X) - h.Boundary.Min(p => p.X))
            .OrderBy(w => w).ToArray();
        Assert(hatchWidths.Length == 3 && Math.Abs(hatchWidths.Sum() - 3500) < 0.01,
            "剖切楼板应分成三段，不能填满两个井道");
        Assert(session.Undo() && session.Model.Slabs.Count == 0 && session.Redo(), "板编辑不能撤销重做");
        var reload = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        Assert(reload.Slabs[0].Openings.Count == 2, "楼板洞口保存后丢失");
        var floors = reload.Storeys;
        floors.Add(new StoreyModel { Id = "3F", Name = "三层", Elevation = 6900, Height = 3300,
            TemplateStoreyId = "2F" });
        var expanded = StandardStoreyLayout.Materialize(reload);
        Assert(expanded.Slabs.Single(s => s.StoreyId == "3F").Openings.Count == 2
            && expanded.Slabs.Single(s => s.StoreyId == "3F").Openings[0].Id == "stair@STD@3F",
            "标准层井道位置或身份丢失");
        var invalid = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model)).Slabs[0];
        invalid.Openings[0].Outline = Rect(-1, 1000, 2500, 3000);
        var revision = session.Revision;
        Assert(!session.TryUpsertSlab(invalid, out _, out _) && session.Revision == revision,
            "洞口跨界未拒绝或失败编辑产生提交");
        invalid.Openings[0].Outline = Rect(0, 1000, 2500, 3000);
        Assert(SlabGeometry.Validate(invalid) != null, "洞口贴外边界未拒绝");
        invalid.Openings[0].Outline = Rect(3400, 1000, 4500, 3000);
        Assert(SlabGeometry.Validate(invalid) != null, "洞口重叠未拒绝");
        invalid.Openings[0].Outline = Rect(3600, 1500, 4000, 2000);
        Assert(SlabGeometry.Validate(invalid) != null, "嵌套洞口未拒绝");
        invalid.Openings.Clear();
        invalid.Outline = new List<PointModel> { new PointModel(0, 0), new PointModel(2000, 2000),
            new PointModel(0, 2000), new PointModel(3000, 0) };
        Assert(SlabGeometry.Validate(invalid) != null, "自交轮廓未拒绝");
        invalid.Outline = new List<PointModel> { new PointModel(0, 0), new PointModel(2000, 0),
            new PointModel(2000, 1000), new PointModel(1000, 1000), new PointModel(1000, 2000),
            new PointModel(0, 2000), new PointModel(0, 0) };
        Assert(Math.Abs(SlabGeometry.Build(invalid).Triangles.Sum(Area) - 3000000) < 0.01,
            "凹轮廓或重复闭合点三角化错误");
        foreach (var p in invalid.Outline) { p.X += 1e9; p.Y += 1e9; }
        Assert(Math.Abs(SlabGeometry.Build(invalid).Triangles.Sum(Area) - 3000000) < 0.01,
            "大坐标下精度丢失");
        Directory.CreateDirectory(".artifacts/slab-openings");
        File.WriteAllText(".artifacts/slab-openings/model.json", BuildingModelJson.ToJson(session.Model));
        var designModel = BuildingModelJson.FromJson(BuildingModelJson.ToJson(session.Model));
        designModel.Name = "住宅模型";
        designModel.Walls = new List<WallModel> {
            new WallModel { Id = "W-1", StoreyId = "2F", X1 = 0, Y1 = 0, X2 = 6000, Y2 = 0, Thickness = 240 },
            new WallModel { Id = "W-2", StoreyId = "2F", X1 = 6000, Y1 = 0, X2 = 6000, Y2 = 5000, Thickness = 240 },
            new WallModel { Id = "W-3", StoreyId = "2F", X1 = 6000, Y1 = 5000, X2 = 0, Y2 = 5000, Thickness = 240 },
            new WallModel { Id = "W-4", StoreyId = "2F", X1 = 0, Y1 = 5000, X2 = 0, Y2 = 0, Thickness = 240 } };
        designModel.Openings.Add(new OpeningModel { Id = "M1524", Code = "M1524", Kind = "门",
            HostWallId = "W-1", Offset = 3000, Width = 1500, Height = 2400 });
        designModel.Columns = new List<ColumnModel> {
            new ColumnModel { Id = "KZ-1", StoreyId = "2F", X = 0, Y = 0, Width = 300, Depth = 300 },
            new ColumnModel { Id = "KZ-2", StoreyId = "2F", X = 6000, Y = 0, Width = 300, Depth = 300 },
            new ColumnModel { Id = "KZ-3", StoreyId = "2F", X = 6000, Y = 5000, Width = 300, Depth = 300 },
            new ColumnModel { Id = "KZ-4", StoreyId = "2F", X = 0, Y = 5000, Width = 300, Depth = 300 } };
        var symbols = OrthographicProjector.CreatePlanDetailSymbols(designModel, "2F");
        var door=designModel.Openings.Single(o=>o.Id=="M1524");
        var doorCells=DoorWindowElevationGeometryBuilder.Build(OpeningElevationAdapter.ToScheduleItem(door,
            OpeningConstruction.Resolve(designModel,door),door.Width,door.Height)).Cells;
        var expectedLines=OpeningPlanGeometry.Build(door,OpeningConstruction.Resolve(designModel,door),240).Count;
        Assert(symbols.Count(l => l.Layer == ViewLayers.Opening) == expectedLines
            && symbols.Count(l => l.Layer == ViewLayers.Cut) == 16
            && symbols.Any(l => l.X1 == -150), "视口门窗及柱图例缺边或偏离原始毫米坐标");
        File.WriteAllText(".artifacts/slab-openings/design-model.json", BuildingModelJson.ToJson(designModel));
        var transform = new BuildingModelEditSession(session.Model);
        Assert(transform.TryTransformSlab(id, 7000, 100, true, out var copyId, out error), error);
        var copy = transform.Model.Slabs.Single(s => s.Id == copyId);
        Assert(copy.Code == "S-2" && copy.Openings.Count == 2
            && copy.Openings[0].Id != committed.Openings[0].Id
            && copy.Openings[0].Outline[0].X == committed.Openings[0].Outline[0].X + 7000,
            "复制楼板缺少洞口、独立身份或编号");
        Assert(transform.TryTransformSlab(copyId, -500, 50, false, out _, out error), error);
        Assert(transform.Model.Slabs.Single(s => s.Id == copyId).Outline[0].X == 6500, "移动板边界未更新");
        var transformRevision = transform.Revision;
        Assert(!transform.TryTransformSlab(copyId, double.NaN, 0, false, out _, out _)
            && transform.Revision == transformRevision, "非法位移产生提交");
        Assert(transform.Undo() && transform.Undo() && transform.Model.Slabs.Count == 1
            && transform.Redo() && transform.Redo(), "楼板变换撤销重做失败");
        Console.WriteLine("PASS 楼板：双井道、凹轮廓、面积、剖面断开、图层、非法输入、保存撤销、标准层");
    }

    private static List<PointModel> Rect(double x0, double y0, double x1, double y1)
        => new List<PointModel> { new PointModel(x0, y0), new PointModel(x1, y0),
            new PointModel(x1, y1), new PointModel(x0, y1) };
    private static double Cross(PointModel a, PointModel b, PointModel c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static double Area(List<PointModel> t) => Math.Abs(Cross(t[0], t[1], t[2])) / 2d;
    private static bool Contains(List<PointModel> t, PointModel p)
        => Cross(t[0], t[1], p) >= -1e-6 && Cross(t[1], t[2], p) >= -1e-6
            && Cross(t[2], t[0], p) >= -1e-6;
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
