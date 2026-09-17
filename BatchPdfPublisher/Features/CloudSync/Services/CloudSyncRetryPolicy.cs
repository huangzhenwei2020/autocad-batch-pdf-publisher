using System;

namespace BatchPdfPublisher.Services
{
    public static class CloudSyncRetryPolicy
    {
        public static bool ShouldQueueWatcherEvent(bool synchronizationRunning)
        {
            // Provider-cache and downloaded-local-file writes are observed by the same
            // watchers as user edits. Re-queuing them creates endless no-op sync passes.
            return !synchronizationRunning;
        }

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
