using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadWorkingEllipseSelfTest
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
                File.WriteAllText(Path.Combine(root, $"ellipse-plane-{view}.txt"), string.Join(" ", new[] {
                    plane.OriginX, plane.OriginY, plane.OriginZ, plane.AxisXX, plane.AxisXY, plane.AxisXZ,
                    plane.AxisYX, plane.AxisYY, plane.AxisYZ }.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));

                client.Command("ELLIPSE"); client.Command("40,60,7"); client.Command("70,100,20"); client.Command("24,72");
                client.Command("@cancel");
                client.Command("ELLIPSEARC"); client.Command("200,100,7"); client.Command("212,116"); client.Command("168,124");
                client.Command("232,76"); client.Command("212,116"); client.Command("@cancel");
                Check(client.State.EntityCount == 2, "Working-plane ellipse or ellipse arc was not created");
                client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@10,20,5"); client.Command("@cancel");
                client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@300,0"); client.Command("@cancel");
                Check(client.State.EntityCount == 4, "Working-plane ellipse copy failed");
                client.Command("@undo"); Check(client.State.EntityCount == 2, "Ellipse copy undo failed");
                client.Command("@redo"); Check(client.State.EntityCount == 4, "Ellipse copy redo failed");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, $"工作平面椭圆-{view}" + extension));
                client.Close(document);
                foreach (var extension in new[] { ".dwg", ".dxf" })
                {
                    await client.OpenAsync(Path.Combine(root, $"工作平面椭圆-{view}" + extension));
                    document = client.State.ActiveId;
                    Check(client.State.EntityCount == 4, "Working-plane ellipse save/reopen changed the count");
                    client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,0,10"); client.Command("@cancel");
                    await client.SaveAsync(Path.Combine(root, $"工作平面椭圆增量-{view}" + extension));
                    client.Close(document);
                }
            }
            client.Activate(original);
            workspace.RefreshState();
        }
    }
}
