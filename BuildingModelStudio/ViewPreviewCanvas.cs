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
    /// 立面/剖面预览画布：把 <see cref="ViewDocument"/>（程序算出来的视图）直接画出来，
    /// 不用开 CAD、不用落图就能看门窗分格与开启线画得对不对。
    ///
    /// 只做显示，不做几何：所有坐标都来自视图 JSON（那才是 CAD 落图用的同一份数据），
    /// 所以"预览里看到什么，CAD 里就落到什么"。
    ///
    /// 绘制纪律（与平面画布一致）：坐标先过 <see cref="DrawGuard"/>，
    /// OnPaint 出错只提示不抛异常 —— 预览坏掉不能把程序带走。
    /// </summary>
    internal sealed class ViewPreviewCanvas : Control
    {
        private ViewDocument _view;
        private double _scale = 0.08d;                   // 屏幕像素 / 模型毫米
        private double _offsetX = 60d, _offsetY = 60d;
        private Point _lastMouse;
        private bool _panning;
        private double _cursorX, _cursorY;
        private string _lastPaintError;
        /// <summary>当前选中的模型构件 id（视图里的锚点按它找）——重算视图后仍然选中同一个构件。</summary>
        private string _selectedElementId;

        public event Action<string> StatusChanged;
        /// <summary>点选变化（参数是被点中的锚点，点空白处为 null）。</summary>
        public event Action<ViewAnchor> AnchorSelected;

        public ViewPreviewCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(24, 26, 30);
            Cursor = Cursors.Cross;
        }

        /// <summary>自检用：上一次绘制画了多少条线/文字/填充/尺寸（证明"真的画了"）。</summary>
        internal int LastLineCount { get; private set; }
        internal int LastTextCount { get; private set; }
        internal int LastHatchCount { get; private set; }
        internal int LastDimensionCount { get; private set; }
        internal int LastCircleCount { get; private set; }
        internal string LastPaintError { get { return _lastPaintError; } }
        internal double ViewScale { get { return _scale; } }

        public ViewDocument View
        {
            get { return _view; }
            set
            {
                _view = value;
                _needsFit = true;
                ZoomExtents();
                Invalidate();
                RaiseStatus();
            }
        }

        /// <summary>当前选中的锚点（按构件 id 在当前视图里找）；没有选中返回 null。</summary>
        internal ViewAnchor SelectedAnchor
        {
            get
            {
                if (_view == null || string.IsNullOrEmpty(_selectedElementId)) return null;
                foreach (var anchor in _view.Anchors ?? new List<ViewAnchor>())
                    if (anchor != null && string.Equals(anchor.ElementId, _selectedElementId, StringComparison.OrdinalIgnoreCase))
                        return anchor;
                return null;
            }
        }

        internal string SelectedElementId { get { return _selectedElementId; } }

        /// <summary>自检用：直接选中某个构件（相当于点中了它）。</summary>
        internal void SelectElement(string elementId)
        {
            _selectedElementId = elementId;
            Invalidate();
            RaiseStatus();
        }

        /// <summary>自检用：把一次点击送进画布。</summary>
        internal void SimulateClick(Point screen)
        {
            SelectAt(screen);
        }

        /// <summary>自检用：视图坐标 → 屏幕坐标（用于"点某个洞口"）。</summary>
        internal Point ModelToScreenForTest(double x, double y)
        {
            var point = ToScreen(x, y);
            return new Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }

        /// <summary>点选：命中锚点（门窗洞口）就选中，点空白处取消选中。</summary>
        private void SelectAt(Point screen)
        {
            ViewAnchor found = null;
            if (_view != null && ViewportUsable())
            {
                var tolerance = 10d / Math.Max(1e-9d, _scale);      // 10 像素的容差，不用点得很准
                var x = (screen.X - _offsetX) / _scale;
                var y = (_offsetY - screen.Y) / _scale;
                foreach (var anchor in _view.Anchors ?? new List<ViewAnchor>())
                {
                    if (anchor == null) continue;
                    if (x < Math.Min(anchor.X1, anchor.X2) - tolerance) continue;
                    if (x > Math.Max(anchor.X1, anchor.X2) + tolerance) continue;
                    if (y < Math.Min(anchor.Y1, anchor.Y2) - tolerance) continue;
                    if (y > Math.Max(anchor.Y1, anchor.Y2) + tolerance) continue;
                    found = anchor;
                    break;
                }
            }
            _selectedElementId = found == null ? null : found.ElementId;
            Invalidate();
            RaiseStatus();
            AnchorSelected?.Invoke(found);
        }

        /// <summary>
        /// 视图在窗口还没有尺寸时就装进来了（构造期常见）：先记一笔，
        /// 等控件真正有尺寸再"缩放适应"一次 —— 否则用户切到预览页看到的会是一片空白。
        /// </summary>
        private bool _needsFit;
        /// <summary>当前取景是不是"自动适应"状态；是的话窗口一改大小就重新适应（用户手动缩放/平移后就不再自动改）。</summary>
        private bool _autoFit = true;

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FitIfNeeded();
            // 窗口大小变了：如果是自动适应状态，就按新尺寸重新适应一次
            if (_autoFit && _view != null && Width > 0 && Height > 0) ZoomExtents();
        }

        private void FitIfNeeded()
        {
            if (!_needsFit || Width <= 0 || Height <= 0) return;
            _needsFit = false;
            ZoomExtents();
            Invalidate();
        }

        /// <summary>自检用：直接摆好视图变换，不走鼠标。</summary>
        internal void SetViewport(double scale, double offsetX, double offsetY)
        {
            _scale = scale;
            _offsetX = offsetX;
            _offsetY = offsetY;
            Invalidate();
        }

        private bool ViewportUsable()
        {
            return DrawGuard.ViewportUsable(_scale, _offsetX, _offsetY);
        }

        private PointF ToScreen(double x, double y)
        {
            return new PointF((float)DrawGuard.Clamp(_offsetX + x * _scale), (float)DrawGuard.Clamp(_offsetY - y * _scale));
        }

        public void ZoomExtents()
        {
            _autoFit = true;
            if (_view == null || Width <= 0 || Height <= 0)
            {
                _needsFit = _view != null;      // 还没尺寸：等 OnSizeChanged 再适应
                return;
            }
            _needsFit = false;
            var bounds = BoundsOf(_view);
            if (bounds == null)
            {
                _scale = 0.08d;
                _offsetX = 60d;
                _offsetY = Height - 60d;
                return;
            }
            // 左上角要留给图例（图例大约 300×180 像素）：把图缩在"减去图例"的那块框里，
            // 图例就不会压在立面上，底部图名也不会被裁掉。
            var left = Math.Min(300d, Width * 0.35d);
            var top = Math.Min(180d, Height * 0.28d);
            var boxWidth = Math.Max(50d, Width - left - 16d);
            var boxHeight = Math.Max(50d, Height - top - 16d);

            var minX = bounds.Value.Left - 500d;
            var maxX = bounds.Value.Right + 500d;
            var minY = bounds.Value.Top - 500d;
            var maxY = bounds.Value.Bottom + 500d;
            var width = Math.Max(1d, maxX - minX);
            var height = Math.Max(1d, maxY - minY);
            _scale = Math.Max(0.002d, Math.Min(boxWidth / width, boxHeight / height));
            _offsetX = left + (boxWidth - width * _scale) / 2d - minX * _scale;
            _offsetY = top + (boxHeight - height * _scale) / 2d + maxY * _scale;
        }

        /// <summary>
        /// 视图的范围（线条 + 填充 + 文字）：缩放到这个范围。
        /// 文字也要算进去 —— 标高在右边、图名在下面，不算进来就会被裁掉。
        /// </summary>
        private static RectangleF? BoundsOf(ViewDocument view)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            var found = false;
            foreach (var line in view.Lines ?? new List<ViewLine>())
            {
                if (line == null) continue;
                if (!DrawGuard.Sane(line.X1, line.Y1) || !DrawGuard.Sane(line.X2, line.Y2)) continue;
                Include(Math.Min(line.X1, line.X2), Math.Min(line.Y1, line.Y2));
                Include(Math.Max(line.X1, line.X2), Math.Max(line.Y1, line.Y2));
            }
            foreach (var hatch in view.Hatches ?? new List<ViewHatch>())
                foreach (var point in hatch == null ? new List<PointModel>() : hatch.Boundary ?? new List<PointModel>())
                {
                    if (point == null || !DrawGuard.Sane(point.X, point.Y)) continue;
                    Include(point.X, point.Y);
                }
            foreach (var circle in view.Circles ?? new List<ViewCircle>())
            {
                if (circle == null || !DrawGuard.Sane(circle.X, circle.Y) || !DrawGuard.IsFinite(circle.Radius)) continue;
                var radius = Math.Abs(circle.Radius);
                Include(circle.X - radius, circle.Y - radius);
                Include(circle.X + radius, circle.Y + radius);
            }
            foreach (var text in view.Texts ?? new List<ViewText>())
            {
                if (text == null || string.IsNullOrEmpty(text.Text)) continue;
                if (!DrawGuard.Sane(text.X, text.Y)) continue;
                var height = text.Height > 1d ? text.Height : 250d;
                var width = height * 0.62d * text.Text.Length;        // 与预览绘制同一套估算
                Include(text.X, text.Y - height);                     // 文字左下角在 (X, Y)
                Include(text.X + width, text.Y);
            }
            foreach (var dimension in view.Dimensions ?? new List<ViewDimension>())
            {
                if (dimension == null) continue;
                if (!DrawGuard.Sane(dimension.From, dimension.To)) continue;
                var outer = Math.Max(dimension.AnchorPosition, dimension.LinePosition);
                var inner = Math.Min(dimension.AnchorPosition, dimension.LinePosition);
                // 尺寸线比建筑轮廓还靠外，不算进来的话会被裁掉
                if (dimension.Vertical)
                {
                    Include(inner, Math.Min(dimension.From, dimension.To));
                    Include(outer, Math.Max(dimension.From, dimension.To));
                }
                else
                {
                    Include(Math.Min(dimension.From, dimension.To), inner);
                    Include(Math.Max(dimension.From, dimension.To), outer);
                }
            }
            if (!found) return null;
            return RectangleF.FromLTRB((float)minX, (float)minY, (float)maxX, (float)maxY);

            void Include(double x, double y)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                found = true;
            }
        }

        // ───────────────────────── 鼠标 ─────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _panning = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button == MouseButtons.Left) SelectAt(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _offsetX += e.X - _lastMouse.X;
                _offsetY += e.Y - _lastMouse.Y;
                _lastMouse = e.Location;
                _autoFit = false;               // 手动平移过就不再自动适应
                Invalidate();
            }
            if (ViewportUsable())
            {
                _cursorX = (e.X - _offsetX) / _scale;
                _cursorY = (_offsetY - e.Y) / _scale;
            }
            RaiseStatus();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _panning = false;
            Cursor = Cursors.Cross;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!ViewportUsable()) return;
            var beforeX = (e.X - _offsetX) / _scale;
            var beforeY = (_offsetY - e.Y) / _scale;
            _scale = Math.Max(0.002d, Math.Min(2d, _scale * (e.Delta > 0 ? 1.15d : 1d / 1.15d)));
            _offsetX = e.X - beforeX * _scale;      // 让光标下的那个点不动
            _offsetY = e.Y + beforeY * _scale;
            _autoFit = false;                       // 手动缩放过就不再自动适应
            Invalidate();
            RaiseStatus();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.A && (ModifierKeys & Keys.Control) == Keys.Control) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.A)
            {
                ZoomExtents();
                Invalidate();
                RaiseStatus();
            }
        }

        private void RaiseStatus()
        {
            var selected = SelectedAnchor;
            var text = _view == null
                ? "立面预览：还没有视图"
                : "光标 " + Math.Round(_cursorX) + ", " + Math.Round(_cursorY) + " mm"
                    + "　" + _view.Title + "　1:" + _view.Scale
                    + "　线 " + (_view.Lines == null ? 0 : _view.Lines.Count)
                    + " / 文字 " + (_view.Texts == null ? 0 : _view.Texts.Count)
                    + " / 填充 " + (_view.Hatches == null ? 0 : _view.Hatches.Count)
                    + "　1px≈" + Math.Round(1d / Math.Max(1e-9d, _scale), 1) + "mm"
                    + (selected == null ? "　（点门窗可选中）" : "　已选中：" + selected.ElementId);
            StatusChanged?.Invoke(text);
        }

        // ───────────────────────── 绘制 ─────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            SafeRender(e.Graphics);
        }

        /// <summary>带兜底的绘制：出错只提示，不抛给 WinForms（自检也走这里）。</summary>
        internal void SafeRender(Graphics g)
        {
            try
            {
                Render(g);
                _lastPaintError = null;
            }
            catch (Exception exception)
            {
                var message = exception.GetType().Name + "：" + exception.Message;
                if (!string.Equals(message, _lastPaintError, StringComparison.Ordinal))
                {
                    _lastPaintError = message;
                    StatusChanged?.Invoke("预览显示出错，已跳过本次绘制：" + message);
                }
                try
                {
                    g.Clear(BackColor);
                    DrawHint(g, "预览显示出错，已跳过本次绘制：" + message);
                }
                catch
                {
                    // 提示都画不出来就算了，别再抛一次
                }
            }
        }

        internal void Render(Graphics g)
        {
            if (g == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            LastLineCount = 0;
            LastTextCount = 0;
            LastHatchCount = 0;
            LastDimensionCount = 0;
            LastCircleCount = 0;
            FitIfNeeded();      // 控件刚拿到尺寸时，先把视图摆正再画（否则切过来可能是一片空白）

            if (_view == null)
            {
                DrawHint(g, "还没有视图：点「重算当前视图」按现在的模型算一张出来。");
                return;
            }
            if (!ViewportUsable())
            {
                DrawHint(g, "视图变换异常，已恢复默认视图。");
                ZoomExtents();
                return;
            }

            DrawHatches(g);
            DrawLines(g);
            DrawCircles(g);
            DrawDimensions(g);
            DrawTexts(g);
            DrawSelection(g);
            DrawLegend(g);
        }

        /// <summary>
        /// 尺寸标注：界线 + 尺寸线 + 建筑标记（45° 斜短线）+ 数值。
        /// CAD 里这些是**真的标注**（可拉伸、可改）；预览按同一套位置画出来，好核对层高与洞口定位。
        /// </summary>
        private void DrawDimensions(Graphics g)
        {
            foreach (var dimension in _view.Dimensions ?? new List<ViewDimension>())
            {
                if (dimension == null) continue;
                if (!DrawGuard.IsFinite(dimension.From) || !DrawGuard.IsFinite(dimension.To)
                    || !DrawGuard.IsFinite(dimension.AnchorPosition) || !DrawGuard.IsFinite(dimension.LinePosition)) continue;
                var layer = string.IsNullOrWhiteSpace(dimension.Layer) ? ViewLayers.Dimension : dimension.Layer;
                var style = ViewLayers.Find(layer);
                var color = ColorFor(layer, style);
                var text = string.IsNullOrWhiteSpace(dimension.Text)
                    ? Math.Round(Math.Abs(dimension.To - dimension.From)).ToString("0")
                    : dimension.Text;
                using (var pen = new Pen(color, Math.Max(0.9f, WidthFor(layer, style))))
                {
                    if (dimension.Vertical)
                    {
                        var anchorX = ToScreen(dimension.AnchorPosition, 0d).X;
                        var lineX = ToScreen(dimension.LinePosition, 0d).X;
                        var fromY = ToScreen(0d, dimension.From).Y;
                        var toY = ToScreen(0d, dimension.To).Y;
                        g.DrawLine(pen, anchorX, fromY, lineX, fromY);
                        g.DrawLine(pen, anchorX, toY, lineX, toY);
                        g.DrawLine(pen, lineX, fromY, lineX, toY);
                        DrawTick(g, pen, lineX, fromY);
                        DrawTick(g, pen, lineX, toY);
                        DrawDimensionText(g, text, lineX, (fromY + toY) / 2f, true, color);
                    }
                    else
                    {
                        var anchorY = ToScreen(0d, dimension.AnchorPosition).Y;
                        var lineY = ToScreen(0d, dimension.LinePosition).Y;
                        var fromX = ToScreen(dimension.From, 0d).X;
                        var toX = ToScreen(dimension.To, 0d).X;
                        g.DrawLine(pen, fromX, anchorY, fromX, lineY);
                        g.DrawLine(pen, toX, anchorY, toX, lineY);
                        g.DrawLine(pen, fromX, lineY, toX, lineY);
                        DrawTick(g, pen, fromX, lineY);
                        DrawTick(g, pen, toX, lineY);
                        DrawDimensionText(g, text, (fromX + toX) / 2f, lineY, false, color);
                    }
                }
                LastDimensionCount++;
            }
        }

        /// <summary>建筑标记：尺寸线端部的 45° 斜短线。</summary>
        private static void DrawTick(Graphics g, Pen pen, float x, float y)
        {
            const float size = 4f;
            g.DrawLine(pen, x - size, y + size, x + size, y - size);
        }

        private void DrawDimensionText(Graphics g, string text, float x, float y, bool vertical, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            var pixels = (float)Math.Max(7d, Math.Min(18d, 250d * _scale));
            using (var font = new Font("Microsoft YaHei UI", pixels, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            {
                var state = g.Save();
                try
                {
                    g.TranslateTransform(x, y);
                    if (vertical) g.RotateTransform(-90f);
                    var size = g.MeasureString(text, font);
                    // 竖直尺寸：文字贴在尺寸线左侧；水平尺寸：文字压在尺寸线上方
                    g.DrawString(text, font, brush, -size.Width / 2f, vertical ? 3f : -size.Height - 2f);
                }
                finally
                {
                    g.Restore(state);
                }
            }
        }

        /// <summary>选中高亮：虚线框套住点中的那个洞口，配合右侧类型库就知道在改哪一樘。</summary>
        private void DrawSelection(Graphics g)
        {
            var anchor = SelectedAnchor;
            if (anchor == null) return;
            var first = ToScreen(anchor.X1, anchor.Y1);
            var second = ToScreen(anchor.X2, anchor.Y2);
            var left = Math.Min(first.X, second.X);
            var top = Math.Min(first.Y, second.Y);
            var width = Math.Abs(second.X - first.X);
            var height = Math.Abs(second.Y - first.Y);
            if (width < 1f || height < 1f) return;
            using (var pen = new Pen(Color.FromArgb(255, 210, 120), 1.8f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, left, top, width, height);
        }

        private void DrawHatches(Graphics g)
        {
            foreach (var hatch in _view.Hatches ?? new List<ViewHatch>())
            {
                if (hatch == null) continue;
                var boundary = hatch.Boundary ?? new List<PointModel>();
                if (boundary.Count < 3) continue;
                if (boundary.Any(p => p == null || !DrawGuard.Sane(p.X, p.Y))) continue;
                var points = boundary.Select(p => ToScreen(p.X, p.Y)).ToArray();

                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(points);
                    if (hatch.Spacing > 0.5d)
                    {
                        // 用户定义图案：按间距画 45°（或给定角度）细线，用裁剪让形状说话
                        var state = g.Save();
                        try
                        {
                            g.SetClip(path, CombineMode.Intersect);
                            var style = ViewLayers.Find(hatch.Layer) ?? ViewLayers.Find(ViewLayers.CutHatch);
                            using (var pen = new Pen(ColorFor(hatch.Layer, style), 0.9f))
                                DrawParallelLines(g, pen, points, hatch.Angle, hatch.Spacing);
                        }
                        finally
                        {
                            g.Restore(state);
                        }
                    }
                    else
                    {
                        using (var brush = new SolidBrush(Color.FromArgb(70, ColorFor(hatch.Layer, ViewLayers.Find(hatch.Layer)))))
                            g.FillPath(brush, path);
                    }
                }
                LastHatchCount++;
            }
        }

        /// <summary>在给定的屏幕多边形范围内按角度与间距画平行线（已由调用方裁剪到形状内）。</summary>
        private void DrawParallelLines(Graphics g, Pen pen, PointF[] points, double angleDegrees, double spacing)
        {
            var minX = points.Min(p => p.X);
            var maxX = points.Max(p => p.X);
            var minY = points.Min(p => p.Y);
            var maxY = points.Max(p => p.Y);
            var span = (float)Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            var step = (float)(spacing * _scale);
            if (!DrawGuard.IsFinite(step) || step < 3f) step = 3f;          // 太密就没法看，也不值得画
            if (step > span) step = span;                                    // 一条都不画也不对
            if (span <= 0f || !DrawGuard.IsFinite(span)) return;
            if (span / step > 4000f) return;                                 // 安全阀：绝不无限循环

            var angle = (float)(angleDegrees * Math.PI / 180d);
            var dx = (float)Math.Cos(angle);
            var dy = (float)Math.Sin(angle);
            var centerX = (minX + maxX) / 2f;
            var centerY = (minY + maxY) / 2f;
            var count = (int)Math.Ceiling(span / step);
            for (var index = -count; index <= count; index++)
            {
                var offset = index * step;
                // 与主线垂直的方向上平移
                var px = centerX - dy * offset;
                var py = centerY + dx * offset;
                var half = span;
                var x1 = (float)DrawGuard.Clamp(px - dx * half);
                var y1 = (float)DrawGuard.Clamp(py - dy * half);
                var x2 = (float)DrawGuard.Clamp(px + dx * half);
                var y2 = (float)DrawGuard.Clamp(py + dy * half);
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        private void DrawLines(Graphics g)
        {
            foreach (var line in _view.Lines ?? new List<ViewLine>())
            {
                if (line == null) continue;
                if (!DrawGuard.Sane(line.X1, line.Y1) || !DrawGuard.Sane(line.X2, line.Y2)) continue;
                var style = ViewLayers.Find(line.Layer);
                using (var pen = new Pen(ColorFor(line.Layer, style), WidthFor(line.Layer, style)))
                {
                    if (IsHidden(line.LineType)) pen.DashStyle = DashStyle.Dash;
                    else if (IsCenter(line.LineType)) pen.DashStyle = DashStyle.DashDot;
                    var from = ToScreen(line.X1, line.Y1);
                    var to = ToScreen(line.X2, line.Y2);
                    // 裁到视口再画：虚线/点划线太长时 GDI+ 会生成海量虚线段直接卡死
                    var x1 = from.X; var y1 = from.Y; var x2 = to.X; var y2 = to.Y;
                    if (!DrawGuard.ClipLine(ref x1, ref y1, ref x2, ref y2, 0f, 0f, Width, Height)) continue;
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
                LastLineCount++;
            }
        }

        /// <summary>圆（轴号圆圈等）。</summary>
        private void DrawCircles(Graphics g)
        {
            foreach (var circle in _view.Circles ?? new List<ViewCircle>())
            {
                if (circle == null) continue;
                if (!DrawGuard.Sane(circle.X, circle.Y) || !DrawGuard.IsFinite(circle.Radius)) continue;
                var radius = Math.Abs(circle.Radius) * _scale;
                if (!DrawGuard.IsFinite(radius) || radius < 1d || radius > 1e6d) continue;
                var style = ViewLayers.Find(circle.Layer);
                using (var pen = new Pen(ColorFor(circle.Layer, style), Math.Max(0.9f, WidthFor(circle.Layer, style))))
                {
                    var center = ToScreen(circle.X, circle.Y);
                    g.DrawEllipse(pen, center.X - (float)radius, center.Y - (float)radius, (float)radius * 2f, (float)radius * 2f);
                }
                LastCircleCount++;
            }
        }

        private void DrawTexts(Graphics g)
        {
            foreach (var text in _view.Texts ?? new List<ViewText>())
            {
                if (text == null || string.IsNullOrEmpty(text.Text)) continue;
                if (!DrawGuard.Sane(text.X, text.Y)) continue;
                var height = text.Height > 1d ? text.Height : 250d;
                var pixels = (float)(height * _scale);
                if (!DrawGuard.IsFinite(pixels) || pixels < 4f) continue;
                pixels = Math.Min(pixels, 60f);
                var style = ViewLayers.Find(text.Layer);
                using (var font = new Font("Microsoft YaHei UI", pixels, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(ColorFor(text.Layer, style)))
                {
                    var point = ToScreen(text.X, text.Y);
                    // 视图格式里文字 (X, Y) 是"左下角"（插件落图用的 DBText 位置就是基线左端），
                    // 所以这里把字符串画在 Y 的上方，预览与 CAD 才对得上。
                    g.DrawString(text.Text, font, brush, point.X, point.Y - font.Height);
                }
                LastTextCount++;
            }
        }

        /// <summary>左上角图例：这一张图上有什么图层、各多少条 —— 对着它看 CAD 里落得对不对。</summary>
        private void DrawLegend(Graphics g)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in _view.Lines ?? new List<ViewLine>())
            {
                if (line == null) continue;
                var key = line.Layer ?? "（未指定图层）";
                counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
            }

            using (var font = new Font("Microsoft YaHei UI", 9.5f))
            using (var titleFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
            {
                // 先把要写的行攒出来，量好尺寸再铺底，图例就不会糊在立面线条上
                var title = _view.Title + "　1:" + _view.Scale;
                var rows = new List<string>();
                foreach (var style in ViewLayers.All)
                {
                    counts.TryGetValue(style.Name, out var count);
                    rows.Add(style.Name + "（" + count + " 条）");
                }
                foreach (var pair in counts.Where(p => ViewLayers.All.All(s => s.Name != p.Key)))
                    rows.Add(pair.Key + "（" + pair.Value + " 条）");

                var padding = 8f;
                var swatch = 26f;
                var titleSize = g.MeasureString(title, titleFont);
                var width = titleSize.Width;
                var height = titleSize.Height + 4f;
                var rowSize = new List<SizeF>();
                foreach (var row in rows)
                {
                    var size = g.MeasureString(row, font);
                    rowSize.Add(size);
                    width = Math.Max(width, swatch + size.Width);
                    height += size.Height + 2f;
                }
                foreach (var warning in _view.Warnings ?? new List<string>())
                {
                    var size = g.MeasureString("提示：" + warning, font);
                    rowSize.Add(size);
                    width = Math.Max(width, size.Width);
                    height += size.Height + 2f;
                }

                using (var background = new SolidBrush(Color.FromArgb(170, 16, 18, 22)))
                    g.FillRectangle(background, 6f, 6f, width + padding * 2f, height + padding * 2f);

                var y = 6f + padding;
                using (var textBrush = new SolidBrush(Color.FromArgb(225, 230, 238)))
                {
                    g.DrawString(title, titleFont, textBrush, 6f + padding, y);
                    y += titleSize.Height + 4f;
                    for (var index = 0; index < rows.Count; index++)
                    {
                        var layer = index < ViewLayers.All.Length ? ViewLayers.All[index].Name : null;
                        if (layer != null)
                        {
                            var style = ViewLayers.Find(layer);
                            using (var pen = new Pen(ColorFor(layer, style), WidthFor(layer, style)))
                                g.DrawLine(pen, 6f + padding + 2f, y + rowSize[index].Height / 2f,
                                    6f + padding + swatch, y + rowSize[index].Height / 2f);
                        }
                        g.DrawString(rows[index], font, textBrush, 6f + padding + swatch + 4f, y);
                        y += rowSize[index].Height + 2f;
                    }
                    var warningIndex = rows.Count;
                    using (var warnBrush = new SolidBrush(Color.FromArgb(255, 200, 120)))
                        foreach (var warning in _view.Warnings ?? new List<string>())
                        {
                            g.DrawString("提示：" + warning, font, warnBrush, 6f + padding, y);
                            y += rowSize[warningIndex++].Height + 2f;
                        }
                }
            }
        }

        private void DrawHint(Graphics g, string text)
        {
            using (var font = new Font("Microsoft YaHei UI", 10f))
            using (var brush = new SolidBrush(Color.FromArgb(150, 156, 166)))
                g.DrawString(text, font, brush, 16, 16);
        }

        /// <summary>暗色底上的图层配色（与平面画布一套观感）。</summary>
        internal static Color ColorFor(string layer, ViewLayers.Style style)
        {
            switch (layer)
            {
                case ViewLayers.Cut: return Color.FromArgb(235, 240, 246);
                case ViewLayers.Ground: return Color.FromArgb(205, 212, 220);
                case ViewLayers.Elevation: return Color.FromArgb(168, 176, 188);
                case ViewLayers.Opening: return Color.FromArgb(120, 200, 170);
                case ViewLayers.CutHatch: return Color.FromArgb(120, 132, 148);
                case ViewLayers.LevelText: return Color.FromArgb(255, 210, 120);
                case ViewLayers.Title: return Color.FromArgb(160, 220, 255);
                case ViewLayers.Axis: return Color.FromArgb(200, 160, 230);
                case ViewLayers.Room: return Color.FromArgb(150, 210, 225);
                default: return Color.FromArgb(180, 186, 196);
            }
        }

        /// <summary>线宽：按制图标准里的线宽（1/100 mm）折算成屏幕像素，并夹到看得见的范围。</summary>
        internal static float WidthFor(string layer, ViewLayers.Style style)
        {
            var weight = style == null ? 25 : style.LineWeight;
            var width = weight / 22f;
            if (!DrawGuard.IsFinite(width)) width = 1.2f;
            return Math.Max(0.9f, Math.Min(3.2f, width));
        }

        private static bool IsHidden(string lineType)
        {
            return !string.IsNullOrWhiteSpace(lineType)
                && lineType.IndexOf("HIDDEN", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCenter(string lineType)
        {
            return !string.IsNullOrWhiteSpace(lineType)
                && (lineType.IndexOf("CENTER", StringComparison.OrdinalIgnoreCase) >= 0
                    || lineType.IndexOf("DASHDOT", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
