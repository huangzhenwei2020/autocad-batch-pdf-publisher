using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
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
            // 样式版本让旧的汉字色块标签自动重建；快捷键更新仍同步到提示。
            var shortcuts = ShortcutSettingsService.Load();
            foreach (var feature in features)
            {
                var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
                string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                if (!buttons.Any(x => x.Id == RibbonIconAssets.StyleVersion + feature.Id
                    && string.Equals(x.Text, label, StringComparison.Ordinal)
                    && string.Equals(x.ToolTip as string, ButtonToolTip(feature, shortcut), StringComparison.Ordinal))) return false;
            }
            return true;
        }

        /// <summary>
        /// 使用原生大按钮，图标在上、名称在下；沿用面板规划以保证所有功能保留。
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

        private static RibbonButton CreateButton(FeatureDefinition feature, IDictionary<string, string> shortcuts)
        {
            string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
            var image = RibbonIconAssets.ForFeature(feature.Id);
            // 与确认稿一致：图形图标 + 简称，完整名称和当前快捷键放入提示。
            var label = string.IsNullOrWhiteSpace(feature.ShortName) ? feature.Name : feature.ShortName;
            return new RibbonButton
            {
                Id = RibbonIconAssets.StyleVersion + feature.Id,
                Text = label,
                ToolTip = ButtonToolTip(feature, shortcut),
                ShowText = true,
                ShowImage = true,
                Image = RibbonIconAssets.SmallForFeature(feature.Id),
                LargeImage = image,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                CommandParameter = feature.Command + " ",
                CommandHandler = new CommandHandler(feature.Command + " ")
            };
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
