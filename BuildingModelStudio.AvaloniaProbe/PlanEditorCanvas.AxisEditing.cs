using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Globalization;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;
internal enum AxisEditMode { Off, LabelVisibility, LabelDeletion, LineVisibility, LineDeletion, Add,
    LabelShow, LabelRestore, LineShow, LineRestore }
internal sealed record AxisEditPick(string Id,bool Start);
internal sealed record AxisEditRequest(AxisEditMode Mode,string? Id,bool Start,bool Vertical,double Position);
internal sealed record AxisEditBatch(AxisEditMode Mode,IReadOnlyList<AxisEditPick> Picks,string FloorId);
internal sealed partial class PlanEditorCanvas
{
    public AxisEditMode AxisMode {get;private set;}
    public bool AxisAddVertical {get;set;}=true;
    public event Action<AxisEditRequest>? AxisEditRequested;
    public event Action<AxisEditBatch>? AxisBatchRequested;
    private readonly HashSet<AxisEditPick> _axisSelection = new();
    private Point? _axisPress;
    private bool _axisSubtract;
    private bool _axisBatchPending;
    internal int AxisSelectionCount => _axisSelection.Count;
    internal Rect AxisPromptBounds {get;private set;}
    private bool AxisEndsOnly => AxisMode is AxisEditMode.LabelVisibility or AxisEditMode.LabelDeletion
        or AxisEditMode.LabelShow or AxisEditMode.LabelRestore;
    private bool AxisDeleting => AxisMode is AxisEditMode.LabelDeletion or AxisEditMode.LineDeletion;
    private bool AxisRestoring => AxisMode is AxisEditMode.LabelShow or AxisEditMode.LabelRestore
        or AxisEditMode.LineShow or AxisEditMode.LineRestore;
    private static readonly Dictionary<string,Bitmap> AxisCursorIcons = new();
    private AxisEditPick? _axisHover;
    private List<(AxisModel Axis,Point Start,Point End)>? _axisGhostCache;
    internal void SetAxisEditMode(AxisEditMode mode) {
        if (_axisBatchPending) return;
        ClearAxisSelection();
        AxisMode=mode;_axisHover=null;_axisEndpointCache=null;_axisGhostCache=null;InvalidateVisual();
    }
    internal void ClearAxisSelection() { _axisSelection.Clear(); _axisPress=null; InvalidateVisual(); }
    internal void CompleteAxisBatch(bool success) {
        _axisBatchPending=false;if(success)ClearAxisSelection();InvalidateVisual();
    }
    internal void ConfirmAxisSelection() {
        if(_axisBatchPending||_axisPress!=null||_axisSelection.Count==0||AxisBatchRequested==null)return;
        _axisBatchPending=true;
        AxisBatchRequested.Invoke(new(AxisMode,_axisSelection.ToArray(),_axisStoreyId));
    }
    private IReadOnlyList<(AxisModel Axis,Point Start,Point End)> EditingGhosts()
    {
        // Store world endpoints as Points. Projection stays current as the viewport pans/zooms.
        if(_axisGhostCache!=null)return _axisGhostCache;
        _axisGhostCache=new();
        foreach(var ends in BuildingAxisLayout.Layout(_model,_storeyId,4500,AxisBubbleRadius,includeGhosts:true)) {
            var axis=ends.Axis;var start=ends.StartLabel;var end=ends.EndLabel;
            _axisGhostCache.Add((axis,new Point(start.X,start.Y),new Point(end.X,end.Y)));
        }
        return _axisGhostCache;
    }
    private AxisEditPick? PickEditingAxis(Point pointer,bool endsOnly)
    {
        AxisEditPick? result=null;var best=AxisBubbleRadius*_scale+5;
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
        _snapKind=PlanEditing.SnapNone;
        _axisHover=AxisMode==AxisEditMode.Add?null:PickEditingAxis(point,AxisEndsOnly);
    }
    private void HandleAxisEditPress(Point point,bool subtract) {
        if(_axisBatchPending)return;
        if(AxisMode==AxisEditMode.Add) {
            var p=Snap(point);AxisEditRequested?.Invoke(new(AxisMode,null,false,AxisAddVertical,AxisAddVertical?p.X:p.Y));return;
        }
        if(_axisPress is Point first) {
            _axisPress=null;SelectAxisBox(first,point,_axisSubtract||subtract);
        } else {
            var hit=PickEditingAxis(point,AxisEndsOnly);
            var axis=hit==null?null:EditingGhosts().FirstOrDefault(p=>p.Axis.Id==hit.Id).Axis;
            if(hit!=null&&axis!=null&&EligibleAxis(axis,hit.Start))SelectAxisPick(hit,subtract,true);
            else {_axisPress=point;_axisSubtract=subtract;}
        }
        UpdateAxisEditHover(point);InvalidateVisual();
    }
    private bool EligibleAxis(AxisModel axis,bool start) => AxisMode switch {
        AxisEditMode.LabelVisibility => !axis.Deleted&&!axis.Hidden&&!(start?axis.StartRemoved:axis.EndRemoved)&&!(start?axis.StartHidden:axis.EndHidden),
        AxisEditMode.LabelShow => !axis.Deleted&&!axis.Hidden&&!(start?axis.StartRemoved:axis.EndRemoved)&&(start?axis.StartHidden:axis.EndHidden),
        AxisEditMode.LabelDeletion => !axis.Deleted&&!(start?axis.StartRemoved:axis.EndRemoved),
        AxisEditMode.LabelRestore => !axis.Deleted&&(start?axis.StartRemoved:axis.EndRemoved),
        AxisEditMode.LineVisibility => !axis.Deleted&&!axis.Hidden,
        AxisEditMode.LineShow => !axis.Deleted&&axis.Hidden,
        AxisEditMode.LineDeletion => !axis.Deleted,
        AxisEditMode.LineRestore => axis.Deleted,
        _ => false
    };
    private void SelectAxisPick(AxisEditPick pick,bool subtract,bool toggle) {
        var axis=EditingGhosts().FirstOrDefault(x=>x.Axis.Id==pick.Id).Axis;
        if(axis==null||!EligibleAxis(axis,pick.Start))return;
        if(!AxisEndsOnly)pick=new(pick.Id,false);
        if(subtract||(toggle&&_axisSelection.Contains(pick)))_axisSelection.Remove(pick);
        else _axisSelection.Add(pick);
    }
    internal void FinishAxisSelection(Point point) {
        if(_axisPress is not Point press)return;
        if(Distance(press,point)>=4) {_axisPress=null;SelectAxisBox(press,point,_axisSubtract);}
        InvalidateVisual();
    }
    internal void SelectAxisBox(Point from,Point to,bool subtract=false) {
        var box=AxisSelectionRect(from,to);var crossing=to.X<from.X;
        foreach(var item in EditingGhosts()) {
            var a=Screen(item.Start.X,item.Start.Y);var b=Screen(item.End.X,item.End.Y);
            if(AxisEndsOnly) {
                var radius=AxisBubbleRadius*_scale;
                foreach(var (at,start) in new[] {(a,true),(b,false)}) {
                    var bubble=new Rect(at.X-radius,at.Y-radius,2*radius,2*radius);
                    if(crossing?box.Intersects(bubble):box.Contains(bubble))SelectAxisPick(new(item.Axis.Id,start),subtract,false);
                }
            } else {
                var intersects=item.Axis.Vertical
                    ?a.X>=box.Left&&a.X<=box.Right&&Math.Max(a.Y,b.Y)>=box.Top&&Math.Min(a.Y,b.Y)<=box.Bottom
                    :a.Y>=box.Top&&a.Y<=box.Bottom&&Math.Max(a.X,b.X)>=box.Left&&Math.Min(a.X,b.X)<=box.Right;
                if(crossing?intersects:box.Contains(a)&&box.Contains(b))SelectAxisPick(new(item.Axis.Id,false),subtract,false);
            }
        }
        InvalidateVisual();
    }
    private void DrawAxisEditOverlay(DrawingContext context)
    {
        if(AxisMode==AxisEditMode.Off)return;
        var ghost=new SolidColorBrush(Color.Parse("#617586"));
        var ghostPen=new Pen(ghost,1,new DashStyle(new[] {4d,5d},0));
        var selected=new Pen(new SolidColorBrush(Color.Parse("#38D4FF")),2);
        var hover=new Pen(new SolidColorBrush(Color.Parse(AxisDeleting?"#FFCA73":"#B9E6FF")),2);
        foreach(var item in EditingGhosts()) {
            var axis=item.Axis;var a=Screen(item.Start.X,item.Start.Y);var b=Screen(item.End.X,item.End.Y);
            if(axis.Hidden||axis.Deleted)context.DrawLine(ghostPen,a,b);
            void End(bool start,Point at,bool hidden,bool removed) {
                if(axis.Hidden||axis.Deleted||hidden||removed) {
                    DrawAxisBubble(context,(start?axis.StartName:axis.EndName)??axis.Name??"×",at,ghost);
                    if(removed||axis.Deleted) {var half=250*_scale;context.DrawLine(ghostPen,new(at.X-half,at.Y-half),new(at.X+half,at.Y+half));
                        context.DrawLine(ghostPen,new(at.X-half,at.Y+half),new(at.X+half,at.Y-half));}
                }
                if(_axisSelection.Contains(new(axis.Id,start))&&AxisEndsOnly) {
                    context.DrawEllipse(new SolidColorBrush(Color.Parse("#20384A")),selected,at,AxisBubbleRadius*_scale+2,AxisBubbleRadius*_scale+2);
                    DrawAxisBubble(context,(start?axis.StartName:axis.EndName)??axis.Name??"×",at,selected.Brush!);
                    if(AxisDeleting) {
                        var x=at+new Vector(8,8);var mark=new Pen(new SolidColorBrush(Color.Parse("#FFCA73")),2);
                        context.DrawLine(mark,x-new Vector(3,3),x+new Vector(3,3));context.DrawLine(mark,x+new Vector(-3,3),x+new Vector(3,-3));
                    }
                }
                if(_axisHover?.Id==axis.Id&&_axisHover.Start==start)context.DrawEllipse(null,hover,at,AxisBubbleRadius*_scale+3,AxisBubbleRadius*_scale+3);
            }
            End(true,a,axis.StartHidden,axis.StartRemoved);End(false,b,axis.EndHidden,axis.EndRemoved);
            if(!AxisEndsOnly&&_axisSelection.Contains(new(axis.Id,false)))context.DrawLine(selected,a,b);
            if(_axisHover?.Id==axis.Id&&!AxisEndsOnly)context.DrawLine(hover,a,b);
        }
        if(AxisMode==AxisEditMode.Add&&_cursor!=null) {
            var at=Screen(_cursor.X,_cursor.Y);var preview=new Pen(new SolidColorBrush(Color.Parse("#65E8B2")),1,new DashStyle(new[] {8d,4d},0));
            context.DrawLine(preview,AxisAddVertical?new(at.X,0):new(0,at.Y),AxisAddVertical?new(at.X,Bounds.Height):new(Bounds.Width,at.Y));
        }
        if(_axisPress is Point press&&_lastPointer is Point end&&Distance(press,end)>=4) {
            var crossing=end.X<press.X;var color=crossing?"#65E8B2":"#38B7FF";
            context.DrawRectangle(new SolidColorBrush(Color.Parse(crossing?"#2065E8B2":"#2038B7FF")),
                new Pen(new SolidColorBrush(Color.Parse(color)),1,crossing?new DashStyle(new[] {5d,4d},0):null),AxisSelectionRect(press,end));
        }
        if(_lastPointer is Point pointer&&_panStart==null)DrawAxisCursor(context,pointer);
    }
    private static Rect AxisSelectionRect(Point a,Point b) => new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y));
    private void DrawAxisCursor(DrawingContext context,Point at) {
        var white=new Pen(Brushes.White,1.5);var dark=new Pen(new SolidColorBrush(Color.Parse("#0B1118")),3.5);
        using(context.PushTransform(Matrix.CreateTranslation(at.X,at.Y))) {
            if(AxisDeleting) {
                context.DrawRectangle(null,dark,new Rect(-5,-5,10,10));context.DrawRectangle(null,white,new Rect(-5,-5,10,10));
                foreach(var (a,b) in new[] {(new Point(-8,-8),new Point(-8,-5)),(new Point(8,-8),new Point(8,-5)),
                    (new Point(-8,8),new Point(-8,5)),(new Point(8,8),new Point(8,5))})context.DrawLine(white,a,b);
            } else if(AxisMode==AxisEditMode.Add) {
                context.DrawLine(dark,new(-8,0),new(8,0));context.DrawLine(dark,new(0,-8),new(0,8));
                context.DrawLine(white,new(-8,0),new(8,0));context.DrawLine(white,new(0,-8),new(0,8));
            } else {
                context.DrawGeometry(Brushes.White,new Pen(new SolidColorBrush(Color.Parse("#0B1118")),1.5),
                    Geometry.Parse("M0,0 L0,18 L5,13 L9,21 L12,19 L8,11 L15,11 Z"));
                context.DrawRectangle(null,new Pen(new SolidColorBrush(Color.Parse("#38D4FF")),1.5),new Rect(18,7,7,7));
            }
            if(AxisDeleting) {
                var center=new Point(13,13);context.DrawEllipse(new SolidColorBrush(Color.Parse("#FFCA73")),dark,center,7,7);
                var cross=new Pen(new SolidColorBrush(Color.Parse("#101D27")),1.8);
                context.DrawLine(cross,center-new Vector(3,3),center+new Vector(3,3));
                context.DrawLine(cross,center+new Vector(-3,3),center+new Vector(3,-3));
            } else {
                var icon=AxisMode==AxisEditMode.Add?"file-plus":AxisRestoring?"eye":"eye-off";
                if(!AxisCursorIcons.TryGetValue(icon,out var bitmap)) {
                    bitmap=new Bitmap(AssetLoader.Open(new Uri("avares://万落建筑模型/Resources/Icons/"+icon+".png")));AxisCursorIcons[icon]=bitmap;
                }
                context.DrawImage(bitmap,new Rect(29,12,16,16));
            }
        }
        var prompt=AxisMode switch {
            AxisEditMode.LabelVisibility=>"选择要隐藏的轴号",AxisEditMode.LabelShow=>"选择要显示的轴号",
            AxisEditMode.LabelDeletion=>"选择要删除的轴号",AxisEditMode.LabelRestore=>"选择要恢复的轴号",
            AxisEditMode.LineVisibility=>"选择要隐藏的轴线",AxisEditMode.LineShow=>"选择要显示的轴线",
            AxisEditMode.LineDeletion=>"选择要删除的轴线",AxisEditMode.LineRestore=>"选择要恢复的轴线",
            _=>"指定新轴线的位置"};
        var text=new FormattedText(prompt+"\n"+(_axisBatchPending?"正在应用…":AxisMode==AxisEditMode.Add?"点击创建 · Esc 取消":$"已选 {_axisSelection.Count} 项 · 空格 / 回车确认"),
            CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),13,Brushes.White);
        var w=Math.Min(text.Width+16,Bounds.Width-8);var h=text.Height+12;
        var x=Math.Clamp(at.X+16,4,Math.Max(4,Bounds.Width-w-4));
        var y=at.Y+34+h<=Bounds.Height?at.Y+34:Math.Max(4,at.Y-h-16);
        AxisPromptBounds=new Rect(x,y,w,h);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#F2101D27")),new Pen(new SolidColorBrush(Color.Parse("#526E81")),1),AxisPromptBounds,3,3);
        context.DrawText(text,new Point(x+8,y+6));
    }
    internal Point AxisEditWorldPoint(double x,double y)=>Screen(x,y);
    internal Point AxisEditPoint(string id,bool start,bool center=false) {
        var ghost=EditingGhosts().Single(g=>g.Axis.Id==id);var p=center?new Point((ghost.Start.X+ghost.End.X)/2,(ghost.Start.Y+ghost.End.Y)/2):start?ghost.Start:ghost.End;
        return Screen(p.X,p.Y);
    }
}
