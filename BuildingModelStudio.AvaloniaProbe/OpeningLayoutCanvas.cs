using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using System.Globalization;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class OpeningLayoutCanvas : Control
{
    internal OpeningLayoutEditing State=null!;
    internal readonly HashSet<int> Selected=new();
    internal OpeningModel Opening=null!;
    internal OpeningTypeModel Type=null!;
    internal event Action? Changed,SelectionChanged;
    internal event Action<string>? Error;
    internal double Snap=5;
    internal bool IsReturnFace;
    private double _dragScale;
    private Point _dragPress;
    private double _zoom=1;
    private Point _pan;
    private Point? _panStart;
    private (bool vertical,double coordinate,double along)? _divider;
    private OpeningLayoutEditing? _dragOriginal;
    private double _target;
    private Rect Area=>new(62,62,Math.Max(1,Bounds.Width-104),Math.Max(1,Bounds.Height-104));
    private double Scale=>Math.Min(Area.Width/Math.Max(1,State.Width),Area.Height/Math.Max(1,State.Height))*_zoom;
    private Point Screen(double x,double y)=>new(Area.Center.X+(x-State.Width/2)*Scale+_pan.X,Area.Center.Y-(y-State.Height/2)*Scale+_pan.Y);
    private Point World(Point p)=>new((p.X-Area.Center.X-_pan.X)/Scale+State.Width/2,(Area.Center.Y+_pan.Y-p.Y)/Scale+State.Height/2);
    internal void Notify(bool changed=true){InvalidateVisual();if(changed)Changed?.Invoke();SelectionChanged?.Invoke();}
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#252E38")),new Rect(Bounds.Size));if(State==null)return;
        // Grid spacing is in model units, with a readable density at every zoom level.
        var gridStep=100d;while(gridStep*Scale<16)gridStep*=2;while(gridStep*Scale>48)gridStep/=2;
        var start=World(new Point(0,Bounds.Height));var end=World(new Point(Bounds.Width,0));
        var gridPen=new Pen(new SolidColorBrush(Color.Parse("#303B46")),.6);
        for(var x=Math.Ceiling(start.X/gridStep)*gridStep;x<=end.X;x+=gridStep)context.DrawLine(gridPen,Screen(x,start.Y),Screen(x,end.Y));
        for(var y=Math.Ceiling(start.Y/gridStep)*gridStep;y<=end.Y;y+=gridStep)context.DrawLine(gridPen,Screen(start.X,y),Screen(end.X,y));
        for(var i=0;i<State.Cells.Count;i++) {
            var cell=State.Cells[i];var rect=new Rect(Screen(cell.Left,cell.Top),Screen(cell.Right,cell.Bottom));
            var color=cell.IsDeleted ? "#30343A" : Selected.Contains(i) ? "#71358AB8" : cell.Material=="实板" ? "#AA6C6358" : "#80506979";
            context.FillRectangle(new SolidColorBrush(Color.Parse(color)),rect);
            var text=new FormattedText($"{cell.Right-cell.Left:0.#} × {cell.Top-cell.Bottom:0.#}\n{(cell.IsDeleted ? "已删除" : cell.Opening)} · {cell.Material}",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,new SolidColorBrush(Color.Parse("#C1D1DF")));
            if(rect.Width>text.Width+12 && rect.Height>52)context.DrawText(text,new Point(rect.Center.X-text.Width/2,rect.Center.Y-text.Height/2-16));
        }
        try {
            var type=OpeningConstruction.Copy(Type);type.DivisionPreset="自定义";type.Width=Opening.Width;type.Height=Opening.Height;
            if(IsReturnFace){type.HasInstallationGap=false;type.InstallationGap=0;}
            type.CustomCellLayout=DoorWindowElevationGeometryBuilder.SerializeCellLayout(State.Cells);
            var item=OpeningElevationAdapter.ToScheduleItem(Opening,type,Opening.Width,Opening.Height);
            item.ElevationType=Type.ElevationType=="凸窗" ? "普通窗" : item.ElevationType;
            var geometry=DoorWindowElevationGeometryBuilder.Build(item);var gap=item.HasInstallationGap ? item.InstallationGap : 0;
            foreach(var line in geometry.Lines) {
                var pen=new Pen(new SolidColorBrush(Color.Parse(line.Role==DoorWindowLineRole.Opening ? "#90BFCD" : "#CFDAE4")),line.Role==DoorWindowLineRole.Frame ? 1.5 : .8,
                    line.Role==DoorWindowLineRole.Opening ? DashStyle.Dash : null);
                context.DrawLine(pen,Screen(line.X1-gap,line.Y1-gap),Screen(line.X2-gap,line.Y2-gap));
            }
        }catch { }
        foreach(var i in Selected.Where(i=>i>=0&&i<State.Cells.Count)){var c=State.Cells[i];context.DrawRectangle(null,new Pen(new SolidColorBrush(Color.Parse("#65C7FF")),2),new Rect(Screen(c.Left,c.Top),Screen(c.Right,c.Bottom)));}
        DrawDimensions(context);
        if(_divider is { } d)context.DrawLine(new Pen(Brushes.OrangeRed,2),d.vertical ? Screen(_target,0) : Screen(0,_target),d.vertical ? Screen(_target,State.Height) : Screen(State.Width,_target));
    }
    private void DrawDimensions(DrawingContext context)
    {
        var a=Screen(0,State.Height);var b=Screen(State.Width,0);
        var brush=new SolidColorBrush(Color.Parse("#B4C6D5"));var pen=new Pen(brush,.8);
        void Label(string value,Point at){var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,brush);context.DrawText(text,new Point(at.X-text.Width/2,at.Y-text.Height/2));}
        var y=a.Y-24;context.DrawLine(pen,new Point(a.X,y),new Point(b.X,y));
        foreach(var x in new[]{a.X,b.X}){context.DrawLine(pen,new Point(x,a.Y-5),new Point(x,y-6));context.DrawLine(pen,new Point(x-3,y+3),new Point(x+3,y-3));}
        Label(State.Width.ToString("0.#",CultureInfo.InvariantCulture),new Point((a.X+b.X)/2,y-13));
        var dx=a.X-24;context.DrawLine(pen,new Point(dx,a.Y),new Point(dx,b.Y));
        foreach(var yy in new[]{a.Y,b.Y}){context.DrawLine(pen,new Point(a.X-5,yy),new Point(dx-6,yy));context.DrawLine(pen,new Point(dx-3,yy+3),new Point(dx+3,yy-3));}
        using(context.PushTransform(Matrix.CreateRotation(-Math.PI/2)*Matrix.CreateTranslation(dx-13,(a.Y+b.Y)/2)))Label(State.Height.ToString("0.#",CultureInfo.InvariantCulture),new Point(0,0));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);if(State==null)return;var p=e.GetPosition(this);var buttons=e.GetCurrentPoint(this).Properties;
        if(buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed){_panStart=p;e.Pointer.Capture(this);return;}
        if(!buttons.IsLeftButtonPressed)return;
        var w=World(p);
        var vertical=State.Cells.SelectMany(c=>new[]{c.Left,c.Right}).Where(x=>x>0.05 && x<State.Width-.05).Distinct().OrderBy(x=>Math.Abs(x-w.X)).FirstOrDefault(double.NaN);
        var horizontal=State.Cells.SelectMany(c=>new[]{c.Bottom,c.Top}).Where(y=>y>.05 && y<State.Height-.05).Distinct().OrderBy(y=>Math.Abs(y-w.Y)).FirstOrDefault(double.NaN);
        var vd=double.IsNaN(vertical) ? double.PositiveInfinity : Math.Abs(vertical-w.X);
        var hd=double.IsNaN(horizontal) ? double.PositiveInfinity : Math.Abs(horizontal-w.Y);
        if(!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && Math.Min(vd,hd)*Scale<6) {
            var v=vd<hd;var coordinate=v ? vertical : horizontal;var along=v ? w.Y : w.X;
            var probe=new OpeningLayoutEditing(State.Width,State.Height,State.Cells);
            if(probe.MoveDivider(v,coordinate,coordinate,out _,along)){
                _divider=(v,coordinate,along);_target=coordinate;_dragOriginal=State;_dragScale=Scale;_dragPress=p;
                Cursor=new Cursor(v ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);e.Pointer.Capture(this);return;
            }
        }
        var index=State.Cells.FindIndex(c=>w.X>=c.Left && w.X<=c.Right && w.Y>=c.Bottom && w.Y<=c.Top);
        if(!e.KeyModifiers.HasFlag(KeyModifiers.Shift))Selected.Clear();
        if(index>=0 && !Selected.Add(index))Selected.Remove(index);Notify(false);e.Handled=true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p=e.GetPosition(this);
        if(_panStart is Point previous){_pan+=p-previous;_panStart=p;InvalidateVisual();}
        if(_divider is {} divider && _dragOriginal is {} original){
            var target=divider.coordinate+(divider.vertical ? p.X-_dragPress.X : _dragPress.Y-p.Y)/_dragScale;_target=target;
            var trial=new OpeningLayoutEditing(original.Width,original.Height,original.Cells);
            if(trial.MoveDivider(divider.vertical,divider.coordinate,_target,out _,divider.along,Snap)){State=trial;_target=trial.LastDividerCoordinate;Notify();}
            else InvalidateVisual();
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if(_divider is {} divider && _dragOriginal is {} original){State=original;if(!State.MoveDivider(divider.vertical,divider.coordinate,_target,out var error,divider.along,Snap))Error?.Invoke(error);Notify();}
        _divider=null;_dragOriginal=null;_panStart=null;Cursor=null;e.Pointer.Capture(null);InvalidateVisual();
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if(_dragOriginal is {} original){State=original;_dragOriginal=null;_divider=null;Notify();}
        _panStart=null;Cursor=null;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e){if(_divider==null)_zoom=Math.Clamp(_zoom*Math.Pow(1.15,e.Delta.Y),.2,20);InvalidateVisual();e.Handled=true;}
}
