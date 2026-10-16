using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadRegisteredPlanPreview : FrameworkElement
    {
        private CadUnderlayIndex _index;
        private CadFloorPlanCapture _capture;
        private double _zoom=1,_scale=1,_midX,_midY;
        private Vector _offset;
        private Point? _pan;
        private readonly List<Tuple<StreamGeometry,bool>> _paths=new List<Tuple<StreamGeometry,bool>>();
        private StreamGeometry _region;
        private Rect _bounds;
        private static readonly Brush Background=new SolidColorBrush(Color.FromRgb(19,24,30));
        public int PrimitiveCount => _index?.Primitives.Count??0;
        public CadRegisteredPlanPreview(){ClipToBounds=true;Focusable=true;Cursor=Cursors.Hand;}
        public void SetCapture(CadFloorPlanCapture capture)
        {
            if(ReferenceEquals(_capture,capture))return;
            _capture=capture;_index=capture?.Underlay==null?null:new CadUnderlayIndex(capture);
            _paths.Clear();_region=null;_bounds=Rect.Empty;
            if(_index!=null)
            {
                foreach(var primitive in _index.Primitives)
                {
                    var path=Path(Samples(primitive),false);_bounds.Union(path.Bounds);
                    var dashed=(primitive.SourceLineType??"").IndexOf("DASH",StringComparison.OrdinalIgnoreCase)>=0||(primitive.SourceLineType??"").IndexOf("HIDDEN",StringComparison.OrdinalIgnoreCase)>=0;
                    _paths.Add(Tuple.Create(path,dashed));
                }
                foreach(var text in _index.Texts)_bounds.Union(new Point(text.X,text.Y));
                if(_index.Region.Count>1){_region=Path(_index.Region.Select(p=>new Point(p.X,p.Y)).ToArray(),true);_bounds=_region.Bounds;}
            }
            Fit();
        }
        public void Fit(){_zoom=1;_offset=new Vector();InvalidateVisual();}
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Background,null,new Rect(RenderSize));
            if(_index==null||_bounds.IsEmpty)
            {
                var text=new FormattedText(_capture==null?"尚未登记平面":"旧登记暂无底图，可更新参照底图",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"),15,Brushes.LightSlateGray,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text,new Point(20,Math.Max(20,ActualHeight/2-text.Height/2)));return;
            }
            _midX=_bounds.Left+_bounds.Width/2;_midY=_bounds.Top+_bounds.Height/2;
            _scale=Math.Max(.000001,Math.Min(Math.Max(1,ActualWidth-72)/Math.Max(1,_bounds.Width),Math.Max(1,ActualHeight-72)/Math.Max(1,_bounds.Height)))*_zoom;
            var pen=new Pen(new SolidColorBrush(Color.FromRgb(182,198,211)),1/_scale);
            var dashedPen=new Pen(pen.Brush,1/_scale){DashStyle=DashStyles.Dash};
            var origin=Map(new Point(0,0));
            dc.PushTransform(new MatrixTransform(_scale,0,0,-_scale,origin.X,origin.Y));
            var visible=new Rect((0-origin.X)/_scale,(origin.Y-ActualHeight)/_scale,ActualWidth/_scale,ActualHeight/_scale);
            foreach(var path in _paths)
            {
                if(visible.IntersectsWith(path.Item1.Bounds))dc.DrawGeometry(null,path.Item2?dashedPen:pen,path.Item1);
            }
            if(_region!=null)dc.DrawGeometry(null,new Pen(Brushes.DodgerBlue,1/_scale){DashStyle=DashStyles.Dash},_region);
            dc.Pop();
            foreach(var item in _index.Texts)
            {
                var height=item.Height*_scale;if(height<5||height>80)continue;
                var text=new FormattedText(item.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),Math.Max(5,height),Brushes.SlateGray,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var point=Map(new Point(item.X,item.Y));dc.PushTransform(new RotateTransform(-item.RotationDegrees,point.X,point.Y));dc.DrawText(text,point);dc.Pop();
            }
            var datum=_capture.Floor.Alignment.ModelBase;var at=Map(new Point(datum.X,datum.Y));var cross=new Pen(Brushes.Turquoise,1.5);
            dc.DrawLine(cross,at-new Vector(12,0),at+new Vector(12,0));dc.DrawLine(cross,at-new Vector(0,12),at+new Vector(0,12));
        }
        private Point Map(Point point)=>new Point(ActualWidth/2+(point.X-_midX)*_scale+_offset.X,ActualHeight/2-(point.Y-_midY)*_scale+_offset.Y);
        private static StreamGeometry Path(Point[] points,bool closed)
        {
            var path=new StreamGeometry();using(var drawing=path.Open()){drawing.BeginFigure(points[0],false,closed);drawing.PolyLineTo(points.Skip(1).ToArray(),true,false);}path.Freeze();return path;
        }
        private static Point[] Samples(ComponentPlanPrimitive p)
        {
            if(p.Kind=="Line")return new[]{new Point(p.X1,p.Y1),new Point(p.X2,p.Y2)};
            var sweep=p.Kind=="Circle"?360:p.SweepDegrees;var count=Math.Max(8,(int)Math.Ceiling(Math.Abs(sweep)/3));
            return Enumerable.Range(0,count+1).Select(i=>{var angle=(p.StartDegrees+sweep*i/count)*Math.PI/180;return new Point(p.X1+p.Radius*Math.Cos(angle),p.Y1+p.Radius*Math.Sin(angle));}).ToArray();
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var next=Math.Max(.2,Math.Min(30,_zoom*Math.Pow(1.15,e.Delta/120d)));var ratio=next/_zoom;
            var relative=e.GetPosition(this)-new Point(ActualWidth/2,ActualHeight/2);_offset=relative-(relative-_offset)*ratio;_zoom=next;InvalidateVisual();e.Handled=true;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){Focus();_pan=e.GetPosition(this);CaptureMouse();e.Handled=true;}
        protected override void OnMouseMove(MouseEventArgs e){if(!_pan.HasValue)return;var point=e.GetPosition(this);_offset+=point-_pan.Value;_pan=point;InvalidateVisual();e.Handled=true;}
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){_pan=null;ReleaseMouseCapture();e.Handled=true;}
        protected override void OnLostMouseCapture(MouseEventArgs e){_pan=null;base.OnLostMouseCapture(e);}
    }
}
