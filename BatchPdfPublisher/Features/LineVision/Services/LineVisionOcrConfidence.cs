using System;

namespace BatchPdfPublisher.Services
{
    internal static class LineVisionOcrConfidence
    {
        public static bool IsLow(double confidence, double threshold)
        {
            if (double.IsNaN(confidence) || double.IsInfinity(confidence)) return true;
            var safeThreshold = Math.Max(0d, Math.Min(1d, threshold));
            return confidence < safeThreshold;
        }

        public static string StatusText(double confidence, double threshold)
        {
            return IsLow(confidence, threshold) ? "待复核" : "正常";
        }
    }
}
