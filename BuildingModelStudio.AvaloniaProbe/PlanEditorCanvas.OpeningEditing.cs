using Avalonia;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class PlanEditorCanvas
{
    private readonly Dictionary<(string,string,double,double,double,double,bool,bool,double,double),List<ViewLine>> _openingPreviewCache=new();
    private OpeningModel? _openingGripDraft;
    private WallModel? _openingGripWall;
    private bool _openingDirectionGrip;
    private bool _openingLabelGrip;
    private PointModel? _openingLabelPressWorld;
    private double _openingLabelStartAlong,_openingLabelStartNormal;
    private bool _openingDistanceLocked,_openingDistanceFromStart;
    private readonly Dictionary<string,List<List<PointModel>>> _openingPickRegions=new();
    private string? _preselectedId;
    private string? _openingGripHover;
    private bool _openingGripMoved;
    private bool _openingGripClickMode;
    private bool _openingReleasingCapture;
    private bool _openingCenterRightDown;
    private PointModel? _openingBaseDirection;
    private readonly Dictionary<(string,string,double,double,double,double,bool,bool,double),PointModel?> _openingHandleCache=new();
    private readonly Dictionary<(string,double,bool),FormattedText> _openingCodeTextCache=new();
    private Point _openingGripPress;
    internal int OpeningPreviewBuildCount {get;private set;}
    internal bool OpeningPreviewMatches(string id,IReadOnlyList<ViewLine> expected)=>_openingSymbols.TryGetValue(id,out var actual)
        &&actual.Count==expected.Count&&actual.Zip(expected,(a,b)=>Math.Abs(a.X1-b.X1)<.001&&Math.Abs(a.Y1-b.Y1)<.001
            &&Math.Abs(a.X2-b.X2)<.001&&Math.Abs(a.Y2-b.Y2)<.001).All(equal=>equal);
    public event Func<OpeningModel,bool>? OpeningGripRequested;
    public event Func<OpeningModel,bool>? OpeningLabelRequested;
    public event Action<Point?,double,bool>? OpeningDistanceChanged;
    public event Action? OpeningCentering;

    internal bool HasOpeningGrip=>_openingGripDraft!=null;
    private void CancelOpeningGrip(){_openingGripDraft=null;_openingGripWall=null;_openingGripMoved=false;_openingGripClickMode=false;
        _openingDistanceLocked=false;OpeningDistanceChanged?.Invoke(null,0,true);}
    private OpeningModel? SelectedOpening=>_model.Openings.FirstOrDefault(o=>o.Id==_selectedId&&Selectable(o.Id));
    private PointModel? LocalDirectionHandle(WallModel wall,OpeningModel opening)
    {
        var code=_model.OpeningOverrides?.FirstOrDefault(o=>o.OpeningId==opening.Id)?.TypeCode??OpeningConstruction.EffectiveCode(opening);
        var key=(code,opening.Kind,opening.Width,opening.Height,opening.Sill,wall.Thickness,opening.PlanFlipAlong,opening.PlanFlipNormal,opening.PlanOpenAngle);
        if(!_openingHandleCache.TryGetValue(key,out var handle)) {
            var type=_model.OpeningTypes.FirstOrDefault(t=>string.Equals(t.Code,code,StringComparison.OrdinalIgnoreCase))??OpeningConstruction.Default(opening);
            try {handle=OpeningPlanGeometry.DirectionHandle(opening,type,wall.Thickness);}
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException) {
                // Invalid legacy/draft grids must not terminate Avalonia's render loop.
                System.Diagnostics.Trace.WriteLine($"Opening direction grip {opening.Id}: {ex.Message}");
                handle=null;
            }
            _openingHandleCache[key]=handle;
        }
        return handle;
    }
    private static double OpeningWallLength(WallModel w)=>Math.Sqrt(Math.Pow(w.X2-w.X1,2)+Math.Pow(w.Y2-w.Y1,2));
    private PointModel OpeningGripWorld(WallModel wall,OpeningModel opening,bool direction)
    {
        var length=OpeningWallLength(wall);var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
        var handle=direction?LocalDirectionHandle(wall,opening):null;
        var along=opening.Offset+(handle!=null?handle.X-opening.Width/2:0);
        var normal=handle?.Y??0;
        var point=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*along,wall.Y1+uy*along);
        return new PointModel(point.X-uy*normal,point.Y+ux*normal);
    }
    internal Point OpeningGripPoint(bool direction)
    {
        var opening=_openingGripDraft??SelectedOpening;
        var wall=_openingGripWall??_model.Walls.First(w=>w.Id==opening!.HostWallId);
        var p=OpeningGripWorld(wall,opening!,direction);return Screen(p.X,p.Y);
    }
    private ViewText? OpeningLabel(WallModel wall,OpeningModel opening)
    {
        var settings=DrawingAnnotationSettings.Resolve(_model);
        return OrthographicProjector.CreatePlanOpeningLabel(wall,opening,settings.TextHeight*AxisAnnotationScale,settings.WidthFactor);
    }
    internal Point OpeningLabelGripPoint()
    {
        var opening=_openingGripDraft??SelectedOpening;
        var wall=_openingGripWall??_model.Walls.First(w=>w.Id==opening!.HostWallId);
        var label=OpeningLabel(wall,opening!)!;var angle=label.Rotation*Math.PI/180;
        var text=OpeningCodeText(label,false);var width=text.Width*label.WidthFactor/_scale;var height=text.Height/_scale;
        var top=Screen(label.X+Math.Cos(angle)*width/2-Math.Sin(angle)*height,
            label.Y+Math.Sin(angle)*width/2+Math.Cos(angle)*height);
        return top+new Vector(-Math.Sin(angle),-Math.Cos(angle))*9;
    }
    private bool TryBeginOpeningGrip(Point point)
    {
        if(_pendingOpening!=null||_moving||_selectedIds.Count!=1)return false;
        var source=SelectedOpening;if(source==null)return false;
        var wall=_model.Walls.FirstOrDefault(w=>w.Id==source.HostWallId&&w.StoreyId==_storeyId&&Selectable(w.Id));
        if(wall==null||OpeningWallLength(wall)<1)return false;
        var positionDistance=Distance(point,OpeningGripPoint(false));
        var directionDistance=LocalDirectionHandle(wall,source)!=null?Distance(point,OpeningGripPoint(true)):double.MaxValue;
        var labelDistance=string.IsNullOrWhiteSpace(source.Code)?double.MaxValue:Distance(point,OpeningLabelGripPoint());
        var nearest=Math.Min(positionDistance,Math.Min(directionDistance,labelDistance));
        if(nearest>14)return false;
        var labelGrip=labelDistance==nearest;
        var direction=!labelGrip&&directionDistance==nearest;
        _openingGripDraft=new OpeningModel {Id=source.Id,HostWallId=source.HostWallId,Code=source.Code,Kind=source.Kind,
            Offset=source.Offset,Width=source.Width,Height=source.Height,Sill=source.Sill,ThresholdHeight=source.ThresholdHeight,
            PlanFlipAlong=source.PlanFlipAlong,PlanFlipNormal=source.PlanFlipNormal,
            PlanLabelAlong=source.PlanLabelAlong,PlanLabelNormal=source.PlanLabelNormal,
            PlanOpenAngle=source.PlanOpenAngle,OpenIn3D=source.OpenIn3D,CodeManuallyEdited=source.CodeManuallyEdited};
        _openingGripWall=wall;_openingDirectionGrip=direction;_openingGripMoved=false;_openingGripPress=point;
        _openingLabelGrip=labelGrip;_openingLabelPressWorld=World(point);
        _openingLabelStartAlong=source.PlanLabelAlong;_openingLabelStartNormal=source.PlanLabelNormal;
        _openingGripClickMode=false;
        var baseline=new OpeningModel {Id=source.Id,Code=source.Code,Kind=source.Kind,Width=source.Width,Height=source.Height,Sill=source.Sill,PlanOpenAngle=source.PlanOpenAngle};
        _openingBaseDirection=LocalDirectionHandle(wall,baseline);
        MoveStageChanged?.Invoke(labelGrip?"移动门窗编号；拖动松手或再次点击完成，Esc 取消":direction?
            "移动鼠标调整左右与内外方向；拖动松手或再次点击完成，Esc 取消":"沿墙移动门窗；右键：沿墙居中；拖动松手完成，可输入洞口边到最近墙端的净距，Esc 取消");
        UpdateOpeningDistance(point);
        return true;
    }
    private void MoveOpeningGrip(Point point)
    {
        if(_openingGripDraft==null||_openingGripWall==null||_openingDistanceLocked)return;
        if(Distance(point,_openingGripPress)<3&&!_openingGripMoved)return;
        _openingGripMoved=true;
        if(_openingLabelGrip) {
            var w=_openingGripWall;var p=World(point);var start=_openingLabelPressWorld!;var length=OpeningWallLength(w);
            var ux=(w.X2-w.X1)/length;var uy=(w.Y2-w.Y1)/length;
            _openingGripDraft.PlanLabelAlong=_openingLabelStartAlong+(p.X-start.X)*ux+(p.Y-start.Y)*uy;
            _openingGripDraft.PlanLabelNormal=_openingLabelStartNormal-(p.X-start.X)*uy+(p.Y-start.Y)*ux;
            KeepOpeningLabelOutsideWall(w,_openingGripDraft);
        } else if(_openingDirectionGrip) {
            var center=OpeningGripWorld(_openingGripWall,_openingGripDraft,false);var p=World(point);
            var w=_openingGripWall;var length=OpeningWallLength(w);var ux=(w.X2-w.X1)/length;var uy=(w.Y2-w.Y1)/length;
            var along=(p.X-center.X)*ux+(p.Y-center.Y)*uy;
            var normal=-(p.X-center.X)*uy+(p.Y-center.Y)*ux;
            var deadzone=4/_scale;
            if(Math.Abs(along)>deadzone&&_openingBaseDirection!=null)
                _openingGripDraft.PlanFlipAlong=(along<0)!=(_openingBaseDirection.X-_openingGripDraft.Width/2<0);
            if(Math.Abs(normal)>deadzone&&_openingBaseDirection!=null)
                _openingGripDraft.PlanFlipNormal=(normal<0)!=(_openingBaseDirection.Y<0);
        } else {
            var hit=HitWall(point);if(hit.wall!=null)_openingGripWall=hit.wall;
            var w=_openingGripWall;var p=World(point);var length=OpeningWallLength(w);
            _openingGripDraft.HostWallId=w.Id;
            _openingGripDraft.Offset=((p.X-w.X1)*(w.X2-w.X1)+(p.Y-w.Y1)*(w.Y2-w.Y1))/length;
        }
        UpdateOpeningDistance(point);
    }
    private void KeepOpeningLabelOutsideWall(WallModel wall,OpeningModel opening)
    {
        var label=OpeningLabel(wall,opening);if(label==null)return;
        var center=OpeningGripWorld(wall,opening,false);var angle=label.Rotation*Math.PI/180;
        var text=OpeningCodeText(label,false);var height=text.Height/_scale;
        var width=text.Width*label.WidthFactor/_scale;
        var cx=label.X+Math.Cos(angle)*width/2-Math.Sin(angle)*height/2;
        var cy=label.Y+Math.Sin(angle)*width/2+Math.Cos(angle)*height/2;
        var length=OpeningWallLength(wall);var normal=(-(cx-center.X)*(wall.Y2-wall.Y1)+(cy-center.Y)*(wall.X2-wall.X1))/length;
        var minimum=wall.Thickness/2+height/2+Math.Max(10,label.Height*.12);
        if(Math.Abs(normal)<minimum)opening.PlanLabelNormal+=(normal<0?-minimum:minimum)-normal;
    }
    private void UpdateOpeningDistance(Point point)
    {
        if(_openingGripDraft==null||_openingGripWall==null||_openingDirectionGrip||_openingLabelGrip)return;
        var clearance=OpeningPlanGeometry.WallEndClearance(OpeningWallLength(_openingGripWall),_openingGripDraft,out var fromStart);
        _openingDistanceFromStart=fromStart;OpeningDistanceChanged?.Invoke(point,clearance,fromStart);
    }
    internal bool CanEnterOpeningDistance=>HasOpeningGrip&&!_openingDirectionGrip&&!_openingLabelGrip;
    internal void LockOpeningDistance()=>_openingDistanceLocked=true;
    internal bool CenterOpeningGrip()
    {
        if(!CanEnterOpeningDistance||_openingGripWall==null)return false;
        OpeningCentering?.Invoke();
        _openingGripDraft!.Offset=OpeningWallLength(_openingGripWall)/2;
        _openingGripMoved=true;_openingDistanceLocked=false;
        UpdateOpeningDistance(OpeningGripPoint(false));FinishOpeningGrip();
        if(HasOpeningGrip){_openingDistanceLocked=true;_openingGripClickMode=true;}
        return !HasOpeningGrip;
    }
    internal bool HandleOpeningCenterPress(Avalonia.Input.PointerPressedEventArgs e)
    {
        if(e.GetCurrentPoint(this).Properties.PointerUpdateKind!=Avalonia.Input.PointerUpdateKind.RightButtonPressed
            ||!CanEnterOpeningDistance)return false;
        // Capture the matching release even when the right click starts over the distance input.
        _openingReleasingCapture=true;
        try {e.Pointer.Capture(this);}finally {_openingReleasingCapture=false;}
        _openingCenterRightDown=true;CenterOpeningGrip();e.Handled=true;return true;
    }
    internal bool HandleOpeningCenterRelease(Avalonia.Input.PointerReleasedEventArgs e)
    {
        if(!_openingCenterRightDown||e.GetCurrentPoint(this).Properties.PointerUpdateKind!=Avalonia.Input.PointerUpdateKind.RightButtonReleased)return false;
        _openingCenterRightDown=false;_openingReleasingCapture=true;
        try {if(e.Pointer.Captured==this)e.Pointer.Capture(null);}finally {_openingReleasingCapture=false;}
        e.Handled=true;return true;
    }
    internal bool CommitOpeningDistance(string text)
    {
        if(!CanEnterOpeningDistance||_openingGripWall==null)return false;
        if(!double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.CurrentCulture,out var clearance)
            ||!double.IsFinite(clearance)||clearance<0||clearance>OpeningWallLength(_openingGripWall)-_openingGripDraft!.Width) {
            MoveStageChanged?.Invoke("净距须为不小于 0 且不超出墙长的毫米数。");return false;
        }
        _openingGripDraft!.Offset=OpeningPlanGeometry.OffsetFromWallEnd(OpeningWallLength(_openingGripWall),_openingGripDraft.Width,clearance,_openingDistanceFromStart);
        _openingGripMoved=true;_openingDistanceLocked=false;FinishOpeningGrip();
        if(HasOpeningGrip)_openingDistanceLocked=true;
        return !HasOpeningGrip;
    }
    private void FinishOpeningGrip()
    {
        var draft=_openingGripDraft;
        if(_openingDistanceLocked)return;
        if(!_openingGripMoved||draft==null||(_openingLabelGrip?OpeningLabelRequested?.Invoke(draft):OpeningGripRequested?.Invoke(draft))==true)CancelOpeningGrip();
        InvalidateVisual();
    }
    internal void ConfirmOpeningGrip()=>FinishOpeningGrip();
    private void DrawOpeningPreviewSymbols(DrawingContext context,WallModel wall,OpeningModel opening,Pen pen,OpeningTypeModel? placementType=null)
    {
        var code=_model.OpeningOverrides?.FirstOrDefault(o=>o.OpeningId==opening.Id)?.TypeCode??opening.Code;
        var key=(code??"",opening.Kind,opening.Width,opening.Height,opening.Sill,wall.Thickness,opening.PlanFlipAlong,opening.PlanFlipNormal,opening.PlanOpenAngle,opening.ThresholdHeight);
        if(!_openingPreviewCache.TryGetValue(key,out var lines)) {
            var localWall=new WallModel {Id="local",StoreyId="local",X2=opening.Width,Thickness=wall.Thickness};
            var model=new BuildingModelDocument {Walls=new(){localWall},OpeningTypes=placementType==null?_model.OpeningTypes:new(){placementType},
                Openings=new(){new OpeningModel {Id="local",HostWallId="local",Code=code,Kind=opening.Kind,Offset=opening.Width/2,
                    Width=opening.Width,Height=opening.Height,Sill=opening.Sill,ThresholdHeight=opening.ThresholdHeight,PlanFlipAlong=opening.PlanFlipAlong,PlanFlipNormal=opening.PlanFlipNormal,PlanOpenAngle=opening.PlanOpenAngle}}};
            try {lines=OrthographicProjector.CreatePlanDetailSymbols(model,"local");}
            catch(Exception ex) when(ex is InvalidOperationException or ArgumentException) {
                System.Diagnostics.Trace.WriteLine($"Opening grip preview {opening.Id}: {ex.Message}");
                lines=new();
            }
            _openingPreviewCache[key]=lines;OpeningPreviewBuildCount++;
        }
        var length=OpeningWallLength(wall);var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
        var from=opening.Offset-opening.Width/2;var start=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*from,wall.Y1+uy*from);
        DrawOpeningSymbolLines(context,lines,(x,y)=>Screen(start.X+ux*x-uy*y,start.Y+uy*x+ux*y),pen);
    }
    internal static void DrawOpeningSymbolLines(DrawingContext context,List<ViewLine> lines,Func<double,double,Point> map,Pen pen)
    {
        foreach(var line in lines.Where(l=>l.OpeningArcId==null))
            context.DrawLine(line.LineType=="HIDDEN"?new Pen(pen.Brush,pen.Thickness,new DashStyle(new[]{4d,3d},0)):pen,
                map(line.X1,line.Y1),map(line.X2,line.Y2));
        var brush=pen.Brush is ISolidColorBrush color?new SolidColorBrush(color.Color,.55):pen.Brush;
        var arcPen=new Pen(brush,pen.Thickness,new DashStyle(new[]{5d,3d},0),PenLineCap.Flat,PenLineJoin.Round);
        foreach(var arc in lines.Where(l=>l.OpeningArcId!=null).GroupBy(l=>l.OpeningArcId)) {
            var geometry=new StreamGeometry();using(var path=geometry.Open()) {
                var first=arc.First();path.BeginFigure(map(first.X1,first.Y1),false);
                foreach(var line in arc)path.LineTo(map(line.X2,line.Y2));path.EndFigure(false);
            }
            context.DrawGeometry(null,arcPen,geometry);
        }
    }
    private void DrawOpeningGrips(DrawingContext context)
    {
        var opening=_openingGripDraft??SelectedOpening;if(opening==null)return;
        var wall=_openingGripWall??_model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId&&w.StoreyId==_storeyId&&Visible(w.Id));
        if(wall==null||OpeningWallLength(wall)<1)return;
        if(_openingGripDraft!=null&&!_openingLabelGrip) {
            var center=OpeningGripWorld(wall,opening,false);var p=Screen(center.X,center.Y);var length=OpeningWallLength(wall);
            var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
            var delta=new Vector(ux*opening.Width*_scale/2,-uy*opening.Width*_scale/2);
            if(OpeningPlanGeometry.CutsWall(opening))context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#111A25")),wall.Thickness*_scale),p-delta,p+delta);
            var valid=opening.Offset-opening.Width/2>=0&&opening.Offset+opening.Width/2<=length;
            DrawOpeningPreviewSymbols(context,wall,opening,new Pen(valid?Brushes.Cyan:Brushes.OrangeRed,2));
        }
        var position=OpeningGripWorld(wall,opening,false);var screen=Screen(position.X,position.Y);
        context.DrawRectangle(Brushes.Cyan,new Pen(Brushes.Black,1),new Rect(screen.X-7,screen.Y-7,14,14));
        if(LocalDirectionHandle(wall,opening)!=null) {
            var direction=OpeningGripWorld(wall,opening,true);var target=Screen(direction.X,direction.Y);
            context.DrawLine(new Pen(Brushes.Gold,1,new DashStyle(new[]{3d,3d},0)),screen,target);
            context.DrawEllipse(Brushes.Gold,new Pen(Brushes.Black,1),target,7,7);
        }
        if(!string.IsNullOrWhiteSpace(opening.Code)) {
            var labelPoint=OpeningLabelGripPoint();
            context.DrawRectangle(Brushes.LightGreen,new Pen(Brushes.Black,1),new Rect(labelPoint.X-7,labelPoint.Y-7,14,14));
        }
        if(CanEnterOpeningDistance)DrawOpeningClearance(context,wall,opening);
    }
    private void DrawOpeningClearance(DrawingContext context,WallModel wall,OpeningModel opening)
    {
        var length=OpeningWallLength(wall);OpeningPlanGeometry.WallEndClearance(length,opening,out var first);
        var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
        var end=first?0:length;var jamb=opening.Offset+(first?-opening.Width/2:opening.Width/2);
        var a=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*end,wall.Y1+uy*end);
        var b=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*jamb,wall.Y1+uy*jamb);
        var pa=Screen(a.X,a.Y);var pb=Screen(b.X,b.Y);var shift=new Vector(uy,-ux)*(wall.Thickness*_scale/2+22);
        var pen=new Pen(Brushes.Cyan,1);context.DrawLine(pen,pa,pa+shift);context.DrawLine(pen,pb,pb+shift);
        context.DrawLine(pen,pa+shift,pb+shift);
        foreach(var p in new[]{pa+shift,pb+shift})context.DrawLine(pen,p-new Vector(4,4),p+new Vector(4,4));
    }
    private void DrawOpeningCodes(DrawingContext context)
    {
        foreach(var source in _model.Openings.Where(o=>Visible(o.Id))) {
            var opening=source.Id==_openingGripDraft?.Id?_openingGripDraft:source;
            var wall=_model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId&&w.StoreyId==_storeyId&&Visible(w.Id));
            if(wall==null)continue;
            var label=OpeningLabel(wall,opening);
            if(label==null)continue;
            var text=OpeningCodeText(label,_selectedIds.Contains(opening.Id));
            var p=Screen(label.X,label.Y);
            using(context.PushTransform(Matrix.CreateScale(label.WidthFactor,1)*Matrix.CreateRotation(-label.Rotation*Math.PI/180)*Matrix.CreateTranslation(p.X,p.Y)))
                context.DrawText(text,new Point(0,-text.Height));
        }
    }
    internal string? PreselectedElement=>_preselectedId;
    private string? OpeningGripHover(Point point)
    {
        if(_selectedIds.Count!=1||SelectedOpening is not OpeningModel opening)return null;
        var wall=_model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId&&w.StoreyId==_storeyId&&Selectable(w.Id));
        if(wall==null||OpeningWallLength(wall)<1)return null;
        var candidates=new List<(double distance,string name)> {(Distance(point,OpeningGripPoint(false)),"移动门窗")};
        if(LocalDirectionHandle(wall,opening)!=null)candidates.Add((Distance(point,OpeningGripPoint(true)),"调整开启方向"));
        if(!string.IsNullOrWhiteSpace(opening.Code))candidates.Add((Distance(point,OpeningLabelGripPoint()),"移动编号"));
        var nearest=candidates.OrderBy(c=>c.distance).First();return nearest.distance<=14?nearest.name:null;
    }
    private static double SegmentDistance(Point p,Point a,Point b)
    {
        var dx=b.X-a.X;var dy=b.Y-a.Y;
        var t=Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/Math.Max(1e-12,dx*dx+dy*dy),0,1);
        return Distance(p,new Point(a.X+t*dx,a.Y+t*dy));
    }
    private string? HitOpening(Point point)
    {
        string? hit=null;var best=double.MaxValue;var world=World(point);var tolerance=Math.Max(7,PickboxSize/2);
        foreach(var opening in _model.Openings.Where(o=>Selectable(o.Id))) {
            var wall=_model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId&&w.StoreyId==_storeyId&&Selectable(w.Id));
            if(wall==null)continue;var length=OpeningWallLength(wall);if(length<1)continue;
            var start=OpeningGripWorld(wall,opening,false);var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
            var local=new PointModel((world.X-start.X)*ux+(world.Y-start.Y)*uy+opening.Width/2,
                -(world.X-start.X)*uy+(world.Y-start.Y)*ux);
            var score=double.MaxValue;
            if(_openingSymbols.TryGetValue(opening.Id,out var lines))foreach(var line in lines)
                score=Math.Min(score,SegmentDistance(point,Screen(line.X1,line.Y1),Screen(line.X2,line.Y2)));
            if(_openingPickRegions.TryGetValue(opening.Id,out var regions)&&regions.Any(region=>InsideContour(local,region)))
                score=Math.Min(score,Math.Min(tolerance*.8,Math.Abs(local.Y)*_scale));
            var label=OpeningLabel(wall,opening);
            if(label!=null) {
                var p=Screen(label.X,label.Y);var angle=label.Rotation*Math.PI/180;
                var x=(point.X-p.X)*Math.Cos(angle)-(point.Y-p.Y)*Math.Sin(angle);
                var y=(point.X-p.X)*Math.Sin(angle)+(point.Y-p.Y)*Math.Cos(angle);
                var text=OpeningCodeText(label,false);
                if(new Rect(-3,-text.Height-3,text.Width*label.WidthFactor+6,text.Height+6).Contains(new Point(x,y)))score=Math.Min(score,1);
            }
            if(score<=tolerance&&score<best){best=score;hit=opening.Id;}
        }
        return hit;
    }
    private FormattedText OpeningCodeText(ViewText label,bool selected)
    {
        var key=(label.Text,label.Height*_scale,selected);
        if(!_openingCodeTextCache.TryGetValue(key,out var text)) {
            text=new FormattedText(label.Text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                Typeface.Default,key.Item2,new SolidColorBrush(Color.Parse(selected?"#FFC46B":"#A7B8C5")));
            _openingCodeTextCache[key]=text;
        }
        return text;
    }
    private void DrawSelectionPreview(DrawingContext context)
    {
        if(!ShowSelectionPreview||_preselectedId is not string id||_lastPointer is not Point pnt)return;
        var pen=new Pen(Brushes.Cyan,2,new DashStyle(new[]{4d,2d},0));
        var opening=_model.Openings.FirstOrDefault(o=>o.Id==id);var wall=_model.Walls.FirstOrDefault(w=>w.Id==id);
        string name;
        if(opening!=null) {
            if(_openingSymbols.TryGetValue(id,out var lines))foreach(var line in lines)context.DrawLine(pen,Screen(line.X1,line.Y1),Screen(line.X2,line.Y2));
            var host=_model.Walls.First(w=>w.Id==opening.HostWallId);var center=OpeningGripWorld(host,opening,false);
            var length=OpeningWallLength(host);var along=new Vector((host.X2-host.X1)/length,-(host.Y2-host.Y1)/length)*opening.Width*_scale/2;
            context.DrawLine(pen,Screen(center.X,center.Y)-along,Screen(center.X,center.Y)+along);
            name=(_openingGripHover??opening.Kind)+" "+opening.Code;
        } else {
            List<PointModel>? outline=null;
            var column=_model.Columns.FirstOrDefault(c=>c.Id==id);var beam=_model.Beams.FirstOrDefault(b=>b.Id==id);var slab=_model.Slabs.FirstOrDefault(s=>s.Id==id);
            if(column!=null)outline=StructuralGeometry.ColumnOutline(column);else if(beam!=null)outline=StructuralGeometry.BeamOutline(beam);
            else if(slab!=null&&_slabGeometry.TryGetValue(id,out var geometry))foreach(var contour in geometry.Contours)
                for(var i=0;i<contour.Count;i++)context.DrawLine(pen,Screen(contour[i].X,contour[i].Y),Screen(contour[(i+1)%contour.Count].X,contour[(i+1)%contour.Count].Y));
            if(outline!=null)for(var i=0;i<outline.Count;i++)context.DrawLine(pen,Screen(outline[i].X,outline[i].Y),Screen(outline[(i+1)%outline.Count].X,outline[(i+1)%outline.Count].Y));
            name=wall!=null?"墙 "+wall.Code:column!=null?"柱 "+column.Code:beam!=null?"梁 "+beam.Code:"楼板 "+slab?.Code;
        }
        var text=new FormattedText(name,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brushes.Cyan);
        var x=Math.Clamp(pnt.X+16,4,Math.Max(4,Bounds.Width-text.Width-12));var y=Math.Clamp(pnt.Y+20,4,Math.Max(4,Bounds.Height-text.Height-10));
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#172734")),new Pen(Brushes.Cyan,1),new Rect(x-4,y-3,text.Width+8,text.Height+6));
        context.DrawText(text,new Point(x,y));
    }
    private bool ShowSelectionPreview=>_preselectedId!=null&&_lastPointer!=null&&!HasOpeningGrip
        &&Tool==PlanTool.Select&&!_moving&&AxisMode==AxisEditMode.Off;
}
