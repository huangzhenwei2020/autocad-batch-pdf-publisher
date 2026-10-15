using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed partial class PlanEditorCanvas
{
    private PointModel? _arcEnd;
    private bool _contourArc;
    private WallDrawingChoice _wallDrawing=new();
    internal void SetWallDrawing(WallDrawingChoice choice){_wallDrawing=choice;InvalidateVisual();}
    private string? _filletFirst, _filletSlab;
    private PointModel? _filletPick;
    internal double FilletRadius {get;set;}=1000;
    internal event Func<PointModel,PointModel,double,bool>? ArcWallRequested;
    internal event Func<string,PointModel,string,PointModel,double,bool>? WallFilletRequested;
    internal event Func<string,int,double,bool>? SlabFilletRequested;

    internal void BeginFillet(string? slabId)
    {
        CancelDraft();Tool=PlanTool.Fillet;_filletSlab=slabId;
        Cursor=new Cursor(StandardCursorType.Cross);
        MoveStageChanged?.Invoke(slabId!=null?"圆角 F：点击楼板角点；输入数字可改半径，Esc 退出。":"圆角 F：选择第一面直墙（点击要保留的一侧）；Esc 退出。");
    }
    internal void BeginContourArc()
    {
        if(!IsContourTool||Tool==PlanTool.SlabHoleRectangle||_contour.Count==0)return;
        _contourArc=true;_arcEnd=null;
        MoveStageChanged?.Invoke("圆弧边：指定终点，再指定弧线经过点；L 返回直线。Esc 取消。");
    }
    internal void EndContourArc(){_contourArc=false;_arcEnd=null;InvalidateVisual();}
    private void CancelCurveDraft()
    {
        _arcEnd=null;_contourArc=false;_filletFirst=null;_filletPick=null;_filletSlab=null;
        Cursor=new Cursor(StandardCursorType.None);
    }
    internal bool HandleCurveClick(Point screen)
    {
        if(Tool==PlanTool.Fillet)
        {
            if(_filletSlab!=null)
            {
                var slab=_model.Slabs.FirstOrDefault(s=>s.Id==_filletSlab&&s.StoreyId==_storeyId);
                if(slab==null)return true;
                var index=Enumerable.Range(0,slab.Outline.Count).OrderBy(i=>Distance(screen,Screen(slab.Outline[i].X,slab.Outline[i].Y))).First();
                if(Distance(screen,Screen(slab.Outline[index].X,slab.Outline[index].Y))>24)
                {MoveStageChanged?.Invoke("请点选楼板外轮廓角点。Esc 退出。");return true;}
                SlabFilletRequested?.Invoke(slab.Id,index,FilletRadius);return true;
            }
            var hit=HitWall(screen).wall;
            if(hit==null){MoveStageChanged?.Invoke("请点选直墙；楼板圆角请先选择楼板再输入 F。");return true;}
            if(Math.Abs(hit.Bulge)>1e-10){MoveStageChanged?.Invoke("请选择直墙；弧墙已有圆弧参数。");return true;}
            var point=World(screen);
            if(_filletFirst==null){_filletFirst=hit.Id;_filletPick=point;MoveStageChanged?.Invoke("圆角 F：选择第二面直墙；半径 "+FilletRadius.ToString("0.###")+" mm。");}
            else if(hit.Id==_filletFirst)MoveStageChanged?.Invoke("请选择另一面直墙。");
            else if(WallFilletRequested?.Invoke(_filletFirst,_filletPick!,hit.Id,point,FilletRadius)==true)
            {_filletFirst=null;_filletPick=null;MoveStageChanged?.Invoke("已完成墙圆角；继续选择第一面墙，Esc 退出；Ctrl+Z 撤销。");}
            return true;
        }
        if(Tool!=PlanTool.ArcWall&&!_contourArc)return false;
        var end=Snap(screen);var start=Tool==PlanTool.ArcWall?_wallStart:_contour.LastOrDefault();
        if(start==null){_wallStart=end;MoveStageChanged?.Invoke("弧墙：指定终点，再指定弧线经过点。");return true;}
        if(_arcEnd==null)
        {
            if(Math.Abs(end.X-start.X)+Math.Abs(end.Y-start.Y)<10)return true;
            _arcEnd=end;MoveStageChanged?.Invoke("指定弧线经过点；捕捉 CAD 弧线上的位置可对齐底图。Esc 取消。");return true;
        }
        try
        {
            var bulge=CurveGeometry.ThroughBulge(start,_arcEnd,end);
            if(Tool==PlanTool.ArcWall)
            {
                if(ArcWallRequested?.Invoke(start,_arcEnd,bulge)!=true)return true;
                _wallStart=null;
            }
            else
            {
                _contour[^1].Bulge=bulge;
                var closing=_contour.Count>=3&&Distance(Screen(_arcEnd.X,_arcEnd.Y),Screen(_contour[0].X,_contour[0].Y))<=12;
                if(!closing)AddContourPoint(_arcEnd);
                _contourArc=false;
                if(closing){_arcEnd=null;CompleteContour();}
            }
            _arcEnd=null;MoveStageChanged?.Invoke(Tool==PlanTool.ArcWall?"已画弧墙；点选下一面弧墙的起点，Esc 退出。":"已添加圆弧边；继续画直边，A 添加圆弧，Enter 闭合。");
        }
        catch(ArgumentException ex){MoveStageChanged?.Invoke(ex.Message);}
        return true;
    }
    private void DrawCurve(DrawingContext context,Pen pen,CurveGeometry curve,double offset=0,Vector translation=default)
    {
        var geometry=new StreamGeometry();
        var a=curve.At(0,offset);var b=curve.At(1,offset);
        using(var path=geometry.Open())
        {
            path.BeginFigure(Screen(a.X,a.Y)+translation,false);
            if(curve.IsArc)
            {
                var radius=Math.Abs(curve.Radius-Math.Sign(curve.Sweep)*offset)*_scale;
                path.ArcTo(Screen(b.X,b.Y)+translation,new Size(radius,radius),0,Math.Abs(curve.Sweep)>Math.PI,
                    curve.Sweep>0?SweepDirection.CounterClockwise:SweepDirection.Clockwise);
            }
            else path.LineTo(Screen(b.X,b.Y)+translation);
            path.EndFigure(false);
        }
        context.DrawGeometry(null,pen,geometry);
    }
    private void DrawCurveDraft(DrawingContext context)
    {
        var start=Tool==PlanTool.ArcWall?_wallStart:_contourArc?_contour.LastOrDefault():null;
        if(start==null||_arcEnd==null||_cursor==null)return;
        try
        {
            var bulge=CurveGeometry.ThroughBulge(start,_arcEnd,_cursor);
            var curve=new CurveGeometry(start,_arcEnd,bulge);
            if(Tool==PlanTool.ArcWall)DrawWallDraft(context,curve);
            else DrawCurve(context,new Pen(new SolidColorBrush(Color.Parse("#65E8B2")),2),curve);
        } catch(ArgumentException){}
    }
    private void DrawWallDraft(DrawingContext context,CurveGeometry curve)
    {
        var wall=_wallDrawing.CreateWall(_storeyId,curve.Start,curve.End,Math.Tan(curve.Sweep/4));
        if(PlanEditing.ValidateWall(wall)!=null)return;
        var offset=WallReferenceGeometry.BodyOffset(wall);
        DrawCurve(context,new Pen(new SolidColorBrush(Color.Parse("#294D50")),wall.Thickness*_scale),curve,offset);
        var edge=new Pen(new SolidColorBrush(Color.Parse("#65E8B2")),1.5);
        DrawCurve(context,edge,curve,offset+wall.Thickness/2);DrawCurve(context,edge,curve,offset-wall.Thickness/2);
        DrawCurve(context,new Pen(Brushes.LightGreen,1,new DashStyle(new[]{5d,4d},0)),curve);
    }
    private WallModel PreviewCurveWall(WallModel wall)
    {
        if(_gripWallId!=wall.Id||_gripPosition==null)return wall;
        var copy=new WallModel {Id=wall.Id,StoreyId=wall.StoreyId,X1=wall.X1,Y1=wall.Y1,X2=wall.X2,Y2=wall.Y2,
            Bulge=wall.Bulge,Thickness=wall.Thickness,AxisOffset=wall.AxisOffset,AxisPlacement=wall.AxisPlacement};
        if(_gripIndex==0){copy.X1=_gripPosition.X;copy.Y1=_gripPosition.Y;}
        else if(_gripIndex==1){copy.X2=_gripPosition.X;copy.Y2=_gripPosition.Y;}
        else try {copy.Bulge=CurveGeometry.ThroughBulge(new(copy.X1,copy.Y1),new(copy.X2,copy.Y2),_gripPosition);}
            catch(ArgumentException){return wall;}
        return PlanEditing.ValidateWall(copy)==null?copy:wall;
    }
}
