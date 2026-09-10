using System;
using System.Collections.Generic;
using System.Linq;

namespace WL.Stair.Core.Geometry
{
    public struct PlanClipSegment
    {
        public PlanClipSegment(Point2D start, Point2D end)
        {
            Start = start;
            End = end;
        }

        public Point2D Start { get; }

        public Point2D End { get; }
    }

    public static class PlanPolygonClipper
    {
        public static IList<PlanClipSegment> ClipSegment(
            Point2D start,
            Point2D end,
            IList<Point2D> polygon,
            double tolerance = 0.000001)
        {
            var result = new List<PlanClipSegment>();
            if (polygon == null || polygon.Count < 3) return result;

            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if (dx * dx + dy * dy <= tolerance * tolerance) return result;

            var parameters = new List<double> { 0.0, 1.0 };
            for (var index = 0; index < polygon.Count; index++)
            {
                var edgeStart = polygon[index];
                var edgeEnd = polygon[(index + 1) % polygon.Count];
                double segmentParameter;
                if (TryIntersect(
                    start,
                    end,
                    edgeStart,
                    edgeEnd,
                    tolerance,
                    out segmentParameter))
                    parameters.Add(Clamp01(segmentParameter));
            }

            var ordered = parameters
                .OrderBy(value => value)
                .Aggregate(new List<double>(), (values, value) =>
                {
                    if (values.Count == 0 || Math.Abs(values[values.Count - 1] - value) > tolerance)
                        values.Add(value);
                    return values;
                });

            for (var index = 0; index + 1 < ordered.Count; index++)
            {
                var first = ordered[index];
                var second = ordered[index + 1];
                if (second - first <= tolerance) continue;
                var middle = (first + second) * 0.5;
                var middlePoint = Interpolate(start, end, middle);
                if (!Contains(middlePoint, polygon, tolerance)) continue;
                result.Add(new PlanClipSegment(
                    Interpolate(start, end, first),
                    Interpolate(start, end, second)));
            }

            return result;
        }

        public static bool Contains(Point2D point, IList<Point2D> polygon, double tolerance = 0.000001)
        {
            if (polygon == null || polygon.Count < 3) return false;
            var inside = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var previous = (index + polygon.Count - 1) % polygon.Count;
                var current = polygon[index];
                var prior = polygon[previous];
                if (IsOnSegment(point, prior, current, tolerance)) return true;
                if ((current.Y > point.Y) != (prior.Y > point.Y)
                    && point.X < (prior.X - current.X) * (point.Y - current.Y)
                        / (prior.Y - current.Y) + current.X)
                    inside = !inside;
            }
            return inside;
        }

        private static bool TryIntersect(
            Point2D start,
            Point2D end,
            Point2D edgeStart,
            Point2D edgeEnd,
            double tolerance,
            out double segmentParameter)
        {
            var rx = end.X - start.X;
            var ry = end.Y - start.Y;
            var sx = edgeEnd.X - edgeStart.X;
            var sy = edgeEnd.Y - edgeStart.Y;
            var denominator = Cross(rx, ry, sx, sy);
            var qx = edgeStart.X - start.X;
            var qy = edgeStart.Y - start.Y;
            if (Math.Abs(denominator) <= tolerance)
            {
                segmentParameter = 0.0;
                return false;
            }

            var t = Cross(qx, qy, sx, sy) / denominator;
            var u = Cross(qx, qy, rx, ry) / denominator;
            segmentParameter = t;
            return t >= -tolerance && t <= 1.0 + tolerance
                && u >= -tolerance && u <= 1.0 + tolerance;
        }

        private static bool IsOnSegment(Point2D point, Point2D start, Point2D end, double tolerance)
        {
            var cross = Cross(
                end.X - start.X,
                end.Y - start.Y,
                point.X - start.X,
                point.Y - start.Y);
            if (Math.Abs(cross) > tolerance) return false;
            return point.X >= Math.Min(start.X, end.X) - tolerance
                && point.X <= Math.Max(start.X, end.X) + tolerance
                && point.Y >= Math.Min(start.Y, end.Y) - tolerance
                && point.Y <= Math.Max(start.Y, end.Y) + tolerance;
        }

        private static Point2D Interpolate(Point2D start, Point2D end, double parameter)
        {
            return new Point2D(
                start.X + (end.X - start.X) * parameter,
                start.Y + (end.Y - start.Y) * parameter);
        }

        private static double Cross(double firstX, double firstY, double secondX, double secondY)
        {
            return firstX * secondY - firstY * secondX;
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0.0, Math.Min(1.0, value));
        }
    }
}
