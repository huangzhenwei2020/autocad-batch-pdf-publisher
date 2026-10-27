using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class PlanEditorCanvas
{
    internal OutdoorDrawingDraft? OutdoorDraft;
    internal BuildingModelDocument? OutdoorGeometry;
    internal List<PointModel> OutdoorPath=new(),OutdoorReference=new();
    internal bool OutdoorClosed,OutdoorReferenceClosed;
    internal Action<Point,KeyModifiers,bool>? OutdoorPressed;
    internal Action<Point,KeyModifiers>? OutdoorMoved;
    internal Point OutdoorScreen(double x,double y)=>Screen(x,y);

    internal PointModel OutdoorPoint(Point pixel,bool snap,bool constrain)
    {
        var world=World(pixel);var anchor=constrain?OutdoorDraft?.Points.LastOrDefault():null;
        var result=new PointModel(Math.Round(world.X),Math.Round(world.Y));
        _snapKind=PlanEditing.SnapNone;
        if(snap)
        {
            // Do not attract a path to hidden or frozen model geometry.
            var model=new BuildingModelDocument {
                Walls=_model.Walls.Where(w=>Selectable(w.Id)).ToList(),
                Slabs=_model.Slabs.Where(s=>Selectable(s.Id)).ToList(),
                Columns=_model.Columns.Where(c=>Selectable(c.Id)).ToList(),
                Beams=_model.Beams.Where(b=>Selectable(b.Id)).ToList()};
            var hit=PlanEditing.Snap(model,_storeyId,world.X,world.Y,10/_scale,
                anchor!=null,anchor?.X??0,anchor?.Y??0,null,_resolvedAxes);
            hit=SnapUnderlay(world.X,world.Y,10/_scale,hit,anchor);
            result=new(hit.X,hit.Y);_snapKind=hit.Kind;
            var best=hit.Snapped?Distance(pixel,Screen(hit.X,hit.Y)):10d;
            void Consider(PointModel p,string kind)
            {
                var distance=Distance(pixel,Screen(p.X,p.Y));
                if(distance>best)return;best=distance;result=new(p.X,p.Y);_snapKind=kind;
            }
            foreach(var slab in model.Slabs.Where(s=>s.StoreyId==_storeyId))
                foreach(var contour in new[]{slab.Outline}.Concat(slab.Openings.Select(o=>o.Outline)))
                    for(var i=0;i<contour.Count;i++)
                    {Consider(contour[i],PlanEditing.SnapEndpoint);
                        Consider(new CurveGeometry(contour[i],contour[(i+1)%contour.Count],contour[i].Bulge).At(.5),PlanEditing.SnapMidpoint);}
            if(OutdoorDraft!=null)foreach(var p in OutdoorDraft.Points)Consider(p,PlanEditing.SnapEndpoint);
        }
        if(anchor!=null)
        {
            if(OrthogonalEnabled||_shiftHeld)
            {if(Math.Abs(result.X-anchor.X)>=Math.Abs(result.Y-anchor.Y))result.Y=anchor.Y;else result.X=anchor.X;}
            else if(PolarEnabled&&_snapKind==PlanEditing.SnapNone)result=PolarPoint(anchor,result,PolarStepDegrees);
        }
        _cursor=result;SnapChanged?.Invoke(_snapKind);InvalidateVisual();return result;
    }

    private void DrawOutdoor(DrawingContext context)
    {
        if(OutdoorDraft==null)return;
        void Path(IReadOnlyList<PointModel> points,bool closed,Pen pen)
        {
            for(var i=0;i<points.Count-1+(closed?1:0);i++)
                DrawCurve(context,pen,new CurveGeometry(points[i],points[(i+1)%points.Count],points[i].Bulge));
        }
        if(OutdoorDraft.OriginalSlab is {} original)
            Path(original.Outline,true,new Pen(Brushes.Gray,1,new DashStyle(new double[]{4,4},0)));
        var body=new Pen(new SolidColorBrush(Color.Parse("#62D3D5")),2);
        if(OutdoorGeometry!=null)
        {
            foreach(var wall in OutdoorGeometry.Walls)
            {
                var curve=CurveGeometry.ForWall(wall);var offset=WallReferenceGeometry.BodyOffset(wall);
                DrawCurve(context,body,curve,offset+wall.Thickness/2);
                DrawCurve(context,body,curve,offset-wall.Thickness/2);
                foreach(var t in new[]{0d,1d})
                {var a=curve.At(t,offset+wall.Thickness/2);var b=curve.At(t,offset-wall.Thickness/2);
                    context.DrawLine(body,Screen(a.X,a.Y),Screen(b.X,b.Y));}
            }
            foreach(var slab in OutdoorGeometry.Slabs)
            {
                var shape=new StreamGeometry();
                using(var g=shape.Open())
                {
                    g.SetFillRule(FillRule.EvenOdd);
                    foreach(var contour in SlabGeometry.Build(slab).Contours)
                    {g.BeginFigure(Screen(contour[0].X,contour[0].Y),true);
                        foreach(var p in contour.Skip(1))g.LineTo(Screen(p.X,p.Y));g.EndFigure(true);}
                }
                context.DrawGeometry(new SolidColorBrush(Color.Parse("#305CBABC")),null,shape);
                Path(slab.Outline,true,body);
                foreach(var hole in slab.Openings)Path(hole.Outline,true,body);
            }
        }
        Path(OutdoorPath,OutdoorClosed,new Pen(Brushes.Gold,1.5));
        foreach(var p in OutdoorDraft.Points)context.DrawEllipse(Brushes.Gold,null,Screen(p.X,p.Y),3,3);
        Path(OutdoorReference,OutdoorReferenceClosed,new Pen(Brushes.Cyan,3));
    }
}
