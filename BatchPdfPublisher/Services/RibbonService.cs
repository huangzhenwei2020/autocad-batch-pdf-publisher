using System;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using System.Windows.Media;
using WpfRect = System.Windows.Rect;
using WpfPoint = System.Windows.Point;

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
                var source = new RibbonPanelSource { Title = "图纸与说明" };
                var panel = new RibbonPanel { Source = source };
                source.Items.Add(CreateButton("打开面板（BPP）", "BPP ", "panel"));
                source.Items.Add(CreateButton("创建图框（TKK）", "TKK ", "frame"));
                source.Items.Add(CreateButton("插入目录（ML1）", "ML1 ", "catalog"));
                source.Items.Add(CreateButton("批量改属性（SBB）", "SBB ", "attribute"));
                source.Items.Add(CreateButton("属性定义编辑（BPA）", "BPA ", "attribute"));
                source.Items.Add(CreateButton("建筑说明（JZSM）", "WLJZSM ", "spec"));
                source.Items.Add(CreateButton("楼梯大样（LTDY）", "WLLTDY ", "stair"));
                source.Items.Add(CreateButton("制图标准（BZS）", "BZS ", "standard"));
                source.Items.Add(CreateButton("比例管理（BL1）", "BL1 ", "scale"));
                source.Items.Add(CreateButton("门窗立面（MCLM）", "MCLM ", "doorwindow"));
                source.Items.Add(CreateButton("房间改名（FJGM）", "FJGM ", "room"));
                tab.Panels.Add(panel);
                ribbon.Tabs.Add(tab);
                _installed = true;
                Trace("Ribbon 标签已创建：万落建筑工具，按钮数=" + source.Items.Count);
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
            return tab != null
                && tab.Panels.Count > 0
                && tab.Panels[0].Source != null
                && tab.Panels[0].Source.Items.Count == 11
                && tab.Panels[0].Source.Items[0].Text.IndexOf("BPP", StringComparison.OrdinalIgnoreCase) >= 0
                && tab.Panels[0].Source.Items[7].Text.IndexOf("BZS", StringComparison.OrdinalIgnoreCase) >= 0
                && tab.Panels[0].Source.Items[8].Text.IndexOf("BL1", StringComparison.OrdinalIgnoreCase) >= 0
                && tab.Panels[0].Source.Items[9].Text.IndexOf("MCLM", StringComparison.OrdinalIgnoreCase) >= 0
                && tab.Panels[0].Source.Items[10].Text.IndexOf("FJGM", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Trace(Exception exception)
        {
            Trace("Ribbon 异常：" + exception);
        }

        private static void Trace(string message)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.ui.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { }
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

        private static RibbonButton CreateButton(string text, string command, string icon)
        {
            var image = CreateIcon(icon);
            var description = command.Trim().Equals("SBB", StringComparison.OrdinalIgnoreCase)
                ? "框选不同类型的属性图块，按坐标排序、批量递增并写入同一属性标记。"
                : command.Trim().Equals("BPA", StringComparison.OrdinalIgnoreCase)
                    ? "拾取图块后修改图块名称、属性 TAG、默认内容、字体、字高、宽度和对齐方式。"
                : command.Trim().Equals("TKK", StringComparison.OrdinalIgnoreCase)
                    ? "按纸张、方向和比例在当前图纸中创建标准图框。"
                    : command.Trim().Equals("ML1", StringComparison.OrdinalIgnoreCase)
                        ? "根据当前工程图纸顺序生成目录表并插入 CAD。"
                        : command.Trim().Equals("WLJZSM", StringComparison.OrdinalIgnoreCase)
                            ? "打开万落建筑工具中的建筑设计说明助手。"
                        : command.Trim().Equals("WLLTDY", StringComparison.OrdinalIgnoreCase)
                            ? "打开楼梯构件编辑器，按楼层、梯段和构造参数一键生成楼梯大样。"
                        : command.Trim().Equals("BZS", StringComparison.OrdinalIgnoreCase)
                            ? "检查并补齐万落工具共用的图层、文字样式和标注样式。"
                        : command.Trim().Equals("BL1", StringComparison.OrdinalIgnoreCase)
                            ? "把所选对象转换到指定图纸比例，或按目标比例连续刷对象。"
                        : command.Trim().Equals("FJGM", StringComparison.OrdinalIgnoreCase)
                            ? "以一个天正房间为样板，仅批量修改原名称和使用面积都相同的房间。"
                        : command.Trim().Equals("MCLM", StringComparison.OrdinalIgnoreCase)
                            ? "读取天正门窗表，校验编号和洞口尺寸，并批量设置门窗立面分格与开启参数。"
                            : "打开工程 DWG 管理、图框扫描和批量 PDF 发布面板。";
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
