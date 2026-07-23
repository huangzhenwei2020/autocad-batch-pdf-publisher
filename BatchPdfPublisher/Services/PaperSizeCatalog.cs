using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    // GB/T 14689 基本幅面 B×L；加长幅面按 L + 分数×L 计算，并取整到毫米。
    public static class PaperSizeCatalog
    {
        private static readonly Dictionary<string, double[]> BasicSizes = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "A0", new[] { 841d, 1189d } }, { "A1", new[] { 594d, 841d } },
            { "A2", new[] { 420d, 594d } }, { "A3", new[] { 297d, 420d } },
            { "A4", new[] { 210d, 297d } }
        };

        // GB/T 14689 常用加长幅面长边（毫米）。先查标准表，未列出的特殊
        // 比例才按 L + 分数×L 取整，避免 A1+1/2 被错误算成 1262 mm。
        private static readonly Dictionary<string, Dictionary<double, double>> ExtendedLongSides = new Dictionary<string, Dictionary<double, double>>(StringComparer.OrdinalIgnoreCase)
        {
            { "A0", new Dictionary<double, double> { { .25d, 1486d }, { .5d, 1783d }, { .75d, 2080d }, { 1d, 2378d } } },
            { "A1", new Dictionary<double, double> { { .25d, 1051d }, { .5d, 1261d }, { .75d, 1471d }, { 1d, 1682d }, { 1.25d, 1892d }, { 1.5d, 2102d } } },
            { "A2", new Dictionary<double, double> { { .25d, 743d }, { .5d, 891d }, { .75d, 1041d }, { 1d, 1189d }, { 1.25d, 1338d }, { 1.5d, 1486d } } },
            { "A3", new Dictionary<double, double> { { .25d, 525d }, { .5d, 630d }, { .75d, 735d }, { 1d, 841d }, { 1.5d, 1051d } } }
        };

        public static double[] GetSize(string paper, string extension, string orientation)
        {
            if (!BasicSizes.TryGetValue(paper ?? string.Empty, out var basic)) basic = BasicSizes["A3"];
            var fraction = ParseExtension(extension);
            var longSide = ExtendedLongSides.TryGetValue(paper ?? string.Empty, out var lengths) && lengths.TryGetValue(fraction, out var standardLength)
                ? standardLength
                : Math.Round(basic[1] * (1d + fraction), MidpointRounding.AwayFromZero);
            var landscape = string.Equals(orientation, "横向", StringComparison.OrdinalIgnoreCase);
            return landscape ? new[] { longSide, basic[0] } : new[] { basic[0], longSide };
        }

        public static string Describe(string paper, string extension, string orientation)
        {
            var size = GetSize(paper, extension, orientation);
            return Math.Round(size[0]).ToString(CultureInfo.InvariantCulture) + " × " + Math.Round(size[1]).ToString(CultureInfo.InvariantCulture) + " mm";
        }

        public static bool TryIdentify(double width, double height, out string paper, out string extension, out string orientation)
        {
            paper = string.Empty; extension = string.Empty; orientation = string.Empty;
            var candidates = new[] { "A0", "A1", "A2", "A3", "A4" };
            var extensions = new[] { string.Empty, "1/4", "1/2", "3/4", "1", "1 1/4", "1 1/2", "2" };
            foreach (var candidate in candidates)
                foreach (var candidateExtension in extensions)
                    foreach (var candidateOrientation in new[] { "横向", "纵向" })
                    {
                        var size = GetSize(candidate, candidateExtension, candidateOrientation);
                        if (Math.Abs(width - size[0]) <= 1.0 && Math.Abs(height - size[1]) <= 1.0)
                        { paper = candidate; extension = candidateExtension; orientation = candidateOrientation; return true; }
                    }
            return false;
        }

        public static double ParseExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0d;
            var normalized = value.Trim().Replace("L", string.Empty).Replace("l", string.Empty).Replace("＋", "+").TrimStart('+').Trim();
            var fraction = Regex.Match(normalized, @"(\d+)\s*/\s*(\d+)");
            if (fraction.Success && double.TryParse(fraction.Groups[1].Value, out var numerator) && double.TryParse(fraction.Groups[2].Value, out var denominator) && denominator != 0d)
                return numerator / denominator;
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0d;
        }
    }
}
