using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private static void RunElevationStyleRasterCheck()
    {
        var folder=Path.GetFullPath(".artifacts/elevation-style-raster");Directory.CreateDirectory(folder);
        var model=SampleModelFactory.CreateTwoStoreyHouse();var opening=model.Openings.First(o=>o.Kind.Contains("窗"));
        model.Openings=new(){opening};var type=OpeningConstruction.Default(opening);type.DoorFrameWidth=40;
        type.OpeningMode="双扇平开";type.DivisionPreset="双扇等分";model.OpeningTypes=new(){type};
        var view=OrthographicProjector.ProjectOpeningElevations(model,new ViewDefinitionModel {Id="pixel",Kind=ViewKind.OpeningElevation,Scale=50},OpeningConstruction.Library(model));
        var marks=view.Lines.Where(l=>l.OpeningRole=="Opening").ToList();
        if(marks.Count<4||marks.Any(l=>l.LineType!="DASHED"))throw new InvalidOperationException("平开窗立面缺少虚线开启示意。");
        foreach(var size in new[]{new PixelSize(640,480),new PixelSize(1000,720)})
        {
            var drawing=new DrawingViewCanvas();drawing.Measure(new Size(size.Width,size.Height));drawing.Arrange(new Rect(0,0,size.Width,size.Height));
            drawing.SetView(new ViewDocument {Scale=50,Lines=view.Lines});
            using var bitmap=new RenderTargetBitmap(size,new Vector(96,96));bitmap.Render(drawing);
            bitmap.Save(Path.Combine(folder,"window-"+size.Width+".png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(size,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();bitmap.CopyPixels(buffer);var bytes=new byte[buffer.RowBytes*size.Height];Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
            var colored=0;for(var y=0;y<size.Height;y++)for(var x=0;x<size.Width;x++){
                var offset=y*buffer.RowBytes+x*4;if(bytes[offset+2]<90&&bytes[offset+1]>75&&bytes[offset]>100)colored++;
            }
            if(colored<80)throw new InvalidOperationException("窗框立面没有按窗图层颜色显示。");
            var mark=marks.First();var ink=0;var gaps=0;
            for(var i=10;i<90;i++)
            {
                var t=i/100d;var p=drawing.ModelToScreen(new PointModel(mark.X1+(mark.X2-mark.X1)*t,mark.Y1+(mark.Y2-mark.Y1)*t));
                var offset=(int)Math.Round(p.Y)*buffer.RowBytes+(int)Math.Round(p.X)*4;
                if(bytes[offset+2]<225){ink++;if(bytes[offset+2]<100||bytes[offset+1]<110)throw new InvalidOperationException("开启示意没有采用浅色。");}else gaps++;
            }
            if(ink<12||gaps<12)throw new InvalidOperationException("立面开启线仍为实线或虚线不可见。");
        }
        Console.WriteLine("ELEVATION_STYLE_PIXELS_OK windowColor solidFrames lightDashedOpening smallLarge sourceGeometry");
    }
}
