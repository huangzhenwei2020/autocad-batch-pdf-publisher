using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BatchPdfPublisher.BuildingModel;

internal static class BuildingModelEditSessionTests
{
    public static void Run()
    {
        AddDeleteAndRestoreModel();
        RecoverInterruptedViewBatch();
        EditWallEndpoints();
        MoveJoinedWallGrip();
        MoveTWallJunction();
        TransformWallWithOpenings();
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
        var geometry = new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
        Assert(geometry.TrySetWallGeometry(wallId, 7600d, 300d, 3200d, out error),
            "墙参数修改失败：" + error);
        Assert(geometry.Model.Walls.First(x => x.Id == wallId).Thickness == 300d
            && geometry.Model.Walls.First(x => x.Id == wallId).Height == 3200d,
            "墙厚/墙高没有一起提交");
        var geometryRevision = geometry.Revision;
        Assert(geometry.TrySetWallGeometry(wallId, 7600d, 300d, 3200d, out error)
            && geometry.Revision == geometryRevision, "同值修改不应增加修订");
        Assert(!geometry.TrySetWallGeometry(wallId, 7600d, 300d, 2500d, out error)
            && error.Contains("顶部") && geometry.Revision == geometryRevision,
            "墙高低于窗顶仍被提交");
        Assert(!geometry.TrySetWallGeometry(wallId, 7600d, double.PositiveInfinity, 3200d, out error),
            "无穷墙厚仍被提交");
        var beforeOpeningView = OrthographicProjector.Project(geometry.Model,
            SampleModelFactory.CreateDefaultViews(geometry.Model.Name).First(x => x.Id == "elev-south"));
        Assert(geometry.TrySetOpeningGeometry(openingId, 2200d, 1700d, 1900d, 1000d, out error),
            "洞口参数修改失败：" + error);
        Assert(geometry.Model.Openings.First(x => x.Id == openingId).Width == 1700d,
            "洞口宽没有提交");
        var afterOpeningView = OrthographicProjector.Project(geometry.Model,
            SampleModelFactory.CreateDefaultViews(geometry.Model.Name).First(x => x.Id == "elev-south"));
        Assert(!beforeOpeningView.Lines.Where(x => x.Layer == ViewLayers.Opening)
            .Select(x => $"{x.X1:0.##},{x.Y1:0.##},{x.X2:0.##},{x.Y2:0.##}")
            .SequenceEqual(afterOpeningView.Lines.Where(x => x.Layer == ViewLayers.Opening)
                .Select(x => $"{x.X1:0.##},{x.Y1:0.##},{x.X2:0.##},{x.Y2:0.##}")),
            "门窗宽高和窗台参数未传递到立面线稿");
        geometryRevision = geometry.Revision;
        Assert(!geometry.TrySetOpeningGeometry(openingId, 2200d, 1700d, 2300d, 1000d, out error)
            && error.Contains("顶部") && geometry.Revision == geometryRevision,
            "洞口顶部越界仍被提交");
        Assert(!geometry.TrySetOpeningGeometry(openingId, 3500d, 3000d, 1900d, 1000d, out error)
            && error.Contains("重叠") && geometry.Revision == geometryRevision,
            "加宽洞口与门重叠仍被提交");
        Assert(geometry.Undo() && geometry.Model.Openings.First(x => x.Id == openingId).Width == 1500d,
            "撤销未恢复洞口宽");
        Assert(geometry.Undo() && geometry.Model.Walls.First(x => x.Id == wallId).Thickness == 240d,
            "撤销未恢复墙厚");
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
        var modelFolder = Path.Combine(outputFolder, StudioLaunch.ModelFolderName, "样例");
        Directory.CreateDirectory(modelFolder);
        try
        {
            var modelFile = Path.Combine(modelFolder, "model.json");
            BuildingModelJson.SaveModel(modelFile, session.Model);
            var wrongLocationRejected = false;
            try { BuildingModelViewPublisher.PublishToCad(Path.Combine(outputFolder, "model.json"), session.Model); }
            catch (InvalidOperationException) { wrongLocationRejected = true; }
            Assert(wrongLocationRejected, "不在项目建筑模型目录的文件仍被加入 CAD 待落图");
            var count = BuildingModelViewPublisher.Publish(modelFile, session.Model);
            var viewsFolder = Path.Combine(modelFolder, StudioLaunch.ViewsFolderName);
            Assert(count > 5 && Directory.GetFiles(viewsFolder, "*.json").Length == count,
                "视图文件数量不正确");
            Assert(StudioLaunch.ListViews(modelFolder).Count == count,
                "现有 CAD 取件流程无法识别新生成的视图");
            var south = BuildingModelJson.LoadView(Path.Combine(viewsFolder, "elev-south.json"));
            Assert(south.Lines.Max(x => Math.Max(x.X1, x.X2))
                > originalSouth.Lines.Max(x => Math.Max(x.X1, x.X2)), "落盘视图未包含编辑后的墙长");
            var pushed = BuildingModelViewPublisher.PublishToCad(modelFile, session.Model);
            Assert(pushed.ViewCount == count && pushed.PendingCount > 0
                && File.Exists(pushed.PendingFilePath), "推到 CAD 未生成待落图清单");
            var listed = StudioLaunch.ListViews(modelFolder);
            Assert(listed.Count(x => x.Pending) == pushed.PendingCount
                && listed.Where(x => x.Pending).All(x => x.Kind == ViewKind.Sheet),
                "CAD 清单没有优先标记图纸");
            var first = listed.First(x => x.Pending);
            StudioLaunch.RemovePending(modelFolder, first.Id);
            Assert(StudioLaunch.ListViews(modelFolder).Count(x => x.Pending) == pushed.PendingCount - 1,
                "CAD 落图后待办没有减少");
        }
        finally
        {
            var viewsFolder = Path.Combine(modelFolder, StudioLaunch.ViewsFolderName);
            if (Directory.Exists(viewsFolder))
            {
                foreach (var file in Directory.GetFiles(viewsFolder)) File.Delete(file);
                Directory.Delete(viewsFolder);
            }
            foreach (var file in Directory.GetFiles(modelFolder)) File.Delete(file);
            Directory.Delete(modelFolder);
            Directory.Delete(Path.Combine(outputFolder, StudioLaunch.ModelFolderName));
            Directory.Delete(outputFolder);
        }
        Console.WriteLine("PASS 跨平台墙窗编辑事务：校验失败不提交，撤销/重做恢复参数与构件 ID");
        Console.WriteLine("PASS 墙窗完整参数：墙长/厚/高与洞口定位/宽/高/窗台高原子修改，越界回滚、撤销生效");
        Console.WriteLine("PASS 模型文件：编辑后保存/重新打开保留参数与 ID，覆盖保存保留备份");
        Console.WriteLine("PASS CAD 视图再生：墙长修改传递到立面，剖面和图纸落盘可重新读取");
        Console.WriteLine("PASS CAD 待落图：优先标记图纸，CAD 清单识别，落图后待办递减");
    }

