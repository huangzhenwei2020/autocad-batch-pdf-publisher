using System;
using BatchPdfPublisher.Services;

// 功能区图标规则的固定断言。图标"看不清"是装上去才会发现的问题，
// 所以配色覆盖与字形回退这两条离线锁住。
internal static class RibbonIconThemeTests
{
    public static void RunAll()
    {
        EveryPanelGroupHasItsOwnColor();
        LayerGroupsShareTheLayerColor();
        UnknownGroupFallsBackWithoutCrashing();
        GlyphIsExactlyOneCharacter();
    }

    private static void EveryPanelGroupHasItsOwnColor()
    {
        var groups = new[] { "图纸与发布", "图块与属性", "建筑工具", "制图与标注", "系统设置" };
        var colors = new System.Collections.Generic.List<string>();
        foreach (var group in groups)
        {
            var hex = RibbonIconTheme.ColorHex(group);
            Assert(IsHexColor(hex), group + " 应给出合法的十六进制颜色，实际为 " + hex);
            colors.Add(hex);
        }
        for (var i = 0; i < colors.Count; i++)
            for (var j = i + 1; j < colors.Count; j++)
                Assert(!string.Equals(colors[i], colors[j], StringComparison.OrdinalIgnoreCase),
                    groups[i] + " 与 " + groups[j] + " 用了同一个颜色，颜色就分不出大类了");
        Console.WriteLine("PASS IconThemeGivesEachGroupItsOwnColor");
    }

    private static void LayerGroupsShareTheLayerColor()
    {
        // 图层直达命令的分组是「图层工具-墙体」这类派生名，应当和「图层工具」同色。
        var baseColor = RibbonIconTheme.ColorHex("图层工具");
        Assert(RibbonIconTheme.ColorHex("图层工具-墙体") == baseColor, "派生图层分组应与图层工具同色");
        Assert(RibbonIconTheme.ColorHex("图层工具-门窗") == baseColor, "派生图层分组应与图层工具同色");
        Assert(baseColor != RibbonIconTheme.ColorHex("建筑工具"), "图层工具不应与建筑工具同色");
        Console.WriteLine("PASS IconThemeKeepsLayerGroupsTogether");
    }

    private static void UnknownGroupFallsBackWithoutCrashing()
    {
        foreach (var group in new[] { null, "", "   ", "以后新增的分组" })
        {
            var hex = RibbonIconTheme.ColorHex(group);
            Assert(IsHexColor(hex), "未知分组也要有合法颜色，实际为 " + hex);
        }
        Console.WriteLine("PASS IconThemeFallsBackForUnknownGroup");
    }

    private static void GlyphIsExactlyOneCharacter()
    {
        Assert(RibbonIconTheme.GlyphFor("梯") == "梯", "单字图标应原样返回");
        Assert(RibbonIconTheme.GlyphFor(" 窗 ") == "窗", "图标字应当去掉空白");
        Assert(RibbonIconTheme.GlyphFor("目录") == "目", "多字时只取第一个字");
        var fallback = RibbonIconTheme.GlyphFor(null);
        Assert(fallback.Length > 0, "图标字为空时要有回退，不能画出空白图标");
        Assert(RibbonIconTheme.GlyphFor("") == fallback, "空图标字应当走同一个回退");
        Console.WriteLine("PASS IconThemeAlwaysYieldsOneGlyph");
    }

    private static bool IsHexColor(string value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Length == 7 && value[0] == '#'
            && System.Text.RegularExpressions.Regex.IsMatch(value.Substring(1), "^[0-9A-Fa-f]{6}$");
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) return;
        throw new InvalidOperationException("RibbonIconThemeTests failed: " + message);
    }
}
