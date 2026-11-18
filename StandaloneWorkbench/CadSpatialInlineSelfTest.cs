using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadSpatialInlineSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr position);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }

        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var previous = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            await client.OpenAsync(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "spatial-inline-clip.dwg"));
            var document = client.State.ActiveId;
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            (int X, int Y) Screen(double x, double y, double z)
            {
                CadNative.Check(CadNative.wl_world_to_screen3(client.Handle, x, y, z, out var px, out var py));
                return (px, py);
            }
            void Mouse(uint message, (int X, int Y) pixel, int keys = 0) =>
                SendMessage(host.Handle, message, new IntPtr(keys), new IntPtr((pixel.Y << 16) | (pixel.X & 0xffff)));
            void Click((int X, int Y) pixel, bool twice = false) => CadTestKeyboard.WithModifiers(() =>
            {
                Mouse(0x0200, pixel);
                Mouse(0x0201, pixel, 1); Mouse(0x0202, pixel);
                if (twice) { Mouse(0x0203, pixel, 1); Mouse(0x0202, pixel); }
            });
            CadPaperViewport Viewport() => CadNative.PaperViewports(client.Handle).Single(item => item.ClipCount == 8);
            (int X, int Y) ModelAtPaper(double x, double y) => Screen(100 + (x - 150) * 10, 200, 300 + (y - 100) * 10);
            try
            {
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
                client.Command("@fit");
                var before = Viewport();
                var center = Screen(150, 100, 0);
                var notch = Screen(150, 155, 0);
                Click(center, true);
                Check(client.State.InlineViewport && client.State.EditingViewport == before.Id,
                    "Double-click did not enter the clipped spatial viewport");
                client.Command("@all");
                Check(client.State.SelectionCount == 2, "Select all included a model entity entirely in the notch");
                client.Command("@cancel");
                Click(ModelAtPaper(90, 150));
                Check(client.State.SelectionCount == 1, "The visible part of a clipped line could not be selected");
                client.Command("@cancel");
                Click(ModelAtPaper(182, 164)); Click(ModelAtPaper(118, 134));
                Check(client.State.SelectionCount == 1, "Crossing selection included the hidden line in the notch");
                client.Command("@cancel");

                var count = client.State.EntityCount;
                CadNative.Check(CadNative.wl_workplane(client.Handle, 1, 100, 180, 300));
                client.Command("POINT"); Click(notch); client.Command("@cancel");
                Check(client.State.EntityCount == count, "A click in the notch created geometry");
                if (!client.State.Snap) client.Command("@snap");
                client.Command("LINE");
                var endpoint = ModelAtPaper(60, 150);
                Click((endpoint.X + 2, endpoint.Y - 1));
                client.Command("100,100,0"); client.Command(""); client.Command("@cancel");
                Check(client.State.EntityCount == count + 1, "Inline drawing did not add a line");
                client.Command("@undo");
                Check(client.State.EntityCount == count && client.State.InlineViewport, "Inline drawing undo left the viewport");
                client.Command("@redo");
                client.Command("UCS WORLD");

                Mouse(0x0207, center, 16); Mouse(0x0200, (center.X, center.Y + 20), 16);
                Mouse(0x0208, (center.X, center.Y + 20));
                client.Command("PSPACE");
                var moved = Viewport();
                Check(Math.Abs(moved.ModelX - before.ModelX) < 1e-6 && Math.Abs(moved.ModelY - before.ModelY) < 1e-6 &&
                    Math.Abs(moved.ModelZ - before.ModelZ) > 1, "Vertical inline pan did not update model Z");
                client.Command("MSPACE"); client.Command("@undo");
                var anchor = ModelAtPaper(175, 90);
                var screen = new ScreenPoint { X = anchor.X, Y = anchor.Y };
                ClientToScreen(host.Handle, ref screen);
                Mouse(0x020A, (screen.X, screen.Y), 120 << 16);
                var afterWheel = ModelAtPaper(175, 90);
                Check(Math.Abs(afterWheel.X - anchor.X) <= 1 && Math.Abs(afterWheel.Y - anchor.Y) <= 1,
                    "Inline zoom moved the model point under the cursor");
                client.Command("PSPACE");
                Check(Viewport().Ratio < before.Ratio, "Inline zoom did not update viewport scale");
                client.Command("MSPACE"); client.Command("@undo");
                Click(notch, true);
                Check(client.State.EditingViewport == 0 && Screen(150, 100, 0) == center,
                    "Double-click outside did not restore the paper camera");
                var restored = Viewport();
                Check(Math.Abs(restored.Ratio - before.Ratio) < 1e-8 && Math.Abs(restored.ModelZ - before.ModelZ) < 1e-7,
                    "Undo did not restore spatial viewport navigation");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "非俯视原位裁剪编辑" + extension));
            }
            finally
            {
                client.Close(document);
                client.Activate(previous);
                workspace.RefreshState();
            }
        }
    }
}
