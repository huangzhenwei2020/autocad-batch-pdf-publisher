using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunModelExportCheckAsync()
    {
        void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        var original = BuildingModelJson.ToJson(_session.Model);
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var folder = Path.GetFullPath(".artifacts/model-export-ui/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var window = new ModelExportWindow(model, model.Storeys[0].Id);
        var shown = window.ShowDialog(this);
        try
        {
            await Task.Delay(200);
            await window.OperationTask;
            var notice=new ModelExportResult(Path.Combine(folder,"repaired.fbx"),folder,3,257,192,65000,100000,15000)
                {Warnings=Enumerable.Range(1,4).Select(i=>$"FBX 微小面已自动清理：一层 · 墙 W-{i}，1 个退化面，原面积合计 0.001 mm²；构件与材质保留。").ToArray()};
            window.Status.Text=ModelExportWindow.SavedStatus(notice);
            Check(window.Status.Text.Contains("已保存")&&window.Status.Text.Contains("微小面已自动清理")
                &&window.Status.Text.Contains("另有 2 项"),"Repair warning is missing from successful export status.");
            foreach (var size in new[] { new Size(560, 500), new Size(800, 660) })
            {
                window.Width = size.Width; window.Height = size.Height;
                window.TargetSelector.SelectedIndex = 1;
                await Task.Delay(160);
                window.Status.Text=ModelExportWindow.SavedStatus(notice);
                var visual = ElementComposition.GetElementVisual(window)!;
                using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                bitmap.Save(Path.Combine(folder, "skp-" + size.Width + ".png"), PngBitmapEncoderOptions.Default);
                Check(window.ButtonExport.Bounds.Width >= 50 && window.ButtonCancel.Bounds.Width >= 50, "Export actions collapsed.");
                Check(window.GetVisualDescendants().OfType<ScrollViewer>().Any(s => s.Bounds.Height > 80), "Settings cannot scroll.");
                var root = window.Content as Grid;
                Check(root != null && root.Bounds.Width >= 500, "Export window content clipped.");
            }
            window.TargetSelector.SelectedIndex = 0;
            window.ScopeSelector.SelectedIndex = 1;
            window.NearOrigin.IsChecked = false;
            window.OutputPath.Text = Path.Combine(folder, "window-floor.fbx");
            window.ButtonExport.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.Busy && !window.TargetSelector.IsEffectivelyEnabled, "Export settings remained editable.");
            await window.OperationTask;
            Check(window.Result is { Storeys: 1 } && File.Exists(window.Result.Path), "Window did not export FBX: " + window.Status.Text);
            Check(window.ButtonOutputFolder.IsEnabled && window.ButtonLogFolder.IsEnabled, "Result actions unavailable.");
            window.TargetSelector.SelectedIndex = 1;
            Check(string.IsNullOrWhiteSpace(window.OutputPath.Text), "Changing format retained an incompatible extension.");
            window.ScopeSelector.SelectedIndex = 0;
            window.NearOrigin.IsChecked = true;
            if (ModelExportService.IsSketchUp2026(window.SketchUpApiPath.Text))
            {
                window.OutputPath.Text = Path.Combine(folder, "window-building.skp");
                window.ButtonExport.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.OperationTask;
                Check(window.Result is { Storeys: 2 } && File.Exists(window.Result.Path), "Window did not export SKP: " + window.Status.Text);
            }
            window.TargetSelector.SelectedIndex = 0;
            var canceled = Path.Combine(folder, "canceled.fbx");
            window.OutputPath.Text = canceled;
            window.ButtonExport.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            await window.OperationTask;
            await shown;
            Check(!File.Exists(canceled), "Closing export committed a canceled output.");
            Check(original == BuildingModelJson.ToJson(_session.Model), "Export changed live model.");
            Console.WriteLine("MODEL_EXPORT_UI_OK small large scopes formatSwitch nativeFbx nativeSkp cancelClose unchangedModel " + folder);
        }
        finally
        {
            if (window.Busy) { window.Close(); await window.OperationTask; }
            else window.Close();
        }
    }
}
