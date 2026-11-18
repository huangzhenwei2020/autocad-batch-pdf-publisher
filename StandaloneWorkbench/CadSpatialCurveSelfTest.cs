using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadSpatialCurveSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr point);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            void Click(double x, double y, double z)
            {
                CadNative.Check(CadNative.wl_world_to_screen3(client.Handle, x,y,z,out var px,out var py));
                var point = new IntPtr((py << 16) | (px & 0xffff));
                CadTestKeyboard.WithModifiers(() =>
                {
                    SendMessage(host.Handle,0x0200,IntPtr.Zero,point);
                    SendMessage(host.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(host.Handle,0x0202,IntPtr.Zero,point);
                });
            }
            foreach (var view in new[] { 2,6,1 })
            {
                workspace.NewDrawing();
                var document = client.State.ActiveId;
                CadNative.Check(CadNative.wl_standard_view(client.Handle,view));
                CadNative.Check(CadNative.wl_workplane(client.Handle,1,100,200,300));
                var plane = client.State.WorkPlane;
                File.WriteAllText(Path.Combine(root,$"curve-plane-{view}.txt"),string.Join(" ",new[] {
                    plane.OriginX,plane.OriginY,plane.OriginZ,plane.AxisXX,plane.AxisXY,plane.AxisXZ,
                    plane.AxisYX,plane.AxisYY,plane.AxisYZ }.Select(value => value.ToString("R",CultureInfo.InvariantCulture))));
                if (client.State.Snap) client.Command("@snap");
                client.Command("CIRCLE"); client.Command("30,40,7"); client.Command("55,40,7"); client.Command("@cancel");
                client.Command("ARC"); client.Command("115,40,7"); client.Command("90,65,7"); client.Command("65,40,7"); client.Command("@cancel");
                client.Command("ARC"); client.Command("165,40,7"); client.Command("190,65,7"); client.Command("215,40,7"); client.Command("@cancel");
                Check(client.State.EntityCount == 3,"Working-plane circle or three-point arc did not commit");
                client.Command("@fit"); workspace.RefreshState(); await Task.Delay(100);
                if (view == 2)
                {
                    Click(130,193,315);
                    Check(client.State.SelectionCount == 1,"Front-plane circle point selection failed");
                    Click(130,193,365);
                    Click(130,193,380);
                    client.Command("@undo"); client.Command("@cancel");
                    Check(client.State.EntityCount == 3,"Circle grip undo damaged the document");
                }
                client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@10,20,5"); client.Command("@cancel");
                client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@250,0,0"); client.Command("@cancel");
                Check(client.State.EntityCount == 6,"Spatial curves were lost during move/copy");
                client.Command("@undo"); Check(client.State.EntityCount == 3,"Spatial curve copy undo failed");
                client.Command("@redo"); Check(client.State.EntityCount == 6,"Spatial curve copy redo failed");
                client.Command("@fit"); workspace.RefreshState(); await Task.Delay(100);
                capture(Window.GetWindow(workspace),Path.Combine(root,$"spatial-curves-{view}.png"));
                foreach (var extension in new[] { ".dwg",".dxf" })
                    await client.SaveAsync(Path.Combine(root,$"工作平面圆弧-{view}"+extension));
                client.Close(document);
                foreach (var extension in new[] { ".dwg",".dxf" })
                {
                    await client.OpenAsync(Path.Combine(root,$"工作平面圆弧-{view}"+extension));
                    document = client.State.ActiveId;
                    Check(client.State.EntityCount == 6,"Spatial curve save/reopen changed entity count");
                    client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,0,10"); client.Command("@cancel");
                    await client.SaveAsync(Path.Combine(root,$"工作平面圆弧增量-{view}"+extension));
                    client.Close(document);
                }
            }
            client.Activate(original); workspace.RefreshState();
        }
    }
}
