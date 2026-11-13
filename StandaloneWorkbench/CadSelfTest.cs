using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root)
        {
            var client = workspace.Client;
            var source = client.State.Documents.First(item => item.Id == client.State.ActiveId);
            var sourceCount = client.State.EntityCount;
            workspace.NewDrawing();
            var toolDocument = client.State.ActiveId;
            client.Command("PLINE"); client.Command("0,0"); client.Command("500,0"); client.Command("500,500"); client.Command("");
            Check(client.State.EntityCount == 1, "Polyline did not commit on Enter");
            client.Command("RECTANG"); client.Command("1000,0"); client.Command("1500,500"); client.Command("@cancel");
            Check(client.State.EntityCount == 2, "Rectangle tool did not commit");
            client.Command("A"); client.Command("2000,0"); client.Command("2250,250"); client.Command("2500,0"); client.Command("@cancel");
            Check(client.State.EntityCount == 3, "Three-point arc tool did not commit");
            client.Command("@undo"); client.Command("@redo");
            Check(client.State.EntityCount == 3, "Arc undo/redo failed");
            client.Close(toolDocument);
            client.Activate(source.Id);
            if (Path.GetExtension(source.Path).Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                client.Command("CIRCLE");
                CadNative.Check(CadNative.wl_point(client.Handle, 10000, 10000));
                CadNative.Check(CadNative.wl_point(client.Handle, 10300, 10000));
                await client.SaveAsync(Path.Combine(root, "source-preserved.dwg"));
                sourceCount = client.State.EntityCount;
            }
            workspace.NewDrawing();
            await Task.Delay(100);
            var emptyId = client.State.ActiveId;
            Check(client.State.EntityCount == 0, "New drawing contains unexpected entities");
            client.Command("LINE");
            CadNative.Check(CadNative.wl_point(client.Handle, 0, 0));
            CadNative.Check(CadNative.wl_point(client.Handle, 1000, 0));
            client.Command("@cancel");
            Check(client.State.EntityCount == 1, "Line tool did not commit");
            client.Command("CIRCLE");
            CadNative.Check(CadNative.wl_point(client.Handle, 2000, 1000));
            CadNative.Check(CadNative.wl_point(client.Handle, 2300, 1000));
            Check(client.State.EntityCount == 2, "Circle tool did not commit");
            client.Command("@cancel");
            client.Command("@all");
            Check(client.State.SelectionCount == 2, "Selection did not reach native objects");
            client.Command("MOVE");
            client.Command("0,0");
            client.Command("500,250");
            client.Command("@undo");
            Check(client.State.CanRedo && client.State.EntityCount == 2, "Move undo failed");
            client.Command("@redo");
            client.Command("@all");
            client.Command("COPY");
            client.Command("0,0"); client.Command("2000,0"); client.Command("@cancel");
            Check(client.State.EntityCount == 4, "Copy did not create separate entities");
            client.Command("@undo");
            Check(client.State.EntityCount == 2, "Copy must undo as one operation");
            client.Command("@redo");
            Check(client.State.EntityCount == 4, "Copy redo failed");
            client.Command("@undo");
            client.Command("@cancel");
            client.Command("@fit");
            await Task.Delay(150);
            var host = (CadViewport)workspace.FindName("Viewport");
            var input = (TextBox)workspace.FindName("CommandInput");
            input.Clear();
            SendMessage(host.Handle, 0x0102, new IntPtr('L'), IntPtr.Zero);
            Check(input.Text == "L", "Native keyboard input did not reach the WPF command line");
            input.Clear();
            CadNative.Check(CadNative.wl_world_to_screen(client.Handle, 1000, 250, out var x, out var y));
            var position = new IntPtr((y << 16) | (x & 0xffff));
            SendMessage(host.Handle, 0x0201, new IntPtr(1), position);
            SendMessage(host.Handle, 0x0202, IntPtr.Zero, position);
            await Task.Delay(80);
            Check(client.State.SelectionCount == 1, "Native viewport mouse picking failed");
            var properties = client.State.Properties;
            var startX = properties.FirstOrDefault(item => item.Name == "起点 X");
            Check(startX != null, "Selected line properties are unavailable");
            CadNative.Check(CadNative.wl_property(client.Handle, startX.Name, "650", startX.Kind));
            Check(Math.Abs(double.Parse(client.State.Properties.First(item => item.Name == startX.Name).Value, System.Globalization.CultureInfo.InvariantCulture) - 650) < .001, "Property edit was not applied");
            client.Command("@undo");
            var dwg = Path.Combine(root, "原生编辑 与 保存.dwg");
            var dxf = Path.Combine(root, "原生编辑 与 保存.dxf");
            await client.SaveAsync(dwg);
            Check(File.Exists(dwg) && !client.State.Documents.First(item => item.Id == emptyId).Dirty, "DWG save did not clear dirty state");
            await client.SaveAsync(dxf);
            Check(File.Exists(dxf), "DXF save failed");
            workspace.RefreshState();
            await Task.Delay(100);
            client.Capture(Path.Combine(root, "native-cad-viewport.png"));
            client.Close(emptyId);
            await client.OpenAsync(dwg);
            Check(client.State.EntityCount == 2, "DWG reopen changed the entity count");
            var reopened = client.State.ActiveId;
            await client.OpenAsync(dxf);
            Check(client.State.EntityCount == 2, "DXF reopen changed the entity count");
            client.Close(client.State.ActiveId);
            client.Activate(reopened);
            client.Command("@all"); client.Command("@delete");
            Check(client.State.EntityCount == 0, "Delete selection failed");
            client.Command("@undo");
            Check(client.State.EntityCount == 2, "Delete undo lost entities");
            var originalBytes = File.ReadAllBytes(dwg);
            File.AppendAllText(dwg, "external change");
            var conflict = false;
            try { await client.SaveAsync(dwg); } catch (IOException) { conflict = true; }
            Check(conflict && File.ReadAllBytes(dwg).Length > originalBytes.Length && !client.IsBusy, "External file change was overwritten or left the UI busy");
            File.WriteAllBytes(dwg, originalBytes);
            File.SetAttributes(dwg, File.GetAttributes(dwg) | FileAttributes.ReadOnly);
            var denied = false;
            try { await client.SaveAsync(dwg); } catch (IOException) { denied = true; } catch (UnauthorizedAccessException) { denied = true; }
            finally { File.SetAttributes(dwg, File.GetAttributes(dwg) & ~FileAttributes.ReadOnly); }
            Check(denied && !client.IsBusy && File.ReadAllBytes(dwg).SequenceEqual(originalBytes), "Read-only save changed the target or left the editor busy");
            Check(!Directory.GetFiles(root, "*.wanluo-*").Any(), "Failed save left a temporary drawing");
            client.Close(reopened);
            using (var sharedDrawing = File.Open(dwg, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                await client.OpenAsync(dwg);
                Check(client.State.EntityCount == 2, "Shared drawing did not open while another writer held it");
                client.Close(client.State.ActiveId);
            }
            client.Activate(source.Id);
            Check(client.State.EntityCount == sourceCount, "Switching documents lost the original drawing");
            var invalid = Path.Combine(root, "native-invalid.dwg");
            File.WriteAllText(invalid, "invalid drawing");
            var rejected = false;
            try { await client.OpenAsync(invalid); } catch (IOException) { rejected = true; }
            Check(rejected && !client.IsBusy && client.State.ActiveId == source.Id, "Failed opening replaced the active document");
            var opening = client.OpenAsync(dwg);
            client.CancelOpen();
            try { await opening; } catch (IOException) { }
            Check(!client.IsBusy && client.State.ActiveId == source.Id, "Cancelled opening changed the active document");
            var layer = client.State.Layers.First();
            CadNative.Check(CadNative.wl_layer(client.Handle, layer.Id, layer.Visible ? 0 : 1, layer.Locked ? 1 : 0, 0));
            Check(client.State.Layers.First(item => item.Id == layer.Id).Visible != layer.Visible, "Layer visibility did not change");
            client.Command("@undo");
            Check(client.State.Layers.First(item => item.Id == layer.Id).Visible == layer.Visible, "Layer visibility undo failed");
            var layouts = client.State.Layouts;
            var initialLayout = layouts.FirstOrDefault(item => item.Current);
            foreach (var layout in layouts)
            {
                CadNative.Check(CadNative.wl_layout(client.Handle, layout.Id));
                Check(client.State.Layouts.First(item => item.Id == layout.Id).Current, "Layout activation failed");
            }
            if (initialLayout != null) CadNative.Check(CadNative.wl_layout(client.Handle, initialLayout.Id));
            client.Command("@cancel");
            client.Command("@fit");
            workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-editor-result.txt"), "CAD_EDITOR_OK nativeDocument nativeMousePick keyboard line polyline rectangle arc circle move copy property undo redo dwg dxf reopen conflict readOnly cancel invalid layers layouts");
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    }
}
