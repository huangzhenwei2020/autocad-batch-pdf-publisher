using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal struct CorrectionPoint
    {
        internal double X, Y, Z;
        internal CorrectionPoint(double x, double y, double z = 0) { X = x; Y = y; Z = z; }
    }

    internal sealed class LineCorrectionOptions
    {
        internal double ToleranceDegrees = 1;
        internal bool Horizontal = true, Vertical = true, FlattenZ = true;
    }

    internal sealed class LineCorrectionResult
    {
        internal List<CorrectionPoint> Points;
        internal int CorrectedSegments, FlattenedVertices;
        internal double MaximumMovement;
        internal string Warning;
        internal bool Changed { get { return CorrectedSegments > 0 || FlattenedVertices > 0; } }
    }

    internal static class LineCorrectionGeometry
    {
        private struct Axis
        {
            internal CorrectionPoint Center;
            internal double X, Y, Length;
            internal bool Changed;
        }

        internal static LineCorrectionResult Calculate(IList<CorrectionPoint> source, bool closed,
            bool hasArcs, LineCorrectionOptions options)
        {
            if (options == null || double.IsNaN(options.ToleranceDegrees) || double.IsInfinity(options.ToleranceDegrees)
                || options.ToleranceDegrees < 0 || options.ToleranceDegrees >= 45)
                throw new ArgumentException("角度容差必须大于等于 0 且小于 45 度。");
            if (source == null || source.Count < 2 || source.Any(p => !Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)))
                throw new ArgumentException("顶点坐标无效。");
            var result = new LineCorrectionResult { Points = source.ToList() };
            if (hasArcs && (options.Horizontal || options.Vertical)) result.Warning = "含弧段，保留方向";
            else if (options.Horizontal || options.Vertical)
            {
                string warning;
                var corrected = Correct(source, closed, options, out warning);
                if (corrected != null)
                {
                    result.Points = corrected.Item1;
                    result.CorrectedSegments = corrected.Item2;
                }
                result.Warning = warning;
            }
            for (var i = 0; i < source.Count; i++)
            {
                var point = result.Points[i];
                if (options.FlattenZ && source[i].Z != 0) { point.Z = 0; result.FlattenedVertices++; }
                result.Points[i] = point;
                var dx = point.X - source[i].X; var dy = point.Y - source[i].Y; var dz = point.Z - source[i].Z;
                result.MaximumMovement = Math.Max(result.MaximumMovement, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            return result;
        }

        private static Tuple<List<CorrectionPoint>, int> Correct(IList<CorrectionPoint> points, bool closed,
            LineCorrectionOptions options, out string warning)
        {
            warning = null;
            var count = closed ? points.Count : points.Count - 1;
            var axes = new Axis[count]; var changed = 0;
            for (var i = 0; i < count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-9) { warning = "含零长度或仅 Z 向线段，保留方向"; return null; }
                var axis = new Axis { Center = new CorrectionPoint(a.X + dx / 2, a.Y + dy / 2), X = dx / length, Y = dy / length, Length = length };
                var horizontal = Math.Atan2(Math.Abs(dy), Math.Abs(dx)) * 180 / Math.PI;
                var vertical = 90 - horizontal;
                if (options.Horizontal && horizontal <= options.ToleranceDegrees + 1e-12)
                { axis.X = dx < 0 ? -1 : 1; axis.Y = 0; axis.Changed = dy != 0; }
                else if (options.Vertical && vertical <= options.ToleranceDegrees + 1e-12)
                { axis.X = 0; axis.Y = dy < 0 ? -1 : 1; axis.Changed = dx != 0; }
                axes[i] = axis; if (axis.Changed) changed++;
            }
            if (changed == 0) return Tuple.Create(points.ToList(), 0);
            var output = points.ToList();
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (!closed && (i == 0 || i == points.Count - 1))
                {
                    var axis = axes[i == 0 ? 0 : count - 1];
                    if (!axis.Changed) continue;
                    var sign = i == 0 ? -1d : 1d;
                    output[i] = new CorrectionPoint(axis.Center.X + sign * axis.X * axis.Length / 2,
                        axis.Center.Y + sign * axis.Y * axis.Length / 2, point.Z);
                    continue;
                }
                var previous = axes[(i + count - 1) % count]; var next = axes[i];
                if (!previous.Changed && !next.Changed) continue;
                var cross = previous.X * next.Y - previous.Y * next.X;
                var dx = next.Center.X - previous.Center.X; var dy = next.Center.Y - previous.Center.Y;
                double x, y;
                if (Math.Abs(cross) < 1e-10)
                {
                    if (Math.Abs(dx * previous.Y - dy * previous.X) > 1e-7)
                    { warning = "相邻平行段不能同时保持中点，保留方向"; return null; }
                    var t = (point.X - previous.Center.X) * previous.X + (point.Y - previous.Center.Y) * previous.Y;
                    x = previous.Center.X + t * previous.X; y = previous.Center.Y + t * previous.Y;
                }
                else
                {
                    // Rotate each segment's supporting line about its midpoint,
                    // then trim/extend adjacent lines to one shared vertex.
                    var t = (dx * next.Y - dy * next.X) / cross;
                    x = previous.Center.X + t * previous.X; y = previous.Center.Y + t * previous.Y;
                }
                var movement = Math.Sqrt((x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y));
                if (!Finite(x) || !Finite(y) || movement > Math.Min(previous.Length, next.Length) / 2)
                { warning = "公共顶点位移过大，保留方向"; return null; }
                output[i] = new CorrectionPoint(x, y, point.Z);
            }
            for (var i = 0; i < count; i++)
            {
                var a = output[i]; var b = output[(i + 1) % output.Count]; var axis = axes[i];
                if ((b.X - a.X) * axis.X + (b.Y - a.Y) * axis.Y <= 1e-8)
                { warning = "校正会压缩或反转线段，保留方向"; return null; }
            }
            return Tuple.Create(output, changed);
        }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
    }
}
