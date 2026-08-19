using BatchPdfPublisher.Models;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowElevationPreviewControl : Control
    {
        private DoorWindowScheduleItem _item;
        public DoorWindowElevationPreviewControl()
        {
            DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill;
        }

        public void ShowItem(DoorWindowScheduleItem item) { _item = item; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
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
                try { if (ClientRectangle.Width > 0 && ClientRectangle.Height > 0) DrawCentered(e.Graphics, "预览绘制失败：" + exception.Message, ClientRectangle, Color.Firebrick, 10F); } catch { }
            }
        }

        private void PaintPreview(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (_item == null) { DrawCentered(graphics, "选择一行门窗查看立面预览", ClientRectangle, Color.Gray, 10F); return; }
            DoorWindowElevationGeometry geometry;
            try { geometry = DoorWindowElevationGeometryBuilder.Build(_item); }
            catch (Exception exception) { DrawCentered(graphics, exception.Message, ClientRectangle, Color.Firebrick, 10F); return; }

            var titleBand = 62f; var margin = 34f;
            var area = new RectangleF(margin, margin, Math.Max(1, Width - margin * 2), Math.Max(1, Height - margin * 2 - titleBand));
            var holeW = Safe((float)geometry.HoleWidth, 1f); var holeH = Safe((float)geometry.HoleHeight, 1f);
            var scale = Math.Min(area.Width / holeW, area.Height / holeH);
            scale = Safe(scale, 1f);
            var drawWidth = holeW * scale; var drawHeight = holeH * scale;
            var originX = area.Left + (area.Width - drawWidth) / 2f; var originY = area.Top + (area.Height - drawHeight) / 2f + drawHeight;
            using (var holePen = new Pen(Color.FromArgb(155, 165, 176), 1f) { DashStyle = DashStyle.Dash })
            using (var framePen = new Pen(Color.FromArgb(28, 40, 52), 2.2f))
            using (var mullionPen = new Pen(Color.FromArgb(28, 40, 52), 1.5f))
            using (var openingPen = new Pen(Color.FromArgb(23, 116, 178), 1.25f) { DashStyle = DashStyle.Dash })
            using (var materialPen = new Pen(Color.FromArgb(0, 165, 185), 1.15f))
            {
                foreach (var line in geometry.Lines)
                {
                    var pen = line.Role == DoorWindowLineRole.Hole ? holePen : line.Role == DoorWindowLineRole.Frame ? framePen : line.Role == DoorWindowLineRole.Mullion ? mullionPen : line.Role == DoorWindowLineRole.Material ? materialPen : openingPen;
                    var x1 = X(line.X1); var y1 = Y(line.Y1); var x2 = X(line.X2); var y2 = Y(line.Y2);
                    if (!AllFinite(x1, y1, x2, y2)) continue;
                    graphics.DrawLine(pen, x1, y1, x2, y2);
                }
            }
            DrawDimensions(graphics, originX, originY, drawWidth, drawHeight);
            using (var doorFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            using (var doorBrush = new SolidBrush(Color.FromArgb(185, 120, 55)))
                foreach (var cell in geometry.Cells)
                    if (cell.IsDoor)
                    {
                        var centerX = (X(cell.Left) + X(cell.Right)) / 2f; var centerY = (Y(cell.Bottom) + Y(cell.Top)) / 2f;
                        var size = graphics.MeasureString("门", doorFont);
                        graphics.DrawString("门", doorFont, doorBrush, centerX - size.Width / 2f, centerY - size.Height / 2f);
                    }
            var caption = (_item.Code ?? "未编号") + "  " + _item.SizeText + "  " + (_item.DivisionPreset ?? "") + " / " + (_item.OpeningMode ?? "");
            if (_item.ElevationType == "门联窗") caption += "  门" + (_item.DoorPlacement ?? "靠左") + (_item.DoorPlacement == "居中" ? string.Empty : "，距边 " + _item.DoorEdgeDistance.ToString("0.##") + " mm");
            DrawCentered(graphics, caption, new RectangleF(8, Height - titleBand + 8, Math.Max(0, Width - 16), 24), Color.FromArgb(25, 36, 48), 10F);
            DrawCentered(graphics, "预览按窗口自适应；插入 CAD 时按洞口实际毫米 1:1 绘制", new RectangleF(8, Height - 27, Math.Max(0, Width - 16), 20), Color.DimGray, 8.5F);

            float X(double value) { return originX + (float)value * scale; }
            float Y(double value) { return originY - (float)value * scale; }
        }

        private void DrawDimensions(Graphics graphics, float x, float y, float width, float height)
        {
            using (var pen = new Pen(Color.FromArgb(60, 120, 60), 1f))
            using (var font = new Font("Microsoft YaHei UI", 8.5f))
            using (var brush = new SolidBrush(Color.FromArgb(45, 100, 45)))
            {
                var bottom = Math.Min(Height - 72f, y + 18f); graphics.DrawLine(pen, x, bottom, x + width, bottom); graphics.DrawLine(pen, x, y, x, bottom + 4); graphics.DrawLine(pen, x + width, y, x + width, bottom + 4);
                DrawArrow(graphics, pen, x, bottom, 1); DrawArrow(graphics, pen, x + width, bottom, -1);
                var widthText = _item.Width.ToString("0.##"); var size = graphics.MeasureString(widthText, font); graphics.FillRectangle(Brushes.White, x + width / 2 - size.Width / 2, bottom - size.Height / 2, size.Width, size.Height); graphics.DrawString(widthText, font, brush, x + width / 2 - size.Width / 2, bottom - size.Height / 2);
                var left = Math.Max(11f, x - 18f); graphics.DrawLine(pen, left, y, left, y - height); graphics.DrawLine(pen, left - 4, y, x, y); graphics.DrawLine(pen, left - 4, y - height, x, y - height);
                var heightText = _item.Height.ToString("0.##"); var hSize = graphics.MeasureString(heightText, font);
                var state = graphics.Save(); graphics.TranslateTransform(left, y - height / 2); graphics.RotateTransform(-90); graphics.FillRectangle(Brushes.White, -hSize.Width / 2, -hSize.Height / 2, hSize.Width, hSize.Height); graphics.DrawString(heightText, font, brush, -hSize.Width / 2, -hSize.Height / 2); graphics.Restore(state);
            }
        }

        private static void DrawArrow(Graphics graphics, Pen pen, float x, float y, int direction)
        { graphics.DrawLine(pen, x, y, x + 5 * direction, y - 3); graphics.DrawLine(pen, x, y, x + 5 * direction, y + 3); }

        private static bool AllFinite(params float[] values)
        {
            foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            return true;
        }

        private static float Safe(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return fallback;
            return value;
        }

        private static void DrawCentered(Graphics graphics, string text, Rectangle rectangle, Color color, float size) { DrawCentered(graphics, text, (RectangleF)rectangle, color, size); }
        private static void DrawCentered(Graphics graphics, string text, RectangleF rectangle, Color color, float size)
        {
            if (float.IsNaN(rectangle.Width) || float.IsInfinity(rectangle.Width) || float.IsNaN(rectangle.Height) || float.IsInfinity(rectangle.Height)
                || rectangle.Width <= 0f || rectangle.Height <= 0f || size <= 0f) return;
            using (var font = new Font("Microsoft YaHei UI", Math.Max(4f, size)))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                graphics.DrawString(text ?? string.Empty, font, brush, rectangle, format);
        }
    }
}
