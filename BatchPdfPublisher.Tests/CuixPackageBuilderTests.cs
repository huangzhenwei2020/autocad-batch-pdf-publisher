using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BatchPdfPublisher.Services;

// CUIx 局部菜单生成的固定断言。菜单结构写错时用户只会看到"菜单在但点不动"或者
// "插件菜单不出现"，在 CAD 里排查代价很高，所以这层规则必须离线锁住。
internal static class CuixPackageBuilderTests
{
    private static readonly FeatureDefinition[] Features =
    {
        new FeatureDefinition("publisher", "批量 PDF 面板", "BPP", "BPP", "图纸与发布", "打", "打开批量发布面板。", null, null, "批量打印"),
        new FeatureDefinition("frame", "创建图框", "TKK", "TKK", "图纸与发布", "框", "创建标准图框。", null, null, "创建图框"),
        new FeatureDefinition("catalog", "插入目录", "ML1", "ML1", "图纸与发布", "目", "生成目录表。", null, null, "插入目录"),
        new FeatureDefinition("stair_detail", "楼梯大样", "WLLTDY", "LTDY", "建筑工具", "梯", "打开楼梯编辑器。", "LTDY", null, "楼梯大样"),
        new FeatureDefinition("door_window", "门窗立面", "MCLM", "MCLM", "建筑工具", "窗", "批量设置门窗立面。", null, null, "门窗立面"),
        new FeatureDefinition("menubar", "菜单栏开关", "WLMENUBAR", "CDL", "系统设置", "菜", "显示或隐藏菜单栏。", null, null, "菜单开关"),
        // 图层直达命令：带 AutoLISP 表达式，宏必须用它而不是裸命令。
        new FeatureDefinition("layer_wall", "归层 → WL-墙面", "GL", "QM", "图层工具-墙体", "层", "把所选对象归到墙面图层。", null,
            LayerInvocation("WL-墙面"), "墙面归层"),
        new FeatureDefinition("layer_floor", "归层 → WL-地面", "GL", "DM", "图层工具-墙体", "层", "把所选对象归到地面图层。", null,
            LayerInvocation("WL-地面"), "地面归层")
    };

    /// <summary>
    /// 与 FeatureRegistry.LayerLispInvocation 保持同形：整段一个 (progn ...)，
    /// 主通道是 WLSETLAYER（LispFunction 同进程传值），setenv 只是兼容通道。
    /// </summary>
    private static string LayerInvocation(string layerName)
    {
        return "(progn (if wlsetlayer (wlsetlayer \"" + layerName + "\")) (setenv \"WANLUO_TARGET_LAYER\" \""
            + layerName + "\") (command \"GL\"))";
    }

    public static void RunAll()
    {
        BuildsExpectedPackageParts();
        PackageIsAValidZipWithUtf8Parts();
        TopPopupUsesTheAliasTheMountCommandNeeds();
        GroupsBecomeCascadingSubPupups();
        SingleItemGroupIsFlattened();
        LayerEntriesUseLispInvocation();
        MacrosAreReferencedByTheirOwnUid();
        LabelsAndTextAreXmlEscaped();
        LinksToExistingPmpLibrarySurvive();
        EveryPartIsWellFormedXml();
        UidsAreUniqueInsideThePackage();
        LoadExpressionUnloadsFirstAndStaysOneForm();
    }

