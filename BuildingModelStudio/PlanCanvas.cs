using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 平面草图编辑器（P1.5 的核心）。
    ///
    /// 画布本身就是模型：画的墙就是模型里的墙，不需要"提取"再反推。
    /// 交互（选中/夹点/捕捉/洞口定位）全部走 <see cref="PlanEditing"/> 的纯逻辑，
    /// 这样规则可单元测试，将来 CAD 侧的构件化绘制也能复用同一套。
    /// </summary>
    internal sealed class PlanCanvas : Control
    {
        private enum DragMode { None, Pan, Grip, MoveWall, MoveOpening, MoveColumn, DrawWall }

        private readonly ModelEditHistory _history = new ModelEditHistory();
        private BuildingModelDocument _model;
        private string _storeyId;
        private string _tool = "select";
        private PlanHit _selection;
        private DragMode _drag = DragMode.None;
        private Point _lastMouse;
        private double _dragStartX, _dragStartY;
        private double _dragOriginX1, _dragOriginY1, _dragOriginX2, _dragOriginY2, _dragOriginOffset;
        private double? _drawFromX, _drawFromY;
        private double _cursorX, _cursorY;
        private string _snapKind = PlanEditing.SnapNone;

        private double _scale = 0.08d;          // 屏幕像素 / 模型毫米（0.08 ≈ 1:100 下 1px≈12.5mm）
        private double _offsetX = 60d, _offsetY = 60d;
        private bool _dirty;

        public event Action<string> StatusChanged;
        public event Action SelectionChanged;
        public event Action StructureChanged;
        /// <summary>画布主动请求保存（例如一次操作结束）。</summary>
        public event Action SaveRequested;

        public PlanCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.FromArgb(24, 26, 30);
            Cursor = Cursors.Cross;
        }

        public BuildingModelDocument Model
        {
            get { return _model; }
            set
            {
                _model = value;
                _selection = null;
                if (_model != null) _history.Reset(_model);
                ZoomExtents();
                Invalidate();
                RaiseStatus();
            }
        }

        public ModelEditHistory History { get { return _history; } }
        public PlanHit Selection { get { return _selection; } }
        public bool IsDirty { get { return _dirty; } }

        public string StoreyId
        {
            get { return _storeyId; }
            set { _storeyId = value; _selection = null; Invalidate(); RaiseStatus(); SelectionChanged?.Invoke(); }
        }

        public string Tool
        {
            get { return _tool; }
            set
            {
                _tool = value ?? "select";
                _drawFromX = _drawFromY = null;
                Cursor = _tool == "select" ? Cursors.Default : Cursors.Cross;
                RaiseStatus();
                Invalidate();
            }
        }

        /// <summary>当前工具下新建洞口用的尺寸（来自右侧面板）。</summary>
        public string OpeningKind = "窗";
        public double OpeningWidth = 1500d, OpeningHeight = 1800d, OpeningSill = 900d;
        public double DefaultWallThickness = 200d, DefaultWallHeight = 0d, DefaultColumnSize = 400d;

        /// <summary>当前选中的门窗类型（来自类型库）；不为空时放门窗直接套用它的编号与尺寸。</summary>
        public OpeningTypeModel CurrentType;

        // ───────────────────────── 视图变换 ─────────────────────────

        /// <summary>交给 GDI+ 的屏幕坐标上限（GDI+ 超过 int32 就抛 OverflowException，实测 1e9 可以、2e9 就抛）。</summary>
        private const double MaxScreenCoordinate = 1e8d;

        private PointF ToScreen(double x, double y)
        {
            return new PointF((float)ClampScreen(_offsetX + x * _scale), (float)ClampScreen(_offsetY - y * _scale));
        }

        /// <summary>
        /// 任何屏幕坐标都先夹进 GDI+ 能接受的范围：
        /// GDI+ 的 DrawLine 拿到 ±∞ / NaN / 超过 1e9 的坐标就抛 System.OverflowException（"Overflow error."）。
        /// 屏幕外的构件被夹到边界上，反正也看不见。
        /// </summary>
        private static double ClampScreen(double screen)
        {
            if (double.IsNaN(screen)) return 0d;
            if (screen > MaxScreenCoordinate) return MaxScreenCoordinate;
            if (screen < -MaxScreenCoordinate) return -MaxScreenCoordinate;
            return screen;
        }

        private void ToModel(Point screen, out double x, out double y)
        {
            x = (screen.X - _offsetX) / _scale;
            y = (_offsetY - screen.Y) / _scale;
        }

        /// <summary>视图变换还能不能用（比例非有限、偏移非有限都会让整块画布画不出来）。</summary>
        private bool ViewportUsable()
        {
            return PlanGrid.IsFinite(_scale) && _scale > 0d
                && PlanGrid.IsFinite(_offsetX) && PlanGrid.IsFinite(_offsetY);
        }

        /// <summary>
        /// 能安全交给 GDI+ 的模型坐标：有限、而且不超出合理范围。
        /// 模型是从 json 读进来的，文件坏掉或坐标被改成天文数字时，这里挡住就不会再抛 OverflowException。
        /// </summary>
        private static bool Sane(double x, double y)
        {
            return PlanGrid.IsFinite(x) && PlanGrid.IsFinite(y)
                && Math.Abs(x) <= 1e9d && Math.Abs(y) <= 1e9d;
        }

        public void ZoomExtents()
        {
            if (_model == null || Width <= 0 || Height <= 0) return;
            var points = new List<PointModel>();
            foreach (var wall in _model.Walls ?? new List<WallModel>())
            {
                points.Add(new PointModel(wall.X1, wall.Y1));
                points.Add(new PointModel(wall.X2, wall.Y2));
            }
            foreach (var column in _model.Columns ?? new List<ColumnModel>())
                points.Add(new PointModel(column.X, column.Y));
            foreach (var slab in _model.Slabs ?? new List<SlabModel>())
                foreach (var point in slab.Outline ?? new List<PointModel>()) points.Add(point);
            // 坏数据（NaN / ∞）不能参与取范围，否则比例会变成 NaN，整块画布都画不出来
            points = points.Where(p => p != null && Sane(p.X, p.Y)).ToList();
            if (points.Count == 0)
            {
                _scale = 0.08d;
                _offsetX = 60d;
                _offsetY = Height > 0 ? Height - 60d : 60d;
                return;
            }
            var minX = points.Min(p => p.X) - 2000d;
            var maxX = points.Max(p => p.X) + 2000d;
            var minY = points.Min(p => p.Y) - 2000d;
            var maxY = points.Max(p => p.Y) + 2000d;
            var scaleX = Width / Math.Max(1d, maxX - minX);
            var scaleY = Height / Math.Max(1d, maxY - minY);
            _scale = Math.Max(0.002d, Math.Min(scaleX, scaleY));
            _offsetX = -minX * _scale + (Width - (maxX - minX) * _scale) / 2d;
            _offsetY = maxY * _scale + (Height - (maxY - minY) * _scale) / 2d;
        }

        private void ZoomAt(Point screen, double factor)
        {
            ToModel(screen, out var beforeX, out var beforeY);
            _scale = Math.Max(0.002d, Math.Min(2d, _scale * factor));
            ToModel(screen, out var afterX, out var afterY);
            _offsetX += (afterX - beforeX) * _scale;
            _offsetY -= (afterY - beforeY) * _scale;
            Invalidate();
            RaiseStatus();
        }

        // ───────────────────────── 绘制 ─────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            // 绘制出错绝不能把整个程序带走（历史上出过一次 OverflowException 弹"未经处理的异常"）。
            SafeRender(e.Graphics);
        }

        /// <summary>带兜底的绘制：出错只提示，不抛给 WinForms（画布自检也走这里）。</summary>
        internal void SafeRender(Graphics g)
        {
            try
            {
                Render(g);
                _lastPaintError = null;
            }
            catch (Exception exception)
            {
                ReportPaintError(g, exception);
            }
        }

        /// <summary>把当前视图画到任意 Graphics（画布自检、以后做缩略图都用同一个入口）。</summary>
        internal void Render(Graphics g)
        {
            if (g == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (_model == null) { DrawHint(g, "还没有模型：点右侧「新建样例模型」或「打开模型」"); return; }
            if (!ViewportUsable())
            {
                // 比例/偏移坏掉时自愈：恢复默认视图，下一次绘制就正常了
                DrawHint(g, "视图变换异常，已恢复默认视图。");
                ZoomExtents();
                return;
            }

            DrawGrid(g);
            DrawSlabs(g);
            DrawWalls(g);
            DrawColumns(g);
            DrawOpenings(g);
            DrawPreview(g);
            DrawGrips(g);
        }

        /// <summary>自检用：直接摆好视图变换，不走鼠标。</summary>
        internal void SetViewport(double scale, double offsetX, double offsetY)
        {
            _scale = scale;
            _offsetX = offsetX;
            _offsetY = offsetY;
            Invalidate();
        }

        /// <summary>上一次绘制失败的原因；自检拿它判断有没有被兜底救下来。</summary>
        internal string LastPaintError { get { return _lastPaintError; } }

        /// <summary>自检用：当前视图比例（像素/毫米）。</summary>
        internal double ViewScale { get { return _scale; } }

        private string _lastPaintError;

        private void ReportPaintError(Graphics g, Exception exception)
        {
            var message = exception.GetType().Name + "：" + exception.Message;
            if (!string.Equals(message, _lastPaintError, StringComparison.Ordinal))
            {
                _lastPaintError = message;
                StatusChanged?.Invoke("显示出错，已跳过本次绘制：" + message);
            }
            try
            {
                g.Clear(BackColor);
                DrawHint(g, "显示出错，已跳过本次绘制：" + message);
            }
            catch
            {
                // 连提示都画不出来就只能算了，别再抛一次
            }
        }

        private void DrawGrid(Graphics g)
        {
            // 轴网/网格：1000mm 淡线，每 5 条稍亮。
            // 线的位置由 PlanGrid 算好（纯逻辑、有数量上限、绝不产生 ∞ / NaN），这里只管画。
            var lines = PlanGrid.Compute(Width, Height, _scale, _offsetX, _offsetY);
            if (lines.Count == 0) return;
            using (var thin = new Pen(Color.FromArgb(38, 42, 48)))
            using (var thick = new Pen(Color.FromArgb(58, 64, 72)))
            {
                foreach (var line in lines)
                {
                    var pen = line.Major ? thick : thin;
                    if (line.Vertical) g.DrawLine(pen, line.Screen, 0f, line.Screen, Height);
                    else g.DrawLine(pen, 0f, line.Screen, Width, line.Screen);
                }
            }
        }

        private void DrawSlabs(Graphics g)
        {
            using (var fill = new SolidBrush(Color.FromArgb(52, 50, 58)))
            foreach (var slab in (_model.Slabs ?? new List<SlabModel>()).Where(s => Same(s.StoreyId, _storeyId)))
            {
                var outline = slab.Outline ?? new List<PointModel>();
                if (outline.Count < 3) continue;
                if (outline.Any(p => p == null || !Sane(p.X, p.Y))) continue;
                var points = outline.Select(p => ToScreen(p.X, p.Y)).ToArray();
                g.FillPolygon(fill, points);
            }
        }

        private void DrawWalls(Graphics g)
        {
            foreach (var wall in (_model.Walls ?? new List<WallModel>()).Where(w => Same(w.StoreyId, _storeyId)))
            {
                if (!Sane(wall.X1, wall.Y1) || !Sane(wall.X2, wall.Y2)) continue;
                var selected = _selection != null && _selection.Kind == "wall" && Same(_selection.Id, wall.Id);
                var corners = WallCorners(wall);
                using (var brush = new SolidBrush(selected ? Color.FromArgb(96, 160, 210) : Color.FromArgb(150, 156, 166)))
                    g.FillPolygon(brush, corners.Select(p => ToScreen(p.X, p.Y)).ToArray());
            }
        }

        private static List<PointModel> WallCorners(WallModel wall)
        {
            PlanEditing.WallDirection(wall, out var ux, out var uy);
            var half = Math.Max(1d, wall.Thickness) / 2d;
            var nx = -uy * half;
            var ny = ux * half;
            return new List<PointModel>
            {
                new PointModel(wall.X1 + nx, wall.Y1 + ny),
                new PointModel(wall.X2 + nx, wall.Y2 + ny),
                new PointModel(wall.X2 - nx, wall.Y2 - ny),
                new PointModel(wall.X1 - nx, wall.Y1 - ny)
            };
        }

        private void DrawColumns(Graphics g)
        {
            foreach (var column in (_model.Columns ?? new List<ColumnModel>()).Where(c => Same(c.StoreyId, _storeyId)))
            {
                if (!Sane(column.X, column.Y)) continue;
                var selected = _selection != null && _selection.Kind == "column" && Same(_selection.Id, column.Id);
                var halfW = Math.Max(1d, column.Width) / 2d;
                var halfD = Math.Max(1d, column.Depth) / 2d;
                var points = new[]
                {
                    ToScreen(column.X - halfW, column.Y - halfD),
                    ToScreen(column.X + halfW, column.Y - halfD),
                    ToScreen(column.X + halfW, column.Y + halfD),
                    ToScreen(column.X - halfW, column.Y + halfD)
                };
                using (var brush = new SolidBrush(selected ? Color.FromArgb(220, 170, 90) : Color.FromArgb(110, 116, 126)))
                    g.FillPolygon(brush, points);
            }
        }

        private void DrawOpenings(Graphics g)
        {
            foreach (var opening in _model.Openings ?? new List<OpeningModel>())
            {
                var wall = (_model.Walls ?? new List<WallModel>())
                    .FirstOrDefault(w => w != null && Same(w.Id, opening.HostWallId) && Same(w.StoreyId, _storeyId));
                if (wall == null) continue;
                var selected = _selection != null && _selection.Kind == "opening" && Same(_selection.Id, opening.Id);
                double ax, ay, bx, by;
                PlanEditing.OpeningSpan(wall, opening, out ax, out ay, out bx, out by);
                if (!Sane(ax, ay) || !Sane(bx, by)) continue;
                PlanEditing.WallDirection(wall, out var ux, out var uy);
                var half = Math.Max(1d, wall.Thickness) / 2d;
                var nx = -uy * half;
                var ny = ux * half;
                var points = new[]
                {
                    ToScreen(ax + nx, ay + ny), ToScreen(bx + nx, by + ny),
                    ToScreen(bx - nx, by - ny), ToScreen(ax - nx, ay - ny)
                };
                using (var fill = new SolidBrush(Color.FromArgb(30, 33, 38)))
                    g.FillPolygon(fill, points);
                using (var pen = new Pen(selected ? Color.FromArgb(255, 210, 120) : Color.FromArgb(120, 200, 170), 1.4f))
                    g.DrawPolygon(pen, points);
                // 洞口编号
                PlanEditing.OpeningCentre(wall, opening, out var cx, out var cy);
                var text = string.IsNullOrWhiteSpace(opening.Code) ? opening.Kind : opening.Code;
                if (text != null && _scale > 0.02d)
                    using (var font = new Font("Microsoft YaHei UI", 7.5f))
                    using (var brush = new SolidBrush(Color.FromArgb(180, 220, 200)))
                    {
                        var size = g.MeasureString(text, font);
                        g.DrawString(text, font, brush, ToScreen(cx, cy).X - size.Width / 2f, ToScreen(cx, cy).Y - size.Height - 4f);
                    }
            }
        }

        private void DrawPreview(Graphics g)
        {
            if (!_drawFromX.HasValue) return;
            var from = ToScreen(_drawFromX.Value, _drawFromY ?? 0d);
            var to = ToScreen(_cursorX, _cursorY);
            using (var pen = new Pen(Color.FromArgb(120, 200, 255), 1.6f) { DashStyle = DashStyle.Dash })
                g.DrawLine(pen, from, to);
            var length = Math.Sqrt((_cursorX - _drawFromX.Value) * (_cursorX - _drawFromX.Value)
                + (_cursorY - (_drawFromY ?? 0d)) * (_cursorY - (_drawFromY ?? 0d)));
            using (var font = new Font("Microsoft YaHei UI", 8f))
            using (var brush = new SolidBrush(Color.FromArgb(160, 220, 255)))
                g.DrawString(Math.Round(length) + " mm", font, brush, to.X + 8, to.Y + 8);
        }

        private void DrawGrips(Graphics g)
        {
            if (_selection == null) return;
            var grips = new List<PointModel>();
            if (_selection.Kind == "wall")
            {
                var wall = FindWall(_selection.Id);
                if (wall == null) return;
                grips.Add(new PointModel(wall.X1, wall.Y1));
                grips.Add(new PointModel(wall.X2, wall.Y2));
                grips.Add(new PointModel((wall.X1 + wall.X2) / 2d, (wall.Y1 + wall.Y2) / 2d));
            }
            else if (_selection.Kind == "column")
            {
                var column = (_model.Columns ?? new List<ColumnModel>()).FirstOrDefault(c => c != null && Same(c.Id, _selection.Id));
                if (column == null) return;
                grips.Add(new PointModel(column.X, column.Y));
            }
            using (var brush = new SolidBrush(Color.FromArgb(255, 210, 120)))
            foreach (var grip in grips)
            {
                var point = ToScreen(grip.X, grip.Y);
                g.FillRectangle(brush, point.X - 4, point.Y - 4, 8, 8);
            }
        }

        private void DrawHint(Graphics g, string text)
        {
            using (var font = new Font("Microsoft YaHei UI", 10f))
            using (var brush = new SolidBrush(Color.FromArgb(150, 156, 166)))
                g.DrawString(text, font, brush, 20, 20);
        }

        // ───────────────────────── 鼠标与键盘 ─────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            ToModel(e.Location, out var x, out var y);

            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _drag = DragMode.Pan;
                return;
            }
            if (e.Button != MouseButtons.Left || _model == null) return;

            var snap = PlanEditing.Snap(_model, _storeyId, x, y, 12d / _scale, _drawFromX.HasValue,
                _drawFromX ?? 0d, _drawFromY ?? 0d, 100d);
            _cursorX = snap.X;
            _cursorY = snap.Y;
            _snapKind = snap.Kind;

            switch (_tool)
            {
                case "wall":
                    if (!_drawFromX.HasValue) { _drawFromX = snap.X; _drawFromY = snap.Y; }
                    else
                    {
                        var wall = new WallModel
                        {
                            Id = NewId("W"), StoreyId = _storeyId,
                            X1 = _drawFromX.Value, Y1 = _drawFromY.Value, X2 = snap.X, Y2 = snap.Y,
                            Thickness = DefaultWallThickness, Height = DefaultWallHeight
                        };
                        var error = PlanEditing.ValidateWall(wall);
                        if (error != null) { StatusChanged?.Invoke("提示：" + error); }
                        else
                        {
                            _model.Walls.Add(wall);
                            _selection = new PlanHit { Kind = "wall", Id = wall.Id };
                            Commit("画墙");
                            _drawFromX = snap.X; _drawFromY = snap.Y;      // 连续画墙
                        }
                    }
                    break;
                case "window":
                case "door":
                {
                    var hit = PlanEditing.HitTest(_model, _storeyId, snap.X, snap.Y, 12d / _scale);
                    var wall = hit != null && hit.Kind == "wall" ? FindWall(hit.Id)
                        : (_model.Walls ?? new List<WallModel>()).FirstOrDefault(w => Same(w.StoreyId, _storeyId)
                            && PlanEditing.DistanceToSegment(snap.X, snap.Y, w.X1, w.Y1, w.X2, w.Y2) <= Math.Max(1d, w.Thickness) / 2d + 12d / _scale);
                    if (wall == null) { StatusChanged?.Invoke("请点在一道墙上放门窗。"); break; }
                    OpeningKind = _tool == "door" ? "门" : "窗";
                    var opening = PlanEditing.CreateOpening(OpeningKind, wall.Id, PlanEditing.ProjectOnWall(wall, snap.X, snap.Y));
                    opening.Id = NewId("O");
                    opening.Width = OpeningWidth;
                    opening.Height = OpeningHeight;
                    opening.Sill = _tool == "door" ? 0d : OpeningSill;
                    opening.Code = _tool == "door" ? "M" + Math.Round(opening.Width) : "C" + Math.Round(opening.Width);
                    if (CurrentType != null) PlanEditing.ApplyType(opening, CurrentType);   // 类型库优先
                    var error = PlanEditing.ValidateOpening(_model, wall, opening);
                    if (error != null) { StatusChanged?.Invoke("提示：" + error); break; }
                    _model.Openings.Add(opening);
                    _selection = new PlanHit { Kind = "opening", Id = opening.Id, Offset = opening.Offset };
                    Commit("放" + (opening.Code ?? OpeningKind));
                    break;
                }
                case "column":
                {
                    var column = new ColumnModel
                    {
                        Id = NewId("K"), StoreyId = _storeyId,
                        X = snap.X, Y = snap.Y, Width = DefaultColumnSize, Depth = DefaultColumnSize
                    };
                    _model.Columns.Add(column);
                    _selection = new PlanHit { Kind = "column", Id = column.Id, Grip = 0 };
                    Commit("布柱");
                    break;
                }
                default:
                {
                    var hit = PlanEditing.HitTest(_model, _storeyId, snap.X, snap.Y, 10d / _scale);
                    _selection = hit;
                    SelectionChanged?.Invoke();
                    if (hit == null) { Invalidate(); break; }
                    _dragStartX = x; _dragStartY = y;
                    var wall = hit.Kind == "wall" ? FindWall(hit.Id) : null;
                    if (wall != null)
                    {
                        _dragOriginX1 = wall.X1; _dragOriginY1 = wall.Y1; _dragOriginX2 = wall.X2; _dragOriginY2 = wall.Y2;
                        _drag = hit.Grip >= 0 ? DragMode.Grip : DragMode.MoveWall;
                    }
                    else if (hit.Kind == "opening")
                    {
                        var opening = (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null && Same(o.Id, hit.Id));
                        if (opening != null) { _dragOriginOffset = opening.Offset; _drag = DragMode.MoveOpening; }
                    }
                    else if (hit.Kind == "column")
                    {
                        var column = (_model.Columns ?? new List<ColumnModel>()).FirstOrDefault(c => c != null && Same(c.Id, hit.Id));
                        if (column != null) { _dragOriginX1 = column.X; _dragOriginY1 = column.Y; _drag = DragMode.MoveColumn; }
                    }
                    Invalidate();
                    RaiseStatus();
                    break;
                }
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ToModel(e.Location, out var rawX, out var rawY);
            if (_drag == DragMode.Pan)
            {
                _offsetX += e.X - _lastMouse.X;
                _offsetY += e.Y - _lastMouse.Y;
                _lastMouse = e.Location;
                Invalidate();
                return;
            }
            var snap = PlanEditing.Snap(_model, _storeyId, rawX, rawY, 12d / _scale, _drawFromX.HasValue,
                _drawFromX ?? 0d, _drawFromY ?? 0d, 100d);
            _cursorX = snap.X;
            _cursorY = snap.Y;
            _snapKind = snap.Kind;

            if (_drag != DragMode.None && _drag != DragMode.DrawWall && _model != null)
            {
                var deltaX = _cursorX - _dragStartX;
                var deltaY = _cursorY - _dragStartY;
                if (_drag == DragMode.MoveWall)
                {
                    var wall = FindWall(_selection.Id);
                    if (wall != null)
                    {
                        wall.X1 = _dragOriginX1 + deltaX; wall.Y1 = _dragOriginY1 + deltaY;
                        wall.X2 = _dragOriginX2 + deltaX; wall.Y2 = _dragOriginY2 + deltaY;
                    }
                }
                else if (_drag == DragMode.Grip)
                {
                    var wall = FindWall(_selection.Id);
                    if (wall != null)
                    {
                        if (_selection.Grip == 0) { wall.X1 = _cursorX; wall.Y1 = _cursorY; }
                        else if (_selection.Grip == 1) { wall.X2 = _cursorX; wall.Y2 = _cursorY; }
                        else
                        {
                            // 中点夹点 = 整道墙平移
                            wall.X1 = _dragOriginX1 + deltaX; wall.Y1 = _dragOriginY1 + deltaY;
                            wall.X2 = _dragOriginX2 + deltaX; wall.Y2 = _dragOriginY2 + deltaY;
                        }
                    }
                }
                else if (_drag == DragMode.MoveOpening)
                {
                    var opening = (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null && Same(o.Id, _selection.Id));
                    var wall = opening == null ? null : FindWall(opening.HostWallId);
                    if (opening != null && wall != null)
                    {
                        // 洞口只能沿墙滑动，并且不能滑出墙端
                        var length = PlanEditing.WallLength(wall);
                        opening.Offset = Math.Max(opening.Width / 2d, Math.Min(length - opening.Width / 2d,
                            PlanEditing.ProjectOnWall(wall, _cursorX, _cursorY)));
                    }
                }
                else if (_drag == DragMode.MoveColumn)
                {
                    var column = (_model.Columns ?? new List<ColumnModel>()).FirstOrDefault(c => c != null && Same(c.Id, _selection.Id));
                    if (column != null) { column.X = _dragOriginX1 + deltaX; column.Y = _dragOriginY1 + deltaY; }
                }
                Invalidate();
                RaiseStatus();
                return;
            }
            Invalidate();
            RaiseStatus();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag != DragMode.None && _drag != DragMode.Pan && _drag != DragMode.DrawWall)
            {
                var label = _drag == DragMode.Grip ? "改墙端点" : _drag == DragMode.MoveWall ? "移动墙"
                    : _drag == DragMode.MoveOpening ? "移动洞口" : "移动柱";
                Commit(label);
            }
            if (_drag == DragMode.Pan || _drag != DragMode.None) _drag = DragMode.None;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomAt(e.Location, e.Delta > 0 ? 1.15d : 1d / 1.15d);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Escape || keyData == Keys.Delete) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                _drawFromX = _drawFromY = null;
                _selection = null;
                SelectionChanged?.Invoke();
                Invalidate();
                StatusChanged?.Invoke("已取消当前操作。");
                return;
            }
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelection();
                return;
            }
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); return; }
            if (e.Control && e.KeyCode == Keys.Y) { Redo(); return; }
            if (e.Control && e.KeyCode == Keys.A) { ZoomExtents(); Invalidate(); return; }
        }

        // ───────────────────────── 对外操作 ─────────────────────────

        public void DeleteSelection()
        {
            if (_selection == null || _model == null) { StatusChanged?.Invoke("先选中一个构件再删除。"); return; }
            var removed = false;
            if (_selection.Kind == "wall")
            {
                var wall = FindWall(_selection.Id);
                if (wall != null)
                {
                    _model.Openings.RemoveAll(o => o != null && Same(o.HostWallId, wall.Id));   // 墙上的洞口一起删
                    _model.Walls.Remove(wall);
                    removed = true;
                }
            }
            else if (_selection.Kind == "opening") removed = _model.Openings.RemoveAll(o => o != null && Same(o.Id, _selection.Id)) > 0;
            else if (_selection.Kind == "column") removed = _model.Columns.RemoveAll(c => c != null && Same(c.Id, _selection.Id)) > 0;
            if (!removed) { StatusChanged?.Invoke("没找到要删除的构件。"); return; }
            _selection = null;
            SelectionChanged?.Invoke();
            Commit("删除");
        }

        public void Undo()
        {
            var restored = _history.Undo(_model);
            if (ReferenceEquals(restored, _model)) { StatusChanged?.Invoke("没有可撤销的操作。"); return; }
            _model = restored;
            _selection = null;
            StructureChanged?.Invoke();
            SelectionChanged?.Invoke();
            Invalidate();
            StatusChanged?.Invoke("已撤销（" + _history.Position + "/" + _history.Count + "）。");
            SaveRequested?.Invoke();
        }

        public void Redo()
        {
            var restored = _history.Redo(_model);
            if (ReferenceEquals(restored, _model)) { StatusChanged?.Invoke("没有可重做的操作。"); return; }
            _model = restored;
            _selection = null;
            StructureChanged?.Invoke();
            SelectionChanged?.Invoke();
            Invalidate();
            StatusChanged?.Invoke("已重做（" + _history.Position + "/" + _history.Count + "）。");
            SaveRequested?.Invoke();
        }

        private void Commit(string label)
        {
            _history.Push(_model);
            _dirty = true;
            StructureChanged?.Invoke();
            SelectionChanged?.Invoke();
            SaveRequested?.Invoke();
            StatusChanged?.Invoke(label + "完成（可 Ctrl+Z 撤销）。");
        }

        public void MarkSaved()
        {
            _dirty = false;
        }

        public WallModel FindWall(string id)
        {
            return (_model == null ? new List<WallModel>() : _model.Walls ?? new List<WallModel>())
                .FirstOrDefault(w => w != null && Same(w.Id, id));
        }

        private static string NewId(string prefix)
        {
            return prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private void RaiseStatus()
        {
            var text = "坐标 " + Math.Round(_cursorX) + ", " + Math.Round(_cursorY) + " mm"
                + "　捕捉：" + _snapKind
                + "　比例 1px≈" + Math.Round(1d / _scale, 1) + "mm";
            StatusChanged?.Invoke(text);
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
