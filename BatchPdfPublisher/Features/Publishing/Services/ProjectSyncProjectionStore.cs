using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// Writes a portable project description for cloud synchronization. Machine
    /// paths stay in 项目列表.json; only paths inside the project folder become
    /// $PROJECT$/... tokens in the synchronized projection.
    /// </summary>
    public static class ProjectSyncProjectionStore
    {
        private const string ProjectToken = "$PROJECT$/";
        private static readonly object FileSync = new object();
        public static string ProjectionDirectory { get { return Path.Combine(UserDataPaths.ProjectsDirectory, "同步项目"); } }

        public static void Export(IEnumerable<ProjectProfile> projects)
        {
            if (projects == null) return;
            lock (FileSync)
            {
                Directory.CreateDirectory(ProjectionDirectory);
                foreach (var project in projects.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name)))
                {
                    var portable = CreatePortable(project);
                    var path = ProjectionPath(project.Name);
                    var bytes = Serialize(portable);
                    if (File.Exists(path) && BytesEqual(File.ReadAllBytes(path), bytes)) continue;
                    WriteAtomically(path, bytes);
                }
            }
        }

        public static bool MergeInto(IList<ProjectProfile> projects)
        {
            if (projects == null || !Directory.Exists(ProjectionDirectory)) return false;
            var changed = false;
            lock (FileSync)
            {
                foreach (var path in Directory.EnumerateFiles(ProjectionDirectory, "项目.json", SearchOption.AllDirectories))
                {
                    ProjectProfile remote;
                    try { remote = Deserialize(File.ReadAllBytes(path)); }
                    catch { continue; }
                    if (remote == null || string.IsNullOrWhiteSpace(remote.Name)) continue;
                    var existing = projects.FirstOrDefault(item => item != null &&
                        string.Equals(item.Name, remote.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && BytesEqual(Serialize(CreatePortable(existing)), Serialize(remote))) continue;
                    var localFolder = existing == null || string.IsNullOrWhiteSpace(existing.ProjectFolder)
                        ? DefaultProjectFolder(remote.Name) : existing.ProjectFolder;
                    var localOutput = existing == null ? null : existing.OutputDirectory;
                    var localExternalCad = existing == null ? new List<string>() : ExternalPaths(existing.CadFiles, localFolder);
                    var localExternalSelected = existing == null ? new List<string>() : ExternalPaths(existing.SelectedCadFiles, localFolder);
                    RestoreLocalPaths(remote, localFolder, localOutput, localExternalCad, localExternalSelected);
                    if (existing == null) projects.Add(remote);
                    else projects[projects.IndexOf(existing)] = remote;
                    changed = true;
                }
            }
            return changed;
        }

        public static List<CloudSyncProjectMapping> BuildMappings(IEnumerable<ProjectProfile> projects,
            IEnumerable<CloudSyncProjectMapping> previous)
        {
            var old = (previous ?? Enumerable.Empty<CloudSyncProjectMapping>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ProjectName))
                .GroupBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var result = new List<CloudSyncProjectMapping>();
            foreach (var project in (projects ?? Enumerable.Empty<ProjectProfile>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Name)))
            {
                CloudSyncProjectMapping existing;
                old.TryGetValue(project.Name, out existing);
                result.Add(new CloudSyncProjectMapping
                {
                    ProjectName = project.Name,
                    CloudId = StableProjectId(project.Name),
                    LocalFolder = string.IsNullOrWhiteSpace(project.ProjectFolder) ? DefaultProjectFolder(project.Name) : project.ProjectFolder,
                    Enabled = existing == null || existing.Enabled
                });
            }
            return result;
        }

        public static void RefreshMappings(IEnumerable<ProjectProfile> projects)
        {
            try
            {
                var store = new CloudSyncSettingsStore();
                var settings = store.LoadSettings();
                if (!settings.SyncProjectFiles) return;
                var refreshed = BuildMappings(projects, settings.ProjectMappings);
                var before = settings.ProjectMappings ?? new List<CloudSyncProjectMapping>();
                var changed = before.Count != refreshed.Count || before.Zip(refreshed, (left, right) =>
                    left == null || !string.Equals(left.ProjectName, right.ProjectName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(left.CloudId, right.CloudId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(left.LocalFolder, right.LocalFolder, StringComparison.OrdinalIgnoreCase) ||
                    left.Enabled != right.Enabled).Any(value => value);
                if (!changed) return;
                settings.ProjectMappings = refreshed;
                store.SaveSettings(settings);
                CloudSyncCoordinator.QueueReload(false);
            }
            catch { }
        }

        private static ProjectProfile CreatePortable(ProjectProfile project)
        {
            var clone = Deserialize(Serialize(project));
            var root = string.IsNullOrWhiteSpace(project.ProjectFolder) ? DefaultProjectFolder(project.Name) : project.ProjectFolder;
            clone.ProjectFolder = null;
            clone.OutputDirectory = PortablePath(project.OutputDirectory, root);
            clone.CadFiles = PortablePaths(project.CadFiles, root);
            clone.SelectedCadFiles = PortablePaths(project.SelectedCadFiles, root);
            foreach (var sheet in clone.SavedSheets ?? new List<SheetCatalogItem>())
                sheet.SourceFile = PortablePath(sheet.SourceFile, root);
            return clone;
        }

        private static void RestoreLocalPaths(ProjectProfile project, string root, string previousOutput,
            IEnumerable<string> externalCad, IEnumerable<string> externalSelected)
        {
            project.ProjectFolder = root;
            var resolvedOutput = LocalPath(project.OutputDirectory, root);
            project.OutputDirectory = !string.IsNullOrWhiteSpace(resolvedOutput) ? resolvedOutput
                : !string.IsNullOrWhiteSpace(previousOutput) ? previousOutput : Path.Combine(root, "PDF输出");
            project.CadFiles = LocalPaths(project.CadFiles, root).Concat(externalCad ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            project.SelectedCadFiles = LocalPaths(project.SelectedCadFiles, root).Concat(externalSelected ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
                sheet.SourceFile = LocalPath(sheet.SourceFile, root);
        }

        private static List<string> PortablePaths(IEnumerable<string> paths, string root)
        {
            return (paths ?? Enumerable.Empty<string>()).Select(path => PortablePath(path, root))
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> LocalPaths(IEnumerable<string> paths, string root)
        {
            return (paths ?? Enumerable.Empty<string>()).Select(path => LocalPath(path, root))
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ExternalPaths(IEnumerable<string> paths, string root)
        {
            return (paths ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrWhiteSpace(path) && PortablePath(path, root) == null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string PortablePath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(path);
                if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return ProjectToken.TrimEnd('/');
                var prefix = fullRoot + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
                return ProjectToken + fullPath.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch { return null; }
        }

        private static string LocalPath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.Equals(ProjectToken.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return root;
            if (!path.StartsWith(ProjectToken, StringComparison.OrdinalIgnoreCase)) return null;
            var relative = path.Substring(ProjectToken.Length).Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }

        private static string ProjectionPath(string name)
        {
            return Path.Combine(ProjectionDirectory, StableProjectId(name), "项目.json");
        }

        private static string StableProjectId(string name)
        {
            var safe = new string((name ?? "项目").Trim().Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            if (safe.Length > 36) safe = safe.Substring(0, 36);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((name ?? string.Empty).Trim().ToUpperInvariant()));
                return safe + "-" + string.Concat(hash.Take(5).Select(value => value.ToString("x2")));
            }
        }

        private static string DefaultProjectFolder(string name)
        {
            var safe = new string((string.IsNullOrWhiteSpace(name) ? "默认项目" : name.Trim()).Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
            return Path.Combine(UserDataPaths.ProjectsDirectory, safe);
        }

        private static byte[] Serialize(ProjectProfile project)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ProjectProfile)).WriteObject(stream, project);
                return stream.ToArray();
            }
        }

        private static ProjectProfile Deserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
                return (ProjectProfile)new DataContractJsonSerializer(typeof(ProjectProfile)).ReadObject(stream);
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Copy(temporary, path, true);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            return left != null && right != null && left.Length == right.Length && left.SequenceEqual(right);
        }
    }
}
