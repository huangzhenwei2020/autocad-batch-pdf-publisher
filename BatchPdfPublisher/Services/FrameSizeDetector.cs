using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace BatchPdfPublisher.Services
{
    public sealed class FrameSizeGuess
    {
        public string PaperSize { get; set; }
        public string Extension { get; set; }
        public string MeasuredSize { get; set; }
    }

    public static class FrameSizeDetector
    {
        private static readonly Dictionary<string, double[]> BaseSizes = new Dictionary<string, double[]>
        {
            { "A0", new[] { 841d, 1189d } }, { "A1", new[] { 594d, 841d } },
            { "A2", new[] { 420d, 594d } }, { "A3", new[] { 297d, 420d } }
        };

        public static FrameSizeGuess Guess(Extents3d extents)
        {
            var width = Math.Abs(extents.MaxPoint.X - extents.MinPoint.X);
            var height = Math.Abs(extents.MaxPoint.Y - extents.MinPoint.Y);
            var shorter = Math.Min(width, height); var longer = Math.Max(width, height);
            var bestScore = double.MaxValue; string bestPaper = "A3"; string bestExtension = string.Empty;
            foreach (var candidate in BaseSizes)
            {
                foreach (var extension in new[] { 0d, .25d, .5d })
                {
                    var expectedShort = candidate.Value[0];
                    var expectedLong = candidate.Value[1] * (1d + extension);
                    var score = Math.Abs(Math.Log(Math.Max(shorter, 1d) / expectedShort)) + Math.Abs(Math.Log(Math.Max(longer, 1d) / expectedLong));
                    if (score < bestScore) { bestScore = score; bestPaper = candidate.Key; bestExtension = extension == 0 ? string.Empty : extension == .25 ? "1/4" : "1/2"; }
                }
            }
            return new FrameSizeGuess { PaperSize = bestPaper, Extension = bestExtension, MeasuredSize = Math.Round(width, 1) + " × " + Math.Round(height, 1) };
        }
    }
}