    private static IDictionary<string, byte[]> Parts(Dictionary<string, string> shortcuts = null)
    {
        return CuixPackageBuilder.BuildParts(Features, shortcuts ?? new Dictionary<string, string>(),
            new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
    }

    private static string Text(IDictionary<string, byte[]> parts, string name)
    {
        var bytes = parts[name];
        var offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static void BuildsExpectedPackageParts()
    {
        var parts = Parts();
        foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", "Header.cui", "MenuGroup.cui", "PopMenuRoot.cui", "Menu_Package_Info.xml" })
            Assert(parts.ContainsKey(required), "package should contain part " + required);
        Console.WriteLine("PASS CuixPackageHasAllParts");
    }

    private static void PackageIsAValidZipWithUtf8Parts()
    {
        var path = Path.Combine(Path.GetTempPath(), "wl-cuix-test-" + Guid.NewGuid().ToString("N") + ".cuix");
        try
        {
            CuixPackageBuilder.WritePackage(path, Parts());
            using (var archive = ZipFile.OpenRead(path))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                Assert(names.Contains("MenuGroup.cui") && names.Contains("PopMenuRoot.cui"),
                    "zip should contain the menu parts");
                var entry = archive.GetEntry("MenuGroup.cui");
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    Assert(text.Contains("批量 PDF 面板"), "MenuGroup.cui should keep Chinese text intact");
                }
            }
            // 中文必须能正确往返：CUIx 的 .cui 部件是 UTF-8。
            var group = Text(Parts(), "MenuGroup.cui");
            Assert(group.Contains("批量 PDF 面板") && group.Contains("\\\"WL-墙面\\\"") == false,
                "MenuGroup.cui text should be readable UTF-8");
        }
        finally { try { File.Delete(path); } catch { } }
        Console.WriteLine("PASS CuixPackageBuildsReadableZip");
    }

    private static void TopPopupUsesTheAliasTheMountCommandNeeds()
    {
        var text = Text(Parts(), "PopMenuRoot.cui");
        // menucmd "P16=+BPP.POP16" 依赖这个 Alias，改名会让菜单挂不上菜单栏。
        Assert(text.Contains("<Alias>POP16</Alias>"), "top popup alias must be POP16");
        Assert(text.Contains("万落建筑工具"), "top popup name missing");
        Assert(text.Contains("<PopMenuRoot"), "PopMenuRoot element missing");
        Console.WriteLine("PASS CuixTopPopupCarriesP16Alias");
    }

    private static void GroupsBecomeCascadingSubPupups()
    {
        var text = Text(Parts(), "PopMenuRoot.cui");
        var refs = CountOccurrences(text, "<PopMenuRef ");
        var definitions = CountOccurrences(text, "<PopMenu hasDiesel");
        // 顶层 1 个 + 分组各 1 个；引用数应与子菜单数一致。
        Assert(definitions == refs + 1, "each sub menu needs exactly one PopMenuRef, got " + definitions + " menus / " + refs + " refs");
        Assert(text.Contains(">图纸与发布</Name>"), "group submenu should carry its title");
        Console.WriteLine("PASS CuixGroupsBecomeCascadingSubMenus");
    }

    private static void SingleItemGroupIsFlattened()
    {
        var text = Text(Parts(), "PopMenuRoot.cui");
        // 系统设置 只有一个成员，不应该为它单独开子菜单。
        Assert(!text.Contains(">系统设置</Name>"), "single-item group should not open a submenu");
        Assert(text.Contains("菜单开关（CDL）"), "single-item group member should appear flat");
        Console.WriteLine("PASS CuixFlattensSingleItemGroup");
    }

    private static void LayerEntriesUseLispInvocation()
    {
        var text = Text(Parts(), "MenuGroup.cui");
        // 主通道必须是 LispFunction：AutoLISP 直接调 .NET，同进程传值。
        // 早先只用 (setenv ...)：AutoCAD 不保证把它同步进 Windows 进程环境块，
        // 而 .NET 侧读的就是进程环境块——表现就是"按了图层快捷键却弹出 GL 对话框"。
        Assert(text.Contains("(if wlsetlayer (wlsetlayer &quot;WL-墙面&quot;))"),
            "layer command must pass the target layer through the WLSETLAYER LispFunction");
        Assert(text.Contains("(setenv &quot;WANLUO_TARGET_LAYER&quot; &quot;WL-墙面&quot;)"),
            "the setenv channel should stay as a fallback");
        // 整段必须包成一个形式：菜单宏里多个散装 LISP 形式会被命令行拆开执行。
        Assert(text.Contains("^C^C(progn (if wlsetlayer (wlsetlayer &quot;WL-墙面&quot;)) (setenv &quot;WANLUO_TARGET_LAYER&quot; &quot;WL-墙面&quot;) (command &quot;GL&quot;))"),
            "layer command macro should be a single progn form");
        Assert(!text.Contains("^C^C_GL"), "layer command must not fall back to the bare GL command");
        Assert(text.Contains("^C^C_WLMENUBAR"), "plain feature should still use ^C^C_<command>");
        Console.WriteLine("PASS CuixLayerEntriesUseLispInvocation");
    }

