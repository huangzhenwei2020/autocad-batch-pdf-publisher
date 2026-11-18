using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadViewportEditingSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void Line(double x, double y, double endX, double endY)
            {
                client.Command("LINE"); Point(x, y); Point(endX, endY); client.Command(""); client.Command("@cancel");
            }
            void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
            void Click(double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                var window = ((CadViewport)workspace.FindName("Viewport")).Handle;
                var position = new IntPtr((py << 16) | (px & 0xffff));
                SendMessage(window, 0x0201, new IntPtr(1), position);
                SendMessage(window, 0x0202, IntPtr.Zero, position);
            }
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            Line(0, 0, 1000, 0);
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "编辑视口冻结层", 0, -3));
            var frozen = client.State.Layers.Single(item => item.Name == "编辑视口冻结层").Id;
            Line(0, 1000, 1000, 1000);
            CadNative.Check(CadNative.wl_layer(client.Handle, 0, 1, 0, 1));
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "视口模型编辑"));
            Line(0, 0, 200, 0);
            var parameters = new CadViewportParameters
            {
                X = 150, Y = 100, Width = 300, Height = 200, ModelX = 500, ModelY = 500,
                Ratio = 10, Twist = 30, Locked = 1
            };
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, 0, ref parameters, new[] { frozen }, 1));
            var viewport = CadNative.PaperViewports(client.Handle).Single();
            client.Command("@fit");
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 150, 100, out var paperX, out var paperY));
            workspace.ExecuteCommand("VPMAX");
            Check(client.State.EditingViewport == viewport.Id, "VPMAX did not enter the layout viewport");
            Check(client.State.Layouts.Single(item => item.Current).Name == "视口模型编辑", "Maximizing changed selected layout");
            workspace.ExecuteCommand("VPMAX");
            Check(client.State.EditingViewport == viewport.Id, "Repeating VPMAX exited editing");
            Click(500, 0); Check(client.State.SelectionCount == 1, "Rotated viewport mouse picking missed model geometry");
            client.Command("@cancel");
            Click(500, 1000); Check(client.State.SelectionCount == 0, "Viewport mouse picking selected frozen geometry");
            client.Command("@cancel");
            client.Command("@all");
            Check(client.State.SelectionCount == 1, "Viewport edit selected paper or frozen model entities");
            client.Command("MOVE"); Point(0, 0); Point(500, 250);
            client.Command("CIRCLE"); Point(500, 700); Point(600, 700); client.Command("@cancel");
            Check(client.State.EntityCount == 5, "Viewport circle did not commit");
            client.Command("@undo"); Check(client.State.EntityCount == 4, "Viewport geometry undo failed");
            client.Command("@redo"); Check(client.State.EntityCount == 5, "Viewport geometry redo failed");
            client.Command("LINE"); Point(50, 50); client.Command("@cancel");
            Check(client.State.EditingViewport == viewport.Id && client.State.EntityCount == 5, "Esc exited edit mode or committed a draft");

            workspace.RefreshState(); await Task.Delay(120);
            capture(Window.GetWindow(workspace), Path.Combine(root, "viewport-model-edit.png"));
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "视口内编辑" + extension));

            workspace.NewDrawing();
            var scratch = client.State.ActiveId;
            client.Activate(document);
            Check(client.State.EditingViewport == viewport.Id, "Document switch lost viewport editing context");
            client.Close(scratch);
            client.Command("@fit");
            var host = (CadViewport)workspace.FindName("Viewport");
            SendMessage(host.Handle, 0x0207, new IntPtr(16), new IntPtr((250 << 16) | 400));
            SendMessage(host.Handle, 0x0200, new IntPtr(16), new IntPtr((275 << 16) | 450));
            SendMessage(host.Handle, 0x0208, IntPtr.Zero, new IntPtr((275 << 16) | 450));
            workspace.RefreshState();
            ((Button)workspace.FindName("ViewportEditingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(client.State.EditingViewport == 0, "Return-to-layout button failed");
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 150, 100, out var returnedX, out var returnedY));
            Check(returnedX == paperX && returnedY == paperY, "Viewport navigation changed the paper camera");
            var returned = CadNative.PaperViewports(client.Handle).Single();
            Check(returned.Locked && returned.Ratio == 10 && returned.ModelX == 500 && returned.ModelY == 500 && returned.Twist == 30,
                "Viewport edit changed lock, scale, model center or twist");
            client.Command("@undo");
            Check(client.State.EntityCount == 4, "Temporary edit navigation polluted geometry undo");
            client.Command("@redo");
            await client.SaveAsync(Path.Combine(root, "视口编辑返回.dwg"));

            workspace.EditViewportModel();
            var page = client.State.Layouts.Single(item => item.Current);
            CadNative.Check(CadNative.wl_layout(client.Handle, page.Id));
            Check(client.State.EditingViewport == 0, "Clicking the original layout did not return to paper space");
            client.Command("@all"); Check(client.State.SelectionCount == 2, "Paper-space selection changed after viewport editing");
            client.Command("@cancel");

            parameters.X = 350; parameters.Twist = 0;
            CadNative.Check(CadNative.wl_viewport_apply(client.Handle, document, 0, ref parameters, Array.Empty<uint>(), 0));
            Action<CadParameterWindow> choose = form => form.Dispatcher.BeginInvoke(new Action(() =>
            {
                ((ComboBox)form.Inputs["viewport"]).SelectedIndex = 1;
                capture(form, Path.Combine(root, "viewport-model-edit-dialog.png"));
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            workspace.ParameterWindowOpened += choose;
            try { workspace.EditViewportModel(); } finally { workspace.ParameterWindowOpened -= choose; }
            client.Command("@all"); Check(client.State.SelectionCount == 3, "Unfrozen viewport did not restore model entities");
            client.Command("VPMIN");
            client.Close(document);
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                await client.OpenAsync(Path.Combine(root, "视口内编辑" + extension));
                document = client.State.ActiveId;
                Check(client.State.EditingViewport == 0, "Reopening persisted temporary viewport edit mode");
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "视口模型编辑").Id));
                Check(CadNative.PaperViewports(client.Handle).Single().Ratio == 10, "Saved viewport scale changed");
                workspace.EditViewportModel();
                Line(100, 1200, 1000, 1200);
                await client.SaveAsync(Path.Combine(root, "视口原图编辑" + extension));
                client.Command("VPMIN"); client.Close(document);
            }
            client.Activate(original); workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-viewport-edit-result.txt"), "CAD_VIEWPORT_EDIT_OK model paper freeze move circle undo save switch navigation restore");
        }
    }
}
