using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wanluo.LineVision.VectorWorker
{
    internal static class SvgPathParser
    {
        private static readonly Regex PathRegex = new Regex("<path[^>]*\\sd=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex("[A-Za-z]|[-+]?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?", RegexOptions.Compiled);

        public static List<VectorPolyline> Parse(string svg, double curveStep)
        {
            var result = new List<VectorPolyline>();
            foreach (Match path in PathRegex.Matches(svg ?? string.Empty)) ParsePath(path.Groups[1].Value, Math.Max(1d, curveStep), result);
            return result;
        }

        private static void ParsePath(string data, double curveStep, IList<VectorPolyline> result)
        {
            var tokens = TokenRegex.Matches(data); var index = 0; char command = '\0'; var current = new VectorPoint(); var start = new VectorPoint(); var points = new List<VectorPoint>();
            while (index < tokens.Count)
            {
                if (char.IsLetter(tokens[index].Value[0])) command = tokens[index++].Value[0];
                var relative = char.IsLower(command); var upper = char.ToUpperInvariant(command);
                if (upper == 'Z') { Add(result, points, true); points = new List<VectorPoint>(); current = new VectorPoint(start.X, start.Y); command = '\0'; continue; }
                if (upper == 'M' || upper == 'L')
                {
                    if (!Pair(tokens, ref index, out var x, out var y)) break; if (relative) { x += current.X; y += current.Y; }
                    current = new VectorPoint(x, y); if (upper == 'M') { if (points.Count > 1) Add(result, points, false); points = new List<VectorPoint>(); start = new VectorPoint(x, y); command = relative ? 'l' : 'L'; } points.Add(new VectorPoint(x, y)); continue;
                }
                if (upper == 'H' || upper == 'V')
                {
                    if (!Number(tokens, ref index, out var value)) break; var x = current.X; var y = current.Y; if (upper == 'H') x = relative ? x + value : value; else y = relative ? y + value : value;
                    current = new VectorPoint(x, y); points.Add(new VectorPoint(x, y)); continue;
                }
                if (upper == 'C')
                {
                    if (!Pair(tokens, ref index, out var x1, out var y1) || !Pair(tokens, ref index, out var x2, out var y2) || !Pair(tokens, ref index, out var x, out var y)) break;
                    if (relative) { x1 += current.X; y1 += current.Y; x2 += current.X; y2 += current.Y; x += current.X; y += current.Y; }
                    FlattenCubic(points, current, new VectorPoint(x1, y1), new VectorPoint(x2, y2), new VectorPoint(x, y), curveStep); current = new VectorPoint(x, y); continue;
                }
                index++;
            }
            Add(result, points, false);
        }

        private static void FlattenCubic(IList<VectorPoint> points, VectorPoint p0, VectorPoint p1, VectorPoint p2, VectorPoint p3, double step)
        {
            var estimate = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3); var count = Math.Max(2, (int)Math.Ceiling(estimate / step));
            for (var index = 1; index <= count; index++)
            {
                var t = index / (double)count; var u = 1d - t;
                points.Add(new VectorPoint(u * u * u * p0.X + 3d * u * u * t * p1.X + 3d * u * t * t * p2.X + t * t * t * p3.X, u * u * u * p0.Y + 3d * u * u * t * p1.Y + 3d * u * t * t * p2.Y + t * t * t * p3.Y));
            }
        }

        private static void Add(IList<VectorPolyline> result, List<VectorPoint> points, bool closed) { if (points.Count >= 2) result.Add(new VectorPolyline { Points = points, Closed = closed, Confidence = 0.92d }); }
        private static bool Pair(MatchCollection tokens, ref int index, out double x, out double y) { x = y = 0d; if (!Number(tokens, ref index, out x)) return false; return Number(tokens, ref index, out y); }
        private static bool Number(MatchCollection tokens, ref int index, out double value) { value = 0d; return index < tokens.Count && !char.IsLetter(tokens[index].Value[0]) && double.TryParse(tokens[index++].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        private static double Distance(VectorPoint a, VectorPoint b) { var x = a.X - b.X; var y = a.Y - b.Y; return Math.Sqrt(x * x + y * y); }
    }
}
