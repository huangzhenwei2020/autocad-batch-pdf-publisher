using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    internal interface IVersionedSyncProvider { IEnumerable<string> BlockedPaths { get; } }
    public interface ICloudSyncProvider : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        string StateIdentity { get; }
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

    public sealed class LocalFolderCloudSyncProvider : ICloudSyncProvider, IVersionedSyncProvider
    {
        private readonly CloudSyncSettings _settings;
        public LocalFolderCloudSyncProvider(CloudSyncSettings settings) { _settings = settings; }
        public string Id { get { return "LocalFolder"; } }
        public string DisplayName { get { return "通用云盘同步文件夹"; } }
        public string StateIdentity { get { return "LocalFolder|" + NormalizeIdentityPath(_settings.SyncFolder); } }
        public string WorkingFolder { get { return CloudSyncWorkflow.ScopedCache(StateIdentity); } }
        public bool IsReady { get { return !string.IsNullOrWhiteSpace(_settings.SyncFolder); } }
        private ImmutableCloudJournal _journal;
        public IEnumerable<string> BlockedPaths { get { return _journal == null ? Enumerable.Empty<string>() : _journal.Blocked; } }
        public string Status { get { return CloudSyncFolderDetector.Describe(_settings.SyncFolder); } }
        public void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (!IsReady) throw new InvalidOperationException(Status);
            LocalFolderSyncEngine.ValidateRoots(_settings.SyncFolder, CloudSyncCatalog.CreateDefault(_settings).Roots);
            Directory.CreateDirectory(WorkingFolder);
            var mirror = Path.Combine(WorkingFolder, "万落建筑云同步");
            _journal = new ImmutableCloudJournal(Path.Combine(WorkingFolder, ".v2"), mirror);
            var remote = Path.Combine(_settings.SyncFolder, ImmutableCloudJournal.RemoteDirectory);
            Directory.CreateDirectory(remote);
            foreach (var file in Directory.GetFiles(remote, "*.zip"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(_journal.Archives, Path.GetFileName(file));
                var expected = Path.GetFileNameWithoutExtension(file);
                if (!string.Equals(CloudSyncTransaction.Hash(target), expected, StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = target + ".download";
                    File.Copy(file, tmp, true);
                    if (!string.Equals(CloudSyncTransaction.Hash(tmp), expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("同步目录中的版本包尚未传输完整，稍后重试；未应用本机文件。");
                    if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
                }
            }
            var legacyRoot = Path.Combine(_settings.SyncFolder, "万落建筑云同步");
            var catalog = CloudSyncCatalog.CreateDefault(_settings);
            if (Directory.Exists(legacyRoot))
                foreach (var file in Directory.GetFiles(legacyRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = CloudSyncSource.NormalizeLogicalPath(file.Substring(legacyRoot.Length));
                    string mapped;
                    if (!catalog.TryResolve(relative, out mapped) && relative != CloudSystemPackageService.LogicalPrefix + "/" + CloudSystemPackageService.PackageFileName) continue;
                    var target = ImmutableCloudJournal.SafePath(mirror, relative);
                    if (File.Exists(ImmutableCloudJournal.SafePath(Path.Combine(mirror, ".wanluo-sync", "resolutions"), relative + ".json"))) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
                }
            CloudSystemPackageService.ExpandLegacyToMirror(_settings, mirror, cancellationToken);
            _journal.Materialize(catalog, cancellationToken);
        }
        public void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            result.Conflicts += _journal.Blocked.Count;
            var commit = _journal.CreateCommit(CloudSyncCatalog.CreateDefault(_settings), cancellationToken);
            if (commit == null) return;
            var target = Path.Combine(_settings.SyncFolder, ImmutableCloudJournal.RemoteDirectory, Path.GetFileName(commit));
            if (!File.Exists(target))
            {
                var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(commit, tmp);
                cancellationToken.ThrowIfCancellationRequested();
                if (CloudSyncTransaction.Hash(tmp) != Path.GetFileNameWithoutExtension(target)) throw new IOException("版本提交校验失败。");
                try { File.Move(tmp, target); }
                catch (IOException) { if (CloudSyncTransaction.Hash(target) != CloudSyncTransaction.Hash(commit)) throw; }
            }
            if (CloudSyncTransaction.Hash(target) != CloudSyncTransaction.Hash(commit)) throw new IOException("不可变版本已被外部修改。");
        }
        public void Dispose() { }
        private static string NormalizeIdentityPath(string value) { return string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
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
        public string StateIdentity { get { return "115OpenApi|" + (_settings.ProviderClientId ?? string.Empty).Trim(); } }
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

    public sealed class BaiduNetdiskProvider : ICloudSyncProvider, IVersionedSyncProvider
    {
        public const string DeveloperPortal = "https://yun.baidu.com/open/platform";
        private readonly CloudSyncSettings _settings;
        private readonly CloudSyncCredentialStore _credentials;
        private readonly BaiduNetdiskClient _client;
        private CloudSyncCredential _credential;

        public BaiduNetdiskProvider(CloudSyncSettings settings)
            : this(settings, new CloudSyncCredentialStore(), new BaiduNetdiskClient()) { }

        internal BaiduNetdiskProvider(CloudSyncSettings settings, CloudSyncCredentialStore credentials, BaiduNetdiskClient client)
        { _settings = settings ?? throw new ArgumentNullException("settings"); _credentials = credentials; _client = client; }

        public string Id { get { return "BaiduNetdisk"; } }
        public string DisplayName { get { return "百度网盘直连（无需客户端）"; } }
        public string StateIdentity
        {
            get
            {
                var value = _credential ?? _credentials.Load(Id);
                var identity = value == null ? string.Empty : EnsureAccountIdentity(value);
                return "BaiduNetdisk|" + (identity ?? string.Empty) + "|" + RemoteRoot.ToUpperInvariant();
            }
        }
        public string WorkingFolder { get { return CloudSyncWorkflow.ScopedCache(StateIdentity); } }
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

        private ImmutableCloudJournal _journal;
        private int _networkDownloads;
        private long _networkDownloadBytes;
        public IEnumerable<string> BlockedPaths { get { return _journal == null ? Enumerable.Empty<string>() : _journal.Blocked; } }

        public void Prepare(Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (!IsReady) throw new InvalidOperationException(Status);
            _credential = EnsureCredential(cancellationToken);
            Directory.CreateDirectory(WorkingFolder);
            _client.EnsureDirectoryAsync(_credential.AccessToken, RemoteRoot + "/" + ImmutableCloudJournal.RemoteDirectory, cancellationToken).GetAwaiter().GetResult();
            var entries = _client.ListRecursiveAsync(_credential.AccessToken, RemoteRoot, progress, cancellationToken).GetAwaiter().GetResult();
            var mirror = Path.Combine(WorkingFolder, "万落建筑云同步");
            _journal = new ImmutableCloudJournal(Path.Combine(WorkingFolder, ".v2"), mirror);
            _networkDownloads = 0; _networkDownloadBytes = 0;
            var paths = new List<string>();
            foreach (var entry in entries.Where(e => !e.IsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = CloudSyncSource.NormalizeLogicalPath(RelativeRemotePath(entry.Path));
                if (relative.StartsWith(ImmutableCloudJournal.RemoteDirectory + "/", StringComparison.Ordinal))
                {
                    var name = relative.Substring(ImmutableCloudJournal.RemoteDirectory.Length + 1);
                    if (name.Contains("/") || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = ImmutableCloudJournal.SafePath(_journal.Archives, name);
                    if (!File.Exists(target) || !string.Equals(CloudSyncTransaction.Hash(target), Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                        DownloadVerified(entry, target, progress, cancellationToken);
                    continue;
                }
                if (!ShouldTransferRelativePath(relative) || !relative.StartsWith("万落建筑云同步/", StringComparison.Ordinal)) continue;
                var logical = relative.Substring("万落建筑云同步/".Length);
                if (logical.StartsWith("历史版本/") || logical.StartsWith("冲突文件/") || logical.StartsWith(".wanluo-sync/")) continue;
                var legacy = ImmutableCloudJournal.SafePath(Path.Combine(WorkingFolder, ".legacy"), logical);
                if (!CachedFileMatches(entry, legacy)) DownloadVerified(entry, legacy, progress, cancellationToken);
                var targetPath = ImmutableCloudJournal.SafePath(mirror, logical);
                // Explicit user resolution remains pending until its immutable commit is published.
                if (!File.Exists(ImmutableCloudJournal.SafePath(Path.Combine(mirror, ".wanluo-sync", "resolutions"), logical + ".json")))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    File.Copy(legacy, targetPath, true);
                }
                paths.Add(relative);
            }
            CloudSystemPackageService.ExpandLegacyToMirror(_settings, mirror, cancellationToken);
            _journal.Materialize(CloudSyncCatalog.CreateDefault(_settings), cancellationToken);
            paths.AddRange(CloudSyncCatalog.CreateDefault(_settings).EnumerateFiles()
                .Where(f => File.Exists(ImmutableCloudJournal.SafePath(mirror, f.LogicalPath))).Select(f => "万落建筑云同步/" + f.LogicalPath));
            CloudSyncRemoteInventoryStore.Save(paths);
        }

        private void DownloadVerified(BaiduRemoteEntry entry, string target, Action<CloudSyncProgress> progress, CancellationToken token)
        {
            progress?.Invoke(new CloudSyncProgress { Stage = "正在下载并校验云端版本", LogicalPath = entry.Path });
            var temporary = target + ".download";
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            _client.DownloadAsync(_credential.AccessToken, entry, temporary, (done, total) =>
                progress?.Invoke(new CloudSyncProgress { Stage = "正在下载云端版本", Direction = "下载", BytesCompleted = done, BytesTotal = total }), token).GetAwaiter().GetResult();
            if (new FileInfo(temporary).Length != entry.Size ||
                (!string.IsNullOrWhiteSpace(entry.Md5) && !string.Equals(Md5(temporary), entry.Md5, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("云端下载完整性校验失败，未应用到本机。");
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            _networkDownloads++; _networkDownloadBytes += entry.Size;
        }

        public void Complete(CloudSyncResult result, Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            result.NetworkDownloaded = _networkDownloads; result.NetworkDownloadedBytes = _networkDownloadBytes;
            foreach (var blocked in _journal.Blocked)
            {
                result.Conflicts++;
                result.Operations.Add(new CloudSyncOperation { LogicalPath = blocked, Kind = CloudSyncOperationKind.Conflict,
                    Message = "云端有并发版本，已保留全部分支；请在同步中心选择版本。" });
            }
            var commit = _journal.CreateCommit(CloudSyncCatalog.CreateDefault(_settings), cancellationToken);
            if (commit == null) return;
            var name = Path.GetFileName(commit);
            progress?.Invoke(new CloudSyncProgress { Stage = "正在发布不可变版本包", LogicalPath = name });
            // Content-addressed destination: simultaneous identical retries write identical bytes;
            // different commits have different paths. No existing user/cloud file is overwritten.
            _client.UploadAsync(_credential.AccessToken, commit, RemoteRoot + "/" + ImmutableCloudJournal.RemoteDirectory + "/" + name,
                (done, total) => progress?.Invoke(new CloudSyncProgress { Stage = "正在发布不可变版本包", Direction = "上传", BytesCompleted = done, BytesTotal = total }),
                cancellationToken).GetAwaiter().GetResult();
            result.NetworkUploaded++; result.NetworkUploadedBytes += new FileInfo(commit).Length;
            File.Copy(commit, Path.Combine(_journal.Archives, name), true);
        }

        private CloudSyncCredential EnsureCredential(CancellationToken cancellationToken)
        {
            var value = _credential ?? _credentials.Load(Id);
            if (value == null) throw new InvalidOperationException("百度网盘尚未授权。");
            var identity = EnsureAccountIdentity(value);
            DateTime expires;
            if (string.IsNullOrWhiteSpace(value.AccessToken) || !DateTime.TryParse(value.ExpiresAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out expires) || expires <= DateTime.UtcNow)
            {
                if (string.Equals(value.AuthMode, "Broker", StringComparison.OrdinalIgnoreCase))
                {
                    using (var broker = new BaiduBrokerAuthClient()) value = broker.RefreshAsync(_settings.ProviderBrokerUrl, value.RefreshToken, cancellationToken).GetAwaiter().GetResult();
                }
                else value = _client.RefreshAsync(_settings.ProviderClientId, value, cancellationToken).GetAwaiter().GetResult();
                value.AccountIdentity = identity;
                _credentials.Save(Id, value);
            }
            else if (string.IsNullOrWhiteSpace(value.AccountIdentity)) { value.AccountIdentity = identity; _credentials.Save(Id, value); }
            return value;
        }

        private static string Revision(BaiduRemoteEntry entry)
        {
            return entry.FileSystemId + "|" + entry.Size + "|" + entry.ModifiedAtUnix + "|" + entry.Md5;
        }

        private static string EnsureAccountIdentity(CloudSyncCredential credential)
        {
            if (!string.IsNullOrWhiteSpace(credential.AccountIdentity)) return credential.AccountIdentity;
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(credential.RefreshToken ?? string.Empty));
                return string.Concat(bytes.Take(12).Select(value => value.ToString("x2")));
            }
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
            var legacySystemPrefixes = new[]
            {
                "万落建筑云同步/通用配置/", "万落建筑云同步/项目配置/",
                "万落建筑云同步/图框模板/", "万落建筑云同步/方案库/",
                "万落建筑云同步/历史版本/通用配置/", "万落建筑云同步/历史版本/项目配置/",
                "万落建筑云同步/历史版本/图框模板/", "万落建筑云同步/历史版本/方案库/"
            };
            if (legacySystemPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
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
        internal static bool CachedFileMatches(BaiduRemoteEntry entry, string path)
        {
            if (entry == null || !File.Exists(path) || FileLength(path) != entry.Size) return false;
            if (!string.IsNullOrWhiteSpace(entry.Md5)) return string.Equals(Md5(path), entry.Md5, StringComparison.OrdinalIgnoreCase);
            if (entry.ModifiedAtUnix <= 0) return false;
            var localSeconds = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
            return Math.Abs(localSeconds - entry.ModifiedAtUnix) <= 2;
        }
        private static string Md5(string path) { using (var stream = File.OpenRead(path)) using (var md5 = MD5.Create()) { var builder = new StringBuilder(); foreach (var b in md5.ComputeHash(stream)) builder.Append(b.ToString("x2")); return builder.ToString(); } }
        private static int ToProgress(long done, long total) { return total <= 0 ? 0 : Math.Max(0, Math.Min(1000, (int)(done * 1000L / total))); }
        private static bool IsUnavailableRemoteFile(IOException exception)
        {
            var message = exception == null ? string.Empty : exception.Message;
            return message.IndexOf("文件元信息没有返回下载地址", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("接口错误 42214", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("pcs meta error", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private static void TryDeleteCachedFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        public void Dispose() { _client.Dispose(); }
    }

    public static class CloudSyncWorkflow
    {
        internal static string ScopedCache(string identity)
        {
            using (var sha = SHA256.Create())
                return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "provider-cache", "v2-" +
                    BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24));
        }
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
            return Synchronize(settings, store, progress, cancellationToken, false);
        }

        public static CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncSettingsStore store,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken, bool forceSystemPackage)
        {
            using (var mutex = new Mutex(false, "WanluoArchitectureTools.CloudSync"))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("另一份 AutoCAD 正在同步，请稍后重试。");
                    WriteAudit("开始核对（手动与自动共用流程）。");
                    return SynchronizeLocked(settings, store, progress, cancellationToken, forceSystemPackage);
                }
                catch (Exception exception) { WriteAudit("未完成：" + exception.Message); throw; }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }


        private static CloudSyncResult SynchronizeLocked(CloudSyncSettings settings, CloudSyncSettingsStore store,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken, bool forceSystemPackage)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var actualStore = store ?? new CloudSyncSettingsStore();
            var projects = new PublishPlanStore().LoadProjects();
            var persisted = actualStore.LoadSettings();
            if (persisted != null && persisted.ProjectMappings != null)
            {
                settings.ProjectMappings = persisted.ProjectMappings;
                settings.SyncProjectFiles = persisted.SyncProjectFiles;
            }
            CloudProjectWorkspaceService.ValidateForProjectSync(settings, projects);
            using (var provider = CloudSyncProviderFactory.Create(settings))
            {
                var catalog = CloudSyncCatalog.CreateDefault(settings);
                var transactions = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "transactions");
                var allowedRoots = catalog.Roots.Concat(new[] { provider.WorkingFolder,
                    Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending") })
                    .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList();
                CloudSyncTransaction.Recover(transactions, p => string.Equals(Path.GetFullPath(p), actualStore.StatePath, StringComparison.OrdinalIgnoreCase) ||
                    allowedRoots.Any(root => Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase)));
                provider.Prepare(progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var versioned = provider as IVersionedSyncProvider;
                if (versioned != null) catalog.Exclude(versioned.BlockedPaths);
                using (var transaction = new CloudSyncTransaction(transactions))
                {
                    var firstConnection = EnsureProviderState(actualStore, provider.Id, provider.StateIdentity);
                    var result = new LocalFolderSyncEngine(actualStore)
                        .Synchronize(settings, catalog, provider.WorkingFolder, progress, cancellationToken, firstConnection);
                    if (result.Errors > 0)
                        throw new IOException("本轮有文件处理失败，未发布云端版本，已启动恢复：" +
                            string.Join("；", result.Operations.Where(o => o.Kind == CloudSyncOperationKind.Error).Select(o => o.Message)));
                    cancellationToken.ThrowIfCancellationRequested();
                    provider.Complete(result, progress, cancellationToken);
                    transaction.Commit();
                    progress?.Invoke(new CloudSyncProgress { Stage = "同步完成", Completed = 1, Total = 1 });
                    WriteAudit("完成：" + result.Summary);
                    foreach (var operation in result.Operations) WriteAudit(operation.Kind + " " + operation.LogicalPath + " " + operation.Message);
                    return result;
                }
            }
        }

        private static void WriteAudit(string message)
        {
            try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "cloud-sync-workflow.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); }
            catch { }
        }

        private static bool EnsureProviderState(CloudSyncSettingsStore store, string providerId, string providerScope)
        {
            var state = store.LoadState();
            if (string.Equals(state.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(state.ProviderScope) && string.Equals(state.ProviderScope, providerScope, StringComparison.Ordinal)) return false;
            state.Files.Clear();
            state.SchemaVersion = 2;
            state.ProviderId = providerId;
            state.ProviderScope = providerScope;
            store.SaveState(state);
            return true;
        }
    }
}
