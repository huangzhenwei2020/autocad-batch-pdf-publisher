using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudSyncCenterSnapshot
    {
        public CloudSyncCenterSnapshot()
        {
            Pending = new List<CloudSyncCenterItem>();
            Conflicts = new List<CloudSyncConflictItem>();
            History = new List<CloudSyncCenterItem>();
        }
        public IList<CloudSyncCenterItem> Pending { get; private set; }
        public IList<CloudSyncConflictItem> Conflicts { get; private set; }
        public IList<CloudSyncCenterItem> History { get; private set; }
    }

    public sealed class CloudSyncCenterItem
    {
        public string LogicalPath { get; set; }
        public string FilePath { get; set; }
        public string Kind { get; set; }
        public DateTime ModifiedAt { get; set; }
    }

    public sealed class CloudSyncConflictItem
    {
        public string LogicalPath { get; set; }
        public string LocalCopyPath { get; set; }
        public string RemoteCopyPath { get; set; }
        public DateTime ModifiedAt { get; set; }
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
            _mirrorRoot = string.IsNullOrWhiteSpace(_settings.SyncFolder) ? null :
                Path.GetFullPath(Path.Combine(_settings.SyncFolder, "万落建筑云同步"));
            _pendingRoot = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending");
            _localHistoryRoot = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "history");
        }

        public CloudSyncCenterSnapshot Load()
        {
            var snapshot = new CloudSyncCenterSnapshot();
            LoadPending(snapshot.Pending);
            LoadConflicts(snapshot.Conflicts);
            LoadHistory(snapshot.History, _localHistoryRoot, "本机历史");
            if (!string.IsNullOrWhiteSpace(_mirrorRoot)) LoadHistory(snapshot.History, Path.Combine(_mirrorRoot, "历史版本"), "共享历史");
            var recent = snapshot.History.OrderByDescending(item => item.ModifiedAt).Take(1000).ToList();
            snapshot.History.Clear();
            foreach (var item in recent) snapshot.History.Add(item);
            return snapshot;
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
            if (item == null) throw new ArgumentNullException("item");
            var source = useLocalCopy ? item.LocalCopyPath : item.RemoteCopyPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new IOException(useLocalCopy ? "本机冲突副本不存在。" : "共享冲突副本不存在。");
            string localPath;
            if (!_catalog.TryResolve(item.LogicalPath, out localPath)) throw new IOException("找不到冲突文件的本机工程映射。");
            if (CloudSyncPendingFileService.ShouldDefer(localPath)) throw new IOException("该 DWG 正在 AutoCAD 中打开，请关闭图纸后再解决冲突。");
            var remotePath = ResolveRemote(item.LogicalPath);
            BackupBeforeAction(localPath, item.LogicalPath, "冲突解决前-本机");
            BackupBeforeAction(remotePath, item.LogicalPath, "冲突解决前-共享");
            var hash = LocalFolderSyncEngine.ComputeHash(source);
            CopyAtomically(source, localPath, hash);
            CopyAtomically(source, remotePath, hash);
            MarkResolved(item.LocalCopyPath); MarkResolved(item.RemoteCopyPath);
            CloudSyncCoordinator.RequestSynchronization(false);
        }

        public void RestoreHistory(CloudSyncCenterItem item)
        {
            if (item == null || !File.Exists(item.FilePath)) throw new IOException("历史版本不存在。");
            string target;
            if (!_catalog.TryResolve(item.LogicalPath, out target)) throw new IOException("找不到历史版本的本机工程映射。");
            if (CloudSyncPendingFileService.ShouldDefer(target)) throw new IOException("该 DWG 正在 AutoCAD 中打开，请关闭图纸后再恢复。");
            BackupBeforeAction(target, item.LogicalPath, "历史恢复前");
            CopyAtomically(item.FilePath, target, LocalFolderSyncEngine.ComputeHash(item.FilePath));
            CloudSyncCoordinator.RequestSynchronization(false);
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
                target.Add(new CloudSyncCenterItem
                {
                    LogicalPath = CloudSyncSource.NormalizeLogicalPath(relative.Substring(0, relative.Length - suffix.Length)),
                    FilePath = path,
                    Kind = deletion ? "等待删除" : "等待替换",
                    ModifiedAt = File.GetLastWriteTime(path)
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
                    ModifiedAt = File.GetLastWriteTime(sample)
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
                if (!_catalog.TryResolve(logical, out ignored)) continue;
                target.Add(new CloudSyncCenterItem { LogicalPath = logical, FilePath = path, Kind = kind, ModifiedAt = File.GetLastWriteTime(path) });
            }
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
            if (!File.Exists(path)) return;
            var directory = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "center-backups", category,
                logicalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            File.Copy(path, Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(path)), false);
        }

        private static void CopyAtomically(string source, string target, string expectedHash)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (!string.Equals(LocalFolderSyncEngine.ComputeHash(temporary), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("文件复制后哈希校验失败。");
                if (File.Exists(target)) File.Copy(temporary, target, true);
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
    }
}
