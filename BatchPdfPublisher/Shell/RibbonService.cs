using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using System.Linq;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Services
{
    public static class RibbonService
    {
        private const string TabId = "BPP_BATCH_PDF_TAB";
        /// <summary>
        /// Idle 检查的最小间隔。Application.Idle 在消息队列一空就触发，空闲时一秒能来
        /// 几十上百次；每次检查都要读功能表与快捷键表来判断标签是否过期，还会碰 WPF
        /// 功能区树。加了节流之后 CAD 界面（尤其是模态窗口）不发滞，而工作空间重建后
        /// 最多晚 1.2 秒把标签补回来，用户察觉不到。显式刷新（InstallWhenReady /
        /// RefreshNow）不走节流，立刻生效。
        /// </summary>
        private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMilliseconds(1200);

        private static EventHandler _idleHandler;
        private static bool _installed;
        private static DateTime _lastIdleCheckUtc = DateTime.MinValue;

        public static void InstallWhenReady()
        {
            EnsureIdleHook();
            // A freshly started AutoCAD may already have a Ribbon when NETLOAD
            // completes; install immediately as well as on subsequent idle ticks.
            RefreshNow();
        }

        public static bool RefreshNow()
        {
            _lastIdleCheckUtc = DateTime.UtcNow;
            return TryInstallRibbon(true);
        }

        private static void EnsureIdleHook()
        {
            if (_idleHandler != null) return;
            _idleHandler = (sender, args) =>
            {
                // Workspaces and vertical products (such as T20) can rebuild
                // the Ribbon after our assembly has loaded.  Keep this cheap
                // check on Idle so the tab is restored when that happens.
                var now = DateTime.UtcNow;
                if (now - _lastIdleCheckUtc < IdleCheckInterval) return;
                _lastIdleCheckUtc = now;
                TryInstallRibbon(false);
            };
            Application.Idle += _idleHandler;
        }

        private static bool TryInstallRibbon(bool traceNotReady)
        {
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null)
                {
                    _installed = false;
                    if (traceNotReady) Trace("Ribbon 容器尚未创建，已进入等待状态");
                    return false;
                }

                var existing = ribbon.FindTab(TabId);
                if (IsCurrentTab(existing))
                {
                    existing.IsVisible = true;
                    if (!_installed) QueueShortcutBadges(ribbon, existing);
                    if (!_installed) Trace("Ribbon 标签已恢复并设为可见");
                    _installed = true;
                    return true;
                }

                if (existing != null) ribbon.Tabs.Remove(existing);
                var tab = new RibbonTab { Id = TabId, Title = "万落建筑工具", IsVisible = true };
                var shortcuts = ShortcutSettingsService.Load();
                // 功能区只放固定功能。每图层直达归层命令是动态生成的、数量随图层表增长，
                // 放进功能区会再次把面板撑成一条长龙；它们已经出现在菜单栏子菜单和快捷键上。
                var features = FeatureRegistry.Items;
                foreach (var panel in RibbonPanelPlanner.Plan(features))
                    tab.Panels.Add(CreatePanel(panel.Title, panel.Features, shortcuts));
                tab.Activated += (sender, args) => QueueShortcutBadges(ribbon, tab);
                ribbon.Tabs.Add(tab);
                QueueShortcutBadges(ribbon, tab);
                _installed = true;
                Trace("Ribbon 标签已创建：万落建筑工具，面板数=" + tab.Panels.Count + "，按钮数=" + features.Count);
                return true;
            }
            catch (Exception exception)
            {
                _installed = false;
                Trace(exception);
                return false;
            }
        }

        private static bool IsCurrentTab(RibbonTab tab)
        {
            if (tab == null || tab.Panels.Count == 0) return false;
            var buttons = tab.Panels.Where(x => x.Source != null).SelectMany(x => x.Source.Items).OfType<RibbonButton>().ToList();
            var features = FeatureRegistry.Items;
            if (buttons.Count != features.Count) return false;
            // 样式版本让旧的汉字色块标签自动重建；快捷键更新同步到常显标签与提示。
            var shortcuts = ShortcutSettingsService.Load();
            foreach (var feature in features)
            {
                var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
                string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                var button = buttons.FirstOrDefault(x => x.Id == RibbonIconAssets.StyleVersion + feature.Id + "_button");
                if (button == null
                    || !string.Equals(button.Text, ButtonText(label), StringComparison.Ordinal)
                    || !string.Equals(button.ToolTip as string, ButtonToolTip(feature, shortcut), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// 使用 AutoCAD 原生大按钮，图标在上、名称和快捷键标签在下；保留全部功能面板。
        /// RibbonCompositeItem 在 AutoCAD 2022 / 天正中只显示空面板，因此不要在这里
        /// 承载自定义 WPF 内容。快捷键标签在按钮生成后通过轻量 Adorner 绘制。
        /// </summary>
        private static RibbonPanel CreatePanel(string title, IList<FeatureDefinition> features, IDictionary<string, string> shortcuts)
        {
            var source = new RibbonPanelSource { Title = title == "建筑工具 (2)" ? "建筑编辑" : title };
            for (var index = 0; index < features.Count; index++)
            {
                source.Items.Add(CreateFeatureButton(features[index], shortcuts));
            }
            return new RibbonPanel { Source = source };
        }

        private static void Trace(Exception exception)
        {
            Trace("Ribbon 异常：" + exception);
        }

        private static void Trace(string message)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(UserDataPaths.LogsDirectory, "BatchPdfPublisher.ui.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { }
        }

        public static void Remove()
        {
            if (_idleHandler != null)
            {
                Application.Idle -= _idleHandler;
                _idleHandler = null;
            }
            _installed = false;
            _lastIdleCheckUtc = DateTime.MinValue;
            var ribbon = ComponentManager.Ribbon;
            var tab = ribbon?.FindTab(TabId);
            if (tab != null) ribbon.Tabs.Remove(tab);
        }

        private static RibbonButton CreateFeatureButton(FeatureDefinition feature, IDictionary<string, string> shortcuts)
        {
            string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
            var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
            var tooltip = ButtonToolTip(feature, shortcut);
            return new RibbonButton
            {
                Id = RibbonIconAssets.StyleVersion + feature.Id + "_button",
                Text = ButtonText(label),
                ToolTip = tooltip,
                ShowText = true,
                ShowImage = true,
                Image = RibbonIconAssets.SmallForFeature(feature.Id),
                LargeImage = RibbonIconAssets.ForFeature(feature.Id),
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                Width = shortcut != null && shortcut.Trim().Length > 2 ? 82 : 76,
                MinWidth = shortcut != null && shortcut.Trim().Length > 2 ? 82 : 76,
                CommandParameter = feature.Command + " ",
                CommandHandler = new CommandHandler(feature.Command + " "),
                Tag = new ShortcutBadgeInfo(label, shortcut)
            };
        }

        private static string ButtonText(string label)
        {
            return label;
        }

        private static void QueueShortcutBadges(RibbonControl ribbon, RibbonTab tab)
        {
            if (ribbon == null || tab == null) return;
            ribbon.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => InstallShortcutBadges(ribbon, tab)));
        }

        private static void InstallShortcutBadges(RibbonControl ribbon, RibbonTab tab)
        {
            try
            {
                var buttons = tab.Panels.Where(x => x.Source != null)
                    .SelectMany(x => x.Source.Items).OfType<RibbonButton>().ToList();
                var elements = VisualDescendants(ribbon).OfType<FrameworkElement>().ToList();
                var orderedButtonControls = elements
                    .Where(IsLargeRibbonButtonControl)
                    .OrderBy(x => x.TranslatePoint(new Point(0, 0), ribbon).X)
                    .ToList();
                var installed = 0;
                for (var buttonIndex = 0; buttonIndex < buttons.Count; buttonIndex++)
                {
                    var button = buttons[buttonIndex];
                    var info = button.Tag as ShortcutBadgeInfo;
                    if (info == null) continue;
                    var target = elements
                        .Where(x => (ReferenceEquals(x.DataContext, button)
                            || ReferenceEquals((x as ContentControl)?.Content, button)
                            || string.Equals(NormalizeCaption(AutomationProperties.GetName(x)), NormalizeCaption(info.Label), StringComparison.Ordinal))
                            && x.ActualWidth >= 40 && x.ActualHeight >= 35)
                        .OrderByDescending(x => x.ActualWidth * x.ActualHeight)
                        .FirstOrDefault();
                    // AutoCAD/T20 does not expose the RibbonButton model through DataContext
                    // or Content. On the active custom tab these are the only 72px native
                    // large-button controls, so visual order is the stable fallback.
                    if (target == null && buttonIndex < orderedButtonControls.Count)
                        target = orderedButtonControls[buttonIndex];
                    if (target == null) continue;
                    var layer = AdornerLayer.GetAdornerLayer(target);
                    if (layer == null) continue;
                    var current = layer.GetAdorners(target)?.OfType<ShortcutBadgeAdorner>().FirstOrDefault();
                    if (current != null && string.Equals(current.Shortcut, info.Shortcut, StringComparison.OrdinalIgnoreCase))
                    {
                        installed++;
                        continue;
                    }
                    if (current != null) layer.Remove(current);
                    layer.Add(new ShortcutBadgeAdorner(target, info.Shortcut));
                    installed++;
                }
                Trace("Ribbon 快捷键标签已定位：" + installed + "/" + buttons.Count
                    + "，候选按钮=" + orderedButtonControls.Count);
            }
            catch (Exception exception)
            {
                Trace("Ribbon 快捷键标签定位失败：" + exception);
            }
        }

        private static string NormalizeCaption(string value)
        {
            return new string((value ?? string.Empty).Where(x => !char.IsWhiteSpace(x)).ToArray());
        }

        private static bool IsLargeRibbonButtonControl(FrameworkElement element)
        {
            if (element == null || !element.IsVisible
                || element.ActualWidth < 70 || element.ActualWidth > 90
                || element.ActualHeight < 65 || element.ActualHeight > 78) return false;
            var typeName = element.GetType().FullName ?? string.Empty;
            return typeName.IndexOf("RibbonButton", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("RibbonItemControl", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
        {
            if (root == null) yield break;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (var descendant in VisualDescendants(child)) yield return descendant;
            }
        }

        private sealed class ShortcutBadgeInfo
        {
            internal ShortcutBadgeInfo(string label, string shortcut)
            {
                Label = label ?? string.Empty;
                Shortcut = (shortcut ?? string.Empty).Trim().ToUpperInvariant();
            }

            internal string Label { get; }
            internal string Shortcut { get; }
        }

        private sealed class ShortcutBadgeAdorner : Adorner
        {
            private readonly Typeface _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            internal ShortcutBadgeAdorner(UIElement adornedElement, string shortcut) : base(adornedElement)
            {
                Shortcut = string.IsNullOrWhiteSpace(shortcut) ? "-" : shortcut.Trim().ToUpperInvariant();
                IsHitTestVisible = false;
            }

            internal string Shortcut { get; }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                var text = new FormattedText(Shortcut, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    _typeface, 8d, Brushes.White, 1d);
                var width = Math.Max(20d, Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 8d);
                const double height = 13d;
                var bounds = new Rect(Math.Max(2d, (ActualWidth - width) / 2d), Math.Max(2d, ActualHeight - height - 3d), width, height);
                drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(65, 133, 238)),
                    new Pen(new SolidColorBrush(Color.FromRgb(112, 169, 255)), .5), bounds, 3d, 3d);
                drawingContext.DrawText(text, new Point(bounds.X + (bounds.Width - text.Width) / 2d,
                    bounds.Y + (bounds.Height - text.Height) / 2d - .2d));
            }
        }

        private static string ButtonToolTip(FeatureDefinition feature, string shortcut)
        {
            return feature.Name + "（" + shortcut + "）\n" + feature.Description;
        }

        private sealed class CommandHandler : ICommand
        {
            private readonly string _command;

            public CommandHandler(string command)
            {
                _command = command;
            }

            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                var document = Application.DocumentManager.MdiActiveDocument;
                if (document != null) document.SendStringToExecute(_command, true, false, false);
            }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
