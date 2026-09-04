using System;

namespace BatchPdfPublisher.Services
{
    public static class CloudSyncRetryPolicy
    {
        public static bool ShouldRetry(Exception failure, CloudSyncResult result)
        {
            if (failure is OperationCanceledException || failure is InvalidOperationException ||
                failure is ArgumentException || failure is UnauthorizedAccessException ||
                failure is NotSupportedException) return false;
            if (failure != null) return true;
            return result != null && result.Errors > 0;
        }
    }
}
