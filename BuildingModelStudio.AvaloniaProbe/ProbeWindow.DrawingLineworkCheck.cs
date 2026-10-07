using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private static void RunDrawingLineworkRasterCheck()
    {
        var folder=Path.GetFullPath(".artifacts/drawing-linework");Directory.CreateDirectory(folder);
        var drawing=new DrawingViewCanvas();drawing.Measure(new Size(900,600));drawing.Arrange(new Rect(0,0,900,600));
        var view=new ViewDocument {Scale=100,Lines=new() {
            new ViewLine {Layer=ViewLayers.Cut,LineWeight=70,X2=2000},
            new ViewLine {Layer=ViewLayers.Cut,LineWeight=70,X1=2000,X2=2000,Y2=2000}}};
        drawing.SetView(view);
        foreach(var zoom in new[]{.15,.3}) {
            drawing.ZoomAt(drawing.ModelToScreen(new PointModel(2000,0)),Math.Log(zoom/drawing.PixelsPerMillimetre)/Math.Log(1.18));
            using var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96));image.Render(drawing);
            image.Save(Path.Combine(folder,"corner-stroke-"+zoom+".png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);
            var bytes=new byte[buffer.RowBytes*buffer.Size.Height];Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
            bool Ink(double x,double y) {
                var screen=drawing.ModelToScreen(new PointModel(x,y));var px=(int)Math.Round(screen.X);var py=(int)Math.Round(screen.Y);
                if(px<0||py<0||px>=900||py>=600)return false;
                var offset=py*buffer.RowBytes+px*4;
                return bytes[offset]<140&&bytes[offset+1]<120&&bytes[offset+2]<100;
            }
            if(!Ink(2015,-15)||!Ink(1990,10))throw new InvalidOperationException("墙线斜接描边仍有缺角");
            if(!Ink(1000,25)||Ink(1000,45))throw new InvalidOperationException("CAD 毫米线宽在缩放后失真");
        }
        var opening=new OpeningModel {Code="M0921",Kind="门",Width=900,Height=2100};
        var type=OpeningConstruction.Default(opening);
        foreach(var angle in new[]{15d,30,45,90}) {
            opening.PlanOpenAngle=angle;
            var symbol=new ViewDocument {Scale=100};symbol.Lines=OpeningPlanGeometry.Build(opening,type,200,symbol.StrokeAreas);
            drawing.SetView(symbol);
            using var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96));image.Render(drawing);
            image.Save(Path.Combine(folder,"swing-arrow-"+angle+".png"),PngBitmapEncoderOptions.Default);
        }
        opening.PlanOpenAngle=90;
        var arcLines=OpeningPlanGeometry.Build(opening,type,200).Where(l=>l.OpeningArcId!=null).ToList();
        foreach(var zoom in new[]{.3,.5}) {
            drawing.SetView(new ViewDocument {Scale=100,Lines=arcLines});
            drawing.ZoomAt(new Point(450,300),Math.Log(zoom/drawing.PixelsPerMillimetre)/Math.Log(1.18));
            using var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96));image.Render(drawing);
            image.Save(Path.Combine(folder,"swing-light-dashed-"+zoom+".png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);var bytes=new byte[buffer.RowBytes*buffer.Size.Height];
            Marshal.Copy(buffer.Address,bytes,0,bytes.Length);var ink=0;var gaps=0;
            foreach(var line in arcLines.Skip(2).Take(60)) {
                var p=drawing.ModelToScreen(new PointModel((line.X1+line.X2)/2,(line.Y1+line.Y2)/2));
                var offset=(int)Math.Round(p.Y)*buffer.RowBytes+(int)Math.Round(p.X)*4;
                if(bytes[offset]<220) {
                    ink++;
                    if(bytes[offset+2]<140||bytes[offset+1]<155)throw new InvalidOperationException("开启弧颜色没有变浅");
                } else gaps++;
            }
            if(ink<12||gaps<8)throw new InvalidOperationException("开启弧短线重启虚线相位，或虚线不可见");
        }
        Console.WriteLine("OPENING_ARC_PIXELS_OK lightDashed continuousPhase twoZooms solidLeafAndFrameUnchanged");
        var windowModel=BuildingModelJson.LoadModel(Path.GetFullPath(".artifacts/window-directions/c1216.json"));
        var casement=windowModel.Openings[0];var windowType=OpeningConstruction.Resolve(windowModel,casement);
        foreach(var angle in new[]{30d,90}) {
            casement.PlanOpenAngle=angle;var symbol=new ViewDocument {Scale=100};
            symbol.Lines=OpeningPlanGeometry.Build(casement,windowType,200,symbol.StrokeAreas);drawing.SetView(symbol);
            using var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96));image.Render(drawing);
            image.Save(Path.Combine(folder,"window-closed-base-dashed-open-"+angle+".png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);var bytes=new byte[buffer.RowBytes*buffer.Size.Height];
            Marshal.Copy(buffer.Address,bytes,0,bytes.Length);var leaf=symbol.Lines.First(l=>l.OpeningArcId?.StartsWith("window-leaf-")==true);
            var ink=0;var gaps=0;
            for(var i=10;i<90;i++) {
                var t=i/100d;var p=drawing.ModelToScreen(new PointModel(leaf.X1+(leaf.X2-leaf.X1)*t,leaf.Y1+(leaf.Y2-leaf.Y1)*t));
                var offset=(int)Math.Round(p.Y)*buffer.RowBytes+(int)Math.Round(p.X)*4;
                if(bytes[offset]<220) {
                    ink++;if(bytes[offset+2]<140||bytes[offset+1]<155)throw new InvalidOperationException("窗扇开启单线不是浅色虚线");
                } else gaps++;
            }
            if(ink<12||gaps<8)throw new InvalidOperationException("窗扇开启单线没有形成可见虚线");
        }
        Console.WriteLine("WINDOW_PLAN_PIXELS_OK closedBase lightDashedSingleLeaf angle30-90 noOpenThickness");
        var junction=SampleModelFactory.CreateEmptyModel("T junction drawing check");
        junction.Walls.Add(new WallModel {Id="up",StoreyId="1F",Y2=2000,Thickness=200});
        junction.Walls.Add(new WallModel {Id="down",StoreyId="1F",Y2=-2000,Thickness=200});
        junction.Walls.Add(new WallModel {Id="right",StoreyId="1F",X2=3000,Thickness=200});
        var projected=OrthographicProjector.Project(junction,SampleModelFactory.CreatePlanView(junction.Storeys[0]));
        drawing.SetView(new ViewDocument {Scale=100,StrokeAreas=projected.StrokeAreas,Lines=projected.Lines.Where(l=>l.Layer==ViewLayers.Cut||l.Layer==ViewLayers.Elevation).ToList()});
        PointModel At(double x,double y)=>new(x-projected.OriginX,y-projected.OriginY);
        drawing.ZoomAt(drawing.ModelToScreen(At(0,0)),Math.Log(.5/drawing.PixelsPerMillimetre)/Math.Log(1.18));
        using(var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96))) {
            image.Render(drawing);image.Save(Path.Combine(folder,"junction-no-diagonals.png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);
            var bytes=new byte[buffer.RowBytes*buffer.Size.Height];Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
            bool Ink(double x,double y) {
                var p=drawing.ModelToScreen(At(x,y));var offset=(int)Math.Round(p.Y)*buffer.RowBytes+(int)Math.Round(p.X)*4;
                return bytes[offset]<140&&bytes[offset+1]<120&&bytes[offset+2]<100;
            }
            if(Ink(0,-50)||Ink(0,50)||!Ink(-90,0))
                throw new InvalidOperationException("图纸交接处残留对角线或误删外轮廓");
        }
        if(WallJunctionLines.Resolve(junction,junction.Walls,1200).Count!=2)
            throw new InvalidOperationException("隐藏图纸对角线影响了模型编辑交接线");
        Console.WriteLine("DRAWING_LINEWORK_PIXELS_OK miterOuterCorner cadPaperWeight zoomInvariant swingArrow15-30-45-90");
        Console.WriteLine("DRAWING_JUNCTION_PIXELS_OK noDiagonalSeams continuousOutline editorSeamsRetained");
        foreach(var weight in new[]{18,50,70})foreach(var zoom in new[]{.3,.6}) {
            var ring=new ViewDocument {Scale=100};ring.StrokeAreas.Add(new ViewStrokeArea {Id="wall"});
            var contours=new[] {new[]{new PointModel(0,0),new PointModel(2000,0),new PointModel(2000,2000),new PointModel(0,2000)},
                new[]{new PointModel(200,200),new PointModel(1800,200),new PointModel(1800,1800),new PointModel(200,1800)}};
            foreach(var contour in contours)for(var i=0;i<4;i++) {
                var a=contour[i];var b=contour[(i+1)%4];ring.Lines.Add(new ViewLine {Layer=ViewLayers.Cut,
                    StrokeAreaId="wall",LineWeight=weight,X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});
            }
            drawing.SetView(ring);
            drawing.ZoomAt(drawing.ModelToScreen(new PointModel(1000,0)),Math.Log(zoom/drawing.PixelsPerMillimetre)/Math.Log(1.18));
            using var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96));image.Render(drawing);
            image.Save(Path.Combine(folder,$"inward-wall-{weight}-{zoom}.png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);
            var bytes=new byte[buffer.RowBytes*buffer.Size.Height];Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
            bool Ink(double x,double y) {
                var p=drawing.ModelToScreen(new PointModel(x,y));var px=(int)Math.Round(p.X);var py=(int)Math.Round(p.Y);
                if(px<0||px>=900||py<0||py>=600)throw new InvalidOperationException("Inward stroke sample outside image");
                var offset=py*buffer.RowBytes+px*4;return bytes[offset]<140&&bytes[offset+1]<120&&bytes[offset+2]<100;
            }
            if(Ink(1000,-12)||!Ink(1000,weight/2d)||Ink(1000,weight+12)||Ink(1000,212)||!Ink(1000,200-weight/2d))
                throw new InvalidOperationException("实体轮廓线向外扩张、侵入洞口或内收宽度错误");
        }
        Console.WriteLine("DRAWING_INWARD_PIXELS_OK 18-50-70 twoZooms outerBoundsUnchanged innerHoleClear");
        var frame=new ViewDocument {Scale=100};
        for(var i=0;i<2;i++) {
            var x=i*42d;var id="part-"+i;frame.StrokeAreas.Add(new ViewStrokeArea {Id=id});
            var contour=new[]{new PointModel(x,0),new PointModel(x+40,0),new PointModel(x+40,200),new PointModel(x,200)};
            for(var j=0;j<4;j++){var a=contour[j];var b=contour[(j+1)%4];frame.Lines.Add(new ViewLine {
                Layer=ViewLayers.Opening,LineWeight=18,StrokeAreaId=id,X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});}
        }
        drawing.SetView(frame);drawing.ZoomAt(new Point(450,300),Math.Log(2/drawing.PixelsPerMillimetre)/Math.Log(1.18));
        using(var image=new RenderTargetBitmap(new PixelSize(900,600),new Vector(96,96))) {
            image.Render(drawing);image.Save(Path.Combine(folder,"inward-frame-leaf-clearance.png"),PngBitmapEncoderOptions.Default);
            using var pixels=new WriteableBitmap(image.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using var buffer=pixels.Lock();image.CopyPixels(buffer);var bytes=new byte[buffer.RowBytes*buffer.Size.Height];
            Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
            bool Ink(double x) {var p=drawing.ModelToScreen(new PointModel(x,100));
                var offset=(int)Math.Round(p.Y)*buffer.RowBytes+(int)Math.Round(p.X)*4;
                return bytes[offset]<150&&bytes[offset+1]<130&&bytes[offset+2]<100;}
            if(Ink(-5)||Ink(41)||Ink(87)||!Ink(5)||!Ink(47))throw new InvalidOperationException("门框门扇线宽侵占真实安装间隙");
        }
        Console.WriteLine("DRAWING_FRAME_INWARD_PIXELS_OK twoMillimetreClearancePreserved");
    }
}