    private static void MacrosAreReferencedByTheirOwnUid()
    {
        var group = Text(Parts(), "MenuGroup.cui");
        var pop = Text(Parts(), "PopMenuRoot.cui");
        // 每个 MacroRef 都必须能在 MenuGroup 里找到同名 MenuMacro，否则菜单项点了没反应。
        var refs = System.Text.RegularExpressions.Regex.Matches(pop, "<MacroRef MenuMacroID=\"([^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert(refs.Count > 0, "popup should reference at least one macro");
        foreach (var id in refs)
            Assert(group.Contains("<MenuMacro UID=\"" + id + "\">"), "MenuGroup.cui is missing a macro definition for " + id);
        Console.WriteLine("PASS CuixMacroRefsResolveToMacroDefinitions");
    }

    private static void LabelsAndTextAreXmlEscaped()
    {
        var shortcuts = new Dictionary<string, string> { { "publisher", "FABU" } };
        var pop = Text(Parts(shortcuts), "PopMenuRoot.cui");
        // 菜单和功能区用同一套四字简称，菜单上还要带快捷键。
        Assert(pop.Contains("批量打印（FABU）"), "menu label should use the four-character short name plus the user shortcut");
        Assert(!pop.Contains("批量 PDF 面板"), "menu label should not fall back to the long name when a short name exists");
        // 宏定义里保留完整名称，快捷键设置页和悬停提示读的就是它，信息不能丢。
        Assert(Text(Parts(), "MenuGroup.cui").Contains("批量 PDF 面板"), "macro name should keep the full feature name");

        Assert(CuixPackageBuilder.Xml("A&B<C>\"D\"") == "A&amp;B&lt;C&gt;&quot;D&quot;", "Xml() should escape");
        Assert(CuixPackageBuilder.SafeId("layer_wall-2") == "layer_wall_2", "SafeId should neutralise punctuation");
        Assert(CuixPackageBuilder.SafeId("") == "ITEM", "SafeId should fall back for empty ids");
        Console.WriteLine("PASS CuixLabelsCarryShortcutAndEscapeXml");
    }

    private static void LinksToExistingPmpLibrarySurvive()
    {
        // 菜单里挂的是命令行宏，不碰纸张库；这条断言只是保证生成器不会顺手改到别的部件。
        var parts = Parts();
        Assert(!parts.Keys.Any(k => k.IndexOf(".pmp", StringComparison.OrdinalIgnoreCase) >= 0)
            && !parts.Keys.Any(k => k.IndexOf(".pc3", StringComparison.OrdinalIgnoreCase) >= 0),
            "cuix package must not carry plotter configuration parts");
        Console.WriteLine("PASS CuixPackageLeavesPlotterConfigAlone");
    }

    private static void EveryPartIsWellFormedXml()
    {
        // AutoCAD 直接按 XML 解析这些部件；一个未转义字符就足以让整个局部菜单被拒。
        foreach (var part in Parts())
        {
            var text = Text(Parts(), part.Key);
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(text);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("CuixPackageBuilderTests failed: 部件 " + part.Key +
                    " 不是合法 XML：" + exception.Message);
            }
        }
        Console.WriteLine("PASS CuixPartsAreWellFormedXml");
    }

