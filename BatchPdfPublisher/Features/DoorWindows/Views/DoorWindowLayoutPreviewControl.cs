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
    /// <summary>
    /// 门窗立面整页排版预览：按登记图框分页绘制，每个门窗显示为占位框，
    /// 可拖拽调整顺序（按住拖动到另一槽位释放，该项插入到目标位置）。
    /// 预览与 CAD 插入共用 DoorWindowElevationInsertionService.ComputeLayout。
    /// </summary>
    internal sealed class DoorWindowLayoutPreviewControl : Control
    {
        private IList<DoorWindowScheduleItem> _items = new List<DoorWindowScheduleItem>();
        private FrameDefinition _frame;
        private int _scale = 50;
        private DoorWindowElevationInsertionService.DoorWindowLayoutOptions _options = new DoorWindowElevationInsertionService.DoorWindowLayoutOptions();
        private DoorWindowElevationInsertionService.DoorWindowLayoutPlan _plan;
        private PointF _pageOffset = new PointF(14f, 26f);
        private float _zoom = 1f;
        private int _draggingIndex = -1;
        private DoorWindowElevationInsertionService.DoorWindowLayoutSlot _hoverSlot;
        private DoorWindowElevationInsertionService.DoorWindowLayoutSlot _selectedSlot;
        private string _statusText;

        public DoorWindowLayoutPreviewControl()
        {
            DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill;
            MouseDown += OnPreviewMouseDown;
            MouseMove += OnPreviewMouseMove;
            MouseUp += OnPreviewMouseUp;
            MouseWheel += OnPreviewMouseWheel;
            Resize += (s, e) => RecomputeZoom();
        }

        /// <summary>当前是否有选中的门窗槽位。</summary>
        public bool HasSelection { get { return _selectedSlot != null; } }

        /// <summary>选中槽位所在页（1 起），无选中返回 0。</summary>
        public int SelectedPage { get { return _selectedSlot == null ? 0 : _selectedSlot.Page + 1; } }

        /// <summary>把选中的门窗锁定到它当前所在的页；无选中则忽略。</summary>
        public void LockSelectedToPage()
        {
            if (_selectedSlot == null || _selectedSlot.Item == null) return;
            _selectedSlot.Item.LockedPage = _selectedSlot.Page + 1;
            RecomputeLayout(); Invalidate();
        }

        /// <summary>取消选中门窗的锁定；无选中则忽略。</summary>
        public void UnlockSelected()
        {
            if (_selectedSlot == null || _selectedSlot.Item == null) return;
            _selectedSlot.Item.LockedPage = 0;
            RecomputeLayout(); Invalidate();
        }

        /// <summary>清空选中状态（供表单在切换图框/参数后调用）。</summary>
        public void ClearSelection() { _selectedSlot = null; Invalidate(); }

        public void SetLayout(IList<DoorWindowScheduleItem> items, FrameDefinition frame, int scale, DoorWindowElevationInsertionService.DoorWindowLayoutOptions options)
        {
            _items = (items ?? new List<DoorWindowScheduleItem>()).Where(x => x != null).ToList();
            _frame = frame; _scale = Math.Max(1, scale); _options = options ?? new DoorWindowElevationInsertionService.DoorWindowLayoutOptions();
            _selectedSlot = null;
            try { _plan = DoorWindowElevationInsertionService.ComputeLayout(_items, _scale, _frame, _options); _statusText = null; }
            catch (Exception exception) { _plan = null; _statusText = exception.Message; }
            RecomputeZoom(); Invalidate();
        }

        /// <summary>当前排序后的门窗列表（拖拽调整后的顺序，即插入顺序）。</summary>
        public IList<DoorWindowScheduleItem> OrderedItems => _items;

        private void RecomputeZoom()
        {
            if (Width <= 0 || Height <= 0) { _zoom = 1f; return; }
            if (_plan == null || _plan.PageWidth <= 0 || _plan.PageHeight <= 0) { _zoom = 1f; return; }
            var totalWidth = _plan.PageCount * _plan.PageWidth + (_plan.PageCount - 1) * Math.Max(0d, _options.PageGap);
            if (totalWidth <= 0d) { _zoom = 1f; return; }
            var availableHeight = Math.Max(1f, Height - 26f - 24f);
            _zoom = Math.Min(Math.Max(0.02f, (Width - 28f) / (float)totalWidth), Math.Max(0.02f, availableHeight / (float)_plan.PageHeight));
            _zoom = Safe(_zoom, 1f);
        }

        private RectangleF PageRect(int page)
        {
            var x = _pageOffset.X + page * ((float)_plan.PageWidth + (float)_options.PageGap) * _zoom;
            var w = (float)_plan.PageWidth * _zoom; var h = (float)_plan.PageHeight * _zoom;
            return new RectangleF(Safe(x, _pageOffset.X), Safe(_pageOffset.Y, 0f), Safe(w, 0f), Safe(h, 0f));
        }

        private RectangleF SlotRect(DoorWindowElevationInsertionService.DoorWindowLayoutSlot slot)
        {
            // slot.X/slot.Y 是立面插入原点（相对页左下角，模型单位）。
            // 立面顶边 = slot.Y + 洞口高；占位框高 = FootprintHeight（含下方标注与标题）。
            var topY = slot.Y + Math.Max(0d, slot.Item == null ? 0d : slot.Item.Height);
            var x = _pageOffset.X + slot.Page * ((float)_plan.PageWidth + (float)_options.PageGap) * _zoom + (float)slot.X * _zoom;
            var y = _pageOffset.Y + (float)(_plan.PageHeight - topY) * _zoom;
            var w = (float)slot.FootprintWidth * _zoom; var h = (float)slot.FootprintHeight * _zoom;
            return new RectangleF(Safe(x, _pageOffset.X), Safe(y, _pageOffset.Y), Safe(w, 0f), Safe(h, 0f));
        }

        private DoorWindowElevationInsertionService.DoorWindowLayoutSlot HitTest(Point location)
        {
            if (_plan == null) return null;
            var found = new List<KeyValuePair<DoorWindowElevationInsertionService.DoorWindowLayoutSlot, float>>();
            foreach (var slot in _plan.Slots)
            {
                var rect = SlotRect(slot);
                if (rect.Width > 0f && rect.Height > 0f && rect.Contains(location)) found.Add(new KeyValuePair<DoorWindowElevationInsertionService.DoorWindowLayoutSlot, float>(slot, rect.Width * rect.Height));
            }
            return found.OrderBy(x => x.Value).Select(x => x.Key).FirstOrDefault();
        }

        private void OnPreviewMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var slot = HitTest(e.Location);
            _selectedSlot = slot;
            _draggingIndex = slot == null ? -1 : _items.IndexOf(slot.Item);
            Invalidate();
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            var hover = HitTest(e.Location);
            _hoverSlot = _draggingIndex < 0 ? hover : (hover != null && !ReferenceEquals(hover.Item, _items[_draggingIndex]) ? hover : null);
            if (_draggingIndex < 0 || _draggingIndex >= _items.Count) { Invalidate(); return; }
            if (e.Button != MouseButtons.Left) { _draggingIndex = -1; _hoverSlot = null; Invalidate(); return; }
            var dragItem = _items[_draggingIndex];
            var sourceSlot = _plan == null ? null : _plan.Slots.FirstOrDefault(x => ReferenceEquals(x.Item, dragItem));
            var target = HitTest(e.Location);
            if (target != null && !ReferenceEquals(target.Item, dragItem))
            {
                var targetIndex = _items.IndexOf(target.Item);
                var list = _items.ToList();
                list.RemoveAt(_draggingIndex);
                var insertAt = targetIndex > _draggingIndex ? targetIndex - 1 : targetIndex;
                if (insertAt < 0) insertAt = 0;
                list.Insert(insertAt, dragItem);
                _items = list;
                _draggingIndex = insertAt;
                // 跨页拖拽：自动把该门窗锁定到目标页（拖到第 N 页就钉在第 N 页）。
                if (sourceSlot != null && target.Page != sourceSlot.Page) dragItem.LockedPage = target.Page + 1;
                RecomputeLayout();
                _hoverSlot = null;
                _selectedSlot = _plan == null ? null : _plan.Slots.FirstOrDefault(x => ReferenceEquals(x.Item, dragItem));
            }
            Invalidate();
        }

        private void OnPreviewMouseUp(object sender, MouseEventArgs e)
        {
            _draggingIndex = -1; _hoverSlot = null; Invalidate();
        }

        private void OnPreviewMouseWheel(object sender, MouseEventArgs e)
        {
            if (_plan == null) return;
            var factor = e.Delta > 0 ? 1.1f : 1f / 1.1f;
            _zoom = Safe(Math.Max(0.05f, Math.Min(6f, _zoom * factor)), 1f);
            Invalidate();
        }

        private void RecomputeLayout()
        {
            try { _plan = DoorWindowElevationInsertionService.ComputeLayout(_items, _scale, _frame, _options); _statusText = null; }
            catch (Exception exception) { _plan = null; _statusText = exception.Message; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 用独立内存位图绘制，再一次性贴回控件。AutoCAD 宿主下 WinForms
            // 控件的 e.Graphics 可能在嵌套消息泵中被提前释放（此时任何 GDI+
            // 调用都抛“参数无效”），改用自管生命周期的 Graphics 彻底规避。
            if (Width <= 0 || Height <= 0) return;
            try
            {
                using (var buffer = new Bitmap(Width, Height))
                {
                    using (var bufferGraphics = Graphics.FromImage(buffer))
                    {
                        bufferGraphics.Clear(Color.White);
                        PaintPreview(bufferGraphics);
                    }
                    e.Graphics.DrawImage(buffer, 0, 0, Width, Height);
                }
            }
            catch (Exception exception)
            {
                _statusText = "预览绘制失败：" + exception.Message;
                LogDrawError(exception);
                try { if (ClientRectangle.Width > 0 && ClientRectangle.Height > 0) DrawCentered(e.Graphics, _statusText, ClientRectangle, Color.Firebrick, 9F); } catch { }
            }
        }

        private void LogDrawError(Exception exception)
        {
            try
            {
                var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "door-window-preview.log");
                var info = "frame=" + (_frame == null ? "null" : _frame.PaperDisplay + "/" + _frame.BlockName)
                    + " scale=" + _scale + " zoom=" + _zoom
                    + " items=" + _items.Count
                    + " plan=" + (_plan == null ? "null" : _plan.PageCount + "页 " + _plan.PageWidth + "x" + _plan.PageHeight);
                System.IO.File.AppendAllText(log, DateTime.Now.ToString("O") + " " + info + "\r\n" + exception + "\r\n");
            }
            catch { }
        }

        private void PaintPreview(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_frame == null) { DrawCentered(graphics, "请选择排版图框", ClientRectangle, Color.Gray, 11F); return; }
            if (_plan == null) { DrawCentered(graphics, string.IsNullOrWhiteSpace(_statusText) ? "排版失败" : _statusText, ClientRectangle, Color.Firebrick, 10F); return; }

            using (var pagePen = new Pen(Color.FromArgb(70, 90, 110), 1.6f))
            using (var contentPen = new Pen(Color.FromArgb(150, 160, 175), 1f) { DashStyle = DashStyle.Dash })
            using (var slotPen = new Pen(Color.FromArgb(30, 110, 175), 1.3f))
            using (var dragPen = new Pen(Color.FromArgb(200, 90, 60), 1.6f))
            using (var font = new Font("Microsoft YaHei UI", 8.5f))
            using (var brush = new SolidBrush(Color.FromArgb(25, 36, 48)))
            {
                for (var page = 0; page < _plan.PageCount; page++)
                {
                    var rect = PageRect(page);
                    if (rect.Width <= 0f || rect.Height <= 0f) continue;
                    graphics.FillRectangle(Brushes.White, rect);
                    DrawRectSafe(graphics, pagePen, rect.X, rect.Y, rect.Width, rect.Height, "page");
                    var contentLeft = Safe(rect.X + (float)_plan.ContentLeft * _zoom, rect.X);
                    var contentTop = Safe(rect.Y + (float)(_plan.PageHeight - _plan.ContentTop) * _zoom, rect.Y);
                    var contentW = Safe((float)(_plan.ContentRight - _plan.ContentLeft) * _zoom, 0f);
                    var contentH = Safe((float)(_plan.ContentTop - _plan.ContentBottom) * _zoom, 0f);
                    if (contentW > 0f && contentH > 0f) DrawRectSafe(graphics, contentPen, contentLeft, contentTop, contentW, contentH, "content");
                    DrawCentered(graphics, "第 " + (page + 1) + " 页", new RectangleF(rect.X, Math.Max(2f, rect.Y - 22), rect.Width, 20), Color.FromArgb(60, 75, 92), 9F);
                }
                foreach (var slot in _plan.Slots)
                {
                    var rect = SlotRect(slot);
                    if (rect.Width <= 0f || rect.Height <= 0f) continue;
                    var index = _items.IndexOf(slot.Item);
                    var isDragging = index == _draggingIndex;
                    var isSelected = _selectedSlot != null && ReferenceEquals(_selectedSlot.Item, slot.Item);
                    var isLocked = slot.Item != null && slot.Item.LockedPage > 0;
                    using (var pen = isDragging ? dragPen : isSelected ? new Pen(Color.FromArgb(220, 120, 30), 2f) : slotPen)
                    {
                        DrawRectSafe(graphics, pen, rect.X, rect.Y, rect.Width, rect.Height, "slot");
                        if (isDragging) graphics.FillRectangle(new SolidBrush(Color.FromArgb(40, 245, 180, 160)), rect);
                    }
                    // 锁定标记：槽位左上角显示小锁+所在页号。
                    if (isLocked)
                    {
                        using (var lockBrush = new SolidBrush(Color.FromArgb(170, 80, 20)))
                        using (var lockFont = new Font("Microsoft YaHei UI", 7.5f))
                        {
                            var lockText = "锁定" + slot.Item.LockedPage;
                            var lockSize = graphics.MeasureString(lockText, lockFont);
                            var lx = Math.Max(rect.X + 2f, rect.X + 2f);
                            var ly = Math.Max(rect.Y + 1f, rect.Y + 1f);
                            graphics.FillRectangle(new SolidBrush(Color.FromArgb(235, 250, 240)), lx, ly, lockSize.Width + 4f, lockSize.Height + 2f);
                            graphics.DrawString(lockText, lockFont, lockBrush, lx + 2f, ly + 1f);
                        }
                    }
                    // 槽位内绘制该门窗的实际立面图（含图名/比例/标注，分区布局避免重叠）。
                    DrawItemElevation(graphics, slot.Item, rect, index + 1);
                }
                // 目标插入位置高亮框：拖动时指示门窗将被移动到的槽位。
                if (_hoverSlot != null && _draggingIndex >= 0)
                {
                    var targetRect = SlotRect(_hoverSlot);
                    if (targetRect.Width > 0f && targetRect.Height > 0f)
                    {
                        using (var highlight = new Pen(Color.FromArgb(220, 90, 60), 2f) { DashStyle = DashStyle.Dash })
                        using (var fill = new SolidBrush(Color.FromArgb(50, 255, 180, 60)))
                        {
                            graphics.FillRectangle(fill, targetRect);
                            graphics.DrawRectangle(highlight, targetRect.X, targetRect.Y, targetRect.Width, targetRect.Height);
                        }
                    }
                }
            }
            var hintY = Math.Max(2f, Height - 22);
            var hintW = Math.Max(0f, Width - 12);
            if (hintW > 0f) DrawCentered(graphics, "提示：单击选中门窗，工具栏“锁定到本页/解锁”固定其所在页；拖动调整顺序（跨页拖动自动锁定到目标页）；滚轮缩放。", new RectangleF(6, hintY, hintW, 18), Color.DimGray, 8.5F);
        }

        /// <summary>把 NaN/Infinity/负值替换为 fallback，保证传给 GDI+ 的都是有限非负值。</summary>
        private static float Safe(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return fallback;
            return value;
        }

        /// <summary>在槽位矩形内分区绘制单个门窗：顶部序号、中部立面图、底部标注+图名+比例，避免重叠。</summary>
        private void DrawItemElevation(Graphics graphics, DoorWindowScheduleItem item, RectangleF rect, int order)
        {
            if (item == null || rect.Width < 8f || rect.Height < 8f) return;
            DoorWindowElevationGeometry geometry;
            try { geometry = DoorWindowElevationGeometryBuilder.Build(item); }
            catch { return; }
            var minX = -geometry.BayLeftExtent; var maxX = geometry.HoleWidth + geometry.BayRightExtent;
            var drawGeometryWidth = Safe((float)(maxX - minX), 1f); var holeH = Safe((float)geometry.HoleHeight, 1f);
            if (drawGeometryWidth <= 0f || holeH <= 0f) return;

            // 分区：顶部序号条(13px)，底部图名+比例(16px)，中间画门窗+标注。
            var topBand = 13f;
            var bottomBand = 16f;
            var mid = new RectangleF(rect.X + 2f, rect.Y + topBand, Math.Max(1f, rect.Width - 4f), Math.Max(1f, rect.Height - topBand - bottomBand));
            // 标注带：门窗图底部与 mid 底部之间留 dimBand+2px 画标注线/数字。
            var dimBand = 9f;
            var availH = Math.Max(1f, mid.Height - dimBand - 2f);
            var scale = Math.Min(mid.Width / drawGeometryWidth, availH / holeH);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) scale = 0.05f;

            // 门窗图顶对齐 mid.Top，向下画完整 holeH*scale 高；底部=originY，其下为标注带（屏幕 y 向下）。
            var originX = mid.Left + (mid.Width - drawGeometryWidth * scale) / 2f - (float)minX * scale;
            var originY = mid.Top + holeH * scale;

            using (var framePen = new Pen(Color.FromArgb(40, 60, 80), Math.Max(1f, scale * 1.5f)))
            using (var mullionPen = new Pen(Color.FromArgb(60, 90, 120), Math.Max(0.8f, scale)))
            using (var holePen = new Pen(Color.FromArgb(160, 170, 180), Math.Max(0.7f, scale * 0.8f)) { DashStyle = DashStyle.Dash })
            using (var sashPen = new Pen(Color.FromArgb(150, 150, 150), Math.Max(0.8f, scale)))
            using (var openingPen = new Pen(Color.FromArgb(0, 120, 180), Math.Max(0.7f, scale * 0.8f)) { DashStyle = DashStyle.Dash })
            using (var dimPen = new Pen(Color.FromArgb(80, 130, 80), Math.Max(0.7f, scale * 0.7f)))
            using (var smallFont = new Font("Microsoft YaHei UI", Math.Max(5.5f, Math.Min(7.5f, scale * 1.8f))))
            using (var dimBrush = new SolidBrush(Color.FromArgb(60, 110, 60)))
            using (var textFont = new Font("Microsoft YaHei UI", Math.Max(6f, Math.Min(8f, scale * 2f))))
            using (var textBrush = new SolidBrush(Color.FromArgb(30, 45, 60)))
            {
                // 顶部序号。
                DrawCentered(graphics, order + "." + (item.Code ?? "未编号"), new RectangleF(rect.X, rect.Y, rect.Width, topBand), Color.FromArgb(60, 75, 92), Math.Max(6f, Math.Min(8f, scale * 2f)));

                // 门窗几何。
                foreach (var line in geometry.Lines)
                {
                    var x1 = originX + (float)line.X1 * scale;
                    var y1 = originY - (float)line.Y1 * scale;
                    var x2 = originX + (float)line.X2 * scale;
                    var y2 = originY - (float)line.Y2 * scale;
                    if (!AllFinite(x1, y1, x2, y2)) continue;
                    var pen = line.Role == DoorWindowLineRole.Hole ? holePen : line.Role == DoorWindowLineRole.Frame ? framePen : line.Role == DoorWindowLineRole.Mullion ? mullionPen : line.Role == DoorWindowLineRole.SashFrame ? sashPen : openingPen;
                    graphics.DrawLine(pen, x1, y1, x2, y2);
                }

                // 总宽标注线（门窗图下方）+ 数字。
                var dimBottom = originY + 3f;
                if (dimBottom + 2f <= mid.Bottom)
                {
                    var mainLeft = originX; var mainRight = originX + (float)geometry.HoleWidth * scale;
                    graphics.DrawLine(dimPen, mainLeft, dimBottom, mainRight, dimBottom);
                    graphics.DrawLine(dimPen, mainLeft, originY, mainLeft, dimBottom + 2f);
                    graphics.DrawLine(dimPen, mainRight, originY, mainRight, dimBottom + 2f);
                    var wText = item.Width.ToString("0.##");
                    var wSize = graphics.MeasureString(wText, smallFont);
                    graphics.FillRectangle(Brushes.White, mainLeft + (mainRight - mainLeft) / 2f - wSize.Width / 2f, dimBottom - wSize.Height / 2f, wSize.Width, wSize.Height);
                    graphics.DrawString(wText, smallFont, dimBrush, mainLeft + (mainRight - mainLeft) / 2f - wSize.Width / 2f, dimBottom - wSize.Height / 2f);
                }

                // 底部：图名 + 比例。
                var bottomY = rect.Y + rect.Height - bottomBand;
                DrawCentered(graphics, item.Code ?? "未编号", new RectangleF(rect.X, bottomY, rect.Width, bottomBand * 0.55f), Color.FromArgb(30, 45, 60), Math.Max(6f, Math.Min(8f, scale * 2f)));
                DrawCentered(graphics, "1:" + _scale.ToString(System.Globalization.CultureInfo.InvariantCulture), new RectangleF(rect.X, bottomY + bottomBand * 0.55f, rect.Width, bottomBand * 0.45f), Color.FromArgb(90, 100, 115), Math.Max(5f, Math.Min(7f, scale * 1.7f)));
            }
        }

        private static bool AllFinite(params float[] values)
        {
            foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            return true;
        }

        /// <summary>带参数日志的 DrawRectangle：失败时把 x/y/w/h 写入日志，便于定位 GDI+ 范围限制。</summary>
        private void DrawRectSafe(Graphics graphics, Pen pen, float x, float y, float w, float h, string tag)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)
                || float.IsNaN(w) || float.IsInfinity(w) || float.IsNaN(h) || float.IsInfinity(h)
                || w <= 0f || h <= 0f) return;
            try
            {
                graphics.DrawRectangle(pen, x, y, w, h);
            }
            catch (Exception exception)
            {
                try
                {
                    var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "door-window-preview.log");
                    System.IO.File.AppendAllText(log, DateTime.Now.ToString("O") + " " + tag + " rect x=" + x + " y=" + y + " w=" + w + " h=" + h + " zoom=" + _zoom + "\r\n" + exception + "\r\n");
                }
                catch { }
            }
        }

        private static void DrawCentered(Graphics graphics, string text, RectangleF rectangle, Color color, float size)
        {
            // NaN 会让 rectangle.Width<=0f 判断为 false，必须显式排除 NaN/Infinity。
            if (float.IsNaN(rectangle.X) || float.IsInfinity(rectangle.X)
                || float.IsNaN(rectangle.Y) || float.IsInfinity(rectangle.Y)
                || float.IsNaN(rectangle.Width) || float.IsInfinity(rectangle.Width)
                || float.IsNaN(rectangle.Height) || float.IsInfinity(rectangle.Height)) return;
            if (rectangle.Width <= 0f || rectangle.Height <= 0f || size <= 0f) return;
            using (var font = new Font("Microsoft YaHei UI", Math.Max(4f, size)))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(text ?? string.Empty, font, brush, rectangle, format);
        }
        private static void DrawCentered(Graphics graphics, string text, Rectangle rectangle, Color color, float size) { DrawCentered(graphics, text, (RectangleF)rectangle, color, size); }
    }
}
