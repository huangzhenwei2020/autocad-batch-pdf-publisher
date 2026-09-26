using System;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

internal static class BuildingModelEditSessionTests
{
    public static void Run()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
        var wallId = "1F-S";
        var openingId = "1F-S-C1518";
        string error;

        Assert(session.TrySetWallLength(wallId, 8200d, out error), "墙加长失败：" + error);
        Assert(Length(session.Model, wallId) == 8200d, "墙长修改未提交");
        Assert(session.TrySetOpeningOffset(openingId, 2500d, out error), "窗移动失败：" + error);
        Assert(Offset(session.Model, openingId) == 2500d, "窗移动未提交");
        var revision = session.Revision;
        Assert(!session.TrySetWallLength(wallId, 5000d, out error), "缩墙后门超界仍被允许");
        Assert(!session.TrySetOpeningOffset(openingId, 5200d, out error), "窗与门重叠仍被允许");
        Assert(!session.TrySetWallLength(wallId, double.NaN, out error), "NaN 墙长仍被允许");
        Assert(session.Revision == revision && Length(session.Model, wallId) == 8200d
            && Offset(session.Model, openingId) == 2500d, "非法编辑修改了正式模型");

        Assert(session.Undo() && Offset(session.Model, openingId) == 2200d, "第一次撤销未恢复窗位");
        Assert(session.Undo() && Length(session.Model, wallId) == 7200d, "第二次撤销未恢复墙长");
        Assert(session.Redo() && Length(session.Model, wallId) == 8200d, "重做未恢复墙长");
        Assert(session.Model.Walls.Any(x => x.Id == wallId)
            && session.Model.Openings.Any(x => x.Id == openingId), "编辑/撤销后构件 ID 变化");
        var originalViews = BuildingModelViewPublisher.Generate(SampleModelFactory.CreateTwoStoreyHouse());
        var editedViews = BuildingModelViewPublisher.Generate(session.Model);
        var originalSouth = originalViews.First(x => x.Id == "elev-south");
        var editedSouth = editedViews.First(x => x.Id == "elev-south");
        Assert(editedViews.Any(x => x.Id == "section-1")
            && editedViews.Any(x => x.Id == "sheet-elevations"), "未生成剖面与图纸");
        Assert(editedSouth.Lines.Max(x => Math.Max(x.X1, x.X2))
            > originalSouth.Lines.Max(x => Math.Max(x.X1, x.X2)), "墙长修改未传递到南立面");
        var path = Path.Combine(Path.GetTempPath(), "wanluo-model-edit-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            BuildingModelJson.SaveModel(path, session.Model);
            var reopened = BuildingModelJson.LoadModel(path);
            Assert(Length(reopened, wallId) == 8200d && Offset(reopened, openingId) == 2200d,
                "保存并重新打开后参数不一致");
            Assert(reopened.Walls.Any(x => x.Id == wallId) && reopened.Openings.Any(x => x.Id == openingId),
                "保存并重新打开后构件 ID 变化");
            Assert(session.TrySetOpeningOffset(openingId, 2500d, out error), "再次修改窗位失败：" + error);
            BuildingModelJson.SaveModel(path, session.Model);
            Assert(Offset(BuildingModelJson.LoadModel(path), openingId) == 2500d,
                "覆盖保存后内容不是新模型");
            Assert(File.Exists(path + ".bak") && Offset(BuildingModelJson.LoadModel(path + ".bak"), openingId) == 2200d,
                "覆盖保存未保留上一个版本");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
        var outputFolder = Path.Combine(Path.GetTempPath(), "wanluo-views-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputFolder);
        try
        {
            var modelFile = Path.Combine(outputFolder, "model.json");
            BuildingModelJson.SaveModel(modelFile, session.Model);
            var count = BuildingModelViewPublisher.Publish(modelFile, session.Model);
            var viewsFolder = Path.Combine(outputFolder, StudioLaunch.ViewsFolderName);
            Assert(count > 5 && Directory.GetFiles(viewsFolder, "*.json").Length == count,
                "视图文件数量不正确");
            Assert(StudioLaunch.ListViews(outputFolder).Count == count,
                "现有 CAD 取件流程无法识别新生成的视图");
            var south = BuildingModelJson.LoadView(Path.Combine(viewsFolder, "elev-south.json"));
            Assert(south.Lines.Max(x => Math.Max(x.X1, x.X2))
                > originalSouth.Lines.Max(x => Math.Max(x.X1, x.X2)), "落盘视图未包含编辑后的墙长");
        }
        finally
        {
            var viewsFolder = Path.Combine(outputFolder, StudioLaunch.ViewsFolderName);
            if (Directory.Exists(viewsFolder))
            {
                foreach (var file in Directory.GetFiles(viewsFolder)) File.Delete(file);
                Directory.Delete(viewsFolder);
            }
            foreach (var file in Directory.GetFiles(outputFolder)) File.Delete(file);
            Directory.Delete(outputFolder);
        }
        Console.WriteLine("PASS 跨平台墙窗编辑事务：校验失败不提交，撤销/重做恢复参数与构件 ID");
        Console.WriteLine("PASS 模型文件：编辑后保存/重新打开保留参数与 ID，覆盖保存保留备份");
        Console.WriteLine("PASS CAD 视图再生：墙长修改传递到立面，剖面和图纸落盘可重新读取");
    }

    private static double Length(BuildingModelDocument model, string id)
    {
        var wall = model.Walls.First(x => x.Id == id);
        return Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Y2 - wall.Y1, 2));
    }

    private static double Offset(BuildingModelDocument model, string id)
    {
        return model.Openings.First(x => x.Id == id).Offset;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
