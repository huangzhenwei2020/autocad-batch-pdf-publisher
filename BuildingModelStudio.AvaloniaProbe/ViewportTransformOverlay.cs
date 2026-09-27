using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum ViewTransformTool { Select, Move, Rotate }

/// <summary>Screen-space wall handles. Drag previews never rebuild the 3D mesh.</summary>
internal sealed class ViewportTransformOverlay : Control
{
    private readonly ModelViewport _viewport;
    private WallModel? _wall;
    private double _elevation;
    private PointModel? _pressWorld;
    private string? _handle;
    private bool _copy;
    private double _dx, _dy, _angle;
    public ViewTransformTool Tool { get; private set; }
    public bool IsDragging => _handle != null;
    public event Action<string, double, double, double, bool>? TransformFinished;
    public event Action<string>? PreviewChanged;

    public ViewportTransformOverlay(ModelViewport viewport)
    {
        _viewport = viewport;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public void SetSelection(WallModel? wall, double elevation)
    {
        Cancel();
        _wall = wall;
        _elevation = elevation;
        InvalidateVisual();
    }

    public void SetTool(ViewTransformTool tool)
    {
        Cancel();
        Tool = tool;
        InvalidateVisual();
    }

    public void Cancel()
    {
        _handle = null;
        _pressWorld = null;
        _dx = _dy = _angle = 0;
        InvalidateVisual();
    }

    private Point? Center()
        => _wall == null ? null : _viewport.ProjectModelPoint(
            (_wall.X1 + _wall.X2) / 2, (_wall.Y1 + _wall.Y2) / 2, _elevation);

    private Point? AxisEnd(Point center, bool xAxis)
    {
        if (_wall == null) return null;
        var projected = _viewport.ProjectModelPoint((_wall.X1 + _wall.X2) / 2 + (xAxis ? 1000 : 0),
            (_wall.Y1 + _wall.Y2) / 2 + (xAxis ? 0 : 1000), _elevation);
        if (projected == null) return null;
        var vx = projected.Value.X - center.X;
        var vy = projected.Value.Y - center.Y;
        var length = Math.Sqrt(vx * vx + vy * vy);
        return length < 1 ? null : new Point(center.X + vx / length * 68, center.Y + vy / length * 68);
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double SegmentDistance(Point p, Point a, Point b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / Math.Max(1, dx * dx + dy * dy), 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    public bool TryBegin(Point point, bool copy)
    {
        if (_wall == null || Tool == ViewTransformTool.Select) return false;
        var center = Center();
        if (center == null || !_viewport.TryScreenToPlan(point, _elevation, out var world)) return false;
        string? handle = null;
        if (Tool == ViewTransformTool.Move)
        {
            if (Distance(point, center.Value) <= 15) handle = "xy";
            else
            {
                var x = AxisEnd(center.Value, true);
                var y = AxisEnd(center.Value, false);
                if (x != null && SegmentDistance(point, center.Value, x.Value) <= 10) handle = "x";
                else if (y != null && SegmentDistance(point, center.Value, y.Value) <= 10) handle = "y";
            }
        }
        else if (Math.Abs(Distance(point, center.Value) - 49) <= 11) handle = "rotate";
        if (handle == null) return false;
        _handle = handle;
        _pressWorld = world;
        _copy = copy;
        _dx = _dy = _angle = 0;
        InvalidateVisual();
        return true;
    }

    public void Move(Point point)
    {
        if (_wall == null || _handle == null || _pressWorld == null
            || !_viewport.TryScreenToPlan(point, _elevation, out var world)) return;
        if (_handle == "rotate")
        {
            var cx = (_wall.X1 + _wall.X2) / 2;
            var cy = (_wall.Y1 + _wall.Y2) / 2;
            var before = Math.Atan2(_pressWorld.Y - cy, _pressWorld.X - cx);
            var after = Math.Atan2(world.Y - cy, world.X - cx);
            _angle = (after - before) * 180 / Math.PI;
            if (_angle > 180) _angle -= 360;
            if (_angle < -180) _angle += 360;
        }
        else
        {
            _dx = _handle == "y" ? 0 : world.X - _pressWorld.X;
            _dy = _handle == "x" ? 0 : world.Y - _pressWorld.Y;
        }
        PreviewChanged?.Invoke(_handle == "rotate" ? $"旋转 {_angle:0.0}°" : $"移动 X {_dx:0} / Y {_dy:0} mm");
        InvalidateVisual();
    }

    public void Finish()
    {
        if (_wall == null || _handle == null) return;
        var id = _wall.Id;
        var dx = _dx; var dy = _dy; var angle = _angle; var copy = _copy;
        Cancel();
        if (Math.Abs(dx) + Math.Abs(dy) >= 0.01 || Math.Abs(angle) >= 0.01)
            TransformFinished?.Invoke(id, dx, dy, angle, copy);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_wall == null || Tool == ViewTransformTool.Select) return;
        var center = Center();
        if (center == null) return;
        if (_handle != null)
        {
            var angle = _angle * Math.PI / 180;
            var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
            var cx = (_wall.X1 + _wall.X2) / 2 + _dx;
            var cy = (_wall.Y1 + _wall.Y2) / 2 + _dy;
            var hx = (_wall.X2 - _wall.X1) / 2;
            var hy = (_wall.Y2 - _wall.Y1) / 2;
            var rx = hx * cosine - hy * sine;
            var ry = hx * sine + hy * cosine;
            var first = _viewport.ProjectModelPoint(cx - rx, cy - ry, _elevation);
            var second = _viewport.ProjectModelPoint(cx + rx, cy + ry, _elevation);
            if (first != null && second != null)
                context.DrawLine(new Pen(Brushes.White, 3, dashStyle: new DashStyle(new[] { 5d, 4d }, 0)),
                    first.Value, second.Value);
        }
        if (Tool == ViewTransformTool.Rotate)
        {
            context.DrawEllipse(null, new Pen(Brushes.Orange, 3), center.Value, 49, 49);
            context.DrawEllipse(Brushes.Orange, null, center.Value, 5, 5);
        }
        else
        {
            var x = AxisEnd(center.Value, true);
            var y = AxisEnd(center.Value, false);
            if (x != null) context.DrawLine(new Pen(Brushes.IndianRed, 4), center.Value, x.Value);
            if (y != null) context.DrawLine(new Pen(Brushes.LightGreen, 4), center.Value, y.Value);
            context.FillRectangle(Brushes.White, new Rect(center.Value.X - 6, center.Value.Y - 6, 12, 12));
            if (x != null) context.DrawEllipse(Brushes.IndianRed, null, x.Value, 6, 6);
            if (y != null) context.DrawEllipse(Brushes.LightGreen, null, y.Value, 6, 6);
        }
    }

}
