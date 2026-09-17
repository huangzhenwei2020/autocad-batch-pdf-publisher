using System;
using System.Collections.Generic;
using System.IO;

namespace WL.Stair.CadShared
{
    internal static class WanluoDataPaths
    {
        internal const string UserDataRootVariable = "WANLUO_ARCHITECTURE_TOOLS_USER_DATA_ROOT";
        internal const string PackageRootVariable = "WANLUO_ARCHITECTURE_TOOLS_ROOT";
        private static readonly object MigrationSync = new object();
        private static bool _migrationChecked;

        internal static string Root
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable(UserDataRootVariable);
                var root = string.IsNullOrWhiteSpace(configured)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WanluoArchitectureTools", "用户配置文件")
                    : Path.GetFullPath(configured);
                Directory.CreateDirectory(root);
                MigrateLegacyPackageData(root);
                return root;
            }
        }

        private static void MigrateLegacyPackageData(string targetRoot)
        {
            lock (MigrationSync)
            {
                if (_migrationChecked) return;
                _migrationChecked = true;
                var packageRoot = Environment.GetEnvironmentVariable(PackageRootVariable);
                if (string.IsNullOrWhiteSpace(packageRoot)) return;
                var sourceRoot = Path.Combine(packageRoot, "用户配置文件");
                if (!Directory.Exists(sourceRoot) || PathsEqual(sourceRoot, targetRoot)) return;
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "运行文件", "Logs", "Temp"
                };
                foreach (var source in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = source.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
                        .TrimStart(Path.DirectorySeparatorChar);
                    if (excluded.Contains(relative.Split(Path.DirectorySeparatorChar)[0])) continue;
                    var target = Path.Combine(targetRoot, relative);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (!File.Exists(target)) File.Copy(source, target, false);
                    }
                    catch { }
                }
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
