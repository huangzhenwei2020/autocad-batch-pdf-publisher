using System;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal static class FloorStatisticsRules
    {
        public static bool TryParseFloorCount(string text, out int count)
        {
            count = 1;
            var match = Regex.Match(text ?? "", @"(\d+)\s*[~～至\-—]\s*(\d+)");
            int first, last;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out first) || !int.TryParse(match.Groups[2].Value, out last)) return false;
            var span = Math.Abs((long)last - first) + 1;
            if (span < 1 || span > 999) return false;
            count = (int)span; return true;
        }
    }
}
