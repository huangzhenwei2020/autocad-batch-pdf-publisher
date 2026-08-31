using System;
using System.IO;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public interface ICloudSyncProvider : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        string WorkingFolder { get; }
        bool IsReady { get; }
        string Status { get; }
        void Prepare();
        void Complete(CloudSyncResult result);
    }

    public static class CloudSyncProviderFactory
    {
        public static ICloudSyncProvider Create(CloudSyncSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (string.Equals(settings.Provider, "115OpenApi", StringComparison.OrdinalIgnoreCase))
                return new OneOneFiveOpenApiProvider(settings);
            return new LocalFolderCloudSyncProvider(settings);
        }
    }

    public sealed class LocalFolderCloudSyncProvider : ICloudSyncProvider
    {
        private readonly CloudSyncSettings _settings;
        public LocalFolderCloudSyncProvider(CloudSyncSettings settings) { _settings = settings; }
        public string Id { get { return "LocalFolder"; } }
        public string DisplayName { get { return "通用云盘同步文件夹"; } }
        public string WorkingFolder { get { return string.IsNullOrWhiteSpace(_settings.SyncFolder) ? null : Path.GetFullPath(_settings.SyncFolder); } }
        public bool IsReady { get { return !string.IsNullOrWhiteSpace(WorkingFolder); } }
        public string Status { get { return CloudSyncFolderDetector.Describe(_settings.SyncFolder); } }
        public void Prepare()
        {
            if (!IsReady) throw new InvalidOperationException(Status);
            Directory.CreateDirectory(WorkingFolder);
        }
        public void Complete(CloudSyncResult result) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Official 115 OpenAPI adapter boundary. Network calls intentionally remain
    /// disabled until an approved application exposes its exact API document and
    /// Client ID; no cookie scraping or private web endpoint is permitted here.
    /// </summary>
    public sealed class OneOneFiveOpenApiProvider : ICloudSyncProvider
    {
        public const string DeveloperPortal = "https://open.115.com/";
        private readonly CloudSyncSettings _settings;
        public OneOneFiveOpenApiProvider(CloudSyncSettings settings) { _settings = settings; }
        public string Id { get { return "115OpenApi"; } }
        public string DisplayName { get { return "115 官方 OpenAPI"; } }
        public string WorkingFolder
        {
            get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "provider-cache", "115"); }
        }
        public bool IsReady { get { return false; } }
        public string Status
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_settings.ProviderClientId))
                    return "需要先在 115 开放平台完成开发者认证、创建并审核应用，然后填写 Client ID。";
                return "已保存 Client ID；请提供审核后台的官方接口文档参数后启用授权和文件传输。";
            }
        }
        public void Prepare() { throw new InvalidOperationException(Status); }
        public void Complete(CloudSyncResult result) { }
        public void Dispose() { }
    }

    public static class CloudSyncWorkflow
    {
        public static CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncSettingsStore store)
        {
            return Synchronize(settings, store, null);
        }

        public static CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncSettingsStore store,
            Action<CloudSyncProgress> progress)
        {
            return Synchronize(settings, store, progress, CancellationToken.None);
        }

        public static CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncSettingsStore store,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            using (var provider = CloudSyncProviderFactory.Create(settings))
            {
                provider.Prepare();
                cancellationToken.ThrowIfCancellationRequested();
                var result = new LocalFolderSyncEngine(store ?? new CloudSyncSettingsStore())
                    .Synchronize(settings, CloudSyncCatalog.CreateDefault(settings), provider.WorkingFolder, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                provider.Complete(result);
                return result;
            }
        }
    }
}
