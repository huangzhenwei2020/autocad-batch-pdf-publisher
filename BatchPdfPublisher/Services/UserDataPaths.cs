using System;
using System.IO;

namespace BatchPdfPublisher.Services
{
    /// <summary>All persistent user data owned by Wanluo Architecture Tools.</summary>
    public static class UserDataPaths
    {
        public static string RootDirectory { get { return Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WanluoArchitectureTools")); } }
        public static string SettingsDirectory { get { return Ensure(Path.Combine(RootDirectory, "Settings")); } }
        public static string ProjectsDirectory { get { return Ensure(Path.Combine(RootDirectory, "Projects")); } }
        public static string LogsDirectory { get { return Ensure(Path.Combine(RootDirectory, "Logs")); } }

        public static string SettingsFile(string fileName, params string[] legacyFileNames)
        {
            var target = Path.Combine(SettingsDirectory, fileName);
            if (!File.Exists(target) && legacyFileNames != null)
            {
                foreach (var legacyName in legacyFileNames)
                {
                    if (string.IsNullOrWhiteSpace(legacyName)) continue;
                    var legacy = Path.IsPathRooted(legacyName)
                        ? legacyName
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), legacyName);
                    try { if (File.Exists(legacy)) { File.Copy(legacy, target, false); break; } } catch { }
                }
            }
            return target;
        }

        private static string Ensure(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