    private static void AddDeleteAndRestoreModel()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateEmptyModel("空项目"));
        string wallId, openingId, error;
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, Thickness = 240
        }, out wallId, out error), "新建墙失败：" + error);
        var window = PlanEditing.CreateOpening("窗", wallId, 2500);
        Assert(session.TryAddOpening(window, out openingId, out error), "新建窗失败：" + error);
        Assert(!session.TryAddOpening(PlanEditing.CreateOpening("门", wallId, 2500), out _, out error)
            && error.Contains("重叠"), "重叠门未被拒绝");
        var revision = session.Revision;
        Assert(!session.TryAddWall(new WallModel { StoreyId = "2F", X1 = double.NaN, X2 = 5000 },
            out _, out error) && session.Revision == revision, "非法墙修改了模型");
        Assert(session.TryDeleteElement(wallId, out error) && session.Model.Walls.Count == 0
            && session.Model.Openings.Count == 0, "删墙没有一并删除宿主洞口");
        Assert(session.Undo() && session.Model.Walls.Single().Id == wallId
            && session.Model.Openings.Single().Id == openingId, "撤销删墙未恢复 ID 和洞口");
        Assert(session.Redo() && session.Model.Walls.Count == 0, "重做删墙失败");
        Assert(session.Undo() && session.Model.Openings.Single().HostWallId == wallId,
            "重新撤销后门窗宿主错误");
        Console.WriteLine("PASS 新版公共编辑：增墙开窗、非法输入回滚、删墙级联与撤销重做");
    }

    private static void RecoverInterruptedViewBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "wanluo-batch-" + Guid.NewGuid().ToString("N"));
        var views = Path.Combine(root, StudioLaunch.ViewsFolderName);
        var stage = Path.Combine(root, ".views-staging-test");
        Directory.CreateDirectory(views);
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllText(Path.Combine(views, "old.json"), "old");
            File.WriteAllText(Path.Combine(stage, "new.json"), "new");
            Assert(StudioLaunch.CommitStagedViews(root, stage) == null
                && File.Exists(Path.Combine(views, "new.json"))
                && !File.Exists(Path.Combine(views, "old.json")), "发布后出现新旧混合视图");
            Assert(!StudioLaunch.RecoverInterruptedPublish(root), "完整新批次被误恢复成旧版");
            AssertThrows(() => StudioLaunch.CommitStagedViews(root, stage), "缺少暂存目录仍被切换");
            Assert(File.Exists(Path.Combine(views, "new.json")), "无效暂存目录改变了现有批次");
            var backup = Path.Combine(root, ".views-backup-interrupted");
            Directory.Move(views, backup);
            StudioLaunch.ListViews(root);
            Assert(File.Exists(Path.Combine(views, "new.json"))
                && !StudioLaunch.RecoverInterruptedPublish(root), "CAD 取图时未恢复中断的批次");
            BuildingModelJson.SaveView(Path.Combine(views, "batch0.json"),
                new ViewDocument { Id = "batch0", Title = "批次 0" });
            var writer = Task.Run(() =>
            {
                for (var i = 1; i <= 20; i++)
                {
                    var nextStage = Path.Combine(root, ".views-staging-" + i);
                    Directory.CreateDirectory(nextStage);
                    BuildingModelJson.SaveView(Path.Combine(nextStage, "batch" + i + ".json"),
                        new ViewDocument { Id = "batch" + i, Title = "批次 " + i });
                    StudioLaunch.CommitStagedViews(root, nextStage);
                }
            });
            var reader = Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var listed = StudioLaunch.ListViews(root);
                    Assert(listed.Count == 1, "CAD 并发取图时看到了缺失或混合的视图批次");
                }
            });
            Task.WaitAll(writer, reader);
            Console.WriteLine("PASS 视图整批切换：不混批、无效暂存回滚、中断恢复、并发取图");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void EditWallEndpoints()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateEmptyModel("夹点测试"));
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, Thickness = 240
        }, out var wallId, out var error), "夹点测试建墙失败：" + error);
        Assert(session.TryAddOpening(PlanEditing.CreateOpening("窗", wallId, 2500),
            out var openingId, out error), "夹点测试开窗失败：" + error);
        var revision = session.Revision;
        Assert(!session.TrySetWallEndpoints(wallId, 0, 0, 3000, 0, out error)
            && error.Contains("范围") && session.Revision == revision,
            "缩短墙使窗越界时未完整回滚");
        Assert(!session.TrySetWallEndpoints(wallId, double.NaN, 0, 5000, 0, out error)
            && session.Revision == revision, "非法夹点坐标修改了模型");
        Assert(session.TrySetWallEndpoints(wallId, 0, 0, 6000, 0, out error),
            "合法夹点移动失败：" + error);
        Assert(session.Model.Walls.Single().Id == wallId && session.Model.Walls.Single().X2 == 6000
            && session.Model.Openings.Single().HostWallId == wallId
            && session.Model.Openings.Single().Id == openingId, "夹点移动改变了构件身份");
        Assert(session.Undo() && session.Model.Walls.Single().X2 == 5000,
            "撤销夹点移动未恢复墙端");
        Console.WriteLine("PASS 墙端夹点：合法移动保留 ID，洞口越界和非法坐标回滚，撤销恢复");
    }

    private static void TransformWallWithOpenings()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateEmptyModel("整墙定位测试"));
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, Thickness = 240
        }, out var wallId, out var error), "测试建墙失败：" + error);
        Assert(session.TryAddOpening(PlanEditing.CreateOpening("窗", wallId, 2500),
            out var openingId, out error), "测试开窗失败：" + error);
        var before = session.Revision;
        Assert(!session.TryTransformWall(wallId, double.NaN, 0, 0, false, out _, out error)
            && session.Revision == before, "非法位移改变了模型");
        Assert(session.TryTransformWall(wallId, 1000, 2000, 90, false, out var movedId, out error),
            "整墙定位失败：" + error);
        var moved = session.Model.Walls.Single();
        Assert(movedId == wallId && Math.Abs(moved.X1 - 3500) < 0.001
            && Math.Abs(moved.Y1 - (-500)) < 0.001
            && Math.Abs(moved.X2 - 3500) < 0.001
            && Math.Abs(moved.Y2 - 4500) < 0.001,
            "墙没有绕中心旋转并平移到预期位置");
        Assert(session.Model.Openings.Single().Id == openingId
            && session.Model.Openings.Single().HostWallId == wallId,
            "整墙移动改变了门窗身份或宿主");
        Assert(session.TryTransformWall(wallId, 3000, 0, 0, true, out var copiedId, out error),
            "复制墙和门窗失败：" + error);
        Assert(copiedId != wallId && session.Model.Walls.Count == 2 && session.Model.Openings.Count == 2,
            "复制后墙和门窗数量或身份错误");
        var copiedOpening = session.Model.Openings.Single(x => x.HostWallId == copiedId);
        Assert(copiedOpening.Id != openingId && copiedOpening.Offset == 2500,
            "复制门窗未分配新 ID 或沿墙位置丢失");
        Assert(session.Undo() && session.Model.Walls.Count == 1 && session.Model.Openings.Count == 1,
            "撤销复制没有一并移除墙和门窗");
        Assert(session.Undo() && session.Model.Walls.Single().X1 == 0
            && session.Model.Openings.Single().Id == openingId,
            "撤销移动没有恢复墙或门窗");
        Assert(session.Redo() && session.Redo() && session.Model.Walls.Count == 2
            && session.Model.Openings.Any(x => x.Id == copiedOpening.Id && x.HostWallId == copiedId),
            "重做没有恢复复制的墙和门窗 ID");
        Console.WriteLine("PASS 整墙定位：移动/旋转保留宿主关系，复制门窗一并新建，非法输入回滚，撤销重做原子恢复");
    }

    private static void MoveJoinedWallGrip()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateEmptyModel("墙交接测试"));
        string error;
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, Thickness = 240
        }, out var firstId, out error), "交接主墙创建失败：" + error);
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 5000, Y1 = 0, X2 = 5000, Y2 = 4000, Thickness = 240
        }, out var secondId, out error), "交接支墙创建失败：" + error);
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 5000.3, Y1 = 0.2, X2 = 8000, Y2 = 0, Thickness = 240
        }, out var thirdId, out error), "容差内支墙创建失败：" + error);
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "2F", X1 = 5000, Y1 = 0, X2 = 7000, Y2 = 0, Thickness = 240
        }, out var otherFloorId, out error), "其他楼层墙创建失败：" + error);
        Assert(session.TryAddOpening(PlanEditing.CreateOpening("窗", secondId, 1500),
            out var openingId, out error), "交接墙开窗失败：" + error);
        var revision = session.Revision;
        Assert(!session.TryMoveWallGrip(firstId, 1, 5000, 3000, out error)
            && error.Contains("范围") && session.Revision == revision,
            "相接墙洞口越界时没有整笔回滚");
        Assert(session.Model.Walls.First(w => w.Id == secondId).Y1 == 0,
            "回滚后相接墙端点被改动");
        Assert(!session.TryMoveWallGrip(firstId, 2, 5200, 1000, out error)
            && session.Revision == revision, "非法端点序号改变模型");
        Assert(session.TryMoveWallGrip(firstId, 1, 5200, 1000, out error),
            "移动墙交接点失败：" + error);
        Assert(session.Revision == revision + 1, "联动移动应作为一笔编辑");
        Assert(session.Model.Walls.First(w => w.Id == firstId).X2 == 5200
            && session.Model.Walls.First(w => w.Id == secondId).Y1 == 1000
            && session.Model.Walls.First(w => w.Id == thirdId).X1 == 5200
            && session.Model.Walls.First(w => w.Id == thirdId).Y1 == 1000,
            "相接端点没有一起移到同一位置");
        Assert(session.Model.Walls.First(w => w.Id == otherFloorId).X1 == 5000
            && session.Model.Openings.First(o => o.Id == openingId).HostWallId == secondId,
            "其他楼层或宿主洞口被错误修改");
        Assert(session.Undo() && session.Model.Walls.First(w => w.Id == thirdId).X1 == 5000.3
            && session.Model.Walls.First(w => w.Id == secondId).Y1 == 0,
            "撤销没有恢复全部相接墙");
        Assert(session.Redo() && session.Model.Walls.First(w => w.Id == thirdId).X1 == 5200,
            "重做没有恢复墙交接");
        Console.WriteLine("PASS 墙交接夹点：同楼层容差内相接端点联动，洞口越界整笔回滚，撤销重做恢复");
    }

    private static void MoveTWallJunction()
    {
        var session = new BuildingModelEditSession(SampleModelFactory.CreateEmptyModel("T 形交接测试"));
        string error;
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 0, Y1 = 0, X2 = 6000, Y2 = 0, Thickness = 240
        }, out var hostId, out error), "T 形宿主墙创建失败：" + error);
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "1F", X1 = 3000, Y1 = 0, X2 = 3000, Y2 = 4000, Thickness = 240
        }, out var branchId, out error), "T 形支墙创建失败：" + error);
        Assert(session.TryAddWall(new WallModel
        {
            StoreyId = "2F", X1 = 3000, Y1 = 0, X2 = 3000, Y2 = 4000, Thickness = 240
        }, out var upperId, out error), "上层支墙创建失败：" + error);
        Assert(session.TryAddOpening(PlanEditing.CreateOpening("窗", branchId, 1500),
            out _, out error), "支墙窗创建失败：" + error);
        Assert(PlanEditing.TryProjectWallInterior(session.Model.Walls.First(w => w.Id == hostId),
            3000, 0, 0.5, out var fraction) && fraction == 0.5,
            "T 形交接点未识别为墙身内部");
        Assert(!PlanEditing.TryProjectWallInterior(session.Model.Walls.First(w => w.Id == hostId),
            6000, 0, 0.5, out _), "宿主墙端点被误判为 T 形交接");
        var revision = session.Revision;
        Assert(!session.TryMoveWallGrip(hostId, 1, 6000, 6000, out error)
            && error.Contains("范围") && session.Revision == revision,
            "T 形支墙窗越界时没有整笔回滚");
        Assert(session.Model.Walls.First(w => w.Id == branchId).Y1 == 0,
            "T 形失败回滚后支墙移动了");
        Assert(session.TryMoveWallGrip(hostId, 1, 6000, 1000, out error),
            "T 形宿主墙夹点移动失败：" + error);
        var branch = session.Model.Walls.First(w => w.Id == branchId);
        Assert(Math.Abs(branch.X1 - 3000) < 0.001 && Math.Abs(branch.Y1 - 500) < 0.001
            && session.Model.Walls.First(w => w.Id == upperId).Y1 == 0,
            "T 形支墙未保持宿主墙身比例位置，或影响其他楼层");
        Assert(session.Undo() && session.Model.Walls.First(w => w.Id == branchId).Y1 == 0,
            "撤销未恢复 T 形交接");
        Assert(session.Redo() && Math.Abs(session.Model.Walls.First(w => w.Id == branchId).Y1 - 500) < 0.001,
            "重做未恢复 T 形交接");
        Console.WriteLine("PASS T 形墙交接：支墙端点沿宿主墙身跟随，门窗越界整笔回滚，跨楼层隔离与撤销重做");
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
