using System;
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
        Console.WriteLine("PASS 三维体量构件身份：样例墙/门窗/板/柱可追溯，参数修改后 ID 稳定");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
