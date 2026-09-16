using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 全部用户可见功能的唯一登记表。Ribbon、经典菜单和快捷键设置窗口都从这里读取，
    /// 新功能只要登记一次，就会自动出现在三个入口中。
    /// </summary>
    public static class FeatureRegistry
    {
        private static readonly FeatureDefinition[] FixedItems =
        {
            // 参数依次为：id、完整名称、内部命令、默认快捷键、分组、图标字、说明、外置命令、四字简称。
            // 图标字是功能区色块上那个白字；四字简称用于功能区按钮（网格排版要求等宽），
            // 菜单栏、悬停提示和快捷键设置页仍显示完整名称，信息不丢。
            F("publisher", "批量 PDF 面板", "BPP", "BPP", "图纸与发布", "打", "打开工程 DWG 管理、图框扫描和批量 PDF 发布面板。", null, "批量打印"),
            F("frame", "创建图框", "TKK", "TKK", "图纸与发布", "框", "创建标准图框，或选择已登记图框并按指定比例插入。", null, "创建图框"),
            F("catalog", "插入目录", "ML1", "ML1", "图纸与发布", "目", "根据当前工程图纸顺序生成目录表并插入 CAD。", null, "插入目录"),
            F("attribute_batch", "批量改属性", "BPPATTR", "SBB", "图块与属性", "属", "框选不同类型的属性图块，按坐标排序、批量递增并写入同一属性标记。", null, "批改属性"),
            F("attribute_definition", "属性定义编辑", "BPPATTDEF", "BPA", "图块与属性", "定", "拾取图块后修改图块名称、属性 TAG、默认内容、字体、字高、宽度和对齐方式。", null, "属性定义"),
            F("architecture_spec", "建筑设计说明", "WLJZSM", "JZSM", "建筑工具", "说", "打开万落建筑工具中的建筑设计说明助手。", "JZSM", "建筑说明"),
            F("cad_table_xlsx", "CAD表格编辑/Excel", "WLCAD2XLSX", "CE", "建筑工具", "表", "拾取线框、文字或现有 CAD 表格进行编辑，可导出 Excel 或重新插入 CAD。", "CE", "表格编辑"),
            F("stair_detail", "楼梯大样", "WLLTDY", "LTDY", "建筑工具", "梯", "打开楼梯构件编辑器，按楼层、梯段和构造参数一键生成楼梯大样。", "LTDY", "楼梯大样"),
            F("drafting_standard", "制图标准", "BZS", "BZS", "制图与标注", "标", "检查并补齐万落工具共用的图层、文字样式和标注样式。", null, "制图标准"),
            F("layer_assignment", "归层", "GL", "GL", "图层工具", "层", "把所选对象归到指定图层，可同时把颜色、线型、线宽设为随层，并处理块属性。", null, "对象归层"),
            F("drawing_scale", "比例管理", "BL1", "BL1", "制图与标注", "比", "把所选对象转换到指定图纸比例，并同步普通 CAD 与天正标注。", null, "比例管理"),
            F("door_window", "门窗立面", "MCLM", "MCLM", "建筑工具", "窗", "读取门窗表，校验编号和洞口尺寸，并批量设置门窗立面分格与开启参数。", null, "门窗立面"),
            F("detail_layout", "大样排版", "WLDYLAYOUT", "DYPB", "建筑工具", "排", "逐个框选大样并自动计算边界，在登记图框中拖拽排序和分页排版。", null, "大样排版"),
            F("line_vision", "图像转 CAD", "LINEVISION", "TXC", "建筑工具", "绘", "识别建筑线稿、扫描图或截图中的线条，预览确认后生成可编辑 CAD 图元。", "TXC", "图像转绘"),
            F("room_rename", "房间改名", "FJGM", "FJGM", "建筑工具", "房", "以一个天正房间为样板，批量修改匹配房间的名称。", null, "房间改名"),
            F("shortcut_settings", "快捷键设置", "WLHOTKEYS", "KJJPZ", "系统设置", "键", "统一查看、修改和恢复万落建筑工具的快捷键。", null, "快捷设置"),
            F("menubar", "菜单栏开关", "WLMENUBAR", "CDL", "系统设置", "菜", "显示或隐藏 AutoCAD 经典菜单栏；从隐藏切回显示时会自动把万落建筑工具下拉菜单挂回菜单栏。", null, "菜单开关"),
            F("cloud_sync", "云同步", "WLCLOUDSYNC", "YTB", "系统设置", "云", "同步通用配置、跨项目方案库、图框模板和项目文件，并保留冲突副本与历史版本。", null, "云端同步")
        };

        /// <summary>固定功能。图层命令由 <see cref="All"/> 动态合成，不写在这里。</summary>
        internal static IReadOnlyList<FeatureDefinition> Items { get { return FixedItems; } }

        /// <summary>
        /// 全部用户可见功能 = 固定功能 + **每图层直达归层命令**。
        ///
        /// 图层命令来自 LayerShortcutStore：只有在图层表里填了快捷键的图层才会出现。
        /// 因为是同一个列表，Ribbon、经典菜单与快捷键设置页会自动三处同步——
        /// 这就是"统一界面控制"与"每个图层有自己的命令"同时成立的方式，
        /// 不存在两个界面各维护一份而不同步的可能。
        ///
        /// 注意：图层命令统一使用内部命令 GL，所以发布期校验
        /// （Build-Release.ps1 要求登记表命令必须在 Commands.cs 注册）依然通过。
        /// </summary>
        public static IReadOnlyList<FeatureDefinition> All
        {
            get
            {
                List<FeatureDefinition> items;
                try
                {
                    var shortcuts = LayerShortcutStore.Load();
                    if (shortcuts.Count == 0) return FixedItems;
                    items = new List<FeatureDefinition>(FixedItems);
                    foreach (var pair in shortcuts)
                    {
                        var role = DraftingLayerRoles.Find(pair.Key);
                        var group = role == null ? "图层工具" : "图层工具-" + role.Group;
                        var name = DraftingStandardService.LayerNameFor(pair.Key);
                        items.Add(new FeatureDefinition(
                            LayerFeatureId(pair.Key),
                            "归层 → " + name,
                            "GL",
                            pair.Value,
                            group,
                            "standard",
                            "把所选对象直接归到“" + name + "”图层。",
                            null,
                            LayerLispInvocation(pair.Key)));
                    }
                }
                catch { return FixedItems; }
                return items;
            }
        }

        public static FeatureDefinition Find(string id)
        {
            return All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>图层命令的功能 id，与快捷键文件、设置界面共用同一约定。</summary>
        public static string LayerFeatureId(string layerKey)
        {
            return "layer_" + (layerKey ?? string.Empty).Trim();
        }

        /// <summary>图层键是否属于动态合成的图层命令。</summary>
        public static bool TryGetLayerKey(string featureId, out string layerKey)
        {
            layerKey = null;
            if (string.IsNullOrWhiteSpace(featureId)) return false;
            if (!featureId.StartsWith("layer_", StringComparison.OrdinalIgnoreCase)) return false;
            layerKey = featureId.Substring("layer_".Length);
            return layerKey.Length > 0;
        }

        /// <summary>
        /// 图层直达命令的 AutoLISP 调用：先把目标图层名放进环境变量，再调用统一的 GL 命令。
        /// GL 读到该变量就直接归层、不弹对话框（见 Commands.AssignSelectedToLayer）。
        /// 这样任意数量的图层共用**一个** [CommandMethod]，无需为每个图层注册命令。
        /// </summary>
        private static string LayerLispInvocation(string layerKey)
        {
            var layerName = DraftingStandardService.LayerNameFor(layerKey);
            return "(setenv \"" + LayerEnvironmentVariable + "\" \"" + EscapeLisp(layerName) + "\") (command \"GL\")";
        }

        internal const string LayerEnvironmentVariable = "WANLUO_TARGET_LAYER";

        private static string EscapeLisp(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static FeatureDefinition F(string id, string name, string command, string shortcut, string group, string icon, string description, string nativeCommand = null, string shortName = null)
        {
            return new FeatureDefinition(id, name, command, shortcut, group, icon, description, nativeCommand, null, shortName);
        }
    }
}
