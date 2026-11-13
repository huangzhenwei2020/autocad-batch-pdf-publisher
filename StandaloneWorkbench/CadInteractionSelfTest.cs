using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadInteractionSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            var input = (TextBox)workspace.FindName("CommandInput");
            var host = (CadViewport)workspace.FindName("Viewport");
            var boxNumber = 0;
            void Space(string text)
            {
                input.Text = text;
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input), 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(input.Text.Length == 0, "Space did not submit the command field");
            }
            void Mouse(uint message, double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var sx, out var sy));
                SendMessage(host.Handle, message, message == 0x0201 ? new IntPtr(1) : IntPtr.Zero, new IntPtr((sy << 16) | (sx & 0xffff)));
            }
            void Click(double x, double y) { Mouse(0x0201, x, y); Mouse(0x0202, x, y); }
            void ClickWithMotion(double x, double y, int dx, int dy)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var firstX, out var firstY));
                var first = new IntPtr((firstY << 16) | (firstX & 0xffff));
                var moved = new IntPtr(((firstY + dy) << 16) | ((firstX + dx) & 0xffff));
                SendMessage(host.Handle, 0x0201, new IntPtr(1), first);
                SendMessage(host.Handle, 0x0200, new IntPtr(1), moved);
                SendMessage(host.Handle, 0x0202, IntPtr.Zero, moved);
            }
            void Box(double x1, double y1, double x2, double y2, int dx = 6, int dy = 4)
            {
                var selected = client.State.SelectionCount;
                ClickWithMotion(x1, y1, dx, dy);
                Check(client.State.Prompt.Contains("对角点"), "Mouse movement during the first click prematurely completed the selection rectangle");
                Check(client.State.SelectionCount == selected, "First mouse release committed a two-click selection");
                Mouse(0x0200, x2, y2);
                Check(client.State.Prompt.Contains("对角点"), "Button-free mouse movement lost the pending selection rectangle");
                var previewPath = Path.Combine(root, "box-" + (++boxNumber) + (x2 < x1 ? "-crossing-released.png" : "-window-released.png"));
                client.Capture(previewPath);
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, (x1 + x2) / 2, (3 * y1 + y2) / 4, out var px, out var py));
                var bitmap = BitmapFrame.Create(new Uri(previewPath), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                var pixels = new byte[4];
                new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(px, py, 1, 1), pixels, 4, 0);
                Check(x2 < x1 ? pixels[1] > pixels[0] + 20 : pixels[0] > pixels[1] + 20,
                    "Selection rectangle fill disappeared after releasing the mouse");
                Click(x2, y2);
            }
            for (var index = 0; index < 2; index++)
            {
                Space("L"); Space((index * 2000) + ",0"); Space((1000 + index * 2000) + ",0"); Space("");
            }
            Check(client.State.EntityCount == 2, "Space-confirmed coordinate input lost line geometry");
            client.Command("@fit"); await Task.Delay(100);
            Box(-100, -100, 1100, 100);
            Check(client.State.SelectionCount == 1, "Left-to-right enclosure selection failed");
            ClickWithMotion(2500, 0, 6, 4);
            Check(client.State.SelectionCount == 2, "Ordinary clicks did not add to selection");
            var keys = new byte[256]; GetKeyboardState(keys); var shift = (byte[])keys.Clone(); shift[0x10] = 0x80;
            try { SetKeyboardState(shift); Click(250, 0); }
            finally { SetKeyboardState(keys); }
            Check(client.State.SelectionCount == 1, "Shift click did not remove an object");
            client.Command("@cancel");
            Box(900, -100, 1100, 100);
            Check(client.State.SelectionCount == 0, "Enclosure selected a partially enclosed line");
            Box(1100, 100, 900, -100);
            Check(client.State.SelectionCount == 1, "Right-to-left crossing selection failed");
            client.Command("@cancel");
            Box(-100, -100, 1100, 100, 180, 80);
            Check(client.State.SelectionCount == 1, "Long held-button movement completed the rectangle before the second click");
            client.Command("@cancel"); Click(-100, -100); client.Command("@cancel");
            Check(client.State.SelectionCount == 0 && !client.State.Prompt.Contains("对角点"), "Esc left an active selection rectangle");
            Space("M");
            Check(client.State.Prompt.Contains("选择对象"), "Command-first move did not ask for selection");
            Box(-100, -100, 3100, 100);
            Check(client.State.SelectionCount == 2, "Command-first selection window lost targets");
            SendMessage(host.Handle, 0x0100, new IntPtr(0x20), IntPtr.Zero);
            Space("0,0"); Space("0,500");
            Check(client.State.EntityCount == 2, "Command-first move changed entity count");
            client.Command("@undo"); Check(client.State.CanRedo, "Move could not be undone");
            client.Command("@cancel"); Space("");
            Check(client.State.Prompt.Contains("选择对象"), "Empty Space did not repeat the previous move command");
            client.Command("@cancel");
            Check(client.State.EntityCount == 2, "Esc removed committed geometry");
            client.Close(document); client.Activate(original); workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-interaction-result.txt"), "CAD_INTERACTION_OK Space coordinates repeat twoClick firstClickMotion heldMotion releaseRetainsRectangle buttonFreePreview blueWindow greenCrossing add shiftRemove commandFirst Esc undo");
        }
        private static void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    }
}
