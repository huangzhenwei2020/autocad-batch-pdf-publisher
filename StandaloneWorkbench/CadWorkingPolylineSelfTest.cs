using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadWorkingPolylineSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

            foreach (var view in new[] { 2, 6, 1 })
            {
                workspace.NewDrawing();
                var document = client.State.ActiveId;
                CadNative.Check(CadNative.wl_standard_view(client.Handle, view));
                CadNative.Check(CadNative.wl_workplane(client.Handle, 1, 100, 200, 300));
                var plane = client.State.WorkPlane;
                File.WriteAllText(Path.Combine(root, $"polyline-plane-{view}.txt"), string.Join(" ", new[] {
                    plane.OriginX, plane.OriginY, plane.OriginZ, plane.AxisXX, plane.AxisXY, plane.AxisXZ,
                    plane.AxisYX, plane.AxisYY, plane.AxisYZ }.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));

                client.Command("PLINE");
                client.Command("20,30,7");
                client.Command("120,30,20");
                client.Command("A");
                client.Command("170,80");
                client.Command("220,30");
                client.Command("L");
                client.Command("C");
                Check(client.State.EntityCount == 1, "Working-plane PLINE did not close and commit");
                client.Command("@all");
                var width = client.State.Properties.Single(item => item.Name == "全局宽度");
                CadNative.Check(CadNative.wl_property(client.Handle, width.Name, "0.25", width.Kind));
                client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@10,20,5"); client.Command("@cancel");
                client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@250,0"); client.Command("@cancel");
                Check(client.State.EntityCount == 2, "Working-plane polyline copy failed");
                client.Command("@undo"); Check(client.State.EntityCount == 1, "Polyline copy undo failed");
                client.Command("@redo"); Check(client.State.EntityCount == 2, "Polyline copy redo failed");

                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, $"工作平面多段线-{view}" + extension));
                client.Close(document);
                foreach (var extension in new[] { ".dwg", ".dxf" })
                {
                    await client.OpenAsync(Path.Combine(root, $"工作平面多段线-{view}" + extension));
                    document = client.State.ActiveId;
                    Check(client.State.EntityCount == 2, "Working-plane polyline save/reopen changed the count");
                    client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,0,10"); client.Command("@cancel");
                    await client.SaveAsync(Path.Combine(root, $"工作平面多段线增量-{view}" + extension));
                    client.Close(document);
                }
            }
            client.Activate(original);
            workspace.RefreshState();
        }
    }
}
