using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Services
{
    public sealed class FrameSizeGuess
    {
        public string PaperSize { get; set; }
        public string Extension { get; set; }
        public string PaperOrientation { get; set; }
        public string PrintScale { get; set; }
        public string MeasuredSize { get; set; }
    }

    public static class FrameSizeDetector
    {
        private static readonly Dictionary<string, double[]> BaseSizes = new Dictionary<string, double[]>
        {
            { "A0", new[] { 841d, 1189d } }, { "A1", new[] { 594d, 841d } },
            { "A2", new[] { 420d, 594d } }, { "A3", new[] { 297d, 420d } },
            { "A4", new[] { 210d, 297d } }
        };

        public static FrameSizeGuess Guess(Extents3d extents, string knownPrintScale = null)
        {
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            var shorter = Math.Min(width, height);
            var longer = Math.Max(width, height);
            var bestScore = double.MaxValue;
            var bestScale = 1;
            var bestQuarterCount = 0;
            string bestPaper = "A3";
            var knownScale = ParseScale(knownPrintScale);
            foreach (var candidate in BaseSizes)
            {
                for (var quarterCount = 0; quarterCount <= 16; quarterCount++)
                {
                    var expectedShort = candidate.Value[0];
                    var expectedLong = candidate.Value[1] * (1d + quarterCount / 4d);
                    var shortScale = Math.Max(shorter, 1d) / expectedShort;
                    var longScale = Math.Max(longer, 1d) / expectedLong;
                    var averageScale = (shortScale + longScale) / 2d;
                    var integerScale = Math.Max(1, (int)Math.Round(averageScale));
                    var scaleMismatch = Math.Abs(shortScale - longScale) / Math.Max(averageScale, 1d);
                    var integerMismatch = Math.Abs(averageScale - integerScale) / integerScale;
                    var knownScaleMismatch = knownScale > 0 ? Math.Abs(Math.Log(integerScale / (double)knownScale)) : 0d;
                    var score = scaleMismatch + integerMismatch + knownScaleMismatch + quarterCount * 0.0001d;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPaper = candidate.Key;
                        bestScale = integerScale;
                        bestQuarterCount = quarterCount;
                    }
                }
            }
            return new FrameSizeGuess
            {
                PaperSize = bestPaper,
                Extension = FormatExtension(bestQuarterCount),
                PaperOrientation = width >= height ? "横向" : "纵向",
                PrintScale = "1:" + bestScale,
                MeasuredSize = Math.Round(width, 1) + " × " + Math.Round(height, 1)
            };
        }

        private static int ParseScale(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var parts = value.Trim().Split(':');
            var denominator = parts.Length > 1 ? parts[parts.Length - 1] : parts[0];
            return int.TryParse(denominator.Trim(), out var scale) && scale > 0 ? scale : 0;
        }

        private static string FormatExtension(int quarterCount)
        {
            if (quarterCount <= 0) return string.Empty;
            var whole = quarterCount / 4;
            var remainder = quarterCount % 4;
            var fraction = remainder == 1 ? "1/4" : remainder == 2 ? "1/2" : remainder == 3 ? "3/4" : string.Empty;
            if (whole == 0) return fraction;
            return string.IsNullOrEmpty(fraction) ? whole.ToString() : whole + " " + fraction;
        }
    }
}
