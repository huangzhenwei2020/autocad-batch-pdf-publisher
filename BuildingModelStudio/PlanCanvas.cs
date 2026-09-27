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
        private enum DragMode { None, Pan, Grip, MoveWall, MoveOpening, MoveColumn, DrawWall, DrawRoom, MoveAxis, MoveRoom, MoveStair, MoveRoof }

        private readonly ModelEditHistory _history = new ModelEditHistory();
        private BuildingModelDocument _model;
        private string _storeyId;
        private string _tool = "select";
        private PlanHit _selection;
        private DragMode _drag = DragMode.None;
        /// <summary>闭合当前房间草稿：至少 3 个点才建房间（名字先给个默认的，可在属性面板改）。</summary>
        public void FinishRoomDraft()
        {
            var points = _roomDraft;
            _roomDraft = null;
            if (points == null || points.Count < 3)
            {
                StatusChanged?.Invoke("房间轮廓至少 3 个点，已取消。");
                Invalidate();
                return;
            }
            var room = new RoomModel
            {
                Id = NewId("R"), StoreyId = _storeyId,
                Name = "房间" + ((_model.Rooms ?? new List<RoomModel>()).Count + 1),
                Outline = points
            };
            var error = PlanEditing.ValidateRoom(room);
            if (error != null) { StatusChanged?.Invoke("提示：" + error); Invalidate(); return; }
            _model.Rooms.Add(room);
            _selection = new PlanHit { Kind = "room", Id = room.Id };
            Commit("画房间 " + room.Name);
            StatusChanged?.Invoke("已建房间 " + room.Name + "（面积 " + room.AreaSquareMetres.ToString("0.00") + " m²，名称可在属性面板改）。");
        }

        private void DrawAxes(Graphics g)
        {
            if (_model == null) return;
            var axes = _model.Axes ?? new List<AxisModel>();
            if (axes.Count == 0) return;
            using (var pen = new Pen(Color.FromArgb(200, 160, 230), 1.1f) { DashStyle = DashStyle.DashDot })
            using (var bubble = new Pen(Color.FromArgb(200, 160, 230), 1.1f))
            using (var font = new Font("Microsoft YaHei UI", 8f))
            using (var brush = new SolidBrush(Color.FromArgb(220, 200, 240)))
            {
                foreach (var axis in axes.Where(a => a != null && Sane(a.Position, a.Position)))
                {
                    var margin = 1200d;
                    PointF from, to;
                    if (axis.Vertical)
                    {
                        var start = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d ? Math.Min(axis.ExtentStart, axis.ExtentEnd) : double.NaN;
                        var end = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d ? Math.Max(axis.ExtentStart, axis.ExtentEnd) : double.NaN;
                        if (double.IsNaN(start))
                        {
                            var ys = (_model.Walls ?? new List<WallModel>()).Where(w => w != null).SelectMany(w => new[] { w.Y1, w.Y2 }).ToList();
                            start = (ys.Count == 0 ? 0d : ys.Min()) - margin;
                            end = (ys.Count == 0 ? 0d : ys.Max()) + margin;
                        }
                        from = ToScreen(axis.Position, start);
                        to = ToScreen(axis.Position, end);
                    }
                    else
                    {
                        var start = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d ? Math.Min(axis.ExtentStart, axis.ExtentEnd) : double.NaN;
                        var end = axis.ExtentStart > 0.5d || axis.ExtentEnd > 0.5d ? Math.Max(axis.ExtentStart, axis.ExtentEnd) : double.NaN;
                        if (double.IsNaN(start))
                        {
                            var xs = (_model.Walls ?? new List<WallModel>()).Where(w => w != null).SelectMany(w => new[] { w.X1, w.X2 }).ToList();
                            start = (xs.Count == 0 ? 0d : xs.Min()) - margin;
                            end = (xs.Count == 0 ? 0d : xs.Max()) + margin;
                        }
                        from = ToScreen(start, axis.Position);
                        to = ToScreen(end, axis.Position);
                    }
                    var selected = _selection != null && _selection.Kind == "axis" && Same(_selection.Id, axis.Id);
                    pen.Color = selected ? Color.FromArgb(255, 210, 120) : Color.FromArgb(200, 160, 230);
                    DrawClippedLine(g, pen, from, to);
                    // 轴号圆圈（画个圆 + 轴号）
                    var label = axis.Name ?? "?";
                    foreach (var point in new[] { from, to })
                    {
                        var extent = axis.Vertical
                            ? (point.Y <= from.Y ? -1 : 1)
                            : (point.X <= from.X ? -1 : 1);
                        var centerX = axis.Vertical ? point.X : point.X + extent * 700f;
                        var centerY = axis.Vertical ? point.Y + extent * 700f : point.Y;
                        g.DrawEllipse(bubble, centerX - 9f, centerY - 9f, 18f, 18f);
                        var size = g.MeasureString(label, font);
                        g.DrawString(label, font, brush, centerX - size.Width / 2f, centerY - size.Height / 2f);
                    }
                }
            }
        }

        private void DrawRooms(Graphics g)        {
            if (_model == null) return;
            var rooms = (_model.Rooms ?? new List<RoomModel>()).Where(r => r != null && Same(r.StoreyId, _storeyId)).ToList();
            if (rooms.Count == 0 && _roomDraft == null) return;
            using (var pen = new Pen(Color.FromArgb(150, 210, 225), 1.1f))
            using (var draft = new Pen(Color.FromArgb(120, 200, 255), 1.4f) { DashStyle = DashStyle.Dash })
            using (var font = new Font("Microsoft YaHei UI", 8f))
            using (var brush = new SolidBrush(Color.FromArgb(180, 225, 235)))
            {
                foreach (var room in rooms)
                {
                    var points = (room.Outline ?? new List<PointModel>()).Where(p => p != null && Sane(p.X, p.Y)).ToList();
                    if (points.Count < 3) continue;
                    var screens = points.Select(p => ToScreen(p.X, p.Y)).ToArray();
                    var selected = _selection != null && _selection.Kind == "room" && Same(_selection.Id, room.Id);
                    pen.Color = selected ? Color.FromArgb(255, 210, 120) : Color.FromArgb(150, 210, 225);
                    g.DrawPolygon(pen, screens);
                    var name = string.IsNullOrWhiteSpace(room.Name) ? "房间" : room.Name;
                    var area = room.AreaSquareMetres.ToString("0.00") + " m²";
                    // 名字与面积写在形心（与投影出来的平面图一致）
                    var centerX = screens.Average(p => p.X);
                    var centerY = screens.Average(p => p.Y);
                    var nameSize = g.MeasureString(name, font);
                    var areaSize = g.MeasureString(area, font);
                    g.DrawString(name, font, brush, centerX - nameSize.Width / 2f, centerY - nameSize.Height);
                    g.DrawString(area, font, brush, centerX - areaSize.Width / 2f, centerY + 2f);
                }
                if (_roomDraft != null && _roomDraft.Count > 0)
                {
                    var screens = _roomDraft.Select(p => ToScreen(p.X, p.Y)).ToList();
                    for (var index = 0; index + 1 < screens.Count; index++) g.DrawLine(draft, screens[index], screens[index + 1]);
                    if (screens.Count > 0) g.DrawLine(draft, screens[screens.Count - 1], ToScreen(_cursorX, _cursorY));
                    for (var index = 0; index < screens.Count; index++) g.FillRectangle(Brushes.Gold, screens[index].X - 3f, screens[index].Y - 3f, 6f, 6f);
                }
            }
        }

        // ───────────────────────── 拖动状态（拖谁只认谁） ─────────────────────────

        private WallModel _dragWall;
        private OpeningModel _dragOpening;
        private ColumnModel _dragColumn;
        private AxisModel _dragAxis;
        private RoomModel _dragRoom;
        private StairModel _dragStair;
        private RoofModel _dragRoof;
        private double _dragOriginAxisPosition;
        private List<PointModel> _dragOriginOutline;
        /// <summary>正在画的房间轮廓（"房间"工具连续点出来的点）。</summary>
        private List<PointModel> _roomDraft;
        private int _dragGrip = -1;
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
                CancelDrag();
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

        /// <summary>
        /// 取消正在进行的拖动：清掉拖动状态与抓着的构件引用。
        /// 删除、撤销/重做、换楼层、换模型、换工具、失去鼠标捕获都要走它 ——
        /// 2026-09-26 的 NullReferenceException 就是"拖柱时选中项被清空，鼠标一动还去解引用"。
        /// </summary>
        private void CancelDrag()
        {
            _drag = DragMode.None;
            _dragWall = null;
            _dragOpening = null;
            _dragColumn = null;
            _dragAxis = null;
            _dragRoom = null;
            _dragStair = null;
            _dragRoof = null;
            _dragOriginOutline = null;
            _dragGrip = -1;
        }

        /// <summary>拖动抓着的构件还在模型里吗（被删除或撤销替换掉的模型会让它不在）。</summary>
        private bool DragTargetAlive()
        {
            switch (_drag)
            {
                case DragMode.MoveWall:
                case DragMode.Grip:
                    return _dragWall != null && (_model.Walls ?? new List<WallModel>()).Contains(_dragWall);
                case DragMode.MoveOpening:
                    return _dragOpening != null && (_model.Openings ?? new List<OpeningModel>()).Contains(_dragOpening);
                case DragMode.MoveColumn:
                    return _dragColumn != null && (_model.Columns ?? new List<ColumnModel>()).Contains(_dragColumn);
                case DragMode.MoveAxis:
                    return _dragAxis != null && (_model.Axes ?? new List<AxisModel>()).Contains(_dragAxis);
                case DragMode.MoveRoom:
                    return _dragRoom != null && (_model.Rooms ?? new List<RoomModel>()).Contains(_dragRoom);
                case DragMode.MoveStair:
                    return _dragStair != null && (_model.Stairs ?? new List<StairModel>()).Contains(_dragStair);
                case DragMode.MoveRoof:
                    return _dragRoof != null && (_model.Roofs ?? new List<RoofModel>()).Contains(_dragRoof);
                default:
                    return true;
            }
        }

        /// <summary>自检用：把鼠标消息直接送进画布（拖动路径不依赖窗口消息循环）。</summary>
        internal void SimulateMouseDown(Point point, MouseButtons button)
        {
            OnMouseDown(new MouseEventArgs(button, 1, point.X, point.Y, 0));
        }

        /// <summary>自检用：送一条鼠标移动。</summary>
        internal void SimulateMouseMove(Point point)
        {
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0));
        }

        /// <summary>自检用：送一条鼠标抬起。</summary>
        internal void SimulateMouseUp(Point point, MouseButtons button)
        {
            OnMouseUp(new MouseEventArgs(button, 1, point.X, point.Y, 0));
        }

        /// <summary>自检用：当前是否在拖动。</summary>
        internal bool IsDragging { get { return _drag != DragMode.None; } }

        public string StoreyId
        {
            get { return _storeyId; }
            set
            {
                CancelDrag();       // 换楼层时原来选中的构件不属于这一层了，拖动必须停
                _storeyId = value;
                _selection = null;
                Invalidate();
                RaiseStatus();
                SelectionChanged?.Invoke();
            }
        }

        public string Tool
        {
            get { return _tool; }
            set
            {
                CancelDrag();
                if (_roomDraft != null) FinishRoomDraft();      // 换工具时把没画完的房间收尾
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
        /// <summary>放楼梯的默认参数（踏步宽、梯段宽）——放完可在属性面板逐项改。</summary>
        public double DefaultStairGoing = 260d, DefaultStairFlightWidth = 1200d;
        /// <summary>放屋面的默认坡度角（26.565° = 1:2 坡）。</summary>
        public double DefaultRoofPitch = 26.565d;

        /// <summary>当前选中的门窗类型（来自类型库）；不为空时放门窗直接套用它的编号与尺寸。</summary>
        public OpeningTypeModel CurrentType;

        // ───────────────────────── 视图变换 ─────────────────────────

        /// <summary>交给 GDI+ 的屏幕坐标上限与夹取：统一走 DrawGuard（立面预览也用同一套）。</summary>
        private PointF ToScreen(double x, double y)
        {
            return new PointF((float)DrawGuard.Clamp(_offsetX + x * _scale), (float)DrawGuard.Clamp(_offsetY - y * _scale));
        }

        private void ToModel(Point screen, out double x, out double y)
        {
            x = (screen.X - _offsetX) / _scale;
            y = (_offsetY - screen.Y) / _scale;
        }

        /// <summary>能安全交给 GDI+ 的模型坐标（坏数据只跳过自己，不拖垮整块画布）。</summary>
        private static bool Sane(double x, double y)
        {
            return DrawGuard.Sane(x, y);
        }

        /// <summary>视图变换还能不能用（比例非有限、偏移非有限都会让整块画布画不出来）。</summary>
        private bool ViewportUsable()
        {
            return DrawGuard.ViewportUsable(_scale, _offsetX, _offsetY);
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
            DrawAxes(g);
            DrawSlabs(g);
            DrawWalls(g);
            DrawColumns(g);
            DrawRoofs(g);
            DrawStairs(g);
            DrawOpenings(g);
            DrawRooms(g);
            DrawPreview(g);
            DrawGrips(g);
        }

        /// <summary>
        /// 画一条线（先裁到视口）。所有线都该走这里 ——
        /// 点划线/虚线在极端缩放下会长到上亿像素，GDI+ 生成虚线段会直接卡死。
        /// </summary>
        private void DrawClippedLine(Graphics g, Pen pen, PointF from, PointF to)
        {
            var x1 = from.X; var y1 = from.Y; var x2 = to.X; var y2 = to.Y;
            if (!DrawGuard.ClipLine(ref x1, ref y1, ref x2, ref y2, 0f, 0f, Width, Height)) return;
            g.DrawLine(pen, x1, y1, x2, y2);
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
                    if (line.Vertical) DrawClippedLine(g, pen, new PointF(line.Screen, 0f), new PointF(line.Screen, Height));
                    else DrawClippedLine(g, pen, new PointF(0f, line.Screen), new PointF(Width, line.Screen));
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

        /// <summary>
        /// 平面草图里的屋面：檐口矩形（虚线，表示"上层投影"）+ 屋脊点划线 + 坡度文字。
        /// 平面图（剖在 1.2m）里不画屋面，这里画是为了**能选中、能改参数**。
        /// </summary>
        private void DrawRoofs(Graphics g)
        {
            if (_model == null) return;
            var roofs = (_model.Roofs ?? new List<RoofModel>()).Where(r => r != null && Same(r.StoreyId, _storeyId)).ToList();
            if (roofs.Count == 0) return;
            using (var eavePen = new Pen(Color.FromArgb(215, 150, 110), 1.2f) { DashStyle = DashStyle.Dash })
            using (var ridgePen = new Pen(Color.FromArgb(235, 175, 130), 1.4f) { DashStyle = DashStyle.DashDot })
            using (var font = new Font("Microsoft YaHei UI", 8f))
            using (var brush = new SolidBrush(Color.FromArgb(240, 190, 150)))
            {
                foreach (var roof in roofs)
                {
                    var geometry = RoofGeometry.Build(_model, roof);
                    if (geometry == null) continue;
                    var selected = _selection != null && _selection.Kind == "roof" && Same(_selection.Id, roof.Id);
                    eavePen.Color = selected ? Color.FromArgb(255, 210, 120) : Color.FromArgb(215, 150, 110);
                    DrawClippedLine(g, eavePen, ToScreen(geometry.X0, geometry.Y0), ToScreen(geometry.X1, geometry.Y0));
                    DrawClippedLine(g, eavePen, ToScreen(geometry.X1, geometry.Y0), ToScreen(geometry.X1, geometry.Y1));
                    DrawClippedLine(g, eavePen, ToScreen(geometry.X1, geometry.Y1), ToScreen(geometry.X0, geometry.Y1));
                    DrawClippedLine(g, eavePen, ToScreen(geometry.X0, geometry.Y1), ToScreen(geometry.X0, geometry.Y0));
                    DrawClippedLine(g, ridgePen, ToScreen(geometry.RidgeStart.X, geometry.RidgeStart.Y),
                        ToScreen(geometry.RidgeEnd.X, geometry.RidgeEnd.Y));
                    var label = ToScreen((geometry.X0 + geometry.X1) / 2d, geometry.Y1);
                    g.DrawString("屋脊 " + Math.Round(geometry.RidgeElevation) + " / 坡度 "
                        + Math.Round(geometry.PitchDegrees, 1) + "°", font, brush, label.X + 4f, label.Y + 4f);
                }
            }
        }

        /// <summary>
        /// 平面草图里的楼梯：与出图用的是**同一套几何**（<see cref="StairGeometry"/>），
        /// 所以草图上看到的踏步、平台、箭头就是落图后的样子。
        /// </summary>
        private void DrawStairs(Graphics g)
        {
            if (_model == null) return;
            var stairs = (_model.Stairs ?? new List<StairModel>()).Where(s => s != null && Same(s.StoreyId, _storeyId)).ToList();
            if (stairs.Count == 0) return;
            using (var pen = new Pen(Color.FromArgb(215, 180, 120), 1.1f))
            using (var thick = new Pen(Color.FromArgb(235, 205, 150), 1.6f))
            using (var font = new Font("Microsoft YaHei UI", 8f))
            using (var brush = new SolidBrush(Color.FromArgb(240, 215, 165)))
            {
                foreach (var stair in stairs)
                {
                    var geometry = StairGeometry.Build(_model, stair);
                    if (geometry == null) continue;
                    var selected = _selection != null && _selection.Kind == "stair" && Same(_selection.Id, stair.Id);
                    pen.Color = selected ? Color.FromArgb(255, 210, 120) : Color.FromArgb(215, 180, 120);
                    if (geometry.Flights.Count == 0)
                    {
                        DrawClippedLine(g, pen, ToScreen(geometry.X0, geometry.Y0), ToScreen(geometry.X1, geometry.Y0));
                        DrawClippedLine(g, pen, ToScreen(geometry.X1, geometry.Y0), ToScreen(geometry.X1, geometry.Y1));
                        DrawClippedLine(g, pen, ToScreen(geometry.X1, geometry.Y1), ToScreen(geometry.X0, geometry.Y1));
                        DrawClippedLine(g, pen, ToScreen(geometry.X0, geometry.Y1), ToScreen(geometry.X0, geometry.Y0));
                        continue;
                    }
                    foreach (var flight in geometry.Flights)
                    {
                        DrawClippedLine(g, pen, ToScreen(flight.X0, flight.Y0), ToScreen(flight.X1, flight.Y0));
                        DrawClippedLine(g, pen, ToScreen(flight.X1, flight.Y0), ToScreen(flight.X1, flight.Y1));
                        DrawClippedLine(g, pen, ToScreen(flight.X1, flight.Y1), ToScreen(flight.X0, flight.Y1));
                        DrawClippedLine(g, pen, ToScreen(flight.X0, flight.Y1), ToScreen(flight.X0, flight.Y0));
                        foreach (var tread in flight.Treads)
                            DrawClippedLine(g, pen, ToScreen(tread[0].X, tread[0].Y), ToScreen(tread[1].X, tread[1].Y));
                    }
                    DrawClippedLine(g, thick, ToScreen(geometry.LandingX0, geometry.LandingY0),
                        ToScreen(geometry.LandingX1, geometry.LandingY0));
                    DrawClippedLine(g, thick, ToScreen(geometry.LandingX1, geometry.LandingY0),
                        ToScreen(geometry.LandingX1, geometry.LandingY1));
                    DrawClippedLine(g, thick, ToScreen(geometry.LandingX1, geometry.LandingY1),
                        ToScreen(geometry.LandingX0, geometry.LandingY1));
                    DrawClippedLine(g, thick, ToScreen(geometry.LandingX0, geometry.LandingY1),
                        ToScreen(geometry.LandingX0, geometry.LandingY0));
                    foreach (var rail in geometry.Handrails)
                        DrawClippedLine(g, pen, ToScreen(rail[0].X, rail[0].Y), ToScreen(rail[1].X, rail[1].Y));

                    // 上行箭头（起点处标"上"）
                    var path = geometry.UpPath ?? new List<PointModel>();
                    if (path.Count >= 2)
                    {
                        for (var index = 0; index + 1 < path.Count; index++)
                            DrawClippedLine(g, pen, ToScreen(path[index].X, path[index].Y),
                                ToScreen(path[index + 1].X, path[index + 1].Y));
                        var tip = ToScreen(path[path.Count - 1].X, path[path.Count - 1].Y);
                        var before = ToScreen(path[path.Count - 2].X, path[path.Count - 2].Y);
                        var dx = tip.X - before.X;
                        var dy = tip.Y - before.Y;
                        var length = Math.Sqrt(dx * dx + dy * dy);
                        if (length > 1e-6d)
                        {
                            var ux = dx / length;
                            var uy = dy / length;
                            g.DrawLine(pen, tip, new PointF(tip.X - (float)(ux * 10 - uy * 5), tip.Y - (float)(uy * 10 + ux * 5)));
                            g.DrawLine(pen, tip, new PointF(tip.X - (float)(ux * 10 + uy * 5), tip.Y - (float)(uy * 10 - ux * 5)));
                        }
                        var start = ToScreen(path[0].X, path[0].Y);
                        g.DrawString("上", font, brush, start.X - 6f, start.Y - 6f);
                    }
                }
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
                _drawFromX ?? 0d, _drawFromY ?? 0d);
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
                case "axis":
                    // 拉一条轴线：两次点击，方向取主轴方向，位置取垂直坐标（自动编号）
                    if (!_drawFromX.HasValue) { _drawFromX = snap.X; _drawFromY = snap.Y; }
                    else
                    {
                        var dx = Math.Abs(snap.X - _drawFromX.Value);
                        var dy = Math.Abs(snap.Y - _drawFromY.Value);
                        if (Math.Max(dx, dy) < 1d) { StatusChanged?.Invoke("提示：轴线太短，请拉出一段距离。"); break; }
                        var axis = new AxisModel
                        {
                            Id = NewId("AX"),
                            Vertical = dx <= dy,                                     // 竖向拉出来的就是竖轴
                            Position = dx <= dy ? _drawFromX.Value : _drawFromY.Value,
                            ExtentStart = dx <= dy ? Math.Min(_drawFromY.Value, snap.Y) : Math.Min(_drawFromX.Value, snap.X),
                            ExtentEnd = dx <= dy ? Math.Max(_drawFromY.Value, snap.Y) : Math.Max(_drawFromX.Value, snap.X)
                        };
                        _model.Axes.Add(axis);
                        PlanEditing.RenumberAxes(_model);
                        _selection = new PlanHit { Kind = "axis", Id = axis.Id };
                        Commit("加轴线 " + axis.Name);
                        _drawFromX = _drawFromY = null;
                    }
                    break;
                case "room":
                    // 连续点出房间轮廓；点回起点或按 Esc/右键结束并闭合
                    if (_roomDraft == null) _roomDraft = new List<PointModel>();
                    if (_roomDraft.Count >= 3)
                    {
                        var first = _roomDraft[0];
                        if (Math.Abs(snap.X - first.X) < 200d && Math.Abs(snap.Y - first.Y) < 200d)
                        {
                            FinishRoomDraft();
                            break;
                        }
                    }
                    _roomDraft.Add(new PointModel(snap.X, snap.Y));
                    StatusChanged?.Invoke("房间轮廓已点 " + _roomDraft.Count + " 个点（点回起点或按 Esc 闭合）。");
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
                case "roof":
                {
                    // 两次点击拉出檐口矩形（把挑檐一起拉进去）；长边自动当屋脊方向
                    if (!_drawFromX.HasValue)
                    {
                        _drawFromX = snap.X; _drawFromY = snap.Y;
                        StatusChanged?.Invoke("再点一下确定檐口矩形的另一个角（记得把挑檐拉进去）。");
                        break;
                    }
                    var fromX = _drawFromX.Value;
                    var fromY = _drawFromY ?? 0d;
                    var dx = Math.Abs(snap.X - fromX);
                    var dy = Math.Abs(snap.Y - fromY);
                    if (Math.Min(dx, dy) < 1000d)
                    {
                        StatusChanged?.Invoke("提示：屋面太小了，请拉出至少 1m 见方的檐口矩形。");
                        break;
                    }
                    var roof = new RoofModel
                    {
                        Id = NewId("RF"), StoreyId = _storeyId,
                        X = Math.Min(fromX, snap.X), Y = Math.Min(fromY, snap.Y),
                        Width = dx, Depth = dy, AlongX = dx >= dy,
                        PitchDegrees = DefaultRoofPitch, EaveElevation = 0d
                    };
                    var error = PlanEditing.ValidateRoof(roof);
                    _drawFromX = _drawFromY = null;
                    if (error != null) { StatusChanged?.Invoke("提示：" + error); break; }
                    _model.Roofs.Add(roof);
                    _selection = new PlanHit { Kind = "roof", Id = roof.Id, Grip = -1 };
                    Commit("放屋面 " + roof.Id);
                    var geometry = RoofGeometry.Build(_model, roof);
                    StatusChanged?.Invoke("已放坡屋面（" + Math.Round(roof.Width) + "×" + Math.Round(roof.Depth)
                        + "，" + (roof.AlongX ? "屋脊沿X" : "屋脊沿Y") + "）——檐口标高 " + Math.Round(geometry.EaveElevation)
                        + "、屋脊 " + Math.Round(geometry.RidgeElevation) + "，坡度与方向可在属性面板改。");
                    break;
                }
                case "stair":
                {
                    // 两次点击拉出一个矩形楼梯间：长边就是梯段方向
                    if (!_drawFromX.HasValue) { _drawFromX = snap.X; _drawFromY = snap.Y; StatusChanged?.Invoke("再点一下确定楼梯间的另一个角。"); break; }
                    var fromX = _drawFromX.Value;
                    var fromY = _drawFromY ?? 0d;
                    var dx = Math.Abs(snap.X - fromX);
                    var dy = Math.Abs(snap.Y - fromY);
                    if (Math.Min(dx, dy) < 500d) { StatusChanged?.Invoke("提示：楼梯间太小了，请拉出一个矩形（长边 = 梯段方向）。"); break; }
                    var alongX = dx >= dy;
                    var length = alongX ? dx : dy;
                    var width = alongX ? dy : dx;
                    // 踏步数：先按"目标踏步高 ≈165"估，再受楼梯间净长限制（要留 600 给休息平台）；
                    // 两头都满足不了时交给 ValidateStair 给出人话提示，别硬塞一个 300 高的踏步。
                    var storeyHeight = _model.HeightOf(new StairModel { StoreyId = _storeyId });
                    var byHeight = (int)Math.Round(storeyHeight / (2d * 165d));
                    var byLength = (int)Math.Floor((length - 600d) / DefaultStairGoing);
                    var stair = new StairModel
                    {
                        Id = NewId("ST"), StoreyId = _storeyId,
                        X = Math.Min(fromX, snap.X), Y = Math.Min(fromY, snap.Y),
                        Length = length, Width = width, AlongX = alongX,
                        FlightWidth = Math.Max(600d, Math.Min(DefaultStairFlightWidth, (width - 100d) / 2d)),
                        Going = DefaultStairGoing,
                        StepsPerFlight = Math.Max(3, Math.Min(Math.Max(3, byHeight), Math.Max(3, byLength))),
                        WellWidth = 100d
                    };
                    var error = PlanEditing.ValidateStair(_model, stair);
                    _drawFromX = _drawFromY = null;
                    if (error != null) { StatusChanged?.Invoke("提示：" + error); break; }
                    _model.Stairs.Add(stair);
                    _selection = new PlanHit { Kind = "stair", Id = stair.Id, Grip = -1 };
                    Commit("放楼梯 " + stair.Id);
                    StatusChanged?.Invoke("已放楼梯（" + Math.Round(stair.Length) + "×" + Math.Round(stair.Width)
                        + "，每跑 " + stair.StepsPerFlight + " 级 × " + Math.Round(stair.Going) + "）——踏步数/踏步宽可在属性面板改。");
                    break;
                }
                default:
                {
                    var hit = PlanEditing.HitTest(_model, _storeyId, snap.X, snap.Y, 10d / _scale);
                    _selection = hit;
                    SelectionChanged?.Invoke();
                    if (hit == null) { CancelDrag(); Invalidate(); break; }
                    _dragStartX = x; _dragStartY = y;
                    // 拖动期间**只认这里抓到的对象引用**，不再回头看 _selection：
                    // _selection 可能被删除/撤销/切楼层清掉，再解引用就会 NullReferenceException。
                    _dragWall = null; _dragOpening = null; _dragColumn = null; _dragAxis = null; _dragRoom = null; _dragGrip = -1;
                    var wall = hit.Kind == "wall" ? FindWall(hit.Id) : null;
                    if (wall != null)
                    {
                        _dragWall = wall;
                        _dragGrip = hit.Grip;
                        _dragOriginX1 = wall.X1; _dragOriginY1 = wall.Y1; _dragOriginX2 = wall.X2; _dragOriginY2 = wall.Y2;
                        _drag = hit.Grip >= 0 ? DragMode.Grip : DragMode.MoveWall;
                    }
                    else if (hit.Kind == "opening")
                    {
                        var opening = (_model.Openings ?? new List<OpeningModel>()).FirstOrDefault(o => o != null && Same(o.Id, hit.Id));
                        if (opening != null) { _dragOpening = opening; _dragOriginOffset = opening.Offset; _drag = DragMode.MoveOpening; }
                    }
                    else if (hit.Kind == "column")
                    {
                        var column = (_model.Columns ?? new List<ColumnModel>()).FirstOrDefault(c => c != null && Same(c.Id, hit.Id));
                        if (column != null) { _dragColumn = column; _dragOriginX1 = column.X; _dragOriginY1 = column.Y; _drag = DragMode.MoveColumn; }
                    }
                    else if (hit.Kind == "axis")
                    {
                        var axis = (_model.Axes ?? new List<AxisModel>()).FirstOrDefault(a => a != null && Same(a.Id, hit.Id));
                        if (axis != null) { _dragAxis = axis; _dragOriginAxisPosition = axis.Position; _drag = DragMode.MoveAxis; }
                    }
                    else if (hit.Kind == "room")
                    {
                        var room = (_model.Rooms ?? new List<RoomModel>()).FirstOrDefault(r => r != null && Same(r.Id, hit.Id));
                        if (room != null)
                        {
                            _dragRoom = room;
                            _dragOriginX1 = x; _dragOriginY1 = y;
                            _dragOriginOutline = (room.Outline ?? new List<PointModel>())
                                .Select(p => p == null ? null : new PointModel(p.X, p.Y)).ToList();
                            _drag = DragMode.MoveRoom;
                        }
                    }
                    else if (hit.Kind == "stair")
                    {
                        var stair = (_model.Stairs ?? new List<StairModel>()).FirstOrDefault(s => s != null && Same(s.Id, hit.Id));
                        if (stair != null)
                        {
                            _dragStair = stair;
                            _dragOriginX1 = stair.X; _dragOriginY1 = stair.Y;
                            _drag = DragMode.MoveStair;
                        }
                    }
                    else if (hit.Kind == "roof")
                    {
                        var roof = (_model.Roofs ?? new List<RoofModel>()).FirstOrDefault(r => r != null && Same(r.Id, hit.Id));
                        if (roof != null)
                        {
                            _dragRoof = roof;
                            _dragOriginX1 = roof.X; _dragOriginY1 = roof.Y;
                            _drag = DragMode.MoveRoof;
                        }
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
                _drawFromX ?? 0d, _drawFromY ?? 0d);
            _cursorX = snap.X;
            _cursorY = snap.Y;
            _snapKind = snap.Kind;

            if (_drag != DragMode.None && _drag != DragMode.DrawWall && _model != null)
            {
                if (!DragTargetAlive())
                {
                    // 拖到一半时构件被删掉/撤销/换楼层了：取消拖动，别去解引用旧对象
                    CancelDrag();
                    StatusChanged?.Invoke("拖动的构件已经不在模型里了（被删除或撤销），已取消本次拖动。");
                    Invalidate();
                    RaiseStatus();
                    return;
                }
                var deltaX = _cursorX - _dragStartX;
                var deltaY = _cursorY - _dragStartY;
                if (_drag == DragMode.MoveWall)
                {
                    _dragWall.X1 = _dragOriginX1 + deltaX; _dragWall.Y1 = _dragOriginY1 + deltaY;
                    _dragWall.X2 = _dragOriginX2 + deltaX; _dragWall.Y2 = _dragOriginY2 + deltaY;
                }
                else if (_drag == DragMode.Grip)
                {
                    if (_dragGrip == 0) { _dragWall.X1 = _cursorX; _dragWall.Y1 = _cursorY; }
                    else if (_dragGrip == 1) { _dragWall.X2 = _cursorX; _dragWall.Y2 = _cursorY; }
                    else
                    {
                        // 中点夹点 = 整道墙平移
                        _dragWall.X1 = _dragOriginX1 + deltaX; _dragWall.Y1 = _dragOriginY1 + deltaY;
                        _dragWall.X2 = _dragOriginX2 + deltaX; _dragWall.Y2 = _dragOriginY2 + deltaY;
                    }
                }
                else if (_drag == DragMode.MoveOpening)
                {
                    var wall = FindWall(_dragOpening.HostWallId);
                    if (wall == null)
                    {
                        CancelDrag();
                        StatusChanged?.Invoke("洞口所在的墙已经不在模型里了，已取消本次拖动。");
                        Invalidate();
                        RaiseStatus();
                        return;
                    }
                    // 洞口只能沿墙滑动，并且不能滑出墙端
                    var length = PlanEditing.WallLength(wall);
                    _dragOpening.Offset = Math.Max(_dragOpening.Width / 2d, Math.Min(length - _dragOpening.Width / 2d,
                        PlanEditing.ProjectOnWall(wall, _cursorX, _cursorY)));
                }
                else if (_drag == DragMode.MoveColumn)
                {
                    _dragColumn.X = _dragOriginX1 + deltaX; _dragColumn.Y = _dragOriginY1 + deltaY;
                }
                else if (_drag == DragMode.MoveAxis)
                {
                    // 轴线只能沿垂直方向移动（竖轴改 X、横轴改 Y）
                    _dragAxis.Position = _dragAxis.Vertical ? _cursorX : _cursorY;
                    PlanEditing.RenumberAxes(_model);       // 轴号跟着位置重排
                }
                else if (_drag == DragMode.MoveRoom)
                {
                    var points = _dragRoom.Outline ?? new List<PointModel>();
                    var offsets = _dragOriginOutline ?? new List<PointModel>();
                    for (var index = 0; index < points.Count && index < offsets.Count; index++)
                    {
                        if (points[index] == null || offsets[index] == null) continue;
                        points[index].X = offsets[index].X + deltaX;
                        points[index].Y = offsets[index].Y + deltaY;
                    }
                }
                else if (_drag == DragMode.MoveStair)
                {
                    _dragStair.X = _dragOriginX1 + deltaX;
                    _dragStair.Y = _dragOriginY1 + deltaY;
                }
                else if (_drag == DragMode.MoveRoof)
                {
                    _dragRoof.X = _dragOriginX1 + deltaX;
                    _dragRoof.Y = _dragOriginY1 + deltaY;
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
                    : _drag == DragMode.MoveOpening ? "移动洞口" : _drag == DragMode.MoveColumn ? "移动柱"
                    : _drag == DragMode.MoveAxis ? "移动轴线" : _drag == DragMode.MoveRoom ? "移动房间"
                    : _drag == DragMode.MoveStair ? "移动楼梯" : _drag == DragMode.MoveRoof ? "移动屋面" : "编辑";
                Commit(label);
            }
            CancelDrag();
        }

        /// <summary>鼠标捕获丢了（拖到窗口外面、切走了窗口）就别再继续拖，免得后面去解引用旧对象。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _drag != DragMode.None) CancelDrag();
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
                if (_roomDraft != null) { FinishRoomDraft(); return; }      // 画房间时 Esc = 闭合
                CancelDrag();
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
            else if (_selection.Kind == "axis")
            {
                removed = _model.Axes.RemoveAll(a => a != null && Same(a.Id, _selection.Id)) > 0;
                if (removed) PlanEditing.RenumberAxes(_model);
            }
            else if (_selection.Kind == "room") removed = _model.Rooms.RemoveAll(r => r != null && Same(r.Id, _selection.Id)) > 0;
            else if (_selection.Kind == "stair") removed = _model.Stairs.RemoveAll(s => s != null && Same(s.Id, _selection.Id)) > 0;
            else if (_selection.Kind == "roof") removed = _model.Roofs.RemoveAll(r => r != null && Same(r.Id, _selection.Id)) > 0;
            if (!removed) { StatusChanged?.Invoke("没找到要删除的构件。"); return; }
            CancelDrag();       // 被拖的那一个可能刚被删掉，拖动立即结束
            _selection = null;
            SelectionChanged?.Invoke();
            Commit("删除");
        }

        public void Undo()
        {
            var restored = _history.Undo(_model);
            if (ReferenceEquals(restored, _model)) { StatusChanged?.Invoke("没有可撤销的操作。"); return; }
            CancelDrag();       // 撤销会把模型换成快照，拖动抓着的旧对象已经不是模型里的了
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
            CancelDrag();
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
