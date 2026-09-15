using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public static class CloudBackupService
    {
        public static string GetBackupRoot(CloudSyncSettings settings = null)
        {
            if (settings == null) try { settings = new CloudSyncSettingsStore().LoadSettings(); } catch { }
            var configured = settings == null ? null : settings.BackupRoot;
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑备份");
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim());
        }

        public static string GetHistoryRoot(CloudSyncSettings settings = null)
        {
            return Path.Combine(GetBackupRoot(settings), "历史版本");
        }

        public static string GetPendingHistoryRoot(CloudSyncSettings settings = null)
        {
            return Path.Combine(GetBackupRoot(settings), "待应用前备份");
        }

        public static string GetManualHistoryRoot(CloudSyncSettings settings = null)
        {
            return Path.Combine(GetBackupRoot(settings), "手动操作前备份");
        }

        public static void ValidateLocation(CloudSyncSettings settings, IEnumerable<string> synchronizedRoots, string mirrorRoot)
        {
            var backup = NormalizeDirectory(GetBackupRoot(settings));
            foreach (var root in synchronizedRoots ?? Enumerable.Empty<string>())
                if (Overlaps(backup, NormalizeDirectory(root)))
                    throw new InvalidOperationException("备份保存位置不能放在同步项目或软件数据目录内部，也不能包含这些目录。");
            if (!string.IsNullOrWhiteSpace(mirrorRoot) && Overlaps(backup, NormalizeDirectory(mirrorRoot)))
                throw new InvalidOperationException("备份保存位置不能放在云盘镜像目录内部，也不能包含云盘镜像目录。");
        }

        public static string CreateFirstConnectionSnapshot(CloudSyncSettings settings,
            IDictionary<string, string> localFiles, Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            var files = (localFiles ?? new Dictionary<string, string>()).Where(pair => File.Exists(pair.Value)).ToList();
            if (files.Count == 0) return null;
            var root = Path.Combine(GetBackupRoot(settings), "首次连接备份",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + SafeName(settings == null ? null : settings.DeviceName)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var dataRoot = Path.Combine(root, "文件");
            var required = files.Sum(pair => SafeLength(pair.Value));
            EnsureFreeSpace(root, required);
            Directory.CreateDirectory(dataRoot);
            var manifest = new StringBuilder();
            manifest.AppendLine("万落建筑工具首次连接备份");
            manifest.AppendLine("创建时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            manifest.AppendLine("设备名称：" + (settings == null ? Environment.MachineName : settings.DeviceName));
            manifest.AppendLine("文件数量：" + files.Count);
            manifest.AppendLine("总大小：" + FormatBytes(required));
            manifest.AppendLine();
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pair = files[index];
                progress?.Invoke(new CloudSyncProgress { Stage = "正在备份首次连接前的本机文件", LogicalPath = pair.Key, Completed = index, Total = files.Count });
                var target = SafeSnapshotPath(dataRoot, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(pair.Value, target, false);
                if (SafeLength(pair.Value) != SafeLength(target)) throw new IOException("首次连接备份校验失败：" + pair.Value);
                manifest.AppendLine(pair.Key + " <- " + pair.Value);
            }
            File.WriteAllText(Path.Combine(root, "备份说明.txt"), manifest.ToString(), Encoding.UTF8);
            return root;
        }

        public static void BackupFile(string source, string logicalPath, string category, CloudSyncSettings settings = null)
        {
            if (!File.Exists(source)) return;
            var directory = Path.Combine(GetBackupRoot(settings), category,
                CloudSyncSource.NormalizeLogicalPath(logicalPath).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);
            File.Copy(source, Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(source)), false);
        }

        private static string SafeSnapshotPath(string root, string logicalPath)
        {
            var relative = CloudSyncSource.NormalizeLogicalPath(logicalPath).Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份文件路径越出备份目录。");
            return target;
        }

        private static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool Overlaps(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureFreeSpace(string path, long required)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            var drive = new DriveInfo(root);
            var reserve = Math.Max(100L * 1024 * 1024, required / 20);
            if (drive.AvailableFreeSpace < required + reserve)
                throw new IOException("备份磁盘空间不足，需要约 " + FormatBytes(required + reserve) + "，当前可用 " + FormatBytes(drive.AvailableFreeSpace) + "。");
        }

        private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
        private static string SafeName(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return result;
        }
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024d * 1024)).ToString("0.##") + " MB";
            return (bytes / 1024d).ToString("0.##") + " KB";
        }
    }
}
