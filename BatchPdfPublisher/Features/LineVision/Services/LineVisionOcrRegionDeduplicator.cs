using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionOcrRegionDeduplicator
    {
        private const double MinimumContainment = 0.82d;
        private const double MinimumIntersectionOverUnion = 0.55d;

        public static List<LineVisionOcrTextRegion> Deduplicate(IEnumerable<LineVisionOcrTextRegion> source)
        {
            var items = (source ?? Enumerable.Empty<LineVisionOcrTextRegion>()).Where(item => item != null).ToList();
            if (items.Count < 2) return items;
            var parents = Enumerable.Range(0, items.Count).ToArray();
            for (var left = 0; left < items.Count; left++)
            {
                for (var right = left + 1; right < items.Count; right++)
                {
                    if (AreDuplicates(items[left], items[right])) Union(parents, left, right);
                }
            }

            return Enumerable.Range(0, items.Count)
                .GroupBy(index => Find(parents, index))
                .Select(group => group
                    .OrderByDescending(index => items[index].IsEnabled)
                    .ThenByDescending(index => items[index].Confidence)
                    .ThenBy(index => PolygonArea(items[index].Polygon))
                    .ThenBy(index => index)
                    .First())
                .OrderBy(index => index)
                .Select(index => items[index])
                .ToList();
        }

        internal static bool AreDuplicates(LineVisionOcrTextRegion first, LineVisionOcrTextRegion second)
        {
            var firstText = CanonicalText(first == null ? null : first.Text);
            var secondText = CanonicalText(second == null ? null : second.Text);
            if (firstText.Length == 0 || !string.Equals(firstText, secondText, StringComparison.OrdinalIgnoreCase)) return false;
            var firstPolygon = ValidPolygon(first);
            var secondPolygon = ValidPolygon(second);
            var firstArea = PolygonArea(firstPolygon); var secondArea = PolygonArea(secondPolygon);
            if (firstArea < 1d || secondArea < 1d) return false;
            var intersection = IntersectionArea(firstPolygon, secondPolygon);
            if (intersection <= 0d) return false;
            var containment = intersection / Math.Min(firstArea, secondArea);
            var union = firstArea + secondArea - intersection;
            var intersectionOverUnion = union > 0d ? intersection / union : 0d;
            return containment >= MinimumContainment || intersectionOverUnion >= MinimumIntersectionOverUnion;
        }

        private static string CanonicalText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var result = new StringBuilder(value.Length);
            foreach (var character in value) if (!char.IsWhiteSpace(character)) result.Append(character);
            return result.ToString();
        }

        private static PointF[] ValidPolygon(LineVisionOcrTextRegion region)
        {
            if (region == null) return new PointF[0];
            if (region.Polygon != null && region.Polygon.Length >= 3) return region.Polygon;
            var bounds = region.Bounds;
            return bounds.Width > 0f && bounds.Height > 0f ? LineVisionOcrGeometry.CreatePolygon(bounds, region.RotationDegrees) : new PointF[0];
        }

        private static double IntersectionArea(PointF[] subject, PointF[] clip)
        {
            var output = subject.ToList();
            var clipClockwise = SignedArea(clip) < 0d;
            for (var edge = 0; edge < clip.Length && output.Count > 0; edge++)
            {
                var input = output; output = new List<PointF>();
                var clipStart = clip[edge]; var clipEnd = clip[(edge + 1) % clip.Length];
                var previous = input[input.Count - 1]; var previousInside = IsInside(previous, clipStart, clipEnd, clipClockwise);
                foreach (var current in input)
                {
                    var currentInside = IsInside(current, clipStart, clipEnd, clipClockwise);
                    if (currentInside)
                    {
                        if (!previousInside) output.Add(LineIntersection(previous, current, clipStart, clipEnd));
                        output.Add(current);
                    }
                    else if (previousInside) output.Add(LineIntersection(previous, current, clipStart, clipEnd));
                    previous = current; previousInside = currentInside;
                }
            }
            return PolygonArea(output);
        }

        private static bool IsInside(PointF point, PointF edgeStart, PointF edgeEnd, bool clockwise)
        {
            var cross = Cross(edgeEnd.X - edgeStart.X, edgeEnd.Y - edgeStart.Y, point.X - edgeStart.X, point.Y - edgeStart.Y);
            return clockwise ? cross <= 0.001d : cross >= -0.001d;
        }

        private static PointF LineIntersection(PointF first, PointF second, PointF clipStart, PointF clipEnd)
        {
            var lineX = second.X - first.X; var lineY = second.Y - first.Y;
            var clipX = clipEnd.X - clipStart.X; var clipY = clipEnd.Y - clipStart.Y;
            var denominator = Cross(lineX, lineY, clipX, clipY);
            if (Math.Abs(denominator) < 1e-9) return second;
            var t = Cross(clipStart.X - first.X, clipStart.Y - first.Y, clipX, clipY) / denominator;
            return new PointF((float)(first.X + t * lineX), (float)(first.Y + t * lineY));
        }

        private static double PolygonArea(IEnumerable<PointF> polygon) { return Math.Abs(SignedArea(polygon)); }
        private static double SignedArea(IEnumerable<PointF> polygon)
        {
            var points = (polygon ?? Enumerable.Empty<PointF>()).ToList();
            if (points.Count < 3) return 0d;
            var twiceArea = 0d;
            for (var index = 0; index < points.Count; index++)
            {
                var next = points[(index + 1) % points.Count];
                twiceArea += points[index].X * next.Y - next.X * points[index].Y;
            }
            return twiceArea * 0.5d;
        }

        private static double Cross(double firstX, double firstY, double secondX, double secondY) { return firstX * secondY - firstY * secondX; }
        private static int Find(int[] parents, int index) { while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; } return index; }
        private static void Union(int[] parents, int left, int right) { left = Find(parents, left); right = Find(parents, right); if (left != right) parents[right] = left; }
    }
}
