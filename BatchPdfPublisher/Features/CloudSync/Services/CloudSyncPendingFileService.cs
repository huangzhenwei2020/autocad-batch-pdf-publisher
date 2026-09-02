using System;
using System.IO;
using System.Linq;
using System.Threading;

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
        private static string HistoryRoot { get { return CloudBackupService.GetPendingHistoryRoot(); } }

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
            StageDownload(logicalPath, source, expectedHash, CancellationToken.None);
        }

        public static void StageDownload(string logicalPath, string source, string expectedHash, CancellationToken cancellationToken)
        {
            var target = PendingPath(logicalPath, ".pending");
            CopyAtomically(source, target, expectedHash, cancellationToken);
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
            return ApplyAvailable(catalog, CancellationToken.None);
        }

        public static int ApplyAvailable(CloudSyncCatalog catalog, CancellationToken cancellationToken)
        {
            if (catalog == null || !Directory.Exists(PendingRoot)) return 0;
            var applied = 0;
            foreach (var pending in Directory.EnumerateFiles(PendingRoot, "*", SearchOption.AllDirectories).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    Backup(target, logicalPath, cancellationToken);
                    if (delete) TryDelete(target);
                    else CopyAtomically(pending, target, LocalFolderSyncEngine.ComputeHash(pending), cancellationToken);
                    File.Delete(pending);
                    applied++;
                }
                catch (OperationCanceledException) { throw; }
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

        private static void CopyAtomically(string source, string target, string expectedHash, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                CopyFile(source, temporary, false, cancellationToken);
                if (!string.Equals(LocalFolderSyncEngine.ComputeHash(temporary, cancellationToken), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("待应用文件哈希校验失败。");
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(target))
                {
                    try { File.Replace(temporary, target, null, true); }
                    catch (PlatformNotSupportedException) { ReplaceWithRollback(temporary, target, cancellationToken); }
                    catch (IOException) { ReplaceWithRollback(temporary, target, cancellationToken); }
                }
                else File.Move(temporary, target);
            }
            finally { TryDelete(temporary); }
        }

        private static void Backup(string target, string logicalPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(target)) return;
            var relative = logicalPath.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.Combine(HistoryRoot, Path.GetDirectoryName(relative) ?? string.Empty, Path.GetFileName(relative));
            Directory.CreateDirectory(directory);
            CopyFile(target, Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + Path.GetExtension(target)), false,
                cancellationToken);
        }

        private static void CopyFile(string source, string target, bool overwrite, CancellationToken cancellationToken)
        {
            if (!overwrite && File.Exists(target)) throw new IOException("目标文件已存在：" + target);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
                output.Flush(true);
            }
        }

        private static void ReplaceWithRollback(string temporary, string target, CancellationToken cancellationToken)
        {
            var rollback = target + "." + Guid.NewGuid().ToString("N") + ".rollback";
            CopyFile(target, rollback, false, cancellationToken);
            try { CopyFile(temporary, target, true, cancellationToken); }
            catch { CopyFile(rollback, target, true, CancellationToken.None); throw; }
            finally { TryDelete(rollback); }
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
