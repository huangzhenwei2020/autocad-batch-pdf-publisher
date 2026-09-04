using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    public enum CloudProjectSyncDirection
    {
        None,
        Checking,
        Synchronized,
        Download,
        Upload,
        Bidirectional,
        Conflict
    }

    public sealed class CloudProjectSyncStatus
    {
        public CloudProjectSyncDirection Direction { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Produces a truthful project-level summary from the last completed cloud inventory.
    /// The actual transfer engine still performs the authoritative hash comparison.
    /// </summary>
    public static class CloudProjectSyncStatusService
    {
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

        public static CloudProjectSyncStatus Evaluate(string cloudId, string localFolder, string providerWorkingFolder,
            bool selected, bool inventoryAvailable, IEnumerable<string> remoteProjectPaths, CloudSyncState state)
        {
            if (!selected) return Status(CloudProjectSyncDirection.None, "未选中，不传输");
            if (!inventoryAvailable) return Status(CloudProjectSyncDirection.Checking, "等待核对云端");
            if (string.IsNullOrWhiteSpace(cloudId) || string.IsNullOrWhiteSpace(localFolder) || string.IsNullOrWhiteSpace(providerWorkingFolder))
                return Status(CloudProjectSyncDirection.Checking, "等待核对云端");

            var prefix = "项目文件/" + cloudId.Trim('/') + "/";
            var remote = new HashSet<string>((remoteProjectPaths ?? Enumerable.Empty<string>())
                .Select(CloudSyncSource.NormalizeLogicalPath)
                .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var local = EnumerateLocal(prefix, localFolder);
            var remoteCacheRoot = Path.Combine(providerWorkingFolder, "万落建筑云同步", "项目文件", cloudId);
            var cached = EnumerateLocal(prefix, remoteCacheRoot);
            var synchronized = (state == null || state.Files == null ? Enumerable.Empty<CloudSyncFileState>() : state.Files)
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.LogicalPath))
                .GroupBy(item => CloudSyncSource.NormalizeLogicalPath(item.LogicalPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var upload = false;
            var download = false;
            var uncertain = false;
            foreach (var logical in new HashSet<string>(local.Keys.Concat(remote), StringComparer.OrdinalIgnoreCase))
            {
                string localPath;
                var localExists = local.TryGetValue(logical, out localPath);
                var remoteExists = remote.Contains(logical);
                if (localExists && !remoteExists) { upload = true; continue; }
                if (!localExists && remoteExists) { download = true; continue; }
                if (!localExists) continue;

                string cachePath;
                if (!cached.TryGetValue(logical, out cachePath) || !File.Exists(cachePath))
                {
                    uncertain = true;
                    continue;
                }

                var localInfo = new FileInfo(localPath);
                var remoteInfo = new FileInfo(cachePath);
                var delta = localInfo.LastWriteTimeUtc - remoteInfo.LastWriteTimeUtc;
                if (delta > TimestampTolerance) upload = true;
                else if (delta < -TimestampTolerance) download = true;
                else if (localInfo.Length != remoteInfo.Length) uncertain = true;
                else
                {
                    CloudSyncFileState fileState;
                    if (!synchronized.TryGetValue(logical, out fileState) || !IsSynchronized(fileState))
                        uncertain = true; // Equal metadata is not proof of equal content; the engine will hash it.
                }
            }

            if (upload && download) return Status(CloudProjectSyncDirection.Bidirectional, "双向有更新，按各文件最新同步");
            if (download && !upload) return Status(CloudProjectSyncDirection.Download, "云端较新，等待下载");
            if (upload && !download) return Status(CloudProjectSyncDirection.Upload, "本机较新，等待上传");
            if (uncertain) return Status(CloudProjectSyncDirection.Checking, "时间接近，等待内容核对");
            if (local.Count == 0 && remote.Count == 0) return Status(CloudProjectSyncDirection.Synchronized, "没有可同步文件");
            return Status(CloudProjectSyncDirection.Synchronized, "已是最新");
        }

        private static Dictionary<string, string> EnumerateLocal(string prefix, string root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;
                var source = new CloudSyncSource(prefix.TrimEnd('/'), root, CloudSyncCatalog.IncludeNormalFile);
                foreach (var file in source.EnumerateFiles()) result[file.LogicalPath] = file.LocalPath;
            }
            catch { }
            return result;
        }

        private static bool IsSynchronized(CloudSyncFileState state)
        {
            return state != null && !string.IsNullOrWhiteSpace(state.BaseHash) &&
                   string.Equals(state.BaseHash, state.LocalHash, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(state.BaseHash, state.RemoteHash, StringComparison.OrdinalIgnoreCase);
        }

        private static CloudProjectSyncStatus Status(CloudProjectSyncDirection direction, string text)
        {
            return new CloudProjectSyncStatus { Direction = direction, Text = text };
        }
    }
}
