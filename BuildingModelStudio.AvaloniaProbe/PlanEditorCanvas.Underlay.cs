using Avalonia;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class PlanEditorCanvas
{
    private readonly Dictionary<string,CadUnderlayIndex> _underlayFloors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ComponentPlanPrimitive,StreamGeometry> _underlayArcs = new();
    private readonly Dictionary<CadUnderlayText,FormattedText> _underlayTexts = new();
    private double _underlayTextScale;
    internal CadUnderlayIndex? Underlay => _underlayFloors.GetValueOrDefault(_storeyId);
    internal bool UnderlayVisible { get; set; } = true;
    internal bool UnderlaySnapEnabled { get; set; } = true;
    internal double UnderlayOpacity { get; set; } = .4;
    internal HashSet<string> HiddenUnderlayLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal void SetUnderlays(Dictionary<string,CadUnderlayIndex> floors)
    {
        _underlayFloors.Clear(); foreach (var pair in floors) _underlayFloors.Add(pair.Key,pair.Value);
        _underlayArcs.Clear(); _underlayTexts.Clear();
        InvalidateVisual();
    }
    internal SnapResult SnapUnderlay(double x,double y,double tolerance,SnapResult modelSnap,PointModel? from=null)
    {
        if (!UnderlayVisible || !UnderlaySnapEnabled || Underlay == null) return modelSnap;
        // Preserve model endpoints/intersections; CAD discrete points beat continuous projections.
        if (modelSnap.Snapped && modelSnap.Kind != PlanEditing.SnapNearest && modelSnap.Kind != PlanEditing.SnapAxis)
            return modelSnap;
        var cad = Underlay.Snap(x,y,tolerance,HiddenUnderlayLayers,from);
        if (!cad.Snapped) return modelSnap;
        var cadResult=new SnapResult {X=cad.X,Y=cad.Y,Kind=cad.Kind};
        if (cad.Kind != "CAD · 最近点" || !modelSnap.Snapped) return cadResult;
        var a=Math.Pow(cad.X-x,2)+Math.Pow(cad.Y-y,2);
        var b=Math.Pow(modelSnap.X-x,2)+Math.Pow(modelSnap.Y-y,2);
        return a<b?cadResult:modelSnap;
    }
    private void DrawUnderlay(DrawingContext context)
    {
        var data=Underlay;
        if (!UnderlayVisible || data==null || data.Region.Count<3) return;
        var clip=new StreamGeometry();
        using(var g=clip.Open())
        {
            g.BeginFigure(Screen(data.Region[0].X,data.Region[0].Y),true);
            foreach(var p in data.Region.Skip(1))g.LineTo(Screen(p.X,p.Y));
            g.EndFigure(true);
        }
        var brush=new SolidColorBrush(Color.Parse("#B1BCC4"));
        var min=World(new Point(0,Bounds.Height));var max=World(new Point(Bounds.Width,0));
        using(context.PushGeometryClip(clip))
        using(context.PushOpacity(Math.Clamp(UnderlayOpacity,.05,1)))
        {
            using(context.PushTransform(new Matrix(_scale,0,0,-_scale,Bounds.Width/2-_centerX*_scale,Bounds.Height/2+_centerY*_scale)))
            {
                var pen=new Pen(brush,1/_scale);
                var dashed=new Pen(brush,1/_scale,new DashStyle(new[]{6d,4d},0));
                foreach(var p in data.Query(min.X,min.Y,max.X,max.Y))
                {
                    if(HiddenUnderlayLayers.Contains(p.SourceLayer??"0"))continue;
                    var stroke=(p.SourceLineType??"").Contains("DASH",StringComparison.OrdinalIgnoreCase)?dashed:pen;
                    if(p.Kind=="Line") context.DrawLine(stroke,new Point(p.X1,p.Y1),new Point(p.X2,p.Y2));
                    else if(p.Kind=="Circle") context.DrawEllipse(null,stroke,new Point(p.X1,p.Y1),p.Radius,p.Radius);
                    else
                    {
                        if(!_underlayArcs.TryGetValue(p,out var arc))
                        {
                            arc=new StreamGeometry();
                            using(var g=arc.Open())
                            {
                                var a=CadUnderlayIndex.PointAt(p,0);var b=CadUnderlayIndex.PointAt(p,1);
                                g.BeginFigure(new Point(a.X,a.Y),false);
                                g.ArcTo(new Point(b.X,b.Y),new Size(p.Radius,p.Radius),0,Math.Abs(p.SweepDegrees)>180,
                                    p.SweepDegrees>=0?SweepDirection.Clockwise:SweepDirection.CounterClockwise);
                                g.EndFigure(false);
                            }
                            _underlayArcs.Add(p,arc);
                        }
                        context.DrawGeometry(null,stroke,arc);
                    }
                }
            }
            if(_underlayTextScale!=_scale){_underlayTexts.Clear();_underlayTextScale=_scale;}
            foreach(var t in data.Texts)
            {
                if(HiddenUnderlayLayers.Contains(t.Layer??"0") || t.Height*_scale<4 || t.X<min.X-5000 || t.X>max.X+5000 || t.Y<min.Y-5000 || t.Y>max.Y+5000)continue;
                if(!_underlayTexts.TryGetValue(t,out var text))
                {
                    text=new FormattedText(t.Text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,
                        Math.Min(1000,t.Height*_scale),brush);_underlayTexts.Add(t,text);
                }
                var origin=Screen(t.X,t.Y);
                using(context.PushTransform(Matrix.CreateScale(t.WidthFactor,1)*Matrix.CreateRotation(-t.RotationDegrees*Math.PI/180)*Matrix.CreateTranslation(origin.X,origin.Y)))
                    context.DrawText(text,new Point(0,-text.Height));
            }
        }
    }
}
