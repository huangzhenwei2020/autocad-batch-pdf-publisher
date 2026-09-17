using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowElevationPreviewControl : Control
    {
        private DoorWindowScheduleItem _item;
        private Bitmap _renderCache;
        private bool _renderDirty = true;
        public DoorWindowElevationPreviewControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; Dock = DockStyle.Fill;
        }

        public void ShowItem(DoorWindowScheduleItem item) { _item = item; InvalidateRender(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width <= 0 || Height <= 0) return;
            try
            {
                if (_renderCache == null || _renderCache.Width != Width || _renderCache.Height != Height || _renderDirty)
                {
                    if (_renderCache != null) _renderCache.Dispose();
                    _renderCache = new Bitmap(Width, Height);
                    using (var bufferGraphics = Graphics.FromImage(_renderCache))
                    {
                        bufferGraphics.Clear(Color.White);
                        PaintPreview(bufferGraphics);
                    }
                    _renderDirty = false;
                }
                e.Graphics.DrawImageUnscaled(_renderCache, 0, 0);
            }
            catch (Exception exception)
            {
                try { if (ClientRectangle.Width > 0 && ClientRectangle.Height > 0) DrawCentered(e.Graphics, "预览绘制失败：" + exception.Message, ClientRectangle, Color.Firebrick, 10F); } catch { }
            }
        }

        protected override void OnResize(EventArgs e) { _renderDirty = true; base.OnResize(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _renderCache != null) { _renderCache.Dispose(); _renderCache = null; }
            base.Dispose(disposing);
        }

        private void InvalidateRender() { _renderDirty = true; Invalidate(); }

        // 预览绘制用到的画笔/笔刷/字体都是常量，提成静态字段。
        // 这个控件把渲染结果缓存在 _renderCache 里（改行、改尺寸才重画），所以这里不像
        // 其它预览控件那样是滚动热路径；但每次重画仍会 new 二十来个 GDI+ 对象，顺手清掉。
        private static readonly Pen HolePen = new Pen(Color.FromArgb(155, 165, 176), 1f) { DashStyle = DashStyle.Dash };
        private static readonly Pen FramePen = new Pen(Color.FromArgb(28, 40, 52), 2.2f);
        private static readonly Pen MullionPen = new Pen(Color.FromArgb(28, 40, 52), 1.5f);
        private static readonly Pen SashPen = new Pen(Color.FromArgb(150, 150, 150), 1.25f);
        private static readonly Pen OpeningPen = new Pen(Color.FromArgb(23, 116, 178), 1.25f) { DashStyle = DashStyle.Dash };
        private static readonly Pen MaterialPen = new Pen(Color.FromArgb(0, 165, 185), 1.15f);
        private static readonly Pen RescuePen = new Pen(Color.Red, 1.8f);
        private static readonly Pen DimensionPen = new Pen(Color.FromArgb(60, 120, 60), 1f);
        private static readonly SolidBrush RescueBrush = new SolidBrush(Color.Red);
        private static readonly SolidBrush DoorBrush = new SolidBrush(Color.FromArgb(185, 120, 55));
        private static readonly SolidBrush DimensionBrush = new SolidBrush(Color.FromArgb(45, 100, 45));
        private static readonly Font DoorFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private static readonly Font DimensionFont = new Font("Microsoft YaHei UI", 8.5f);
        private static readonly SolidBrush CenteredBrush = new SolidBrush(Color.Black);
        private static readonly StringFormat CenteredFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        private static readonly Dictionary<int, Font> CenteredFonts = new Dictionary<int, Font>();

        private static Font CenteredFont(float size)
        {
            var key = (int)Math.Round(Math.Max(4f, size) * 10f);
            Font font;
            if (CenteredFonts.TryGetValue(key, out font)) return font;
            font = new Font("Microsoft YaHei UI", key / 10f);
            CenteredFonts[key] = font;
            return font;
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
            var minX = -geometry.BayLeftExtent; var maxX = geometry.HoleWidth + geometry.BayRightExtent;
            var drawGeometryWidth = Safe((float)(maxX - minX), 1f);
            var holeH = Safe((float)geometry.HoleHeight, 1f);
            var scale = Math.Min(area.Width / drawGeometryWidth, area.Height / holeH);
            scale = Safe(scale, 1f);
            var drawWidth = drawGeometryWidth * scale; var drawHeight = holeH * scale;
            var originX = area.Left + (area.Width - drawWidth) / 2f - (float)minX * scale; var originY = area.Top + (area.Height - drawHeight) / 2f + drawHeight;
            foreach (var line in geometry.Lines)
            {
                var pen = line.Role == DoorWindowLineRole.Hole ? HolePen : line.Role == DoorWindowLineRole.Frame ? FramePen : line.Role == DoorWindowLineRole.Mullion ? MullionPen : line.Role == DoorWindowLineRole.SashFrame ? SashPen : line.Role == DoorWindowLineRole.Material ? MaterialPen : OpeningPen;
                var x1 = X(line.X1); var y1 = Y(line.Y1); var x2 = X(line.X2); var y2 = Y(line.Y2);
                if (!AllFinite(x1, y1, x2, y2)) continue;
                graphics.DrawLine(pen, x1, y1, x2, y2);
            }
            DrawDimensions(graphics, originX, originY, drawWidth, drawHeight);
            foreach (var cell in geometry.Cells)
                if (cell.IsDoor)
                {
                    var centerX = (X(cell.Left) + X(cell.Right)) / 2f; var centerY = (Y(cell.Bottom) + Y(cell.Top)) / 2f;
                    var size = graphics.MeasureString("门", DoorFont);
                    graphics.DrawString("门", DoorFont, DoorBrush, centerX - size.Width / 2f, centerY - size.Height / 2f);
                }
            if (_item.GenerateFireRescueElevation)
            {
                var rescueX = originX + (float)geometry.HoleWidth * scale * .5f;
                var rescueY = originY - holeH * scale * .52f;
                var rescueSize = Math.Max(10f, Math.Min(20f, Math.Min(drawWidth, drawHeight) * .12f));
                graphics.DrawRectangle(RescuePen, rescueX - rescueSize / 2f, rescueY - rescueSize / 2f, rescueSize, rescueSize);
                graphics.FillPolygon(RescueBrush, new[] { new PointF(rescueX, rescueY - rescueSize * .28f), new PointF(rescueX - rescueSize * .3f, rescueY + rescueSize * .25f), new PointF(rescueX + rescueSize * .3f, rescueY + rescueSize * .25f) });
            }
            var caption = (_item.Code ?? "未编号") + "  " + _item.SizeText + "  " + (_item.DivisionPreset ?? "") + " / " + (_item.OpeningMode ?? "");
            if (_item.GenerateFireRescueElevation) caption += "  ·  另生成消防救援窗版本";
            if (_item.ElevationType == "门联窗") caption += "  门" + (_item.DoorPlacement ?? "靠左") + (_item.DoorPlacement == "居中" ? string.Empty : "，距边 " + _item.DoorEdgeDistance.ToString("0.##") + " mm");
            if (_item.ElevationType == "凸窗") caption += "  左" + (_item.BayLeftSide ?? "墙") + " " + _item.BayLeftDepth.ToString("0.##") + " mm / 右" + (_item.BayRightSide ?? "墙") + " " + _item.BayRightDepth.ToString("0.##") + " mm";
            DrawCentered(graphics, caption, new RectangleF(8, Height - titleBand + 8, Math.Max(0, Width - 16), 24), Color.FromArgb(25, 36, 48), 10F);
            DrawCentered(graphics, "预览按窗口自适应；插入 CAD 时按洞口实际毫米 1:1 绘制", new RectangleF(8, Height - 27, Math.Max(0, Width - 16), 20), Color.DimGray, 8.5F);

            float X(double value) { return originX + (float)value * scale; }
            float Y(double value) { return originY - (float)value * scale; }
        }

        private void DrawDimensions(Graphics graphics, float x, float y, float width, float height)
        {
            var bottom = Math.Min(Height - 72f, y + 18f); graphics.DrawLine(DimensionPen, x, bottom, x + width, bottom); graphics.DrawLine(DimensionPen, x, y, x, bottom + 4); graphics.DrawLine(DimensionPen, x + width, y, x + width, bottom + 4);
            DrawArrow(graphics, DimensionPen, x, bottom, 1); DrawArrow(graphics, DimensionPen, x + width, bottom, -1);
            var widthText = _item.Width.ToString("0.##"); var size = graphics.MeasureString(widthText, DimensionFont); graphics.FillRectangle(Brushes.White, x + width / 2 - size.Width / 2, bottom - size.Height / 2, size.Width, size.Height); graphics.DrawString(widthText, DimensionFont, DimensionBrush, x + width / 2 - size.Width / 2, bottom - size.Height / 2);
            var left = Math.Max(11f, x - 18f); graphics.DrawLine(DimensionPen, left, y, left, y - height); graphics.DrawLine(DimensionPen, left - 4, y, x, y); graphics.DrawLine(DimensionPen, left - 4, y - height, x, y - height);
            var heightText = _item.Height.ToString("0.##"); var hSize = graphics.MeasureString(heightText, DimensionFont);
            var state = graphics.Save(); graphics.TranslateTransform(left, y - height / 2); graphics.RotateTransform(-90); graphics.FillRectangle(Brushes.White, -hSize.Width / 2, -hSize.Height / 2, hSize.Width, hSize.Height); graphics.DrawString(heightText, DimensionFont, DimensionBrush, -hSize.Width / 2, -hSize.Height / 2); graphics.Restore(state);
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
            CenteredBrush.Color = color;
            graphics.DrawString(text ?? string.Empty, CenteredFont(size), CenteredBrush, rectangle, CenteredFormat);
        }
    }
}
