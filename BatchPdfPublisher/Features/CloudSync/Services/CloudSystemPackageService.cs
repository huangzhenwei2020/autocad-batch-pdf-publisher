using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    internal sealed class CloudSystemPackageState
    {
        [DataMember] public string PackagedFingerprint { get; set; }
        [DataMember] public string PendingSinceUtc { get; set; }
        [DataMember] public string LastPackagedAtUtc { get; set; }
        [DataMember] public string AppliedPackageHash { get; set; }
    }

    public static class CloudSystemPackageService
    {
        public const string LogicalPrefix = "系统文件包";
        public const string PackageFileName = "万落建筑系统文件.zip";
        public static string PackageDirectory { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "system-package"); } }
        public static string PackagePath { get { return Path.Combine(PackageDirectory, PackageFileName); } }
        public static string LogicalPath { get { return LogicalPrefix + "/" + PackageFileName; } }
        private static string StatePath { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "system-package-state.json"); } }
        private static readonly object FileSync = new object();

        public static bool Prepare(CloudSyncSettings settings, bool force, Action<CloudSyncProgress> progress,
            CancellationToken cancellationToken)
        {
            return Prepare(settings, force, false, progress, cancellationToken);
        }

        public static bool Prepare(CloudSyncSettings settings, bool force, bool remotePackageExists,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (!HasSystemContent(settings)) return false;
            lock (FileSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(PackagePath) && remotePackageExists)
                {
                    progress?.Invoke(new CloudSyncProgress { Stage = "发现云端系统文件包", LogicalPath = "将先下载并应用到本机" });
                    return true;
                }
                var files = SystemFiles(settings).ToList();
                var fingerprint = Fingerprint(files, cancellationToken);
                var state = LoadState();
                if (File.Exists(PackagePath) && string.Equals(state.PackagedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    return true;

                DateTime pendingSince;
                if (!DateTime.TryParse(state.PendingSinceUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out pendingSince))
                {
                    pendingSince = DateTime.UtcNow;
                    state.PendingSinceUtc = pendingSince.ToString("o", CultureInfo.InvariantCulture);
                    SaveState(state);
                }
                var firstPackage = !File.Exists(PackagePath) || string.IsNullOrWhiteSpace(state.PackagedFingerprint);
                var due = force || firstPackage || DateTime.UtcNow - pendingSince >= TimeSpan.FromMinutes(Math.Max(1, settings.SystemPackageIntervalMinutes));
                if (!due)
                {
                    var remaining = TimeSpan.FromMinutes(Math.Max(1, settings.SystemPackageIntervalMinutes)) - (DateTime.UtcNow - pendingSince);
                    progress?.Invoke(new CloudSyncProgress { Stage = "系统文件已变化，等待打包", LogicalPath = "约 " + Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + " 分钟后上传" });
                    return false;
                }

                progress?.Invoke(new CloudSyncProgress { Stage = "正在打包系统文件", LogicalPath = files.Count + " 个文件" });
                CreatePackage(files, cancellationToken);
                state.PackagedFingerprint = fingerprint;
                state.PendingSinceUtc = null;
                state.LastPackagedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                state.AppliedPackageHash = LocalFolderSyncEngine.ComputeHash(PackagePath, cancellationToken);
                SaveState(state);
                return true;
            }
        }

        public static void ApplyDownloadedPackage(CloudSyncSettings settings, CloudSyncResult result,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            if (result == null || !result.Operations.Any(operation => operation.Kind == CloudSyncOperationKind.Download &&
                string.Equals(operation.LogicalPath, LogicalPath, StringComparison.OrdinalIgnoreCase))) return;
            if (!File.Exists(PackagePath)) throw new IOException("下载的系统文件包不存在。");
            lock (FileSync)
            {
                var packageHash = LocalFolderSyncEngine.ComputeHash(PackagePath, cancellationToken);
                var state = LoadState();
                if (string.Equals(state.AppliedPackageHash, packageHash, StringComparison.OrdinalIgnoreCase)) return;
                progress?.Invoke(new CloudSyncProgress { Stage = "正在应用系统文件包" });
                using (var archive = ZipFile.OpenRead(PackagePath))
                {
                    foreach (var entry in archive.Entries.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string target;
                        if (!TryResolveEntry(settings, entry.FullName, out target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (File.Exists(target)) CloudBackupService.BackupFile(target,
                            "系统文件包/" + CloudSyncSource.NormalizeLogicalPath(entry.FullName), "系统文件恢复前", settings);
                        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            using (var input = entry.Open())
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                            if (File.Exists(target))
                            {
                                if (!string.Equals(LocalFolderSyncEngine.ComputeHash(target), LocalFolderSyncEngine.ComputeHash(temporary), StringComparison.OrdinalIgnoreCase))
                                {
                                    var preserved = target + ".cloud-conflict-" + packageHash.Substring(0, 12);
                                    if (!File.Exists(preserved)) File.Copy(temporary, preserved, false);
                                    result.Warnings++;
                                    result.Operations.Add(new CloudSyncOperation { Kind = CloudSyncOperationKind.Conflict,
                                        LogicalPath = entry.FullName, Message = "系统配置不同，保留本机及云端冲突副本：" + preserved });
                                }
                                continue;
                            }
                            File.Move(temporary, target);
                            try { File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime); } catch { }
                        }
                        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
                    }
                }
                state.AppliedPackageHash = packageHash;
                state.PackagedFingerprint = Fingerprint(SystemFiles(settings), cancellationToken);
                state.PendingSinceUtc = null;
                state.LastPackagedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                SaveState(state);
            }
        }

        private static bool HasSystemContent(CloudSyncSettings settings)
        {
            return settings.SyncGeneralSettings || settings.SyncProjectConfigurations || settings.SyncTemplatesAndSchemes;
        }

        // Legacy ZIP is an import source, never an instruction to overwrite live configuration.
        internal static void ExpandLegacyToMirror(CloudSyncSettings settings, string mirror, CancellationToken token)
        {
            var package = Path.Combine(mirror, LogicalPrefix, PackageFileName);
            if (!File.Exists(package)) return;
            using (var zip = ZipFile.OpenRead(package))
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string mapped;
                    if (!TryResolveEntry(settings, entry.FullName, out mapped)) continue;
                    var target = ImmutableCloudJournal.SafePath(mirror, entry.FullName);
                    if (File.Exists(ImmutableCloudJournal.SafePath(Path.Combine(mirror, ".wanluo-sync", "resolutions"), entry.FullName + ".json"))) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                }
        }

        private static IEnumerable<PackageSourceFile> SystemFiles(CloudSyncSettings settings)
        {
            if (settings.SyncGeneralSettings)
                foreach (var file in Files("通用配置", UserDataPaths.SettingsDirectory, CloudSyncCatalog.IncludeGeneralSetting)) yield return file;
            if (settings.SyncProjectConfigurations)
                foreach (var file in Files("项目配置", UserDataPaths.ProjectsDirectory, CloudSyncCatalog.IncludePortableProjectConfiguration)) yield return file;
            if (settings.SyncTemplatesAndSchemes)
            {
                foreach (var file in Files("图框模板", UserDataPaths.FrameTemplatesDirectory, CloudSyncCatalog.IncludeNormalFile)) yield return file;
                foreach (var file in Files("方案库/楼梯", Path.Combine(UserDataPaths.RootDirectory, "楼梯大样", "方案库"), CloudSyncCatalog.IncludeNormalFile)) yield return file;
            }
        }

        private static IEnumerable<PackageSourceFile> Files(string prefix, string root, Func<string, bool> include)
        {
            if (!Directory.Exists(root)) yield break;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            {
                var relative = path.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!include(relative)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); } catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                yield return new PackageSourceFile { Path = path, EntryName = CloudSyncSource.NormalizeLogicalPath(prefix + "/" + relative) };
            }
        }

        private static string Fingerprint(IEnumerable<PackageSourceFile> files, CancellationToken cancellationToken)
        {
            using (var aggregate = SHA256.Create())
            {
                foreach (var file in (files ?? Enumerable.Empty<PackageSourceFile>()).OrderBy(item => item.EntryName, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = file.EntryName + "|" + LocalFolderSyncEngine.ComputeHash(file.Path, cancellationToken) + "\n";
                    var bytes = Encoding.UTF8.GetBytes(line);
                    aggregate.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                aggregate.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(aggregate.Hash).Replace("-", string.Empty);
            }
        }

        private static void CreatePackage(IList<PackageSourceFile> files, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(PackageDirectory);
            var temporary = PackagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    foreach (var file in files.OrderBy(item => item.EntryName, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(file.EntryName, CompressionLevel.Optimal);
                        var modified = File.GetLastWriteTime(file.Path);
                        if (modified.Year >= 1980) entry.LastWriteTime = modified;
                        using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var output = entry.Open()) input.CopyTo(output);
                    }
                }
                var newest = files.Select(file => File.GetLastWriteTimeUtc(file.Path)).DefaultIfEmpty(DateTime.UtcNow).Max();
                try { File.SetLastWriteTimeUtc(temporary, newest); } catch { }
                if (File.Exists(PackagePath)) File.Replace(temporary, PackagePath, null, true);
                else File.Move(temporary, PackagePath);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static bool TryResolveEntry(CloudSyncSettings settings, string entryName, out string target)
        {
            target = null;
            var normalized = CloudSyncSource.NormalizeLogicalPath(entryName);
            var slash = normalized.IndexOf('/');
            if (slash <= 0) return false;
            var category = normalized.Substring(0, slash);
            var relative = normalized.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
            string root;
            Func<string, bool> include;
            if (category.Equals("通用配置", StringComparison.OrdinalIgnoreCase) && settings.SyncGeneralSettings)
            { root = UserDataPaths.SettingsDirectory; include = CloudSyncCatalog.IncludeGeneralSetting; }
            else if (category.Equals("项目配置", StringComparison.OrdinalIgnoreCase) && settings.SyncProjectConfigurations)
            { root = UserDataPaths.ProjectsDirectory; include = CloudSyncCatalog.IncludePortableProjectConfiguration; }
            else if (category.Equals("图框模板", StringComparison.OrdinalIgnoreCase) && settings.SyncTemplatesAndSchemes)
            { root = UserDataPaths.FrameTemplatesDirectory; include = CloudSyncCatalog.IncludeNormalFile; }
            else if (normalized.StartsWith("方案库/楼梯/", StringComparison.OrdinalIgnoreCase) && settings.SyncTemplatesAndSchemes)
            { root = Path.Combine(UserDataPaths.RootDirectory, "楼梯大样", "方案库"); relative = normalized.Substring("方案库/楼梯/".Length).Replace('/', Path.DirectorySeparatorChar); include = CloudSyncCatalog.IncludeNormalFile; }
            else return false;
            if (!include(relative)) return false;
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
            target = candidate;
            return true;
        }

        internal static bool TryResolveSystemFile(CloudSyncSettings settings, string logicalPath, out string target)
        {
            var normalized = CloudSyncSource.NormalizeLogicalPath(logicalPath);
            var packagePrefix = LogicalPrefix + "/";
            if (normalized.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(packagePrefix.Length);
            return TryResolveEntry(settings, normalized, out target);
        }

        private static CloudSystemPackageState LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return new CloudSystemPackageState();
                using (var stream = File.OpenRead(StatePath)) return (CloudSystemPackageState)new DataContractJsonSerializer(typeof(CloudSystemPackageState)).ReadObject(stream) ?? new CloudSystemPackageState();
            }
            catch { return new CloudSystemPackageState(); }
        }

        private static void SaveState(CloudSystemPackageState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            var temporary = StatePath + ".tmp";
            using (var stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(CloudSystemPackageState)).WriteObject(stream, state);
            if (File.Exists(StatePath)) File.Replace(temporary, StatePath, StatePath + ".bak", true);
            else File.Move(temporary, StatePath);
        }

        private sealed class PackageSourceFile
        {
            public string Path { get; set; }
            public string EntryName { get; set; }
        }
    }
}
