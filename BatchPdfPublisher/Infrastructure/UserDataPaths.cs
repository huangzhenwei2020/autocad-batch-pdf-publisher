using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BatchPdfPublisher.Services
{
    /// <summary>All persistent user data owned by Wanluo Architecture Tools.</summary>
    public static class UserDataPaths
    {
        public const string PortableRootEnvironmentVariable = "WANLUO_ARCHITECTURE_TOOLS_ROOT";
        private static readonly object MigrationLock = new object();
        private static bool _migrationChecked;
        public static string PluginDirectory { get { return FindPluginDirectory(); } }
        public static string RootDirectory
        {
            get
            {
                var root = Ensure(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WanluoArchitectureTools",
                    "用户配置文件"));
                MigratePortableUserData(root);
                return root;
            }
        }
        public static string SettingsDirectory { get { return Ensure(Path.Combine(RootDirectory, "通用设置")); } }
        public static string ProjectsDirectory { get { return Ensure(Path.Combine(RootDirectory, "项目配置")); } }
        public static string LogsDirectory { get { return Ensure(Path.Combine(RootDirectory, "Logs")); } }
        public static string TemporaryDirectory { get { return Ensure(Path.Combine(RootDirectory, "Temp")); } }
        public static string OutputDirectory { get { return Ensure(Path.Combine(RootDirectory, "输出文件")); } }
        public static string FrameTemplatesDirectory { get { return Ensure(Path.Combine(RootDirectory, "图框模板")); } }

        public static string SettingsFile(string fileName, params string[] legacyFileNames)
        {
            var target = Path.Combine(SettingsDirectory, fileName);
            if (!File.Exists(target))
            {
                var candidates = new System.Collections.Generic.List<string>();
                candidates.Add(Path.Combine(RootDirectory, "Settings", fileName));
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools", "Settings", fileName));
                if (legacyFileNames != null) foreach (var legacyName in legacyFileNames)
                {
                    if (string.IsNullOrWhiteSpace(legacyName)) continue;
                    if (!Path.IsPathRooted(legacyName)) candidates.Add(Path.Combine(RootDirectory, "Settings", legacyName));
                    candidates.Add(Path.IsPathRooted(legacyName)
                        ? legacyName
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), legacyName));
                }
                foreach (var legacy in candidates) try { if (File.Exists(legacy)) { File.Copy(legacy, target, false); break; } } catch { }
            }
            return target;
        }

        public static string RelativeToRoot(string absolutePath)
        {
            var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(absolutePath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        public static string ResolveFromRoot(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.IsPathRooted(path) ? path : Path.Combine(RootDirectory, path);
        }

        private static string FindPluginDirectory()
        {
            var configured = Environment.GetEnvironmentVariable(PortableRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return Path.GetFullPath(configured);
            var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var current = new DirectoryInfo(string.IsNullOrWhiteSpace(location) ? AppDomain.CurrentDomain.BaseDirectory : location);
            for (var level = 0; current != null && level < 6; level++, current = current.Parent)
            {
                var marker = Path.Combine(current.FullName, "portable-root.txt");
                try { if (File.Exists(marker)) { var portable = File.ReadAllText(marker).Trim(); if (Directory.Exists(portable)) return Path.GetFullPath(portable); } } catch { }
                if (File.Exists(Path.Combine(current.FullName, "万落建筑工具启动器.exe")) || (Directory.Exists(Path.Combine(current.FullName, "CadApi")) && Directory.Exists(Path.Combine(current.FullName, "Resources")))) return current.FullName;
            }
            return Path.GetFullPath(string.IsNullOrWhiteSpace(location) ? AppDomain.CurrentDomain.BaseDirectory : location);
        }

        private static void MigratePortableUserData(string targetRoot)
        {
            lock (MigrationLock)
            {
                if (_migrationChecked) return;
                _migrationChecked = true;
                var sourceRoot = Path.Combine(PluginDirectory, "用户配置文件");
                if (PathsEqual(sourceRoot, targetRoot) || !Directory.Exists(sourceRoot)) return;
                MergeDirectory(sourceRoot, targetRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "运行文件", "Logs", "Temp"
                });
            }
        }

        private static void MergeDirectory(string sourceRoot, string targetRoot, ISet<string> excludedTopLevelNames)
        {
            foreach (var sourceFile in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = sourceFile.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                var topLevel = relative.Split(Path.DirectorySeparatorChar)[0];
                if (excludedTopLevelNames.Contains(topLevel)) continue;
                var targetFile = Path.Combine(targetRoot, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
                    if (!File.Exists(targetFile)) File.Copy(sourceFile, targetFile, false);
                }
                catch { }
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Ensure(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
