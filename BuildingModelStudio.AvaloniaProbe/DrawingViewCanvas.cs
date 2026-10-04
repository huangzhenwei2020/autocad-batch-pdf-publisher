using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class DrawingViewCanvas : Control
{
    private ViewDocument? _view;
    private readonly List<ViewLine> _lines = new();
    private readonly List<ViewText> _texts = new();
    private readonly List<DimensionLabel> _dimensionLabels = new();
    private sealed record DimensionLabel(string Text, Point Center, double FontHeight, double Width, double Height, bool Vertical,double WidthFactor)
    {
        public Rect Bounds => new(Center.X-(Vertical ? Height : Width)/2,Center.Y-(Vertical ? Width : Height)/2,
            Vertical ? Height : Width,Vertical ? Width : Height);
    }
    private double _scale = .02, _centerX, _centerY;
    private bool _fitPending = true;
    private Point? _pan;
    public ViewDocument? View => _view;

    public DrawingViewCanvas()
    {
        ClipToBounds = true; Focusable = true;
        PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
            _pan = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true;
        };
        PointerMoved += (_, e) => {
            if (_pan is not Point old) return;
            var p=e.GetPosition(this); _centerX-=(p.X-old.X)/_scale; _centerY+=(p.Y-old.Y)/_scale;
            _pan=p;InvalidateVisual();
        };
        PointerReleased += (_,e)=> { _pan=null;e.Pointer.Capture(null); };
        PointerCaptureLost += (_,_)=>_pan=null;
        PointerWheelChanged += (_,e)=> {
            ZoomAt(e.GetPosition(this),e.Delta.Y);e.Handled=true;
        };
    }

    internal void ZoomAt(Point p,double delta)
    {
        var x=_centerX+(p.X-Bounds.Width/2)/_scale;var y=_centerY-(p.Y-Bounds.Height/2)/_scale;
        _scale=Math.Clamp(_scale*Math.Pow(1.18,delta),.00001,10);
        _centerX=x-(p.X-Bounds.Width/2)/_scale;_centerY=y+(p.Y-Bounds.Height/2)/_scale;
        _fitPending=false;InvalidateVisual();
    }
    internal double PixelsPerMillimetre => _scale;
    internal Point ModelToScreen(PointModel p) => Screen(p.X,p.Y);
    internal IReadOnlyList<Rect> DimensionTextBounds => _dimensionLabels.Select(l=>l.Bounds).ToArray();

    public void SetView(ViewDocument? view)
    {
        _view=view;_lines.Clear();_texts.Clear();_dimensionLabels.Clear();
        if(view!=null) {
            _lines.AddRange(view.Lines);_texts.AddRange(view.Texts);
            SheetComposer.LayoutDimensionText(view);
            foreach(var d in view.Dimensions) {
                if(Math.Abs(d.To-d.From)<.5)continue;
                var from=d.Vertical ? new PointModel(d.LinePosition,d.From) : new PointModel(d.From,d.LinePosition);
                var to=d.Vertical ? new PointModel(d.LinePosition,d.To) : new PointModel(d.To,d.LinePosition);
                var a=d.Vertical ? new PointModel(d.AnchorPosition,d.From) : new PointModel(d.From,d.AnchorPosition);
                var b=d.Vertical ? new PointModel(d.AnchorPosition,d.To) : new PointModel(d.To,d.AnchorPosition);
                Line(from,to,d.Layer);Line(a,from,d.Layer);Line(b,to,d.Layer);
                var tick=Math.Max(1,view.Scale)*1.2;
                foreach(var p in new[] {from,to})Line(new PointModel(p.X-tick,p.Y-tick),new PointModel(p.X+tick,p.Y+tick),d.Layer);
                var text=DrawingAnnotationSettings.DimensionText(d);
                var fontHeight=d.TextHeight;
                var size=TextSize(text,fontHeight);
                var mid=(d.From+d.To)/2;
                var factor=Math.Min(d.TextWidthFactor,Math.Abs(d.To-d.From)*.9/Math.Max(1,size.Width));
                var label=new DimensionLabel(text,new Point(d.TextX??mid,d.TextY??mid),fontHeight,size.Width*factor,size.Height,d.Vertical,factor);
                _dimensionLabels.Add(label);
            }
        }
        Fit();
    }

    private void Line(PointModel a,PointModel b,string layer)=>_lines.Add(new ViewLine { Layer=layer,X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y });
    private static Size TextSize(string text,double height)
    {
        var measured=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,100,Brushes.Black);
        return new Size(measured.Width*height/100,measured.Height*height/100);
    }
    private Point Screen(double x,double y)=>new(Bounds.Width/2+(x-_centerX)*_scale,Bounds.Height/2-(y-_centerY)*_scale);

    public void Fit()
    {
        if(Bounds.Width<20 || Bounds.Height<20) { _fitPending=true;return; }
        var points=_lines.SelectMany(l=>new[] {new PointModel(l.X1,l.Y1),new PointModel(l.X2,l.Y2)})
            .Concat(_texts.SelectMany(t=>new[] {new PointModel(t.X,t.Y),new PointModel(t.X+(t.Text?.Length ?? 0)*t.Height,t.Y+t.Height)}))
            .Concat((_view?.Circles ?? new()).SelectMany(c=>new[] {new PointModel(c.X-c.Radius,c.Y-c.Radius),new PointModel(c.X+c.Radius,c.Y+c.Radius)}))
            .Concat((_view?.Hatches ?? new()).SelectMany(h=>h.Boundary)).ToArray();
        points=points.Concat(_dimensionLabels.SelectMany(l=>new[] {
            new PointModel(l.Bounds.Left,l.Bounds.Top),new PointModel(l.Bounds.Right,l.Bounds.Bottom) })).ToArray();
        if(points.Length>0) {
            var x0=points.Min(p=>p.X);var x1=points.Max(p=>p.X);var y0=points.Min(p=>p.Y);var y1=points.Max(p=>p.Y);
            _centerX=(x0+x1)/2;_centerY=(y0+y1)/2;
            _scale=Math.Clamp(Math.Min((Bounds.Width-60)/Math.Max(100,x1-x0),(Bounds.Height-60)/Math.Max(100,y1-y0)),.00001,10);
        }
        _fitPending=false;InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result=base.ArrangeOverride(finalSize);if(_fitPending)Fit();return result;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#FAFCFF")),new Rect(Bounds.Size));
        if(_view==null) {
            context.DrawText(new FormattedText("从图纸目录选择一张图。",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                Typeface.Default,16,Brushes.Gray),new Point(24,24));return;
        }
        foreach(var hatch in _view.Hatches) {
            if(hatch.Boundary.Count<3)continue;
            var geometry=new StreamGeometry();using(var path=geometry.Open()) {
                path.BeginFigure(Screen(hatch.Boundary[0].X,hatch.Boundary[0].Y),true);
                foreach(var p in hatch.Boundary.Skip(1))path.LineTo(Screen(p.X,p.Y));path.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse("#DDE6EE")),Pen(hatch.Layer,Brushes.SlateGray),geometry);
            if (string.Equals(hatch.Pattern,"SOLID",StringComparison.OrdinalIgnoreCase)) continue;
            var angle=hatch.Angle*Math.PI/180;var tx=Math.Cos(angle);var ty=Math.Sin(angle);var nx=-ty;var ny=tx;
            var ns=hatch.Boundary.Select(p=>p.X*nx+p.Y*ny).ToArray();var ts=hatch.Boundary.Select(p=>p.X*tx+p.Y*ty).ToArray();
            var spacing=hatch.Spacing>0 ? hatch.Spacing : 2.5*Math.Max(1,hatch.Scale)*Math.Max(1,_view.Scale);
            using(context.PushGeometryClip(geometry)) {
                var pen=Pen(hatch.Layer,Brushes.SlateGray);
                for(var n=Math.Floor(ns.Min()/spacing)*spacing;n<=ns.Max();n+=spacing)
                    context.DrawLine(pen,Screen(ts.Min()*tx+n*nx,ts.Min()*ty+n*ny),Screen(ts.Max()*tx+n*nx,ts.Max()*ty+n*ny));
            }
        }
        foreach(var line in _lines) {
            var axis=line.Layer==ViewLayers.Axis;
            var hidden=axis || line.LineType=="HIDDEN";
            var pen=Pen(line.Layer,axis ? Brushes.SlateGray : new SolidColorBrush(Color.Parse(line.Layer==ViewLayers.Opening ? "#275D80" : "#253746")),hidden);
            context.DrawLine(pen,Screen(line.X1,line.Y1),Screen(line.X2,line.Y2));
        }
        foreach(var circle in _view.Circles)context.DrawEllipse(null,Pen(circle.Layer,Brushes.SlateGray),Screen(circle.X,circle.Y),circle.Radius*_scale,circle.Radius*_scale);
        foreach(var text in _texts) {
            if(text.Height<=0)continue;
            var font=text.Height*_scale;
            var formatted=new FormattedText(text.Text ?? "",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,font,Brushes.Black);
            var p=Screen(text.X,text.Y);
            using(context.PushTransform(Matrix.CreateScale(text.WidthFactor>0?text.WidthFactor:1,1)*Matrix.CreateRotation(-text.Rotation*Math.PI/180)*Matrix.CreateTranslation(p.X,p.Y)))
                context.DrawText(formatted,new Point(0,-formatted.Height));
        }
        foreach(var label in _dimensionLabels) {
            var text=new FormattedText(label.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,label.FontHeight*_scale,Brushes.Black);
            var center=Screen(label.Center.X,label.Center.Y);
            using(context.PushTransform(Matrix.CreateScale(label.WidthFactor,1)*Matrix.CreateRotation(label.Vertical ? -Math.PI/2 : 0)*Matrix.CreateTranslation(center.X,center.Y))) {
                var pad=label.FontHeight*.1*_scale;
                context.FillRectangle(new SolidColorBrush(Color.Parse("#FAFCFF")),new Rect(-text.Width/2-pad,-text.Height/2-pad,text.Width+2*pad,text.Height+2*pad));
                context.DrawText(text,new Point(-text.Width/2,-text.Height/2));
            }
        }
    }

    private Pen Pen(string layer,IBrush brush,bool dashed=false)
    {
        var weight=ViewLayers.Find(layer)?.LineWeight ?? 13;
        return new Pen(brush,Math.Max(1,weight)/100d*Math.Max(1,_view?.Scale ?? 100)*_scale,
            dashed ? new DashStyle(new[] { 50d,25d },0) : null);
    }
}
