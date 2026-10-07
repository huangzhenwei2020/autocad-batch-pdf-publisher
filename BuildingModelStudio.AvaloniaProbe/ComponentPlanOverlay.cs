using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ComponentPlanOverlay : Control
{
    private readonly DrawingViewCanvas _canvas;
    internal ComponentPlanSymbol? Plan;
    internal IReadOnlyList<int> Indices=Array.Empty<int>();
    internal ComponentPlanOverlay(DrawingViewCanvas canvas)
    {
        _canvas=canvas;IsHitTestVisible=false;ClipToBounds=true;
        canvas.PointerMoved+=(_,_)=>InvalidateVisual();canvas.PointerWheelChanged+=(_,_)=>InvalidateVisual();canvas.SizeChanged+=(_,_)=>InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if(Plan==null)return;var pen=new Pen(new SolidColorBrush(Color.Parse("#E49F16")),2);
        foreach(var index in Indices) {
            if(index<0||index>=Plan.Primitives.Count)continue;var p=Plan.Primitives[index];
            Point At(double x,double y)=>_canvas.ModelToScreen(new PointModel(x,y));
            if(p.Kind=="Line")context.DrawLine(pen,At(p.X1,p.Y1),At(p.X2,p.Y2));
            else if(p.Kind=="Circle"){var r=p.Radius*_canvas.PixelsPerMillimetre;context.DrawEllipse(null,pen,At(p.X1,p.Y1),r,r);}
            else {
                var geometry=new StreamGeometry();using(var path=geometry.Open()) {
                    for(var i=0;i<=128;i++){var angle=(p.StartDegrees+p.SweepDegrees*i/128)*Math.PI/180;var point=At(p.X1+p.Radius*Math.Cos(angle),p.Y1+p.Radius*Math.Sin(angle));if(i==0)path.BeginFigure(point,false);else path.LineTo(point);}path.EndFigure(false);
                }
                context.DrawGeometry(null,pen,geometry);
            }
        }
    }
}
