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
            var bestExtension = string.Empty;
            string bestPaper = "A3";
            var knownScale = ParseScale(knownPrintScale);
            foreach (var candidate in BaseSizes)
            {
                foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(candidate.Key))
                {
                    var expected = PaperSizeCatalog.GetSize(candidate.Key, extension, "横向");
                    var expectedLong = Math.Max(expected[0], expected[1]);
                    var expectedShort = Math.Min(expected[0], expected[1]);
                    var shortScale = shorter / Math.Max(expectedShort, 1d);
                    var longScale = longer / Math.Max(expectedLong, 1d);
                    var averageScale = (shortScale + longScale) / 2d;
                    var integerScale = Math.Max(1, (int)Math.Round(averageScale, MidpointRounding.AwayFromZero));
                    // Prefer an exact paper-library match (including 0.5/1x
                    // and extended fractions) before considering print scale.
                    var dimensionError = Math.Abs(shorter - expectedShort) / Math.Max(expectedShort, 1d)
                        + Math.Abs(longer - expectedLong) / Math.Max(expectedLong, 1d);
                    var scaleMismatch = Math.Abs(shortScale - longScale) / Math.Max(averageScale, 1d);
                    var knownScaleMismatch = knownScale > 0 ? Math.Abs(Math.Log(Math.Max(averageScale, 0.01d) / knownScale)) : 0d;
                    var score = dimensionError * 4d + scaleMismatch + knownScaleMismatch + PaperSizeCatalog.ParseExtension(extension) * 0.0001d;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPaper = candidate.Key;
                        bestScale = integerScale;
                        bestExtension = extension;
                    }
                }
            }
            return new FrameSizeGuess
            {
                PaperSize = bestPaper,
                Extension = PaperSizeCatalog.NormalizeExtension(bestExtension),
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

    }
}
