using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadSpatialViewportSelfTest
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr position);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window,string> capture)
        {
            var client=workspace.Client;
            var original=client.State.ActiveId;
            await client.OpenAsync(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"),"spatial-viewport.dwg"));
            var document=client.State.ActiveId;
            CadNative.Check(CadNative.wl_layout(client.Handle,1));
            if (CadNative.PaperViewports(client.Handle).Count!=4) throw new InvalidOperationException("Spatial viewport import count changed");
            client.Command("@fit"); workspace.RefreshState(); await Task.Delay(120);
            capture(Window.GetWindow(workspace),Path.Combine(root,"spatial-viewports.png"));
            var layout=client.State.Layouts.Single(item=>item.Current);
            CadNative.Check(CadNative.wl_layout_update(client.Handle,document,layout.Key,"观察副本",layout.Width,layout.Height,1,1));
            client.Command("@undo"); client.Command("@redo");
            foreach (var extension in new[]{".dwg",".dxf"})
            {
                var path=Path.Combine(root,"非俯视视口"+extension);
                await client.SaveAsync(path); client.Close(document); await client.OpenAsync(path); document=client.State.ActiveId;
                CadNative.Check(CadNative.wl_layout(client.Handle,1));
                if (CadNative.PaperViewports(client.Handle).Count!=4) throw new InvalidOperationException("Spatial viewport roundtrip failed");
            }
            await CadSpatialNavigationSelfTest.RunAsync(workspace, root, capture);
            await CheckMaximizedEditing(workspace, root, capture);
            client.Close(document); client.Activate(original); workspace.RefreshState();
        }

        private static async Task CheckMaximizedEditing(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var previousDocument = client.State.ActiveId;
            await client.OpenAsync(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "spatial-viewport.dwg"));
            var document = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            (int X, int Y) Screen(double x, double y, double z)
            {
                CadNative.Check(CadNative.wl_world_to_screen3(client.Handle, x, y, z, out var px, out var py));
                return (px, py);
            }
            try
            {
                CadNative.Check(CadNative.wl_layout(client.Handle, 1));
                client.Command("@fit");
                var viewports = CadNative.PaperViewports(client.Handle).OrderBy(item => item.X).ToArray();
                Check(viewports.Length == 4 && viewports.All(item => item.CanMaximizeEdit && item.CanInlineEdit),
                    "Non-planar orthographic viewport editing capabilities were not exposed");
                var paper = Screen(150, 100, 0);
                var projections = File.ReadAllLines(Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"),
                    "spatial-projection.txt"));
                for (var index = 0; index < viewports.Length; ++index)
                {
                    var viewport = viewports[index];
                    var coords = projections[index].Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                    var expected = Screen(coords[0], coords[1], 0);
                    CadNative.Check(CadNative.wl_viewport_inline(client.Handle, document, viewport.Id));
                    var actual = Screen(200, 200, 400);
                    Check(client.State.InlineViewport && Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1,
                        "Inline interaction camera did not match the layout's spatial projection");
                    client.Command("PSPACE");
                    CadNative.Check(CadNative.wl_viewport_edit(client.Handle, document, viewport.Id));
                    Check(client.State.EditingViewport == viewport.Id && !client.State.InlineViewport,
                        "Could not maximize non-planar viewport");
                    var position = Screen(150, 200, 350);
                    var packed = new IntPtr((position.Y << 16) | (position.X & 0xffff));
                    CadTestKeyboard.WithModifiers(() =>
                    {
                        SendMessage(host.Handle, 0x0200, IntPtr.Zero, packed);
                        SendMessage(host.Handle, 0x0201, new IntPtr(1), packed);
                        SendMessage(host.Handle, 0x0202, IntPtr.Zero, packed);
                    });
                    Check(client.State.SelectionCount == 1, "Spatial viewport picking missed the projected model line");
                    client.Command("@cancel");
                    CadNative.Check(CadNative.wl_workplane(client.Handle, 1, 100, 200, 300));
                    var baseX = 10 + 30 * index;
                    var before = client.State.EntityCount;
                    client.Command("LINE");
                    client.Command($"{baseX},20,0");
                    client.Command($"{baseX + 80},65,0");
                    client.Command("");
                    client.Command("@cancel");
                    Check(client.State.EntityCount == before + 1, "Spatial viewport drawing did not add a model line");
                    client.Command("@undo");
                    Check(client.State.EntityCount == before && client.State.EditingViewport == viewport.Id,
                        "Undo did not remove the model line within the viewport");
                    client.Command("@redo");
                    Check(client.State.EntityCount == before + 1, "Redo did not restore the model line");
                    client.Command("UCS WORLD");
                    if (index == 1)
                    {
                        workspace.RefreshState();
                        await Task.Delay(100);
                        capture(Window.GetWindow(workspace), Path.Combine(root, "spatial-maximized-edit.png"));
                    }
                    client.Command("VPMIN");
                    Check(client.State.EditingViewport == 0 && Screen(150, 100, 0) == paper, "Paper camera changed after VPMIN");
                    var returned = CadNative.PaperViewports(client.Handle).Single(item => item.Id == viewport.Id);
                    Check(returned.Ratio == viewport.Ratio && returned.ModelX == viewport.ModelX &&
                        returned.ModelY == viewport.ModelY && returned.ModelZ == viewport.ModelZ &&
                        returned.Locked == viewport.Locked && returned.Twist == viewport.Twist,
                        "Temporary spatial viewport editing changed saved viewport parameters");
                }
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "非俯视最大化编辑" + extension));
            }
            finally
            {
                client.Close(document);
                client.Activate(previousDocument);
                workspace.RefreshState();
            }
        }
    }
}
