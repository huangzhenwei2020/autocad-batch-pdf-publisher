using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public sealed class LocalFolderSyncEngine
    {
        private static readonly object ProcessSync = new object();
        private readonly CloudSyncSettingsStore _store;
        private readonly string _localHistoryRoot;

        public LocalFolderSyncEngine(CloudSyncSettingsStore store)
            : this(store, Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "history"))
        {
        }

        internal LocalFolderSyncEngine(CloudSyncSettingsStore store, string localHistoryRoot)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _localHistoryRoot = Path.GetFullPath(localHistoryRoot ?? throw new ArgumentNullException("localHistoryRoot"));
        }

        public CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncCatalog catalog)
        {
            return Synchronize(settings, catalog, settings == null ? null : settings.SyncFolder);
        }

        public CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncCatalog catalog, string workingFolder)
        {
            return Synchronize(settings, catalog, workingFolder, null);
        }

        public CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncCatalog catalog, string workingFolder,
            Action<CloudSyncProgress> progress)
        {
            return Synchronize(settings, catalog, workingFolder, progress, CancellationToken.None);
        }

        public CloudSyncResult Synchronize(CloudSyncSettings settings, CloudSyncCatalog catalog, string workingFolder,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (!settings.Enabled) throw new InvalidOperationException("云同步尚未启用。");
            if (string.IsNullOrWhiteSpace(workingFolder)) throw new InvalidOperationException("同步提供商没有可用的工作目录。");

            if (!Monitor.TryEnter(ProcessSync))
                throw new IOException("同步任务已经在运行，请等待当前任务完成。");
            try
            {
                using (var crossProcess = new Mutex(false, "WanluoArchitectureTools.CloudSync"))
                {
                    var acquired = false;
                    try
                    {
                        try { acquired = crossProcess.WaitOne(TimeSpan.FromSeconds(2)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new IOException("另一份 AutoCAD 正在执行同步，请稍后重试。");
                        cancellationToken.ThrowIfCancellationRequested();
                        return SynchronizeLocked(settings, catalog, workingFolder, progress, cancellationToken);
                    }
                    finally { if (acquired) crossProcess.ReleaseMutex(); }
                }
            }
            finally { Monitor.Exit(ProcessSync); }
        }

        private CloudSyncResult SynchronizeLocked(CloudSyncSettings settings, CloudSyncCatalog catalog, string workingFolder,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, "正在应用待处理文件", null, 0, 0);
            CloudSyncPendingFileService.ApplyAvailable(catalog, cancellationToken);
            var mirrorRoot = Path.GetFullPath(Path.Combine(workingFolder, "万落建筑云同步"));
            ValidateRoots(mirrorRoot, catalog.Roots);
            Directory.CreateDirectory(mirrorRoot);

            Report(progress, "正在读取同步基线", null, 0, 0);
            var state = _store.LoadState();
            var stateByPath = state.Files
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.LogicalPath))
                .GroupBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            Report(progress, "正在扫描本机文件", null, 0, 0);
            var localFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var scanned = 0;
            foreach (var file in catalog.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                localFiles[file.LogicalPath] = file.LocalPath;
                if (++scanned % 100 == 0) Report(progress, "正在扫描本机文件", file.LogicalPath, scanned, 0);
            }
            Report(progress, "正在扫描云盘同步目录", null, 0, 0);
            var remoteFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            scanned = 0;
            foreach (var file in EnumerateRemoteFiles(mirrorRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string mappedPath;
                if (!catalog.TryResolve(file.LogicalPath, out mappedPath)) continue;
                remoteFiles[file.LogicalPath] = file.LocalPath;
                if (++scanned % 100 == 0) Report(progress, "正在扫描云盘同步目录", file.LogicalPath, scanned, 0);
            }
            var paths = new HashSet<string>(localFiles.Keys, StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(remoteFiles.Keys);
            foreach (var logicalPath in stateByPath.Keys)
            {
                string mappedPath;
                if (catalog.TryResolve(logicalPath, out mappedPath)) paths.Add(logicalPath);
            }

            var result = new CloudSyncResult();
            var orderedPaths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            var progressClock = Stopwatch.StartNew();
            for (var index = 0; index < orderedPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logicalPath = orderedPaths[index];
                if (index == 0 || index == orderedPaths.Count - 1 || progressClock.ElapsedMilliseconds >= 150)
                {
                    Report(progress, "正在核对文件", logicalPath, index, orderedPaths.Count);
                    progressClock.Restart();
                }
                try
                {
                    ProcessFile(settings, catalog, mirrorRoot, logicalPath, localFiles, remoteFiles, stateByPath, result,
                        cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    result.Errors++;
                    result.Operations.Add(new CloudSyncOperation
                    {
                        LogicalPath = logicalPath,
                        Kind = CloudSyncOperationKind.Error,
                        Message = exception.Message
                    });
                }
            }

            Report(progress, "正在保存同步状态", null, orderedPaths.Count, orderedPaths.Count);
            cancellationToken.ThrowIfCancellationRequested();
            state.Files = stateByPath.Values.OrderBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase).ToList();
            _store.SaveState(state);
            Report(progress, "正在整理历史版本", null, orderedPaths.Count, orderedPaths.Count);
            CleanupHistory(_localHistoryRoot, settings, cancellationToken);
            CleanupHistory(Path.Combine(mirrorRoot, "历史版本"), settings, cancellationToken);
            Report(progress, "同步完成", null, orderedPaths.Count, orderedPaths.Count);
            return result;
        }

        private void ProcessFile(CloudSyncSettings settings, CloudSyncCatalog catalog, string mirrorRoot,
            string logicalPath, IDictionary<string, string> localFiles, IDictionary<string, string> remoteFiles,
            IDictionary<string, CloudSyncFileState> states, CloudSyncResult result, CancellationToken cancellationToken)
        {
            string localPath;
            if (!localFiles.TryGetValue(logicalPath, out localPath) && !catalog.TryResolve(logicalPath, out localPath))
                throw new InvalidOperationException("找不到该同步文件对应的本地数据源。");
            string remotePath;
            if (!remoteFiles.TryGetValue(logicalPath, out remotePath)) remotePath = ResolveRemotePath(mirrorRoot, logicalPath);

            var localHash = HashIfExists(localPath, cancellationToken);
            var remoteHash = HashIfExists(remotePath, cancellationToken);
            CloudSyncFileState fileState;
            states.TryGetValue(logicalPath, out fileState);
            var baseHash = fileState == null ? null : EmptyToNull(fileState.BaseHash);

            if (localHash == null && remoteHash == null)
            {
                states.Remove(logicalPath);
                return;
            }
            if (HashesEqual(localHash, remoteHash) && localHash != null)
            {
                SaveState(states, logicalPath, localHash, localHash, remoteHash);
                return;
            }

            if (fileState != null && HashesEqual(localHash, fileState.LocalHash) &&
                HashesEqual(remoteHash, fileState.RemoteHash))
            {
                result.Conflicts++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.Conflict,
                    "本机和共享目录的冲突仍待处理，未重复生成冲突副本。");
                return;
            }

            if (baseHash == null)
            {
                if (localHash != null && remoteHash == null)
                {
                    Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result, cancellationToken);
                    return;
                }
                if (localHash == null && remoteHash != null)
                {
                    Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result, cancellationToken);
                    return;
                }
                if (localHash != null && remoteHash != null &&
                    string.Equals(settings.InitialSyncPreference, "Remote", StringComparison.OrdinalIgnoreCase))
                {
                    Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result, cancellationToken);
                    return;
                }
                if (localHash != null && remoteHash != null &&
                    string.Equals(settings.InitialSyncPreference, "Local", StringComparison.OrdinalIgnoreCase))
                {
                    Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result, cancellationToken);
                    return;
                }
                CreateConflict(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, remoteHash, baseHash, states, result,
                    cancellationToken);
                return;
            }

            if (localHash == null && HashesEqual(remoteHash, baseHash))
            {
                Backup(remotePath, Path.Combine(mirrorRoot, "历史版本"), logicalPath, settings, cancellationToken);
                File.Delete(remotePath);
                states.Remove(logicalPath);
                result.Deleted++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.DeleteRemote, "本地删除已同步到共享目录。");
                return;
            }
            if (remoteHash == null && HashesEqual(localHash, baseHash))
            {
                if (CloudSyncPendingFileService.ShouldDefer(localPath))
                {
                    CloudSyncPendingFileService.StageDelete(logicalPath);
                    result.Pending++;
                    AddOperation(result, logicalPath, CloudSyncOperationKind.Pending, "图纸正在 AutoCAD 中打开，远端删除已暂存，关闭图纸后应用。");
                    return;
                }
                Backup(localPath, _localHistoryRoot, logicalPath, settings, cancellationToken);
                File.Delete(localPath);
                states.Remove(logicalPath);
                result.Deleted++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.DeleteLocal, "共享目录删除已应用到本机。");
                return;
            }
            if (HashesEqual(localHash, baseHash) && remoteHash != null)
            {
                Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result, cancellationToken);
                return;
            }
            if (HashesEqual(remoteHash, baseHash) && localHash != null)
            {
                Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result, cancellationToken);
                return;
            }

            CreateConflict(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, remoteHash, baseHash, states, result,
                cancellationToken);
        }

        private void Upload(CloudSyncSettings settings, string mirrorRoot, string logicalPath, string localPath,
            string remotePath, string localHash, IDictionary<string, CloudSyncFileState> states, CloudSyncResult result,
            CancellationToken cancellationToken)
        {
            if (File.Exists(remotePath)) Backup(remotePath, Path.Combine(mirrorRoot, "历史版本"), logicalPath, settings, cancellationToken);
            AtomicCopy(localPath, remotePath, localHash, cancellationToken);
            SaveState(states, logicalPath, localHash, localHash, localHash);
            result.Uploaded++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Upload, "已上传到共享目录。");
        }

        private void Download(CloudSyncSettings settings, string logicalPath, string remotePath, string localPath,
            string remoteHash, IDictionary<string, CloudSyncFileState> states, CloudSyncResult result,
            CancellationToken cancellationToken)
        {
            if (CloudSyncPendingFileService.ShouldDefer(localPath))
            {
                CloudSyncPendingFileService.StageDownload(logicalPath, remotePath, remoteHash, cancellationToken);
                result.Pending++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.Pending, "图纸正在 AutoCAD 中打开，远端版本已下载到待应用区，关闭图纸后替换。");
                return;
            }
            if (File.Exists(localPath)) Backup(localPath, _localHistoryRoot, logicalPath, settings, cancellationToken);
            AtomicCopy(remotePath, localPath, remoteHash, cancellationToken);
            SaveState(states, logicalPath, remoteHash, remoteHash, remoteHash);
            result.Downloaded++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Download, "已从共享目录更新本机文件。");
        }

        private void CreateConflict(CloudSyncSettings settings, string mirrorRoot, string logicalPath,
            string localPath, string remotePath, string localHash, string remoteHash, string baseHash,
            IDictionary<string, CloudSyncFileState> states, CloudSyncResult result, CancellationToken cancellationToken)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            var device = SafeName(settings.DeviceName);
            var conflictRoot = Path.Combine(mirrorRoot, "冲突文件", timestamp + "-" + device);
            if (File.Exists(localPath)) CopyConflict(localPath, conflictRoot, logicalPath, ".local-conflict", cancellationToken);
            if (File.Exists(remotePath)) CopyConflict(remotePath, conflictRoot, logicalPath, ".remote-conflict", cancellationToken);
            var state = GetOrCreateState(states, logicalPath);
            state.BaseHash = baseHash;
            state.LocalHash = localHash;
            state.RemoteHash = remoteHash;
            result.Conflicts++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Conflict,
                "本机和共享目录均已修改，已保留两份冲突副本，未覆盖正式文件。");
        }

        private static void CopyConflict(string source, string conflictRoot, string logicalPath, string suffix,
            CancellationToken cancellationToken)
        {
            var target = ResolveRemotePath(conflictRoot, logicalPath) + suffix;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            CopyFile(source, target, false, cancellationToken);
        }

        private static IEnumerable<CloudSyncFile> EnumerateRemoteFiles(string mirrorRoot)
        {
            if (!Directory.Exists(mirrorRoot)) yield break;
            foreach (var path in Directory.EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories))
            {
                var relative = path.Substring(mirrorRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
                if (string.Equals(top, "历史版本", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(top, "冲突文件", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(top, ".wanluo-sync", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal) ||
                    Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new CloudSyncFile(CloudSyncSource.NormalizeLogicalPath(relative), path);
            }
        }

        private static void AtomicCopy(string source, string target, string expectedHash, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("同步目标目录无效。");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                CopyFile(source, temporary, false, cancellationToken);
                var actualHash = ComputeHash(temporary, cancellationToken);
                if (!HashesEqual(actualHash, expectedHash)) throw new IOException("同步文件哈希校验失败。");
                if (File.Exists(target))
                {
                    try { File.Replace(temporary, target, null, true); }
                    catch (PlatformNotSupportedException) { ReplaceWithRollback(temporary, target, cancellationToken); }
                    catch (IOException) { ReplaceWithRollback(temporary, target, cancellationToken); }
                }
                else File.Move(temporary, target);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void ReplaceWithRollback(string temporary, string target, CancellationToken cancellationToken)
        {
            var rollback = target + "." + Guid.NewGuid().ToString("N") + ".rollback";
            CopyFile(target, rollback, false, cancellationToken);
            try { CopyFile(temporary, target, true, cancellationToken); }
            catch { CopyFile(rollback, target, true, CancellationToken.None); throw; }
            finally { try { File.Delete(rollback); } catch { } }
        }

        private static void Backup(string source, string historyRoot, string logicalPath, CloudSyncSettings settings,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(source)) return;
            var relative = logicalPath.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.Combine(historyRoot, Path.GetDirectoryName(relative) ?? string.Empty,
                Path.GetFileName(relative));
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(source);
            var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + extension;
            CopyFile(source, Path.Combine(directory, name), false, cancellationToken);
            PruneDirectory(directory, settings);
        }

        private static void CleanupHistory(string historyRoot, CloudSyncSettings settings, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(historyRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(historyRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PruneDirectory(directory, settings);
            }
        }

        private static void PruneDirectory(string directory, CloudSyncSettings settings)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.HistoryRetentionDays));
                var files = Directory.EnumerateFiles(directory).Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc).ToList();
                for (var index = 0; index < files.Count; index++)
                    if (index >= Math.Max(1, settings.KeepVersionsPerFile) || files[index].LastWriteTimeUtc < cutoff)
                        files[index].Delete();
            }
            catch { }
        }

        private static string HashIfExists(string path, CancellationToken cancellationToken)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? ComputeHash(path, cancellationToken) : null;
        }

        internal static string ComputeHash(string path)
        {
            return ComputeHash(path, CancellationToken.None);
        }

        internal static string ComputeHash(string path, CancellationToken cancellationToken)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    algorithm.TransformBlock(buffer, 0, read, null, 0);
                }
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(algorithm.Hash).Replace("-", string.Empty);
            }
        }

        private static void CopyFile(string source, string target, bool overwrite, CancellationToken cancellationToken)
        {
            if (!overwrite && File.Exists(target)) throw new IOException("目标文件已存在：" + target);
            var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, mode, FileAccess.Write, FileShare.None))
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

        private static void SaveState(IDictionary<string, CloudSyncFileState> states, string logicalPath,
            string baseHash, string localHash, string remoteHash)
        {
            var state = GetOrCreateState(states, logicalPath);
            state.BaseHash = baseHash;
            state.LocalHash = localHash;
            state.RemoteHash = remoteHash;
            state.LastSynchronizedAtUtc = DateTime.UtcNow.ToString("O");
        }

        private static CloudSyncFileState GetOrCreateState(IDictionary<string, CloudSyncFileState> states, string logicalPath)
        {
            CloudSyncFileState state;
            if (states.TryGetValue(logicalPath, out state)) return state;
            state = new CloudSyncFileState { LogicalPath = logicalPath };
            states[logicalPath] = state;
            return state;
        }

        private static string ResolveRemotePath(string root, string logicalPath)
        {
            var relative = CloudSyncSource.NormalizeLogicalPath(logicalPath).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("同步相对路径越过了同步目录。");
            return full;
        }

        private static void ValidateRoots(string mirrorRoot, IEnumerable<string> localRoots)
        {
            var mirror = Path.GetFullPath(mirrorRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var rootValue in localRoots)
            {
                var root = Path.GetFullPath(rootValue).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (mirror.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(mirror, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("同步文件夹不能位于万落工具的数据目录内部，也不能包含数据目录。");
            }
        }

        private static bool HashesEqual(string left, string right)
        {
            return left != null && right != null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string EmptyToNull(string value) { return string.IsNullOrWhiteSpace(value) ? null : value; }
        private static string SafeName(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? Environment.MachineName : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return result;
        }
        private static void AddOperation(CloudSyncResult result, string logicalPath, CloudSyncOperationKind kind, string message)
        {
            result.Operations.Add(new CloudSyncOperation { LogicalPath = logicalPath, Kind = kind, Message = message });
        }

        private static void Report(Action<CloudSyncProgress> progress, string stage, string logicalPath, int completed, int total)
        {
            if (progress == null) return;
            try { progress(new CloudSyncProgress { Stage = stage, LogicalPath = logicalPath, Completed = completed, Total = total }); }
            catch { }
        }
    }
}
