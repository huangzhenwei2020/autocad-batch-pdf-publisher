using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadWorkingPlaneSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr point);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            var host = (CadViewport)workspace.FindName("Viewport");
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            void Mouse(double x, double y, double z, bool click)
            {
                CadNative.Check(CadNative.wl_world_to_screen3(client.Handle, x, y, z, out var px, out var py));
                var point = new IntPtr((py << 16) | (px & 0xffff));
                SendMessage(host.Handle, 0x0200, IntPtr.Zero, point);
                if (!click) return;
                SendMessage(host.Handle, 0x0201, new IntPtr(1), point);
                SendMessage(host.Handle, 0x0202, IntPtr.Zero, point);
            }
            CadNative.Check(CadNative.wl_standard_view(client.Handle, 2));
            Action<CadParameterWindow> opened = form => form.Dispatcher.BeginInvoke(new Action(() =>
            {
                ((ComboBox)form.Inputs["plane"]).SelectedIndex = 1;
                foreach (var field in new[] { ("x", "100"), ("y", "200"), ("z", "300") })
                    ((TextBox)form.Inputs[field.Item1]).Text = field.Item2;
                form.UpdateLayout();
                foreach (var input in form.Inputs.Values)
                {
                    var bounds = input.TransformToAncestor(form).TransformBounds(new Rect(input.RenderSize));
                    Check(bounds.Right <= form.ActualWidth && bounds.Left >= 0, "Working plane field is clipped");
                }
                capture(form, Path.Combine(root, "working-plane-dialog.png"));
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            workspace.ParameterWindowOpened += opened;
            try { workspace.WorkingPlaneSettings(); }
            finally { workspace.ParameterWindowOpened -= opened; }
            Check(Math.Abs(client.State.WorkPlane.AxisYZ - 1) < 1e-9 && client.State.WorkPlane.OriginZ == 300, "View working plane was not applied");
            client.Command("@undo"); Check(client.State.WorkPlane.World, "Working plane undo failed");
            client.Command("@redo"); Check(!client.State.WorkPlane.World, "Working plane redo failed");
            CadNative.Check(CadNative.wl_standard_view(client.Handle, 6));
            client.Command("UCS VIEW");
            Check(client.State.WorkPlane.OriginX == 100 && client.State.WorkPlane.OriginZ == 300 &&
                Math.Abs(client.State.WorkPlane.AxisYZ - 1) > .01, "UCS VIEW changed the origin or missed the view axes");
            client.Command("@undo");
            CadNative.Check(CadNative.wl_standard_view(client.Handle, 2));
            client.Command("LINE"); client.Command("0,0"); client.Command("@100,0"); client.Command("@0,100"); client.Command("@0,0,10"); client.Command("@cancel");
            client.Command("POINT"); client.Command("25,25,10"); client.Command("@cancel");
            client.Command("@fit");
            if (client.State.Snap) client.Command("@snap");
            client.Command("LINE"); Mouse(100,200,300,true); Mouse(150,200,360,true); client.Command("@cancel");
            client.Command("@ortho");
            client.Command("LINE"); client.Command("0,0"); Mouse(110,200,350,true); client.Command("@cancel");
            client.Command("@ortho");
            client.Command("@snap");
            client.Command("LINE"); Mouse(100.2,200,300.2,true); client.Command("80,80"); client.Command("@cancel");
            client.Command("POINT"); Mouse(150,200,300,true); client.Command("@cancel");
            Check(client.State.EntityCount == 8, "Working plane drawing count changed");
            client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@10,20"); client.Command("@cancel");
            client.Command("@all"); client.Command("MOVE"); client.Command("0,0"); client.Command("@0,5"); client.Command("@cancel");
            await client.SaveAsync(Path.Combine(root, "工作平面纯Z.dwg"));
            client.Command("@undo");
            client.Command("@all"); client.Command("COPY"); client.Command("0,0"); client.Command("@200,0"); client.Command("@cancel");
            Check(client.State.EntityCount == 16, "Working plane copy failed");
            client.Command("@undo"); Check(client.State.EntityCount == 8, "Working plane copy undo failed");
            client.Command("@redo"); Check(client.State.EntityCount == 16, "Working plane copy redo failed");
            client.Command("RECTANG"); Check(client.State.Prompt == "命令", "Unsupported planar tool remained active");
            client.Command("@fit"); workspace.RefreshState(); await Task.Delay(120);
            capture(Window.GetWindow(workspace), Path.Combine(root, "working-plane-front.png"));
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "三维工作平面" + extension));

            client.Command("UCS WORLD"); Check(client.State.WorkPlane.World, "UCS WORLD failed");
            CadNative.Check(CadNative.wl_standard_view(client.Handle, 0));
            client.Command("CIRCLE"); client.Command("0,0"); client.Command("100,0"); client.Command("@cancel");
            Check(client.State.EntityCount == 17, "World-plane drawing did not recover");
            client.Close(document);
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                await client.OpenAsync(Path.Combine(root, "三维工作平面" + extension));
                document = client.State.ActiveId;
                Check(Math.Abs(client.State.WorkPlane.OriginZ - 300) < 1e-8 && Math.Abs(client.State.WorkPlane.AxisYZ - 1) < 1e-8,
                    "Working plane was lost after saving");
                client.Close(document);
            }
            client.Activate(original); workspace.RefreshState();
        }
    }
}
