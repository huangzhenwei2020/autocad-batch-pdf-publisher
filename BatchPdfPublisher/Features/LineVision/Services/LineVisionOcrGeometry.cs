using BatchPdfPublisher.Models;
using System;
using System.Drawing;

namespace BatchPdfPublisher.Services
{
    internal sealed class LineVisionOcrPlacement
    {
        public PointF BaselineOrigin { get; set; }
        public double TextHeightPixels { get; set; }
        public double RotationDegrees { get; set; }
    }

    internal static class LineVisionOcrGeometry
    {
        public static LineVisionOcrPlacement GetPlacement(LineVisionOcrTextRegion region)
        {
            if (region == null) throw new ArgumentNullException("region");
            var polygon = region.Polygon;
            if (polygon == null || polygon.Length < 4)
                polygon = CreatePolygon(region.Bounds, region.RotationDegrees);
            if (polygon == null || polygon.Length < 4)
                return new LineVisionOcrPlacement { RotationDegrees = NormalizeDegrees(region.RotationDegrees) };
            polygon = OrientToLongEdge(polygon);

            var edgeAngle = Angle(polygon[0], polygon[1]);
            var rotation = NormalizeDegrees(region.RotationDegrees);
            if (double.IsNaN(rotation) || double.IsInfinity(rotation)) rotation = edgeAngle;
            var reversed = Math.Abs(NormalizeDegrees(rotation - edgeAngle)) > 90d;
            var height = (Distance(polygon[0], polygon[3]) + Distance(polygon[1], polygon[2])) * 0.5d;
            return new LineVisionOcrPlacement
            {
                BaselineOrigin = reversed ? polygon[1] : polygon[3],
                TextHeightPixels = Math.Max(1d, height),
                RotationDegrees = rotation
            };
        }

        public static PointF[] CreatePolygon(RectangleF bounds, double rotationDegrees)
        {
            if (bounds.Width <= 0f || bounds.Height <= 0f) return new PointF[0];
            var rotation = NormalizeDegrees(rotationDegrees);
            var vertical = Math.Abs(rotation) > 45d && Math.Abs(rotation) < 135d;
            var length = vertical ? bounds.Height : bounds.Width;
            var height = vertical ? bounds.Width : bounds.Height;
            var radians = rotation * Math.PI / 180d;
            var alongX = Math.Cos(radians); var alongY = Math.Sin(radians);
            var downX = -alongY; var downY = alongX;
            var centerX = bounds.Left + bounds.Width * 0.5d;
            var centerY = bounds.Top + bounds.Height * 0.5d;
            var halfLength = length * 0.5d; var halfHeight = height * 0.5d;
            return new[]
            {
                Point(centerX - alongX * halfLength - downX * halfHeight, centerY - alongY * halfLength - downY * halfHeight),
                Point(centerX + alongX * halfLength - downX * halfHeight, centerY + alongY * halfLength - downY * halfHeight),
                Point(centerX + alongX * halfLength + downX * halfHeight, centerY + alongY * halfLength + downY * halfHeight),
                Point(centerX - alongX * halfLength + downX * halfHeight, centerY - alongY * halfLength + downY * halfHeight)
            };
        }

        public static double NormalizeDegrees(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return value;
            value %= 360d;
            if (value <= -180d) value += 360d;
            if (value > 180d) value -= 360d;
            return value;
        }

        private static PointF[] OrientToLongEdge(PointF[] polygon)
        {
            if (polygon.Length < 4 || Distance(polygon[0], polygon[1]) >= Distance(polygon[1], polygon[2])) return polygon;
            return new[] { polygon[1], polygon[2], polygon[3], polygon[0] };
        }

        private static double Angle(PointF first, PointF second)
        {
            return NormalizeDegrees(Math.Atan2(second.Y - first.Y, second.X - first.X) * 180d / Math.PI);
        }

        private static double Distance(PointF first, PointF second)
        {
            var dx = second.X - first.X; var dy = second.Y - first.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static PointF Point(double x, double y) { return new PointF((float)x, (float)y); }
    }
}
