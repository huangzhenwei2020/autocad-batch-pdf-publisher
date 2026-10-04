using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal enum PlanTool { Select, Wall, Opening, Slab, SlabOutline, SlabHoleRectangle, SlabHolePolygon, Column, Beam }
internal enum PlanAxisConstraint { Free, X, Y }

internal sealed partial class PlanEditorCanvas : Control
{
    private BuildingModelDocument _model = SampleModelFactory.CreateEmptyModel("空模型");
    private string _storeyId = "1F";
    private string _axisStoreyId = "1F";
    private string? _selectedId;
    private readonly HashSet<string> _selectedIds=new();
    private readonly HashSet<string> _hiddenIds=new(),_frozenIds=new();
    private bool Visible(string id)=>!_hiddenIds.Contains(id);
    private bool Selectable(string id)=>Visible(id)&&!_frozenIds.Contains(id);
    internal bool IsElementShown(string id)=>Visible(id);
    internal bool IsElementSelectable(string id)=>Selectable(id);
    internal void SetViewState(IEnumerable<string> hidden,IEnumerable<string> frozen)
    {
        _hiddenIds.Clear();_hiddenIds.UnionWith(hidden);_frozenIds.Clear();_frozenIds.UnionWith(frozen);
        IndexOrthogonalJunctions();RebuildPlanSymbols();InvalidateVisual();
    }
    private PointModel? _wallStart;
    private PointModel? _cursor;
    private readonly List<PointModel> _contour = new();
    private readonly Dictionary<string, SlabGeometry> _slabGeometry = new();
    private List<ViewLine> _planSymbols = new();
    private readonly Dictionary<string,List<ViewLine>> _openingSymbols=new();
    private List<Tuple<PointModel,PointModel>> _planSeams=new();
    private CadPendingOpening? _pendingOpening;
    private WallModel? _pendingWall;
    private double _pendingOffset;
    private bool _pendingPlacementLocked;
    public event Func<CadPendingOpening,string,double,bool>? PendingOpeningRequested;
    internal bool HasPendingPlacement=>_pendingOpening!=null;
    internal bool ConfirmPendingPlacement()
    {
        if(!_pendingPlacementLocked||_pendingOpening==null||_pendingWall==null)return false;
        if(PendingOpeningRequested?.Invoke(_pendingOpening,_pendingWall.Id,_pendingOffset)!=true)return false;
        _pendingOpening=null;_pendingWall=null;_pendingPlacementLocked=false;InvalidateVisual();return true;
    }
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
    private List<(AxisModel Axis,PointModel Start,PointModel End)>? _axisEndpointCache;
    private double _scale = 0.07;
    private double _centerX;
    private double _centerY;
    private bool _fitted;
    private bool _shiftHeld;
    private bool _moving;
    private bool _copying;
    private PointModel? _moveBase;
    public bool GridVisible { get; set; }
    public double PickboxSize { get; set; } = 8;
    public double CrosshairPercent { get; set; } = 10;
    public string CrosshairColor { get; set; } = "#B3D8E1";
    public PlanTool Tool { get; set; }
    internal OpeningPlacementChoice? PlacementOpeningChoice {get;private set;}
    internal OpeningTypeModel? PlacementOpeningType=>PlacementOpeningChoice?.Type;
    internal void SetOpeningPlacementType(OpeningTypeModel? type)=>SetOpeningPlacement(type==null?null:OpeningPlacementChoice.FromType(type));
    internal void SetOpeningPlacement(OpeningPlacementChoice? choice){PlacementOpeningChoice=choice;_openingPreviewCache.Clear();InvalidateVisual();}
    public PlanAxisConstraint AxisConstraint { get; private set; }
    public bool IsMoving => _moving;
    public bool IsCopyingMove => _copying;
    public bool HasWallStart => Tool == PlanTool.Wall && _wallStart != null;
    public bool PolarEnabled { get; private set; }
    public bool OrthogonalEnabled { get; private set; }
    public double PolarStepDegrees { get; private set; } = 45d;
    public event Func<PointModel, PointModel, bool>? WallRequested;
    public event Func<PointModel, PointModel, bool>? BeamRequested;
    public event Func<PointModel,bool>? ColumnRequested;
    internal bool CreateColumn(PointModel center)=>ColumnRequested?.Invoke(center)==true;
    internal bool CreateBeam(PointModel start,PointModel end)=>BeamRequested?.Invoke(start,end)==true;
    public event Action<string, string, double>? OpeningRequested;
    public event Action<string?>? OpeningActivated;
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
            && !_model.Slabs.Any(s => s.Id == _selectedId && s.StoreyId == _storeyId)
            && !_model.Columns.Any(c=>c.Id==_selectedId&&c.StoreyId==_storeyId)
            && !_model.Beams.Any(b=>b.Id==_selectedId&&b.StoreyId==_storeyId)))
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
        Cursor = new Cursor(StandardCursorType.None);
        LostFocus += (_, _) =>
        {
            _shiftHeld = false;
            if (!OrthogonalEnabled) SetAxisConstraint(PlanAxisConstraint.Free);
        };
    }

    public void SetModel(BuildingModelDocument model, string storeyId)
    {
        _pendingOpening=null;_pendingWall=null;_pendingPlacementLocked=false;
        CancelOpeningGrip();_openingPreviewCache.Clear();
        if (!_axisBatchPending) ClearAxisSelection();
        _axisEndpointCache=null;_axisGhostCache=null;
        _model = model;
        _slabGeometry.Clear();
        foreach (var slab in model.Slabs) _slabGeometry[slab.Id] = SlabGeometry.Build(slab);
        _axisStoreyId=storeyId;
        _storeyId = model.FindStorey(storeyId)?.TemplateStoreyId ?? storeyId;
        _gripPreview = null;
        _moving = false;
        _copying = false;
        _moveBase = null;
        _resolvedAxes = BuildingAxisLayout.Resolve(model, _axisStoreyId);
        IndexOrthogonalJunctions();
        RebuildPlanSymbols();
        if (!_fitted) Fit();
        InvalidateVisual();
    }

    public void SetStorey(string id)
    {
        _axisEndpointCache=null;_axisGhostCache=null;
        _axisStoreyId=id;
        _storeyId = _model.FindStorey(id)?.TemplateStoreyId ?? id;
        _resolvedAxes=BuildingAxisLayout.Resolve(_model,id);
        CancelDraft();
        IndexOrthogonalJunctions();
        RebuildPlanSymbols();
        Fit();
    }

    private void RebuildPlanSymbols()
    {
        _openingPreviewCache.Clear();_openingHandleCache.Clear();_openingCodeTextCache.Clear();_openingSymbols.Clear();_openingPickRegions.Clear();_preselectedId=null;
        var view=new BuildingModelDocument {Walls=_model.Walls.Where(w=>Visible(w.Id)).ToList()};
        _planSymbols = OrthographicProjector.CreatePlanDetailSymbols(view, _storeyId);
        foreach(var opening in _model.Openings.Where(o=>Visible(o.Id))) {
            var wall=_model.Walls.FirstOrDefault(w=>w.Id==opening.HostWallId&&w.StoreyId==_storeyId&&Visible(w.Id));
            if(wall==null)continue;
            var symbols=new BuildingModelDocument {Walls=new(){wall},Openings=new(){opening},OpeningTypes=_model.OpeningTypes,OpeningOverrides=_model.OpeningOverrides};
            _openingSymbols[opening.Id]=OrthographicProjector.CreatePlanDetailSymbols(OpeningConstruction.ApplyOverrides(symbols),_storeyId);
            var code=_model.OpeningOverrides?.FirstOrDefault(o=>o.OpeningId==opening.Id)?.TypeCode??OpeningConstruction.EffectiveCode(opening);
            var type=_model.OpeningTypes.FirstOrDefault(t=>string.Equals(t.Code,code,StringComparison.OrdinalIgnoreCase))??OpeningConstruction.Default(opening);
            _openingPickRegions[opening.Id]=OpeningPlanGeometry.SelectionRegions(opening,type,wall.Thickness);
        }
        _planSeams=WallJunctionLines.Resolve(_model,view.Walls.Where(w=>w.StoreyId==_storeyId),(_model.FindStorey(_storeyId)?.Elevation??0)+1200).ToList();
    }

    public void SetSelection(string? id)
    {
        if(id!=_selectedId)CancelOpeningGrip();
        _selectedIds.Clear();if(id!=null)_selectedIds.Add(id);
        _selectedId = id;
        InvalidateVisual();
    }
    public void SetSelections(IEnumerable<string> ids){var selection=ids.ToList();if(!_selectedIds.SetEquals(selection))CancelOpeningGrip();_selectedIds.Clear();foreach(var id in selection)_selectedIds.Add(id);_selectedId=_selectedIds.LastOrDefault();InvalidateVisual();}

    public void CancelDraft()
    {
        CancelOpeningGrip();
        _pendingOpening=null;_pendingWall=null;_pendingPlacementLocked=false;
        if (!_axisBatchPending) ClearAxisSelection();
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
        _openingCodeTextCache.Clear();
        if (_selectedId == null || Bounds.Width <= 50 || Bounds.Height <= 50)
        { Fit(); return; }
        var points = _openingSymbols.TryGetValue(_selectedId,out var symbols)&&symbols.Count>0
            ? symbols.SelectMany(l=>new[]{new Point3DModel(l.X1,l.Y1,0),new Point3DModel(l.X2,l.Y2,0)}).ToArray()
            : BuildingVolumeBuilder.Build(_model, _storeyId).Faces.Where(f => f.ElementId == _selectedId).SelectMany(f => f.Points).ToArray();
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
        _openingCodeTextCache.Clear();
        if (size.Width <= 50 || size.Height <= 50)
        {
            _fitted = false;
            return;
        }
        var points = _model.Walls.Where(w => w.StoreyId == _storeyId && Visible(w.Id))
            .SelectMany(w => new[] { new PointModel(w.X1, w.Y1), new PointModel(w.X2, w.Y2) })
            .Concat(_model.Slabs.Where(s => s.StoreyId == _storeyId && Visible(s.Id))
                .SelectMany(s => s.Outline ?? new List<PointModel>()))
            .Concat(_model.Columns.Where(c=>c.StoreyId==_storeyId&&Visible(c.Id)).SelectMany(StructuralGeometry.ColumnOutline))
            .Concat(_model.Beams.Where(b=>b.StoreyId==_storeyId&&Visible(b.Id)).SelectMany(StructuralGeometry.BeamOutline))
            .Concat(_openingSymbols.Values.SelectMany(lines=>lines).SelectMany(l=>new[]{new PointModel(l.X1,l.Y1),new PointModel(l.X2,l.Y2)}))
            .Concat(AxisEndpoints().SelectMany(axis=>new[] {
                new PointModel(axis.Start.X-AxisBubbleRadius,axis.Start.Y-AxisBubbleRadius),new PointModel(axis.Start.X+AxisBubbleRadius,axis.Start.Y+AxisBubbleRadius),
                new PointModel(axis.End.X-AxisBubbleRadius,axis.End.Y-AxisBubbleRadius),new PointModel(axis.End.X+AxisBubbleRadius,axis.End.Y+AxisBubbleRadius) })).ToArray();
        if (points.Length == 0) { _centerX = _centerY = 0; _scale = 0.07; }
        else
        {
            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxY = points.Max(p => p.Y);
            _centerX = (minX + maxX) / 2;
            _centerY = (minY + maxY) / 2;
            _scale = Math.Clamp(Math.Min((size.Width - 48) / Math.Max(1000, maxX - minX),
                (size.Height - 48) / Math.Max(1000, maxY - minY)), 0.001, 0.5);
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
        var from = Tool is PlanTool.Wall or PlanTool.Beam ? _wallStart : IsContourTool ? _contour.LastOrDefault() : null;
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
        if(HandleOpeningCenterPress(e))return;
        if (buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed)
        {
            _axisPress=null;
            _panStart = point;
            e.Pointer.Capture(this);
            return;
        }
        if (!buttons.IsLeftButtonPressed) return;
        if(AxisMode!=AxisEditMode.Off) {
            HandleAxisEditPress(point,e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled=true;return;
        }
        if(HasOpeningGrip) {MoveOpeningGrip(point);FinishOpeningGrip();e.Handled=true;return;}
        if(Tool==PlanTool.Select&&TryBeginOpeningGrip(point)) {e.Pointer.Capture(this);e.Handled=true;InvalidateVisual();return;}
        if(Tool==PlanTool.Select&&!_moving) {
            if(_pendingOpening!=null) {
                var hit=HitWall(point);_pendingWall=hit.wall;_pendingOffset=hit.offset;
                _pendingPlacementLocked=hit.wall!=null;
                MoveStageChanged?.Invoke(hit.wall==null?"请选择门窗所在的墙；Esc 取消":"位置预览：回车或空格放置门窗，Esc 取消；再次点击可换位置");
                e.Handled=true;InvalidateVisual();return;
            }
            var pending=(_model.CadImport?.PendingOpenings??new()).FirstOrDefault(p=> {
                if(p.StoreyId!=_storeyId||p.ReferencePosition==null)return false;
                var origin=Screen(p.ReferencePosition.X,p.ReferencePosition.Y);
                var label=new FormattedText(p.Code+" · 点击放置",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,
                    DrawingAnnotationSettings.Resolve(_model).TextHeight*AxisAnnotationScale*_scale,Brushes.White);
                return Distance(point,origin)<=10||new Rect(origin.X+180*_scale,origin.Y-label.Height/2,label.Width,label.Height).Contains(point);
            });
            if(pending!=null) {
                _pendingOpening=pending;_pendingWall=null;_pendingPlacementLocked=false;
                MoveStageChanged?.Invoke("移动 "+pending.Code+" 到所在墙的位置，点击预览，回车确认；Esc 取消");
                e.Handled=true;InvalidateVisual();return;
            }
        }
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
        if(Tool==PlanTool.Column)CreateColumn(Snap(point));
        else if (Tool == PlanTool.Wall || Tool==PlanTool.Beam)
        {
            var end = Snap(point);
            if (_wallStart == null) _wallStart = end;
            else
            {
                var start = _wallStart;
                if (Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) >= 10)
                {
                    if ((Tool==PlanTool.Beam ? CreateBeam(start,end) : WallRequested?.Invoke(start, end)==true)) _wallStart = end;
                }
            }
        }
        else if (Tool == PlanTool.Opening && PlacementOpeningType!=null)
        {
            var hit = HitWall(point);
            if (hit.wall != null) OpeningRequested?.Invoke(PlacementOpeningType.Kind,
                hit.wall.Id, hit.offset);
        }
        else
        {
            var hole = _model.Slabs.Where(s => s.StoreyId == _storeyId && Selectable(s.Id))
                .SelectMany(s => (s.Openings ?? new()).Select(o => (slab: s, opening: o)))
                .FirstOrDefault(h => InsideContour(World(point), h.opening.Outline));
            if (hole.opening != null) SlabOpeningPicked?.Invoke(hole.slab.Id, hole.opening.Id);
            else {var id=HitElement(point);ElementPicked?.Invoke(id);if(e.ClickCount==2)OpeningActivated?.Invoke(id);}
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
        if(AxisMode!=AxisEditMode.Off) { UpdateAxisEditHover(point);InvalidateVisual();return; }
        if(_openingGripDraft!=null){MoveOpeningGrip(point);InvalidateVisual();return;}
        _openingGripHover=null;_preselectedId=null;
        if(Tool==PlanTool.Select&&!_moving&&_pendingOpening==null&&_panStart==null) {
            _openingGripHover=OpeningGripHover(point);
            _preselectedId=_openingGripHover!=null?_selectedId:HitElement(point);
        }
        if(_pendingOpening!=null&&!_pendingPlacementLocked) {var hit=HitWall(point);_pendingWall=hit.wall;_pendingOffset=hit.offset;}
        if (Tool == PlanTool.Wall || Tool==PlanTool.Beam || Tool==PlanTool.Column || _moving || IsContourTool) _cursor = Snap(point);
        else { _cursor = World(point); _snapKind = PlanEditing.SnapNone; }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    { base.OnPointerExited(e); _lastPointer=null;_preselectedId=null;_openingGripHover=null; InvalidateVisual(); }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if(HandleOpeningCenterRelease(e))return;
        if(_openingGripDraft!=null&&e.InitialPressMouseButton==MouseButton.Left) {
            if(!_openingGripClickMode) {
                if(_openingGripMoved){MoveOpeningGrip(e.GetPosition(this));FinishOpeningGrip();}else _openingGripClickMode=true;
            }
            _openingReleasingCapture=true;
            try {if(e.Pointer.Captured==this)e.Pointer.Capture(null);}finally{_openingReleasingCapture=false;}
            e.Handled=true;return;
        }
        if (AxisMode!=AxisEditMode.Off && e.InitialPressMouseButton==MouseButton.Left && _axisPress!=null) {
            FinishAxisSelection(e.GetPosition(this));
            if(e.Pointer.Captured==this)e.Pointer.Capture(null);
            e.Handled=true;return;
        }
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
        if(_axisPress!=null){e.Handled=true;return;}
        var p = e.GetPosition(this);
        ZoomAt(p, e.Delta.Y);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if(!_openingReleasingCapture)CancelOpeningGrip();
        if(!_openingReleasingCapture)_openingCenterRightDown=false;
        _axisPress=null;
        InvalidateVisual();
    }

    internal void ZoomAt(Point p, double wheelDelta)
    {
        _openingCodeTextCache.Clear();
        var before = World(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, wheelDelta), 0.00001, 10);
        var after = World(p);
        _centerX += before.X - after.X;
        _centerY += before.Y - after.Y;
        InvalidateVisual();
    }

    internal double PixelsPerMillimetre => _scale;
    internal Point ModelToScreen(PointModel point) => Screen(point.X, point.Y);
    private double Stroke(double millimetres = 15) => millimetres * _scale;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(HasOpeningGrip&&e.Key is Key.Space or Key.Enter) {
            ConfirmOpeningGrip();e.Handled=true;return;
        }
        if(_pendingOpening!=null&&e.Key is Key.Space or Key.Enter) {
            ConfirmPendingPlacement();
            e.Handled=true;InvalidateVisual();return;
        }
        if(AxisMode!=AxisEditMode.Off && e.Key is Key.Space or Key.Enter) {
            ConfirmAxisSelection();e.Handled=true;return;
        }
        if(AxisMode!=AxisEditMode.Off && e.Key==Key.Escape) {
            if(!_axisBatchPending)ClearAxisSelection();e.Handled=true;return;
        }
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
        var best = PickboxSize/2;
        var offset = 0d;
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId && Selectable(w.Id)))
        {
            var a = Screen(wall.X1, wall.Y1);
            var b = Screen(wall.X2, wall.Y2);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared < 1) continue;
            var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
            var nearest = new Point(a.X + t * dx, a.Y + t * dy);
            var distance = Math.Max(0,Distance(nearest,p)-wall.Thickness*_scale/2);
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
        var openingHit=HitOpening(p);if(openingHit!=null)return openingHit;
        var structurePoint=World(p);
        foreach(var c in _model.Columns.Where(c=>c.StoreyId==_storeyId&&Selectable(c.Id)))
            if(InsideContour(structurePoint,StructuralGeometry.ColumnOutline(c)))return c.Id;
        foreach(var b in _model.Beams.Where(b=>b.StoreyId==_storeyId&&Selectable(b.Id)))
            if(InsideContour(structurePoint,StructuralGeometry.BeamOutline(b)))return b.Id;
        var wallHit = HitWall(p).wall;
        if (wallHit != null) return wallHit.Id;
        foreach (var slab in _model.Slabs.Where(s => s.StoreyId == _storeyId && Selectable(s.Id)))
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
                    if (Distance(p, new Point(a.X + t * dx, a.Y + t * dy)) < PickboxSize/2) return slab.Id;
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
        if(GridVisible) {
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
        }
        DrawAxes(context);
        foreach (var slab in _model.Slabs.Where(s => s.StoreyId == _storeyId && Visible(s.Id)))
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
            context.DrawGeometry(new SolidColorBrush(Color.Parse(_selectedIds.Contains(slab.Id)
                ? "#28313A" : "#192530")), null, fill);
            var pen = new Pen(new SolidColorBrush(Color.Parse(_selectedIds.Contains(slab.Id) ? "#FFC46B" : "#A7B8C5")), Stroke(20));
            foreach (var contour in geometry.Contours)
                for (var i = 0; i < contour.Count; i++)
                    context.DrawLine(pen, Screen(contour[i].X, contour[i].Y),
                        Screen(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y));
            if (_selectedIds.Contains(slab.Id)) DrawSlabAnnotations(context, slab);
        }
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId && Visible(w.Id)))
        {
            var selected = _selectedIds.Contains(wall.Id);
            var preselected = ShowSelectionPreview && _preselectedId == wall.Id;
            var (first, second) = PreviewWallBody(wall);
            if (_slabGeometry.ContainsKey(_selectedId ?? ""))
            {
                var dx = second.X - first.X; var dy = second.Y - first.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < .001) continue;
                var half = wall.Thickness * _scale / 2;
                var normal = new Vector(-dy / length * half, dx / length * half);
                var body = new StreamGeometry();
                using (var draw = body.Open())
                {
                    draw.BeginFigure(first + normal, true); draw.LineTo(second + normal);
                    draw.LineTo(second - normal); draw.LineTo(first - normal); draw.EndFigure(true);
                }
                context.DrawGeometry(preselected?Brushes.Cyan:new SolidColorBrush(Color.Parse("#334652")),
                    new Pen(new SolidColorBrush(Color.Parse("#8298A8")), Stroke()), body);
                continue;
            }
            // Highlight the wall body before opening cuts so the preview stays continuous without filling holes.
            var pen = new Pen(preselected?Brushes.Cyan:new SolidColorBrush(Color.Parse(selected ? "#FFC46B" : "#9BC4E9")),
                wall.Thickness * _scale);
            context.DrawLine(pen, first, second);
        }
        DrawOrthogonalJunctions(context);
        foreach (var seam in _planSeams)
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#59768F")), Stroke()),
                Screen(seam.Item1.X, seam.Item1.Y), Screen(seam.Item2.X, seam.Item2.Y));
        foreach (var wall in _model.Walls.Where(w => w.StoreyId == _storeyId && Visible(w.Id)))
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#192D3C")), Stroke()),
                PreviewWallEndpoint(wall, 0), PreviewWallEndpoint(wall, 1));
        foreach (var opening in _model.Openings.Where(o=>Visible(o.Id)))
        {
            if(opening.Id==_openingGripDraft?.Id&&!_openingLabelGrip)continue;
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
            if(OpeningPlanGeometry.CutsWall(opening))context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#111A25")),
                wall.Thickness * _scale), a, b);
            if (_moving || _gripPreview != null)
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(_selectedIds.Contains(opening.Id)
                    ? "#FFC46B" : "#5AD4EC")), Stroke(25)), a, b);
        }
        if (!_moving && _gripPreview == null)
        {
            foreach (var line in _planSymbols)
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(line.Layer == ViewLayers.Opening
                    ? "#A7B8C5" : "#58788F")), Stroke()), Screen(line.X1, line.Y1), Screen(line.X2, line.Y2));
            foreach(var pair in _openingSymbols.Where(p=>p.Key!=_openingGripDraft?.Id||_openingLabelGrip))foreach(var line in pair.Value)
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse(_selectedIds.Contains(pair.Key)?"#FFC46B":"#A7B8C5")),1,
                    line.LineType=="HIDDEN"?new DashStyle(new[]{4d,3d},0):null),Screen(line.X1,line.Y1),Screen(line.X2,line.Y2));
            DrawOpeningCodes(context);
        }
        void DrawStructure(string id,List<PointModel> outline)
        {
            if(_moving&&id==_selectedId&&_moveBase!=null&&_cursor!=null)
                outline=outline.Select(p=>new PointModel(p.X+_cursor.X-_moveBase.X,p.Y+_cursor.Y-_moveBase.Y)).ToList();
            var pen=new Pen(new SolidColorBrush(Color.Parse(_selectedIds.Contains(id)?"#FFC46B":"#8CB4D5")),Stroke(20));
            for(var i=0;i<outline.Count;i++)context.DrawLine(pen,Screen(outline[i].X,outline[i].Y),Screen(outline[(i+1)%outline.Count].X,outline[(i+1)%outline.Count].Y));
        }
        foreach(var c in _model.Columns.Where(c=>c.StoreyId==_storeyId&&Visible(c.Id)))DrawStructure(c.Id,StructuralGeometry.ColumnOutline(c));
        foreach(var b in _model.Beams.Where(b=>b.StoreyId==_storeyId&&Visible(b.Id)))DrawStructure(b.Id,StructuralGeometry.BeamOutline(b));
        if (_slabGeometry.TryGetValue(_selectedId ?? "", out var selectedSlabGeometry))
            foreach (var contour in selectedSlabGeometry.Contours)
                for (var i = 0; i < contour.Count; i++)
                    context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#FFC46B")), Stroke(30)),
                        Screen(contour[i].X, contour[i].Y),
                        Screen(contour[(i + 1) % contour.Count].X, contour[(i + 1) % contour.Count].Y));
        foreach (var pending in _model.CadImport?.PendingOpenings ?? new List<CadPendingOpening>())
        {
            if (pending.StoreyId != _storeyId || pending.ReferencePosition == null) continue;
            var p = Screen(pending.ReferencePosition.X, pending.ReferencePosition.Y);
            var brush = new SolidColorBrush(Color.Parse("#FFCC66"));
            var pen = new Pen(brush, Stroke(25));
            var radius=100*_scale;var arm=150*_scale;
            context.DrawEllipse(null, pen, p, radius, radius);
            context.DrawLine(pen, new Point(p.X - arm, p.Y), new Point(p.X + arm, p.Y));
            context.DrawLine(pen, new Point(p.X, p.Y - arm), new Point(p.X, p.Y + arm));
            var label = new FormattedText(pending.Code + " · 点击放置",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
                DrawingAnnotationSettings.Resolve(_model).TextHeight*AxisAnnotationScale*_scale, brush);
            context.DrawText(label, new Point(p.X + 180*_scale, p.Y - label.Height / 2));
        }
        if(_pendingOpening!=null&&_pendingWall!=null) {
            var wall=_pendingWall;var length=Math.Sqrt(Math.Pow(wall.X2-wall.X1,2)+Math.Pow(wall.Y2-wall.Y1,2));
            var from=_pendingOffset-_pendingOpening.Width/2;var to=_pendingOffset+_pendingOpening.Width/2;
            var ux=(wall.X2-wall.X1)/length;var uy=(wall.Y2-wall.Y1)/length;
            var a=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*from,wall.Y1+uy*from);
            var b=WallReferenceGeometry.BodyPoint(wall,wall.X1+ux*to,wall.Y1+uy*to);
            var normal=new Vector(-uy*wall.Thickness*_scale/2,-ux*wall.Thickness*_scale/2);
            var first=Screen(a.X,a.Y);var second=Screen(b.X,b.Y);
            var previewPen=new Pen(from<0||to>length?Brushes.OrangeRed:Brushes.Cyan,2);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#111A25")),wall.Thickness*_scale),first,second);
            context.DrawLine(previewPen,first+normal,second+normal);context.DrawLine(previewPen,first-normal,second-normal);
            context.DrawLine(previewPen,first+normal,first-normal);context.DrawLine(previewPen,second+normal,second-normal);
            DrawOpeningPreviewSymbols(context,wall,new OpeningModel {Code=_pendingOpening.Code,Kind=_pendingOpening.Kind,
                Offset=_pendingOffset,Width=_pendingOpening.Width,Height=_pendingOpening.Height},previewPen);
        }
        if(_pendingOpening!=null&&_lastPointer is Point pendingPointer) {
            var text=new FormattedText(_pendingPlacementLocked?"回车 / 空格确认 · Esc 取消":"移动到目标墙，点击确定位置",
                CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brushes.White);
            var x=Math.Clamp(pendingPointer.X+14,4,Math.Max(4,Bounds.Width-text.Width-12));
            var y=Math.Clamp(pendingPointer.Y+18,4,Math.Max(4,Bounds.Height-text.Height-8));
            context.FillRectangle(new SolidColorBrush(Color.Parse("#233340")),new Rect(x-4,y-4,text.Width+8,text.Height+8));
            context.DrawText(text,new Point(x,y));
        }
        DrawOpeningGrips(context);
        if(Tool==PlanTool.Opening&&PlacementOpeningType!=null&&_lastPointer is Point placementPointer) {
            var hit=HitWall(placementPointer);
            if(hit.wall!=null) {
                var type=PlacementOpeningType;
                var opening=PlacementOpeningChoice!.CreateOpening(hit.wall.Id,hit.offset);
                var valid=hit.offset>=type.Width/2&&hit.offset+type.Width/2<=OpeningWallLength(hit.wall);
                DrawOpeningPreviewSymbols(context,hit.wall,opening,new Pen(valid?Brushes.Cyan:Brushes.OrangeRed,2),type);
            }
        }
        DrawSelectionPreview(context);
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
                selectedWall.Thickness * _scale);
            var body = PreviewWallBody(selectedWall);
            context.DrawLine(preview, body.first + new Vector(dx*_scale,-dy*_scale),
                body.second + new Vector(dx*_scale,-dy*_scale));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), 1),
                Screen(_moveBase.X, _moveBase.Y), Screen(_cursor.X, _cursor.Y));
        }
        if (_moving && _moveBase != null && _cursor != null
            && _slabGeometry.TryGetValue(_selectedId ?? "", out var movingSlab))
        {
            var dx = _cursor.X - _moveBase.X; var dy = _cursor.Y - _moveBase.Y;
            var pen = new Pen(new SolidColorBrush(Color.Parse("#65E8B2")), Stroke(25));
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
        if(AxisMode==AxisEditMode.Off && _lastPointer is Point cursor && _panStart == null) {
            var cursorPen=new Pen(new SolidColorBrush(Color.Parse(CrosshairColor)),1);
            var half=Math.Max(2,PickboxSize/2);
            var length=Math.Max(Bounds.Width,Bounds.Height)*CrosshairPercent/100/2;
            context.DrawRectangle(null,cursorPen,new Rect(cursor.X-half,cursor.Y-half,half*2,half*2));
            context.DrawLine(cursorPen,new Point(cursor.X-length,cursor.Y),new Point(cursor.X-half,cursor.Y));
            context.DrawLine(cursorPen,new Point(cursor.X+half,cursor.Y),new Point(cursor.X+length,cursor.Y));
            context.DrawLine(cursorPen,new Point(cursor.X,cursor.Y-length),new Point(cursor.X,cursor.Y-half));
            context.DrawLine(cursorPen,new Point(cursor.X,cursor.Y+half),new Point(cursor.X,cursor.Y+length));
        }
        DrawAxisEditOverlay(context);
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
        var gap = 700*_scale;
        DrawDimension(context, new Point(outerA.X, outerA.Y - gap), new Point(outerB.X, outerA.Y - gap),
            (outerB.X - outerA.X) / _scale, false);
        DrawDimension(context, new Point(outerB.X + gap, outerA.Y), new Point(outerB.X + gap, outerB.Y),
            (outerB.Y - outerA.Y) / _scale, true);
        foreach (var hole in slab.Openings ?? new())
        {
            var a = Screen(hole.Outline.Min(p => p.X), hole.Outline.Max(p => p.Y));
            var b = Screen(hole.Outline.Max(p => p.X), hole.Outline.Min(p => p.Y));
            var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var crossing = new Pen(new SolidColorBrush(Color.Parse("#506475")), Stroke());
            context.DrawLine(crossing, a, b);
            context.DrawLine(crossing, new Point(b.X, a.Y), new Point(a.X, b.Y));
            var text = new FormattedText(hole.Name ?? "洞口", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 250*_scale, Brushes.White);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#111A25")),
                new Rect(center.X - text.Width / 2 - 60*_scale, center.Y - text.Height / 2, text.Width + 120*_scale, text.Height));
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            DrawDimension(context, new Point(a.X, a.Y - 300*_scale), new Point(b.X, a.Y - 300*_scale),
                (b.X - a.X) / _scale, false);
            DrawDimension(context, new Point(a.X - 300*_scale, a.Y), new Point(a.X - 300*_scale, b.Y),
                (b.Y - a.Y) / _scale, true);
        }
    }

    private void DrawDimension(DrawingContext context, Point a, Point b, double mm, bool vertical)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#D4E0E8")), Stroke());
        context.DrawLine(pen, a, b);
        foreach (var p in new[] { a, b })
            context.DrawLine(pen, vertical ? new Point(p.X - 100*_scale, p.Y) : new Point(p.X, p.Y - 100*_scale),
                vertical ? new Point(p.X + 100*_scale, p.Y) : new Point(p.X, p.Y + 100*_scale));
        var dx = vertical ? 0 : 80*_scale; var dy = vertical ? 80*_scale : 0;
        context.DrawLine(pen, a, new Point(a.X + dx + dy, a.Y + dy - dx));
        context.DrawLine(pen, a, new Point(a.X + dx - dy, a.Y + dy + dx));
        context.DrawLine(pen, b, new Point(b.X - dx + dy, b.Y - dy - dx));
        context.DrawLine(pen, b, new Point(b.X - dx - dy, b.Y - dy + dx));
        var text = new FormattedText(mm.ToString("0.##", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 250*_scale, Brushes.White);
        if (vertical)
        {
            using var transform = context.PushTransform(Matrix.CreateRotation(-Math.PI / 2)
                * Matrix.CreateTranslation((a.X + b.X) / 2 - 200*_scale, (a.Y + b.Y) / 2));
            context.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
        }
        else context.DrawText(text, new Point((a.X + b.X) / 2 - text.Width / 2, a.Y - text.Height - 50*_scale));
    }

    private void DrawAxes(DrawingContext context)
    {
        var brush=new SolidColorBrush(Color.Parse("#6A9AA8"));
        var pen=new Pen(brush,Stroke(10),new DashStyle(new[] { 10d,4d,2d,4d },0));
        var labels=new SolidColorBrush(Color.Parse("#B3D8E1"));
        foreach(var endpoints in AxisEndpoints()) {
            var axis=endpoints.Axis;
            if(axis.Hidden||axis.Deleted)continue;
            var a=Screen(endpoints.Start.X,endpoints.Start.Y);var b=Screen(endpoints.End.X,endpoints.End.Y);
            context.DrawLine(pen,a,b);
            if(string.IsNullOrWhiteSpace(axis.Name))continue;
            if(!axis.StartHidden&&!axis.StartRemoved) DrawAxisBubble(context,axis.StartName ?? axis.Name,a,labels);
            if(!axis.EndHidden&&!axis.EndRemoved) DrawAxisBubble(context,axis.EndName ?? axis.Name,b,labels);
        }
    }

    private IReadOnlyList<(AxisModel Axis,PointModel Start,PointModel End)> AxisEndpoints()
    {
        if(_axisEndpointCache!=null)return _axisEndpointCache;
        _axisEndpointCache=new();
        foreach(var placement in BuildingAxisLayout.Layout(_model,_storeyId,4500,AxisBubbleRadius,includeGhosts:AxisMode!=AxisEditMode.Off))
            if(AxisMode!=AxisEditMode.Off||(!placement.Axis.Hidden&&!placement.Axis.Deleted))
                _axisEndpointCache.Add((placement.Axis,placement.Start,placement.End));
        return _axisEndpointCache;
    }

    private void DrawOrthogonalJunctions(DrawingContext context)
    {
        foreach (var (horizontal, vertical) in _orthogonalJunctions)
        {
            if(!Visible(horizontal.Id)||!Visible(vertical.Id))continue;
            if (_gripPreview != null && _gripWallId != null
                && (horizontal.Id == _gripWallId || vertical.Id == _gripWallId)) continue;
            var horizontalBody = PreviewWallBody(horizontal);
            var verticalBody = PreviewWallBody(vertical);
            var x = (verticalBody.first.X + verticalBody.second.X) / 2;
            var y = (horizontalBody.first.Y + horizontalBody.second.Y) / 2;
            var width = vertical.Thickness * _scale;
            var height = horizontal.Thickness * _scale;
            var color = ShowSelectionPreview && (_preselectedId==horizontal.Id || _preselectedId==vertical.Id) ? "#00FFFF"
                : _selectedIds.Contains(horizontal.Id) || _selectedIds.Contains(vertical.Id) ? "#FFC46B"
                : _slabGeometry.ContainsKey(_selectedId ?? "") ? "#334652" : "#9BC4E9";
            context.FillRectangle(new SolidColorBrush(Color.Parse(color)),
                new Rect(x - width / 2, y - height / 2, width, height));
        }
    }

    private void IndexOrthogonalJunctions()
    {
        _orthogonalJunctions.Clear();
        var walls = _model.Walls.Where(w => w.StoreyId == _storeyId && Visible(w.Id)).ToArray();
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

    private void DrawAxisBubble(DrawingContext context, string name, Point center, IBrush brush)
    {
        var pen = new Pen(brush, Stroke(15));
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#111A25")), pen, center, AxisBubbleRadius*_scale, AxisBubbleRadius*_scale);
        var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, DrawingAnnotationSettings.Resolve(_model).TextHeight*AxisAnnotationScale*_scale, brush);
        var width=DrawingAnnotationSettings.Resolve(_model).WidthFactor;
        width=Math.Min(width,AxisBubbleRadius*_scale*1.6/Math.Max(1,text.Width));
        using(context.PushTransform(Matrix.CreateScale(width,1)*Matrix.CreateTranslation(center.X,center.Y)))
            context.DrawText(text, new Point(-text.Width/2,-text.Height/2));
    }
    private int AxisAnnotationScale => _model.DrawingScales?.Plan ?? 100;
    private double AxisBubbleRadius => DrawingAnnotationSettings.Resolve(_model).AxisDiameter*AxisAnnotationScale/2;
}
