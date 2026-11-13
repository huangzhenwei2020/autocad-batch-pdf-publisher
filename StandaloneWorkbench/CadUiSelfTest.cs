using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadUiSelfTest
    {
        internal static async Task RunAsync(MainWindow window, CadWorkspace cad, string root, Action<Window, string> capture)
        {
            var viewport = (CadViewport)cad.FindName("Viewport");
            var ribbon = (TabControl)cad.FindName("EditTools");
            var baseline = viewport.ActualWidth;
            var title = (FrameworkElement)window.FindName("CaptionTitle");
            var captionPoint = title.PointToScreen(new Point(title.ActualWidth / 2, title.ActualHeight / 2));
            var nativeWindow = new WindowInteropHelper(window).Handle;
            foreach (TabItem tab in ribbon.Items)
            {
                ribbon.SelectedItem = tab; await LayoutAsync(window);
                foreach (var button in Descendants<CadIconButton>(ribbon))
                {
                    Check(Descendants<CadIcon>(button).All(icon => icon.Source != null), "Ribbon tool icon was not loaded");
                    foreach (var label in Descendants<TextBlock>(button))
                        Check(label.DesiredSize.Width <= button.ActualWidth + 1, "Ribbon label is clipped " + button.Label);
                }
                if (Equals(tab.Header, "绘图") || Equals(tab.Header, "修改") || Equals(tab.Header, "标注") || Equals(tab.Header, "插入"))
                    capture(window, Path.Combine(root, "cad-tools-" + tab.Header + ".png"));
            }
            ribbon.SelectedIndex = 0; await LayoutAsync(window);
            var captionHit = SendMessage(nativeWindow, 0x0084, IntPtr.Zero, new IntPtr(((int)captionPoint.Y << 16) | ((int)captionPoint.X & 0xffff)));
            Check(captionHit.ToInt32() == 2, "Custom title bar cannot drag the window");
            var maximize = (CadIconButton)window.FindName("MaximizeButton");
            var buttonPoint = maximize.PointToScreen(new Point(maximize.ActualWidth / 2, maximize.ActualHeight / 2));
            Check(SendMessage(nativeWindow, 0x0084, IntPtr.Zero, new IntPtr(((int)buttonPoint.Y << 16) | ((int)buttonPoint.X & 0xffff))).ToInt32() == 1, "Title-bar controls are not mouse interactive");
            Click(maximize); await LayoutAsync(window);
            Check(window.WindowState == WindowState.Maximized, "Maximize caption button failed");
            Click(maximize); await LayoutAsync(window);
            Check(window.WindowState == WindowState.Normal, "Restore caption button failed");
            var projectToggle = Descendants<CadIconButton>(window).First(item => Equals(item.ToolTip, "显示／收起项目浏览器"));
            Click(projectToggle); await LayoutAsync(window);
            Check(viewport.ActualWidth < baseline - 200, "Project pane did not resize the drawing area");
            Click(projectToggle); await LayoutAsync(window);
            Check(Math.Abs(viewport.ActualWidth - baseline) < 2, "Project pane did not restore the drawing width");

            foreach (var index in Enumerable.Range(0, ribbon.Items.Count))
            {
                ribbon.SelectedIndex = index; await LayoutAsync(window);
                Check(Descendants<CadIcon>(ribbon).All(icon => icon.Source != null), "Ribbon has a missing icon");
            }
            ribbon.SelectedIndex = 0;
            await LayoutAsync(window);
            var projectChoice = (ComboBox)window.FindName("Projects");
            Check(Descendants<TextBlock>(projectChoice).Any(item => item.Text == ((BatchPdfPublisher.Models.ProjectProfile)projectChoice.SelectedItem).Name), "Project selector displays a type name");
            Check(Descendants<TextBlock>(ribbon).Any(item => item.Text == cad.Client.State.Layers.First(layer => layer.Current).Name), "Layer selector displays a type name");
            var sourceDocument = cad.Client.State.ActiveId;
            cad.NewDrawing();
            var propertyDocument = cad.Client.State.ActiveId;
            cad.Client.Command("LINE"); cad.Client.Command("0,0"); cad.Client.Command("1000,0");
            cad.Client.Command("@cancel");
            cad.Client.Command("@all"); cad.RefreshState();
            Shortcut(viewport, '1', true); await LayoutAsync(window);
            Check(((FrameworkElement)cad.FindName("InspectorPanel")).IsVisible, "Ctrl+1 did not show properties");
            Check(((DataGrid)cad.FindName("Properties")).Items.Count > 0, "Selected entities have no property rows");
            capture(window, Path.Combine(root, "cad-properties.png"));
            Shortcut(viewport, '1', true); await LayoutAsync(window);
            Check(!((FrameworkElement)cad.FindName("InspectorPanel")).IsVisible, "Ctrl+1 did not hide properties");
            cad.Client.Close(propertyDocument); cad.Client.Activate(sourceDocument); cad.RefreshState();
            Shortcut(viewport, 0x71, false); await LayoutAsync(window);
            Check(((FrameworkElement)cad.FindName("History")).IsVisible, "F2 did not show command history");
            Shortcut(viewport, 0x71, false); await LayoutAsync(window);
            Check(!((FrameworkElement)cad.FindName("History")).IsVisible, "F2 did not hide command history");
            var height = viewport.ActualHeight;
            Shortcut(viewport, 0x70, true); await LayoutAsync(window);
            Check(viewport.ActualHeight > height + 65, "Ctrl+F1 did not release ribbon height");
            capture(window, Path.Combine(root, "cad-ribbon-collapsed.png"));
            Shortcut(viewport, 0x70, true); await LayoutAsync(window);
            Shortcut(viewport, '0', true); await LayoutAsync(window);
            Check(viewport.ActualHeight > height + 65, "Ctrl+0 did not enter clean-screen mode");
            Shortcut(viewport, '0', true); await LayoutAsync(window);
            Check(Math.Abs(viewport.ActualHeight - height) < 2, "Clean-screen exit changed the original ribbon state");
            cad.Client.Command("@cancel");

            var documents = cad.Client.State.Documents.Count;
            var original = cad.Client.State.ActiveId;
            var nextDocument = cad.Client.State.Documents.First().Id;
            cad.NewDrawing();
            var extra = cad.Client.State.ActiveId;
            Shortcut(viewport, 0x09, true); await LayoutAsync(window);
            Check(cad.Client.State.ActiveId == nextDocument, "Ctrl+Tab did not switch documents");
            cad.Client.Close(extra); cad.Client.Activate(original); cad.RefreshState();
            Check(cad.Client.State.Documents.Count == documents, "Document switching changed the open set");

            var measurements = new List<object>();
            foreach (var size in new[] { new Size(1440, 900), new Size(1000, 700), new Size(1818, 1169) })
            {
                window.Width = size.Width; window.Height = size.Height;
                await LayoutAsync(window);
                var canvas = viewport.TransformToAncestor(window).TransformBounds(new Rect(viewport.RenderSize));
                var command = ((FrameworkElement)cad.FindName("CommandDock")).TransformToAncestor(window).TransformBounds(new Rect(((FrameworkElement)cad.FindName("CommandDock")).RenderSize));
                var status = ((FrameworkElement)cad.FindName("StatusDock")).TransformToAncestor(window).TransformBounds(new Rect(((FrameworkElement)cad.FindName("StatusDock")).RenderSize));
                Check(Math.Abs(canvas.Left - command.Left) < 2 && Math.Abs(canvas.Right - command.Right) < 2, "Command and canvas edges differ");
                Check(Math.Abs(canvas.Left - status.Left) < 2 && Math.Abs(canvas.Right - status.Right) < 2, "Status and canvas edges differ");
                var content = (FrameworkElement)window.Content;
                var ratio = canvas.Width * canvas.Height / (content.ActualWidth * content.ActualHeight);
                Check(ratio > (size.Width == 1000 ? .59 : .68), "Drawing area ratio below the CAD layout target");
                measurements.Add(new { Width = size.Width, Height = size.Height, CanvasWidth = canvas.Width, CanvasHeight = canvas.Height, CanvasRatio = ratio });
                capture(window, Path.Combine(root, "cad-ui-" + (int)size.Width + ".png"));
            }
            File.WriteAllText(Path.Combine(root, "cad-ui-result.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            window.Width = 1440; window.Height = 900; await LayoutAsync(window);
        }

        private static async Task LayoutAsync(Window window) { window.UpdateLayout(); await Task.Delay(100); window.UpdateLayout(); }
        private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T result) yield return result;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        private static void Shortcut(CadViewport viewport, int key, bool control)
        {
            var previous = new byte[256]; GetKeyboardState(previous);
            var keys = new byte[256]; if (control) keys[0x11] = 0x80;
            try { SetKeyboardState(keys); SendMessage(viewport.Handle, 0x0100, new IntPtr(key), new IntPtr(1)); }
            finally { SetKeyboardState(previous); }
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    }
}
