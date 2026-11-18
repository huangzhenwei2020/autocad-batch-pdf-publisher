using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadStylesSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client;
            var original = client.State.ActiveId;
            workspace.SuppressToolDialogs = true;
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            var initial = CadNative.StyleState(client.Handle);
            void Form(string command, Action<CadParameterWindow> edit)
            {
                client.Command(command);
                Action<CadParameterWindow> opened = form =>
                {
                    form.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        edit(form);
                        form.UpdateLayout();
                        if (form.Inputs.TryGetValue("elements", out var input))
                        {
                            var grid = (DataGrid)input;
                            Check(grid.Columns.All(column => column.ActualWidth > 80), "Style element grid columns collapsed");
                        }
                        capture(form, Path.Combine(root, command + "-styles.png"));
                        form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(!form.IsVisible, command + " style form rejected valid parameters");
                    }));
                };
                workspace.ParameterWindowOpened += opened;
                try { workspace.ShowToolDialog(CadNative.DialogState(client.Handle)); }
                finally { workspace.ParameterWindowOpened -= opened; }
            }
            Form("STYLE", form =>
            {
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 2;
                Set(form, "name", "Standard");
                form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(form.IsVisible, "Duplicate style name closed its parameter form");
                Set(form, "name", "工程正文"); Set(form, "font", "arial.ttf"); Set(form, "bigFont", "");
                Set(form, "height", "250"); Set(form, "width", "0.8"); Set(form, "oblique", "12");
            });
            var text = CadNative.StyleState(client.Handle).Text.Single(item => item.Name == "工程正文");
            Check(CadNative.StyleState(client.Handle).CurrentText == text.Id, "New text style was not made current");
            client.Command("@undo"); Check(CadNative.StyleState(client.Handle).Text.Count == initial.Text.Count, "Style creation undo failed");
            client.Command("@redo"); Check(CadNative.StyleState(client.Handle).CurrentText == text.Id, "Style redo lost current style");
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 1, text.Id, text.Name, text.Font, "", 300, .9, 15));
            client.Command("@undo"); Check(CadNative.StyleState(client.Handle).Text.Single(item => item.Id == text.Id).Height == 250, "Style parameter undo failed");
            var rejected = CadNative.wl_text_style_manage(client.Handle, document, 0, text.Id, text.Name, text.Font, "", 100, 1, 0);
            Check(rejected == 0 && CadNative.StyleState(client.Handle).Text.Count == initial.Text.Count + 1, "Duplicate style changed table");
            client.Command("TEXT"); Point(0, 0);
            var request = CadNative.DialogState(client.Handle);
            Check(request.Height == 250, "Fixed text height was not used by TEXT");
            CadNative.Check(CadNative.wl_text_submit(client.Handle, document, 0, "样式文字", 250, 0, 0));
            Form("MLSTYLE", form =>
            {
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 2;
                var grid = (DataGrid)form.Inputs["elements"];
                var editable = (System.ComponentModel.IEditableCollectionView)grid.Items;
                var pending = (CadMLineRow)editable.AddNew(); pending.Offset = "1"; pending.Color = "ByLayer";
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 1;
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 2;
                Set(form, "name", "三线轮廓"); Set(form, "description", "三条元素线");
                var rows = (System.Collections.ObjectModel.ObservableCollection<CadMLineRow>)((DataGrid)form.Inputs["elements"]).ItemsSource;
                rows.Clear();
                rows.Add(new CadMLineRow { Offset = "0.5", Color = "ByLayer" });
                rows.Add(new CadMLineRow { Offset = "0", Color = "#FF0000" });
                rows.Add(new CadMLineRow { Offset = "-0.5", Color = "ByLayer" });
                ((CheckBox)form.Inputs["start"]).IsChecked = true;
                ((CheckBox)form.Inputs["end"]).IsChecked = true;
            });
            CadNative.Check(CadNative.wl_mline_settings(client.Handle, 200, 1));
            DrawMline(0);
            // Preserve a style that has never been assigned to any entity.
            var current = CadNative.StyleState(client.Handle);
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 0, text.Id, "未用文字样式", "arial.ttf", "", 0, 1, 0));
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 2, text.Id, "", "", "", 0, 1, 0));
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var path = Path.Combine(root, "cad-styles" + extension);
                await client.SaveAsync(path);
                client.Close(document);
                await client.OpenAsync(path); document = client.State.ActiveId;
                var loaded = CadNative.StyleState(client.Handle);
                Check(loaded.Text.Any(item => item.Name == "未用文字样式"), "Unused named text style was lost on saving");
                Check(loaded.Text.Single(item => item.Id == loaded.CurrentText).Name == "工程正文", "Current text style did not round-trip");
                var mline = loaded.MLine.Single(item => item.Name == "三线轮廓");
                Check(loaded.CurrentMLine == mline.Id && mline.Elements.Count == 3 && mline.StartCap && mline.EndCap, "MLine named/current style did not round-trip");
                Check(mline.Elements[1].Color == 0xFF0000, "MLine element color was lost");
                var elements = CadWorkspace.ReadMLineElements(new[] {
                    new CadMLineRow { Offset = "0.75", Color = "ByLayer" }, new CadMLineRow { Offset = "0", Color = "#00FF00" },
                    new CadMLineRow { Offset = "-0.75", Color = "ByLayer" } });
                CadNative.Check(CadNative.wl_mline_style_manage(client.Handle, document, 1, mline.Id, mline.Name, "修改后", elements, elements.Length, 1, 1, 0));
                DrawMline(1000);
                var edited = Path.Combine(root, "cad-styles-edited" + extension);
                await client.SaveAsync(edited); client.Close(document);
                await client.OpenAsync(edited); document = client.State.ActiveId;
                var modified = CadNative.StyleState(client.Handle);
                Check(modified.MLine.Single(item => item.Name == "三线轮廓").Elements[0].Offset == .75, "Edited style did not save");
                Check(modified.MLine.Any(item => item.Name.StartsWith("三线轮廓_Snapshot_") && item.Elements[0].Offset == .5), "Original MLine style snapshot was lost");
                if (extension == ".dwg")
                {
                    client.Close(document); await client.OpenAsync(Path.Combine(root, "cad-styles.dwg")); document = client.State.ActiveId;
                }
            }
            client.Command("STYLE");
            var cancel = CadNative.DialogState(client.Handle);
            CadNative.Check(CadNative.wl_dialog_cancel(client.Handle, document, cancel.Kind));
            Check(string.IsNullOrEmpty(CadNative.DialogState(client.Handle).Kind), "Style cancellation left a dialog request");
            client.Close(document);
            workspace.NewDrawing(); document = client.State.ActiveId;
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 0, 0, "待改名文字样式", "arial.ttf", "", 150, 1, 0));
            client.Command("TEXT"); Point(0, 0);
            CadNative.Check(CadNative.wl_text_submit(client.Handle, document, 0, "重命名验证", 150, 0, 0));
            var sourceElements = new[] { new CadMLineElement { Offset = .5, Color = -1 }, new CadMLineElement { Offset = -.5, Color = -1 } };
            CadNative.Check(CadNative.wl_mline_style_manage(client.Handle, document, 0, 0, "待改名多线样式", "", sourceElements, 2, 0, 0, 0));
            DrawMline(0);
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 0, 0, "待删文字样式", "arial.ttf", "", 0, 1, 0));
            CadNative.Check(CadNative.wl_mline_style_manage(client.Handle, document, 0, 0, "待删多线样式", "", sourceElements, 2, 0, 0, 0));
            var beforeRename = Path.Combine(root, "named-style-source.dwg"); await client.SaveAsync(beforeRename); client.Close(document);
            await client.OpenAsync(beforeRename); document = client.State.ActiveId;
            var named = CadNative.StyleState(client.Handle);
            var textId = named.Text.Single(item => item.Name == "待改名文字样式").Id;
            var lineId = named.MLine.Single(item => item.Name == "待改名多线样式").Id;
            Check(CadNative.wl_text_style_manage(client.Handle, document, 4, textId, "", "", "", 0, 1, 0) == 0, "A referenced text style was removed");
            Check(CadNative.wl_mline_style_manage(client.Handle, document, 4, lineId, "", "", null, 0, 0, 0, 0) == 0, "A referenced MLine style was removed");
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 3, textId, "已改名文字样式", "", "", 0, 1, 0));
            client.Command("@undo"); Check(CadNative.StyleState(client.Handle).Text.Any(item => item.Name == "待改名文字样式"), "Style rename undo failed"); client.Command("@redo");
            CadNative.Check(CadNative.wl_mline_style_manage(client.Handle, document, 3, lineId, "已改名多线样式", "", null, 0, 0, 0, 0));
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 4, named.Text.Single(item => item.Name == "待删文字样式").Id, "", "", "", 0, 1, 0));
            CadNative.Check(CadNative.wl_mline_style_manage(client.Handle, document, 4, named.MLine.Single(item => item.Name == "待删多线样式").Id, "", "", null, 0, 0, 0, 0));
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var path = Path.Combine(root, "named-style-management" + extension);
                await client.SaveAsync(path); client.Close(document); await client.OpenAsync(path); document = client.State.ActiveId;
                var loaded = CadNative.StyleState(client.Handle);
                Check(loaded.Text.Any(item => item.Name == "已改名文字样式") && loaded.MLine.Any(item => item.Name == "已改名多线样式"), "Style rename was not persisted");
                Check(!loaded.Text.Any(item => item.Name.StartsWith("待")) && !loaded.MLine.Any(item => item.Name.StartsWith("待")), "Renamed/deleted source style survived saving");
            }
            client.Close(document); client.Activate(original);
            workspace.SuppressToolDialogs = false; workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-styles-result.txt"), "CAD_STYLES_OK WPF create edit current undo redo duplicate fixedHeight elementColor unusedStyle DWG DXF snapshot cancel");

            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void DrawMline(double y) { client.Command("MLINE"); Point(0, y + 1000); Point(2000, y + 1000); client.Command(""); client.Command("@cancel"); }
        }
        private static void Set(CadParameterWindow form, string key, string value) => ((TextBox)form.Inputs[key]).Text = value;
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
