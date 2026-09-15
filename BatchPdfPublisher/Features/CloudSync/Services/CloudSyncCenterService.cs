using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudSyncCenterSnapshot
    {
        public CloudSyncCenterSnapshot()
        {
            Pending = new List<CloudSyncCenterItem>();
            Conflicts = new List<CloudSyncConflictItem>();
            History = new List<CloudSyncCenterItem>();
            WorkFiles = new List<CloudSyncCenterItem>();
        }
        public IList<CloudSyncCenterItem> Pending { get; private set; }
        public IList<CloudSyncConflictItem> Conflicts { get; private set; }
        public IList<CloudSyncCenterItem> History { get; private set; }
        public IList<CloudSyncCenterItem> WorkFiles { get; private set; }
    }

    public sealed class CloudSyncCenterItem
    {
        public string LogicalPath { get; set; }
        public string FilePath { get; set; }
        public string Kind { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string Category { get; set; }
        public string Purpose { get; set; }
        public string DisplayPath { get; set; }
        public bool LocalExists { get; set; }
        public bool CloudExists { get; set; }
        public long Size { get; set; }
    }

    public sealed class CloudSyncConflictItem
    {
        public string LogicalPath { get; set; }
        public string LocalCopyPath { get; set; }
        public string RemoteCopyPath { get; set; }
        public DateTime ModifiedAt { get; set; }
        public string Category { get; set; }
        public string Purpose { get; set; }
        public string DisplayPath { get; set; }
    }

    public sealed class CloudSyncCenterService
    {
        private readonly CloudSyncSettings _settings;
        private readonly CloudSyncCatalog _catalog;
        private readonly string _mirrorRoot;
        private readonly string _pendingRoot;
        private readonly string _localHistoryRoot;

        public CloudSyncCenterService()
        {
            _settings = new CloudSyncSettingsStore().LoadSettings();
            _catalog = CloudSyncCatalog.CreateDefault(_settings);
            using (var provider = CloudSyncProviderFactory.Create(_settings))
                _mirrorRoot = provider.IsReady ? Path.GetFullPath(Path.Combine(provider.WorkingFolder, "万落建筑云同步")) : null;
            _pendingRoot = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending");
            _localHistoryRoot = CloudBackupService.GetHistoryRoot(_settings);
        }

        public CloudSyncCenterSnapshot Load()
        {
            var snapshot = new CloudSyncCenterSnapshot();
            LoadPending(snapshot.Pending);
            LoadConflicts(snapshot.Conflicts);
            LoadWorkFiles(snapshot.WorkFiles);
            LoadAllHistory(snapshot.History);
            var recent = snapshot.History.OrderByDescending(item => item.ModifiedAt).Take(1000).ToList();
            snapshot.History.Clear();
            foreach (var item in recent) snapshot.History.Add(item);
            return snapshot;
        }

        private void LoadAllHistory(IList<CloudSyncCenterItem> target)
        {
            LoadHistory(target, _localHistoryRoot, "本机历史");
            LoadHistory(target, CloudBackupService.GetPendingHistoryRoot(_settings), "待应用前备份");
            foreach (var category in new[] { "冲突解决前-本机", "冲突解决前-共享", "历史恢复前" })
                LoadHistory(target, Path.Combine(CloudBackupService.GetManualHistoryRoot(_settings), category), category);
            LoadHistory(target, Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "history"), "旧版本机历史");
            LoadHistory(target, Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending-history"), "旧版待应用备份");
            foreach (var category in new[] { "冲突解决前-本机", "冲突解决前-共享", "历史恢复前" })
                LoadHistory(target, Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "center-backups", category), "旧版" + category);
            if (!string.IsNullOrWhiteSpace(_mirrorRoot)) LoadHistory(target, Path.Combine(_mirrorRoot, "历史版本"), "共享历史");
        }

        public IList<CloudSyncCenterItem> HistoryFor(string logicalPath)
        {
            if (string.IsNullOrWhiteSpace(logicalPath)) return new List<CloudSyncCenterItem>();
            var all = new List<CloudSyncCenterItem>();
            LoadAllHistory(all);
            return all.Where(item => string.Equals(item.LogicalPath, logicalPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ModifiedAt).ToList();
        }

        public int ApplyPending()
        {
            var applied = CloudSyncPendingFileService.ApplyAvailable(_catalog);
            if (applied > 0) CloudSyncCoordinator.RequestSynchronization(false);
            return applied;
        }

        public void DiscardPending(CloudSyncCenterItem item)
        {
            if (item == null || !IsWithin(item.FilePath, _pendingRoot)) throw new IOException("待应用文件路径无效。");
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }

        public void ResolveConflict(CloudSyncConflictItem item, bool useLocalCopy)
        {
            using (var mutex = new Mutex(false, "WanluoArchitectureTools.CloudSync"))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("同步正在进行，请完成后再处理冲突。");
                    using (var transaction = new CloudSyncTransaction(Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "transactions")))
                    { ResolveConflictLocked(item, useLocalCopy); transaction.Commit(); }
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        private void ResolveConflictLocked(CloudSyncConflictItem item, bool useLocalCopy)
        {
            if (item == null) throw new ArgumentNullException("item");
            var source = useLocalCopy ? item.LocalCopyPath : item.RemoteCopyPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new IOException(useLocalCopy ? "本机冲突副本不存在。" : "共享冲突副本不存在。");
            if (string.IsNullOrEmpty(_mirrorRoot) || !IsWithin(source, Path.Combine(_mirrorRoot, "冲突文件")))
                throw new IOException("冲突副本不在当前同步范围内。");
            string localPath;
            if (!TryResolveLocal(item.LogicalPath, out localPath)) throw new IOException("找不到冲突文件的本机映射。");
            if (CloudSyncPendingFileService.ShouldDefer(localPath)) throw new IOException("该 DWG 正在 AutoCAD 中打开，请关闭图纸后再解决冲突。");
            var remotePath = ResolveRemote(item.LogicalPath);
            BackupBeforeAction(localPath, item.LogicalPath, "冲突解决前-本机");
            BackupBeforeAction(remotePath, item.LogicalPath, "冲突解决前-共享");
            var hash = LocalFolderSyncEngine.ComputeHash(source);
            CopyAtomically(source, localPath, hash);
            CopyAtomically(source, remotePath, hash);
            ImmutableCloudJournal.RecordResolution(_mirrorRoot, item.LogicalPath, hash, source);
            MarkResolved(item.LocalCopyPath); MarkResolved(item.RemoteCopyPath);
            CloudSyncCoordinator.RequestSynchronization(false);
        }

        public string RestoreHistory(CloudSyncCenterItem item)
        {
            if (item == null || !File.Exists(item.FilePath)) throw new IOException("历史版本不存在。");
            string target;
            if (!TryResolveLocal(item.LogicalPath, out target)) throw new IOException("找不到历史版本的本机文件映射。");
            if (CloudSyncPendingFileService.ShouldDefer(target)) throw new IOException("该 DWG 正在 AutoCAD 中打开，请关闭图纸后再恢复。");
            BackupBeforeAction(target, item.LogicalPath, "历史恢复前");
            CopyAtomically(item.FilePath, target, LocalFolderSyncEngine.ComputeHash(item.FilePath));
            CloudSyncCoordinator.RequestSynchronization(false);
            return target;
        }

        public string FolderFor(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        }

        private void LoadPending(IList<CloudSyncCenterItem> target)
        {
            if (!Directory.Exists(_pendingRoot)) return;
            foreach (var path in Directory.EnumerateFiles(_pendingRoot, "*", SearchOption.AllDirectories))
            {
                var deletion = path.EndsWith(".delete-pending", StringComparison.OrdinalIgnoreCase);
                var suffix = deletion ? ".delete-pending" : ".pending";
                if (!deletion && !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = path.Substring(_pendingRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var logical = CloudSyncSource.NormalizeLogicalPath(relative.Substring(0, relative.Length - suffix.Length));
                target.Add(new CloudSyncCenterItem
                {
                    LogicalPath = logical,
                    FilePath = path,
                    Kind = deletion ? "等待删除" : "等待替换",
                    ModifiedAt = File.GetLastWriteTime(path),
                    Category = CategoryFor(logical),
                    Purpose = PurposeFor(logical),
                    DisplayPath = DisplayPathFor(logical)
                });
            }
        }

        private void LoadConflicts(IList<CloudSyncConflictItem> target)
        {
            if (string.IsNullOrWhiteSpace(_mirrorRoot)) return;
            var root = Path.Combine(_mirrorRoot, "冲突文件");
            if (!Directory.Exists(root)) return;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".local-conflict", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".remote-conflict", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var group in files.GroupBy(path => ConflictKey(root, path), StringComparer.OrdinalIgnoreCase))
            {
                var local = group.FirstOrDefault(path => path.EndsWith(".local-conflict", StringComparison.OrdinalIgnoreCase));
                var remote = group.FirstOrDefault(path => path.EndsWith(".remote-conflict", StringComparison.OrdinalIgnoreCase));
                var sample = local ?? remote;
                var relative = group.Key;
                var slash = relative.IndexOf('/');
                var logical = slash >= 0 ? relative.Substring(slash + 1) : relative;
                target.Add(new CloudSyncConflictItem
                {
                    LogicalPath = logical,
                    LocalCopyPath = local,
                    RemoteCopyPath = remote,
                    ModifiedAt = File.GetLastWriteTime(sample),
                    Category = CategoryFor(logical),
                    Purpose = PurposeFor(logical),
                    DisplayPath = DisplayPathFor(logical)
                });
            }
        }

        private void LoadHistory(IList<CloudSyncCenterItem> target, string root, string kind)
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var logical = Path.GetDirectoryName(relative);
                if (string.IsNullOrWhiteSpace(logical)) continue;
                logical = CloudSyncSource.NormalizeLogicalPath(logical);
                string ignored;
                if (!TryResolveLocal(logical, out ignored)) continue;
                target.Add(new CloudSyncCenterItem
                {
                    LogicalPath = logical,
                    FilePath = path,
                    Kind = kind,
                    ModifiedAt = File.GetLastWriteTime(path),
                    Category = CategoryFor(logical),
                    Purpose = PurposeFor(logical),
                    DisplayPath = DisplayPathFor(logical)
                });
            }
        }

        private void LoadWorkFiles(IList<CloudSyncCenterItem> target)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _catalog.EnumerateFiles())
                if (file.LogicalPath.StartsWith("项目文件/", StringComparison.OrdinalIgnoreCase)) paths.Add(file.LogicalPath);
            foreach (var logical in CloudSyncRemoteInventoryStore.ProjectFilePaths())
            {
                string ignored;
                if (_catalog.TryResolve(logical, out ignored)) paths.Add(logical);
            }
            if (!string.IsNullOrWhiteSpace(_mirrorRoot))
            {
                var projectRoot = Path.Combine(_mirrorRoot, "项目文件");
                if (Directory.Exists(projectRoot))
                    foreach (var file in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories))
                    {
                        var relative = file.Substring(_mirrorRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string ignored;
                        var logical = CloudSyncSource.NormalizeLogicalPath(relative);
                        if (_catalog.TryResolve(logical, out ignored)) paths.Add(logical);
                    }
            }
            var remotePaths = new HashSet<string>(CloudSyncRemoteInventoryStore.ProjectFilePaths(), StringComparer.OrdinalIgnoreCase);
            foreach (var logical in paths.OrderBy(DisplayPathFor, StringComparer.CurrentCultureIgnoreCase))
            {
                string localPath;
                if (!TryResolveLocal(logical, out localPath)) continue;
                var remotePath = string.IsNullOrWhiteSpace(_mirrorRoot) ? null : ResolveRemote(logical);
                var localExists = File.Exists(localPath);
                var cachedRemoteExists = !string.IsNullOrWhiteSpace(remotePath) && File.Exists(remotePath);
                var cloudExists = remotePaths.Contains(logical) || cachedRemoteExists;
                var source = localExists ? localPath : cachedRemoteExists ? remotePath : null;
                target.Add(new CloudSyncCenterItem
                {
                    LogicalPath = logical,
                    FilePath = source,
                    Kind = Path.GetExtension(logical).TrimStart('.').ToUpperInvariant(),
                    ModifiedAt = source == null ? DateTime.MinValue : File.GetLastWriteTime(source),
                    Category = "项目文件",
                    Purpose = PurposeFor(logical),
                    DisplayPath = DisplayPathFor(logical),
                    LocalExists = localExists,
                    CloudExists = cloudExists,
                    Size = source == null ? 0 : SafeLength(source)
                });
            }
        }

        public static string CategoryFor(string logicalPath)
        {
            var path = CloudSyncSource.NormalizeLogicalPath(logicalPath);
            if (path.StartsWith("项目文件/", StringComparison.OrdinalIgnoreCase)) return "项目文件";
            if (path.StartsWith("项目配置/", StringComparison.OrdinalIgnoreCase)) return "项目资料";
            if (path.StartsWith("通用配置/", StringComparison.OrdinalIgnoreCase)) return "软件设置";
            if (path.StartsWith("图框模板/", StringComparison.OrdinalIgnoreCase)) return "图框模板";
            if (path.StartsWith("方案库/", StringComparison.OrdinalIgnoreCase)) return "方案库";
            if (path.StartsWith(CloudSystemPackageService.LogicalPrefix + "/", StringComparison.OrdinalIgnoreCase)) return "系统文件";
            return "其他";
        }

        public static string PurposeFor(string logicalPath)
        {
            switch (CategoryFor(logicalPath))
            {
                case "项目文件": return "项目中的图纸及配套资料；仅同步已选择的项目";
                case "项目资料": return "用于识别项目、图框登记和发布参数，不等于整套 DWG";
                case "软件设置": return "文字、标注、界面及常用制图设置";
                case "图框模板": return "登记过的图框和排版范围";
                case "方案库": return "楼梯大样等可复用参数方案";
                case "系统文件": return "设置、项目登记、图框和方案库的合并压缩包";
                default: return "云同步产生的其他数据";
            }
        }

        private string DisplayPathFor(string logicalPath)
        {
            var path = CloudSyncSource.NormalizeLogicalPath(logicalPath);
            if (path.StartsWith("项目文件/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                if (parts.Length >= 2)
                {
                    var mapping = (_settings.ProjectMappings ?? new List<CloudSyncProjectMapping>()).FirstOrDefault(item =>
                        item != null && string.Equals(item.CloudId, parts[1], StringComparison.OrdinalIgnoreCase));
                    var project = mapping == null ? parts[1] : mapping.ProjectName;
                    return project + (parts.Length > 2 ? " / " + string.Join(" / ", parts.Skip(2)) : string.Empty);
                }
            }
            var slash = path.IndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar) : path;
        }

        private string ResolveRemote(string logicalPath)
        {
            if (string.IsNullOrWhiteSpace(_mirrorRoot)) throw new IOException("尚未设置共享同步目录。");
            var relative = CloudSyncSource.NormalizeLogicalPath(logicalPath).Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(_mirrorRoot, relative));
            if (!IsWithin(target, _mirrorRoot)) throw new IOException("同步相对路径越过了共享目录。");
            return target;
        }

        private void BackupBeforeAction(string path, string logicalPath, string category)
        {
            CloudBackupService.BackupFile(path, logicalPath, Path.Combine("手动操作前备份", category), _settings);
        }

        private bool TryResolveLocal(string logicalPath, out string localPath)
        {
            return TryResolveProjectFileInWorkspace(logicalPath, out localPath) ||
                   _catalog.TryResolve(logicalPath, out localPath) ||
                   CloudSystemPackageService.TryResolveSystemFile(_settings, logicalPath, out localPath);
        }

        private bool TryResolveProjectFileInWorkspace(string logicalPath, out string localPath)
        {
            localPath = null;
            var normalized = CloudSyncSource.NormalizeLogicalPath(logicalPath);
            if (!normalized.StartsWith("项目文件/", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = normalized.Split('/');
            if (parts.Length < 3) return false;
            var mapping = (_settings.ProjectMappings ?? new List<CloudSyncProjectMapping>()).FirstOrDefault(item =>
                item != null && item.Enabled && string.Equals(item.CloudId, parts[1], StringComparison.OrdinalIgnoreCase));
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.ProjectName)) return false;
            var workspace = CloudProjectWorkspaceService.GetWorkspaceRoot(_settings);
            var projectRoot = CloudProjectWorkspaceService.IsUnderWorkspace(mapping.LocalFolder, workspace)
                ? Path.GetFullPath(mapping.LocalFolder) : CloudProjectWorkspaceService.ProjectFolderFor(_settings, mapping.ProjectName);
            var rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(2));
            var candidate = Path.GetFullPath(Path.Combine(rootPrefix, relative));
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            localPath = candidate;
            return true;
        }

        private static void CopyAtomically(string source, string target, string expectedHash)
        {
            var expectedBefore = CloudSyncTransaction.Hash(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (!string.Equals(LocalFolderSyncEngine.ComputeHash(temporary), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("文件复制后哈希校验失败。");
                CloudSyncTransaction.BeforeReplace(target, expectedBefore, expectedHash);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static string ConflictKey(string root, string path)
        {
            var relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative.EndsWith(".local-conflict", StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(0, relative.Length - ".local-conflict".Length);
            if (relative.EndsWith(".remote-conflict", StringComparison.OrdinalIgnoreCase)) relative = relative.Substring(0, relative.Length - ".remote-conflict".Length);
            return CloudSyncSource.NormalizeLogicalPath(relative);
        }

        private static void MarkResolved(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            File.Move(path, path + ".resolved-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        }

        private static bool IsWithin(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static long SafeLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }
    }
}
