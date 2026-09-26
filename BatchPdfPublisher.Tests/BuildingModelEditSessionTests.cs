using System;
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
        Console.WriteLine("PASS 跨平台墙窗编辑事务：校验失败不提交，撤销/重做恢复参数与构件 ID");
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
