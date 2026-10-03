using Avalonia;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal enum AxisEditMode { Off, LabelVisibility, LabelDeletion, LineVisibility, LineDeletion, Add }
internal sealed record AxisEditPick(string Id,bool Start);
internal sealed record AxisEditRequest(AxisEditMode Mode,string? Id,bool Start,bool Vertical,double Position);
internal sealed partial class PlanEditorCanvas
{
    public AxisEditMode AxisMode {get;private set;}
    public bool AxisAddVertical {get;set;}=true;
    public event Action<AxisEditRequest>? AxisEditRequested;
    private AxisEditPick? _axisHover;
    private List<(AxisModel Axis,Point Start,Point End)>? _axisGhostCache;
    internal void SetAxisEditMode(AxisEditMode mode) {
        AxisMode=mode;_axisHover=null;_axisEndpointCache=null;_axisGhostCache=null;InvalidateVisual();
    }
    private IReadOnlyList<(AxisModel Axis,Point Start,Point End)> EditingGhosts()
    {
        // Store world endpoints as Points. Projection stays current as the viewport pans/zooms.
        if(_axisGhostCache!=null)return _axisGhostCache;
        _axisGhostCache=new();
        foreach(var ends in AxisEndpoints()) {
            var axis=ends.Axis;var start=ends.Start;var end=ends.End;
            if(axis.StartRemoved||axis.EndRemoved) {
                var walls=_model.Walls.Where(w=>w.StoreyId==_storeyId).ToArray();
                var values=walls.SelectMany(w=>axis.Vertical?new[] {w.Y1-w.Thickness/2,w.Y2+w.Thickness/2}:new[] {w.X1-w.Thickness/2,w.X2+w.Thickness/2}).ToArray();
                var automatic=axis.ExtentStart==0&&axis.ExtentEnd==0;
                var lo=automatic?(values.Length==0?-1500:values.Min())-4500:Math.Min(axis.ExtentStart,axis.ExtentEnd);
                var hi=automatic?(values.Length==0?1500:values.Max())+4500:Math.Max(axis.ExtentStart,axis.ExtentEnd);
                if(axis.StartRemoved)start=axis.Vertical?new(axis.Position,lo):new(lo,axis.Position);
                if(axis.EndRemoved)end=axis.Vertical?new(axis.Position,hi):new(hi,axis.Position);
            }
            _axisGhostCache.Add((axis,new Point(start.X,start.Y),new Point(end.X,end.Y)));
        }
        return _axisGhostCache;
    }
    private AxisEditPick? PickEditingAxis(Point pointer,bool endsOnly)
    {
        AxisEditPick? result=null;var best=450*_scale+5;
        foreach(var item in EditingGhosts()) {
            var a=Screen(item.Start.X,item.Start.Y);var b=Screen(item.End.X,item.End.Y);
            var da=Distance(pointer,a);var db=Distance(pointer,b);
            if(Math.Min(da,db)<best) {best=Math.Min(da,db);result=new(item.Axis.Id,da<db);}
        }
        if(result!=null||endsOnly)return result;
        best=Math.Max(5,PickboxSize/2);
        foreach(var item in EditingGhosts()) {
            var a=Screen(item.Start.X,item.Start.Y);var b=Screen(item.End.X,item.End.Y);
            var dx=b.X-a.X;var dy=b.Y-a.Y;var t=Math.Clamp(((pointer.X-a.X)*dx+(pointer.Y-a.Y)*dy)/Math.Max(1,dx*dx+dy*dy),0,1);
            var distance=Distance(pointer,new Point(a.X+t*dx,a.Y+t*dy));
            if(distance<best) {best=distance;result=new(item.Axis.Id,t<.5);}
        }
        return result;
    }
    private void UpdateAxisEditHover(Point point) {
        _cursor=AxisMode==AxisEditMode.Add?Snap(point):World(point);
        _axisHover=AxisMode==AxisEditMode.Add?null:PickEditingAxis(point,AxisMode is AxisEditMode.LabelVisibility or AxisEditMode.LabelDeletion);
    }
    private void HandleAxisEditClick(Point point) {
        if(AxisMode==AxisEditMode.Add) {
            var p=Snap(point);AxisEditRequested?.Invoke(new(AxisMode,null,false,AxisAddVertical,AxisAddVertical?p.X:p.Y));return;
        }
        var hit=PickEditingAxis(point,AxisMode is AxisEditMode.LabelVisibility or AxisEditMode.LabelDeletion);
        if(hit!=null)AxisEditRequested?.Invoke(new(AxisMode,hit.Id,hit.Start,false,0));
    }
    private void DrawAxisEditOverlay(DrawingContext context)
    {
        if(AxisMode==AxisEditMode.Off)return;
        var ghost=new SolidColorBrush(Color.Parse("#617586"));
        var ghostPen=new Pen(ghost,1,new DashStyle(new[] {4d,5d},0));
        var selected=new Pen(new SolidColorBrush(Color.Parse("#FFCA73")),2);
        foreach(var item in EditingGhosts()) {
            var axis=item.Axis;var a=Screen(item.Start.X,item.Start.Y);var b=Screen(item.End.X,item.End.Y);
            if(axis.Hidden||axis.Deleted)context.DrawLine(ghostPen,a,b);
            void End(bool start,Point at,bool hidden,bool removed) {
                if(axis.Hidden||axis.Deleted||hidden||removed) {
                    DrawAxisBubble(context,(start?axis.StartName:axis.EndName)??axis.Name??"×",at,ghost);
                    if(removed||axis.Deleted) {var half=250*_scale;context.DrawLine(ghostPen,new(at.X-half,at.Y-half),new(at.X+half,at.Y+half));
                        context.DrawLine(ghostPen,new(at.X-half,at.Y+half),new(at.X+half,at.Y-half));}
                }
                if(_axisHover?.Id==axis.Id&&_axisHover.Start==start)context.DrawEllipse(null,selected,at,450*_scale+3,450*_scale+3);
            }
            End(true,a,axis.StartHidden,axis.StartRemoved);End(false,b,axis.EndHidden,axis.EndRemoved);
            if(_axisHover?.Id==axis.Id&&AxisMode is AxisEditMode.LineVisibility or AxisEditMode.LineDeletion)context.DrawLine(selected,a,b);
        }
        if(AxisMode==AxisEditMode.Add&&_cursor!=null) {
            var at=Screen(_cursor.X,_cursor.Y);var preview=new Pen(new SolidColorBrush(Color.Parse("#65E8B2")),1,new DashStyle(new[] {8d,4d},0));
            context.DrawLine(preview,AxisAddVertical?new(at.X,0):new(0,at.Y),AxisAddVertical?new(at.X,Bounds.Height):new(Bounds.Width,at.Y));
        }
    }
    internal Point AxisEditWorldPoint(double x,double y)=>Screen(x,y);
    internal Point AxisEditPoint(string id,bool start,bool center=false) {
        var ghost=EditingGhosts().Single(g=>g.Axis.Id==id);var p=center?new Point((ghost.Start.X+ghost.End.X)/2,(ghost.Start.Y+ghost.End.Y)/2):start?ghost.Start:ghost.End;
        return Screen(p.X,p.Y);
    }
}
