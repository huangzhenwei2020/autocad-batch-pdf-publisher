using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 生成 AutoCAD 局部自定义文件（CUIx），让"万落建筑工具"以下拉菜单形式出现在经典菜单栏上。
    ///
    /// 为什么不用 MNU：Autodesk 官方文档已注明传统菜单文件 (MNS/MNU/CUI) 被
    /// "基于 XML 的 CUIx 文件"取代（MENU 命令标注为"旧式"）。实测 AutoCAD 2022 上
    /// -MENULOAD 加载 .mnu 不再生成 .mnc，菜单栏上也挂不出来。
    ///
    /// 本类不引用任何 AutoCAD 类型，便于离线断言。结构完全照抄本机
    /// %APPDATA%\Autodesk\AutoCAD 2024\R24.3\chs\Support\acetmain.cuix
    /// （Express Tools，正是一个往菜单栏加下拉菜单的第三方局部文件）：
    ///
    ///   [Content_Types].xml      OPC 包装（UTF-8 带 BOM）
    ///   _rels/.rels              声明 Header.cui / MenuGroup.cui / PopMenuRoot.cui
    ///   Header.cui               文件版本头（UTF-8 无 BOM）
    ///   MenuGroup.cui            宏定义 MenuMacro（UID 即被 MacroRef 引用的 ID）
    ///   PopMenuRoot.cui          下拉菜单树（Alias=POP16 → menucmd "P16=+BPP.POP16"）
    ///   Menu_Package_Info.xml    部件清单（UTF-8 带 BOM）
    /// </summary>
    public static class CuixPackageBuilder
    {
        public const string MenuGroupName = "BPP";
        public const string MenuGroupDisplayName = "万落建筑工具";
        /// <summary>弹菜单别名。menucmd "P16=+BPP.POP16" 依赖它，不能随意改。</summary>
        public const string PopupAlias = "POP16";
        public const string PackageFileName = "BPP_万落建筑工具.cuix";

        private const string TopMenuUid = "ID_BPP_MENU";

        /// <summary>菜单栏里分组出现的先后；未列出的分组排在"系统设置"之前。</summary>
        private static readonly string[] GroupOrder =
        {
            "图纸与发布", "图块与属性", "建筑工具", "制图与标注"
        };

        private static readonly string[] PartOrder =
        {
            "/Header.cui", "/MenuGroup.cui", "/PopMenuRoot.cui"
        };

        public static IDictionary<string, byte[]> BuildParts(
            IEnumerable<FeatureDefinition> features,
            IDictionary<string, string> shortcuts,
            DateTime stampUtc)
        {
            var groups = (features ?? Enumerable.Empty<FeatureDefinition>())
                .Where(x => x != null)
                .GroupBy(x => x.Group ?? string.Empty)
                .OrderBy(g => GroupRank(g.Key))
                .ThenBy(g => g.Key, StringComparer.CurrentCulture)
                .Select(g => g.ToList())
                .Where(items => items.Count > 0)
                .ToList();

            var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            parts["[Content_Types].xml"] = Utf8(ContentTypes, true);
            parts["_rels/.rels"] = Utf8(Relationships, true);
            parts["Header.cui"] = Utf8(Header, false);
            parts["Menu_Package_Info.xml"] = Utf8(PackageInfo(stampUtc), true);
            parts["MenuGroup.cui"] = Utf8(BuildMenuGroup(groups), false);
            parts["PopMenuRoot.cui"] = Utf8(BuildPopMenuRoot(groups, shortcuts), false);
            return parts;
        }

        /// <summary>
        /// 生成"加载局部菜单并把弹菜单挂上菜单栏"的 AutoLISP 表达式。
        ///
        /// 两个要点，都是踩过的坑：
        /// 1. **先卸载同名菜单组再加载**。AutoCAD 对已加载的菜单组再执行 -MENULOAD
        ///    是空操作，会继续用上一次的定义——菜单改了却在 CAD 里看不到最新样子，
        ///    就是这个原因。用 (menugroup "BPP") 判断是否已加载，避免无谓的卸载报错。
        /// 2. **整段包成一个形式**，路径与组名都作为 (command ...) 的参数，绝不按行裸发。
        ///    按行发的时候，一旦 -MENULOAD 没按预期消费掉这些输入，残留的 "BPP" 就会
        ///    落到命令行上被当成命令执行——BPP 正是打开批量打印面板的命令。
        /// </summary>
        public static string BuildLoadExpression(string path)
        {
            var escaped = (path ?? string.Empty).Replace("\\", "/").Replace("\"", "\\\"");
            return "(progn "
                + "(if (menugroup \"" + MenuGroupName + "\") (command \"_.-MENUUNLOAD\" \"" + MenuGroupName + "\")) "
                + "(command \"_.-MENULOAD\" \"" + escaped + "\" \"" + MenuGroupName + "\") "
                + "(menucmd \"P16=+" + MenuGroupName + "." + PopupAlias + "\") "
                + "(princ))";
        }

        /// <summary>把各部分写成一个 CUIx（本质是 ZIP）。</summary>
        public static void WritePackage(string path, IDictionary<string, byte[]> parts)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var part in parts)
                {
                    var entry = archive.CreateEntry(part.Key, CompressionLevel.Optimal);
                    using (var target = entry.Open()) target.Write(part.Value, 0, part.Value.Length);
                }
            }
        }

        public static int GroupRank(string group)
        {
            var name = group ?? string.Empty;
            var index = Array.IndexOf(GroupOrder, name);
            if (index >= 0) return index;
            if (name.StartsWith("图层工具", StringComparison.Ordinal)) return GroupOrder.Length;
            if (string.Equals(name, "系统设置", StringComparison.Ordinal)) return GroupOrder.Length + 1;
            return GroupOrder.Length + 2;
        }

        /// <summary>XML 文本与属性值转义。功能名/快捷键是用户可改的，必须转义。</summary>
        public static string Xml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>把功能 id 变成可安全用作 XML 属性值的 UID 片段。</summary>
        public static string SafeId(string value)
        {
            var builder = new StringBuilder();
            foreach (var ch in value ?? string.Empty)
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            var text = builder.ToString().Trim('_');
            return text.Length == 0 ? "ITEM" : text;
        }

        private static byte[] Utf8(string text, bool bom)
        {
            var encoding = new UTF8Encoding(bom);
            return encoding.GetBytes(text);
        }

        private static string BuildMenuGroup(IList<List<FeatureDefinition>> groups)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\"?>\r\n");
            builder.Append("<MenuGroup xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" Name=\"")
                .Append(Xml(MenuGroupName)).Append("\" DisplayName=\"").Append(Xml(MenuGroupDisplayName)).Append("\">\r\n");
            builder.Append("  <MacroGroup Name=\"").Append(Xml(MenuGroupName)).Append("Macros\" Citizen=\"A\">\r\n");
            foreach (var feature in groups.SelectMany(g => g))
            {
                builder.Append("    <MenuMacro UID=\"").Append(MacroUid(feature)).Append("\">\r\n");
                builder.Append("      <Macro type=\"Any\">\r\n");
                builder.Append("        <Revision MajorVersion=\"16\" MinorVersion=\"2\" UserVersion=\"0\" />\r\n");
                builder.Append("        <ModifiedRev MajorVersion=\"18\" MinorVersion=\"0\" UserVersion=\"0\" />\r\n");
                builder.Append("        <Name xlate=\"true\" UID=\"XLS_").Append(MacroUid(feature)).Append("\">")
                    .Append(Xml(feature.Name)).Append("</Name>\r\n");
                builder.Append("        <Command>").Append(Xml(Macro(feature))).Append(" </Command>\r\n");
                builder.Append("        <HelpString xlate=\"true\" UID=\"XLS_H_").Append(MacroUid(feature)).Append("\">")
                    .Append(Xml(feature.Description)).Append("</HelpString>\r\n");
                builder.Append("        <CLICommand xlate=\"true\" UID=\"XLS_C_").Append(MacroUid(feature)).Append("\">")
                    .Append(Xml(feature.Command)).Append("</CLICommand>\r\n");
                builder.Append("      </Macro>\r\n");
                builder.Append("    </MenuMacro>\r\n");
            }
            builder.Append("  </MacroGroup>\r\n");
            builder.Append("</MenuGroup>\r\n");
            return builder.ToString();
        }

        private static string BuildPopMenuRoot(IList<List<FeatureDefinition>> groups, IDictionary<string, string> shortcuts)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\"?>\r\n");
            builder.Append("<PopMenuRoot xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n");

            // 顶层弹菜单：它的 Alias 就是 menucmd "P16=+BPP.POP16" 里的 POP16。
            builder.Append("  <PopMenu hasDiesel=\"false\" UID=\"").Append(TopMenuUid).Append("\">\r\n");
            builder.Append("    <ModifiedRev MajorVersion=\"18\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
            builder.Append("    <Alias>").Append(Xml(PopupAlias)).Append("</Alias>\r\n");
            builder.Append("    <Name xlate=\"true\" UID=\"XLS_MENU\">").Append(Xml(MenuGroupDisplayName)).Append("</Name>\r\n");

            var itemIndex = 0;
            var subIndex = 0;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var items = groups[groupIndex];
                if (groupIndex > 0) AppendSeparator(builder, ref itemIndex);
                if (items.Count == 1)
                {
                    AppendItem(builder, items[0], Label(items[0], shortcuts), ref itemIndex);
                    continue;
                }
                subIndex++;
                builder.Append("    <PopMenuRef pUID=\"").Append(SubMenuUid(subIndex)).Append("\" UID=\"PMR_BPP_")
                    .Append(subIndex.ToString("D3")).Append("\">\r\n");
                builder.Append("      <ModifiedRev MajorVersion=\"18\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
                builder.Append("    </PopMenuRef>\r\n");
            }
            builder.Append("  </PopMenu>\r\n");

            // 各分组的级联子菜单。PopMenuItem 的 UID 在整个部件里必须唯一，
            // 所以继续沿用同一个计数器，而不是每个子菜单各自从 1 数起。
            subIndex = 0;
            foreach (var items in groups)
            {
                if (items.Count == 1) continue;
                subIndex++;
                builder.Append("  <PopMenu hasDiesel=\"false\" UID=\"").Append(SubMenuUid(subIndex)).Append("\">\r\n");
                builder.Append("    <ModifiedRev MajorVersion=\"18\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
                builder.Append("    <Name xlate=\"true\" UID=\"XLS_GRP_").Append(subIndex.ToString("D3")).Append("\">")
                    .Append(Xml(items[0].Group)).Append("</Name>\r\n");
                for (var i = 0; i < items.Count; i++)
                {
                    AppendItem(builder, items[i], Label(items[i], shortcuts), ref itemIndex);
                }
                builder.Append("  </PopMenu>\r\n");
            }

            builder.Append("</PopMenuRoot>\r\n");
            return builder.ToString();
        }

        private static void AppendSeparator(StringBuilder builder, ref int index)
        {
            index++;
            builder.Append("    <PopMenuItem IsSeparator=\"true\" hasDiesel=\"false\" UID=\"PMI_BPP_S")
                .Append(index.ToString("D3")).Append("\">\r\n");
            builder.Append("      <ModifiedRev MajorVersion=\"18\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
            builder.Append("    </PopMenuItem>\r\n");
        }

        private static void AppendItem(StringBuilder builder, FeatureDefinition feature, string text, ref int index)
        {
            index++;
            builder.Append("    <PopMenuItem IsSeparator=\"false\" hasDiesel=\"false\" UID=\"PMI_BPP_")
                .Append(index.ToString("D3")).Append("\">\r\n");
            builder.Append("      <ModifiedRev MajorVersion=\"18\" MinorVersion=\"1\" UserVersion=\"0\" />\r\n");
            builder.Append("      <NameRef UID=\"XLS_N_").Append(MacroUid(feature)).Append("\" xlate=\"true\">")
                .Append(Xml(text)).Append("</NameRef>\r\n");
            builder.Append("      <MenuItem>\r\n");
            builder.Append("        <MacroRef MenuMacroID=\"").Append(MacroUid(feature)).Append("\" />\r\n");
            builder.Append("      </MenuItem>\r\n");
            builder.Append("    </PopMenuItem>\r\n");
        }

        private static string SubMenuUid(int index) { return "ID_BPP_GRP_" + index.ToString("D3"); }

        /// <summary>宏的 UID；PopMenuRoot 里 MacroRef MenuMacroID 引用的就是这个值。</summary>
        public static string MacroUid(FeatureDefinition feature)
        {
            return "ID_BPP_" + SafeId(feature == null ? null : feature.Id);
        }

        /// <summary>
        /// 菜单项文字：四字简称 + 当前快捷键，例如「批量打印（BPP）」。
        ///
        /// 与功能区按钮用同一套四字简称，两个入口看起来是一套东西；快捷键写在菜单里
        /// 是因为菜单栏宽度不受限，而功能区按钮一旦带上快捷键就会宽出四成、把面板撑爆。
        /// </summary>
        public static string Label(FeatureDefinition feature, IDictionary<string, string> shortcuts)
        {
            if (feature == null) return string.Empty;
            string shortcut = null;
            if (shortcuts != null) shortcuts.TryGetValue(feature.Id, out shortcut);
            if (string.IsNullOrWhiteSpace(shortcut)) shortcut = feature.DefaultShortcut;
            var name = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
            return name + "（" + (shortcut ?? string.Empty) + "）";
        }

        /// <summary>
        /// 菜单宏。图层直达命令带参数（先写目标图层环境变量再调用统一的 GL），
        /// 必须走登记好的 AutoLISP 表达式，直接发内部命令会退化成"再选一次图层"。
        /// </summary>
        public static string Macro(FeatureDefinition feature)
        {
            if (feature == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(feature.LispInvocation)) return "^C^C" + feature.LispInvocation;
            return "^C^C_" + feature.Command;
        }

        private static string PackageInfo(DateTime stampUtc)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<MenuPackageParts>\r\n");
            foreach (var part in PartOrder)
                builder.Append("  <PartData PartData_Name=\"").Append(part).Append("\" PartData_Modified=\"")
                    .Append(stampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK")).Append("\" />\r\n");
            builder.Append("  <PartData PartData_Name=\"/VirtualMNRRoot\" PartData_Modified=\"")
                .Append(stampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK")).Append("\" />\r\n");
            builder.Append("  <PartData PartData_Name=\"/Menu_Package_Info.xml\" PartData_Modified=\"")
                .Append(stampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK")).Append("\" />\r\n");
            builder.Append("</MenuPackageParts>\r\n");
            return builder.ToString();
        }

        private const string ContentTypes =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"cui\" ContentType=\"text/xml\" />" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\" />" +
            "<Default Extension=\"xml\" ContentType=\"text/xml\" /></Types>";

        private const string Relationships =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Type=\"CUI\" Target=\"/Header.cui\" Id=\"Rheader0000000001\" />" +
            "<Relationship Type=\"CUI\" Target=\"/MenuGroup.cui\" Id=\"Rmenugroup000001\" />" +
            "<Relationship Type=\"CUI\" Target=\"/PopMenuRoot.cui\" Id=\"Rpopmenu00000001\" /></Relationships>";

        private const string Header =
            "<?xml version=\"1.0\"?>\r\n" +
            "<CustSection xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n" +
            "  <FileVersion MajorVersion=\"0\" MinorVersion=\"6\" IncrementalVersion=\"1\" UserVersion=\"0\" />\r\n" +
            "  <Header>\r\n" +
            "    <CommonConfiguration>\r\n" +
            "      <CommonItems>\r\n" +
            "        <ModifiedRev MajorVersion=\"18\" MinorVersion=\"0\" UserVersion=\"0\" />\r\n" +
            "      </CommonItems>\r\n" +
            "    </CommonConfiguration>\r\n" +
            "  </Header>\r\n" +
            "</CustSection>\r\n";
    }
}
