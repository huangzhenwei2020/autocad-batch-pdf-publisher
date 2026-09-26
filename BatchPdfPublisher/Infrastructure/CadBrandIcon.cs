using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal static class CadBrandIcon
    {
        public static Control CreateTitleMark()
        {
            return new BrandMark { Dock = DockStyle.Fill, Margin = new Padding(5, 4, 5, 4) };
        }

        public static Icon CreateWindowIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                Draw(graphics, new Rectangle(0, 0, 32, 32));
                var handle = bitmap.GetHicon();
                try
                {
                    using (var nativeIcon = Icon.FromHandle(handle))
                        return (Icon)nativeIcon.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        private static void Draw(Graphics graphics, Rectangle bounds)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var state = graphics.Save();
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 32F, bounds.Height / 32F);

            using (var background = CadDialogTheme.Rounded(new Rectangle(1, 1, 30, 30), 6))
            using (var fill = new SolidBrush(Color.FromArgb(13, 92, 149)))
            using (var border = new Pen(Color.FromArgb(53, 184, 238), 1.2F))
            {
                graphics.FillPath(fill, background);
                graphics.DrawPath(border, background);
            }

            using (var roof = new Pen(Color.FromArgb(89, 211, 255), 2.2F))
            using (var letter = new Pen(Color.White, 3F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                graphics.DrawLines(roof, new[] { new PointF(6, 10), new PointF(16, 5), new PointF(26, 10) });
                graphics.DrawLines(letter, new[] { new PointF(6.5F, 12), new PointF(10, 24), new PointF(16, 18),
                    new PointF(22, 24), new PointF(25.5F, 12) });
            }
            graphics.Restore(state);
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private sealed class BrandMark : Control
        {
            public BrandMark()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? CadDialogTheme.Canvas : Parent.BackColor);
                var size = Math.Min(ClientSize.Width, ClientSize.Height);
                Draw(e.Graphics, new Rectangle((ClientSize.Width - size) / 2, (ClientSize.Height - size) / 2, size, size));
            }
        }
    }
}
