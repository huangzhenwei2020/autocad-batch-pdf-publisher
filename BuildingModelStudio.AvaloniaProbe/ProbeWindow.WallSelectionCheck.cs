using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BatchPdfPublisher.BuildingModel;
using System.Runtime.InteropServices;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class ProbeWindow
{
    private async Task RunWallSelectionPreviewCheckAsync()
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        var savedSession=_session;var savedSelection=_selectedId;
        var model=new BuildingModelDocument {Name="墙体连续预选校对",
            Storeys=new(){new StoreyModel {Id="1F",Name="一层",Height=3000}},
            Walls=new(){
                new WallModel {Id="horizontal",Code="W-1",StoreyId="1F",X2=6000,Thickness=200},
                new WallModel {Id="vertical",Code="W-2",StoreyId="1F",X1=6000,X2=6000,Y2=4000,Thickness=200},
                new WallModel {Id="oblique",Code="W-3",StoreyId="1F",X1=1000,Y1=2500,X2=4500,Y2=4000,Thickness=200}},
            Openings=new(){new OpeningModel {Id="cut",Code="M0921",Kind="门",HostWallId="horizontal",Offset=2000,Width=900,Height=2100}}};
        try {
            _session=new BuildingModelEditSession(model);await RefreshModelAsync("墙体连续预选校对");
            SelectById(null);_planCanvas.Fit();await Task.Delay(180);
            var before=BuildingModelJson.ToJson(_session.Model);
            var pointer=new Pointer(994,PointerType.Mouse,true);
            void Hover(PointModel world){var p=_planCanvas.ModelToScreen(world);
                _planCanvas.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent,_planCanvas,pointer,this,
                    _planCanvas.TranslatePoint(p,this)!.Value,0,new PointerPointProperties(RawInputModifiers.None,PointerUpdateKind.Other),KeyModifiers.None));}
            foreach(var wall in model.Walls) {
                var dx=wall.X2-wall.X1;var dy=wall.Y2-wall.Y1;var length=Math.Sqrt(dx*dx+dy*dy);
                var ux=dx/length;var uy=dy/length;
                PointModel At(double along,double normal=0)=>new(wall.X1+ux*along-uy*normal,wall.Y1+uy*along+ux*normal);
                Hover(At(500));await Task.Delay(100);
                Check(_planCanvas.PreselectedElement==wall.Id,"墙体预选对象错误："+wall.Id);
                using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_planCanvas.Bounds.Width),(int)Math.Ceiling(_planCanvas.Bounds.Height)),new Vector(96,96));
                bitmap.Render(_planCanvas);
                bitmap.Save(System.IO.Path.GetFullPath($".artifacts/opening-symbols/{(int)Width}x{(int)Height}-wall-pixels-"+wall.Id+".png"),PngBitmapEncoderOptions.Default);
                using var pixels=new WriteableBitmap(bitmap.PixelSize,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
                using var buffer=pixels.Lock();bitmap.CopyPixels(buffer);
                var bytes=new byte[buffer.RowBytes*buffer.Size.Height];Marshal.Copy(buffer.Address,bytes,0,bytes.Length);
                bool Cyan(PointModel world){var p=_planCanvas.ModelToScreen(world);var x=(int)Math.Round(p.X);var y=(int)Math.Round(p.Y);
                    // Allow a one-pixel annotation/crosshair overlay without accepting a dashed wall gap.
                    for(var py=y-1;py<=y+1;py++)for(var px=x-1;px<=x+1;px++) {
                        if(px<0||py<0||px>=buffer.Size.Width||py>=buffer.Size.Height)continue;
                        var offset=py*buffer.RowBytes+px*4;
                        if(bytes[offset]>230&&bytes[offset+1]>230&&bytes[offset+2]<30)return true;
                    }
                    return false;}
                bool WallCyan(double along)=>Enumerable.Range(0,9).Any(i=>Cyan(At(along,-80+i*20)));
                for(var along=100d;along<length-100;along+=50) {
                    if(wall.Id=="horizontal"&&along>1500&&along<2500)continue;
                    Check(WallCyan(along),"墙体预选存在非洞口断段："+wall.Id+" @"+along);
                }
                if(wall.Id=="horizontal")Check(!Cyan(At(2000,30)),"墙体预选盖住门洞");
                if(wall.Id=="vertical")Check(!Cyan(new PointModel(3500,45)),"预选串到相邻墙");
                await SaveOpeningCheck("wall-hover-"+wall.Id+".png");
                Hover(new PointModel(-1500,-1500));
                Check(_planCanvas.PreselectedElement==null,"移出墙体后残留预选");
            }
            Check(BuildingModelJson.ToJson(_session.Model)==before,"预选修改了模型或选择状态");
            Console.WriteLine("WALL_PREVIEW_OK solidPixels horizontal vertical oblique openingCut neighbourIsolation hoverExit noMutation");
        } finally {_session=savedSession;await RefreshModelAsync("墙体预选校对完成");SelectById(savedSelection);}
    }
}
