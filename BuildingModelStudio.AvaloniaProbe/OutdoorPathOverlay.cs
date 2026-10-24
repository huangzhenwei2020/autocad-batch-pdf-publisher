using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OutdoorPathOverlay(ModelViewport viewport) : Control
{
    internal List<PointModel> Points=new();
    internal double Elevation;
    internal bool Closed;
    internal List<PointModel> ReferencePoints=new();
    internal double ReferenceElevation;
    internal bool ReferenceClosed;
    internal List<PointModel> OriginalPoints=new();
    internal double OriginalElevation;
    public override void Render(DrawingContext context)
    {
        var originalPen=new Pen(Brushes.Gray,1,new DashStyle(new double[]{4,4},0));
        for(var i=0;i<OriginalPoints.Count;i++)
        {
            var a=OriginalPoints[i];var b=OriginalPoints[(i+1)%OriginalPoints.Count];
            var samples=new CurveGeometry(a,b,a.Bulge).Sample();
            for(var j=1;j<samples.Count;j++)
            {
                var p=viewport.ProjectModelPoint(samples[j-1].X,samples[j-1].Y,OriginalElevation);
                var q=viewport.ProjectModelPoint(samples[j].X,samples[j].Y,OriginalElevation);
                if(p!=null&&q!=null)context.DrawLine(originalPen,p.Value,q.Value);
            }
        }
        var pen=new Pen(new SolidColorBrush(Color.Parse("#FFD079")),2);
        Point? At(PointModel p)=>viewport.ProjectModelPoint(p.X,p.Y,Elevation);
        for(var i=0;i<Points.Count-1+(Closed?1:0);i++)
        {
            var a=Points[i];var b=Points[(i+1)%Points.Count];
            var sample=new CurveGeometry(a,b,a.Bulge).Sample();
            for(var j=1;j<sample.Count;j++) if(At(sample[j-1]) is Point p&&At(sample[j]) is Point q)context.DrawLine(pen,p,q);
        }
        foreach(var point in Points)if(At(point) is Point pixel)context.DrawEllipse(Brushes.Gold,null,pixel,3,3);
        var referencePen=new Pen(Brushes.Cyan,3);
        for(var i=0;i<ReferencePoints.Count-1+(ReferenceClosed?1:0);i++)
        {
            var a=ReferencePoints[i];var b=ReferencePoints[(i+1)%ReferencePoints.Count];
            var samples=new CurveGeometry(a,b,a.Bulge).Sample();
            for(var j=1;j<samples.Count;j++)
            {
                var p=viewport.ProjectModelPoint(samples[j-1].X,samples[j-1].Y,ReferenceElevation);
                var q=viewport.ProjectModelPoint(samples[j].X,samples[j].Y,ReferenceElevation);
                if(p!=null&&q!=null)context.DrawLine(referencePen,p.Value,q.Value);
            }
        }
    }
}
