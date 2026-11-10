using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private async Task RunComponentImportCheckAsync()
    {
        var source=Environment.GetEnvironmentVariable("WANLUO_COMPONENT_IMPORT_QA_SOURCE")??throw new IOException("Synthetic FBX fixture required.");
        if(!Path.GetFullPath(source).StartsWith(Path.GetFullPath(".artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Private test fixture required.");
        var before=File.ReadAllBytes(source);var modelBefore=BuildingModelJson.ToJson(_session.Model);
        var root=Path.GetFullPath(".artifacts/component-import-ui/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var window=new ComponentImportWindow(new ComponentAssetLibrary(Path.Combine(root,"catalog"))) {Width=Program.SnapshotSize?.Width??1180,Height=Program.SnapshotSize?.Height??820};
        window.Show(this);
        try
        {
            await window.SmokeAsync(source);
            var visual=ElementComposition.GetElementVisual(window)!;var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);image.Save(Path.Combine(root,"import-window.png"),PngBitmapEncoderOptions.Default);
            if(!File.ReadAllBytes(source).SequenceEqual(before)||BuildingModelJson.ToJson(_session.Model)!=modelBefore)throw new IOException("Imported resource changed source/project.");
            Console.WriteLine("COMPONENT_IMPORT_UI_ARTIFACT "+root);
        }
        finally{window.Close();}
        var catalog=new ComponentCatalog(Path.Combine(root,"catalog"));var existing=new ComponentImportWindow(new ComponentAssetLibrary(catalog.Root),catalog.Load().Records.Single());existing.Show(this);
        try{await existing.SmokeExistingAsync(source);}finally{existing.Close();}
    }
}
