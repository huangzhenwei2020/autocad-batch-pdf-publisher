using System;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// Keeps remote DWG changes away from drawings that are currently open in
    /// AutoCAD. Pending files live outside the shared folder and are applied on
    /// the next synchronization after the drawing closes.
    /// </summary>
    public static class CloudSyncPendingFileService
    {
        private static readonly object Sync = new object();
        private static Func<string, bool> _openPathProbe;
        private static string PendingRoot { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending"); } }
        private static string HistoryRoot { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending-history"); } }

        public static void RegisterOpenPathProbe(Func<string, bool> probe)
        {
            lock (Sync) _openPathProbe = probe;
        }

        public static void ClearOpenPathProbe()
        {
            lock (Sync) _openPathProbe = null;
        }

        public static bool ShouldDefer(string path)
        {
            if (!IsDrawing(path)) return false;
            Func<string, bool> probe;
            lock (Sync) probe = _openPathProbe;
            try { if (probe != null && probe(path)) return true; } catch { }
            if (!File.Exists(path)) return false;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static void StageDownload(string logicalPath, string source, string expectedHash)
        {
            var target = PendingPath(logicalPath, ".pending");
            CopyAtomically(source, target, expectedHash);
            TryDelete(PendingPath(logicalPath, ".delete-pending"));
        }

        public static void StageDelete(string logicalPath)
        {
            var target = PendingPath(logicalPath, ".delete-pending");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, DateTime.UtcNow.ToString("O"));
            TryDelete(PendingPath(logicalPath, ".pending"));
        }

        public static int ApplyAvailable(CloudSyncCatalog catalog)
        {
            if (catalog == null || !Directory.Exists(PendingRoot)) return 0;
            var applied = 0;
            foreach (var pending in Directory.EnumerateFiles(PendingRoot, "*", SearchOption.AllDirectories).ToList())
            {
                var delete = pending.EndsWith(".delete-pending", StringComparison.OrdinalIgnoreCase);
                var suffix = delete ? ".delete-pending" : ".pending";
                if (!delete && !pending.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = pending.Substring(PendingRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var logicalPath = CloudSyncSource.NormalizeLogicalPath(relative.Substring(0, relative.Length - suffix.Length));
                string target;
                if (!catalog.TryResolve(logicalPath, out target) || ShouldDefer(target)) continue;
                try
                {
                    Backup(target, logicalPath);
                    if (delete) TryDelete(target);
                    else CopyAtomically(pending, target, LocalFolderSyncEngine.ComputeHash(pending));
                    File.Delete(pending);
                    applied++;
                }
                catch { }
            }
            return applied;
        }

        private static string PendingPath(string logicalPath, string suffix)
        {
            var relative = CloudSyncSource.NormalizeLogicalPath(logicalPath).Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(PendingRoot, relative + suffix));
            var prefix = Path.GetFullPath(PendingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("待应用文件路径无效。");
            return target;
        }

        private static void CopyAtomically(string source, string target, string expectedHash)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (!string.Equals(LocalFolderSyncEngine.ComputeHash(temporary), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("待应用文件哈希校验失败。");
                if (File.Exists(target)) File.Copy(temporary, target, true);
                else File.Move(temporary, target);
            }
            finally { TryDelete(temporary); }
        }

        private static void Backup(string target, string logicalPath)
        {
            if (!File.Exists(target)) return;
            var relative = logicalPath.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.Combine(HistoryRoot, Path.GetDirectoryName(relative) ?? string.Empty, Path.GetFileName(relative));
            Directory.CreateDirectory(directory);
            File.Copy(target, Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(target)), false);
        }

        private static bool IsDrawing(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
