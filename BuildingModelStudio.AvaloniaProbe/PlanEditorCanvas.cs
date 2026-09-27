using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum PlanTool { Select, Wall, Door, Window }

internal sealed class PlanEditorCanvas : Control
{
    private BuildingModelDocument _model = SampleModelFactory.CreateEmptyModel("空模型");
    private string _storeyId = "1F";
    private string? _selectedId;
    private PointModel? _wallStart;
    private PointModel? _cursor;
    private string _snapKind = PlanEditing.SnapNone;
    private Point? _panStart;
    private string? _gripWallId;
    private int _gripIndex;
    private PointModel? _gripPosition;
    private Point? _gripPress;
    private bool _gripMoved;
    private double _scale = 0.07;
    private double _centerX;
    private double _centerY;
    private bool _fitted;
    public PlanTool Tool { get; set; }
    public event Func<PointModel, PointModel, bool>? WallRequested;
    public event Action<string, string, double>? OpeningRequested;
    public event Action<string?>? ElementPicked;
    public event Action<string, int, PointModel>? WallGripReleased;
    public event Action<string>? SnapChanged;

    public PlanEditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void SetModel(BuildingModelDocument model, string storeyId)
    {
        _model = model;
        _storeyId = storeyId;
        if (!_fitted) Fit();
        InvalidateVisual();
    }

    public void SetStorey(string id)
    {
        _storeyId = id;
        _wallStart = null;
        Fit();
    }

    public void SetSelection(string? id)
    {
        _selectedId = id;
        InvalidateVisual();
    }

    public void CancelDraft()
    {
        _wallStart = null;
        _cursor = null;
        _snapKind = PlanEditing.SnapNone;
        _gripWallId = null;
        _gripPosition = null;
        _gripPress = null;
        _gripMoved = false;
        InvalidateVisual();
    }

    public bool TryDrawWallLength(double length, out string error)
    {
        error = "请先在画布上指定墙的起点。";
        if (Tool != PlanTool.Wall || _wallStart == null) return false;
        if (double.IsNaN(length) || double.IsInfinity(length) || length < 10)
        { error = "墙长必须是至少 10 mm 的有限数值。"; return false; }
        var dx = (_cursor?.X ?? _wallStart.X + 1) - _wallStart.X;
        var dy = (_cursor?.Y ?? _wallStart.Y) - _wallStart.Y;
        var direction = Math.Sqrt(dx * dx + dy * dy);
        if (direction < 1e-6) { dx = 1; dy = 0; direction = 1; }
        var end = new PointModel(_wallStart.X + dx / direction * length,
            _wallStart.Y + dy / direction * length);
        if (WallRequested?.Invoke(_wallStart, end) != true)
        { error = "未能创建该墙。"; return false; }
        _wallStart = end;
        InvalidateVisual();
        error = "";
        return true;
    }

    public void Fit() => FitToSize(Bounds.Size);

    private void FitToSize(Size size)
    {
        if (size.Width <= 50 || size.Height <= 50)
        {
            _fitted = false;
            return;
        }
        var walls = _model.Walls.Where(w => w.StoreyId == _storeyId).ToArray();
        if (walls.Length == 0) { _centerX = _centerY = 0; _scale = 0.07; }
        else
        {
            var minX = walls.Min(w => Math.Min(w.X1, w.X2));
            var maxX = walls.Max(w => Math.Max(w.X1, w.X2));
            var minY = walls.Min(w => Math.Min(w.Y1, w.Y2));
            var maxY = walls.Max(w => Math.Max(w.Y1, w.Y2));
            _centerX = (minX + maxX) / 2;
            _centerY = (minY + maxY) / 2;
            _scale = Math.Clamp(Math.Min((size.Width - 80) / Math.Max(1000, maxX - minX),
                (size.Height - 80) / Math.Max(1000, maxY - minY)), 0.01, 0.5);
        }
        _fitted = true;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (!_fitted && finalSize.Width > 50 && finalSize.Height > 50) FitToSize(finalSize);
        return result;
    }

    private Point Screen(double x, double y)
        => new(Bounds.Width / 2 + (x - _centerX) * _scale,
            Bounds.Height / 2 - (y - _centerY) * _scale);

    private PointModel World(Point p)
        => new(_centerX + (p.X - Bounds.Width / 2) / _scale,
            _centerY - (p.Y - Bounds.Height / 2) / _scale);

