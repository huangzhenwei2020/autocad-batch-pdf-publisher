using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadCursorSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            var input = (TextBox)workspace.FindName("CommandInput");
            Check(input.Cursor == Cursors.IBeam, "Command input must use the text cursor");
            using (var empty = new CadClient()) Check(empty.State.CursorMode == "arrow", "Empty document cursor must be the arrow");
            client.Command("L"); client.Command("1000,1000"); client.Command("4000,2000"); client.Command("");
            client.Command("@fit");
            await Task.Delay(100);
            var x = 120;
            var y = 120;
            var imageNumber = 0;
            void Mouse(uint message, int px, int py, int buttons = 0)
            {
                SendMessage(host.Handle, message, new IntPtr(buttons), new IntPtr((py << 16) | (px & 0xffff)));
            }
            void Cursor(string mode, int systemId = 0)
            {
                Check(client.State.CursorMode == mode, "Unexpected cursor state " + client.State.CursorMode + ", expected " + mode);
                SendMessage(host.Handle, 0x0020, host.Handle, new IntPtr((0x0200 << 16) | 1));
                Check(GetCursor() == (systemId == 0 ? IntPtr.Zero : LoadCursor(IntPtr.Zero, new IntPtr(systemId))), "System cursor did not match " + mode);
            }
            BitmapSource Capture(string name)
            {
                var path = Path.Combine(root, "cursor-" + (++imageNumber) + "-" + name + ".png");
                client.Capture(path);
                return new FormatConvertedBitmap(BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
            }
            bool White(BitmapSource bitmap, int px, int py)
            {
                var pixels = new byte[3 * 3 * 4];
                bitmap.CopyPixels(new Int32Rect(px - 1, py - 1, 3, 3), pixels, 12, 0);
                for (var index = 0; index < pixels.Length; index += 4)
                    if (pixels[index] > 210 && pixels[index + 1] > 210 && pixels[index + 2] > 210) return true;
                return false;
            }
            Mouse(0x0200, x, y);
            Cursor("idle");
            var idle = Capture("idle");
            Check(White(idle, x + 13, y) && White(idle, x + 6, y + 6), "Idle cursor lost its crosshair or pickbox");
            client.Command("M"); Cursor("select");
            var select = Capture("selection");
            Check(!White(select, x + 13, y) && White(select, x + 6, y + 6), "Object selection must display the pickbox alone");
            Mouse(0x0201, x, y, 1); Mouse(0x0202, x, y); Cursor("point");
            Check(client.State.Prompt.Contains("对角点"), "Selection rectangle did not request the second corner");
            client.Command("@cancel");
            client.Command("L"); Cursor("point");
            var point = Capture("point");
            Check(White(point, x + 13, y) && !White(point, x + 6, y + 6), "Point input must display the crosshair alone");
            client.Command("2000,2500");
            Mouse(0x0207, x, y, 0x10); Cursor("pan", 32646);
            var pan = Capture("pan");
            Check(!White(pan, x + 13, y) && !White(pan, x + 6, y + 6), "Panning left a CAD cursor underneath the move cursor");
            Mouse(0x0200, x + 30, y + 20, 0x10);
            Mouse(0x0208, x + 30, y + 20); Cursor("point");
            client.Command("3000,2500"); client.Command("");
            Check(client.State.EntityCount == 2, "Middle pan lost an active drawing command");
            client.Command("L");
            Mouse(0x0207, x, y, 0x10);
            SendMessage(host.Handle, 0x001f, IntPtr.Zero, IntPtr.Zero);
            Cursor("point");
            client.Command("2000,3000"); client.Command("3000,3000"); client.Command("");
            Check(client.State.EntityCount == 3, "Capture cancellation left the drawing tool suspended");
            client.Command("L");
            Mouse(0x0207, x, y, 0x10);
            SetCapture(IntPtr.Zero); Cursor("point");
            Mouse(0x0207, x, y, 0x10);
            input.Focus(); Cursor("point");
            client.Command("@cancel");
            Mouse(0x0209, x, y, 0x10); Cursor("idle");
            Mouse(0x0208, x, y);
            client.Command("PAN"); Cursor("pan", 32646);
            client.Command(""); Cursor("idle");
            Mouse(0x0200, x, y);
            SendMessage(host.Handle, 0x02a3, IntPtr.Zero, IntPtr.Zero);
            Check(!client.State.PointerInside, "Mouse leave did not clear the viewport cursor");
            var outside = Capture("outside");
            Check(!White(outside, x + 13, y) && !White(outside, x + 6, y + 6), "Viewport retained its cursor over text inputs");
            Mouse(0x0200, x, y); Cursor("idle");
            CadNative.Check(CadNative.wl_busy(client.Handle, 1)); Cursor("busy", 32514);
            CadNative.Check(CadNative.wl_busy(client.Handle, 0)); Cursor("idle");

            // A normal grip drop must commit before capture-loss cleanup runs.
            client.Command("@cancel");
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 2500, 1500, out var lineX, out var lineY));
            Mouse(0x0201, lineX, lineY, 1); Mouse(0x0202, lineX, lineY);
            Check(client.State.SelectionCount == 1, "Grip fixture line was not selected");
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 1000, 1000, out var gripX, out var gripY));
            Mouse(0x0200, gripX, gripY);
            Mouse(0x0201, gripX, gripY, 1); Mouse(0x0202, gripX, gripY);
            Mouse(0x0200, gripX + 35, gripY + 25); Cursor("move", 32646);
            Mouse(0x0201, gripX + 35, gripY + 25, 1);
            Mouse(0x0202, gripX + 35, gripY + 25); Cursor("idle");
            var changedX = client.State.Properties.First(item => item.Name == "起点 X").Value;
            Check(Math.Abs(double.Parse(changedX, System.Globalization.CultureInfo.InvariantCulture) - 1000) > .1, "Capture cleanup discarded the grip drop");
            client.Command("@undo");
            Check(client.State.CanRedo, "Grip drop could not be undone");
            client.Command("@cancel");
            Mouse(0x0207, x, y, 0x10);
            client.Activate(original); Cursor("idle");
            Check(GetCapture() == IntPtr.Zero, "Document switching retained mouse capture");
            client.Close(document);
            workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-cursor-result.txt"), "CAD_CURSOR_OK idle point select pan release cancel leave reenter busy text gripDrop undo documentSwitch");
        }

        private static void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern IntPtr GetCursor();
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetCapture();
        [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    }
}
