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

        public static void InstallWhenReady()
        {
            if (_idleHandler != null) return;
            _idleHandler = (sender, args) =>
            {
                try
                {
                    var ribbon = ComponentManager.Ribbon;
                    if (ribbon == null) return;
                    var existing = ribbon.FindTab(TabId);
                    if (existing != null && existing.Panels.Count > 0 && existing.Panels[0].Source.Items.Count == 5 && existing.Panels[0].Source.Items[0].Text.IndexOf("BPP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        existing.IsVisible = true;
                        return;
                    }
                    if (existing != null) ribbon.Tabs.Remove(existing);
                    var tab = new RibbonTab { Id = TabId, Title = "BPP_批量打印" };
                    var source = new RibbonPanelSource { Title = "批量打印" };
                    var panel = new RibbonPanel { Source = source };
                    source.Items.Add(CreateButton("打开面板（BPP）", "BPP ", "panel"));
                    source.Items.Add(CreateButton("创建图框（TKK）", "TKK ", "frame"));
                    source.Items.Add(CreateButton("插入目录（ML1）", "ML1 ", "catalog"));
                    source.Items.Add(CreateButton("批量改属性（SBB）", "SBB ", "attribute"));
                    source.Items.Add(CreateButton("属性定义编辑", "BPPATTDEF ", "attribute"));
                    tab.Panels.Add(panel);
                    ribbon.Tabs.Add(tab);
                }
                catch (Exception exception) { Trace(exception); }
            };
            Application.Idle += _idleHandler;
            // A freshly started AutoCAD may already have a Ribbon when NETLOAD
            // completes; install immediately as well as on subsequent idle ticks.
            _idleHandler(null, EventArgs.Empty);
        }

        private static void Trace(Exception exception)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPdfPublisher.ui.log"), DateTime.Now.ToString("O") + " Ribbon: " + exception + Environment.NewLine); } catch { }
        }

        public static void Remove()
        {
            if (_idleHandler != null)
            {
                Application.Idle -= _idleHandler;
                _idleHandler = null;
            }
            var ribbon = ComponentManager.Ribbon;
            var tab = ribbon?.FindTab(TabId);
            if (tab != null) ribbon.Tabs.Remove(tab);
        }

        private static RibbonButton CreateButton(string text, string command, string icon)
        {
            var image = CreateIcon(icon);
            var description = command.Trim().Equals("SBB", StringComparison.OrdinalIgnoreCase)
                ? "框选不同类型的属性图块，按坐标排序、批量递增并写入同一属性标记。"
                : command.Trim().Equals("BPPATTDEF", StringComparison.OrdinalIgnoreCase)
                    ? "拾取图块后修改图块名称、属性 TAG、默认内容、字体、字高、宽度和对齐方式。"
                : command.Trim().Equals("TKK", StringComparison.OrdinalIgnoreCase)
                    ? "按纸张、方向和比例在当前图纸中创建标准图框。"
                    : command.Trim().Equals("ML1", StringComparison.OrdinalIgnoreCase)
                        ? "根据当前工程图纸顺序生成目录表并插入 CAD。"
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
            if (kind == "frame")
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
