using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task<bool> RunZoomCheckAsync()
    {
        try {
            var folder=Path.GetFullPath(".artifacts/actual-size-zoom");Directory.CreateDirectory(folder);
            var model=SampleModelFactory.CreateEmptyModel("缩放尺寸检查");
            model.Walls.Add(new WallModel { Id="H",StoreyId="1F",X2=5000,Thickness=200 });
            model.Walls.Add(new WallModel { Id="V",StoreyId="1F",Y2=5000,Thickness=200 });
            var original=BuildingModelJson.ToJson(model);
            var canvas=new PlanEditorCanvas();canvas.Measure(new Size(800,600));canvas.Arrange(new Rect(0,0,800,600));canvas.SetModel(model,"1F");
            var probe=new PointModel(1500,0);
            foreach(var target in new[] {.02,.3,.6}) {
                var p=canvas.ModelToScreen(probe);
                canvas.ZoomAt(p,Math.Log(target/canvas.PixelsPerMillimetre)/Math.Log(1.15));
                await Task.Delay(40);
                using var bitmap=new RenderTargetBitmap(new PixelSize(800,600),new Vector(96,96));bitmap.Render(canvas);
                bitmap.Save(Path.Combine(folder,"wall-"+target.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"),PngBitmapEncoderOptions.Default);
                var bytes=new byte[800*600*4];var handle=GCHandle.Alloc(bytes,GCHandleType.Pinned);
                try { bitmap.CopyPixels(new PixelRect(0,0,800,600),handle.AddrOfPinnedObject(),bytes.Length,800*4); }
                finally { handle.Free(); }
                var x=(int)Math.Round(canvas.ModelToScreen(probe).X);var ys=new List<int>();
                for(var y=0;y<600;y++) {
                    var offset=(y*800+x)*4;
                    if(Math.Abs(bytes[offset+1]-196)<6 && ((Math.Abs(bytes[offset]-233)<6 && Math.Abs(bytes[offset+2]-155)<6)
                        || (Math.Abs(bytes[offset]-155)<6 && Math.Abs(bytes[offset+2]-233)<6)))ys.Add(y);
                }
                var actual=ys.Count==0 ? 0 : ys.Max()-ys.Min()+1;
                if(Math.Abs(actual-200*target)>2)throw new InvalidOperationException($"实际渲染墙厚错误 scale={target} expected={200*target} pixels={actual}");
                Console.WriteLine($"ZOOM_WALL_PIXELS scale={target} thickness={actual}");
            }
            if(BuildingModelJson.ToJson(model)!=original)throw new InvalidOperationException("滚轮缩放改写了模型几何");
            var view=new ViewDocument { Id="zoom-dimensions",Scale=100,Kind=ViewKind.Plan };
            foreach(var span in new[] {(0d,100d),(100d,400d),(400d,4000d)}) {
                view.Dimensions.Add(new ViewDimension { Layer=ViewLayers.Dimension,Vertical=false,From=span.Item1,To=span.Item2,LinePosition=0,AnchorPosition=1000 });
                view.Dimensions.Add(new ViewDimension { Layer=ViewLayers.Dimension,Vertical=true,From=span.Item1,To=span.Item2,LinePosition=-1500,AnchorPosition=1000 });
            }
            var drawing=new DrawingViewCanvas();drawing.Measure(new Size(800,600));drawing.Arrange(new Rect(0,0,800,600));drawing.SetView(view);
            var bounds=drawing.DimensionTextBounds.ToArray();
            for(var i=0;i<bounds.Length;i++)for(var j=i+1;j<bounds.Length;j++)
                if(bounds[i].Intersects(bounds[j]))throw new InvalidOperationException("短尺寸文字仍然重叠");
            var path=Path.Combine(folder,"drawing-before.json");BuildingModelJson.SaveView(path,view);var before=File.ReadAllText(path);
            foreach(var delta in new[] {0d,5d,-10d}) {
                var origin=drawing.ModelToScreen(new PointModel(0,0));var oldScale=drawing.PixelsPerMillimetre;
                drawing.ZoomAt(origin,delta);
                var after=drawing.ModelToScreen(new PointModel(0,0));
                if(Math.Abs(after.X-origin.X)+Math.Abs(after.Y-origin.Y)>1e-7 || !bounds.SequenceEqual(drawing.DimensionTextBounds)
                    || Math.Abs(drawing.PixelsPerMillimetre/oldScale-Math.Pow(1.18,delta))>1e-9)
                    throw new InvalidOperationException("图纸缩放改变了锚点、尺寸文字大小或排布");
                using var bitmap=new RenderTargetBitmap(new PixelSize(800,600),new Vector(96,96));bitmap.Render(drawing);
                bitmap.Save(Path.Combine(folder,"dimensions-"+delta+".png"),PngBitmapEncoderOptions.Default);
            }
            BuildingModelJson.SaveView(path,view);
            if(File.ReadAllText(path)!=before)throw new InvalidOperationException("缩放改写了输出图纸");
            Console.WriteLine("ACTUAL_SIZE_ZOOM_OK rendered-wall-thickness pointer-anchor dimension-collisions stable-layout unchanged-model-and-output");
            return true;
        } catch(Exception ex) { Console.Error.WriteLine("ACTUAL_SIZE_ZOOM_FAILED "+ex);return false; }
    }
}