    private Point PreviewWallEndpoint(WallModel wall, int index)
    {
        var x = index == 0 ? wall.X1 : wall.X2;
        var y = index == 0 ? wall.Y1 : wall.Y2;
        if (_gripWallId != null && _gripPosition != null)
        {
            var selected = _model.Walls.FirstOrDefault(w => w.Id == _gripWallId);
            if (selected != null && wall.StoreyId == selected.StoreyId)
            {
                var anchorX = _gripIndex == 0 ? selected.X1 : selected.X2;
                var anchorY = _gripIndex == 0 ? selected.Y1 : selected.Y2;
                if ((wall.Id == selected.Id && index == _gripIndex)
                    || (wall.Id != selected.Id && Math.Sqrt(Math.Pow(x - anchorX, 2)
                        + Math.Pow(y - anchorY, 2)) <= 0.5d))
                    return Screen(_gripPosition.X, _gripPosition.Y);
                if (wall.Id != selected.Id && PlanEditing.TryProjectWallInterior(selected,
                    x, y, 0.5d, out var fraction))
                {
                    var startX = _gripIndex == 0 ? _gripPosition.X : selected.X1;
                    var startY = _gripIndex == 0 ? _gripPosition.Y : selected.Y1;
                    var endX = _gripIndex == 1 ? _gripPosition.X : selected.X2;
                    var endY = _gripIndex == 1 ? _gripPosition.Y : selected.Y2;
                    return Screen(startX + (endX - startX) * fraction,
                        startY + (endY - startY) * fraction);
                }
            }
        }
        return Screen(x, y);
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private PointModel Snap(Point p, string? excludedWallId = null)
    {
        var world = World(p);
        var from = Tool == PlanTool.Wall ? _wallStart : null;
        var snapped = PlanEditing.Snap(_model, _storeyId, world.X, world.Y, 10d / _scale,
            from != null, from?.X ?? 0, from?.Y ?? 0, excludedWallId);
        if (snapped.Kind != _snapKind && (Tool == PlanTool.Wall || _gripWallId != null))
            SnapChanged?.Invoke(snapped.Kind);
        _snapKind = snapped.Kind;
        return new PointModel(snapped.X, snapped.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed)
        {
            _panStart = point;
            e.Pointer.Capture(this);
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        if (Tool == PlanTool.Select && _selectedId != null)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == _selectedId && w.StoreyId == _storeyId);
            if (wall != null)
            {
                var first = Screen(wall.X1, wall.Y1);
                var second = Screen(wall.X2, wall.Y2);
                if (Distance(point, first) <= 12 || Distance(point, second) <= 12)
                {
                    _gripWallId = wall.Id;
                    _gripIndex = Distance(point, first) <= Distance(point, second) ? 0 : 1;
                    _gripPosition = _gripIndex == 0
                        ? new PointModel(wall.X1, wall.Y1) : new PointModel(wall.X2, wall.Y2);
                    _gripPress = point;
                    _gripMoved = false;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }
        }
        if (Tool == PlanTool.Wall)
        {
            var end = Snap(point);
            if (_wallStart == null) _wallStart = end;
            else
            {
                var start = _wallStart;
                if (Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) >= 10)
                {
                    if (WallRequested?.Invoke(start, end) == true) _wallStart = end;
                }
            }
        }
        else if (Tool == PlanTool.Door || Tool == PlanTool.Window)
        {
            var hit = HitWall(point);
            if (hit.wall != null) OpeningRequested?.Invoke(Tool == PlanTool.Door ? "门" : "窗",
                hit.wall.Id, hit.offset);
        }
        else ElementPicked?.Invoke(HitElement(point));
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_panStart is Point previous)
        {
            _centerX -= (point.X - previous.X) / _scale;
            _centerY += (point.Y - previous.Y) / _scale;
            _panStart = point;
        }
        if (_gripWallId != null)
        {
            if (_gripPress is Point press && Distance(point, press) >= 3) _gripMoved = true;
            if (_gripMoved) _gripPosition = Snap(point, _gripWallId);
            InvalidateVisual();
            return;
        }
        if (Tool == PlanTool.Wall) _cursor = Snap(point);
        else { _cursor = World(point); _snapKind = PlanEditing.SnapNone; }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panStart = null;
        if (_gripWallId != null && _gripPosition != null)
        {
            var id = _gripWallId;
            var index = _gripIndex;
            var position = _gripPosition;
            var moved = _gripMoved;
            _gripWallId = null;
            _gripPosition = null;
            _gripPress = null;
            _gripMoved = false;
            if (moved) WallGripReleased?.Invoke(id, index, position);
            InvalidateVisual();
        }
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var before = World(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.002, 2);
        var after = World(p);
        _centerX += before.X - after.X;
        _centerY += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        CancelDraft();
        e.Handled = true;
    }

    private (WallModel? wall, double offset) HitWall(Point p)
    {
        WallModel? hit = null;
        var best = 16d;
        var offset = 0d;
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId))
        {
            var a = Screen(wall.X1, wall.Y1);
            var b = Screen(wall.X2, wall.Y2);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1) continue;
            var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
            var nearest = new Point(a.X + t * dx, a.Y + t * dy);
            var distance = Distance(nearest, p);
            if (distance < best)
            {
                best = distance; hit = wall;
                offset = t * Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Y2 - wall.Y1, 2));
            }
        }
        return (hit, offset);
    }

    private string? HitElement(Point p)
    {
        foreach (var opening in _model.Openings)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == opening.HostWallId && w.StoreyId == _storeyId);
            if (wall == null) continue;
            var length = Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Y2 - wall.Y1, 2));
            if (length < 1) continue;
            var start = (opening.Offset - opening.Width / 2) / length;
            var end = (opening.Offset + opening.Width / 2) / length;
            var a = Screen(wall.X1 + (wall.X2 - wall.X1) * start,
                wall.Y1 + (wall.Y2 - wall.Y1) * start);
            var b = Screen(wall.X1 + (wall.X2 - wall.X1) * end,
                wall.Y1 + (wall.Y2 - wall.Y1) * end);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var distanceAlong = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy)
                / Math.Max(1, dx * dx + dy * dy), 0, 1);
            if (Distance(new Point(a.X + distanceAlong * dx, a.Y + distanceAlong * dy), p) < 14)
                return opening.Id;
        }
        return HitWall(p).wall?.Id;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111A25")), new Rect(Bounds.Size));
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#263545")), 1);
        var min = World(new Point(0, Bounds.Height));
        var max = World(new Point(Bounds.Width, 0));
        var spacing = Math.Max(500d, Math.Ceiling(50d / _scale / 500d) * 500d);
        for (var x = Math.Ceiling(min.X / spacing) * spacing; x <= max.X; x += spacing)
            context.DrawLine(gridPen, Screen(x, min.Y), Screen(x, max.Y));
        for (var y = Math.Ceiling(min.Y / spacing) * spacing; y <= max.Y; y += spacing)
            context.DrawLine(gridPen, Screen(min.X, y), Screen(max.X, y));
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId))
        {
            var selected = wall.Id == _selectedId;
            var first = PreviewWallEndpoint(wall, 0);
            var second = PreviewWallEndpoint(wall, 1);
            var pen = new Pen(new SolidColorBrush(Color.Parse(selected ? "#FFC46B" : "#9BC4E9")),
                Math.Clamp(wall.Thickness * _scale, 3, 30));
            context.DrawLine(pen, first, second);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#142033")), 1),
                first, second);
        }
        foreach (var opening in _model.Openings)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == opening.HostWallId && w.StoreyId == _storeyId);
            if (wall == null) continue;
            var first = PreviewWallEndpoint(wall, 0);
            var second = PreviewWallEndpoint(wall, 1);
            var length = Distance(first, second) / _scale;
            if (length < 1) continue;
            var t1 = (opening.Offset - opening.Width / 2) / length;
            var t2 = (opening.Offset + opening.Width / 2) / length;
            var a = new Point(first.X + (second.X - first.X) * t1,
                first.Y + (second.Y - first.Y) * t1);
            var b = new Point(first.X + (second.X - first.X) * t2,
                first.Y + (second.Y - first.Y) * t2);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#111A25")),
                Math.Clamp(wall.Thickness * _scale + 2, 5, 32)), a, b);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(opening.Id == _selectedId
                ? "#FFC46B" : opening.Kind == "门" ? "#F4B779" : "#5AD4EC")), 3), a, b);
        }
        var selectedWall = _model.Walls.FirstOrDefault(w => w.Id == _selectedId && w.StoreyId == _storeyId);
        if (selectedWall != null)
        {
            var first = PreviewWallEndpoint(selectedWall, 0);
            var second = PreviewWallEndpoint(selectedWall, 1);
            var gripBrush = new SolidColorBrush(Color.Parse("#FFC46B"));
            context.FillRectangle(gripBrush, new Rect(first.X - 5, first.Y - 5, 10, 10));
            context.FillRectangle(gripBrush, new Rect(second.X - 5, second.Y - 5, 10, 10));
        }
        if (_wallStart != null && _cursor != null)
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), 2),
                Screen(_wallStart.X, _wallStart.Y), Screen(_cursor.X, _cursor.Y));
        var marker = _gripWallId != null ? _gripPosition : _cursor;
        if (marker != null && _snapKind != PlanEditing.SnapNone)
        {
            var p = Screen(marker.X, marker.Y);
            var color = _snapKind == PlanEditing.SnapIntersection ? "#FFCC66"
                : _snapKind == PlanEditing.SnapPerpendicular ? "#67E9BE" : "#6AC9FF";
            var pen = new Pen(new SolidColorBrush(Color.Parse(color)), 2);
            context.DrawEllipse(null, pen, p, 6, 6);
            context.DrawLine(pen, new Point(p.X - 9, p.Y), new Point(p.X + 9, p.Y));
            context.DrawLine(pen, new Point(p.X, p.Y - 9), new Point(p.X, p.Y + 9));
        }
    }
}
