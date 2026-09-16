using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 图层角色登记表：为制图标准里的每个图层键（系统标签）记录用途与**使用方**。
    ///
    /// 存在的意义是让"每个图层都要真正有作用"变成可核对的事实，而不是靠人记：
    /// 用户能在图层面板上看到每个图层被谁使用；构建期自检可以据此报出无人使用的
    /// "装饰图层"（见 build/Test-LayerUsage.ps1）。
    ///
    /// 本文件必须保持**无 AutoCAD 依赖**，以便进纯逻辑单元测试工程。
    /// </summary>
    public static class DraftingLayerRoles
    {
        public sealed class Role
        {
            public string Key;
            /// <summary>面板上的分组标题，如"图框"、"建筑"、"注释"、"楼梯"、"门窗"、"大样"。</summary>
            public string Group;
            /// <summary>该图层当前被哪些模块使用；空数组表示目前没有任何代码使用它。</summary>
            public string[] Consumers;
        }

        public static readonly Role[] All =
        {
            R("Frame", "图框", "图框创建与插入", "大样排版"),
            R("Catalog", "图框", "图纸目录插入"),
            R("Outline", "建筑", "图像转 CAD（水平线、墙外轮廓）、归层"),
            R("Fine", "建筑", "图像转 CAD（无法判定方向的曲线）"),
            R("Structure", "建筑", "图像转 CAD（垂直线）"),
            R("Hidden", "建筑", "图像转 CAD（斜线）"),
            R("Hatch", "建筑", "图像转 CAD（墙体填充）"),
            R("AnnotationTextLayer", "注释", "图框属性文字、门窗立面文字、图像转 CAD 文字、比例更新"),
            R("AnnotationDimensionLayer", "注释", "比例更新的标注归层、门窗立面标注"),
            R("StairAxis", "楼梯", "楼梯大样（轴线）"),
            R("StairOutline", "楼梯", "楼梯大样（轮廓）"),
            R("StairTread", "楼梯", "楼梯大样（踏步）"),
            R("StairSection", "楼梯", "楼梯大样（剖面）"),
            R("StairWall", "楼梯", "楼梯大样（剖面墙）"),
            R("StairSide", "楼梯", "楼梯大样（侧面）"),
            R("StairHandrail", "楼梯", "楼梯大样（扶手）"),
            R("StairCutHatch", "楼梯", "楼梯大样（剖切填充）"),
            R("StairBreakLine", "楼梯", "楼梯大样（折断线）"),
            R("DoorWindowWindow", "门窗", "门窗立面（窗）、楼梯门窗洞口"),
            R("DoorWindowDoor", "门窗", "门窗立面（门）、楼梯门窗洞口"),
            R("DoorWindowOpening", "门窗", "门窗立面（开启洞口）、楼梯门窗洞口"),
            R("DetailSeparator", "大样", "大样排版（分隔线）"),
            R("DetailIndex", "大样", "大样排版（索引）")
        };

        public static IEnumerable<Role> ByGroup(string group)
        {
            return All.Where(x => string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase));
        }

        public static Role Find(string key)
        {
            return All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把使用方渲染成面板/日志用的一行文字；无人使用时明确说明。</summary>
        public static string DescribeConsumers(string key)
        {
            var role = Find(key);
            if (role == null) return "未登记（该图层不在角色表中）";
            if (role.Consumers == null || role.Consumers.Length == 0) return "当前无任何功能使用（装饰图层）";
            return string.Join("、", role.Consumers);
        }

        private static Role R(string key, string group, params string[] consumers)
        {
            return new Role { Key = key, Group = group, Consumers = consumers ?? new string[0] };
        }
    }
}
