using System;
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
        private static EventHandler _idleHandler;
        private static bool _installed;

        public static void InstallWhenReady()
        {
            if (_idleHandler == null)
            {
                _idleHandler = (sender, args) =>
                {
                    // Workspaces and vertical products (such as T20) can rebuild
                    // the Ribbon after our assembly has loaded.  Keep this cheap
                    // check on Idle so the tab is restored when that happens.
                    TryInstallRibbon(false);
                };
                Application.Idle += _idleHandler;
            }
            // A freshly started AutoCAD may already have a Ribbon when NETLOAD
            // completes; install immediately as well as on subsequent idle ticks.
            TryInstallRibbon(true);
        }

        public static bool RefreshNow()
        {
            InstallWhenReady();
            return TryInstallRibbon(true);
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
                foreach (var group in FeatureRegistry.All.GroupBy(x => x.Group))
                {
                    var source = new RibbonPanelSource { Title = group.Key };
                    foreach (var feature in group)
                    {
                        string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                        source.Items.Add(CreateButton(feature.Name + "（" + shortcut + "）", feature.Command + " ", feature.Icon, feature.Description));
                    }
                    tab.Panels.Add(new RibbonPanel { Source = source });
                }
                ribbon.Tabs.Add(tab);
                _installed = true;
                Trace("Ribbon 标签已创建：万落建筑工具，按钮数=" + FeatureRegistry.All.Count);
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
            if (buttons.Count != FeatureRegistry.All.Count) return false;
            var shortcuts = ShortcutSettingsService.Load();
            foreach (var feature in FeatureRegistry.All)
            {
                string shortcut; if (!shortcuts.TryGetValue(feature.Id, out shortcut)) shortcut = feature.DefaultShortcut;
                if (!buttons.Any(x => x.Text.IndexOf(feature.Name, StringComparison.OrdinalIgnoreCase) >= 0 && x.Text.IndexOf(shortcut, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }
            return true;
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
            var ribbon = ComponentManager.Ribbon;
            var tab = ribbon?.FindTab(TabId);
            if (tab != null) ribbon.Tabs.Remove(tab);
        }

        private static RibbonButton CreateButton(string text, string command, string icon, string description)
        {
            var image = CreateIcon(icon);
            return new RibbonButton
            {
                Text = text,
                ToolTip = description,
                ShowText = true,
                ShowImage = true,
                Image = image,
                LargeImage = image,
                Size = RibbonItemSize.Standard,
                Orientation = Orientation.Horizontal,
                CommandParameter = command,
                CommandHandler = new CommandHandler(command)
            };
        }

        private static ImageSource CreateIcon(string kind)
        {
            var group = new DrawingGroup();
            var pen = new Pen(Brushes.DarkSlateBlue, 1.6);
            var brush = new SolidColorBrush(Color.FromRgb(45, 112, 190));
            if (kind == "stair")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 13), new WpfPoint(14, 13))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 13), new WpfPoint(2, 10))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 10), new WpfPoint(5, 10))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(5, 10), new WpfPoint(5, 7))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(5, 7), new WpfPoint(8, 7))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(8, 7), new WpfPoint(8, 4))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(8, 4), new WpfPoint(13, 4))));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(13, 4), 1.6, 1.6)));
            }
            else if (kind == "room")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new WpfRect(2, 3, 12, 10))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(8, 3), new WpfPoint(8, 8))));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(8, 10), 2.2, 2.2)));
            }
            else if (kind == "doorwindow")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(8, 2), new WpfPoint(8, 14))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 8), new WpfPoint(14, 8))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 14), new WpfPoint(8, 8))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(14, 14), new WpfPoint(8, 8))));
            }
            else if (kind == "image")
            {
                group.Children.Add(new GeometryDrawing(Brushes.White, pen, new RectangleGeometry(new WpfRect(2, 3, 12, 10))));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(5, 6), 1.4, 1.4)));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(3, 12), new WpfPoint(7, 8))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(7, 8), new WpfPoint(10, 11))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(10, 11), new WpfPoint(13, 7))));
            }
            else if (kind == "spec")
            {
                group.Children.Add(new GeometryDrawing(Brushes.White, pen, new RectangleGeometry(new WpfRect(3, 2, 10, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(5, 5), new WpfPoint(11, 5))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(5, 8), new WpfPoint(11, 8))));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(11.5, 11.5), 2.5, 2.5)));
            }
            else if (kind == "frame")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(5, 5), new WpfPoint(11, 11))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(11, 5), new WpfPoint(5, 11))));
            }
            else if (kind == "catalog")
            {
                for (var y = 3; y <= 11; y += 4)
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(3, y), new WpfPoint(13, y))));
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
            }
            else if (kind == "attribute")
            {
                group.Children.Add(new GeometryDrawing(brush, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
                group.Children.Add(new GeometryDrawing(Brushes.White, null, new LineGeometry(new WpfPoint(5, 6), new WpfPoint(11, 6))));
                group.Children.Add(new GeometryDrawing(Brushes.White, null, new LineGeometry(new WpfPoint(5, 9), new WpfPoint(11, 9))));
            }
            else if (kind == "standard")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 6), new WpfPoint(14, 6))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 10), new WpfPoint(14, 10))));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(6, 6), 1.4, 1.4)));
                group.Children.Add(new GeometryDrawing(brush, null, new EllipseGeometry(new WpfPoint(10, 10), 1.4, 1.4)));
            }
            else if (kind == "scale")
            {
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 12), new WpfPoint(14, 4))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 9), new WpfPoint(2, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(2, 12), new WpfPoint(5, 12))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(11, 4), new WpfPoint(14, 4))));
                group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new WpfPoint(14, 4), new WpfPoint(14, 7))));
            }
            else
            {
                group.Children.Add(new GeometryDrawing(brush, pen, new RectangleGeometry(new WpfRect(2, 2, 12, 12))));
                group.Children.Add(new GeometryDrawing(Brushes.White, null, new LineGeometry(new WpfPoint(5, 6), new WpfPoint(11, 6))));
                group.Children.Add(new GeometryDrawing(Brushes.White, null, new LineGeometry(new WpfPoint(5, 9), new WpfPoint(11, 9))));
            }
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
