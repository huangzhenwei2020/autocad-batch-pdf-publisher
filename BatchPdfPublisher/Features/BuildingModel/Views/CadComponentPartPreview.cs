using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;

namespace BatchPdfPublisher.Views
{
    public sealed class CadComponentPartPreview : FrameworkElement
    {
        public ComponentPlanSymbol Symbol;
        public IList<int> Highlight = new List<int>();
        public IList<int> OnlyIndices;
        public IList<ComponentPlanPart> PartGroups;
        public PointModel BasePoint;
        public Action<PointModel> BasePointRequested;
        public bool ShowEmptyText = true;
        public Action<IList<int>, bool> AssignRequested;
        private Point? _start;
        private Point _end;
        private double _scale = 1, _midX, _midY, _zoom = 1;
        private Vector _offset;
        private ComponentPlanSymbol _lastSymbol;
        public CadComponentPartPreview() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross; }
        protected override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 26, 36)), new Pen(new SolidColorBrush(Color.FromRgb(52, 76, 96)), 1), new Rect(RenderSize));
            var primitives = Symbol?.Primitives;
            if (primitives == null || primitives.Count == 0) { if (ShowEmptyText) Label(context, "选择整樘平面后，在这里标注部件"); return; }
            if (!ReferenceEquals(_lastSymbol, Symbol)) { _zoom = 1; _offset = new Vector(); _lastSymbol = Symbol; }
            var indices = OnlyIndices ?? Enumerable.Range(0, primitives.Count).ToArray();
            if (indices.Count == 0) return;
            var points = indices.SelectMany(i => Samples(primitives[i])).ToArray();
            var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X); var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
            _midX = (minX + maxX) / 2; _midY = (minY + maxY) / 2;
            var padding = ShowEmptyText ? 48 : 10;
            _scale = Math.Max(.000001, Math.Min(Math.Max(1, ActualWidth - padding) / Math.Max(1, maxX - minX), Math.Max(1, ActualHeight - padding) / Math.Max(1, maxY - minY))) * _zoom;
            var active = new Pen(new SolidColorBrush(Color.FromRgb(70, 192, 249)), ShowEmptyText ? 3 : 1.5);
            var activeIndices = new HashSet<int>(Highlight);
            foreach (var index in indices.OrderBy(i => activeIndices.Contains(i) ? 1 : 0)) {
                var p = primitives[index];
                var role=ComponentPlanSymbols.PrimitiveRole(Symbol,index,PartGroups);
                var color=ComponentPlanSymbols.RoleColor(role);
                var brush=new SolidColorBrush(color==30?Color.FromRgb(255,155,30):color==1?Color.FromRgb(255,90,75):color==2?Color.FromRgb(240,217,55):
                    color==4?Color.FromRgb(80,200,210):color==8?Color.FromRgb(155,165,175):color==9?Color.FromRgb(195,205,215):Color.FromRgb(122,150,170));
                var pen=activeIndices.Contains(index)?active:new Pen(brush,ShowEmptyText?1.4:1);
                if(role=="OpeningSymbol")pen=new Pen(pen.Brush,pen.Thickness) {DashStyle=new DashStyle(new[]{8d,5d},0)};
                if (p.Kind == "Line") context.DrawLine(pen, Map(new Point(p.X1, p.Y1)), Map(new Point(p.X2, p.Y2)));
                else if (p.Kind == "Circle") context.DrawEllipse(null, pen, Map(new Point(p.X1, p.Y1)), p.Radius * _scale, p.Radius * _scale);
                else {
                    var samples=Samples(p);var path=new StreamGeometry();
                    using(var draw=path.Open()){draw.BeginFigure(Map(samples[0]),false,false);draw.PolyLineTo(samples.Skip(1).Select(Map).ToArray(),true,false);}
                    context.DrawGeometry(null,pen,path);
                }
            }
            if(BasePoint!=null) {
                var at=Map(new Point(BasePoint.X,BasePoint.Y));var marker=new Pen(Brushes.LimeGreen,2);
                context.DrawEllipse(null,marker,at,5,5);context.DrawLine(marker,at-new Vector(11,0),at+new Vector(11,0));context.DrawLine(marker,at-new Vector(0,11),at+new Vector(0,11));
            }
            if (_start.HasValue) context.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 70, 192, 249)), active, new Rect(_start.Value, _end));
        }
        private void Label(DrawingContext context, string text)
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 13, new SolidColorBrush(Color.FromRgb(149, 170, 189)), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - 32) };
            context.DrawText(formatted, new Point(16, Math.Max(16, (ActualHeight - formatted.Height) / 2)));
        }
        public Point Map(Point point) => new Point(ActualWidth / 2 + (point.X - _midX) * _scale + _offset.X, ActualHeight / 2 - (point.Y - _midY) * _scale + _offset.Y);
        private PointModel Unmap(Point p) => new PointModel((p.X - ActualWidth / 2 - _offset.X) / _scale + _midX, -(p.Y - ActualHeight / 2 - _offset.Y) / _scale + _midY);
        private static Point[] Samples(ComponentPlanPrimitive p)
        {
            if (p.Kind == "Line") return new[] { new Point(p.X1, p.Y1), new Point(p.X2, p.Y2) };
            var sweep = p.Kind == "Circle" ? 360 : p.SweepDegrees; var count = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / 3));
            return Enumerable.Range(0, count + 1).Select(i => { var a = (p.StartDegrees + sweep * i / count) * Math.PI / 180; return new Point(p.X1 + p.Radius * Math.Cos(a), p.Y1 + p.Radius * Math.Sin(a)); }).ToArray();
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e); if (Symbol == null) return;
            if(BasePointRequested!=null) {
                var mouse=e.GetPosition(this);var point=Unmap(mouse);
                var vertices=Symbol.Primitives.SelectMany(p=>{var samples=Samples(p);return new[]{samples[0],samples[samples.Length-1]};}).OrderBy(p=>(Map(p)-mouse).Length).ToArray();
                if(vertices.Length>0&&(Map(vertices[0])-mouse).Length<=12)point=new PointModel(vertices[0].X,vertices[0].Y);
                BasePointRequested(point);InvalidateVisual();e.Handled=true;return;
            }
            if(AssignRequested==null)return; Focus(); _start = _end = e.GetPosition(this); CaptureMouse(); e.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!_start.HasValue) return; _end = e.GetPosition(this); InvalidateVisual(); e.Handled = true; }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e); if (!_start.HasValue) return; var start = _start.Value; var end = e.GetPosition(this); _start = null; ReleaseMouseCapture();
            if ((end - start).Length < 5) { var index = ComponentPlanSymbols.Pick(Symbol, Unmap(end), 8 / _scale); if (index >= 0) AssignRequested?.Invoke(new[] { index }, Highlight.Contains(index)); }
            else { var rect = new Rect(start, end); var indices = Enumerable.Range(0, Symbol.Primitives.Count).Where(i => Touches(Symbol.Primitives[i], rect)).ToArray(); if (indices.Length > 0) AssignRequested?.Invoke(indices, false); }
            InvalidateVisual(); e.Handled = true;
        }
        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e); if (Symbol == null || AssignRequested == null) return; var index = ComponentPlanSymbols.Pick(Symbol, Unmap(e.GetPosition(this)), 8 / _scale); if (index >= 0) AssignRequested(new[] { index }, true); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _start = null; InvalidateVisual(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e); if (Symbol == null || OnlyIndices != null) return; var next = Math.Max(.25, Math.Min(20, _zoom * Math.Pow(1.15, e.Delta / 120d))); var ratio = next / _zoom;
            var relative = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2); _offset = relative - (relative - _offset) * ratio; _zoom = next; InvalidateVisual(); e.Handled = true;
        }
        private bool Touches(ComponentPlanPrimitive p, Rect rect)
        {
            var points = Samples(p).Select(Map).ToArray();
            for (var i = 1; i < points.Length; i++) {
                var a = points[i - 1]; var b = points[i]; var dx = b.X - a.X; var dy = b.Y - a.Y; var lo = 0d; var hi = 1d;
                bool Clip(double direction, double distance) { if (Math.Abs(direction) < .000001) return distance >= 0; var t = distance / direction; if (direction < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t); return lo <= hi; }
                if (Clip(-dx, a.X - rect.Left) && Clip(dx, rect.Right - a.X) && Clip(-dy, a.Y - rect.Top) && Clip(dy, rect.Bottom - a.Y)) return true;
            }
            return false;
        }
    }
}
