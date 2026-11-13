using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadStartupSelfTest
    {
        internal static async Task BeforeWindowAsync(string root)
        {
            using var client = new CadClient();
            client.New();
            var state = client.State;
            if (state.ActiveId == 0 || state.Documents.Count != 1 || state.Layers.Count == 0 || state.Layouts.Count != 3)
                throw new InvalidOperationException("Cold new drawing did not create an active document before the viewport attached");
            client.Command("LINE"); client.Command("0,0"); client.Command("1000,0"); client.Command("");
            if (client.State.EntityCount != 1) throw new InvalidOperationException("Cold new drawing cannot draw a line");
            var path = Path.Combine(root, "冷启动首次新建.dwg");
            await client.SaveAsync(path);
            client.Close(client.State.ActiveId);
            await client.OpenAsync(path);
            if (client.State.EntityCount != 1) throw new InvalidOperationException("Cold new drawing did not save and reopen");
            File.WriteAllText(Path.Combine(root, "cad-cold-new-result.txt"), "CAD_COLD_NEW_OK beforeViewport line save reopen");
        }

        internal static async Task InWindowAsync(MainWindow window, string root, Action<Window, string> capture)
        {
            var cad = (CadWorkspace)window.FindName("CadEditor");
            var client = cad.Client;
            var viewport = (CadViewport)cad.FindName("Viewport");
            var input = (TextBox)cad.FindName("CommandInput");
            var ribbon = (TabControl)cad.FindName("EditTools");
            void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Ready()
            {
                var state = client.State;
                Check(state.ActiveId != 0 && state.Documents.Count > 0 && state.Layers.Any(layer => layer.Current), "New entry has no active document or current layer");
                Check(((ListBox)cad.FindName("Documents")).SelectedItem is CadDrawing document && document.Id == state.ActiveId,
                    "New drawing tab did not activate");
            }
            void Command(string text)
            {
                input.Text = text;
                var source = PresentationSource.FromVisual(input);
                if (source == null) throw new InvalidOperationException("Command field is not attached for '" + text + "', workspace="
                    + ((TabControl)window.FindName("Workspace")).SelectedIndex + ", visible=" + cad.IsVisible + ", loaded=" + cad.IsLoaded);
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(input.Text.Length == 0, "New drawing command did not accept Space");
            }
            void Point(double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var sx, out var sy));
                var position = new IntPtr((sy << 16) | (sx & 0xffff));
                SendMessage(viewport.Handle, 0x0201, new IntPtr(1), position);
                SendMessage(viewport.Handle, 0x0202, IntPtr.Zero, position);
            }

            Ready();
            Check(((TabControl)window.FindName("Workspace")).SelectedIndex == 0 && cad.IsVisible && viewport.Handle != IntPtr.Zero,
                "Startup did not select and attach the 2D drawing workspace");
            Check(client.State.Documents.Count == 1 && !client.State.Documents[0].Dirty, "Startup did not prepare one clean blank drawing");
            capture(window, Path.Combine(root, "cold-new-initial.png"));
            Click(Buttons((DependencyObject)cad.FindName("CommonTools")).First(button => Equals(button.Tag, "LINE")));
            Point(1000, 1000); Point(4000, 2000); Command("");
            Check(client.State.EntityCount == 1 && client.State.Documents[0].Dirty, "Startup ribbon drawing did not commit mouse input");
            capture(window, Path.Combine(root, "cold-new-drawn.png"));
            var nativeImage = Path.Combine(root, "cold-new-visible-line.png");
            client.Capture(nativeImage);
            var bitmap = BitmapFrame.Create(new Uri(nativeImage), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 2500, 1500, out var lineX, out var lineY));
            var pixels = new byte[5 * 5 * 4];
            new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(lineX - 2, lineY - 2, 5, 5), pixels, 5 * 4, 0);
            Check(Enumerable.Range(0, 25).Any(index => pixels[index * 4] > 180 && pixels[index * 4 + 1] > 180 && pixels[index * 4 + 2] > 180),
                "New drawing line exists in the document but is invisible in the viewport");
            var dwg = Path.Combine(root, "新建入口绘图.dwg");
            await client.SaveAsync(dwg);
            await cad.CloseCurrentAsync();
            Check(client.State.Documents.Count == 0, "Saved startup drawing did not close");

            var previous = new byte[256]; GetKeyboardState(previous);
            var control = new byte[256]; control[0x11] = 0x80;
            try { SetKeyboardState(control); SendMessage(viewport.Handle, 0x0100, new IntPtr('N'), new IntPtr(1)); }
            finally { SetKeyboardState(previous); }
            Ready();
            await cad.CloseCurrentAsync();
            Check(client.State.Documents.Count == 0, "Empty new drawing unexpectedly required saving");
            Click((Button)window.FindName("NewDrawingQuickAccess")); Ready();
            await cad.CloseCurrentAsync();
            ((MenuItem)window.FindName("NewDrawingFileMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Ready();
            await cad.CloseCurrentAsync();
            Click((Button)cad.FindName("NewDrawingTabButton")); Ready();
            await cad.CloseCurrentAsync();
            ribbon.SelectedItem = ribbon.Items.Cast<TabItem>().First(item => Equals(item.Header, "管理")); window.UpdateLayout(); await Task.Delay(80);
            Click(Buttons((DependencyObject)cad.FindName("FileTools")).OfType<CadIconButton>().First(button => button.Label == "新建")); Ready();
            await cad.CloseCurrentAsync();
            ribbon.SelectedIndex = 0; window.UpdateLayout(); await Task.Delay(80);

            Click(Buttons((DependencyObject)cad.FindName("CommonTools")).First(button => Equals(button.Tag, "CIRCLE")));
            Ready(); Point(2000, 1000); Point(2300, 1000);
            Check(client.State.EntityCount == 1, "Drawing tool did not create a drawable document after closing the last drawing");
            await client.SaveAsync(Path.Combine(root, "空白窗口画圆.dxf"));
            await cad.CloseCurrentAsync();
            Command("NEW"); Ready(); Command("QNEW"); Ready();
            Check(client.State.Documents.Count == 2, "NEW/QNEW reused an existing drawing");
            await cad.CloseCurrentAsync(); await cad.CloseCurrentAsync();
            Command("L"); Ready(); Command("0,0"); Command("1000,0"); Command("");
            Check(client.State.EntityCount == 1, "Empty canvas command line did not prepare a drawable document");
            client.Command("@undo"); Check(client.State.EntityCount == 0, "Fresh drawing undo failed");
            client.Command("@redo"); Check(client.State.EntityCount == 1, "Fresh drawing redo failed");
            await client.SaveAsync(Path.Combine(root, "空白窗口命令画线.dwg"));
            await cad.CloseCurrentAsync();
            await client.OpenAsync(dwg);
            Check(client.State.EntityCount == 1, "New drawing mouse geometry did not survive save/reopen");
            client.Capture(Path.Combine(root, "cold-new-mouse-drawing.png"));
            await cad.CloseCurrentAsync();
            cad.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-new-ui-result.txt"), "CAD_NEW_UI_OK startup clean nativeCtrlN quickAccess tabPlus managementRibbon NEW QNEW emptyCanvasDraw mouseLine circle coordinates undo redo DWG DXF reopen");
        }

        private static IEnumerable<Button> Buttons(DependencyObject root)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is Button button) yield return button;
                foreach (var nested in Buttons(child)) yield return nested;
            }
        }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    }
}
