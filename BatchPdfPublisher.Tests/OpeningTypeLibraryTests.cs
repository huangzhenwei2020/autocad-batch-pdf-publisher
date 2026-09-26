using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;

/// <summary>
/// 门窗类型库的护栏测试：中间格式往返、按编号查类型、把类型套到洞口（含门落地规则）。
/// 类型库是"复用已有门窗参数"的落点，坏了会让程序里放出来的门窗尺寸与 CAD 不一致。
/// </summary>
internal static class OpeningTypeLibraryTests
{
    private const double Tolerance = 0.5d;

    public static void Run()
    {
        RoundTripsWithoutLoss();
        AppliesTypeToOpening();
        FindsTypeByCodeIgnoringCaseAndSpaces();
        DemoLibraryIsUsable();
        Console.WriteLine("PASS 门窗类型库：往返 / 套用 / 按编号查找 / 演示库可用");
    }

    private static void RoundTripsWithoutLoss()
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoOpeningLibraryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var library = SampleModelFactory.CreateDemoOpeningLibrary();
            var path = Path.Combine(root, "openings.json");
            BuildingModelJson.SaveOpeningLibrary(path, library);
            var reloaded = BuildingModelJson.LoadOpeningLibrary(path);
            Assert(reloaded.Types.Count == library.Types.Count, "类型数量在往返后不一致");
            Assert(reloaded.Templates.Count == library.Templates.Count, "模板数量在往返后不一致");
            var first = reloaded.Types.First(t => t.Code == "C1518");
            Assert(Math.Abs(first.Width - 1500d) < Tolerance && Math.Abs(first.Height - 1800d) < Tolerance,
                "C1518 尺寸在往返后不一致");
            Assert(Math.Abs(first.Sill - 900d) < Tolerance, "C1518 窗台高在往返后不一致");
            Assert(first.DivisionPreset == "双扇等分" && first.OpeningMode == "双向推拉",
                "C1518 的立面做法在往返后丢失");
            Assert(first.AtlasName == "图集 12J4-1", "C1518 的图集名在往返后丢失");
            Console.WriteLine("   往返：" + new FileInfo(path).Length + " 字节，类型 " + reloaded.Types.Count
                + " 个、模板 " + reloaded.Templates.Count + " 个");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void AppliesTypeToOpening()
    {
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        var window = PlanEditing.CreateOpening("窗", "w1", 1000d);
        PlanEditing.ApplyType(window, library.Types.First(t => t.Code == "C1518"));
        Assert(window.Code == "C1518", "编号没套上：" + window.Code);
        Assert(Math.Abs(window.Width - 1500d) < Tolerance && Math.Abs(window.Height - 1800d) < Tolerance,
            "尺寸没套上");
        Assert(Math.Abs(window.Sill - 900d) < Tolerance, "窗台高没套上：" + window.Sill);

        // 门一律落地，即使类型里写了窗台高
        var door = PlanEditing.CreateOpening("门", "w1", 1000d);
        PlanEditing.ApplyType(door, library.Types.First(t => t.Code == "M0921"));
        Assert(door.Code == "M0921" && door.Kind == "门", "门类型没套上");
        Assert(Math.Abs(door.Sill) < Tolerance, "门应当落地（窗台 0），实际 " + door.Sill);

        // 门联窗按"门"处理（编号/类型里含"门"）
        var combined = PlanEditing.CreateOpening("窗", "w1", 1000d);
        PlanEditing.ApplyType(combined, new OpeningTypeModel { Code = "MLC1524", Kind = "门联窗", Width = 1500d, Height = 2400d, Sill = 300d });
        Assert(Math.Abs(combined.Sill) < Tolerance, "门联窗也应落地，实际 " + combined.Sill);

        Console.WriteLine("   套用：C1518 → 1500×1800@900；M0921 与门联窗 落地");
    }

    private static void FindsTypeByCodeIgnoringCaseAndSpaces()
    {
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        Assert(PlanEditing.FindType(library, " c1518 ") != null, "带空格与小写的编号应能找到");
        Assert(PlanEditing.FindType(library, "C1518").Width > 0d, "按编号找到的类型应带尺寸");
        Assert(PlanEditing.FindType(library, "不存在的编号") == null, "不存在的编号应返回空");
        Assert(PlanEditing.FindType(null, "C1518") == null, "空库应返回空");
        Console.WriteLine("   查找：忽略大小写与空格；不存在的编号返回空");
    }

    private static void DemoLibraryIsUsable()
    {
        var library = SampleModelFactory.CreateDemoOpeningLibrary();
        Assert(library.Types.Count >= 4, "演示库类型太少：" + library.Types.Count);
        Assert(library.Types.Any(t => t.Kind == "门" && t.Sill < Tolerance), "演示库里没有落地门");
        Assert(library.Types.Any(t => t.Kind == "窗" && t.Sill > 0d), "演示库里没有带窗台的窗");
        Assert(library.Types.All(t => t.Width > 0d && t.Height > 0d), "演示库里存在无效尺寸");
        Assert(library.Types.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() == library.Types.Count,
            "演示库里编号重复");
        Console.WriteLine("   演示库：" + string.Join("、", library.Types.Select(t => t.Code).ToArray())
            + "；模板 " + library.Templates.Count + " 个");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
