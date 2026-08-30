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
            foreach (var path in Directory.EnumerateFiles(LocalRoot, "*", SearchOption.AllDirectories))
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
            var candidate = Path.GetFullPath(Path.Combine(LocalRoot, relative));
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

        public CloudSyncCatalog(IEnumerable<CloudSyncSource> sources)
        {
            _sources = (sources ?? Enumerable.Empty<CloudSyncSource>()).ToList();
        }

        public IEnumerable<CloudSyncFile> EnumerateFiles()
        {
            return _sources.SelectMany(source => source.EnumerateFiles())
                .GroupBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }

        public bool TryResolve(string logicalPath, out string localPath)
        {
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
            if (settings == null) throw new ArgumentNullException("settings");
            var sources = new List<CloudSyncSource>();
            if (settings.SyncGeneralSettings)
                sources.Add(new CloudSyncSource("通用配置", UserDataPaths.SettingsDirectory, IncludeGeneralSetting));
            if (settings.SyncProjectConfigurations)
                sources.Add(new CloudSyncSource("项目配置", UserDataPaths.ProjectsDirectory,
                    relative => IncludeProjectFile(relative, settings.SyncProjectFiles)));
            if (settings.SyncTemplatesAndSchemes)
            {
                sources.Add(new CloudSyncSource("图框模板", UserDataPaths.FrameTemplatesDirectory, IncludeNormalFile));
                var portableRoot = UserDataPaths.PluginDirectory;
                var stairSchemes = Path.Combine(portableRoot, "用户配置文件", "楼梯大样", "方案库");
                sources.Add(new CloudSyncSource("方案库/楼梯", stairSchemes, IncludeNormalFile));
            }
            return new CloudSyncCatalog(sources);
        }

        private static bool IncludeGeneralSetting(string relative)
        {
            var fileName = Path.GetFileName(relative);
            if (fileName.Equals("cloud-sync.settings.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("当前项目.txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("项目列表.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("BatchPdfPublisher.projects.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("BatchPdfPublisher.active-project.txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("recent-projects.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("ui-layout.settings", StringComparison.OrdinalIgnoreCase)) return false;
            return IncludeNormalFile(relative);
        }

        private static bool IncludeProjectFile(string relative, bool includeDrawingFiles)
        {
            if (!IncludeNormalFile(relative)) return false;
            var extension = Path.GetExtension(relative);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
            if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase)) return includeDrawingFiles;
            return true;
        }

        private static bool IncludeNormalFile(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            var parts = normalized.Split('/');
            if (parts.Any(part => part.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("输出文件", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("PDF输出", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals(".cloud-sync", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("冲突文件", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("历史版本", StringComparison.OrdinalIgnoreCase))) return false;
            var fileName = Path.GetFileName(relative);
            if (fileName.StartsWith(".", StringComparison.Ordinal) ||
                fileName.StartsWith("~", StringComparison.Ordinal)) return false;
            var extension = Path.GetExtension(fileName);
            return !TemporaryExtensions.Any(item => item.Equals(extension, StringComparison.OrdinalIgnoreCase));
        }
    }
}
