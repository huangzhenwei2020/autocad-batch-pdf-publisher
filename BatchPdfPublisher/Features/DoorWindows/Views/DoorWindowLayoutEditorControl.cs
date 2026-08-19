using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowLayoutEditorControl : Control
    {
        private sealed class Divider
        {
            public bool Vertical; public double Coordinate, Start, End;
            public readonly List<int> Before = new List<int>();
            public readonly List<int> After = new List<int>();
        }

        private readonly List<DoorWindowLayoutCell> _cells = new List<DoorWindowLayoutCell>();
        private readonly HashSet<int> _selectedIndices = new HashSet<int>();
        private int _selected = -1;
        private Divider _dragDivider;
        private RectangleF _drawingArea;
        private double _frameWidth = 1d, _frameHeight = 1d;
        private double _outerProfileWidth = 50d, _mullionProfileWidth = 50d;
        private double _installationGap = 20d;
        private bool _hasInstallationGap = true;
        private bool _hasOuterFrame = true, _hasMullion = true;
        private string _doorFrameType = "N型";

        public event EventHandler LayoutChanged;
        public event EventHandler SelectedCellChanged;

        public DoorWindowLayoutEditorControl()
        {
            DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill; MinimumSize = new Size(360, 300);
        }

        public IList<DoorWindowLayoutCell> Cells { get { return _cells; } }
        public IEnumerable<DoorWindowLayoutCell> OrderedCells { get { return _cells.OrderByDescending(x => x.Top).ThenBy(x => x.Left).ThenByDescending(x => x.Bottom); } }
        public DoorWindowLayoutCell SelectedCell { get { return _selected >= 0 && _selected < _cells.Count ? _cells[_selected] : null; } }
        public IEnumerable<DoorWindowLayoutCell> SelectedCells { get { return _selectedIndices.Where(x => x >= 0 && x < _cells.Count).Select(x => _cells[x]); } }
        public int SelectionCount { get { return _selectedIndices.Count; } }

        public void LoadLayout(double width, double height, IEnumerable<DoorWindowLayoutCell> cells)
        {
            _frameWidth = Math.Max(1d, width); _frameHeight = Math.Max(1d, height); _cells.Clear();
            foreach (var cell in cells ?? Enumerable.Empty<DoorWindowLayoutCell>()) _cells.Add(Copy(cell));
            if (_cells.Count == 0) _cells.Add(new DoorWindowLayoutCell { Left = 0, Bottom = 0, Right = _frameWidth, Top = _frameHeight, Opening = "固定", Material = "无" });
            var first = OrderedCells.FirstOrDefault(); _selected = first == null ? -1 : _cells.IndexOf(first); _selectedIndices.Clear(); if (_selected >= 0) _selectedIndices.Add(_selected); Invalidate(); RaiseSelectionChanged();
        }

        public void SetProfileWidths(double outerWidth, double mullionWidth) { _outerProfileWidth = Math.Max(0d, outerWidth); _mullionProfileWidth = Math.Max(0d, mullionWidth); Invalidate(); }
        public void SetInstallationGap(bool enabled, double gap) { _hasInstallationGap = enabled; _installationGap = Math.Max(0d, gap); Invalidate(); }
        public void SetConstruction(bool hasOuterFrame, bool hasMullion, string doorFrameType) { _hasOuterFrame = hasOuterFrame; _hasMullion = hasMullion; _doorFrameType = string.IsNullOrWhiteSpace(doorFrameType) ? "N型" : doorFrameType; Invalidate(); }
        public void ResizeLayout(double width, double height)
        {
            width = Math.Max(1d, width); height = Math.Max(1d, height); var sx = width / _frameWidth; var sy = height / _frameHeight;
            foreach (var cell in _cells) { cell.Left *= sx; cell.Right *= sx; cell.Bottom *= sy; cell.Top *= sy; }
            _frameWidth = width; _frameHeight = height; OnLayoutChanged();
        }

        public void SplitSelected(bool vertical)
        {
            var cell = SelectedCell; if (cell == null) return;
            var copy = Copy(cell);
            if (vertical)
            {
                var middle = (cell.Left + cell.Right) / 2d; cell.Right = middle; copy.Left = middle;
            }
            else
            {
                var middle = (cell.Bottom + cell.Top) / 2d; cell.Top = middle; copy.Bottom = middle;
            }
            copy.IsDoor = false; _cells.Insert(_selected + 1, copy); _selectedIndices.Clear(); _selectedIndices.Add(_selected); OnLayoutChanged();
        }

        public bool MergeSelected()
        {
            var selected = _selectedIndices.OrderBy(x => x).Where(x => x >= 0 && x < _cells.Count).Select(x => _cells[x]).ToList();
            if (selected.Count < 2 || selected.Any(x => x.IsDeleted)) return false;
            const double tolerance = .05d;
            var left = selected.Min(x => x.Left); var right = selected.Max(x => x.Right); var bottom = selected.Min(x => x.Bottom); var top = selected.Max(x => x.Top);
            var area = selected.Sum(x => (x.Right - x.Left) * (x.Top - x.Bottom));
            if (Math.Abs(area - (right - left) * (top - bottom)) > tolerance * Math.Max(1d, (right - left) + (top - bottom))) return false;
            var merged = Copy(SelectedCell ?? selected[0]); merged.Left = left; merged.Right = right; merged.Bottom = bottom; merged.Top = top;
            foreach (var cell in selected) MergeValues(merged, cell);
            _cells.RemoveAll(x => selected.Contains(x)); _cells.Add(merged); _selected = _cells.IndexOf(merged); _selectedIndices.Clear(); _selectedIndices.Add(_selected); OnLayoutChanged(); return true;
        }

        public bool CenterSelected()
        {
            var selected = SelectedCells.Where(x => !x.IsDeleted).ToList(); if (selected.Count == 0) return false;
            const double tolerance = .05d;
            var bottom = selected.Min(x => x.Bottom); var top = selected.Max(x => x.Top);
            if (selected.All(x => Math.Abs(x.Bottom - bottom) < tolerance && Math.Abs(x.Top - top) < tolerance))
            {
                var ordered = selected.OrderBy(x => x.Left).ToList();
                for (var i = 1; i < ordered.Count; i++) if (Math.Abs(ordered[i - 1].Right - ordered[i].Left) > tolerance) return false;
                var selectedWidth = ordered.Last().Right - ordered.First().Left; var side = (_frameWidth - selectedWidth) / 2d;
                if (side < 50d) return false;
                ScaleBand(bottom, top, 0d, ordered.First().Left, 0d, side);
                var shift = side - ordered.First().Left; foreach (var cell in selected) { cell.Left += shift; cell.Right += shift; }
                ScaleBand(bottom, top, ordered.Last().Right - shift, _frameWidth, side + selectedWidth, _frameWidth);
                OnLayoutChanged(); return true;
            }
            return false;
        }

        private void ScaleBand(double bottom, double top, double oldLeft, double oldRight, double newLeft, double newRight)
        {
            const double tolerance = .05d; if (oldRight - oldLeft < tolerance) return;
            foreach (var cell in _cells.Where(x => !_selectedIndices.Contains(_cells.IndexOf(x)) && Math.Abs(x.Bottom - bottom) < tolerance && Math.Abs(x.Top - top) < tolerance && x.Left >= oldLeft - tolerance && x.Right <= oldRight + tolerance))
            { cell.Left = newLeft + (cell.Left - oldLeft) / (oldRight - oldLeft) * (newRight - newLeft); cell.Right = newLeft + (cell.Right - oldLeft) / (oldRight - oldLeft) * (newRight - newLeft); }
        }

        public bool ToggleSelectedDeleted()
        {
            var selected = SelectedCells.ToList(); if (selected.Count == 0) return false;
            var delete = selected.Any(x => !x.IsDeleted);
            if (delete && _cells.Count(x => !x.IsDeleted && !selected.Contains(x)) == 0) return false;
            foreach (var cell in selected) { cell.IsDeleted = delete; if (delete) cell.IsDoor = false; }
            OnLayoutChanged(); return true;
        }

        public void ResetToFullFrame()
        {
            _cells.Clear(); _cells.Add(new DoorWindowLayoutCell { Left = 0, Bottom = 0, Right = _frameWidth, Top = _frameHeight, Opening = "固定", Material = "无" }); _selected = 0; _selectedIndices.Clear(); _selectedIndices.Add(0); OnLayoutChanged();
        }

        public bool SetSelectedWidth(double width)
        {
            var cell = SelectedCell; if (cell == null || width < 1d) return false;
            var divider = DividerAt(true, cell.Right, (cell.Bottom + cell.Top) / 2d);
            if (divider == null) return false;
            return MoveDivider(divider, cell.Left + width);
        }

        public bool SetSelectedHeight(double height)
        {
            var cell = SelectedCell; if (cell == null || height < 1d) return false;
            var divider = DividerAt(false, cell.Bottom, (cell.Left + cell.Right) / 2d);
            if (divider == null) return false;
            return MoveDivider(divider, cell.Top - height);
        }

        public void SetSelectedOpening(string opening) { var selected = SelectedCells.ToList(); if (selected.Count == 0) return; foreach (var cell in selected.Where(x => !x.IsDeleted)) cell.Opening = string.IsNullOrWhiteSpace(opening) ? "固定" : opening; OnLayoutChanged(); }
        public void SetSelectedMaterial(string material) { var selected = SelectedCells.ToList(); if (selected.Count == 0) return; foreach (var cell in selected.Where(x => !x.IsDeleted)) cell.Material = string.IsNullOrWhiteSpace(material) ? "无" : material; OnLayoutChanged(); }
        public void SetSelectedDoor(bool isDoor)
        {
            var selected = SelectedCells.ToList(); if (selected.Count == 0) return;
            foreach (var cell in selected.Where(x => !x.IsDeleted)) cell.IsDoor = isDoor; OnLayoutChanged();
        }

        public bool EqualizeSelectedWidths() { return Equalize(true); }
        public bool EqualizeSelectedHeights() { return Equalize(false); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var margin = 54f; var footer = 34f; var usable = new RectangleF(margin, 18, Math.Max(10, Width - margin * 2), Math.Max(10, Height - margin - footer));
            var scale = Math.Min(usable.Width / (float)_frameWidth, usable.Height / (float)_frameHeight);
            var drawWidth = (float)_frameWidth * scale; var drawHeight = (float)_frameHeight * scale;
            _drawingArea = new RectangleF(usable.Left + (usable.Width - drawWidth) / 2f, usable.Top + (usable.Height - drawHeight) / 2f, drawWidth, drawHeight);
            using (var selectedBrush = new SolidBrush(Color.FromArgb(30, 34, 128, 190)))
            using (var pen = new Pen(Color.FromArgb(35, 49, 64), 1.6f))
            using (var selectedPen = new Pen(Color.FromArgb(22, 112, 180), 2.5f))
            using (var outerPen = new Pen(Color.Black, 2f))
            using (var font = new Font("Microsoft YaHei UI", 8.5f))
            using (var doorFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (var openingPen = new Pen(Color.FromArgb(35, 125, 190), 1.5f) { DashStyle = DashStyle.Dash })
            {
                var displayOrder = OrderedCells.Select((cell, index) => new { cell, number = index + 1 }).ToDictionary(x => x.cell, x => x.number);
                for (var index = 0; index < _cells.Count; index++)
                {
                    var rect = ToPixels(_cells[index]); if (_selectedIndices.Contains(index)) e.Graphics.FillRectangle(selectedBrush, rect);
                    var drawPen = _selectedIndices.Contains(index) ? selectedPen : pen; if (_cells[index].IsDeleted) drawPen.DashStyle = DashStyle.Dash;
                    DrawCellBoundary(e.Graphics, drawPen, _cells[index]); drawPen.DashStyle = DashStyle.Solid;
                    if (!_cells[index].IsDeleted) DrawProfileRectangle(e.Graphics, pen, _cells[index]);
                    if (!_cells[index].IsDeleted) DrawMaterialSymbol(e.Graphics, openingPen, _cells[index]);
                    if (!_cells[index].IsDeleted) DrawOpeningSymbol(e.Graphics, openingPen, OpeningRectangle(_cells[index]), _cells[index].Opening, (_cells[index].Left + _cells[index].Right) / 2d <= _frameWidth / 2d);
                    var text = displayOrder[_cells[index]] + "  " + (_cells[index].Right - _cells[index].Left).ToString("0.##") + "×" + (_cells[index].Top - _cells[index].Bottom).ToString("0.##") + "\n" + (_cells[index].Opening ?? "固定") + " / " + (string.IsNullOrWhiteSpace(_cells[index].Material) ? "无" : _cells[index].Material);
                    if (_cells[index].IsDeleted) text = displayOrder[_cells[index]] + "  已删除（可恢复）";
                    TextRenderer.DrawText(e.Graphics, text, font, Rectangle.Round(rect), Color.FromArgb(45, 55, 65), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    if (_cells[index].IsDoor && !_cells[index].IsDeleted) TextRenderer.DrawText(e.Graphics, "门", doorFont, new Rectangle((int)rect.X + 4, (int)rect.Y + 4, 28, 22), Color.FromArgb(190, 105, 30));
                }
                outerPen.Color = Color.FromArgb(165, 175, 185); outerPen.DashStyle = DashStyle.Dash;
                if (_hasInstallationGap && _installationGap > 0d)
                {
                    var active = _cells.Where(x => !x.IsDeleted).Select(x => new DoorWindowCell(x.Left, x.Bottom, x.Right, x.Top)).ToList();
                    foreach (var segment in DoorWindowElevationGeometryBuilder.BuildInstallationGapOutline(active, _installationGap))
                        e.Graphics.DrawLine(outerPen, ToPixelX(segment.X1), ToPixelY(segment.Y1), ToPixelX(segment.X2), ToPixelY(segment.Y2));
                }
                TextRenderer.DrawText(e.Graphics, "拖动分隔线调整；蓝色框为当前面板", font, new Rectangle(0, Height - 28, Width, 22), Color.DimGray, TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus(); _dragDivider = HitDivider(e.Location);
            if (_dragDivider != null) { Capture = true; return; }
            var hit = HitCell(e.Location);
            if ((ModifierKeys & Keys.Shift) == Keys.Shift)
            {
                if (hit >= 0) { if (!_selectedIndices.Add(hit)) _selectedIndices.Remove(hit); _selected = _selectedIndices.Contains(hit) ? hit : _selectedIndices.LastOrDefault(); if (_selectedIndices.Count == 0) _selected = -1; }
            }
            else { _selectedIndices.Clear(); if (hit >= 0) _selectedIndices.Add(hit); _selected = hit; }
            Invalidate(); RaiseSelectionChanged();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragDivider != null && e.Button == MouseButtons.Left)
            {
                var coordinate = _dragDivider.Vertical ? FromPixelX(e.X) : FromPixelY(e.Y); MoveDivider(_dragDivider, coordinate); return;
            }
            var divider = HitDivider(e.Location); Cursor = divider == null ? Cursors.Default : divider.Vertical ? Cursors.VSplit : Cursors.HSplit;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (_dragDivider != null) { _dragDivider = null; Capture = false; OnLayoutChanged(); } }

        private Divider HitDivider(Point point)
        {
            if (!_drawingArea.Contains(point)) return null;
            var x = FromPixelX(point.X); var y = FromPixelY(point.Y); var toleranceX = 7d * _frameWidth / Math.Max(1f, _drawingArea.Width); var toleranceY = 7d * _frameHeight / Math.Max(1f, _drawingArea.Height);
            Divider best = null; var bestDistance = double.MaxValue;
            foreach (var cell in _cells)
            {
                foreach (var coordinate in new[] { cell.Left, cell.Right }) if (coordinate > .05d && coordinate < _frameWidth - .05d && y >= cell.Bottom - toleranceY && y <= cell.Top + toleranceY && Math.Abs(x - coordinate) < bestDistance && Math.Abs(x - coordinate) <= toleranceX)
                { best = DividerAt(true, coordinate, y); bestDistance = Math.Abs(x - coordinate); }
                foreach (var coordinate in new[] { cell.Bottom, cell.Top }) if (coordinate > .05d && coordinate < _frameHeight - .05d && x >= cell.Left - toleranceX && x <= cell.Right + toleranceX && Math.Abs(y - coordinate) < bestDistance && Math.Abs(y - coordinate) <= toleranceY)
                { best = DividerAt(false, coordinate, x); bestDistance = Math.Abs(y - coordinate); }
            }
            return best;
        }

        private Divider DividerAt(bool vertical, double coordinate, double along)
        {
            const double tolerance = .05d; var segments = new List<Tuple<double, double>>();
            foreach (var cell in _cells)
            {
                if (vertical && (Math.Abs(cell.Left - coordinate) < tolerance || Math.Abs(cell.Right - coordinate) < tolerance)) segments.Add(Tuple.Create(cell.Bottom, cell.Top));
                if (!vertical && (Math.Abs(cell.Bottom - coordinate) < tolerance || Math.Abs(cell.Top - coordinate) < tolerance)) segments.Add(Tuple.Create(cell.Left, cell.Right));
            }
            var seed = segments.FirstOrDefault(x => along >= x.Item1 - tolerance && along <= x.Item2 + tolerance); if (seed == null) return null;
            var start = seed.Item1; var end = seed.Item2; var changed = true;
            while (changed) { changed = false; foreach (var segment in segments) if (segment.Item2 >= start - tolerance && segment.Item1 <= end + tolerance) { var nextStart = Math.Min(start, segment.Item1); var nextEnd = Math.Max(end, segment.Item2); if (nextStart != start || nextEnd != end) { start = nextStart; end = nextEnd; changed = true; } } }
            var result = new Divider { Vertical = vertical, Coordinate = coordinate, Start = start, End = end };
            for (var index = 0; index < _cells.Count; index++)
            {
                var cell = _cells[index]; var overlaps = vertical ? cell.Top > start + tolerance && cell.Bottom < end - tolerance : cell.Right > start + tolerance && cell.Left < end - tolerance;
                if (!overlaps) continue;
                if (vertical && Math.Abs(cell.Right - coordinate) < tolerance || !vertical && Math.Abs(cell.Top - coordinate) < tolerance) result.Before.Add(index);
                if (vertical && Math.Abs(cell.Left - coordinate) < tolerance || !vertical && Math.Abs(cell.Bottom - coordinate) < tolerance) result.After.Add(index);
            }
            return result.Before.Count > 0 && result.After.Count > 0 ? result : null;
        }

        private bool MoveDivider(Divider divider, double coordinate)
        {
            if (divider == null) return false; const double minimum = 50d;
            var lower = divider.Before.Select(i => divider.Vertical ? _cells[i].Left : _cells[i].Bottom).Max() + minimum;
            var upper = divider.After.Select(i => divider.Vertical ? _cells[i].Right : _cells[i].Top).Min() - minimum;
            coordinate = Math.Max(lower, Math.Min(upper, coordinate));
            foreach (var index in divider.Before) { if (divider.Vertical) _cells[index].Right = coordinate; else _cells[index].Top = coordinate; }
            foreach (var index in divider.After) { if (divider.Vertical) _cells[index].Left = coordinate; else _cells[index].Bottom = coordinate; }
            divider.Coordinate = coordinate; Invalidate(); if (_dragDivider == null) OnLayoutChanged(); else RaiseSelectionChanged(); return true;
        }

        private int HitCell(Point point)
        { for (var index = _cells.Count - 1; index >= 0; index--) if (ToPixels(_cells[index]).Contains(point)) return index; return -1; }
        private RectangleF ToPixels(DoorWindowLayoutCell cell) { return new RectangleF(ToPixelX(cell.Left), ToPixelY(cell.Top), ToPixelX(cell.Right) - ToPixelX(cell.Left), ToPixelY(cell.Bottom) - ToPixelY(cell.Top)); }
        private float ToPixelX(double value) { return _drawingArea.Left + (float)(value / _frameWidth) * _drawingArea.Width; }
        private float ToPixelY(double value) { return _drawingArea.Bottom - (float)(value / _frameHeight) * _drawingArea.Height; }
        private double FromPixelX(float value) { return (value - _drawingArea.Left) / Math.Max(1f, _drawingArea.Width) * _frameWidth; }
        private double FromPixelY(float value) { return (_drawingArea.Bottom - value) / Math.Max(1f, _drawingArea.Height) * _frameHeight; }
        private void OnLayoutChanged() { Invalidate(); if (LayoutChanged != null) LayoutChanged(this, EventArgs.Empty); RaiseSelectionChanged(); }
        private void RaiseSelectionChanged() { if (SelectedCellChanged != null) SelectedCellChanged(this, EventArgs.Empty); }
        private bool Equalize(bool widths)
        {
            var selected = SelectedCells.ToList(); if (selected.Count < 2) return false; var changed = false;
            var groups = widths ? selected.GroupBy(x => x.Bottom.ToString("0.###") + ":" + x.Top.ToString("0.###")) : selected.GroupBy(x => x.Left.ToString("0.###") + ":" + x.Right.ToString("0.###"));
            foreach (var group in groups.Where(x => x.Count() > 1))
            {
                var ordered = widths ? group.OrderBy(x => x.Left).ToList() : group.OrderByDescending(x => x.Top).ToList();
                var continuous = true; for (var index = 1; index < ordered.Count; index++) if (Math.Abs((widths ? ordered[index - 1].Right - ordered[index].Left : ordered[index - 1].Bottom - ordered[index].Top)) > .05d) continuous = false;
                if (!continuous) continue;
                if (widths) { var start = ordered.First().Left; var end = ordered.Last().Right; for (var index = 0; index < ordered.Count; index++) { ordered[index].Left = start + (end - start) * index / ordered.Count; ordered[index].Right = start + (end - start) * (index + 1) / ordered.Count; } }
                else { var top = ordered.First().Top; var bottom = ordered.Last().Bottom; for (var index = 0; index < ordered.Count; index++) { ordered[index].Top = top - (top - bottom) * index / ordered.Count; ordered[index].Bottom = top - (top - bottom) * (index + 1) / ordered.Count; } }
                changed = true;
            }
            if (changed) OnLayoutChanged(); return changed;
        }

        private static DoorWindowLayoutCell Copy(DoorWindowLayoutCell cell) { return new DoorWindowLayoutCell { Left = cell.Left, Bottom = cell.Bottom, Right = cell.Right, Top = cell.Top, Opening = cell.Opening, Material = string.IsNullOrWhiteSpace(cell.Material) ? "无" : cell.Material, IsDoor = cell.IsDoor, IsDeleted = cell.IsDeleted }; }
        private static void MergeValues(DoorWindowLayoutCell target, DoorWindowLayoutCell source) { target.IsDoor = target.IsDoor || source.IsDoor; if (string.IsNullOrWhiteSpace(target.Opening)) target.Opening = source.Opening; if (string.IsNullOrWhiteSpace(target.Material)) target.Material = source.Material; }

        private static void DrawOpeningSymbol(Graphics graphics, Pen pen, RectangleF rect, string opening, bool leftHalf)
        {
            var mode = opening ?? string.Empty; if (mode == "" || mode == "固定") return;
            var left = rect.Left + 3f; var right = rect.Right - 3f; var top = rect.Top + 3f; var bottom = rect.Bottom - 3f; var middleX = (left + right) / 2f; var middleY = (top + bottom) / 2f;
            if (mode == "左平开") { graphics.DrawLine(pen, left, top, right, middleY); graphics.DrawLine(pen, right, middleY, left, bottom); }
            else if (mode == "右平开") { graphics.DrawLine(pen, right, top, left, middleY); graphics.DrawLine(pen, left, middleY, right, bottom); }
            else if (mode == "上悬") { graphics.DrawLine(pen, left, top, middleX, bottom); graphics.DrawLine(pen, middleX, bottom, right, top); }
            else if (mode == "下悬") { graphics.DrawLine(pen, left, bottom, middleX, top); graphics.DrawLine(pen, middleX, top, right, bottom); }
            else if (mode == "推拉" || mode == "右推拉") DrawSlidingArrow(graphics, pen, left, right, middleY, true);
            else if (mode == "左推拉") DrawSlidingArrow(graphics, pen, left, right, middleY, false);
            else if (mode == "双向推拉") DrawSlidingArrow(graphics, pen, left, right, middleY, leftHalf);
            else if (mode == "百叶") for (var index = 1; index < 7; index++) { var y = top + (bottom - top) * index / 7f; graphics.DrawLine(pen, left, y, right, y); }
        }

        private void DrawProfileRectangle(Graphics graphics, Pen pen, DoorWindowLayoutCell cell)
        {
            var leftNeighbor = ActiveNeighbor(cell, true, cell.Left); var rightNeighbor = ActiveNeighbor(cell, true, cell.Right);
            var bottomNeighbor = ActiveNeighbor(cell, false, cell.Bottom); var topNeighbor = ActiveNeighbor(cell, false, cell.Top);
            var leftInset = leftNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, leftNeighbor) : 0d);
            var rightInset = rightNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, rightNeighbor) : 0d);
            var bottomInset = bottomNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, bottomNeighbor) : 0d);
            var topInset = topNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, topNeighbor) : 0d);
            leftInset = Math.Min(leftInset, (cell.Right - cell.Left) * .45d); rightInset = Math.Min(rightInset, (cell.Right - cell.Left) * .45d);
            bottomInset = Math.Min(bottomInset, (cell.Top - cell.Bottom) * .45d); topInset = Math.Min(topInset, (cell.Top - cell.Bottom) * .45d);
            var nDoorBottom = cell.IsDoor && bottomNeighbor == null && _doorFrameType == "N型"; var inner = ToPixels(new DoorWindowLayoutCell { Left = cell.Left + leftInset, Bottom = nDoorBottom ? cell.Bottom : cell.Bottom + bottomInset, Right = cell.Right - rightInset, Top = cell.Top - topInset });
            if (bottomNeighbor != null && _hasMullion || bottomNeighbor == null && _hasOuterFrame) if (!nDoorBottom) graphics.DrawLine(pen, inner.Left, inner.Bottom, inner.Right, inner.Bottom);
            if (rightNeighbor != null && _hasMullion || rightNeighbor == null && _hasOuterFrame) graphics.DrawLine(pen, inner.Right, inner.Bottom, inner.Right, inner.Top);
            if (topNeighbor != null && _hasMullion || topNeighbor == null && _hasOuterFrame) graphics.DrawLine(pen, inner.Right, inner.Top, inner.Left, inner.Top);
            if (leftNeighbor != null && _hasMullion || leftNeighbor == null && _hasOuterFrame) graphics.DrawLine(pen, inner.Left, inner.Top, inner.Left, inner.Bottom);
        }

        private DoorWindowLayoutCell ActiveNeighbor(DoorWindowLayoutCell owner, bool vertical, double coordinate)
        {
            const double tolerance = .05d;
            foreach (var other in _cells.Where(x => !x.IsDeleted && !ReferenceEquals(x, owner)))
            {
                if (vertical && (Math.Abs(other.Left - coordinate) < tolerance || Math.Abs(other.Right - coordinate) < tolerance) && Math.Min(owner.Top, other.Top) - Math.Max(owner.Bottom, other.Bottom) > tolerance) return other;
                if (!vertical && (Math.Abs(other.Bottom - coordinate) < tolerance || Math.Abs(other.Top - coordinate) < tolerance) && Math.Min(owner.Right, other.Right) - Math.Max(owner.Left, other.Left) > tolerance) return other;
            }
            return null;
        }

        private double SharedInset(DoorWindowLayoutCell first, DoorWindowLayoutCell second) { return IsOperable(first.Opening) || IsOperable(second.Opening) ? _mullionProfileWidth : _mullionProfileWidth / 2d; }
        private static bool IsOperable(string value) { var mode = (value ?? string.Empty).Trim(); return mode != "" && mode != "固定" && mode != "未设置" && mode != "百叶"; }

        private RectangleF OpeningRectangle(DoorWindowLayoutCell cell)
        {
            var leftNeighbor = ActiveNeighbor(cell, true, cell.Left); var rightNeighbor = ActiveNeighbor(cell, true, cell.Right); var bottomNeighbor = ActiveNeighbor(cell, false, cell.Bottom); var topNeighbor = ActiveNeighbor(cell, false, cell.Top);
            var leftInset = leftNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, leftNeighbor) : 0d);
            var rightInset = rightNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, rightNeighbor) : 0d);
            var bottomInset = bottomNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, bottomNeighbor) : 0d);
            var topInset = topNeighbor == null ? (_hasOuterFrame ? _outerProfileWidth : 0d) : (_hasMullion ? SharedInset(cell, topNeighbor) : 0d);
            if (cell.IsDoor && bottomNeighbor == null && _doorFrameType == "N型") bottomInset = 0d;
            var clearance = Math.Min(6d, Math.Min(cell.Right - cell.Left, cell.Top - cell.Bottom) * .01d);
            var inset = new DoorWindowLayoutCell { Left = cell.Left + leftInset + clearance, Right = cell.Right - rightInset - clearance, Bottom = cell.Bottom + bottomInset + clearance, Top = cell.Top - topInset - clearance };
            return inset.Right > inset.Left && inset.Top > inset.Bottom ? ToPixels(inset) : ToPixels(cell);
        }

        private void DrawCellBoundary(Graphics graphics, Pen pen, DoorWindowLayoutCell cell)
        {
            var rect = ToPixels(cell); if (cell.IsDeleted) { graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height); return; }
            var leftNeighbor = ActiveNeighbor(cell, true, cell.Left); var rightNeighbor = ActiveNeighbor(cell, true, cell.Right); var bottomNeighbor = ActiveNeighbor(cell, false, cell.Bottom); var topNeighbor = ActiveNeighbor(cell, false, cell.Top);
            Draw(rect.Left, rect.Top, rect.Left, rect.Bottom, leftNeighbor);
            Draw(rect.Right, rect.Top, rect.Right, rect.Bottom, rightNeighbor);
            Draw(rect.Left, rect.Top, rect.Right, rect.Top, topNeighbor);
            Draw(rect.Left, rect.Bottom, rect.Right, rect.Bottom, bottomNeighbor);
            void Draw(float x1, float y1, float x2, float y2, DoorWindowLayoutCell neighbor)
            {
                if (neighbor != null && _hasMullion && !IsOperable(cell.Opening) && !IsOperable(neighbor.Opening)) return;
                var old = pen.DashStyle; if (neighbor != null && !_hasMullion) pen.DashStyle = DashStyle.Dash;
                graphics.DrawLine(pen, x1, y1, x2, y2); pen.DashStyle = old;
            }
        }

        private static void DrawSlidingArrow(Graphics graphics, Pen pen, float left, float right, float y, bool pointsRight)
        {
            var start = pointsRight ? left + (right - left) * .2f : right - (right - left) * .2f; var end = pointsRight ? right - (right - left) * .2f : left + (right - left) * .2f; var sign = pointsRight ? -1f : 1f;
            graphics.DrawLine(pen, start, y, end, y); graphics.DrawLine(pen, end, y, end + sign * 10f, y - 5f); graphics.DrawLine(pen, end, y, end + sign * 10f, y + 5f);
        }

        private void DrawMaterialSymbol(Graphics graphics, Pen pen, DoorWindowLayoutCell cell)
        {
            var value = string.IsNullOrWhiteSpace(cell.Material) ? "无" : cell.Material; if (value == "无") return;
            var leftNeighbor = ActiveNeighbor(cell, true, cell.Left); var rightNeighbor = ActiveNeighbor(cell, true, cell.Right); var bottomNeighbor = ActiveNeighbor(cell, false, cell.Bottom); var topNeighbor = ActiveNeighbor(cell, false, cell.Top);
            var outer = _hasOuterFrame ? _outerProfileWidth : 0d; var leftInset = leftNeighbor == null ? outer : (_hasMullion ? SharedInset(cell, leftNeighbor) : 0d); var rightInset = rightNeighbor == null ? outer : (_hasMullion ? SharedInset(cell, rightNeighbor) : 0d);
            var bottomInset = bottomNeighbor == null ? outer : (_hasMullion ? SharedInset(cell, bottomNeighbor) : 0d); var topInset = topNeighbor == null ? outer : (_hasMullion ? SharedInset(cell, topNeighbor) : 0d); var extra = Math.Min(12d, Math.Min(cell.Right - cell.Left, cell.Top - cell.Bottom) * .04d);
            var inner = ToPixels(new DoorWindowLayoutCell { Left = cell.Left + leftInset + extra, Right = cell.Right - rightInset - extra, Bottom = cell.Bottom + bottomInset + extra, Top = cell.Top - topInset - extra }); if (inner.Width < 10 || inner.Height < 10) return;
            if (value == "玻璃")
            {
                graphics.DrawRectangle(pen, inner.X, inner.Y, inner.Width, inner.Height); var cx = inner.Left + inner.Width / 2f; var cy = inner.Top + inner.Height / 2f;
                for (var index = -1; index <= 1; index++) graphics.DrawLine(pen, cx - 10, cy + index * 6 + 4, cx + 10, cy + index * 6 - 4);
            }
            else if (value == "百叶") for (var index = 1; index < 8; index++) { var y = inner.Top + inner.Height * index / 8f; graphics.DrawLine(pen, inner.Left, y, inner.Right, y); }
            else if (value == "实板") { graphics.DrawLine(pen, inner.Left, inner.Top, inner.Right, inner.Bottom); graphics.DrawLine(pen, inner.Left, inner.Bottom, inner.Right, inner.Top); }
        }
    }
}
