using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadExtendedSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.SuppressToolDialogs = true;
            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void Draw(string command, params double[] points)
            {
                var count = client.State.EntityCount;
                client.Command("@cancel"); client.Command(command);
                for (var index = 0; index < points.Length; index += 2) Point(points[index], points[index + 1]);
                client.Command(""); client.Command("@cancel");
                Check(client.State.EntityCount == count + 1, command + " did not create exactly one entity");
                client.Command("@undo"); Check(client.State.EntityCount == count, command + " could not be undone");
                client.Command("@redo"); Check(client.State.EntityCount == count + 1, command + " could not be redone");
            }
            void New() { workspace.NewDrawing(); }
            void Close() { client.Command("@cancel"); client.Close(client.State.ActiveId); client.Activate(original); }
            void Click(double x, double y)
            {
                var host = (CadViewport)workspace.FindName("Viewport");
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                var point = new IntPtr((py << 16) | (px & 0xffff));
                SendMessage(host.Handle, 0x0201, new IntPtr(1), point); SendMessage(host.Handle, 0x0202, IntPtr.Zero, point);
            }
            void CompleteForm(string image, Action<CadParameterWindow> fill)
            {
                Action<CadParameterWindow> complete = form =>
                {
                    fill(form); capture(form, Path.Combine(root, image + "-parameter-window.png"));
                    form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!form.IsVisible, image + " form did not accept its valid parameters");
                };
                workspace.ParameterWindowOpened += complete;
                try { workspace.ShowToolDialog(CadNative.DialogState(client.Handle)); }
                finally { workspace.ParameterWindowOpened -= complete; }
            }
            void SubmitText(bool multiline, string content, double height = 150)
            {
                var request = CadNative.DialogState(client.Handle);
                Check(request.Kind == (multiline ? "mtext" : "text"), "Text placement did not request its input window");
                CadNative.Check(CadNative.wl_text_submit(client.Handle, request.DocumentId, multiline ? 1 : 0, content, height, 15, multiline ? 1500 : 0));
            }
            New();
            Draw("ELLIPSE", 0, 0, 1000, 0, 0, 500);
            Draw("ELLIPSEARC", 2500, 0, 3500, 0, 2500, 500, 3500, 0, 2500, 500);
            Draw("SPLINE", 4500, 0, 5000, 500, 5500, 0);
            Draw("POINT", 6500, 0);
            Draw("RAY", 7500, 0, 8500, 0);
            Draw("XLINE", 9000, 0, 10000, 0);
            CadNative.Check(CadNative.wl_mline_settings(client.Handle, 200, 1));
            Draw("MLINE", 10000, 1000, 10000, 2000, 12000, 2000);
            Draw("SOLID", 14000, 0, 15000, 0, 14000, 1000, 15000, 1000);
            Draw("WIPEOUT", 17000, 0, 18000, 0, 18000, 1000);
            Draw("RECTANG", 0, 3000, 2000, 4500);
            client.Command("@fit");
            client.Command("HATCH");
            var hatch = CadNative.DialogState(client.Handle);
            Check(hatch.Kind == "hatch" && hatch.Choices.Contains("ANSI31"), "Hatch dialog did not list native patterns");
            CompleteForm("hatch", form => ((TextBox)form.Inputs["scale"]).Text = "10");
            Point(1000, 3700); client.Command("@cancel");
            Check(client.State.EntityCount == 11, "Hatch did not fill the rectangle");
            client.Command("TEXT"); Point(3000, 3000); SubmitText(false, "独立单行文字");
            client.Command("MTEXT"); Point(6000, 3000); SubmitText(true, "第一行\\P第二行");
            Draw("TABLE", 9000, 5000, 13000, 3000);
            CadNative.Check(CadNative.wl_dimension_settings(client.Handle, 100, 80, 25, 50, 30, 0, 0));
            Draw("DIMLINEAR", 0, 6000, 1000, 6500, 500, 7500);
            Draw("DIMALIGNED", 2000, 6000, 3000, 6500, 2500, 7500);
            client.Command("DIMANGULAR"); client.Command("");
            Point(5000, 6000); Point(6000, 6000); Point(5000, 7000); Point(5500, 6500); client.Command("@cancel");
            Draw("DIMORDINATE", 8000, 6500, 8500, 7500);
            Draw("CIRCLE", 10000, 6500, 10500, 6500);
            client.Command("@fit");
            Draw("DIMRADIUS", 10500, 6500, 11000, 7400);
            Draw("DIMDIAMETER", 10500, 6500, 11000, 5500);
            Draw("DIMJOGGED", 10500, 6500, 9500, 6000, 10000, 7000, 10500, 7200);
            Draw("ARC", 12000, 6000, 12500, 6500, 13000, 6000);
            client.Command("@fit");
            Draw("DIMARC", 12500, 6500, 12500, 7000);
            var beforeLeader = client.State.EntityCount;
            CadNative.Check(CadNative.wl_leader_start(client.Handle, 0, "引线注释", 150));
            Point(15000, 6000); Point(16000, 6500); client.Command(""); client.Command("@cancel");
            Check(client.State.EntityCount == beforeLeader + 1, "Leader did not finish on Enter");
            CadNative.Check(CadNative.wl_leader_start(client.Handle, 1, "多重引线注释", 150));
            Point(17000, 6000); client.Command(""); Point(18000, 6500); client.Command("@cancel");
            Check(client.State.EntityCount == beforeLeader + 2, "MLeader did not complete its landing");
            var drawing = Path.Combine(root, "扩展二维绘图.dwg");
            await client.SaveAsync(drawing);
            var countBeforeSave = client.State.EntityCount;
            Close(); await client.OpenAsync(drawing);
            Check(client.State.EntityCount == countBeforeSave, "Extended DWG lost entities on reopening");
            client.Command("@fit"); await Task.Delay(150);
            client.Capture(Path.Combine(root, "extended-reopened.png"));
            Click(10000, 4500); Click(10000, 4500);
            File.WriteAllText(Path.Combine(root, "table-selection-state.json"), System.Text.Json.JsonSerializer.Serialize(new { State = client.State, Dialog = CadNative.DialogState(client.Handle) }));
            Check(CadNative.DialogState(client.Handle).Kind == "mtext" && CadNative.DialogState(client.Handle).Editing, "Saved table lost editable cells");
            SubmitText(true, "表格单元");
            await client.SaveAsync(drawing);
            await client.SaveAsync(Path.Combine(root, "扩展二维绘图.dxf")); Close();

            // Native dialog completion is also exercised through the actual WPF form.
            New(); client.Command("TEXT"); Point(100, 100);
            Action<CadParameterWindow> fillText = form =>
            {
                ((TextBox)form.Inputs["text"]).Text = "WPF文字验证";
                ((TextBox)form.Inputs["height"]).Text = "120";
                capture(form, Path.Combine(root, "text-parameter-window.png"));
                ((TextBox)form.Inputs["height"]).Text = "-1";
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(form.IsVisible && client.State.EntityCount == 0, "Invalid text height closed the form or changed the drawing");
                ((TextBox)form.Inputs["height"]).Text = "120";
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            workspace.ParameterWindowOpened += fillText;
            try { workspace.ShowToolDialog(CadNative.DialogState(client.Handle)); }
            finally { workspace.ParameterWindowOpened -= fillText; }
            Check(client.State.EntityCount == 1 && string.IsNullOrEmpty(CadNative.DialogState(client.Handle).Kind), "WPF text form did not commit");
            client.Command("TEXT"); Point(200, 200);
            Action<CadParameterWindow> cancel = form => form.DialogResult = false;
            workspace.ParameterWindowOpened += cancel;
            try { workspace.ShowToolDialog(CadNative.DialogState(client.Handle)); }
            finally { workspace.ParameterWindowOpened -= cancel; }
            Check(client.State.EntityCount == 1 && string.IsNullOrEmpty(CadNative.DialogState(client.Handle).Kind), "Cancelled text window created geometry or retained its request");
            Close();

            New(); Draw("LINE", 0, 0, 1000, 0);
            client.Command("ARRAY"); Check(client.State.CursorMode == "select", "Command-first array did not ask for selection");
            client.Command("@all"); client.Command("");
            Check(CadNative.DialogState(client.Handle).Kind == "array", "Command-first selection did not continue into array parameters");
            var array = new CadArrayParameters { Type = 0, Rows = 2, Columns = 3, Count = 6, RotateItems = 1, RowSpacing = 500, ColumnSpacing = 1500, FillAngle = 360 };
            CompleteForm("array", form =>
            {
                ((TextBox)form.Inputs["rows"]).Text = "2"; ((TextBox)form.Inputs["columns"]).Text = "3";
                ((TextBox)form.Inputs["rowSpacing"]).Text = "500"; ((TextBox)form.Inputs["columnSpacing"]).Text = "1500";
            });
            Check(client.State.EntityCount == 6, "Rectangular array lost copies");
            client.Command("@undo"); Check(client.State.EntityCount == 1, "Array must undo in one step");
            client.Command("@redo");
            await client.SaveAsync(Path.Combine(root, "二维阵列.dwg"));
            client.Command("@undo"); client.Command("@cancel"); client.Command("@all"); client.Command("ARRAY");
            array.Type = 1; array.Count = 4;
            CadNative.Check(CadNative.wl_array_submit(client.Handle, client.State.ActiveId, ref array));
            Check(client.State.EntityCount == 4, "Polar array lost copies"); client.Command("@undo");
            client.Command("@cancel"); client.Command("@all"); client.Command("BLOCK"); Point(0, 0);
            Check(CadNative.DialogState(client.Handle).Kind == "blockDefine", "Block definition did not request a name");
            CompleteForm("block-define", form => ((TextBox)form.Inputs["name"]).Text = "独立验证块");
            client.Command("INSERT"); Check(CadNative.DialogState(client.Handle).Blocks.Any(item => item.Name == "独立验证块"), "Defined block was missing from insertion choices");
            CompleteForm("block-insert", form => ((ComboBox)form.Inputs["block"]).SelectedIndex = 0);
            Point(5000, 0); client.Command("@cancel");
            Check(client.State.EntityCount == 2, "Block insertion did not place its instance");
            await client.SaveAsync(Path.Combine(root, "二维块.dwg"));
            client.Command("@all"); client.Command("EXPLODE"); Check(client.State.EntityCount == 2, "Exploding two single-line blocks changed geometry count");
            client.Command("@undo"); Check(client.State.CanRedo, "Block explode cannot be undone"); Close();

            New();
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "新建验证层", 0, -3));
            var layer = client.State.Layers.Single(item => item.Name == "新建验证层");
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 2, layer.Id, "修改验证层", 0x00ff00, 35));
            client.Command("@undo"); Check(client.State.Layers.Any(item => item.Name == "新建验证层"), "Layer edit undo failed"); client.Command("@redo");
            Draw("LINE", 0, 0, 1000, 0);
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 1, layer.Id, "", 0, -3));
            Check(!client.State.Layers.Any(item => item.Id == layer.Id), "Layer deletion failed");
            client.Command("@undo"); Check(client.State.Layers.Single(item => item.Id == layer.Id).Rgb == 0x00ff00, "Layer deletion undo lost color");
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "撤销验证"));
            client.Command("@undo"); Check(client.State.Layouts.Single(item => item.Current).Model, "Undoing a new layout did not return to model space");
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "打印验证"));
            var layout = client.State.Layouts.Single(item => item.Name == "打印验证");
            CadNative.Check(CadNative.wl_layout(client.Handle, layout.Id));
            client.Command("TEXT"); Point(10, 10); SubmitText(false, "图纸空间文字", 3);
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 1, layout.Id, ""));
            Check(!client.State.Layouts.Any(item => item.Name == "打印验证"), "Layout deletion failed");
            client.Command("@undo"); Check(client.State.Layouts.Any(item => item.Name == "打印验证"), "Layout deletion undo failed");
            await client.SaveAsync(Path.Combine(root, "图层布局.dwg")); Close();

            New(); Draw("LINE", 0, 0, 1000, 0); client.Command("@fit");
            client.Command("OFFSET"); Point(500, 0); Point(500, 200); client.Command("@cancel");
            Check(client.State.EntityCount == 2, "Offset did not create a parallel copy"); client.Command("@undo");
            client.Command("BREAK"); Point(300, 0); Point(700, 0); client.Command("@cancel");
            Check(client.State.EntityCount == 2, "Break did not split the line"); client.Command("@undo");
            Draw("LINE", 0, 0, 0, 1000); client.Command("@fit");
            CadNative.Check(CadNative.wl_corner_settings(client.Handle, 200, 200, 100));
            client.Command("FILLET"); Point(500, 0); Point(0, 500); client.Command("@cancel");
            Check(client.State.EntityCount == 3, "Fillet did not create a tangent arc"); client.Command("@undo");
            client.Command("CHAMFER"); Point(500, 0); Point(0, 500); client.Command("@cancel");
            Check(client.State.EntityCount == 3, "Chamfer did not create its edge"); client.Command("@undo"); Close();

            New(); Draw("LINE", 0, 0, 800, 0); Draw("LINE", 1000, -500, 1000, 500); client.Command("@fit");
            client.Command("EXTEND"); Point(700, 0); client.Command("@cancel"); Click(250, 0);
            double EndX() => double.Parse(client.State.Properties.Single(item => item.Name == "端点 X").Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(Math.Abs(EndX() - 1000) < .01, "Extend did not reach its boundary"); client.Command("@cancel");
            Draw("LINE", 500, -500, 500, 500); client.Command("@fit");
            client.Command("TRIM"); Point(750, 0); client.Command("@cancel"); Click(250, 0);
            Check(Math.Abs(EndX() - 500) < .01, "Trim did not remove the clicked segment"); client.Command("@cancel"); client.Command("@undo");
            client.Command("STRETCH"); Point(1100, 100); Point(900, -100); Point(1000, 0); Point(1200, 200); client.Command("@cancel"); Click(250, 200d / 1200 * 250);
            Check(Math.Abs(EndX() - 1200) < .01, "Stretch did not move the enclosed endpoint"); client.Command("@cancel"); client.Command("@undo");
            Click(250, 0); Check(Math.Abs(EndX() - 1000) < .01, "Stretch undo lost the original endpoint"); client.Command("@cancel"); Close();

            client.Activate(original); workspace.SuppressToolDialogs = false; workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-extended-result.txt"), "CAD_EXTENDED_OK curves point ray xline mline solid wipeout hatch text mtext table dimensions leaders arrays blocks layers layouts offset break fillet chamfer trim extend stretch undo redo DWG DXF WPF commit cancel");
        }
        private static void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    }
}
