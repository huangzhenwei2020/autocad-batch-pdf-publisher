using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static readonly string[] Papers = { "A0", "A1", "A2", "A3", "A4" };
        private static readonly int[] CommonScales = { 1, 2, 5, 10, 20, 25, 30, 40, 50, 75, 100, 150, 200, 250, 300, 400, 500, 750, 1000 };

        public static FrameSizeGuess Guess(Extents3d extents, string knownPrintScale = null, string blockName = null)
        {
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            var shorter = Math.Min(width, height);
            var longer = Math.Max(width, height);
            var knownScale = ParseScale(knownPrintScale);
            var namePaper = ParsePaperFromName(blockName);
            var nameExtension = ParseExtensionFromName(blockName, namePaper);
            var bestScore = double.MaxValue;
            var bestScale = 1;
            var bestExtension = string.Empty;
            var bestPaper = namePaper ?? "A3";

            foreach (var paper in Papers)
            {
                // A block name such as A3_BPP_... is deliberate metadata and is
                // more reliable than dimensions alone (A3@100 resembles A1@50).
                if (!string.IsNullOrWhiteSpace(namePaper) && !string.Equals(paper, namePaper, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(paper))
                {
                    if (nameExtension != null && !string.Equals(PaperSizeCatalog.NormalizeExtension(extension), nameExtension, StringComparison.OrdinalIgnoreCase)) continue;
                    var expected = PaperSizeCatalog.GetSize(paper, extension, "横向");
                    var expectedLong = Math.Max(expected[0], expected[1]);
                    var expectedShort = Math.Min(expected[0], expected[1]);
                    var rawScale = (shorter / expectedShort + longer / expectedLong) / 2d;
                    var scale = knownScale > 0 ? knownScale : NearestCommonScale(rawScale);
                    var normalizedShort = shorter / Math.Max(scale, 1);
                    var normalizedLong = longer / Math.Max(scale, 1);
                    var dimensionError = Math.Abs(normalizedShort - expectedShort) / expectedShort
                        + Math.Abs(normalizedLong - expectedLong) / expectedLong;
                    var uniformityError = Math.Abs(shorter / expectedShort - longer / expectedLong) / Math.Max(rawScale, 0.0001d);
                    var scaleError = knownScale > 0 ? 0d : Math.Abs(Math.Log(Math.Max(rawScale, .0001d) / scale));
                    var score = dimensionError * 8d + uniformityError * 4d + scaleError;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPaper = paper;
                        bestScale = scale;
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

        private static int NearestCommonScale(double value)
        {
            if (value <= 0d) return 1;
            return CommonScales.OrderBy(x => Math.Abs(Math.Log(value / x))).First();
        }

        private static string ParsePaperFromName(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"(?:^|[^A-Z0-9])(A[0-4])(?:[^0-9]|$)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }

        // Created block names encode '/' as '_', for example A1+1_4_BPP_封面.
        private static string ParseExtensionFromName(string value, string paper)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(paper)) return null;
            var tail = value.Substring(Math.Min(value.Length, value.IndexOf(paper, StringComparison.OrdinalIgnoreCase) + paper.Length));
            var match = Regex.Match(tail, @"^\s*[+＋]\s*(\d+)(?:\s*[/_]\s*(\d+))?", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var raw = match.Groups[1].Value + (match.Groups[2].Success ? "/" + match.Groups[2].Value : string.Empty);
            return PaperSizeCatalog.NormalizeExtension(raw);
        }

        private static int ParseScale(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var parts = value.Trim().Replace('：', ':').Split(':');
            var denominator = parts.Length > 1 ? parts[parts.Length - 1] : parts[0];
            return int.TryParse(denominator.Trim(), out var scale) && scale > 0 ? scale : 0;
        }
    }
}
