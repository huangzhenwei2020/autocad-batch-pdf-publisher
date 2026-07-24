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
            { "A2", new Dictionary<double, double> { { .25d, 743d }, { .5d, 891d }, { .75d, 1041d }, { 1d, 1189d }, { 1.25d, 1338d }, { 1.5d, 1486d }, { 1.75d, 1635d }, { 2d, 1783d }, { 2.25d, 1932d }, { 2.5d, 2080d } } },
            { "A3", new Dictionary<double, double> { { .5d, 630d }, { 1d, 841d }, { 1.5d, 1051d }, { 2d, 1261d }, { 2.5d, 1471d }, { 3d, 1682d }, { 3.5d, 1892d } } }
        };

        private static readonly Dictionary<string, string[]> SupportedExtensions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "A0", new[] { "", "1/4", "1/2", "3/4", "1" } },
            { "A1", new[] { "", "1/4", "1/2", "3/4", "1", "5/4", "3/2" } },
            { "A2", new[] { "", "1/4", "1/2", "3/4", "1", "5/4", "3/2", "7/4", "2", "9/4", "5/2" } },
            { "A3", new[] { "", "1/2", "1", "3/2", "2", "5/2", "3", "7/2" } },
            { "A4", new[] { "" } }
        };

        public static string[] GetSupportedExtensions(string paper)
        {
            return SupportedExtensions.TryGetValue(paper ?? string.Empty, out var values) ? (string[])values.Clone() : new[] { "" };
        }

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
            foreach (var candidate in candidates)
                foreach (var candidateExtension in GetSupportedExtensions(candidate))
                    foreach (var candidateOrientation in new[] { "横向", "纵向" })
                    {
                        var size = GetSize(candidate, candidateExtension, candidateOrientation);
                        if (Math.Abs(width - size[0]) <= 1.0 && Math.Abs(height - size[1]) <= 1.0)
                        { paper = candidate; extension = candidateExtension; orientation = candidateOrientation; return true; }
                    }
            return false;
        }

        public static string FormatExtension(double value)
        {
            var quarter = (int)Math.Round(value * 4d, MidpointRounding.AwayFromZero);
            if (quarter <= 0) return string.Empty;
            switch (quarter)
            {
                case 1: return "1/4"; case 2: return "1/2"; case 3: return "3/4";
                case 4: return "1"; case 5: return "5/4"; case 6: return "3/2"; case 7: return "7/4";
                case 8: return "2"; case 9: return "9/4"; case 10: return "5/2"; case 12: return "3"; case 14: return "7/2";
                default: return (quarter / 4d).ToString(CultureInfo.InvariantCulture);
            }
        }

        public static string NormalizeExtension(string value)
        {
            return FormatExtension(ParseExtension(value));
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
