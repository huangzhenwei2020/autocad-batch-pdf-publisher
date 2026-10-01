using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum PlanTool { Select, Wall, Door, Window, Slab, SlabOutline, SlabHoleRectangle, SlabHolePolygon }
internal enum PlanAxisConstraint { Free, X, Y }

internal sealed class PlanEditorCanvas : Control
{
    private BuildingModelDocument _model = SampleModelFactory.CreateEmptyModel("空模型");
    private string _storeyId = "1F";
    private string? _selectedId;
    private PointModel? _wallStart;
    private PointModel? _cursor;
    private readonly List<PointModel> _contour = new();
    private readonly Dictionary<string, SlabGeometry> _slabGeometry = new();
    private List<ViewLine> _planSymbols = new();
    public event Action<string, string>? SlabOpeningPicked;
    public bool IsContourTool => Tool is PlanTool.Slab or PlanTool.SlabOutline
        or PlanTool.SlabHoleRectangle or PlanTool.SlabHolePolygon;
    public event Func<PlanTool, List<PointModel>, bool>? ContourRequested;
    internal void AddContourPoint(PointModel point)
    {
        if (!IsContourTool || !double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        if (_contour.Count > 0 && Math.Abs(_contour[^1].X - point.X) < 0.001
            && Math.Abs(_contour[^1].Y - point.Y) < 0.001) return;
        _contour.Add(new PointModel(point.X, point.Y));
        if (Tool == PlanTool.SlabHoleRectangle && _contour.Count == 2) CompleteContour();
        InvalidateVisual();
    }

    internal bool CompleteContour()
    {
        var points = Tool == PlanTool.SlabHoleRectangle && _contour.Count == 2
            ? RectangleContour(_contour[0], _contour[1]) : _contour.ToList();
        if (points.Count < 3) return false;
        if (ContourRequested?.Invoke(Tool, points) != true) return false;
        CancelDraft();
        Tool = PlanTool.Select;
        return true;
    }

    private static List<PointModel> RectangleContour(PointModel a, PointModel b) => new()
    { new(a.X, a.Y), new(b.X, a.Y), new(b.X, b.Y), new(a.X, b.Y) };
    private Point? _lastPointer;
    private string _snapKind = PlanEditing.SnapNone;
    private Point? _panStart;
    private string? _gripWallId;
    private int _gripIndex;
    private PointModel? _gripPosition;
    private Point? _gripPress;
    private bool _gripMoved;
    private Dictionary<string, WallEndpointMove>? _gripPreview;
    private readonly List<(WallModel horizontal, WallModel vertical)> _orthogonalJunctions = new();
    private List<AxisModel> _resolvedAxes = new();
    private double _scale = 0.07;
    private double _centerX;
    private double _centerY;
    private bool _fitted;
    private bool _shiftHeld;
    private bool _moving;
    private bool _copying;
    private PointModel? _moveBase;
    public PlanTool Tool { get; set; }
    public PlanAxisConstraint AxisConstraint { get; private set; }
    public bool IsMoving => _moving;
    public bool IsCopyingMove => _copying;
    public bool HasWallStart => Tool == PlanTool.Wall && _wallStart != null;
    public bool PolarEnabled { get; private set; }
    public bool OrthogonalEnabled { get; private set; }
    public double PolarStepDegrees { get; private set; } = 45d;
    public event Func<PointModel, PointModel, bool>? WallRequested;
    public event Action<string, string, double>? OpeningRequested;
    public event Action<string?>? ElementPicked;
    public event Action<string, int, PointModel>? WallGripReleased;
    public event Action<string>? SnapChanged;
    public event Action<PlanAxisConstraint>? AxisConstraintChanged;
    public event Action<string, PointModel, PointModel, bool>? MoveRequested;
    public event Action<string>? MoveStageChanged;

    public void SetPolar(bool enabled, double stepDegrees)
    {
        if (!double.IsFinite(stepDegrees) || stepDegrees < 1d || stepDegrees > 90d)
            throw new ArgumentOutOfRangeException(nameof(stepDegrees));
        PolarEnabled = enabled;
        PolarStepDegrees = stepDegrees;
        if (_lastPointer is Point point && (Tool == PlanTool.Wall || _moving))
            _cursor = Snap(point);
        InvalidateVisual();
    }

    public void SetOrthogonal(bool enabled)
    {
        OrthogonalEnabled = enabled;
        if (_lastPointer is Point point) UpdateShiftConstraint(point, _shiftHeld);
        else if (!enabled) SetAxisConstraint(PlanAxisConstraint.Free);
        InvalidateVisual();
    }

    internal static PointModel PolarPoint(PointModel anchor, PointModel target, double stepDegrees)
    {
        var dx = target.X - anchor.X; var dy = target.Y - anchor.Y;
        var radius = Math.Sqrt(dx * dx + dy * dy);
        if (radius < 1e-9) return target;
        var step = stepDegrees * Math.PI / 180d;
        var angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return new PointModel(anchor.X + radius * Math.Cos(angle),
            anchor.Y + radius * Math.Sin(angle));
    }

    public bool BeginMove(bool copy = false)
    {
        if (_selectedId == null || (!_model.Walls.Any(w => w.Id == _selectedId && w.StoreyId == _storeyId)
            && !_model.Slabs.Any(s => s.Id == _selectedId && s.StoreyId == _storeyId)))
            return false;
        CancelDraft();
        Tool = PlanTool.Select;
        _moving = true;
        _copying = copy;
        MoveStageChanged?.Invoke((copy ? "复制 CO" : "移动 M")
            + "：指定基点（可捕捉墙端点或轴线）。Esc 取消。");
        return true;
    }

    public void SetAxisConstraint(PlanAxisConstraint constraint)
    {
        AxisConstraint = constraint;
        if ((Tool == PlanTool.Wall || _moving) && _lastPointer is Point point)
            _cursor = Snap(point);
        AxisConstraintChanged?.Invoke(constraint);
        InvalidateVisual();
    }

    private void UpdateShiftConstraint(Point point, bool held)
    {
        _shiftHeld = held;
        if (!held && !OrthogonalEnabled) { if (AxisConstraint != PlanAxisConstraint.Free)
            SetAxisConstraint(PlanAxisConstraint.Free); return; }
        PointModel? anchor = _wallStart ?? _moveBase ?? (IsContourTool ? _contour.LastOrDefault() : null);
        if (_gripWallId != null)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == _gripWallId);
            if (wall != null) anchor = _gripIndex == 0
                ? new PointModel(wall.X1, wall.Y1) : new PointModel(wall.X2, wall.Y2);
        }
        if (anchor == null) return;
        var world = World(point);
        var direction = ShiftDirection(anchor, world);
        if (direction != AxisConstraint) SetAxisConstraint(direction);
    }

    internal static PlanAxisConstraint ShiftDirection(PointModel anchor, PointModel world)
        => Math.Abs(world.X - anchor.X) >= Math.Abs(world.Y - anchor.Y)
            ? PlanAxisConstraint.X : PlanAxisConstraint.Y;

    public PlanEditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        LostFocus += (_, _) =>
        {
            _shiftHeld = false;
            if (!OrthogonalEnabled) SetAxisConstraint(PlanAxisConstraint.Free);
        };
    }

    public void SetModel(BuildingModelDocument model, string storeyId)
    {
        _model = model;
        _slabGeometry.Clear();
        foreach (var slab in model.Slabs) _slabGeometry[slab.Id] = SlabGeometry.Build(slab);
        _storeyId = model.FindStorey(storeyId)?.TemplateStoreyId ?? storeyId;
        _gripPreview = null;
        _moving = false;
        _copying = false;
        _moveBase = null;
        _resolvedAxes = BuildingAxisLayout.Resolve(model);
        IndexOrthogonalJunctions();
        RebuildPlanSymbols();
        if (!_fitted) Fit();
        InvalidateVisual();
    }

    public void SetStorey(string id)
    {
        _storeyId = _model.FindStorey(id)?.TemplateStoreyId ?? id;
        CancelDraft();
        IndexOrthogonalJunctions();
        RebuildPlanSymbols();
        Fit();
    }

    private void RebuildPlanSymbols()
    {
        _planSymbols = OrthographicProjector.CreatePlanDetailSymbols(_model, _storeyId);
    }

    public void SetSelection(string? id)
    {
        _selectedId = id;
        InvalidateVisual();
    }

    public void CancelDraft()
    {
        _moving = false;
        _copying = false;
        _moveBase = null;
        _wallStart = null;
        _contour.Clear();
        _cursor = null;
        _lastPointer = null;
        _shiftHeld = false;
        SetAxisConstraint(PlanAxisConstraint.Free);
        _snapKind = PlanEditing.SnapNone;
        _gripWallId = null;
        _gripPosition = null;
        _gripPress = null;
        _gripMoved = false;
        _gripPreview = null;
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

    public bool TryDrawWallPolar(double length, double angleDegrees, out string error)
    {
        error = "请先在画布上指定墙的起点。";
        if (Tool != PlanTool.Wall || _wallStart == null) return false;
        if (!double.IsFinite(length) || length < 10 || !double.IsFinite(angleDegrees))
        { error = "请输入有效的墙长和角度，例如 @3000<45。"; return false; }
        var angle = angleDegrees * Math.PI / 180d;
        var end = new PointModel(_wallStart.X + length * Math.Cos(angle),
            _wallStart.Y + length * Math.Sin(angle));
        if (WallRequested?.Invoke(_wallStart, end) != true)
        { error = "未能创建该墙。"; return false; }
        _wallStart = end;
        InvalidateVisual();
        error = "";
        return true;
    }

    public void Fit() => FitToSize(Bounds.Size);

    public void FrameSelection()
    {
        if (_selectedId == null || Bounds.Width <= 50 || Bounds.Height <= 50)
        { Fit(); return; }
        var points = BuildingVolumeBuilder.Build(_model, _storeyId).Faces
            .Where(f => f.ElementId == _selectedId).SelectMany(f => f.Points).ToArray();
        if (points.Length == 0) { Fit(); return; }
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        _centerX = (minX + maxX) / 2;
        _centerY = (minY + maxY) / 2;
        _scale = Math.Clamp(Math.Min(Bounds.Width * 0.85 / Math.Max(100, maxX - minX),
            Bounds.Height * 0.85 / Math.Max(100, maxY - minY)), 0.002, 2);
        _fitted = true;
        InvalidateVisual();
    }

    private void FitToSize(Size size)
    {
        if (size.Width <= 50 || size.Height <= 50)
        {
            _fitted = false;
            return;
        }
        var points = _model.Walls.Where(w => w.StoreyId == _storeyId)
            .SelectMany(w => new[] { new PointModel(w.X1, w.Y1), new PointModel(w.X2, w.Y2) })
            .Concat(_model.Slabs.Where(s => s.StoreyId == _storeyId)
                .SelectMany(s => s.Outline ?? new List<PointModel>())).ToArray();
        if (points.Length == 0) { _centerX = _centerY = 0; _scale = 0.07; }
        else
        {
            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxY = points.Max(p => p.Y);
            _centerX = (minX + maxX) / 2;
            _centerY = (minY + maxY) / 2;
            var axisBand = Math.Clamp(Math.Min(size.Width, size.Height) * 0.28d, 100d, 180d);
            _scale = Math.Clamp(Math.Min((size.Width - axisBand) / Math.Max(1000, maxX - minX),
                (size.Height - axisBand) / Math.Max(1000, maxY - minY)), 0.01, 0.5);
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
        if (_gripPreview != null && _gripPreview.TryGetValue(wall.Id + "|" + index, out var moved))
            return Screen(moved.X, moved.Y);
        return Screen(x, y);
    }

    private (Point first, Point second) PreviewWallBody(WallModel wall)
    {
        var first = PreviewWallEndpoint(wall, 0);
        var second = PreviewWallEndpoint(wall, 1);
        var dx = second.X - first.X; var dy = second.Y - first.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6) return (first, second);
        var offset = WallReferenceGeometry.BodyOffset(wall) * _scale / length;
        return (new Point(first.X + dy * offset, first.Y - dx * offset),
            new Point(second.X + dy * offset, second.Y - dx * offset));
    }

    private void UpdateGripPreview()
    {
        if (_gripWallId == null || _gripPosition == null) return;
        _gripPreview = new Dictionary<string, WallEndpointMove>(StringComparer.OrdinalIgnoreCase)
        {
            [_gripWallId + "|" + _gripIndex] = new WallEndpointMove
            {
                WallId = _gripWallId, Index = _gripIndex,
                X = _gripPosition.X, Y = _gripPosition.Y
            }
        };
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private PointModel Snap(Point p, string? excludedWallId = null)
    {
        var world = World(p);
        var from = Tool == PlanTool.Wall ? _wallStart : IsContourTool ? _contour.LastOrDefault() : null;
        var snapped = PlanEditing.Snap(_model, _storeyId, world.X, world.Y, 10d / _scale,
            from != null, from?.X ?? 0, from?.Y ?? 0, excludedWallId, _resolvedAxes);
        if (snapped.Kind != _snapKind && (Tool == PlanTool.Wall || _gripWallId != null || _moving))
            SnapChanged?.Invoke(snapped.Kind);
        _snapKind = snapped.Kind;
        PointModel? anchor = _wallStart ?? _moveBase
            ?? (IsContourTool && Tool != PlanTool.SlabHoleRectangle ? _contour.LastOrDefault() : null);
        if (excludedWallId != null)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == excludedWallId);
            if (wall != null) anchor = _gripIndex == 0
                ? new PointModel(wall.X1, wall.Y1) : new PointModel(wall.X2, wall.Y2);
        }
        if (anchor != null && AxisConstraint == PlanAxisConstraint.X)
            return new PointModel(snapped.X, anchor.Y);
        if (anchor != null && AxisConstraint == PlanAxisConstraint.Y)
            return new PointModel(anchor.X, snapped.Y);
        if (anchor != null && PolarEnabled && snapped.Kind == PlanEditing.SnapNone)
            return PolarPoint(anchor, new PointModel(snapped.X, snapped.Y), PolarStepDegrees);
        return new PointModel(snapped.X, snapped.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        _lastPointer = point;
        UpdateShiftConstraint(point, _shiftHeld || e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        var buttons = e.GetCurrentPoint(this).Properties;
        if (buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed)
        {
            _panStart = point;
            e.Pointer.Capture(this);
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        if (IsContourTool)
        {
            var end = Snap(point);
            if (Tool != PlanTool.SlabHoleRectangle && _contour.Count >= 3
                && Distance(point, Screen(_contour[0].X, _contour[0].Y)) <= 12)
                CompleteContour();
            else
            {
                AddContourPoint(end);
                if (e.ClickCount == 2 && Tool != PlanTool.SlabHoleRectangle) CompleteContour();
            }
            e.Handled = true; InvalidateVisual(); return;
        }
        if (_moving)
        {
            var snapped = Snap(point);
            if (_moveBase == null)
            {
                _moveBase = snapped;
                MoveStageChanged?.Invoke((_copying ? "复制 CO" : "移动 M")
                    + "：指定目标点；按住 Shift 自动锁定水平/垂直。Esc 取消。");
            }
            else
            {
                var from = _moveBase;
                var id = _selectedId!;
                var copy = _copying;
                _moving = false;
                _copying = false;
                _moveBase = null;
                MoveRequested?.Invoke(id, from, snapped, copy);
            }
            e.Handled = true;
            InvalidateVisual();
            return;
        }
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
        else
        {
            var hole = _model.Slabs.Where(s => s.StoreyId == _storeyId)
                .SelectMany(s => (s.Openings ?? new()).Select(o => (slab: s, opening: o)))
                .FirstOrDefault(h => InsideContour(World(point), h.opening.Outline));
            if (hole.opening != null) SlabOpeningPicked?.Invoke(hole.slab.Id, hole.opening.Id);
            else ElementPicked?.Invoke(HitElement(point));
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        _lastPointer = point;
        UpdateShiftConstraint(point, _shiftHeld || e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (_panStart is Point previous)
        {
            _centerX -= (point.X - previous.X) / _scale;
            _centerY += (point.Y - previous.Y) / _scale;
            _panStart = point;
        }
        if (_gripWallId != null)
        {
            if (_gripPress is Point press && Distance(point, press) >= 3) _gripMoved = true;
            if (_gripMoved)
            {
                _gripPosition = Snap(point, _gripWallId);
                UpdateGripPreview();
            }
            InvalidateVisual();
            return;
        }
        if (Tool == PlanTool.Wall || _moving || IsContourTool) _cursor = Snap(point);
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
            _gripPreview = null;
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
        if (IsContourTool && e.Key == Key.Enter)
        { CompleteContour(); e.Handled = true; return; }
        if (IsContourTool && e.Key == Key.Back)
        { if (_contour.Count > 0) _contour.RemoveAt(_contour.Count - 1);
            InvalidateVisual(); e.Handled = true; return; }
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (_lastPointer is Point point) UpdateShiftConstraint(point, true);
            return;
        }
        if (e.Key != Key.Escape) return;
        CancelDraft();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key != Key.LeftShift && e.Key != Key.RightShift) return;
        if (_lastPointer is Point point) UpdateShiftConstraint(point, false);
        else { _shiftHeld = false; if (!OrthogonalEnabled) SetAxisConstraint(PlanAxisConstraint.Free); }
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
        var wallHit = HitWall(p).wall;
        if (wallHit != null) return wallHit.Id;
        foreach (var slab in _model.Slabs.Where(s => s.StoreyId == _storeyId))
        {
            if (slab.Outline == null || slab.Outline.Count < 3) continue;
            foreach (var contour in _slabGeometry[slab.Id].Contours)
                for (var i = 0; i < contour.Count; i++)
                {
                    var a = Screen(contour[i].X, contour[i].Y);
                    var b = Screen(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y);
                    var dx = b.X - a.X; var dy = b.Y - a.Y;
                    var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy)
                        / Math.Max(1, dx * dx + dy * dy), 0, 1);
                    if (Distance(p, new Point(a.X + t * dx, a.Y + t * dy)) < 10) return slab.Id;
                }
        }
        var world = World(p);
        return _model.Slabs.LastOrDefault(s => s.StoreyId == _storeyId
            && InsideContour(world, s.Outline) && !(s.Openings ?? new()).Any(o => InsideContour(world, o.Outline)))?.Id;
    }

    private static bool InsideContour(PointModel point, List<PointModel> contour)
    {
        var inside = false;
        for (var i = 0; i < contour.Count; i++)
        {
            var a = contour[i]; var b = contour[(i + 1) % contour.Count];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111A25")), new Rect(Bounds.Size));
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#263545")), 1);
        var min = World(new Point(0, Bounds.Height));
        var max = World(new Point(Bounds.Width, 0));
        var spacing = Math.Max(500d, Math.Ceiling(50d / _scale / 500d) * 500d);
        var minorSpacing = spacing / 5;
        if (minorSpacing * _scale >= 8)
        {
            var minorPen = new Pen(new SolidColorBrush(Color.Parse("#192936")), 1);
            for (var x = Math.Ceiling(min.X / minorSpacing) * minorSpacing; x <= max.X; x += minorSpacing)
                context.DrawLine(minorPen, Screen(x, min.Y), Screen(x, max.Y));
            for (var y = Math.Ceiling(min.Y / minorSpacing) * minorSpacing; y <= max.Y; y += minorSpacing)
                context.DrawLine(minorPen, Screen(min.X, y), Screen(max.X, y));
        }
        for (var x = Math.Ceiling(min.X / spacing) * spacing; x <= max.X; x += spacing)
            context.DrawLine(gridPen, Screen(x, min.Y), Screen(x, max.Y));
        for (var y = Math.Ceiling(min.Y / spacing) * spacing; y <= max.Y; y += spacing)
            context.DrawLine(gridPen, Screen(min.X, y), Screen(max.X, y));
        DrawAxes(context, min, max);
        foreach (var slab in _model.Slabs.Where(s => s.StoreyId == _storeyId))
        {
            if (slab.Outline == null || slab.Outline.Count < 3) continue;
            var geometry = _slabGeometry[slab.Id];
            var fill = new StreamGeometry();
            using (var drawing = fill.Open())
            {
                drawing.SetFillRule(FillRule.EvenOdd);
                foreach (var contour in geometry.Contours)
                {
                    drawing.BeginFigure(Screen(contour[0].X, contour[0].Y), true);
                    foreach (var point in contour.Skip(1)) drawing.LineTo(Screen(point.X, point.Y));
                    drawing.EndFigure(true);
                }
            }
            context.DrawGeometry(new SolidColorBrush(Color.Parse(slab.Id == _selectedId
                ? "#28313A" : "#192530")), null, fill);
            var pen = new Pen(new SolidColorBrush(Color.Parse(slab.Id == _selectedId ? "#FFC46B" : "#A7B8C5")), 1.5);
            foreach (var contour in geometry.Contours)
                for (var i = 0; i < contour.Count; i++)
                    context.DrawLine(pen, Screen(contour[i].X, contour[i].Y),
                        Screen(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y));
            if (slab.Id == _selectedId) DrawSlabAnnotations(context, slab);
        }
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId))
        {
            var selected = wall.Id == _selectedId;
            var (first, second) = PreviewWallBody(wall);
            if (_slabGeometry.ContainsKey(_selectedId ?? ""))
            {
                var dx = second.X - first.X; var dy = second.Y - first.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < .001) continue;
                var half = Math.Clamp(wall.Thickness * _scale, 3, 30) / 2;
                var normal = new Vector(-dy / length * half, dx / length * half);
                var body = new StreamGeometry();
                using (var draw = body.Open())
                {
                    draw.BeginFigure(first + normal, true); draw.LineTo(second + normal);
                    draw.LineTo(second - normal); draw.LineTo(first - normal); draw.EndFigure(true);
                }
                context.DrawGeometry(new SolidColorBrush(Color.Parse("#334652")),
                    new Pen(new SolidColorBrush(Color.Parse("#8298A8")), 1), body);
                continue;
            }
            var pen = new Pen(new SolidColorBrush(Color.Parse(selected ? "#FFC46B" : "#9BC4E9")),
                Math.Clamp(wall.Thickness * _scale, 3, 30));
            context.DrawLine(pen, first, second);
        }
        DrawOrthogonalJunctions(context);
        foreach (var seam in WallJunctionLines.Resolve(_model,
            _model.Walls.Where(w => w.StoreyId == _storeyId),
            (_model.FindStorey(_storeyId)?.Elevation ?? 0d) + 1200d))
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#59768F")), 1),
                Screen(seam.Item1.X, seam.Item1.Y), Screen(seam.Item2.X, seam.Item2.Y));
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId))
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#192D3C")), 1),
                PreviewWallEndpoint(wall, 0), PreviewWallEndpoint(wall, 1));
        foreach (var opening in _model.Openings)
        {
            var wall = _model.Walls.FirstOrDefault(w => w.Id == opening.HostWallId && w.StoreyId == _storeyId);
            if (wall == null) continue;
            var (first, second) = PreviewWallBody(wall);
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
            if (!opening.HasSwingLeaf() || _moving || _gripPreview != null)
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(opening.Id == _selectedId
                    ? "#FFC46B" : "#5AD4EC")), 3), a, b);
        }
        if (!_moving && _gripPreview == null)
            foreach (var line in _planSymbols)
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(line.Layer == ViewLayers.Opening
                    ? "#A7B8C5" : "#58788F")), 1), Screen(line.X1, line.Y1), Screen(line.X2, line.Y2));
        if (_slabGeometry.TryGetValue(_selectedId ?? "", out var selectedSlabGeometry))
            foreach (var contour in selectedSlabGeometry.Contours)
                for (var i = 0; i < contour.Count; i++)
                    context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#FFC46B")), 2),
                        Screen(contour[i].X, contour[i].Y),
                        Screen(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y));
        foreach (var pending in _model.CadImport?.PendingOpenings ?? new List<CadPendingOpening>())
        {
            if (pending.StoreyId != _storeyId || pending.ReferencePosition == null) continue;
            var p = Screen(pending.ReferencePosition.X, pending.ReferencePosition.Y);
            var brush = new SolidColorBrush(Color.Parse("#FFCC66"));
            var pen = new Pen(brush, 2);
            context.DrawEllipse(null, pen, p, 7, 7);
            context.DrawLine(pen, new Point(p.X - 10, p.Y), new Point(p.X + 10, p.Y));
            context.DrawLine(pen, new Point(p.X, p.Y - 10), new Point(p.X, p.Y + 10));
            var label = new FormattedText(pending.Code + " · 待定位（参考）",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 12, brush);
            context.DrawText(label, new Point(p.X + 12, p.Y - label.Height / 2));
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
        if (IsContourTool && _contour.Count > 0)
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), 2);
            var points = _contour.ToList();
            if (Tool == PlanTool.SlabHoleRectangle && _cursor != null)
                points = RectangleContour(_contour[0], _cursor);
            else if (_cursor != null) points.Add(_cursor);
            for (var i = 0; i + 1 < points.Count; i++)
                context.DrawLine(pen, Screen(points[i].X, points[i].Y), Screen(points[i + 1].X, points[i + 1].Y));
            if (points.Count >= 3) context.DrawLine(pen, Screen(points[points.Count - 1].X, points[points.Count - 1].Y),
                Screen(points[0].X, points[0].Y));
        }
        if (_moving && _moveBase != null && _cursor != null && selectedWall != null)
        {
            var dx = _cursor.X - _moveBase.X;
            var dy = _cursor.Y - _moveBase.Y;
            var preview = new Pen(new SolidColorBrush(Color.Parse("#65E8B2")),
                Math.Clamp(selectedWall.Thickness * _scale, 3, 30));
            context.DrawLine(preview, Screen(selectedWall.X1 + dx, selectedWall.Y1 + dy),
                Screen(selectedWall.X2 + dx, selectedWall.Y2 + dy));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), 1),
                Screen(_moveBase.X, _moveBase.Y), Screen(_cursor.X, _cursor.Y));
        }
        if (_moving && _moveBase != null && _cursor != null
            && _slabGeometry.TryGetValue(_selectedId ?? "", out var movingSlab))
        {
            var dx = _cursor.X - _moveBase.X; var dy = _cursor.Y - _moveBase.Y;
            var pen = new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), 2);
            foreach (var contour in movingSlab.Contours)
                for (var i = 0; i < contour.Count; i++)
                {
                    var a = contour[i]; var b = contour[(i + 1) % contour.Count];
                    context.DrawLine(pen, Screen(a.X + dx, a.Y + dy), Screen(b.X + dx, b.Y + dy));
                }
        }
        var marker = _gripWallId != null ? _gripPosition : _cursor;
        if (marker != null && _snapKind != PlanEditing.SnapNone)
        {
            var p = Screen(marker.X, marker.Y);
            var color = _snapKind == PlanEditing.SnapIntersection
                || _snapKind == PlanEditing.SnapAxisIntersection ? "#FFCC66"
                : _snapKind == PlanEditing.SnapPerpendicular ? "#67E9BE" : "#6AC9FF";
            var pen = new Pen(new SolidColorBrush(Color.Parse(color)), 2);
            context.DrawEllipse(null, pen, p, 6, 6);
            context.DrawLine(pen, new Point(p.X - 9, p.Y), new Point(p.X + 9, p.Y));
            context.DrawLine(pen, new Point(p.X, p.Y - 9), new Point(p.X, p.Y + 9));
        }
        var origin = new Point(26, Bounds.Height - 26);
        var xPen = new Pen(new SolidColorBrush(Color.Parse("#FF5A5A")), 2);
        var yPen = new Pen(new SolidColorBrush(Color.Parse("#67BF57")), 2);
        context.DrawLine(xPen, origin, new Point(origin.X + 38, origin.Y));
        context.DrawLine(xPen, new Point(origin.X + 38, origin.Y), new Point(origin.X + 31, origin.Y - 4));
        context.DrawLine(yPen, origin, new Point(origin.X, origin.Y - 38));
        context.DrawLine(yPen, new Point(origin.X, origin.Y - 38), new Point(origin.X + 4, origin.Y - 31));
        context.DrawText(new FormattedText("X", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 14, Brushes.White), new Point(origin.X + 43, origin.Y - 8));
        context.DrawText(new FormattedText("Y", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 14, Brushes.White), new Point(origin.X - 6, origin.Y - 58));
    }

    private void DrawSlabAnnotations(DrawingContext context, SlabModel slab)
    {
        var outerA = Screen(slab.Outline.Min(p => p.X), slab.Outline.Max(p => p.Y));
        var outerB = Screen(slab.Outline.Max(p => p.X), slab.Outline.Min(p => p.Y));
        if (outerB.X - outerA.X < 100 || outerB.Y - outerA.Y < 100) return;
        if (outerA.Y >= 28)
        {
            var gap = Math.Min(48, outerA.Y - 26);
            DrawDimension(context, new Point(outerA.X, outerA.Y - gap), new Point(outerB.X, outerA.Y - gap),
                (outerB.X - outerA.X) / _scale, false);
        }
        var rightGap = Math.Min(48, Bounds.Width - outerB.X - 12);
        if (rightGap >= 12)
            DrawDimension(context, new Point(outerB.X + rightGap, outerA.Y), new Point(outerB.X + rightGap, outerB.Y),
                (outerB.Y - outerA.Y) / _scale, true);
        foreach (var hole in slab.Openings ?? new())
        {
            var a = Screen(hole.Outline.Min(p => p.X), hole.Outline.Max(p => p.Y));
            var b = Screen(hole.Outline.Max(p => p.X), hole.Outline.Min(p => p.Y));
            var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            if (b.X - a.X < 55 || b.Y - a.Y < 55) continue;
            var crossing = new Pen(new SolidColorBrush(Color.Parse("#506475")), 1);
            context.DrawLine(crossing, a, b);
            context.DrawLine(crossing, new Point(b.X, a.Y), new Point(a.X, b.Y));
            var text = new FormattedText(hole.Name ?? "洞口", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 15, Brushes.White);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#111A25")),
                new Rect(center.X - text.Width / 2 - 4, center.Y - text.Height / 2, text.Width + 8, text.Height));
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            DrawDimension(context, new Point(a.X, a.Y - 20), new Point(b.X, a.Y - 20),
                (b.X - a.X) / _scale, false);
            DrawDimension(context, new Point(a.X - 20, a.Y), new Point(a.X - 20, b.Y),
                (b.Y - a.Y) / _scale, true);
        }
    }

    private static void DrawDimension(DrawingContext context, Point a, Point b, double mm, bool vertical)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#D4E0E8")), 1);
        context.DrawLine(pen, a, b);
        foreach (var p in new[] { a, b })
            context.DrawLine(pen, vertical ? new Point(p.X - 6, p.Y) : new Point(p.X, p.Y - 6),
                vertical ? new Point(p.X + 6, p.Y) : new Point(p.X, p.Y + 6));
        var dx = vertical ? 0 : 5; var dy = vertical ? 5 : 0;
        context.DrawLine(pen, a, new Point(a.X + dx + dy, a.Y + dy - dx));
        context.DrawLine(pen, a, new Point(a.X + dx - dy, a.Y + dy + dx));
        context.DrawLine(pen, b, new Point(b.X - dx + dy, b.Y - dy - dx));
        context.DrawLine(pen, b, new Point(b.X - dx - dy, b.Y - dy + dx));
        var text = new FormattedText(mm.ToString("0.##", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 15, Brushes.White);
        if (vertical)
        {
            using var transform = context.PushTransform(Matrix.CreateRotation(-Math.PI / 2)
                * Matrix.CreateTranslation((a.X + b.X) / 2 - 12, (a.Y + b.Y) / 2));
            context.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
        }
        else context.DrawText(text, new Point((a.X + b.X) / 2 - text.Width / 2, a.Y - text.Height - 2));
    }

    private void DrawAxes(DrawingContext context, PointModel min, PointModel max)
    {
        var brush = new SolidColorBrush(Color.Parse("#6A9AA8"));
        var pen = new Pen(brush, 1, new DashStyle(new[] { 10d, 4d, 2d, 4d }, 0));
        var labelBrush = new SolidColorBrush(Color.Parse("#B3D8E1"));
        foreach (var axis in _resolvedAxes)
        {
            if (axis == null || double.IsNaN(axis.Position) || double.IsInfinity(axis.Position)) continue;
            var named = !string.IsNullOrWhiteSpace(axis.Name);
            if (axis.Vertical)
            {
                if (axis.Position < min.X || axis.Position > max.X) continue;
                var start = axis.ExtentStart == 0 && axis.ExtentEnd == 0 ? min.Y : axis.ExtentStart;
                var end = axis.ExtentStart == 0 && axis.ExtentEnd == 0 ? max.Y : axis.ExtentEnd;
                var a = Screen(axis.Position, Math.Max(start, min.Y));
                var b = Screen(axis.Position, Math.Min(end, max.Y));
                if (a.Y < b.Y) continue;
                context.DrawLine(pen, a, b);
                if (named)
                {
                    DrawAxisBubble(context, axis.StartName ?? axis.Name,
                        new Point(a.X, Math.Min(Bounds.Height - 18, a.Y - 17)), labelBrush);
                    DrawAxisBubble(context, axis.EndName ?? axis.Name,
                        new Point(b.X, Math.Max(18, b.Y + 17)), labelBrush);
                }
            }
            else
            {
                if (axis.Position < min.Y || axis.Position > max.Y) continue;
                var start = axis.ExtentStart == 0 && axis.ExtentEnd == 0 ? min.X : axis.ExtentStart;
                var end = axis.ExtentStart == 0 && axis.ExtentEnd == 0 ? max.X : axis.ExtentEnd;
                var a = Screen(Math.Max(start, min.X), axis.Position);
                var b = Screen(Math.Min(end, max.X), axis.Position);
                if (a.X > b.X) continue;
                context.DrawLine(pen, a, b);
                if (named)
                {
                    DrawAxisBubble(context, axis.StartName ?? axis.Name,
                        new Point(Math.Max(18, a.X + 17), a.Y), labelBrush);
                    DrawAxisBubble(context, axis.EndName ?? axis.Name,
                        new Point(Math.Min(Bounds.Width - 18, b.X - 17), b.Y), labelBrush);
                }
            }
        }
    }

    private void DrawOrthogonalJunctions(DrawingContext context)
    {
        foreach (var (horizontal, vertical) in _orthogonalJunctions)
        {
            if (_gripPreview != null && _gripWallId != null
                && (horizontal.Id == _gripWallId || vertical.Id == _gripWallId)) continue;
            var horizontalBody = PreviewWallBody(horizontal);
            var verticalBody = PreviewWallBody(vertical);
            var x = (verticalBody.first.X + verticalBody.second.X) / 2;
            var y = (horizontalBody.first.Y + horizontalBody.second.Y) / 2;
            var width = Math.Clamp(vertical.Thickness * _scale, 3, 30);
            var height = Math.Clamp(horizontal.Thickness * _scale, 3, 30);
            var color = horizontal.Id == _selectedId || vertical.Id == _selectedId ? "#FFC46B"
                : _slabGeometry.ContainsKey(_selectedId ?? "") ? "#334652" : "#9BC4E9";
            context.FillRectangle(new SolidColorBrush(Color.Parse(color)),
                new Rect(x - width / 2, y - height / 2, width, height));
        }
    }

    private void IndexOrthogonalJunctions()
    {
        _orthogonalJunctions.Clear();
        var walls = _model.Walls.Where(w => w.StoreyId == _storeyId).ToArray();
        for (var i = 0; i < walls.Length; i++)
        for (var j = i + 1; j < walls.Length; j++)
        {
            var first = walls[i]; var second = walls[j];
            var firstHorizontal = Math.Abs(first.Y2 - first.Y1) < 0.001;
            var secondHorizontal = Math.Abs(second.Y2 - second.Y1) < 0.001;
            var firstVertical = Math.Abs(first.X2 - first.X1) < 0.001;
            var secondVertical = Math.Abs(second.X2 - second.X1) < 0.001;
            if (!(firstHorizontal && secondVertical || secondHorizontal && firstVertical)) continue;
            var horizontal = firstHorizontal ? first : second;
            var vertical = firstHorizontal ? second : first;
            var h0 = WallReferenceGeometry.BodyPoint(horizontal, horizontal.X1, horizontal.Y1);
            var h1 = WallReferenceGeometry.BodyPoint(horizontal, horizontal.X2, horizontal.Y2);
            var v0 = WallReferenceGeometry.BodyPoint(vertical, vertical.X1, vertical.Y1);
            var v1 = WallReferenceGeometry.BodyPoint(vertical, vertical.X2, vertical.Y2);
            var hHalf = horizontal.Thickness / 2d;
            var vHalf = vertical.Thickness / 2d;
            if (Math.Max(h0.X, h1.X) < v0.X - vHalf - 0.5d
                || Math.Min(h0.X, h1.X) > v0.X + vHalf + 0.5d
                || Math.Max(v0.Y, v1.Y) < h0.Y - hHalf - 0.5d
                || Math.Min(v0.Y, v1.Y) > h0.Y + hHalf + 0.5d) continue;
            _orthogonalJunctions.Add((horizontal, vertical));
        }
    }

    private static void DrawAxisBubble(DrawingContext context, string name, Point center, IBrush brush)
    {
        var pen = new Pen(brush, 1);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#111A25")), pen, center, 14, 14);
        var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, brush);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
