using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken);
        void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken);
    }

    public static class CloudSyncProviderFactory
    {
        public static ICloudSyncProvider Create(CloudSyncSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (string.Equals(settings.Provider, "115OpenApi", StringComparison.OrdinalIgnoreCase))
                return new OneOneFiveOpenApiProvider(settings);
            if (string.Equals(settings.Provider, "BaiduNetdisk", StringComparison.OrdinalIgnoreCase))
                return new BaiduNetdiskProvider(settings);
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
        public void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (!IsReady) throw new InvalidOperationException(Status);
            Directory.CreateDirectory(WorkingFolder);
        }
        public void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken) { }
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
        public void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken) { throw new InvalidOperationException(Status); }
        public void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }

    public sealed class BaiduNetdiskProvider : ICloudSyncProvider
    {
        public const string DeveloperPortal = "https://yun.baidu.com/open/platform";
        private readonly CloudSyncSettings _settings;
        private readonly CloudSyncCredentialStore _credentials;
        private readonly BaiduNetdiskClient _client;
        private readonly Dictionary<string, string> _remoteHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private CloudSyncCredential _credential;

        public BaiduNetdiskProvider(CloudSyncSettings settings)
            : this(settings, new CloudSyncCredentialStore(), new BaiduNetdiskClient()) { }

        internal BaiduNetdiskProvider(CloudSyncSettings settings, CloudSyncCredentialStore credentials, BaiduNetdiskClient client)
        { _settings = settings ?? throw new ArgumentNullException("settings"); _credentials = credentials; _client = client; }

        public string Id { get { return "BaiduNetdisk"; } }
        public string DisplayName { get { return "百度网盘直连（无需客户端）"; } }
        public string WorkingFolder { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "provider-cache", "baidu"); } }
        public bool IsReady
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_settings.ProviderBrokerUrl))
                {
                    try { var brokerValue = _credentials.Load(Id); return brokerValue != null && string.Equals(brokerValue.AuthMode, "Broker", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(brokerValue.RefreshToken); }
                    catch { return false; }
                }
                if (string.IsNullOrWhiteSpace(_settings.ProviderClientId) || string.IsNullOrWhiteSpace(_settings.ProviderRedirectUri)) return false;
                try { var value = _credentials.Load(Id); return value != null && string.Equals(value.ClientId, _settings.ProviderClientId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(value.RefreshToken) && !string.IsNullOrWhiteSpace(value.ClientSecret); }
                catch { return false; }
            }
        }
        public string Status
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_settings.ProviderBrokerUrl))
                    return IsReady ? "百度网盘已通过万落统一应用授权，无需 App Key。" : "尚未登录百度网盘；请点击“登录百度网盘”。";
                if (string.IsNullOrWhiteSpace(_settings.ProviderClientId)) return "请填写百度开放平台 App Key。";
                if (string.IsNullOrWhiteSpace(_settings.ProviderRedirectUri)) return "请填写与百度应用登记一致的回调地址。";
                return IsReady ? "百度网盘已授权，可由插件直接同步，无需安装网盘客户端。" : "尚未授权百度网盘；请点击“连接百度网盘”。";
            }
        }

        public void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (!IsReady) throw new InvalidOperationException(Status);
            Directory.CreateDirectory(WorkingFolder);
            _credential = EnsureCredential(cancellationToken);
            progress?.Invoke(new CloudSyncProgress { Stage = "正在连接百度网盘" });
            IList<BaiduRemoteEntry> entries;
            try { entries = _client.ListRecursiveAsync(_credential.AccessToken, RemoteRoot, progress, cancellationToken).GetAwaiter().GetResult(); }
            catch (IOException ex) when (ex.Message.Contains("31066") || ex.Message.Contains("-9")) { entries = new List<BaiduRemoteEntry>(); }
            var files = entries.Where(x => !x.IsDirectory).ToList();
            _remoteHashes.Clear();
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = RelativeRemotePath(entry.Path);
                if (!ShouldTransferRelativePath(relative)) continue;
                var local = SafeLocalPath(relative);
                _remoteHashes[relative] = entry.Md5 ?? string.Empty;
                if (File.Exists(local) && FileLength(local) == entry.Size && string.Equals(Md5(local), entry.Md5, StringComparison.OrdinalIgnoreCase)) continue;
                progress?.Invoke(new CloudSyncProgress { Stage = "正在从百度网盘下载", LogicalPath = relative });
                _client.DownloadAsync(_credential.AccessToken, entry, local, (done, total) => progress?.Invoke(new CloudSyncProgress
                { Stage = "正在从百度网盘下载", LogicalPath = relative, Completed = ToProgress(done, total), Total = 1000 }), cancellationToken).GetAwaiter().GetResult();
            }
            var remoteSet = new HashSet<string>(_remoteHashes.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var local in Directory.GetFiles(WorkingFolder, "*", SearchOption.AllDirectories))
            {
                var relative = RelativeLocalPath(local);
                if (!remoteSet.Contains(relative)) File.Delete(local);
            }
        }

        public void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            _credential = EnsureCredential(cancellationToken);
            var current = Directory.GetFiles(WorkingFolder, "*", SearchOption.AllDirectories)
                .ToDictionary(RelativeLocalPath, x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in current)
            {
                cancellationToken.ThrowIfCancellationRequested(); var hash = Md5(pair.Value);
                if (_remoteHashes.TryGetValue(pair.Key, out var remoteHash) && string.Equals(hash, remoteHash, StringComparison.OrdinalIgnoreCase)) continue;
                progress?.Invoke(new CloudSyncProgress { Stage = "正在上传到百度网盘", LogicalPath = pair.Key });
                _client.UploadAsync(_credential.AccessToken, pair.Value, RemoteRoot + "/" + pair.Key.Replace('\\', '/'),
                    (done, total) => progress?.Invoke(new CloudSyncProgress { Stage = "正在上传到百度网盘", LogicalPath = pair.Key,
                        Completed = ToProgress(done, total), Total = 1000 }), cancellationToken).GetAwaiter().GetResult();
            }
            foreach (var removed in _remoteHashes.Keys.Where(x => !current.ContainsKey(x)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(new CloudSyncProgress { Stage = "正在删除百度网盘旧文件", LogicalPath = removed });
                _client.DeleteAsync(_credential.AccessToken, RemoteRoot + "/" + removed.Replace('\\', '/'), cancellationToken).GetAwaiter().GetResult();
            }
        }

        private CloudSyncCredential EnsureCredential(CancellationToken cancellationToken)
        {
            var value = _credential ?? _credentials.Load(Id);
            if (value == null) throw new InvalidOperationException("百度网盘尚未授权。");
            DateTime expires;
            if (string.IsNullOrWhiteSpace(value.AccessToken) || !DateTime.TryParse(value.ExpiresAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out expires) || expires <= DateTime.UtcNow)
            {
                if (string.Equals(value.AuthMode, "Broker", StringComparison.OrdinalIgnoreCase))
                {
                    using (var broker = new BaiduBrokerAuthClient()) value = broker.RefreshAsync(_settings.ProviderBrokerUrl, value.RefreshToken, cancellationToken).GetAwaiter().GetResult();
                }
                else value = _client.RefreshAsync(_settings.ProviderClientId, value, cancellationToken).GetAwaiter().GetResult();
                _credentials.Save(Id, value);
            }
            return value;
        }

        private string RemoteRoot { get { return BaiduNetdiskClient.NormalizeRemotePath(string.IsNullOrWhiteSpace(_settings.ProviderRemoteFolder) ? "/apps/万落建筑工具" : _settings.ProviderRemoteFolder); } }
        private string RelativeRemotePath(string path)
        {
            var normalized = BaiduNetdiskClient.NormalizeRemotePath(path); var root = RemoteRoot.TrimEnd('/');
            if (!normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) throw new IOException("百度网盘返回了应用目录外的路径。");
            return normalized.Substring(root.Length + 1).Replace('/', Path.DirectorySeparatorChar);
        }
        private string RelativeLocalPath(string path) { return Path.GetFullPath(path).Substring(Path.GetFullPath(WorkingFolder).TrimEnd(Path.DirectorySeparatorChar).Length + 1); }
        internal bool ShouldTransferRelativePath(string relativePath)
        {
            var path = CloudSyncSource.NormalizeLogicalPath(relativePath);
            const string projectPrefix = "万落建筑云同步/项目文件/";
            if (!path.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            var remainder = path.Substring(projectPrefix.Length);
            var slash = remainder.IndexOf('/');
            var cloudId = slash < 0 ? remainder : remainder.Substring(0, slash);
            return _settings.SyncProjectFiles && (_settings.ProjectMappings ?? new List<CloudSyncProjectMapping>()).Any(mapping =>
                mapping != null && mapping.Enabled && string.Equals(mapping.CloudId, cloudId, StringComparison.OrdinalIgnoreCase));
        }
        private string SafeLocalPath(string relative)
        {
            var root = Path.GetFullPath(WorkingFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("云端文件路径越出本机缓存目录。");
            return path;
        }
        private static long FileLength(string path) { try { return new FileInfo(path).Length; } catch { return -1; } }
        private static string Md5(string path) { using (var stream = File.OpenRead(path)) using (var md5 = MD5.Create()) { var builder = new StringBuilder(); foreach (var b in md5.ComputeHash(stream)) builder.Append(b.ToString("x2")); return builder.ToString(); } }
        private static int ToProgress(long done, long total) { return total <= 0 ? 0 : Math.Max(0, Math.Min(1000, (int)(done * 1000L / total))); }
        public void Dispose() { _client.Dispose(); }
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
            CloudProjectWorkspaceService.ValidateForProjectSync(settings, new PublishPlanStore().LoadProjects());
            using (var provider = CloudSyncProviderFactory.Create(settings))
            {
                provider.Prepare(progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureProviderState(store ?? new CloudSyncSettingsStore(), provider.Id);
                var result = new LocalFolderSyncEngine(store ?? new CloudSyncSettingsStore())
                    .Synchronize(settings, CloudSyncCatalog.CreateDefault(settings), provider.WorkingFolder, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                provider.Complete(result, progress, cancellationToken);
                return result;
            }
        }

        private static void EnsureProviderState(CloudSyncSettingsStore store, string providerId)
        {
            var state = store.LoadState();
            if (string.Equals(state.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(state.ProviderId) || !string.Equals(providerId, "LocalFolder", StringComparison.OrdinalIgnoreCase))
                state.Files.Clear();
            state.ProviderId = providerId;
            store.SaveState(state);
        }
    }
}
