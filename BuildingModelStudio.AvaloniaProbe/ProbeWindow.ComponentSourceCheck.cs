using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal sealed partial class ProbeWindow
{
    private async Task RunComponentSourceCheckAsync()
    {
        var before=BuildingModelJson.ToJson(_session.Model);var folder=Path.GetFullPath(".artifacts/component-sources");
        var plan=Path.Combine(folder,"fixed-window.wlplan.json");var glb=Path.Combine(folder,"fixed-window.glb");
        var planBytes=File.ReadAllBytes(plan);var glbBytes=File.ReadAllBytes(glb);
        var window=new ComponentSourceInspectionWindow {Width=Width,Height=Height};window.Show(this);
        try {
            await window.LoadPlanAsync(plan);await window.LoadMeshAsync(glb);
            for(var i=0;i<100&&!window.Volume.FrameRendered;i++)await Task.Delay(50);
            await Task.Delay(250);
            if(window.Plan?.Primitives.Count!=6||window.Mesh?.Triangles!=12||!window.Volume.FrameRendered)
                throw new InvalidOperationException("源文件预览为空或 GPU 未绘制。");
            var hit=window.Volume.PickAt(new Point(window.Volume.Bounds.Width/2,window.Volume.Bounds.Height/2));
            if(hit!="node-"+window.Mesh.Nodes[0].Index)throw new InvalidOperationException("GLB 预览未参与真实拾取。");
            foreach(var view in window.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Where(c=>c is DrawingViewCanvas||c is ModelViewport))
                if(view.Bounds.Width<180||view.Bounds.Height<250)throw new InvalidOperationException("小窗口预览被裁切。");
            var old=window.Mesh;
            try {await window.LoadMeshAsync(Path.Combine(folder,"bad.glb"));throw new InvalidOperationException("坏资源未拒绝。");}catch(InvalidDataException){}
            if(!ReferenceEquals(window.Mesh,old)||window.MeshPath!=glb)throw new InvalidOperationException("失败读入覆盖有效预览。");
            var visual=ElementComposition.GetElementVisual(window)!;var image=await visual.Compositor.CreateCompositionVisualSnapshot(visual,1);
            image.Save(Path.Combine(folder,$"source-ui-{(int)Width}x{(int)Height}.png"),PngBitmapEncoderOptions.Default);
            if(!File.ReadAllBytes(plan).SequenceEqual(planBytes)||!File.ReadAllBytes(glb).SequenceEqual(glbBytes)||BuildingModelJson.ToJson(_session.Model)!=before)
                throw new InvalidOperationException("只读校验修改了源文件或项目。");
            Console.WriteLine("COMPONENT_SOURCE_UI_OK nativePlan realGlb GPU nodePick dimensions smallBounds invalidRetainsPreview unchangedProjectAndSources");
        }finally{window.Close();}
    }
}
