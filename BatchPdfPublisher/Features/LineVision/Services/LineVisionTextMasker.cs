using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionTextMasker
    {
        public static void Apply(Bitmap target, bool[] darkPixels, IEnumerable<LineVisionOcrTextRegion> regions, double coordinateScale, double expansionPixels)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (coordinateScale <= 0d || double.IsNaN(coordinateScale) || double.IsInfinity(coordinateScale)) throw new ArgumentOutOfRangeException("coordinateScale");
            if (darkPixels != null && darkPixels.Length != target.Width * target.Height) throw new ArgumentException("文字遮罩像素尺寸与图片不一致。", "darkPixels");
            var paths = BuildPaths(regions, coordinateScale);
            if (paths.Count == 0) return;
            var expansion = Math.Max(0d, expansionPixels) * coordinateScale;
            try
            {
                Paint(target, paths, expansion, Color.White);
                if (darkPixels == null) return;
                using (var mask = new Bitmap(target.Width, target.Height, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(mask)) graphics.Clear(Color.Black);
                    Paint(mask, paths, expansion, Color.White);
                    ClearMaskedPixels(mask, darkPixels);
                }
            }
            finally
            {
                foreach (var path in paths) path.Dispose();
            }
        }

        private static List<GraphicsPath> BuildPaths(IEnumerable<LineVisionOcrTextRegion> regions, double scale)
        {
            var result = new List<GraphicsPath>();
            foreach (var region in (regions ?? Enumerable.Empty<LineVisionOcrTextRegion>()).Where(value => value != null && value.IsEnabled))
            {
                var polygon = region.Polygon;
                if (polygon == null || polygon.Length < 3) continue;
                var points = polygon.Select(point => new PointF((float)(point.X * scale), (float)(point.Y * scale))).ToArray();
                if (Math.Abs(SignedArea(points)) < 0.5d) continue;
                var path = new GraphicsPath(); path.AddPolygon(points); result.Add(path);
            }
            return result;
        }

        private static void Paint(Bitmap target, IEnumerable<GraphicsPath> paths, double expansion, Color color)
        {
            using (var graphics = Graphics.FromImage(target))
            using (var brush = new SolidBrush(color))
            {
                graphics.SmoothingMode = SmoothingMode.None;
                foreach (var path in paths)
                {
                    graphics.FillPath(brush, path);
                    if (expansion <= 0d) continue;
                    using (var pen = new Pen(color, (float)Math.Max(1d, expansion * 2d)) { LineJoin = LineJoin.Miter, MiterLimit = 2f }) graphics.DrawPath(pen, path);
                }
            }
        }

        private static void ClearMaskedPixels(Bitmap mask, bool[] darkPixels)
        {
            var data = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    var start = (byte*)data.Scan0;
                    for (var y = 0; y < mask.Height; y++)
                    {
                        var row = start + y * data.Stride;
                        for (var x = 0; x < mask.Width; x++) if (row[x * 3] != 0) darkPixels[y * mask.Width + x] = false;
                    }
                }
            }
            finally { mask.UnlockBits(data); }
        }

        private static double SignedArea(IList<PointF> points)
        {
            var twiceArea = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var next = points[(index + 1) % points.Count];
                twiceArea += points[index].X * next.Y - next.X * points[index].Y;
            }
            return twiceArea * 0.5d;
        }
    }
}
