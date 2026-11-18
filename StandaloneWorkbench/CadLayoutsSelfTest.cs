using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadLayoutsSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.SuppressToolDialogs = true;
            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void Form(Action show, string name, Action<CadParameterWindow> fill)
            {
                Action<CadParameterWindow> opened = form => form.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fill(form); form.UpdateLayout(); capture(form, Path.Combine(root, name + ".png"));
                    form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!form.IsVisible, "Layout form was rejected " + name);
                }));
                workspace.ParameterWindowOpened += opened;
                try { show(); } finally { workspace.ParameterWindowOpened -= opened; }
            }
            void Mouse(uint message, double x, double y)
            {
                var host = (CadViewport)workspace.FindName("Viewport");
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                SendMessage(host.Handle, message, message == 0x0201 ? new IntPtr(1) : IntPtr.Zero, new IntPtr((py << 16) | (px & 0xffff)));
            }
            void Click(double x, double y) { Mouse(0x0201, x, y); Mouse(0x0202, x, y); }
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            client.Command("LINE"); Point(0, 0); Point(10000, 6000); client.Command(""); client.Command("@cancel");
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "视口冻结层", 0, -3));
            var frozenLayer = client.State.Layers.Single(item => item.Name == "视口冻结层");
            client.Command("LINE"); Point(0, 6000); Point(10000, 0); client.Command(""); client.Command("@cancel");
            CadNative.Check(CadNative.wl_layer(client.Handle, 0, 1, 0, 1));
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "布置图")); workspace.RefreshState();
            Form(() => workspace.LayoutSettings(), "layout-page", form =>
            {
                Set(form, "name", "平面布置"); Set(form, "width", "594"); Set(form, "height", "420"); Set(form, "position", "1");
            });
            Check(client.State.Layouts[1].Name == "平面布置" && client.State.Layouts[1].Width == 594, "Layout name/page/order did not update");
            client.Command("@undo"); Check(client.State.Layouts.Any(item => item.Name == "布置图"), "Layout settings undo failed"); client.Command("@redo");
            Form(() => workspace.PaperViewport(), "viewport-create", form =>
            {
                ((ComboBox)form.Inputs["method"]).SelectedIndex = 1;
                ((CheckBox)form.Inputs["fit"]).IsChecked = false;
                Set(form, "x", "210"); Set(form, "y", "148"); Set(form, "width", "300"); Set(form, "height", "180");
                Set(form, "modelX", "5000"); Set(form, "modelY", "3000"); Set(form, "twist", "30");
                ((ComboBox)form.Inputs["ratio"]).Text = "1:50";
                var layers = (ListBox)form.Inputs["frozen"];
                layers.SelectedItems.Add(layers.Items.Cast<CadLayer>().Single(item => item.Id == frozenLayer.Id));
            });
            var first = CadNative.PaperViewports(client.Handle).Single();
            Check(first.Width == 300 && first.Ratio == 50 && first.Locked && first.Frozen.Contains(frozenLayer.Id), "Viewport settings were lost " + System.Text.Json.JsonSerializer.Serialize(first));
            client.Command("@undo"); Check(CadNative.PaperViewports(client.Handle).Count == 0, "Viewport creation undo failed"); client.Command("@redo");
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var file = Path.Combine(root, "布局视口" + extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document = client.State.ActiveId;
                var layout = client.State.Layouts.Single(item => item.Name == "平面布置");
                CadNative.Check(CadNative.wl_layout(client.Handle, layout.Id));
                first = CadNative.PaperViewports(client.Handle).Single();
                Check(Math.Abs(first.Twist - 30) < .0001 && first.Frozen.Count == 1 && Math.Abs(first.Ratio - 50) < .0001, "Viewport round-trip failed " + extension + " " + System.Text.Json.JsonSerializer.Serialize(first));
            }
            client.Command("@fit"); await Task.Delay(100);
            Form(() => workspace.PaperViewport(), "viewport-two-click", form =>
            {
                ((CheckBox)form.Inputs["fit"]).IsChecked = false;
                Set(form, "modelX", "5000"); Set(form, "modelY", "3000");
                ((ComboBox)form.Inputs["ratio"]).Text = "1:100";
            });
            Click(10, 10); Mouse(0x0200, 100, 90);
            Check(CadNative.PaperViewports(client.Handle).Count == 1, "First mouse release committed viewport creation");
            Click(100, 90); client.Command("@cancel");
            Check(CadNative.PaperViewports(client.Handle).Count == 2, "Second viewport corner did not commit");
            first = CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2);
            Form(() => workspace.PaperViewport(true), "viewport-edit", form =>
            {
                ((ComboBox)form.Inputs["viewport"]).SelectedItem = ((ComboBox)form.Inputs["viewport"]).Items.Cast<CadPaperViewport>().Single(item => item.Number == 2);
                Set(form, "width", "330"); Set(form, "height", "200"); Set(form, "modelX", "6000"); Set(form, "modelY", "4000"); Set(form, "twist", "90");
                ((ComboBox)form.Inputs["ratio"]).Text = "1:75";
                ((CheckBox)form.Inputs["locked"]).IsChecked = false;
            });
            client.Command("@undo"); Check(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).Width == 300, "Viewport edit undo failed"); client.Command("@redo");
            client.Command("@fit"); await Task.Delay(120);
            var navHost = (CadViewport)workspace.FindName("Viewport");
            void NavigateMouse(uint message, double x, double y, int buttons = 0)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                SendMessage(navHost.Handle, message, new IntPtr(buttons), new IntPtr((py << 16) | (px & 0xffff)));
            }
            void Wheel(double x, double y)
            {
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                var screen = new ScreenPoint { X = px, Y = py }; ClientToScreen(navHost.Handle, ref screen);
                SendMessage(navHost.Handle, 0x020A, new IntPtr(120 << 16), new IntPtr((screen.Y << 16) | (screen.X & 0xffff)));
            }
            var baseline = CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2);
            workspace.NavigateViewport(); Click(210, 148);
            NavigateMouse(0x0207, 210, 148, 16); NavigateMouse(0x0200, 230, 158, 16); NavigateMouse(0x0208, 230, 158);
            var panned = CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2);
            Check(Math.Abs(panned.ModelX - baseline.ModelX) > 100 && panned.Ratio == baseline.Ratio && panned.X == baseline.X && panned.Width == baseline.Width, "Content pan changed paper frame or failed");
            Wheel(210, 148);
            var zoomed = CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2);
            Check(Math.Abs(zoomed.Ratio - baseline.Ratio / 1.2) < .0001 && zoomed.Width == baseline.Width, "Content wheel zoom failed");
            File.WriteAllLines(Path.Combine(root, "viewport-navigation-center.txt"), new[] {
                zoomed.ModelX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                zoomed.ModelY.ToString("R", System.Globalization.CultureInfo.InvariantCulture) });
            client.Command("@cancel");
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "视口内容调整" + extension));
            client.Command("@undo"); Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).Ratio - baseline.Ratio) < .0001, "Viewport zoom undo failed");
            client.Command("@undo"); Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).ModelX - baseline.ModelX) < .0001, "Viewport pan undo failed");
            workspace.NavigateViewport(); Click(210, 148);
            NavigateMouse(0x0207, 210, 148, 16); NavigateMouse(0x0200, 240, 148, 16);
            client.Command("@undo");
            Check(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).Width == 300, "Toolbar undo failed during content pan preview");
            NavigateMouse(0x0208, 240, 148); client.Command("@redo");
            Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).ModelX - baseline.ModelX) < .0001, "Preview cancellation overwrote redo");
            workspace.NavigateViewport(); Click(210, 148);
            NavigateMouse(0x0207, 210, 148, 16); NavigateMouse(0x0200, 240, 148, 16);
            SendMessage(navHost.Handle, 0x0100, new IntPtr(27), IntPtr.Zero);
            Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).ModelX - baseline.ModelX) < .0001, "Cancel left a viewport pan preview");
            client.Command("@cancel");
            workspace.NavigateViewport(); Click(50, 50); Wheel(50, 50); client.Command("@cancel");
            Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number != 2).Ratio - 100) < .0001, "Locked viewport navigation changed scale");
            Click(45, 148);
            Check(client.State.SelectionCount == 1, "Viewport border could not be selected");
            capture(Window.GetWindow(workspace), Path.Combine(root, "viewport-grips-before.png"));
            Click(375, 248); Mouse(0x0200, 400, 268); await Task.Delay(80);
            capture(Window.GetWindow(workspace), Path.Combine(root, "viewport-grips-preview.png"));
            Click(400, 268);
            var resized = CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2);
            // Mouse input is quantized to viewport pixels before returning to paper coordinates.
            Check(Math.Abs(resized.Width - 355) < 5 && Math.Abs(resized.Height - 220) < 5 && Math.Abs(resized.Ratio - 75) < .001, "Viewport grip resize failed " + System.Text.Json.JsonSerializer.Serialize(resized));
            client.Command("@undo"); Check(Math.Abs(CadNative.PaperViewports(client.Handle).Single(item => item.Number == 2).Width - 330) < .001, "Viewport grip undo failed"); client.Command("@redo");
            client.Command("@cancel");
            client.Command("@all"); client.Command("COPY"); Point(0, 0); Point(1000, 0); client.Command("@cancel");
            var copiedViews = CadNative.PaperViewports(client.Handle);
            Check(copiedViews.Count == 4 && copiedViews.Select(item => item.Number).Distinct().Count() == 4, "Normal Copy duplicated viewport numbers");
            client.Command("@undo");
            client.Command("@all"); client.Command("@copybase"); Point(0, 0); client.Command("@cancel");
            client.Command("@paste"); Point(1000, 1000); client.Command("@cancel");
            copiedViews = CadNative.PaperViewports(client.Handle);
            Check(copiedViews.Count == 4 && copiedViews.Select(item => item.Number).Distinct().Count() == 4, "Clipboard paste duplicated viewport numbers");
            client.Command("@undo");
            var activeLayout = client.State.Layouts.Single(item => item.Current);
            Form(() => workspace.LayoutSettings(true), "layout-copy", form => { Set(form, "name", "复制布置"); Set(form, "position", "1"); });
            Check(client.State.Layouts[1].Name == "复制布置" && CadNative.PaperViewports(client.Handle).Count == 2, "Copied layout lost paper-space entities");
            client.Command("@undo"); Check(!client.State.Layouts.Any(item => item.Name == "复制布置"), "Layout copy undo failed"); client.Command("@redo");
            CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "复制布置").Id));
            client.Command("@fit"); await Task.Delay(100); capture(Window.GetWindow(workspace), Path.Combine(root, "layout-viewports.png"));
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var file = Path.Combine(root, "布局修改复制" + extension);
                await client.SaveAsync(file); client.Close(document); await client.OpenAsync(file); document = client.State.ActiveId;
                Check(client.State.Layouts[1].Name == "复制布置", "Layout tab order did not persist");
                foreach (var name in new[] { "平面布置", "复制布置" })
                {
                    CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == name).Id));
                    Check(CadNative.PaperViewports(client.Handle).Count == 2, "Layout copy merged or lost paper records");
                }
            }
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                client.Close(document); await client.OpenAsync(Path.Combine(root, "布局修改复制" + extension)); document = client.State.ActiveId;
                var layoutsBeforeDelete = client.State.Layouts.Count;
                CadNative.Check(CadNative.wl_layout_manage(client.Handle, 1, client.State.Layouts.Single(item => item.Name == "复制布置").Id, ""));
                var removedFile = Path.Combine(root, "布局删除" + extension);
                await client.SaveAsync(removedFile); client.Close(document); await client.OpenAsync(removedFile); document = client.State.ActiveId;
                Check(client.State.Layouts.Count == layoutsBeforeDelete - 1 && !client.State.Layouts.Any(item => item.Name == "复制布置"), "Deleted layout was recreated during same-format saving " + extension);
            }
            client.Close(document);
            var fixture = Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "layout-source.dwg");
            await client.OpenAsync(fixture); document = client.State.ActiveId;
            var inchPage = client.State.Layouts.Single(item => item.Name == "英制页面");
            Check(Math.Abs(inchPage.Width - 609.6) < .001 && Math.Abs(inchPage.UnitToMm - 25.4) < .001, "Physical paper millimetres were confused with layout inches");
            CadNative.Check(CadNative.wl_layout(client.Handle, inchPage.Id));
            Form(() => workspace.PaperViewport(), "viewport-inches", form =>
            {
                Check(Math.Abs(form.Number("x") - 12) < .001 && Math.Abs(form.Number("y") - 9) < .001, "Inch paper viewport defaults use the wrong units");
                ((ComboBox)form.Inputs["method"]).SelectedIndex = 1;
            });
            var source = client.State.Layouts.Single(item => item.Name == "原始页面");
            Check(Math.Abs(source.Width - 420) < .001 && Math.Abs(source.Height - 594) < .001, "Rotated physical paper size import failed");
            CadNative.Check(CadNative.wl_layout(client.Handle, source.Id));
            CadNative.Check(CadNative.wl_layout_update(client.Handle, document, source.Key, "改名页面", source.Width, source.Height, 1, 0));
            CadNative.Check(CadNative.wl_layout_update(client.Handle, document, source.Key, "复制页面", source.Width, source.Height, 1, 1));
            foreach (var extension in new[] { ".dwg", ".dxf" }) await client.SaveAsync(Path.Combine(root, "layout-metadata" + extension));
            client.Close(document); client.Activate(original); workspace.SuppressToolDialogs = false; workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-layouts-result.txt"), "CAD_LAYOUTS_OK page rename copy order viewport ratio twist freeze lock grip twoClick undo redo DWG DXF sourceMetadata");
        }
        private static void Set(CadParameterWindow form, string key, string value) => ((TextBox)form.Inputs[key]).Text = value;
        private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
    }
}
