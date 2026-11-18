using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadInlineViewportSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void Line(double x, double y, double endX, double endY)
            {
                client.Command("LINE"); Point(x, y); Point(endX, endY); client.Command(""); client.Command("@cancel");
            }
            (int X, int Y) Screen(double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                return (px, py);
            }
            void Click(int x, int y, bool twice = false)
            {
                var position = new IntPtr((y << 16) | (x & 0xffff));
                SendMessage(host.Handle, 0x0201, new IntPtr(1), position);
                SendMessage(host.Handle, 0x0202, IntPtr.Zero, position);
                if (twice)
                {
                    SendMessage(host.Handle, 0x0203, new IntPtr(1), position);
                    SendMessage(host.Handle, 0x0202, IntPtr.Zero, position);
                }
            }
            void Pan((int X, int Y) point, bool cancel = false, bool undo = false)
            {
                SendMessage(host.Handle, 0x0207, new IntPtr(16), new IntPtr((point.Y << 16) | (point.X & 0xffff)));
                SendMessage(host.Handle, 0x0200, new IntPtr(16), new IntPtr(((point.Y + 10) << 16) | ((point.X + 20) & 0xffff)));
                if (cancel) SendMessage(host.Handle, 0x0100, new IntPtr(27), IntPtr.Zero);
                if (undo)
                {
                    client.Command("@undo");
                    SendMessage(host.Handle, 0x0200, new IntPtr(16), new IntPtr(((point.Y + 20) << 16) | ((point.X + 40) & 0xffff)));
                }
                SendMessage(host.Handle, 0x0208, IntPtr.Zero, new IntPtr(((point.Y + 10) << 16) | ((point.X + 20) & 0xffff)));
            }
            void Wheel((int X, int Y) point, int direction)
            {
                var screen = new ScreenPoint { X = point.X, Y = point.Y };
                ClientToScreen(host.Handle, ref screen);
                SendMessage(host.Handle, 0x020A, new IntPtr((direction * 120) << 16), new IntPtr((screen.Y << 16) | (screen.X & 0xffff)));
            }
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            Line(0, 1000, 3000, 1000);
            client.Command("CIRCLE"); Point(1500, 1000); Point(1700, 1000); client.Command("@cancel");
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "原位冻结层", 0, -3));
            var frozen = client.State.Layers.Single(item => item.Name == "原位冻结层").Id;
            Line(0, 1500, 3000, 1500);
            CadNative.Check(CadNative.wl_layer(client.Handle, 0, 1, 0, 1));
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "原位编辑布局"));
            Line(350, 0, 350, 200);
            Line(120, 60, 180, 60);
            var parameters = new CadViewportParameters
            {
                X = 150, Y = 100, Width = 300, Height = 200, ModelX = 1500, ModelY = 1000,
                Ratio = 10, Twist = 30, Locked = 1
            };
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, 0, ref parameters, new[] { frozen }, 1));
            var first = CadNative.PaperViewports(client.Handle).Single();
            parameters.X = 500; parameters.Twist = 0;
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, 0, ref parameters, Array.Empty<uint>(), 0));
            client.Command("@fit"); workspace.RefreshState(); await Task.Delay(120);
            var center = Screen(150, 100);
            var outside = Screen(325, 100);
            var blank = Screen(50, 20);
            var annotation = Screen(150, 60);
            var secondView = Screen(500, 125);
            var oldSecondView = Screen(500, 100);
            capture(Window.GetWindow(workspace), Path.Combine(root, "inline-paper-before.png"));
            Click(center.X, center.Y, true);
            Check(client.State.InlineViewport && client.State.EditingViewport == first.Id, "Double-click did not enter the original viewport");
            var pick = Screen(500, 1000); Click(pick.X, pick.Y);
            Check(client.State.SelectionCount == 1, "Inline model picking missed a rotated line");
            client.Command("MOVE"); Point(0, 0); Point(100, 250);
            client.Command("CIRCLE"); Point(500, 700); Point(600, 700); client.Command("@cancel");
            client.Command("@all"); Check(client.State.SelectionCount == 3, "Inline selection included paper or frozen objects");
            client.Command("@cancel");
            workspace.RefreshState(); await Task.Delay(180);
            capture(Window.GetWindow(workspace), Path.Combine(root, "inline-model-edit.png"));
            SendMessage(host.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero);
            var canvasPath = Path.Combine(root, "inline-pixel-check.png");
            client.Capture(canvasPath);
            var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(canvasPath), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            bool Bright((int X, int Y) point)
            {
                var colors = new byte[5 * 5 * 4];
                bitmap.CopyPixels(new Int32Rect(point.X - 2, point.Y - 2, 5, 5), colors, 20, 0);
                for (var i = 0; i < colors.Length; i += 4)
                    if (colors[i] > 160 && colors[i + 1] > 160 && colors[i + 2] > 160) return true;
                return false;
            }
            Check(!Bright(blank), "Inline compositing covered the layout with opaque pixels");
            Check(Bright(annotation), "Paper annotation inside the active viewport disappeared");
            Check(Bright(secondView) && !Bright(oldSecondView), "Model edit was not redrawn in the other viewport");
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "原位模型编辑" + extension));
            Click(outside.X, outside.Y, true);
            Check(client.State.EditingViewport == 0, "Double-click outside did not return to paper space");
            var returned = Screen(150, 100);
            Check(returned == center, "Entering and leaving inline mode moved the paper camera");
            var viewport = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(viewport.Ratio == 10 && viewport.Locked && viewport.Twist == 30, "Inline editing changed locked viewport parameters");
            client.Command("@undo"); client.Command("@redo");
            Click(center.X, center.Y, true);
            workspace.ExecuteCommand("VPMAX");
            Check(!client.State.InlineViewport && client.State.EditingViewport == first.Id, "Inline-to-maximized transition failed");
            workspace.ExecuteCommand("MSPACE");
            Check(client.State.InlineViewport, "Maximized-to-inline transition failed");
            Wheel(Screen(1500, 1000), 1); Wheel(Screen(1500, 1000), -1);
            client.Command("PSPACE");
            viewport = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(viewport.Ratio == 10 && viewport.ModelX == 1500 && viewport.ModelY == 1000, "Locked inline navigation changed viewport scale or center");
            parameters.X = 150; parameters.Twist = 30; parameters.Locked = 0;
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, first.Id, ref parameters, new[] { frozen }, 1));
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            Pan(Screen(1500, 1000));
            client.Command("PSPACE");
            var panned = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(panned.ModelX != 1500 && panned.Ratio == 10, "Unlocked inline pan did not change model center");
            client.Command("@undo");
            Check(CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id).ModelX == 1500, "Inline pan did not undo as one gesture");
            client.Command("@redo");
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            Wheel(Screen(panned.ModelX, panned.ModelY), 1);
            client.Command("PSPACE");
            var zoomed = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(Math.Abs(zoomed.Ratio - 10 / 1.1) < .00001, "Unlocked inline wheel did not update viewport scale");
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            Pan(Screen(zoomed.ModelX, zoomed.ModelY), true);
            var lostCapture = Screen(zoomed.ModelX, zoomed.ModelY);
            SendMessage(host.Handle, 0x0200, new IntPtr(16), new IntPtr(((lostCapture.Y + 15) << 16) | ((lostCapture.X + 30) & 0xffff)));
            client.Command("PSPACE");
            var cancelled = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(cancelled.ModelX == zoomed.ModelX && cancelled.ModelY == zoomed.ModelY, "Esc did not restore unfinished inline pan");
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            Pan(Screen(zoomed.ModelX, zoomed.ModelY), undo: true);
            client.Command("PSPACE");
            var undone = CadNative.PaperViewports(client.Handle).Single(item => item.Id == first.Id);
            Check(undone.ModelX == panned.ModelX && undone.ModelY == panned.ModelY && undone.Ratio == 10, "Movement after undo changed a cancelled inline gesture");
            client.Command("@redo");
            File.WriteAllLines(Path.Combine(root, "inline-navigation-center.txt"), new[] {
                zoomed.ModelX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                zoomed.ModelY.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
            foreach (var extension in new[] { ".dwg", ".dxf" }) await client.SaveAsync(Path.Combine(root, "原位导航" + extension));

            parameters.Twist = 0; parameters.Locked = 1;
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, first.Id, ref parameters, new[] { frozen }, 1));
            CadNative.Check(CadNative.wl_viewport_clip(client.Handle, document, first.Id, 0));
            Point(0, 0); Point(300, 0); Point(300, 200); Point(150, 200); Point(150, 100); Point(0, 100); client.Command("");
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            client.Command("CIRCLE"); Point(1250, 1600); Point(1300, 1600); client.Command("@cancel");
            client.Command("@all"); Check(client.State.SelectionCount == 3, "Concave inline selection included hidden geometry"); client.Command("@cancel");
            var boxStart = Screen(2250, 1750); var boxEnd = Screen(250, 100);
            Click(boxStart.X, boxStart.Y);
            SendMessage(host.Handle, 0x0200, IntPtr.Zero, new IntPtr((boxEnd.Y << 16) | (boxEnd.X & 0xffff)));
            Click(boxEnd.X, boxEnd.Y);
            Check(client.State.SelectionCount == 3, "Crossing box included geometry in the viewport notch"); client.Command("@cancel");
            foreach (var extension in new[] { ".dwg", ".dxf" }) await client.SaveAsync(Path.Combine(root, "原位凹形裁剪" + extension));
            client.Command("PSPACE");
            CadNative.Check(CadNative.wl_viewport_clip(client.Handle, document, first.Id, 2)); Point(150, 100); Point(240, 100);
            CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, first.Id));
            client.Command("CIRCLE"); Point(2300, 1800); Point(2330, 1800); client.Command("@cancel");
            client.Command("@all"); Check(client.State.SelectionCount == 3, "Circle clipping selected invisible corner geometry"); client.Command("@cancel");
            workspace.RefreshState(); await Task.Delay(120);
            capture(Window.GetWindow(workspace), Path.Combine(root, "inline-circle-clip.png"));
            var window = Window.GetWindow(workspace);
            var previousWidth = window.Width; var previousHeight = window.Height;
            window.Width = 1100; window.Height = 700; window.UpdateLayout(); await Task.Delay(140);
            Check(client.State.InlineViewport, "Resizing lost inline editing mode");
            capture(window, Path.Combine(root, "inline-small-window.png"));
            window.Width = previousWidth; window.Height = previousHeight; window.UpdateLayout(); await Task.Delay(100);
            foreach (var extension in new[] { ".dwg", ".dxf" }) await client.SaveAsync(Path.Combine(root, "原位圆形裁剪" + extension));
            client.Command("XLINE"); Point(-4000, 1000); Point(-3000, 1000); client.Command("@cancel");
            client.Command("RAY"); Point(-4000, 1200); Point(-3000, 1200); client.Command("@cancel");
            client.Command("@all"); Check(client.State.SelectionCount == 5, "Clipping rejected visible construction lines with outside origins"); client.Command("@cancel");
            client.Command("PSPACE");
            client.Close(document);
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                await client.OpenAsync(Path.Combine(root, "原位模型编辑" + extension));
                document = client.State.ActiveId;
                Check(client.State.EditingViewport == 0, "Inline editing mode persisted after reopen");
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "原位编辑布局").Id));
                var sourceViewport = CadNative.PaperViewports(client.Handle).Single(item => item.X == 150);
                CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, sourceViewport.Id));
                workspace.NewDrawing();
                var scratch = client.State.ActiveId;
                client.Activate(document); client.Close(scratch);
                Check(client.State.InlineViewport, "Document switching lost inline editing mode");
                Line(100, 1200, 1000, 1200);
                await client.SaveAsync(Path.Combine(root, "原位原图编辑" + extension));
                client.Command("PSPACE"); client.Close(document);
            }
            client.Activate(original); workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-inline-edit-result.txt"), "CAD_INLINE_EDIT_OK doubleClick model paper rotation freeze pixels concave circle selection navigation undo sourceSave documentSwitch resize");
        }
    }
}
