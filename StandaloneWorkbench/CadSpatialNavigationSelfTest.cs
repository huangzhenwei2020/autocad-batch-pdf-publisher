using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadSpatialNavigationSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr point);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var document = client.State.ActiveId;
            CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
            var host = (CadViewport)workspace.FindName("Viewport");
            var expected = new string[4];
            var viewports = CadNative.PaperViewports(client.Handle).OrderBy(item => item.X).ToArray();

            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }
            CadPaperViewport Current(ulong id) => CadNative.PaperViewports(client.Handle).Single(item => item.Id == id);
            void Mouse(uint message, double x, double y, int keys = 0)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                SendMessage(host.Handle, message, new IntPtr(keys), new IntPtr((py << 16) | (px & 0xffff)));
            }
            void Select(CadPaperViewport viewport)
            {
                workspace.NavigateViewport();
                Mouse(0x0201, viewport.X, viewport.Y, 1);
                Mouse(0x0202, viewport.X, viewport.Y);
            }
            void Pan(CadPaperViewport viewport, bool release = true)
            {
                Mouse(0x0207, viewport.X, viewport.Y, 16);
                Mouse(0x0200, viewport.X, viewport.Y + 20, 16);
                if (release) Mouse(0x0208, viewport.X, viewport.Y + 20);
            }
            void Wheel(CadPaperViewport viewport)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, viewport.X + 20, viewport.Y + 15, out var x, out var y));
                var point = new ScreenPoint { X = x, Y = y };
                ClientToScreen(host.Handle, ref point);
                SendMessage(host.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr((point.Y << 16) | (point.X & 0xffff)));
            }
            void Lock(CadPaperViewport viewport, bool locked)
            {
                var parameters = new CadViewportParameters
                {
                    X = viewport.X, Y = viewport.Y, Width = viewport.Width, Height = viewport.Height,
                    ModelX = viewport.ModelX, ModelY = viewport.ModelY, Ratio = viewport.Ratio,
                    Twist = viewport.Twist, Locked = locked ? 1 : 0
                };
                CadNative.Check(CadNative.wl_viewport_apply(client.Handle, client.State.ActiveId, viewport.Id, ref parameters, viewport.Frozen.ToArray(), viewport.Frozen.Count));
                Check(Math.Abs(Current(viewport.Id).ModelZ - viewport.ModelZ) < 1e-7, "Viewport parameter edit flattened Z");
            }
            bool SameCenter(CadPaperViewport a, CadPaperViewport b) =>
                Math.Abs(a.ModelX - b.ModelX) + Math.Abs(a.ModelY - b.ModelY) + Math.Abs(a.ModelZ - b.ModelZ) < 1e-6;

            client.Command("@fit");
            for (var index = 0; index < viewports.Length; ++index)
            {
                var viewport = Current(viewports[index].Id);
                Lock(viewport, false);
                Select(viewport);
                Pan(viewport);
                var panned = Current(viewport.Id);
                Check(!SameCenter(viewport, panned), "Spatial content pan did not move");
                if (index == 0)
                    Check(Math.Abs(panned.ModelZ - viewport.ModelZ) > 100 && Math.Abs(panned.ModelX - viewport.ModelX) < 1e-7 &&
                        Math.Abs(panned.ModelY - viewport.ModelY) < 1e-7, "Elevation pan failed to commit a pure Z change");
                Wheel(viewport);
                var zoomed = Current(viewport.Id);
                Check(Math.Abs(zoomed.Ratio - viewport.Ratio / 1.2) < 1e-7 && zoomed.X == viewport.X &&
                    zoomed.Y == viewport.Y && zoomed.Width == viewport.Width && zoomed.Height == viewport.Height,
                    "Spatial navigation changed frame or scale incorrectly");
                client.Command("@cancel");
                client.Command("@undo"); client.Command("@undo");
                Check(SameCenter(Current(viewport.Id), viewport), "Spatial pan/zoom undo failed");
                client.Command("@redo"); client.Command("@redo");
                Check(SameCenter(Current(viewport.Id), zoomed), "Spatial pan/zoom redo failed");

                Select(zoomed); Pan(zoomed, false);
                SendMessage(host.Handle, 0x0100, new IntPtr(27), IntPtr.Zero);
                Mouse(0x0208, zoomed.X, zoomed.Y + 20);
                Check(SameCenter(Current(viewport.Id), zoomed), "Escape left a spatial navigation preview");
                Select(zoomed); Pan(zoomed, false);
                SendMessage(host.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
                Mouse(0x0200, zoomed.X, zoomed.Y + 40, 16);
                Mouse(0x0208, zoomed.X, zoomed.Y + 40);
                Check(SameCenter(Current(viewport.Id), zoomed), "Capture loss left spatial navigation active");
                Lock(zoomed, true);
                Select(zoomed); Pan(zoomed); Wheel(zoomed); client.Command("@cancel");
                Check(SameCenter(Current(viewport.Id), zoomed) && Math.Abs(Current(viewport.Id).Ratio - zoomed.Ratio) < 1e-7,
                    "Locked spatial viewport changed");
                var saved = Current(viewport.Id);
                expected[index] = string.Join(" ", new[] { saved.ModelX, saved.ModelY, saved.ModelZ, saved.Ratio }
                    .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            }
            File.WriteAllLines(Path.Combine(root, "spatial-navigation-center.txt"), expected);
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "非俯视导航" + extension));
            workspace.RefreshState(); await Task.Delay(100);
            capture(Window.GetWindow(workspace), Path.Combine(root, "spatial-navigation.png"));

            var unclipped = Current(viewports[0].Id);
            CadNative.Check(CadNative.wl_viewport_clip(client.Handle, document, unclipped.Id, 0));
            foreach (var point in new[] { (30d,40d), (200d,40d), (200d,130d), (30d,130d) })
                CadNative.Check(CadNative.wl_point(client.Handle, point.Item1, point.Item2));
            client.Command("");
            var clipped = Current(unclipped.Id);
            Check(clipped.ClipCount == 4 && Math.Abs(clipped.ModelZ - (unclipped.ModelZ - 15 * unclipped.Ratio)) < 1e-6 &&
                Math.Abs(clipped.ModelX - (unclipped.ModelX - 35 * unclipped.Ratio)) < 1e-6,
                "Spatial clipping changed the visible model position");
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "非俯视裁剪" + extension));
            client.Command("@undo");
            Check(SameCenter(Current(unclipped.Id), unclipped), "Spatial clipping undo lost the center");

            await client.OpenAsync(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "perspective-viewport.dwg"));
            var perspectiveDocument = client.State.ActiveId;
            CadNative.Check(CadNative.wl_layout(client.Handle, 1));
            client.Command("@fit");
            var perspective = CadNative.PaperViewports(client.Handle).Single(item => item.Perspective);
            Lock(perspective, false);
            Select(perspective); Pan(perspective); Wheel(perspective); client.Command("@cancel");
            Check(SameCenter(Current(perspective.Id), perspective) && Current(perspective.Id).Ratio == perspective.Ratio,
                "Perspective viewport used orthographic navigation");
            await client.SaveAsync(Path.Combine(root, "透视视口保护.dwg"));
            client.Close(perspectiveDocument); client.Activate(document); workspace.RefreshState();
        }
    }
}
