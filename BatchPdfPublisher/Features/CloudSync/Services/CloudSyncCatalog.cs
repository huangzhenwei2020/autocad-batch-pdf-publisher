using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudSyncSource
    {
        private readonly Func<string, bool> _include;

        public CloudSyncSource(string logicalPrefix, string localRoot, Func<string, bool> include)
        {
            LogicalPrefix = NormalizeLogicalPath(logicalPrefix).TrimEnd('/');
            LocalRoot = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _include = include ?? delegate { return true; };
        }

        public string LogicalPrefix { get; private set; }
        public string LocalRoot { get; private set; }

        public IEnumerable<CloudSyncFile> EnumerateFiles()
        {
            if (!Directory.Exists(LocalRoot)) yield break;
            foreach (var path in CloudSyncCatalog.EnumerateRegularFiles(LocalRoot))
            {
                var relative = path.Substring(LocalRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!IsSafeRelativePath(relative) || !_include(relative)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                yield return new CloudSyncFile(CombineLogical(LogicalPrefix, relative), path);
            }
        }

        public bool TryResolve(string logicalPath, out string localPath)
        {
            localPath = null;
            var normalized = NormalizeLogicalPath(logicalPath);
            var prefix = LogicalPrefix + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var relative = normalized.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
            if (!IsSafeRelativePath(relative) || !_include(relative)) return false;
            string candidate;
            try { candidate = ImmutableCloudJournal.SafePath(LocalRoot, NormalizeLogicalPath(relative)); }
            catch (InvalidDataException) { return false; }
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) return false;
            var rootPrefix = LocalRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            localPath = candidate;
            return true;
        }

        private static bool IsSafeRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part == ".." || part == ".");
        }

        internal static string NormalizeLogicalPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        internal static string CombineLogical(string prefix, string relative)
        {
            return NormalizeLogicalPath(prefix + "/" + relative);
        }
    }

    public sealed class CloudSyncFile
    {
        public CloudSyncFile(string logicalPath, string localPath)
        {
            LogicalPath = CloudSyncSource.NormalizeLogicalPath(logicalPath);
            LocalPath = Path.GetFullPath(localPath);
        }

        public string LogicalPath { get; private set; }
        public string LocalPath { get; private set; }
    }

    public sealed class CloudSyncCatalog
    {
        private static readonly string[] TemporaryExtensions =
        {
            ".tmp", ".bak", ".dwl", ".dwl2", ".sv$", ".ac$", ".log", ".pdb", ".dll", ".exe", ".zip"
        };

        private readonly IList<CloudSyncSource> _sources;
        private readonly HashSet<string> _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal void Exclude(IEnumerable<string> paths)
        {
            foreach (var path in paths) _excluded.Add(CloudSyncSource.NormalizeLogicalPath(path));
        }

        internal static IEnumerable<string> EnumerateRegularFiles(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                FileAttributes directoryAttributes;
                try { directoryAttributes = File.GetAttributes(directory); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0) continue;

                string[] files;
                try { files = Directory.GetFiles(directory); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var file in files)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(file); }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) == 0) yield return file;
                }

                string[] children;
                try { children = Directory.GetDirectories(directory); }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var child in children)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(child); }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
        }

        public CloudSyncCatalog(IEnumerable<CloudSyncSource> sources)
        {
            _sources = (sources ?? Enumerable.Empty<CloudSyncSource>()).ToList();
        }

        public IEnumerable<CloudSyncFile> EnumerateFiles()
        {
            return _sources.SelectMany(source => source.EnumerateFiles())
                .Where(file => !_excluded.Contains(file.LogicalPath))
                .GroupBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }

        public bool TryResolve(string logicalPath, out string localPath)
        {
            if (_excluded.Contains(CloudSyncSource.NormalizeLogicalPath(logicalPath))) { localPath = null; return false; }
            foreach (var source in _sources)
                if (source.TryResolve(logicalPath, out localPath)) return true;
            localPath = null;
            return false;
        }

        public IEnumerable<string> Roots
        {
            get { return _sources.Select(source => source.LocalRoot).Distinct(StringComparer.OrdinalIgnoreCase); }
        }

        public static CloudSyncCatalog CreateDefault(CloudSyncSettings settings)
        {
            return CreateDefault(settings, true);
        }

        public static CloudSyncCatalog CreateDefault(CloudSyncSettings settings, bool includeSystemPackage)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            var sources = new List<CloudSyncSource>();
            var projectMappings = settings.SyncProjectFiles && settings.ProjectMappings != null
                ? settings.ProjectMappings.Where(item => item != null && item.Enabled &&
                    !string.IsNullOrWhiteSpace(item.CloudId) && !string.IsNullOrWhiteSpace(item.LocalFolder) &&
                    !ProjectSyncProjectionStore.IsCloudProjectArchived(item.CloudId)).ToList()
                : new List<CloudSyncProjectMapping>();
            if (includeSystemPackage)
            {
                if (settings.SyncGeneralSettings)
                    sources.Add(new CloudSyncSource("通用配置", UserDataPaths.SettingsDirectory, IncludeGeneralSetting));
                if (settings.SyncProjectConfigurations)
                    sources.Add(new CloudSyncSource("项目配置", UserDataPaths.ProjectsDirectory, IncludePortableProjectConfiguration));
                if (settings.SyncTemplatesAndSchemes)
                {
                    sources.Add(new CloudSyncSource("图框模板", UserDataPaths.FrameTemplatesDirectory, IncludeNormalFile));
                    sources.Add(new CloudSyncSource("方案库/楼梯", Path.Combine(UserDataPaths.RootDirectory, "楼梯大样", "方案库"), IncludeNormalFile));
                }
            }
            if (settings.SyncProjectFiles)
            {
                foreach (var mapping in projectMappings)
                {
                    string root;
                    try { root = Path.GetFullPath(mapping.LocalFolder); }
                    catch { continue; }
                    sources.Add(new CloudSyncSource("项目文件/" + mapping.CloudId, root, IncludeExternalProjectFile));
                }
            }
            return new CloudSyncCatalog(sources);
        }

        internal static bool IncludeGeneralSetting(string relative)
        {
            var fileName = Path.GetFileName(relative);
            if (fileName.IndexOf(".cloud-conflict-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (fileName.Equals("cloud-sync.settings.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("当前项目.txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("项目列表.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("BatchPdfPublisher.projects.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("BatchPdfPublisher.active-project.txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("recent-projects.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("ui-layout.settings", StringComparison.OrdinalIgnoreCase)) return false;
            return IncludeNormalFile(relative);
        }

        internal static bool IncludePortableProjectConfiguration(string relative)
        {
            if (!IncludeNormalFile(relative)) return false;
            var normalized = CloudSyncSource.NormalizeLogicalPath(relative);
            if (normalized.Equals("同步项目/归档项目.json", StringComparison.OrdinalIgnoreCase)) return true;
            return normalized.StartsWith("同步项目/", StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(normalized).Equals("项目.json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IncludeExternalProjectFile(string relative)
        {
            return IncludeNormalFile(relative);
        }

        internal static bool IncludeNormalFile(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            var parts = normalized.Split('/');
            if (parts.Any(part => part.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("输出文件", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("PDF输出", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("自动保存", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals(".cloud-sync", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("冲突文件", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("历史版本", StringComparison.OrdinalIgnoreCase))) return false;
            var fileName = Path.GetFileName(relative);
            if (fileName.StartsWith(".", StringComparison.Ordinal) ||
                fileName.StartsWith("~", StringComparison.Ordinal) ||
                fileName.IndexOf(".cloud-conflict-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            var extension = Path.GetExtension(fileName);
            return !TemporaryExtensions.Any(item => item.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }

    }
}
