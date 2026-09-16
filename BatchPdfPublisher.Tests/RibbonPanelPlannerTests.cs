using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Services;

// 功能区面板规划的固定断言。规划错了在 CAD 里只表现为"面板很宽"或者"功能不见了"，
// 很难定位，所以把上限和"不丢功能"这两条锁死。
internal static class RibbonPanelPlannerTests
{
    private static FeatureDefinition F(string id, string group)
    {
        return new FeatureDefinition(id, id, id.ToUpperInvariant(), id.ToUpperInvariant(), group, "panel", "d", null);
    }

    /// <summary>与 FeatureRegistry 当前分组一致的样本。</summary>
    private static List<FeatureDefinition> Sample()
    {
        return new List<FeatureDefinition>
        {
            F("publisher", "图纸与发布"), F("frame", "图纸与发布"), F("catalog", "图纸与发布"),
            F("attribute_batch", "图块与属性"), F("attribute_definition", "图块与属性"),
            F("architecture_spec", "建筑工具"), F("cad_table_xlsx", "建筑工具"), F("stair_detail", "建筑工具"),
            F("door_window", "建筑工具"), F("detail_layout", "建筑工具"), F("line_vision", "建筑工具"),
            F("room_rename", "建筑工具"),
            F("drafting_standard", "制图与标注"), F("drawing_scale", "制图与标注"),
            F("layer_assignment", "图层工具"),
            F("shortcut_settings", "系统设置"), F("menubar", "系统设置"), F("cloud_sync", "系统设置")
        };
    }

    public static void RunAll()
    {
        MergesGroupsIntoFewPanels();
        EveryPanelStaysInsideTheRowLimit();
        NoFeatureIsLost();
        UnknownGroupsGetTheirOwnPanel();
        OversizedGroupsAreSplit();
    }

    private static void MergesGroupsIntoFewPanels()
    {
        var panels = RibbonPanelPlanner.Plan(Sample());
        // 按钮带快捷键后每个更宽，所以每行 2 个、每面板上限 6 个。
        // 18 个功能原来会摊成 6 个一行高的面板；现在合到 5 个、每个最多 2 列。
        Assert(panels.Count == 5, "expected 5 panels but got " + panels.Count + ": " +
            string.Join(" / ", panels.Select(p => p.Title + "=" + p.Features.Count)));
        Assert(panels[0].Title == "图纸与发布" && panels[0].Features.Count == 5, "first panel should merge 图纸与发布 + 图块与属性");
        Assert(panels[1].Title == "建筑工具" && panels[1].Features.Count == 4, "建筑工具 should be split evenly, first half");
        Assert(panels[2].Title == "建筑工具 (2)" && panels[2].Features.Count == 3, "建筑工具 should be split evenly, second half");
        Assert(panels[3].Title == "制图与图层" && panels[3].Features.Count == 3, "fourth panel should merge 制图与标注 + 图层工具");
        Assert(panels[4].Title == "系统设置" && panels[4].Features.Count == 3, "last panel should hold 系统设置");
        Console.WriteLine("PASS RibbonMergesGroupsIntoFewPanels");
    }

    private static void EveryPanelStaysInsideTheRowLimit()
    {
        foreach (var panels in new[] { RibbonPanelPlanner.Plan(Sample()), RibbonPanelPlanner.Plan(Many()) })
            foreach (var panel in panels)
                Assert(panel.Features.Count <= RibbonPanelPlanner.MaxItemsPerPanel,
                    "panel " + panel.Title + " has " + panel.Features.Count + " items, over the " + RibbonPanelPlanner.MaxItemsPerPanel + " limit");
        Console.WriteLine("PASS RibbonPanelsStayInsideRowLimit");
    }

    private static void NoFeatureIsLost()
    {
        var sample = Sample();
        var planned = RibbonPanelPlanner.Plan(sample).SelectMany(p => p.Features).ToList();
        Assert(planned.Count == sample.Count, "planner dropped or duplicated features: " + planned.Count + " vs " + sample.Count);
        foreach (var feature in sample)
            Assert(planned.Count(x => ReferenceEquals(x, feature)) == 1, "feature " + feature.Id + " should appear exactly once");
        Console.WriteLine("PASS RibbonPlannerKeepsEveryFeature");
    }

    private static void UnknownGroupsGetTheirOwnPanel()
    {
        var features = new List<FeatureDefinition> { F("a", "图纸与发布"), F("b", "全新分组") };
        var panels = RibbonPanelPlanner.Plan(features);
        Assert(panels.Any(p => p.Title == "全新分组" && p.Features.Count == 1),
            "a group missing from the plan should still get its own panel");
        Console.WriteLine("PASS RibbonUnknownGroupsGetOwnPanel");
    }

    private static void OversizedGroupsAreSplit()
    {
        // 一个分组超过面板上限时必须拆成多个面板，否则又会变成一条长龙；
        // 而且要**均分**，不能前面塞满、最后一个只剩一两项。
        var panels = RibbonPanelPlanner.Plan(Many());
        var building = panels.Where(p => p.Title.StartsWith("建筑工具", StringComparison.Ordinal)).ToList();
        Assert(building.Count >= 2, "an oversized group should be split into several panels");
        Assert(building[1].Title == "建筑工具 (2)", "follow-up panels should be numbered, got " + building[1].Title);
        var largest = building.Max(p => p.Features.Count);
        var smallest = building.Min(p => p.Features.Count);
        Assert(largest - smallest <= 1, "拆分应当均分，实际各面板为 " + string.Join("/", building.Select(p => p.Features.Count)));
        Console.WriteLine("PASS RibbonOversizedGroupsAreSplit");
    }

    private static List<FeatureDefinition> Many()
    {
        var features = Sample();
        for (var i = 0; i < 8; i++) features.Add(F("extra_" + i, "建筑工具"));
        return features;
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) return;
        throw new InvalidOperationException("RibbonPanelPlannerTests failed: " + message);
    }
}
