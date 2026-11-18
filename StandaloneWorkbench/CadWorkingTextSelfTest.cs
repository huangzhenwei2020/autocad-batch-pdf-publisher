using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadWorkingTextSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
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
                    File.WriteAllText(Path.Combine(root, $"text-plane-{view}.txt"), string.Join(" ", new[]
                    {
                        plane.OriginX, plane.OriginY, plane.OriginZ, plane.AxisXX, plane.AxisXY, plane.AxisXZ,
                        plane.AxisYX, plane.AxisYY, plane.AxisYZ
                    }.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));

                    client.Command("TEXT"); client.Command("30,40,7");
                    var request = CadNative.DialogState(client.Handle);
                    if (request.Kind != "text") throw new InvalidOperationException("Single-line text ignored the working plane.");
                    CadNative.Check(CadNative.wl_text_submit(client.Handle, request.DocumentId, 0, "立面文字", 6, 25, 0));
                    client.Command("MTEXT"); client.Command("140,80,12");
                    request = CadNative.DialogState(client.Handle);
                    if (request.Kind != "mtext") throw new InvalidOperationException("Multiline text ignored the working plane.");
                    CadNative.Check(CadNative.wl_text_submit(client.Handle, request.DocumentId, 1, "文字一\r\n文字二", 8, -35, 45));
                    if (client.State.EntityCount != 2) throw new InvalidOperationException("Working-plane text was not created.");

                    client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@4,8,3"); client.Command("@cancel");
                    client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@200,0"); client.Command("@cancel");
                    if (client.State.EntityCount != 4) throw new InvalidOperationException("Working-plane text copy failed.");
                    client.Command("@undo");
                    if (client.State.EntityCount != 2) throw new InvalidOperationException("Working-plane text copy undo failed.");
                    client.Command("@redo");
                    foreach (var extension in new[] { ".dwg", ".dxf" })
                        await client.SaveAsync(Path.Combine(root, $"工作平面文字-{view}" + extension));
                    client.Close(document);

                    foreach (var extension in new[] { ".dwg", ".dxf" })
                    {
                        await client.OpenAsync(Path.Combine(root, $"工作平面文字-{view}" + extension));
                        document = client.State.ActiveId;
                        if (client.State.EntityCount != 4) throw new InvalidOperationException("Working-plane text changed after reopening.");
                        client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,0,10"); client.Command("@cancel");
                        await client.SaveAsync(Path.Combine(root, $"工作平面文字增量-{view}" + extension));
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
