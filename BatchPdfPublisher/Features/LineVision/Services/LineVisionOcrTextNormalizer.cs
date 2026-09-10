using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionOcrTextNormalizer
    {
        private static readonly Regex DimensionMultiply = new Regex(@"(?<=\d)[ \t]*[xX×*][ \t]*(?=\d)", RegexOptions.Compiled);
        private static readonly Regex Diameter = new Regex(@"[ΦφØø∅][ \t]*(?=\d)", RegexOptions.Compiled);
        private static readonly Regex PlusMinus = new Regex(@"\+[ \t]*/[ \t]*-[ \t]*(?=\d)", RegexOptions.Compiled);
        private static readonly Regex NumericRatio = new Regex(@"(?<=\d)[ \t]*:[ \t]*(?=\d)", RegexOptions.Compiled);
        private static readonly Regex NumericDecimal = new Regex(@"(?<=\d)[。·](?=\d)", RegexOptions.Compiled);
        private static readonly Regex HorizontalWhitespace = new Regex(@"[ \t]+", RegexOptions.Compiled);

        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var value = text.Normalize(NormalizationForm.FormKC)
                .Replace('\u2212', '-')
                .Replace('\u2013', '-')
                .Replace('\u2014', '-')
                .Replace('\uFF5E', '~');
            value = DimensionMultiply.Replace(value, "×");
            value = Diameter.Replace(value, "Φ");
            value = PlusMinus.Replace(value, "±");
            value = NumericRatio.Replace(value, ":");
            value = NumericDecimal.Replace(value, ".");

            var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => HorizontalWhitespace.Replace(line, " ").Trim())
                .ToArray();
            return string.Join(Environment.NewLine, lines).Trim();
        }
    }
}
