using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DetailLayoutPreviewControl : Control
    {
        private readonly List<DetailLayoutItem> _items = new List<DetailLayoutItem>();
        private DetailLayoutPlan _plan;
        private FrameDefinition _frame;
        private DetailLayoutOptions _options;
        private int _scale = 50;
        private float _zoom = 1f;
        private PointF _offset;
        private int _dragIndex = -1;
        private DetailLayoutSlot _hoverSlot;
        private string _error;

        public event EventHandler OrderChanged;
        public event EventHandler SelectionChanged;
        public IList<DetailLayoutItem> OrderedItems { get { return _items; } }
        public int SelectedIndex { get; private set; } = -1;

        public DetailLayoutPreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(232, 236, 241);
            Dock = DockStyle.Fill;
            TabStop = true;
        }

        public void SetLayout(IEnumerable<DetailLayoutItem> items, FrameDefinition frame, int scale, DetailLayoutOptions options, bool replaceOrder)
        {
            if (replaceOrder)
            {
                _items.Clear();
                _items.AddRange((items ?? Enumerable.Empty<DetailLayoutItem>()).Where(x => x != null));
            }
            _frame = frame;
            _scale = Math.Max(1, scale);
            _options = options ?? new DetailLayoutOptions();
            Rebuild(true);
        }

        public void SelectIndex(int index)
        {
            SelectedIndex = index >= 0 && index < _items.Count ? index : -1;
            Invalidate();
        }

        private void Rebuild(bool fit)
        {
            try
            {
                _plan = DetailLayoutService.ComputeLayout(_items, _frame, _scale, _options);
                _error = null;
                if (fit) FitPages();
            }
            catch (Exception exception) { _plan = null; _error = exception.Message; }
            Invalidate();
        }

        private void FitPages()
        {
            if (_plan == null || Width < 80 || Height < 80) return;
            var gap = Math.Max(0d, _options.PageGap) * _scale;
            var totalWidth = _plan.PageCount * _plan.PageWidth + Math.Max(0, _plan.PageCount - 1) * gap;
            _zoom = Math.Min((Width - 60f) / (float)Math.Max(1d, totalWidth), (Height - 70f) / (float)Math.Max(1d, _plan.PageHeight));
            _zoom = Math.Max(0.00005f, Math.Min(20f, _zoom));
            var shownWidth = (float)totalWidth * _zoom;
            var shownHeight = (float)_plan.PageHeight * _zoom;
            _offset = new PointF(Math.Max(24f, (Width - shownWidth) / 2f), Math.Max(34f, (Height - shownHeight) / 2f));
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitPages(); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_plan == null) return;
            var before = _zoom;
            _zoom = Math.Max(0.00005f, Math.Min(20f, _zoom * (e.Delta > 0 ? 1.12f : 0.89f)));
            if (before > 0f)
            {
                _offset.X = e.X - (e.X - _offset.X) * _zoom / before;
                _offset.Y = e.Y - (e.Y - _offset.Y) * _zoom / before;
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var slot = HitTest(e.Location);
            SelectedIndex = slot == null ? -1 : _items.IndexOf(slot.Item);
            _dragIndex = e.Button == MouseButtons.Left ? SelectedIndex : -1;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIndex < 0 || e.Button != MouseButtons.Left) return;
            _hoverSlot = HitTest(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragIndex >= 0 && _hoverSlot != null)
            {
                var target = _items.IndexOf(_hoverSlot.Item);
                if (target >= 0 && target != _dragIndex)
                {
                    var item = _items[_dragIndex];
                    _items.RemoveAt(_dragIndex);
                    _items.Insert(Math.Max(0, Math.Min(target, _items.Count)), item);
                    SelectedIndex = _items.IndexOf(item);
                    Rebuild(false);
                    OrderChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            _dragIndex = -1;
            _hoverSlot = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_plan == null)
            {
                DrawCentered(graphics, string.IsNullOrWhiteSpace(_error) ? "点击“添加大样”或“框选平面”开始" : _error, ClientRectangle, Color.FromArgb(90, 100, 112), 10f);
                return;
            }
            using (var pagePen = new Pen(Color.FromArgb(80, 92, 108), 1.4f))
            using (var rangePen = new Pen(Color.FromArgb(145, 155, 168), 1f) { DashStyle = DashStyle.Dash })
            using (var selectedPen = new Pen(Color.FromArgb(220, 115, 25), 2f))
            using (var dragFill = new SolidBrush(Color.FromArgb(45, 240, 165, 105)))
            {
                for (var page = 0; page < _plan.PageCount; page++)
                {
                    var rect = PageRect(page);
                    graphics.FillRectangle(Brushes.White, rect);
                    graphics.DrawRectangle(pagePen, rect.X, rect.Y, rect.Width, rect.Height);
                    var range = new RectangleF(rect.X + (float)_plan.ContentLeft * _zoom,
                        rect.Y + (float)(_plan.PageHeight - _plan.ContentTop) * _zoom,
                        (float)(_plan.ContentRight - _plan.ContentLeft) * _zoom,
                        (float)(_plan.ContentTop - _plan.ContentBottom) * _zoom);
                    graphics.DrawRectangle(rangePen, range.X, range.Y, range.Width, range.Height);
                    var gridX = range.Left;
                    for (var column = 0; column < _plan.Columns - 1; column++)
                    {
                        gridX += (float)_plan.ColumnWidths[column] * _zoom;
                        graphics.DrawLine(rangePen, gridX, range.Top, gridX, range.Bottom);
                    }
                    var gridY = range.Top;
                    for (var row = 0; row < _plan.Rows - 1; row++)
                    {
                        gridY += (float)_plan.RowHeights[row] * _zoom;
                        graphics.DrawLine(rangePen, range.Left, gridY, range.Right, gridY);
                    }
                    DrawCentered(graphics, "第 " + (page + 1) + " 页", new RectangleF(rect.X, rect.Y - 24f, rect.Width, 20f), Color.FromArgb(55, 68, 84), 9f);
                }
                foreach (var slot in _plan.Slots)
                {
                    var rect = SlotRect(slot);
                    var cell = CellRect(slot);
                    var index = _items.IndexOf(slot.Item);
                    var selected = index == SelectedIndex;
                    var dragging = index == _dragIndex;
                    if (dragging) graphics.FillRectangle(dragFill, cell);
                    if (selected) graphics.DrawRectangle(selectedPen, cell.X, cell.Y, cell.Width, cell.Height);
                    DrawDetail(graphics, slot.Item, rect, index);
                    if (slot.Item.AddIndexNumber)
                    {
                        var number = _items.Take(index + 1).Count(item => item.AddIndexNumber);
                        DrawIndexMarker(graphics, slot, number);
                    }
                }
                if (_dragIndex >= 0 && _hoverSlot != null)
                {
                    var rect = CellRect(_hoverSlot);
                    using (var hover = new Pen(Color.FromArgb(220, 85, 45), 2f) { DashStyle = DashStyle.Dash }) graphics.DrawRectangle(hover, rect.X, rect.Y, rect.Width, rect.Height);
                }
            }
            DrawCentered(graphics, "拖动大样调整顺序；滚轮缩放预览", new RectangleF(0, Height - 24f, Width, 20f), Color.DimGray, 8.5f);
        }

        private RectangleF PageRect(int page)
        {
            var gap = (float)(Math.Max(0d, _options.PageGap) * _scale) * _zoom;
            return new RectangleF(_offset.X + page * ((float)_plan.PageWidth * _zoom + gap), _offset.Y, (float)_plan.PageWidth * _zoom, (float)_plan.PageHeight * _zoom);
        }

        private RectangleF SlotRect(DetailLayoutSlot slot)
        {
            var page = PageRect(slot.Page);
            return new RectangleF(page.X + (float)slot.X * _zoom,
                page.Y + (float)(_plan.PageHeight - slot.Y - slot.Height) * _zoom,
                Math.Max(2f, (float)slot.Width * _zoom), Math.Max(2f, (float)slot.Height * _zoom));
        }

        private RectangleF CellRect(DetailLayoutSlot slot)
        {
            var page = PageRect(slot.Page);
            return new RectangleF(page.X + (float)slot.CellX * _zoom,
                page.Y + (float)(_plan.PageHeight - slot.CellY - slot.CellHeight) * _zoom,
                Math.Max(2f, (float)slot.CellWidth * _zoom), Math.Max(2f, (float)slot.CellHeight * _zoom));
        }

        private DetailLayoutSlot HitTest(Point point)
        { return _plan == null ? null : _plan.Slots.LastOrDefault(x => CellRect(x).Contains(point)); }

        private void DrawIndexMarker(Graphics graphics, DetailLayoutSlot slot, int number)
        {
            var page = PageRect(slot.Page);
            var radius = Math.Max(3.5d * _scale, 35d);
            var centerX = page.X + (float)(slot.X - Math.Max(4d * _scale, 40d)) * _zoom;
            var centerY = page.Y + (float)(_plan.PageHeight - slot.Y - Math.Max(3.5d * _scale, 35d)) * _zoom;
            var shownRadius = Math.Max(4f, Math.Min(14f, (float)radius * _zoom));
            using (var pen = new Pen(Color.FromArgb(45, 62, 78), 1f))
            {
                graphics.DrawEllipse(pen, centerX - shownRadius, centerY - shownRadius, shownRadius * 2f, shownRadius * 2f);
            }
            DrawCentered(graphics, number.ToString(), new RectangleF(centerX - shownRadius, centerY - shownRadius, shownRadius * 2f, shownRadius * 2f), Color.FromArgb(35, 50, 68), Math.Max(6f, shownRadius * 0.9f));
        }

        private static void DrawDetail(Graphics graphics, DetailLayoutItem item, RectangleF rect, int index)
        {
            if (item == null || rect.Width < 4f || rect.Height < 4f) return;
            var header = Math.Min(18f, Math.Max(10f, rect.Height * 0.16f));
            var drawing = new RectangleF(rect.X + 3f, rect.Y + header + 2f, Math.Max(1f, rect.Width - 6f), Math.Max(1f, rect.Height - header - 5f));
            var factor = Math.Min(drawing.Width / (float)Math.Max(1e-6d, item.Width), drawing.Height / (float)Math.Max(1e-6d, item.Height));
            var x0 = drawing.X + (drawing.Width - (float)item.Width * factor) / 2f;
            var y0 = drawing.Bottom - (drawing.Height - (float)item.Height * factor) / 2f;
            using (var geometryPen = new Pen(Color.FromArgb(45, 62, 78), 0.8f))
            using (var proxyPen = new Pen(Color.FromArgb(135, 145, 155), 0.7f) { DashStyle = DashStyle.Dot })
            using (var textBrush = new SolidBrush(Color.FromArgb(65, 78, 92)))
            using (var textFont = new Font("Microsoft YaHei UI", 6.5f))
            {
                foreach (var primitive in item.Preview.Take(2500))
                {
                    var x1 = x0 + (float)(primitive.X1 - item.MinPoint.X) * factor;
                    var y1 = y0 - (float)(primitive.Y1 - item.MinPoint.Y) * factor;
                    var x2 = x0 + (float)(primitive.X2 - item.MinPoint.X) * factor;
                    var y2 = y0 - (float)(primitive.Y2 - item.MinPoint.Y) * factor;
                    if (primitive.Kind == DetailPreviewPrimitiveKind.Line) graphics.DrawLine(geometryPen, x1, y1, x2, y2);
                    else
                    {
                        var box = RectangleF.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
                        if (box.Width < 1f) box.Width = 1f; if (box.Height < 1f) box.Height = 1f;
                        if (primitive.Kind == DetailPreviewPrimitiveKind.Ellipse) graphics.DrawEllipse(geometryPen, box);
                        else if (primitive.Kind == DetailPreviewPrimitiveKind.Text && box.Width > 8f && box.Height > 5f)
                            graphics.DrawString(primitive.Text ?? string.Empty, textFont, textBrush, box);
                        else graphics.DrawRectangle(proxyPen, box.X, box.Y, box.Width, box.Height);
                    }
                }
            }
            DrawCentered(graphics, item.Name + "  " + (string.IsNullOrWhiteSpace(item.ScaleText) ? "" : item.ScaleText), new RectangleF(rect.X + 2f, rect.Y + 1f, Math.Max(1f, rect.Width - 4f), header), Color.FromArgb(30, 52, 72), 7.5f);
        }

        private static void DrawCentered(Graphics graphics, string text, RectangleF rect, Color color, float size)
        {
            if (rect.Width <= 0f || rect.Height <= 0f) return;
            using (var font = new Font("Microsoft YaHei UI", size))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(text ?? string.Empty, font, brush, rect, format);
        }
        private static void DrawCentered(Graphics graphics, string text, Rectangle rect, Color color, float size) { DrawCentered(graphics, text, (RectangleF)rect, color, size); }
    }
}
