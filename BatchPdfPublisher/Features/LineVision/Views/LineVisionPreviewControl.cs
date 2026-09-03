using BatchPdfPublisher.Models;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal enum LineVisionPreviewMode { Original, Binary, Result }

    internal sealed class LineVisionRegionEventArgs : EventArgs
    {
        public LineVisionRegionEventArgs(Rectangle region) { Region = region; }
        public Rectangle Region { get; private set; }
    }

    internal sealed class LineVisionCalibrationEventArgs : EventArgs
    {
        public LineVisionCalibrationEventArgs(PointF first, PointF second)
        {
            First = first; Second = second;
            var dx = second.X - first.X; var dy = second.Y - first.Y;
            PixelDistance = Math.Sqrt(dx * dx + dy * dy);
        }
        public PointF First { get; private set; }
        public PointF Second { get; private set; }
        public double PixelDistance { get; private set; }
    }

    internal sealed class LineVisionPreviewControl : Control
    {
        private Bitmap _input;
        private LineVisionResult _result;
        private float _zoom = 1f;
        private PointF _offset;
        private Point _mouseDown;
        private PointF _panStart;
        private bool _panning;
        private bool _selectingRegion;
        private PointF _regionStart;
        private RectangleF _region;
        private bool _calibrating;
        private PointF? _calibrationStart;

        public event EventHandler<LineVisionRegionEventArgs> RegionSelected;
        public event EventHandler<LineVisionCalibrationEventArgs> CalibrationSelected;
        public LineVisionPreviewMode PreviewMode { get; set; } = LineVisionPreviewMode.Result;
        public int SelectedSegmentIndex { get; set; } = -1;
        public int SelectedTextIndex { get; set; } = -1;
        public int SelectedCircleIndex { get; set; } = -1;
        public int SelectedArcIndex { get; set; } = -1;

        public LineVisionPreviewControl()
        {
            Dock = DockStyle.Fill; DoubleBuffered = true; BackColor = Color.FromArgb(30, 36, 43); TabStop = true;
        }

        public void LoadInput(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("图片不存在。", path);
            if (_input != null) _input.Dispose();
            using (var source = new Bitmap(path)) _input = new Bitmap(source);
            _result = null; _region = RectangleF.Empty; _calibrationStart = null;
            Fit(); Invalidate();
        }

        public void SetResult(LineVisionResult result)
        {
            if (_input != null) { _input.Dispose(); _input = null; }
            _result = result; PreviewMode = LineVisionPreviewMode.Result;
            _region = RectangleF.Empty; _calibrationStart = null; Fit(); Invalidate();
        }

        public void BeginRegionSelection()
        {
            _selectingRegion = true; _calibrating = false; _region = RectangleF.Empty;
            Cursor = Cursors.Cross; Invalidate();
        }

        public void BeginCalibration()
        {
            if (_result == null) return;
            _calibrating = true; _selectingRegion = false; _calibrationStart = null;
            Cursor = Cursors.Cross; Invalidate();
        }

        public void Fit()
        {
            var image = CurrentImage();
            if (image == null || Width < 40 || Height < 40) return;
            _zoom = Math.Max(0.01f, Math.Min((Width - 28f) / image.Width, (Height - 28f) / image.Height));
            _offset = new PointF((Width - image.Width * _zoom) * 0.5f, (Height - image.Height * _zoom) * 0.5f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _input != null) _input.Dispose();
            _input = null; base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e); var before = _zoom;
            _zoom = Math.Max(0.02f, Math.Min(30f, _zoom * (e.Delta > 0 ? 1.13f : 0.885f)));
            _offset.X = e.X - (e.X - _offset.X) * _zoom / before;
            _offset.Y = e.Y - (e.Y - _offset.Y) * _zoom / before;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus(); _mouseDown = e.Location;
            if (_selectingRegion && e.Button == MouseButtons.Left) { _regionStart = ToImage(e.Location); _region = RectangleF.Empty; }
            else if (_calibrating && e.Button == MouseButtons.Left) HandleCalibration(ToImage(e.Location));
            else if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) { _panning = true; _panStart = _offset; Cursor = Cursors.SizeAll; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _offset = new PointF(_panStart.X + e.X - _mouseDown.X, _panStart.Y + e.Y - _mouseDown.Y); Invalidate(); return;
            }
            if (_selectingRegion && e.Button == MouseButtons.Left)
            {
                var current = ToImage(e.Location);
                _region = RectangleF.FromLTRB(Math.Min(_regionStart.X, current.X), Math.Min(_regionStart.Y, current.Y), Math.Max(_regionStart.X, current.X), Math.Max(_regionStart.Y, current.Y));
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning) { _panning = false; Cursor = Cursors.Default; return; }
            if (!_selectingRegion || _region.Width < 2 || _region.Height < 2) return;
            _selectingRegion = false; Cursor = Cursors.Default;
            var image = CurrentImage(); if (image == null) return;
            var region = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height), Rectangle.Round(_region));
            if (RegionSelected != null) RegionSelected(this, new LineVisionRegionEventArgs(region));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var image = CurrentImage();
            if (image == null) { DrawCentered(e.Graphics, "选择 PNG、JPG、BMP 或 TIFF 图片开始", Color.FromArgb(185, 195, 205)); return; }
            e.Graphics.InterpolationMode = _zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(image, _offset.X, _offset.Y, image.Width * _zoom, image.Height * _zoom);
            if (PreviewMode == LineVisionPreviewMode.Result && _result != null)
            {
                for (var index = 0; index < _result.Segments.Count; index++)
                {
                    var segment = _result.Segments[index]; if (!segment.IsEnabled) continue;
                    var selected = index == SelectedSegmentIndex;
                    using (var pen = new Pen(selected ? Color.Magenta : ColorFor(segment.Direction), selected ? 3.2f : Math.Max(1.2f, Math.Min(3f, _zoom * 0.65f))))
                        e.Graphics.DrawLine(pen, ToResultScreen(segment.X1, segment.Y1), ToResultScreen(segment.X2, segment.Y2));
                }
                for (var index = 0; index < _result.Circles.Count; index++)
                {
                    var circle = _result.Circles[index]; if (!circle.IsEnabled) continue;
                    var center = ToResultScreen(circle.CenterX, circle.CenterY);
                    var radius = (float)(circle.Radius * _result.SourcePreviewScale * _zoom);
                    var selected = index == SelectedCircleIndex;
                    using (var pen = new Pen(selected ? Color.Magenta : Color.Orange, selected ? 3.2f : Math.Max(1.5f, _zoom * 0.75f))) e.Graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
                }
                for (var index = 0; index < _result.Arcs.Count; index++)
                {
                    var arc = _result.Arcs[index]; if (!arc.IsEnabled) continue;
                    var center = ToResultScreen(arc.CenterX, arc.CenterY); var radius = (float)(arc.Radius * _result.SourcePreviewScale * _zoom);
                    var selected = index == SelectedArcIndex;
                    using (var pen = new Pen(selected ? Color.Magenta : Color.Cyan, selected ? 3.2f : Math.Max(1.5f, _zoom * 0.75f)))
                        e.Graphics.DrawArc(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f, (float)arc.StartAngleDegrees, (float)arc.SweepAngleDegrees);
                }
                for (var index = 0; index < _result.TextRegions.Count; index++)
                {
                    var text = _result.TextRegions[index];
                    var bounds = text.Bounds;
                    var screen = new RectangleF(
                        ToResultScreen(bounds.Left, bounds.Top).X,
                        ToResultScreen(bounds.Left, bounds.Top).Y,
                        (float)(bounds.Width * _result.SourcePreviewScale * _zoom),
                        (float)(bounds.Height * _result.SourcePreviewScale * _zoom));
                    var selected = index == SelectedTextIndex;
                    var color = !text.IsEnabled ? Color.FromArgb(190, 235, 80, 80) : selected ? Color.Magenta : Color.FromArgb(230, 190, 90, 255);
                    using (var fill = new SolidBrush(Color.FromArgb(selected ? 55 : 28, color))) e.Graphics.FillRectangle(fill, screen);
                    using (var pen = new Pen(color, selected ? 3f : 1.5f) { DashStyle = text.IsEnabled ? DashStyle.Solid : DashStyle.Dash })
                        e.Graphics.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
                }
            }
            if (!_region.IsEmpty)
            {
                var screen = ToScreen(_region);
                using (var fill = new SolidBrush(Color.FromArgb(35, 255, 145, 35))) e.Graphics.FillRectangle(fill, screen);
                using (var pen = new Pen(Color.Orange, 2f) { DashStyle = DashStyle.Dash }) e.Graphics.DrawRectangle(pen, screen.X, screen.Y, screen.Width, screen.Height);
            }
            if (_calibrationStart.HasValue)
            {
                var point = ToResultScreen(_calibrationStart.Value.X, _calibrationStart.Value.Y);
                using (var pen = new Pen(Color.Magenta, 2f)) { e.Graphics.DrawEllipse(pen, point.X - 5, point.Y - 5, 10, 10); }
            }
        }

        private void HandleCalibration(PointF point)
        {
            if (_result != null)
            {
                if (PreviewMode == LineVisionPreviewMode.Binary)
                    point = new PointF((float)(point.X * _result.SourcePixelsPerAnalysisPixel), (float)(point.Y * _result.SourcePixelsPerAnalysisPixel));
                else if (_result.SourcePreviewScale > 0d)
                    point = new PointF((float)(point.X / _result.SourcePreviewScale), (float)(point.Y / _result.SourcePreviewScale));
            }
            if (!_calibrationStart.HasValue) { _calibrationStart = point; Invalidate(); return; }
            var args = new LineVisionCalibrationEventArgs(_calibrationStart.Value, point);
            _calibrationStart = null; _calibrating = false; Cursor = Cursors.Default; Invalidate();
            if (args.PixelDistance > 1d && CalibrationSelected != null) CalibrationSelected(this, args);
        }

        private Bitmap CurrentImage()
        {
            if (_result == null) return _input;
            if (PreviewMode == LineVisionPreviewMode.Binary) return _result.BinaryPreview;
            return _result.SourcePreview;
        }

        private PointF ToImage(Point point) { return new PointF((point.X - _offset.X) / _zoom, (point.Y - _offset.Y) / _zoom); }
        private PointF ToScreen(double x, double y) { return new PointF(_offset.X + (float)x * _zoom, _offset.Y + (float)y * _zoom); }
        private PointF ToResultScreen(double x, double y)
        {
            var scale = _result == null ? 1d : _result.SourcePreviewScale;
            return ToScreen(x * scale, y * scale);
        }
        private RectangleF ToScreen(RectangleF value) { return new RectangleF(_offset.X + value.X * _zoom, _offset.Y + value.Y * _zoom, value.Width * _zoom, value.Height * _zoom); }

        private static Color ColorFor(LineVisionDirection direction)
        {
            if (direction == LineVisionDirection.Horizontal) return Color.LimeGreen;
            if (direction == LineVisionDirection.Vertical) return Color.DeepSkyBlue;
            if (direction == LineVisionDirection.Diagonal || direction == LineVisionDirection.Angled) return Color.Gold;
            return Color.Red;
        }

        private void DrawCentered(Graphics graphics, string text, Color color)
        {
            using (var font = new Font("Microsoft YaHei UI", 10f))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(text, font, brush, ClientRectangle, format);
        }
    }
}