    private static void UidsAreUniqueInsideThePackage()
    {
        // Express Tools 的 PMI_/PMR_ 编号在整个部件里是全局唯一的。子菜单各自从 1
        // 数起会撞号，CAD 里表现为菜单项错位或点不动，很难查，所以这里锁死。
        foreach (var part in new[] { "PopMenuRoot.cui", "MenuGroup.cui" })
        {
            var text = Text(Parts(), part);
            // 只看 UID="..."（定义），不要误匹配 pUID="..."（引用）——同一编号在
            // “定义”和“引用”处各出现一次是正常的。
            var uids = System.Text.RegularExpressions.Regex.Matches(text, "(?<![A-Za-z])UID=\"([^\"]+)\"")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
            var duplicates = uids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert(duplicates.Count == 0, part + " 里有重复 UID：" + string.Join(", ", duplicates));
        }

        // PopMenuItem / PopMenuRef / PopMenu 的编号必须能对上：每条引用都要有定义。
        var pop = Text(Parts(), "PopMenuRoot.cui");
        var defined = System.Text.RegularExpressions.Regex.Matches(pop, "<PopMenu hasDiesel=\"false\" UID=\"([^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToHashSet();
        var referenced = System.Text.RegularExpressions.Regex.Matches(pop, "<PopMenuRef pUID=\"([^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
        foreach (var id in referenced)
            Assert(defined.Contains(id), "PopMenuRef 指向了不存在的子菜单：" + id);
        Console.WriteLine("PASS CuixUidsAreUniqueAndResolvable");
    }

    private static void LoadExpressionUnloadsFirstAndStaysOneForm()
    {
        var expression = CuixPackageBuilder.BuildLoadExpression(@"C:\临时 目录\BPP_万落建筑工具.cuix");

        // ① 必须先卸载同名菜单组：AutoCAD 对已加载的组再 -MENULOAD 是空操作，
        //    会继续用旧定义——菜单改了却在 CAD 里看不到最新样子就是这个原因。
        Assert(expression.Contains("(menugroup \"BPP\")"), "load expression should probe whether the group is already loaded");
        Assert(expression.Contains("-MENUUNLOAD"), "load expression should unload the existing menu group first");
        Assert(expression.IndexOf("-MENUUNLOAD", StringComparison.Ordinal) < expression.IndexOf("-MENULOAD", StringComparison.Ordinal),
            "unload must come before load");

        // ② 必须仍是单个 AutoLISP 形式、且不含换行。按行裸发时，一旦 -MENULOAD 没按
        //    预期消费掉输入，残留的 "BPP" 就会落到命令行上被当成命令执行（BPP 正是
        //    打开批量打印面板的命令）。
        Assert(expression.StartsWith("(progn ", StringComparison.Ordinal), "should be a single progn form");
        Assert(expression.EndsWith(")", StringComparison.Ordinal), "form should be closed");
        Assert(!expression.Contains("\n") && !expression.Contains("\r"), "must stay on one line so no token can leak to the command prompt");
        Assert(CountOccurrences(expression, "\"BPP\"") == 3, "the menu group name should only appear as a quoted argument");

        // ③ 反斜杠路径要换成正斜杠，否则 AutoLISP 字符串里的 \\ 会被当转义。
        Assert(!expression.Contains("\\临时"), "path backslashes should be normalised to forward slashes");
        Assert(expression.Contains("C:/临时 目录/"), "path should be kept with forward slashes");

        // ④ 挂载菜单栏也在这一个形式里，靠 (command ...) 的同步特性保证顺序。
        Assert(expression.Contains("P16=+BPP.POP16"), "the popup should be mounted in the same form");
        Console.WriteLine("PASS CuixLoadExpressionUnloadsFirstAndStaysOneForm");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0) { count++; index += token.Length; }
        return count;
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) return;
        throw new InvalidOperationException("CuixPackageBuilderTests failed: " + message);
    }
}
