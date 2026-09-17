using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using System.Windows.Media;
using WpfRect = System.Windows.Rect;
using WpfPoint = System.Windows.Point;
using System.Linq;

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
            var buttons = tab.Panels.Where(x => x.Source != null).SelectMany(x => x.Source.Items).OfType<RibbonButton>().ToList();
            var features = FeatureRegistry.Items;
            if (buttons.Count != features.Count) return false;
            // 按钮文字是「四字简称（当前快捷键）」。旧标签比对不上就会重建，
            // 所以用户改了快捷键之后这里也自动跟着更新。
            var shortcuts = ShortcutSettingsService.Load();
            foreach (var feature in features)
            {
                var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
                string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                if (!buttons.Any(x => string.Equals(x.Text, label + "（" + shortcut + "）", StringComparison.Ordinal))) return false;
            }
            return true;
        }

        /// <summary>
        /// 按规划生成一个面板：每行 <see cref="RibbonPanelPlanner.ButtonsPerRow"/> 个，
        /// 用 RibbonRowBreak 换行；单个面板的按钮上限由
        /// <see cref="RibbonPanelPlanner"/> 负责拆分，这里只负责排版。
        /// </summary>
        private static RibbonPanel CreatePanel(string title, IList<FeatureDefinition> features, IDictionary<string, string> shortcuts)
        {
            var source = new RibbonPanelSource { Title = title };
            for (var index = 0; index < features.Count; index++)
            {
                source.Items.Add(CreateButton(features[index], shortcuts));
                // 每满一行换行；最后一行不加，避免多出一行空行。
                if ((index + 1) % RibbonPanelPlanner.ButtonsPerRow == 0 && index + 1 < features.Count)
                    source.Items.Add(new RibbonRowBreak());
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

        private static RibbonButton CreateButton(FeatureDefinition feature, IDictionary<string, string> shortcuts)
        {
            string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
            var image = CreateIcon(feature);
            // 功能区是网格排版，按钮文字用统一四字简称，并且带上当前快捷键。
            // 完整名称放悬停提示第一行，信息不丢。
            var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
            return new RibbonButton
            {
                Text = label + "（" + shortcut + "）",
                ToolTip = feature.Name + "\n" + feature.Description,
                ShowText = true,
                ShowImage = true,
                Image = image,
                LargeImage = image,
                Size = RibbonItemSize.Standard,
                Orientation = Orientation.Horizontal,
                CommandParameter = feature.Command + " ",
                CommandHandler = new CommandHandler(feature.Command + " ")
            };
        }

        /// <summary>
        /// 生成图标：**按分组着色的圆角色块 + 一个白色汉字**。
        ///
        /// 原来是用同一支细蓝线画的小图形，16 px 下几乎分不出来。改成色块 + 汉字之后：
        /// 颜色一眼分大类（图纸蓝 / 图块紫 / 建筑青 / 制图橙 / 图层绿 / 系统灰），
        /// 汉字一眼分具体功能，而且汉字在 16 px 下比线条图形清楚得多。
        /// 矢量绘制，缩放不会糊；图标字来自功能登记表，改功能时同步那一个字即可。
        /// </summary>
        private static ImageSource CreateIcon(FeatureDefinition feature)
        {
            const double size = 16d;
            var group = new DrawingGroup();

            var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(RibbonIconTheme.ColorHex(feature.Group)));
            group.Children.Add(new GeometryDrawing(background, null,
                new RectangleGeometry(new WpfRect(0d, 0d, size, size), 3.5d, 3.5d)));

            var glyph = RibbonIconTheme.GlyphFor(feature.Icon);
            // 这里不 using System.Windows，避免 Application 与 AutoCAD 的 Application 撞名。
            var typeface = new Typeface(new FontFamily("Microsoft YaHei"),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal);
            var text = new FormattedText(glyph, System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, typeface, size * 0.74d, Brushes.White, 96d);
            var geometry = text.BuildGeometry(new WpfPoint((size - text.Width) / 2d, (size - text.Height) / 2d));
            group.Children.Add(new GeometryDrawing(Brushes.White, null, geometry));

            return new DrawingImage(group);
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
