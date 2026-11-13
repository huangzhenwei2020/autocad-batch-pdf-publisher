namespace BatchPdfPublisher.Services
{
    public static class CloudSyncCoordinator
    {
        public static void RequestSynchronization(bool immediate)
        {
            if (new CloudSyncSettingsStore().LoadSettings().Enabled) CloudSyncAgentSignal.Request();
        }

        public static void QueueReload(bool synchronizeAfterReload) => CloudSyncAgentSignal.Request();
    }
}
