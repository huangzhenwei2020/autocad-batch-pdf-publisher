using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Wanluo.StandaloneWorkbench
{
    internal static class CadManagementSelfTest
    {
        internal static async Task RunAsync(CadWorkspace workspace, string root, Action<Window, string> capture)
        {
            var client = workspace.Client; var original = client.State.ActiveId;
            workspace.SuppressToolDialogs = true;
            void Point(double x, double y) => CadNative.Check(CadNative.wl_point(client.Handle, x, y));
            void Line() { client.Command("LINE"); Point(0, 0); Point(1000, 0); client.Command(""); }
            void Form(Action show, string name, Action<CadParameterWindow> edit)
            {
                Action<CadParameterWindow> opened = form => form.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    edit(form); form.UpdateLayout(); capture(form, Path.Combine(root, name + ".png"));
                    form.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!form.IsVisible, name + " parameters were rejected");
                }));
                workspace.ParameterWindowOpened += opened;
                try { show(); } finally { workspace.ParameterWindowOpened -= opened; }
            }
            workspace.NewDrawing();
            var document = client.State.ActiveId;
            Form(workspace.LineTypes, "linetype", form =>
            {
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 2;
                Set(form, "name", "工程点划线"); Set(form, "description", "自定义线型"); Set(form, "pattern", "12,-6,0,-6");
            });
            var type = CadNative.DrawingSettings(client.Handle).Types.Single(item => item.Name == "工程点划线");
            Check(CadNative.DrawingSettings(client.Handle).Current == type.Id, "New line type not current");
            client.Command("@undo"); Check(!CadNative.DrawingSettings(client.Handle).Types.Any(item => item.Name == type.Name), "Line type creation undo failed");
            client.Command("@redo");
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "线型验证", 0, -3));
            var layer = client.State.Layers.Single(item => item.Name == "线型验证");
            CadNative.Check(CadNative.wl_layer_details(client.Handle, document, layer.Id, layer.Name, 0x1234EF, 35, type.Id));
            client.Command("@undo"); Check(client.State.Layers.Single(item => item.Id == layer.Id).LineType == 2, "Layer line type undo failed"); client.Command("@redo");
            Line();
            Form(workspace.LineTypes, "linetype-scale", form =>
            {
                ((ComboBox)form.Inputs["operation"]).SelectedIndex = 3; Set(form, "scale", "20");
                ((CheckBox)form.Inputs["paperScale"]).IsChecked = false;
            });
            Form(workspace.PointStyle, "point-style", form =>
            {
                ((ComboBox)form.Inputs["shape"]).SelectedIndex = 3;
                ((ComboBox)form.Inputs["frame"]).SelectedIndex = 1;
                ((ComboBox)form.Inputs["units"]).SelectedIndex = 2; Set(form, "size", "3");
            });
            client.Command("POINT"); Point(500, 500); client.Command("@cancel");
            for (var view = 0; view < 10; ++view)
            {
                CadNative.Check(CadNative.wl_standard_view(client.Handle, view));
                Check(client.State.StandardView == view, "Standard view feedback failed");
            }
            CadNative.Check(CadNative.wl_standard_view(client.Handle, 0));
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var path = Path.Combine(root, "drawing-settings" + extension); await client.SaveAsync(path); client.Close(document);
                await client.OpenAsync(path); document = client.State.ActiveId;
                var settings = CadNative.DrawingSettings(client.Handle); type = settings.Types.Single(item => item.Name == "工程点划线");
                Check(settings.Current == type.Id && settings.Scale == 20 && !settings.PaperScale, "Current line type or global scale lost on saving");
                Check(settings.PointMode == 35 && settings.PointSize == -3, "Point display parameters lost on saving");
                var pattern = new[] { 24d, -6, 0, -6 };
                CadNative.Check(CadNative.wl_linetype_manage(client.Handle, document, 1, type.Id, type.Name, "修改后的定义", pattern, pattern.Length, 20, 0));
                await client.SaveAsync(Path.Combine(root, "drawing-settings-edited" + extension));
            }
            client.Close(document);
            workspace.NewDrawing(); document = client.State.ActiveId;
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "源图层", 0, -3));
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 0, 0, "源文字样式", "arial.ttf", "", 150, .8, 0));
            Line(); client.Command("@all"); client.Command("BLOCK"); Point(0, 0);
            CadNative.Check(CadNative.wl_block_submit(client.Handle, document, 1, "内部块", 0)); client.Command("@cancel");
            client.Command("@all"); client.Command("BLOCK"); Point(0, 0);
            CadNative.Check(CadNative.wl_block_submit(client.Handle, document, 1, "源块", 0)); client.Command("@cancel");
            client.Command("TEXT"); Point(0, 300);
            CadNative.Check(CadNative.wl_text_submit(client.Handle, document, 0, "跨图纸文字", 150, 0, 0));
            client.Command("@all"); client.Command("@copybase"); Point(0, 0); client.Command("@cancel");
            client.Close(document); // Clipboard dependencies must outlive the source document.
            workspace.NewDrawing(); document = client.State.ActiveId;
            CadNative.Check(CadNative.wl_layer_manage(client.Handle, 0, 0, "目标图层", 0, -3));
            CadNative.Check(CadNative.wl_text_style_manage(client.Handle, document, 0, 0, "目标文字样式", "arial.ttf", "", 250, 1, 0));
            Line(); client.Command("@all"); client.Command("BLOCK"); Point(0, 0);
            CadNative.Check(CadNative.wl_block_submit(client.Handle, document, 1, "目标块", 0)); client.Command("@cancel");
            client.Command("@paste"); client.Command("@cancel");
            Check(client.State.EntityCount == 1 && !client.State.Layers.Any(item => item.Name == "源图层"), "Cancelled paste changed document dependencies");
            client.Command("@paste"); Point(10000, 1000); client.Command("@cancel");
            Check(client.State.EntityCount == 3 && client.State.Layers.Any(item => item.Name == "源图层"), "Cross-document paste lost entities or layers");
            client.Command("@undo");
            Check(client.State.EntityCount == 1 && !client.State.Layers.Any(item => item.Name == "源图层") && !CadNative.StyleState(client.Handle).Text.Any(item => item.Name == "源文字样式"), "Paste undo did not roll back dependencies");
            client.Command("@redo");
            Check(client.State.EntityCount == 3 && CadNative.StyleState(client.Handle).Text.Any(item => item.Name == "源文字样式"), "Paste redo lost dependencies");
            await client.SaveAsync(Path.Combine(root, "clipboard-transfer.dwg"));
            await client.SaveAsync(Path.Combine(root, "clipboard-transfer.dxf"));
            client.Command("@all"); client.Command("@cut"); Check(client.State.EntityCount == 0, "Cut did not remove selection");
            client.Command("@undo"); Check(client.State.EntityCount == 3, "Cut undo failed");
            CadNative.Check(CadNative.wl_layout_manage(client.Handle, 0, 0, "剪贴板布局"));
            client.Command("@paste"); Point(3000, 1000); client.Command("@cancel");
            await client.SaveAsync(Path.Combine(root, "clipboard-paper.dwg"));
            client.Close(document);
            var fixtures = Environment.GetEnvironmentVariable("WANLUO_NATIVE_FIXTURE_ROOT");
            Check(!string.IsNullOrEmpty(fixtures), "Native external reference fixtures were not prepared");
            var host = Path.Combine(root, "xref-host.dwg"); File.Copy(Path.Combine(fixtures, "host-xref.dwg"), host);
            await client.OpenAsync(host); document = client.State.ActiveId;
            Check(CadNative.References(client.Handle).Single().Status == "未解析", "External reference fixture should initially be unresolved");
            var referenceDirectory = Path.Combine(root, "located-references"); Directory.CreateDirectory(referenceDirectory);
            var referencePath = Path.Combine(referenceDirectory, "共享轴网.dwg"); File.Copy(Path.Combine(fixtures, "separate-xref.dwg"), referencePath);
            var referenceBytes = File.ReadAllBytes(referencePath);
            Form(workspace.LocateReferences, "xref-locate", form => Set(form, "folder", referenceDirectory));
            Check(CadNative.References(client.Handle).Single().Status == "已加载" && client.State.Documents.Single(item => item.Id == document).Dirty, "Located reference did not load or mark its updated path dirty");
            foreach (var extension in new[] { ".dwg", ".dxf" })
            {
                var path = Path.Combine(root, "xref-located" + extension);
                await client.SaveAsync(path); client.Close(document); await client.OpenAsync(path); document = client.State.ActiveId;
                var reference = CadNative.References(client.Handle).Single();
                Check(reference.Status == "已加载" && Path.GetFullPath(reference.Path) == Path.GetFullPath(referencePath), "Located external path was not saved");
            }
            Check(referenceBytes.SequenceEqual(File.ReadAllBytes(referencePath)), "Reference source file changed");
            client.Command("@all"); client.Command("@copybase"); Point(0, 0); client.Command("@cancel");
            client.Close(document); workspace.NewDrawing(); document = client.State.ActiveId;
            client.Command("@paste"); Point(1000, 1000); client.Command("@cancel");
            var copiedReference = Path.Combine(root, "clipboard-xref.dwg"); await client.SaveAsync(copiedReference); client.Close(document);
            await client.OpenAsync(copiedReference); document = client.State.ActiveId;
            Check(CadNative.References(client.Handle).Single().Status == "已加载", "Copied external reference lost its file after source closure");
            client.Close(document);
            using (var saveWorkspace = new CadWorkspace())
            {
                var saveClient = saveWorkspace.Client;
                var savedDocuments = new ulong[2];
                for (var index = 0; index < savedDocuments.Length; ++index)
                {
                    saveWorkspace.NewDrawing(); savedDocuments[index] = saveClient.State.ActiveId;
                    await saveClient.SaveAsync(Path.Combine(root, "save-all-" + index + ".dwg"));
                    saveClient.Command("LINE");
                    CadNative.Check(CadNative.wl_point(saveClient.Handle, 0, 0));
                    CadNative.Check(CadNative.wl_point(saveClient.Handle, 1000, 0));
                    saveClient.Command("");
                }
                var selectedBeforeSave = saveClient.State.ActiveId;
                await saveWorkspace.SaveAllDrawingsAsync();
                Check(saveClient.State.ActiveId == selectedBeforeSave && saveClient.State.Documents.All(item => !item.Dirty), "Save all did not preserve active drawing or clear saved modifications");
                for (var index = 0; index < savedDocuments.Length; ++index)
                {
                    saveClient.Close(savedDocuments[index]); await saveClient.OpenAsync(Path.Combine(root, "save-all-" + index + ".dwg"));
                    Check(saveClient.State.EntityCount == 1, "Save-all drawing contents were lost"); saveClient.Close(saveClient.State.ActiveId);
                }
            }
            client.Activate(original); workspace.SuppressToolDialogs = false; workspace.RefreshState();
            File.WriteAllText(Path.Combine(root, "cad-management-result.txt"), "CAD_MANAGEMENT_OK lineTypes current pattern sourceEdit layerLineType pointMode view clipboard sourceClosed IDRemap cancel undo redo cut paperSpace");
        }
        private static void Set(CadParameterWindow form, string key, string value) => ((TextBox)form.Inputs[key]).Text = value;
        private static void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
    }
}
