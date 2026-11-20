using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadPerspectiveViewportSelfTest
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr keys, IntPtr point);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);
        [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }

        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var previous = client.State.ActiveId;
            var fixture = Path.Combine(Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT"), "perspective-projection.dwg");
            ulong document = 0;
            try
            {
                foreach (var (suffix, file, edited) in new[]
                {
                    ("源图", fixture, false),
                    ("DWG", Path.Combine(root, "透视视口回读.dwg"), false),
                    ("DXF", Path.Combine(root, "透视视口回读.dxf"), false),
                    ("参数DWG", Path.Combine(root, "透视参数编辑.dwg"), true),
                    ("参数DXF", Path.Combine(root, "透视参数编辑.dxf"), true)
                })
                {
                    await client.OpenAsync(file);
                    document = client.State.ActiveId;
                    CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
                    var viewport = CadNative.PaperViewports(client.Handle).Single(item => item.Perspective);
                    if (viewport.CanInlineEdit || viewport.CanMaximizeEdit) throw new InvalidOperationException("Perspective viewport offered orthographic editing.");
                    client.Command("@fit");
                    await Task.Delay(100);
                    var image = Path.Combine(root, $"透视视口-{suffix}.png");
                    client.Capture(image);
                    var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(image), BitmapCreateOptions.IgnoreImageCache,
                        BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                    bool ColorAt(double x, double y, Func<byte, byte, byte, bool> matches)
                    {
                        CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                        var area = new Int32Rect(px - 3, py - 3, 7, 7);
                        var pixels = new byte[7 * 7 * 4];
                        bitmap.CopyPixels(area, pixels, 7 * 4, 0);
                        for (var i = 0; i < pixels.Length; i += 4)
                            if (matches(pixels[i + 2], pixels[i + 1], pixels[i])) return true;
                        return false;
                    }
                    var focal = Math.Sqrt(300 * 300 + 200 * 200) * (edited ? 1.5 : 1);
                    bool red = ColorAt(200, 100 + 50 * focal / 700, (r,g,b) => r > 160 && g < 100 && b < 100);
                    bool green = ColorAt(edited ? 200 : 180, 100 - 80 * focal / 1200, (r,g,b) => g > 160 && r < 100 && b < 100);
                    bool beyondFarEnd = ColorAt(edited ? 217 : 200, 100 - 80 * focal / 1200, (r,g,b) => g > 160 && r < 100 && b < 100);
                    bool depthClipped = !ColorAt(150, 100, (r,g,b) => b > 150 || (r > 150 && g > 150));
                    if (red == edited || !green || beyondFarEnd || !depthClipped)
                        throw new InvalidOperationException($"Perspective projection pixel check failed ({suffix}, near={red}, far={green}, overflow={beyondFarEnd}, clipped={depthClipped}).");

                    if (suffix == "源图")
                    {
                        await VerifyViewportPropertiesAsync(workspace, viewport, root);
                    }
                    client.Close(document);
                    document = 0;
                }
                await VerifyNavigationAsync(workspace, root);
                await VerifyDrawingAsync(workspace, fixture, root);
            }
            finally
            {
                if (document != 0) client.Close(document);
                client.Activate(previous);
                workspace.RefreshState();
            }
        }

        private static async Task VerifyViewportPropertiesAsync(CadWorkspace workspace, CadPaperViewport viewport, string root)
        {
            var client = workspace.Client;
            var host = (CadViewport)workspace.FindName("Viewport");
            var grid = (DataGrid)workspace.FindName("Properties");
            var inspector = (FrameworkElement)workspace.FindName("InspectorPanel");
            var wasVisible = inspector.IsVisible;
            var previousTab = ((TabControl)workspace.FindName("InspectorTabs")).SelectedIndex;
            workspace.ShowInspector(0);
            try
            {
                workspace.UpdateLayout();
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, viewport.X, viewport.Y + viewport.Height / 2, out var x, out var y));
                var point = new IntPtr((y << 16) | (x & 0xffff));
                void SelectBorder() => CadTestKeyboard.WithModifiers(() =>
                {
                    SendMessage(host.Handle, 0x0201, new IntPtr(1), point);
                    SendMessage(host.Handle, 0x0202, IntPtr.Zero, point);
                });
                SelectBorder();
                workspace.RefreshState();
                CadPaperViewport Current() => CadNative.PaperViewports(client.Handle).Single(item => item.Id == viewport.Id);
                if (!Current().Selected || !Current().Locked)
                    throw new InvalidOperationException("A locked perspective viewport could not be selected from its border.");
                T Editor<T>(string name) where T : DependencyObject
                {
                    var property = grid.Items.OfType<CadProperty>().Single(item => item.Name == name);
                    grid.ScrollIntoView(property);
                    grid.UpdateLayout();
                    return FindControl<T>(grid.Columns[1].GetCellContent(property)) ??
                        throw new InvalidOperationException("Missing property editor: " + name);
                }
                void ChooseLock(string value)
                {
                    var combo = Editor<ComboBox>("视口锁定");
                    if (combo == null || !combo.IsEnabled || (string)combo.SelectedValue != (Current().Locked ? "1" : "0"))
                        throw new InvalidOperationException("The viewport property did not show its current lock state.");
                    combo.SetCurrentValue(ComboBox.SelectedValueProperty, value);
                    if (Current().Locked != (value == "1"))
                        throw new InvalidOperationException($"Choosing lock value {value} did not update the viewport. " +
                            $"Dropdown: {combo.SelectedValue}, status: {((TextBlock)workspace.FindName("Status")).Text}");
                }
                void KeyDown(TextBox input, Key key)
                {
                    var source = PresentationSource.FromVisual(input);
                    var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    input.RaiseEvent(e);
                }
                if (Editor<TextBox>("透视焦距 (mm)").IsEnabled)
                    throw new InvalidOperationException("A locked viewport offered editable perspective optics.");
                ChooseLock("0");
                ChooseLock("1");
                client.Command("@undo"); workspace.RefreshState();
                var unlocked = Current();
                if (unlocked.Locked || unlocked.ModelX != viewport.ModelX || unlocked.ModelY != viewport.ModelY ||
                    unlocked.ModelZ != viewport.ModelZ || unlocked.Ratio != viewport.Ratio || unlocked.Twist != viewport.Twist)
                    throw new InvalidOperationException("Viewport lock undo changed its camera or failed to unlock.");
                client.Command("@redo");
                if (!Current().Locked) throw new InvalidOperationException("Viewport lock redo did not restore the lock.");
                client.Command("@undo"); workspace.RefreshState();

                var width = Editor<TextBox>("视口宽");
                width.SetCurrentValue(TextBox.TextProperty, "315");
                if (Current().Width != viewport.Width) throw new InvalidOperationException("The property changed before the numeric edit committed.");
                KeyDown(width, Key.Enter);
                if (Current().Width != 315) throw new InvalidOperationException("Enter did not commit a numeric property edit.");
                width = Editor<TextBox>("视口宽");
                width.SetCurrentValue(TextBox.TextProperty, "325");
                KeyDown(width, Key.Escape);
                if (Current().Width != 315 || width.Text != "315") throw new InvalidOperationException("Escape did not discard the draft.");
                width.SetCurrentValue(TextBox.TextProperty, "-");
                KeyDown(width, Key.Enter);
                if (Current().Width != 315 || width.Text != "315") throw new InvalidOperationException("Invalid numeric input changed the drawing.");
                width.SetCurrentValue(TextBox.TextProperty, "330");
                width.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, width, host)
                    { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                if (Current().Width != 330) throw new InvalidOperationException("Leaving the property editor did not commit its value.");
                client.Command("@undo"); client.Command("@undo");
                if (Current().Width != viewport.Width) throw new InvalidOperationException("Property edits did not undo one at a time.");
                workspace.RefreshState();
                width = Editor<TextBox>("视口宽");
                width.SetCurrentValue(TextBox.TextProperty, "350");
                client.Command("@cancel"); SelectBorder();
                KeyDown(width, Key.Enter);
                if (!Current().Selected || Current().Width != viewport.Width)
                    throw new InvalidOperationException("A draft was applied after the native selection changed.");
                client.Command("@cancel");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "透视视口回读" + extension));

                SelectBorder(); workspace.RefreshState();
                TextBox EditNumber(string name, string value)
                {
                    var input = Editor<TextBox>(name);
                    if (!input.IsEnabled) throw new InvalidOperationException("Unlocked optics could not be edited: " + name);
                    input.SetCurrentValue(TextBox.TextProperty, value);
                    KeyDown(input, Key.Enter);
                    return Editor<TextBox>(name);
                }
                if (EditNumber("透视焦距 (mm)", "0").Text != "42")
                    throw new InvalidOperationException("An invalid focal length changed the camera.");
                if (EditNumber("透视焦距 (mm)", "63").Text != "63" || Math.Abs(Current().Ratio - viewport.Ratio / 1.5) > 1e-8)
                    throw new InvalidOperationException("The focal length or view height did not update.");
                client.Command("@undo"); workspace.RefreshState();
                if (Editor<TextBox>("透视焦距 (mm)").Text != "42")
                    throw new InvalidOperationException("Focal length undo failed.");
                client.Command("@redo"); workspace.RefreshState();
                var clip = Editor<ComboBox>("前裁剪");
                if (!clip.Items.OfType<ComboBoxItem>().Select(item => item.Content).SequenceEqual(new object[] { "已开启", "已关闭" }))
                    throw new InvalidOperationException("Clipping switches did not display their on/off labels.");
                clip.SetCurrentValue(ComboBox.SelectedValueProperty, "0");
                if (client.State.Properties.Single(property => property.Name == "前裁剪").Value != "0")
                    throw new InvalidOperationException("The front clipping switch did not take effect.");
                client.Command("@undo"); workspace.RefreshState();
                if (Editor<TextBox>("透视焦距 (mm)").Text != "63" || (string)Editor<ComboBox>("前裁剪").SelectedValue != "1")
                    throw new InvalidOperationException("Undo did not restore front clipping.");
                if (EditNumber("前裁剪距目标", "250").Text != "250" || EditNumber("后裁剪距目标", "-300").Text != "-300" ||
                    EditNumber("前裁剪距目标", "-350").Text != "250")
                    throw new InvalidOperationException("Clipping plane edits or ordering validation failed.");
                client.Command("@cancel");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "透视参数编辑" + extension));
            }
            finally
            {
                if (wasVisible) workspace.ShowInspector(previousTab);
                else workspace.ToggleProperties();
                workspace.UpdateLayout();
            }
        }

        private static T FindControl<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T control) return control;
            if (root != null)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
                {
                    var child = FindControl<T>(VisualTreeHelper.GetChild(root, i));
                    if (child != null) return child;
                }
            return null;
        }

        private static async Task VerifyDrawingAsync(CadWorkspace workspace, string fixture, string root)
        {
            var client = workspace.Client;
            var host = (CadViewport)workspace.FindName("Viewport");
            await client.OpenAsync(fixture);
            var document = client.State.ActiveId;
            try
            {
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
                client.Command("@fit");
                var count = client.State.EntityCount;
                var focal = Math.Sqrt(300 * 300 + 200 * 200);
                void SendMouse(uint message, double paperX, double paperY, int keys = 0)
                {
                    CadNative.Check(CadNative.wl_world_to_screen(client.Handle, paperX, paperY, out var x, out var y));
                    var point = new IntPtr((y << 16) | (x & 0xffff));
                    SendMessage(host.Handle, message, new IntPtr(keys), point);
                }
                void Click(double paperX, double paperY)
                {
                    SendMouse(0x0201, paperX, paperY, 1);
                    SendMouse(0x0202, paperX, paperY);
                }
                if (!client.State.Snap) client.Command("@snap");
                client.Command("VPLINE");
                Click(150, 100);                                  // Activate the viewport.
                Click(150 - 120 * focal / 700, 100 + 50 * focal / 700);  // Snap the red line's 3D endpoint.
                Click(330, 100);                                  // Reject a click outside the frame.
                if (client.State.EntityCount != count) throw new InvalidOperationException("A click outside the perspective viewport committed geometry.");
                Click(150, 100);                                  // Intersect the world XY plane.
                client.Command("");
                if (client.State.EntityCount != count + 1) throw new InvalidOperationException("VPLINE did not add a model line.");
                client.Command("@undo");
                if (client.State.EntityCount != count) throw new InvalidOperationException("Perspective line undo failed.");
                client.Command("@redo");
                client.Command("VPPOINT");
                Click(150, 100);
                Click(150, 100 - 80 * focal / 1200);               // Snap the far green midpoint.
                client.Command("@cancel");
                if (client.State.EntityCount != count + 2 || !CadNative.PaperViewports(client.Handle).Single(item => item.Perspective).Locked)
                    throw new InvalidOperationException("Perspective drawing changed the viewport lock or lost the point.");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "透视视口绘图" + extension));
                await VerifySelectionAsync(workspace, Click, focal, root);
                await VerifyTransferAsync(workspace, Click, (x, y) => SendMouse(0x0200, x, y), focal, root);
            }
            finally { client.Close(document); }
        }

        private static async Task VerifyTransferAsync(CadWorkspace workspace, Action<double, double> click, Action<double, double> move,
            double focal, string root)
        {
            var client = workspace.Client;
            var count = client.State.EntityCount;
            var redX = 150 - 120 * focal / 700;
            var redY = 100 + 50 * focal / 700;
            var redPickX = redX + 40;
            var redPickY = redY - .6;
            var greenY = 100 - 80 * focal / 1150;
            var layer = client.State.Layers.Single(item => item.Name == "0");
            void Start(string command)
            {
                if (!workspace.ExecuteCommand(command, true)) throw new InvalidOperationException(command + " did not start.");
                click(150, 100);
            }
            Start("VPMOVE");
            CadNative.Check(CadNative.wl_layer(client.Handle, layer.Id, 1, 1, 0));
            click(redPickX, redPickY);
            await CheckPromptAsync("图层已锁定");
            CadNative.Check(CadNative.wl_layer(client.Handle, layer.Id, 1, 0, 0));
            click(redPickX, redPickY);
            click(redX, redY);
            click(330, 100);
            await CheckPromptAsync("视口可见范围");
            move(redX + 20, redY + 20);
            await CheckPromptAsync("移动目标点");
            client.Capture(Path.Combine(root, "透视移动预览.png"));
            client.Command("@cancel");
            Start("VPMOVE");
            click(redPickX, redPickY);
            click(redX, redY);
            click(redX + 20, redY + 20);
            await CheckPromptAsync("命令");
            if (client.State.EntityCount != count) throw new InvalidOperationException("Perspective move changed the object count.");
            client.Command("@undo"); client.Command("@redo");

            Start("VPCOPY");
            click(150, greenY);
            var centerX = 100 + 10 * Math.Cos(.2) - 15 * Math.Sin(.2);
            var centerY = 200 - 10 * Math.Sin(.2) - 15 * Math.Cos(.2);
            var x = centerX - 80 * Math.Sin(.2);
            var y = centerY - 80 * Math.Cos(.2);
            client.Command(FormattableString.Invariant($"{x:R},{y:R},150"));
            client.Command("@0,0,0");
            if (client.State.EntityCount != count) throw new InvalidOperationException("Perspective copy created a zero-displacement duplicate.");
            client.Command("@70,25,50");
            client.Command("@-50,80,-25");
            client.Command("");
            if (client.State.EntityCount != count + 2) throw new InvalidOperationException("Perspective point copy did not add both destinations.");
            client.Command("@undo");
            if (client.State.EntityCount != count + 1) throw new InvalidOperationException("Perspective copy was not undoable one destination at a time.");
            client.Command("@redo");
            if (client.State.EntityCount != count + 2 || !CadNative.PaperViewports(client.Handle).Single(item => item.Perspective).Locked)
                throw new InvalidOperationException("Perspective copy redo failed or changed the viewport lock.");
            foreach (var extension in new[] { ".dwg", ".dxf" })
                await client.SaveAsync(Path.Combine(root, "透视视口移动复制" + extension));

            async Task CheckPromptAsync(string expected)
            {
                await Task.Delay(80);
                var actual = client.State.Prompt;
                if (!actual.Contains(expected)) throw new InvalidOperationException($"Expected perspective prompt '{expected}', actual '{actual}'.");
            }
        }

        private static async Task VerifySelectionAsync(CadWorkspace workspace, Action<double, double> click, double focal, string root)
        {
            var client = workspace.Client;
            var inspector = (FrameworkElement)workspace.FindName("InspectorPanel");
            var wasVisible = inspector.IsVisible;
            var previousTab = ((TabControl)workspace.FindName("InspectorTabs")).SelectedIndex;
            var grid = (DataGrid)workspace.FindName("Properties");
            try
            {
                if (!workspace.ExecuteCommand("VPPICK") || !inspector.IsVisible)
                    throw new InvalidOperationException("Perspective pick did not open its property panel.");
                workspace.UpdateLayout();
                click(150, 100);
                click(200, 100);
                if (client.State.PropertySelectionId != 0)
                    throw new InvalidOperationException("Perspective selection hit a depth-clipped entity.");
                var lineY = 100 + 50 * focal / 700;
                click(175, lineY);
                var lineId = client.State.PropertySelectionId;
                string Value(string name) => client.State.Properties.Single(item => item.Name == name).Value;
                if (lineId == 0 || client.State.SelectionCount != 1 || Value("端点 Z") != "600.000000")
                    throw new InvalidOperationException("Perspective pick missed the visible line or its original Z.");
                var image = Path.Combine(root, "透视拾取高亮.png");
                client.Capture(image);
                var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(image), BitmapCreateOptions.IgnoreImageCache,
                    BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                // The native crosshair covers the line immediately around the pick.
                CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 195, lineY, out var px, out var py));
                var pixels = new byte[7 * 7 * 4];
                bitmap.CopyPixels(new Int32Rect(px - 3, py - 3, 7, 7), pixels, 7 * 4, 0);
                if (!Enumerable.Range(0, 49).Any(i => pixels[i * 4 + 2] < 150 && pixels[i * 4 + 1] > 150 && pixels[i * 4] > 180))
                    throw new InvalidOperationException("Selected projected line was not highlighted.");

                void Edit(string name, string value)
                {
                    workspace.RefreshState();
                    var property = grid.Items.OfType<CadProperty>().Single(item => item.Name == name);
                    grid.ScrollIntoView(property);
                    grid.UpdateLayout();
                    var field = FindControl<TextBox>(grid.Columns[1].GetCellContent(property));
                    if (field == null || !field.IsEnabled) throw new InvalidOperationException("Missing perspective property editor: " + name);
                    field.SetCurrentValue(TextBox.TextProperty, value);
                    field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field), 0, Key.Enter)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                Edit("端点 Z", "550");
                if (Value("端点 Z") != "550.000000") throw new InvalidOperationException("Perspective line property did not commit.");
                client.Command("@undo");
                if (client.State.PropertySelectionId != lineId || Value("端点 Z") != "600.000000")
                    throw new InvalidOperationException("Perspective property undo dropped the selected line or its original coordinates.");
                client.Command("@redo");
                if (Value("端点 Z") != "550.000000") throw new InvalidOperationException("Perspective property redo failed.");
                click(150, 100 - 80 * focal / 1200);
                if (client.State.PropertySelectionId == 0 || client.State.PropertySelectionId == lineId || Value("位置 Z") != "100.000000")
                    throw new InvalidOperationException("Perspective picking did not select the point over its coincident line.");
                var layer = client.State.Layers.Single(item => item.Name == "0");
                CadNative.Check(CadNative.wl_layer(client.Handle, layer.Id, 1, 1, 0));
                if (client.State.Properties.Any(item => item.Editable))
                    throw new InvalidOperationException("Perspective selection allowed edits on a locked layer.");
                CadNative.Check(CadNative.wl_layer(client.Handle, layer.Id, 1, 0, 0));
                Edit("位置 Z", "150");
                if (Value("位置 Z") != "150.000000") throw new InvalidOperationException("Perspective point property did not commit.");
                client.Command("@cancel");
                if (client.State.SelectionCount != 0 || client.State.PropertySelectionId != 0)
                    throw new InvalidOperationException("Perspective property selection survived exiting its tool.");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "透视视口属性" + extension));
            }
            finally
            {
                client.Command("@cancel");
                if (wasVisible) workspace.ShowInspector(previousTab);
                else workspace.ToggleProperties();
                workspace.UpdateLayout();
            }
        }

        private static async Task VerifyNavigationAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var host = (CadViewport)workspace.FindName("Viewport");
            await client.OpenAsync(Path.Combine(root, "透视视口回读.dwg"));
            var document = client.State.ActiveId;
            try
            {
                CadNative.Check(CadNative.wl_layout(client.Handle, client.State.Layouts.Single(item => item.Name == "Layout1").Id));
                client.Command("@fit");
                CadPaperViewport Viewport() => CadNative.PaperViewports(client.Handle).Single(item => item.Perspective);
                var before = Viewport();
                ScreenPoint Pixel(double x, double y)
                {
                    CadNative.Check(CadNative.wl_world_to_screen(client.Handle, x, y, out var px, out var py));
                    return new ScreenPoint { X = px, Y = py };
                }
                void Mouse(uint message, ScreenPoint point, int keys = 0) =>
                    SendMessage(host.Handle, message, new IntPtr(keys), new IntPtr((point.Y << 16) | (point.X & 0xffff)));
                var center = Pixel(before.X, before.Y);
                var end = Pixel(before.X + 30, before.Y + 20);
                var anchor = Pixel(before.X + 25, before.Y - 15);
                void Select()
                {
                    workspace.NavigateViewport();
                    Mouse(0x0201, center, 1); Mouse(0x0202, center);
                }
                void Pan(bool release)
                {
                    Mouse(0x0207, center, 16); Mouse(0x0200, end, 16);
                    if (release) Mouse(0x0208, end);
                }
                double Distance(CadPaperViewport a, CadPaperViewport b) =>
                    Math.Abs(a.ModelX - b.ModelX) + Math.Abs(a.ModelY - b.ModelY) + Math.Abs(a.ModelZ - b.ModelZ);
                void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

                Select(); Pan(false);
                Check(Distance(Viewport(), before) > 1, "Perspective pan preview did not move");
                Mouse(0x001F, end); Mouse(0x0208, end);
                Check(Distance(Viewport(), before) < 1e-7, "Perspective pan survived lost capture");
                Select(); Pan(true);
                var panned = Viewport();
                Check(Distance(panned, before) > 1 && panned.Ratio == before.Ratio, "Perspective pan did not commit");
                var wheel = anchor;
                Check(ClientToScreen(host.Handle, ref wheel), "Could not send perspective wheel at the paper anchor");
                Mouse(0x020A, wheel, 120 << 16);
                var zoomed = Viewport();
                Check(Distance(zoomed, panned) > 1 && Math.Abs(zoomed.Ratio - before.Ratio / 1.2) < 1e-7 &&
                    zoomed.X == before.X && zoomed.Y == before.Y && zoomed.Width == before.Width && zoomed.Height == before.Height &&
                    Pixel(before.X, before.Y).X == center.X && Pixel(before.X, before.Y).Y == center.Y,
                    "Perspective zoom changed the paper frame or failed to adjust its view field");
                client.Command("@cancel"); client.Command("@undo");
                Check(Distance(Viewport(), panned) < 1e-7 && Math.Abs(Viewport().Ratio - before.Ratio) < 1e-7, "Perspective zoom undo failed");
                client.Command("@undo");
                Check(Distance(Viewport(), before) < 1e-7, "Perspective pan undo failed");
                client.Command("@redo"); client.Command("@redo");
                Check(Distance(Viewport(), zoomed) < 1e-7 && Math.Abs(Viewport().Ratio - zoomed.Ratio) < 1e-7, "Perspective navigation redo failed");

                // Predict a visible red point at the new lens and center and verify the rendered result.
                var angle = before.Twist * Math.PI / 180;
                var dx = zoomed.ModelX - before.ModelX;
                var dy = zoomed.ModelY - before.ModelY;
                var shiftX = dx * Math.Cos(angle) - dy * Math.Sin(angle);
                var shiftY = dx * Math.Sin(angle) + dy * Math.Cos(angle);
                var scale = 1.2 * Math.Sqrt(300 * 300 + 200 * 200) / 700;
                var redPoint = Pixel(before.X - shiftX * scale, before.Y + (50 - shiftY) * scale);
                var image = Path.Combine(root, "透视视口导航.png");
                client.Capture(image);
                var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(image), BitmapCreateOptions.IgnoreImageCache,
                    BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
                var pixels = new byte[7 * 7 * 4];
                bitmap.CopyPixels(new Int32Rect(redPoint.X - 3, redPoint.Y - 3, 7, 7), pixels, 7 * 4, 0);
                Check(Enumerable.Range(0, 49).Any(i => pixels[i * 4 + 2] > 160 && pixels[i * 4 + 1] < 100 && pixels[i * 4] < 100),
                    "Perspective renderer did not use the navigated camera");
                foreach (var extension in new[] { ".dwg", ".dxf" })
                    await client.SaveAsync(Path.Combine(root, "透视视口导航" + extension));
            }
            finally { client.Close(document); }
        }
    }
}
