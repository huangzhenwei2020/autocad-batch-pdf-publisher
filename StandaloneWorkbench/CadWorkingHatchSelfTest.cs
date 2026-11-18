using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadWorkingHatchSelfTest
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr point);

        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            workspace.SuppressToolDialogs = true;
            try
            {
                foreach (var view in new[] { 2, 6, 1 })
                {
                    workspace.NewDrawing();
                    var document = client.State.ActiveId;
                    CadNative.Check(CadNative.wl_standard_view(client.Handle, view));
                    CadNative.Check(CadNative.wl_workplane(client.Handle, 1, 100, 200, 300));
                    var plane = client.State.WorkPlane;
                    File.WriteAllText(Path.Combine(root, $"hatch-plane-{view}.txt"), string.Join(" ", new[]
                    {
                        plane.OriginX, plane.OriginY, plane.OriginZ, plane.AxisXX, plane.AxisXY, plane.AxisXZ,
                        plane.AxisYX, plane.AxisYY, plane.AxisYZ
                    }.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
                    void Click(double x, double y)
                    {
                        CadNative.Check(CadNative.wl_world_to_screen3(client.Handle,
                            plane.OriginX + x * plane.AxisXX + y * plane.AxisYX,
                            plane.OriginY + x * plane.AxisXY + y * plane.AxisYY,
                            plane.OriginZ + x * plane.AxisXZ + y * plane.AxisYZ, out var px, out var py));
                        var point = new IntPtr((py << 16) | (px & 0xffff));
                        CadTestKeyboard.WithModifiers(() =>
                        {
                            SendMessage(host.Handle, 0x0200, IntPtr.Zero, point);
                            SendMessage(host.Handle, 0x0201, new IntPtr(1), point);
                            SendMessage(host.Handle, 0x0202, IntPtr.Zero, point);
                        });
                    }
                    void PathOnPlane(params string[] points)
                    {
                        client.Command("PLINE");
                        foreach (var point in points) client.Command(point);
                        client.Command("C");
                    }
                    void Hatch(string pattern)
                    {
                        client.Command("HATCH");
                        var request = CadNative.DialogState(client.Handle);
                        Check(request.Kind == "hatch", "Hatch command did not open its settings");
                        CadNative.Check(CadNative.wl_hatch_submit(client.Handle, request.DocumentId, pattern, 4, 25));
                    }
                    if (client.State.Snap) client.Command("@snap");
                    PathOnPlane("0,0", "120,0", "120,80", "0,80");
                    PathOnPlane("45,35,30", "55,35", "55,45", "45,45");
                    client.Command("CIRCLE"); client.Command("220,60"); client.Command("250,60"); client.Command("@cancel");
                    Check(client.State.EntityCount == 3, "Working hatch source curves were not created");
                    client.Command("@fit");
                    Hatch("ANSI31");
                    Click(50, 40); client.Command("@cancel");
                    Check(client.State.EntityCount == 4, "Patterned hatch did not find its plane");
                    Hatch("SOLID");
                    client.Command("220,60"); client.Command("@cancel");
                    Check(client.State.EntityCount == 5, "Solid hatch did not find the circle");
                    client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@10,20,5"); client.Command("@cancel");
                    client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@400,0"); client.Command("@cancel");
                    Check(client.State.EntityCount == 10, "Working hatch copy failed");
                    client.Command("@undo"); Check(client.State.EntityCount == 5, "Working hatch copy undo failed");
                    client.Command("@redo"); Check(client.State.EntityCount == 10, "Working hatch copy redo failed");
                    foreach (var extension in new[] { ".dwg", ".dxf" })
                        await client.SaveAsync(Path.Combine(root, $"工作平面填充-{view}" + extension));
                    client.Close(document);
                    foreach (var extension in new[] { ".dwg", ".dxf" })
                    {
                        await client.OpenAsync(Path.Combine(root, $"工作平面填充-{view}" + extension));
                        document = client.State.ActiveId;
                        Check(client.State.EntityCount == 10, "Hatch save/reopen changed entity count");
                        client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,0,10"); client.Command("@cancel");
                        await client.SaveAsync(Path.Combine(root, $"工作平面填充增量-{view}" + extension));
                        client.Close(document);
                    }
                }
            }
            finally
            {
                client.Activate(original);
                workspace.SuppressToolDialogs = false;
                workspace.RefreshState();
            }
        }
    }
}
