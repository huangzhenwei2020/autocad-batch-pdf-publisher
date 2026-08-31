using System;
using System.Collections.Generic;
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
            if (settings == null) throw new ArgumentNullException("settings");
            if (catalog == null) throw new ArgumentNullException("catalog");
            if (!settings.Enabled) throw new InvalidOperationException("云同步尚未启用。");
            if (string.IsNullOrWhiteSpace(workingFolder)) throw new InvalidOperationException("同步提供商没有可用的工作目录。");

            lock (ProcessSync)
            {
                using (var crossProcess = new Mutex(false, "WanluoArchitectureTools.CloudSync"))
                {
                    var acquired = false;
                    try
                    {
                        try { acquired = crossProcess.WaitOne(TimeSpan.FromSeconds(30)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new IOException("另一份 AutoCAD 正在执行同步，请稍后重试。");
                        return SynchronizeLocked(settings, catalog, workingFolder);
                    }
                    finally { if (acquired) crossProcess.ReleaseMutex(); }
                }
            }
        }

        private CloudSyncResult SynchronizeLocked(CloudSyncSettings settings, CloudSyncCatalog catalog, string workingFolder)
        {
            CloudSyncPendingFileService.ApplyAvailable(catalog);
            var mirrorRoot = Path.GetFullPath(Path.Combine(workingFolder, "万落建筑云同步"));
            ValidateRoots(mirrorRoot, catalog.Roots);
            Directory.CreateDirectory(mirrorRoot);

            var state = _store.LoadState();
            var stateByPath = state.Files
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.LogicalPath))
                .GroupBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var localFiles = catalog.EnumerateFiles()
                .ToDictionary(file => file.LogicalPath, file => file.LocalPath, StringComparer.OrdinalIgnoreCase);
            var remoteFiles = EnumerateRemoteFiles(mirrorRoot)
                .ToDictionary(file => file.LogicalPath, file => file.LocalPath, StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(stateByPath.Keys, StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(localFiles.Keys);
            paths.UnionWith(remoteFiles.Keys);

            var result = new CloudSyncResult();
            foreach (var logicalPath in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    ProcessFile(settings, catalog, mirrorRoot, logicalPath, localFiles, remoteFiles, stateByPath, result);
                }
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

            state.Files = stateByPath.Values.OrderBy(item => item.LogicalPath, StringComparer.OrdinalIgnoreCase).ToList();
            _store.SaveState(state);
            CleanupHistory(_localHistoryRoot, settings);
            CleanupHistory(Path.Combine(mirrorRoot, "历史版本"), settings);
            return result;
        }

        private void ProcessFile(CloudSyncSettings settings, CloudSyncCatalog catalog, string mirrorRoot,
            string logicalPath, IDictionary<string, string> localFiles, IDictionary<string, string> remoteFiles,
            IDictionary<string, CloudSyncFileState> states, CloudSyncResult result)
        {
            string localPath;
            if (!localFiles.TryGetValue(logicalPath, out localPath) && !catalog.TryResolve(logicalPath, out localPath))
                throw new InvalidOperationException("找不到该同步文件对应的本地数据源。");
            string remotePath;
            if (!remoteFiles.TryGetValue(logicalPath, out remotePath)) remotePath = ResolveRemotePath(mirrorRoot, logicalPath);

            var localHash = HashIfExists(localPath);
            var remoteHash = HashIfExists(remotePath);
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

            if (baseHash == null)
            {
                if (localHash != null && remoteHash == null)
                {
                    Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result);
                    return;
                }
                if (localHash == null && remoteHash != null)
                {
                    Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result);
                    return;
                }
                if (localHash != null && remoteHash != null &&
                    string.Equals(settings.InitialSyncPreference, "Remote", StringComparison.OrdinalIgnoreCase))
                {
                    Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result);
                    return;
                }
                if (localHash != null && remoteHash != null &&
                    string.Equals(settings.InitialSyncPreference, "Local", StringComparison.OrdinalIgnoreCase))
                {
                    Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result);
                    return;
                }
                CreateConflict(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, remoteHash, baseHash, states, result);
                return;
            }

            if (localHash == null && HashesEqual(remoteHash, baseHash))
            {
                Backup(remotePath, Path.Combine(mirrorRoot, "历史版本"), logicalPath, settings);
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
                Backup(localPath, _localHistoryRoot, logicalPath, settings);
                File.Delete(localPath);
                states.Remove(logicalPath);
                result.Deleted++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.DeleteLocal, "共享目录删除已应用到本机。");
                return;
            }
            if (HashesEqual(localHash, baseHash) && remoteHash != null)
            {
                Download(settings, logicalPath, remotePath, localPath, remoteHash, states, result);
                return;
            }
            if (HashesEqual(remoteHash, baseHash) && localHash != null)
            {
                Upload(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, states, result);
                return;
            }

            CreateConflict(settings, mirrorRoot, logicalPath, localPath, remotePath, localHash, remoteHash, baseHash, states, result);
        }

        private void Upload(CloudSyncSettings settings, string mirrorRoot, string logicalPath, string localPath,
            string remotePath, string localHash, IDictionary<string, CloudSyncFileState> states, CloudSyncResult result)
        {
            if (File.Exists(remotePath)) Backup(remotePath, Path.Combine(mirrorRoot, "历史版本"), logicalPath, settings);
            AtomicCopy(localPath, remotePath, localHash);
            SaveState(states, logicalPath, localHash, localHash, localHash);
            result.Uploaded++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Upload, "已上传到共享目录。");
        }

        private void Download(CloudSyncSettings settings, string logicalPath, string remotePath, string localPath,
            string remoteHash, IDictionary<string, CloudSyncFileState> states, CloudSyncResult result)
        {
            if (CloudSyncPendingFileService.ShouldDefer(localPath))
            {
                CloudSyncPendingFileService.StageDownload(logicalPath, remotePath, remoteHash);
                result.Pending++;
                AddOperation(result, logicalPath, CloudSyncOperationKind.Pending, "图纸正在 AutoCAD 中打开，远端版本已下载到待应用区，关闭图纸后替换。");
                return;
            }
            if (File.Exists(localPath)) Backup(localPath, _localHistoryRoot, logicalPath, settings);
            AtomicCopy(remotePath, localPath, remoteHash);
            SaveState(states, logicalPath, remoteHash, remoteHash, remoteHash);
            result.Downloaded++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Download, "已从共享目录更新本机文件。");
        }

        private void CreateConflict(CloudSyncSettings settings, string mirrorRoot, string logicalPath,
            string localPath, string remotePath, string localHash, string remoteHash, string baseHash,
            IDictionary<string, CloudSyncFileState> states, CloudSyncResult result)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            var device = SafeName(settings.DeviceName);
            var conflictRoot = Path.Combine(mirrorRoot, "冲突文件", timestamp + "-" + device);
            if (File.Exists(localPath)) CopyConflict(localPath, conflictRoot, logicalPath, ".local-conflict");
            if (File.Exists(remotePath)) CopyConflict(remotePath, conflictRoot, logicalPath, ".remote-conflict");
            var state = GetOrCreateState(states, logicalPath);
            state.BaseHash = baseHash;
            state.LocalHash = localHash;
            state.RemoteHash = remoteHash;
            result.Conflicts++;
            AddOperation(result, logicalPath, CloudSyncOperationKind.Conflict,
                "本机和共享目录均已修改，已保留两份冲突副本，未覆盖正式文件。");
        }

        private static void CopyConflict(string source, string conflictRoot, string logicalPath, string suffix)
        {
            var target = ResolveRemotePath(conflictRoot, logicalPath) + suffix;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, false);
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

        private static void AtomicCopy(string source, string target, string expectedHash)
        {
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("同步目标目录无效。");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary, false);
                var actualHash = ComputeHash(temporary);
                if (!HashesEqual(actualHash, expectedHash)) throw new IOException("同步文件哈希校验失败。");
                if (File.Exists(target))
                {
                    try { File.Replace(temporary, target, null, true); }
                    catch (PlatformNotSupportedException) { ReplaceWithRollback(temporary, target); }
                    catch (IOException) { ReplaceWithRollback(temporary, target); }
                }
                else File.Move(temporary, target);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void ReplaceWithRollback(string temporary, string target)
        {
            var rollback = target + "." + Guid.NewGuid().ToString("N") + ".rollback";
            File.Copy(target, rollback, false);
            try { File.Copy(temporary, target, true); }
            catch { File.Copy(rollback, target, true); throw; }
            finally { try { File.Delete(rollback); } catch { } }
        }

        private static void Backup(string source, string historyRoot, string logicalPath, CloudSyncSettings settings)
        {
            if (!File.Exists(source)) return;
            var relative = logicalPath.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.Combine(historyRoot, Path.GetDirectoryName(relative) ?? string.Empty,
                Path.GetFileName(relative));
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(source);
            var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + extension;
            File.Copy(source, Path.Combine(directory, name), false);
            PruneDirectory(directory, settings);
        }

        private static void CleanupHistory(string historyRoot, CloudSyncSettings settings)
        {
            if (!Directory.Exists(historyRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(historyRoot, "*", SearchOption.AllDirectories))
                PruneDirectory(directory, settings);
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

        private static string HashIfExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? ComputeHash(path) : null;
        }

        internal static string ComputeHash(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
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
    }
}
