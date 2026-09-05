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
        private static readonly FeatureDefinition[] Items =
        {
            F("publisher", "批量 PDF 面板", "BPP", "BPP", "图纸与发布", "panel", "打开工程 DWG 管理、图框扫描和批量 PDF 发布面板。"),
            F("frame", "创建图框", "TKK", "TKK", "图纸与发布", "frame", "创建标准图框，或选择已登记图框并按指定比例插入。"),
            F("catalog", "插入目录", "ML1", "ML1", "图纸与发布", "catalog", "根据当前工程图纸顺序生成目录表并插入 CAD。"),
            F("attribute_batch", "批量改属性", "BPPATTR", "SBB", "图块与属性", "attribute", "框选不同类型的属性图块，按坐标排序、批量递增并写入同一属性标记。"),
            F("attribute_definition", "属性定义编辑", "BPPATTDEF", "BPA", "图块与属性", "attribute", "拾取图块后修改图块名称、属性 TAG、默认内容、字体、字高、宽度和对齐方式。"),
            F("architecture_spec", "建筑设计说明", "WLJZSM", "JZSM", "建筑工具", "spec", "打开万落建筑工具中的建筑设计说明助手。", "JZSM"),
            F("stair_detail", "楼梯大样", "WLLTDY", "LTDY", "建筑工具", "stair", "打开楼梯构件编辑器，按楼层、梯段和构造参数一键生成楼梯大样。", "LTDY"),
            F("drafting_standard", "制图标准", "BZS", "BZS", "制图与标注", "standard", "检查并补齐万落工具共用的图层、文字样式和标注样式。"),
            F("drawing_scale", "比例管理", "BL1", "BL1", "制图与标注", "scale", "把所选对象转换到指定图纸比例，并同步普通 CAD 与天正标注。"),
            F("door_window", "门窗立面", "MCLM", "MCLM", "建筑工具", "doorwindow", "读取门窗表，校验编号和洞口尺寸，并批量设置门窗立面分格与开启参数。"),
            F("detail_layout", "大样排版", "WLDYLAYOUT", "DYPB", "建筑工具", "detail", "逐个框选大样并自动计算边界，在登记图框中拖拽排序和分页排版。"),
            F("line_vision", "图像转 CAD", "LINEVISION", "TXZCAD", "建筑工具", "image", "识别建筑线稿、扫描图或截图中的线条，预览确认后生成可编辑 CAD 图元。"),
            F("room_rename", "房间改名", "FJGM", "FJGM", "建筑工具", "room", "以一个天正房间为样板，批量修改匹配房间的名称。"),
            F("shortcut_settings", "快捷键设置", "WLHOTKEYS", "KJJPZ", "系统设置", "shortcut", "统一查看、修改和恢复万落建筑工具的快捷键。"),
            F("cloud_sync", "云同步", "WLCLOUDSYNC", "YTB", "系统设置", "sync", "同步通用配置、跨项目方案库、图框模板和项目文件，并保留冲突副本与历史版本。")
        };

        public static IReadOnlyList<FeatureDefinition> All { get { return Items; } }

        public static FeatureDefinition Find(string id)
        {
            return Items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static FeatureDefinition F(string id, string name, string command, string shortcut, string group, string icon, string description, string nativeCommand = null)
        {
            return new FeatureDefinition(id, name, command, shortcut, group, icon, description, nativeCommand);
        }
    }

    public sealed class FeatureDefinition
    {
        public FeatureDefinition(string id, string name, string command, string defaultShortcut, string group, string icon, string description, string nativeCommand)
        {
            Id = id; Name = name; Command = command; DefaultShortcut = defaultShortcut;
            Group = group; Icon = icon; Description = description;
            NativeCommand = nativeCommand;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Command { get; private set; }
        public string DefaultShortcut { get; private set; }
        public string Group { get; private set; }
        public string Icon { get; private set; }
        public string Description { get; private set; }
        /// <summary>外置组件真正注册的命令。快捷键与它相同时直接使用，不生成 AutoLISP 包装，避免递归。</summary>
        public string NativeCommand { get; private set; }
    }
}
