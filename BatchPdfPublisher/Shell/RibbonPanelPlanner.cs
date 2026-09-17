using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    /// <summary>一个功能区面板的规划结果：标题 + 要放的按钮。</summary>
    public sealed class RibbonPanelPlan
    {
        public RibbonPanelPlan(string title, IList<FeatureDefinition> features)
        {
            Title = title;
            Features = features;
        }

        public string Title { get; private set; }
        public IList<FeatureDefinition> Features { get; private set; }
    }

    /// <summary>面板规划表的一行：标题 + 归入该面板的分组名。</summary>
    public sealed class RibbonPanelPlanEntry
    {
        public RibbonPanelPlanEntry(string title, params string[] groups)
        {
            Title = title;
            Groups = groups ?? new string[0];
        }

        public string Title { get; private set; }
        public string[] Groups { get; private set; }
    }

    /// <summary>
    /// 功能区面板规划。原来是一个分组一个面板、每个面板只排一行，十几个功能一字排开
    /// 会把整条功能区占满（超出的还会被折进下拉）。这里把相关分组并成少量面板，
    /// 并给每个面板的按钮数量设上限，让横向占用不再随功能数量无限增长。
    ///
    /// 不依赖 AutoCAD，可离线断言：面板规划错了在 CAD 里只会表现为"面板很宽"或
    /// "功能不见了"，很难定位。
    /// </summary>
    public static class RibbonPanelPlanner
    {
        /// <summary>面板里每行放几个按钮，超过就换行。</summary>
        public const int ButtonsPerRow = 2;
        /// <summary>面板最多几行。AutoCAD 面板常规密度是 3 行。</summary>
        public const int MaxRows = 3;
        /// <summary>单个面板最多放几个按钮；超出的自动拆成后续面板。</summary>
        public const int MaxItemsPerPanel = ButtonsPerRow * MaxRows;

        /// <summary>
        /// 默认规划。新功能只要在 FeatureRegistry 里登记并归到已有分组，就会自动落进
        /// 对应面板；如果用了全新分组名，它会自己单独成一个面板（不会丢），
        /// 想让它并进某个面板就在这里加进 Groups。
        /// </summary>
        public static readonly RibbonPanelPlanEntry[] DefaultPlan =
        {
            new RibbonPanelPlanEntry("图纸与发布", "图纸与发布", "图块与属性"),
            new RibbonPanelPlanEntry("建筑工具", "建筑工具"),
            new RibbonPanelPlanEntry("制图与图层", "制图与标注", "图层工具"),
            new RibbonPanelPlanEntry("系统设置", "系统设置")
        };

        public static IList<RibbonPanelPlan> Plan(IEnumerable<FeatureDefinition> features)
        {
            return Plan(features, DefaultPlan);
        }

        public static IList<RibbonPanelPlan> Plan(IEnumerable<FeatureDefinition> features, RibbonPanelPlanEntry[] plan)
        {
            var all = (features ?? Enumerable.Empty<FeatureDefinition>()).Where(x => x != null).ToList();
            var result = new List<RibbonPanelPlan>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in plan ?? DefaultPlan)
            {
                var items = all.Where(x => entry.Groups.Contains(x.Group ?? string.Empty, StringComparer.Ordinal)).ToList();
                if (items.Count == 0) continue;
                foreach (var group in entry.Groups) claimed.Add(group);
                AppendChunked(result, entry.Title, items);
            }
            // 没写进规划的分组各自成面板，保证新登记的功能不会漏掉。
            foreach (var group in all.GroupBy(x => x.Group ?? string.Empty)
                         .Where(g => !claimed.Contains(g.Key))
                         .OrderBy(g => g.Key, StringComparer.CurrentCulture))
                AppendChunked(result, group.Key, group.ToList());
            return result;
        }

        private static void AppendChunked(List<RibbonPanelPlan> target, string title, IList<FeatureDefinition> items)
        {
            // 超限时**均分**，而不是前面塞满、最后一个只剩一两项。例如 7 项、上限 6 时
            // 拆成 4 + 3，而不是 6 + 1——避免出现只有一两个按钮的尴尬面板。
            var panelCount = (int)Math.Ceiling(items.Count / (double)MaxItemsPerPanel);
            var perPanel = (int)Math.Ceiling(items.Count / (double)panelCount);
            for (var offset = 0; offset < items.Count; offset += perPanel)
            {
                var chunk = items.Skip(offset).Take(perPanel).ToList();
                var name = offset == 0 ? title : title + " (" + (offset / perPanel + 1) + ")";
                target.Add(new RibbonPanelPlan(name, chunk));
            }
        }
    }
}
