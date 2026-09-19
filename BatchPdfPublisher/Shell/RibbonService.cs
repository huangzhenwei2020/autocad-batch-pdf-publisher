using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Internal.Windows;
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
                ribbon.Tabs.Add(tab);
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
            var buttons = tab.Panels.Where(x => x.Source != null).SelectMany(x => x.Source.Items).ToList();
            var features = FeatureRegistry.Items;
            if (buttons.Count != features.Count) return false;
            // 样式版本让旧的汉字色块标签自动重建；快捷键更新同步到常显标签与提示。
            var shortcuts = ShortcutSettingsService.Load();
            foreach (var feature in features)
            {
                var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
                string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                if (!buttons.Any(x => x.Id == RibbonIconAssets.StyleVersion + feature.Id
                    && string.Equals(x.Text, label, StringComparison.Ordinal)
                    && string.Equals(x.Tag as string, BadgeThemeKey(), StringComparison.Ordinal)
                    && string.Equals(x.ToolTip as string, ButtonToolTip(feature, shortcut), StringComparison.Ordinal))) return false;
            }
            return true;
        }

        /// <summary>
        /// 使用 Ribbon 内容宿主，图标在上、名称与快捷键标签在下；保留全部功能面板。
        /// </summary>
        private static RibbonPanel CreatePanel(string title, IList<FeatureDefinition> features, IDictionary<string, string> shortcuts)
        {
            var source = new RibbonPanelSource { Title = title == "建筑工具 (2)" ? "建筑编辑" : title };
            for (var index = 0; index < features.Count; index++)
            {
                source.Items.Add(CreateButton(features[index], shortcuts));
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

        private static string BadgeThemeKey()
        {
            try { return ReadCadTheme(); }
            catch { return "dark"; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ReadCadTheme() { return Convert.ToInt32(Application.GetSystemVariable("COLORTHEME")) == 0 ? "dark" : "light"; }

        private static RibbonItem CreateButton(FeatureDefinition feature, IDictionary<string, string> shortcuts)
        {
            string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
            var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
            var dark = BadgeThemeKey() == "dark";
            var button = CreateBadgeButton(label, shortcut, RibbonIconAssets.ForScaledFeature(feature.Id), dark);
            button.Command = new CommandHandler(feature.Command + " ");
            button.CommandParameter = feature.Command + " ";
            button.ToolTip = ButtonToolTip(feature, shortcut);
            System.Windows.Automation.AutomationProperties.SetName(button, feature.Name + " " + shortcut);
            // Composite hosts WPF content without forcing the entire caption into a 32px image slot.
            return new RibbonCompositeItem
            {
                Id = RibbonIconAssets.StyleVersion + feature.Id,
                Text = label,
                Tag = dark ? "dark" : "light",
                ToolTip = ButtonToolTip(feature, shortcut),
                Size = RibbonItemSize.Large,
                Content = button
            };
        }

        internal static System.Windows.Controls.Button CreateBadgeButton(string label, string shortcut, ImageSource image, bool dark)
        {
            var foreground = new SolidColorBrush(dark ? Color.FromRgb(240, 246, 255) : Color.FromRgb(22, 37, 60));
            var accent = new SolidColorBrush(dark ? Color.FromRgb(101, 192, 255) : Color.FromRgb(0, 112, 237));
            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(5, 2, 5, 2) };
            stack.Children.Add(new System.Windows.Controls.Image { Source = image, Width = 32, Height = 32, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 5) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrWhiteSpace(shortcut))
                row.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(.7), BorderBrush = accent,
                    Background = new SolidColorBrush(dark ? Color.FromRgb(24, 49, 76) : Color.FromRgb(228, 245, 255)),
                    Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(5, 0, 0, 0),
                    Child = new TextBlock { Text = shortcut, FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = accent }
                });
            stack.Children.Add(row);
            var button = new System.Windows.Controls.Button
            {
                Content = stack, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1), Padding = new Thickness(1), Cursor = Cursors.Hand,
                UseLayoutRounding = true, SnapsToDevicePixels = true, FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(System.Windows.Controls.Button)) { VisualTree = border };
            foreach (var property in new[] { UIElement.IsMouseOverProperty, UIElement.IsKeyboardFocusWithinProperty })
            {
                var trigger = new Trigger { Property = property, Value = true };
                trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(dark ? Color.FromRgb(47, 69, 93) : Color.FromRgb(220, 238, 255)), "Chrome"));
                trigger.Setters.Add(new Setter(Border.BorderBrushProperty, accent, "Chrome")); template.Triggers.Add(trigger);
            }
            button.Template = template;
            return button;
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
