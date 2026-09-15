using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudSyncFolderCandidate
    {
        public string ProviderName { get; set; }
        public string FolderPath { get; set; }
        public string Source { get; set; }

        public override string ToString()
        {
            return ProviderName + "  ·  " + FolderPath;
        }
    }

    /// <summary>
    /// Detects local folders maintained by desktop sync clients. It never logs in
    /// to a cloud service and never treats a running process as proof that a file
    /// has already reached the cloud.
    /// </summary>
    public static class CloudSyncFolderDetector
    {
        public static IList<CloudSyncFolderCandidate> Discover()
        {
            var result = new List<CloudSyncFolderCandidate>();
            Add(result, Environment.GetEnvironmentVariable("OneDriveCommercial"), "OneDrive（工作/学校）", "Windows 环境");
            Add(result, Environment.GetEnvironmentVariable("OneDriveConsumer"), "OneDrive（个人）", "Windows 环境");
            Add(result, Environment.GetEnvironmentVariable("OneDrive"), "OneDrive", "Windows 环境");

            AddDropboxInfo(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"));
            AddDropboxInfo(result, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"));

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddKnownFolder(result, profile, "Dropbox", "Dropbox");
            AddKnownFolder(result, profile, "坚果云", "坚果云");
            AddNutstoreFolders(result, Path.Combine(profile, "Nutstore"));
            AddKnownFolder(result, profile, "Syncthing", "Syncthing");

            return result
                .Where(item => !string.IsNullOrWhiteSpace(item.FolderPath))
                .GroupBy(item => Normalize(item.FolderPath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.ProviderName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static string IdentifyProvider(string folderPath)
        {
            var path = folderPath ?? string.Empty;
            if (Contains(path, "OneDrive")) return "OneDrive";
            if (Contains(path, "Dropbox")) return "Dropbox";
            if (Contains(path, "坚果云") || Contains(path, "Nutstore")) return "坚果云";
            if (Contains(path, "Syncthing")) return "Syncthing";
            return "通用同步文件夹";
        }

        public static string Describe(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return "尚未选择同步文件夹；可自动识别 OneDrive、Dropbox、坚果云或 Syncthing。";

            var provider = IdentifyProvider(folderPath);
            if (IsNutstoreInternalRoot(folderPath))
                return "坚果云：当前路径是客户端内部目录，不是同步空间；请点击“自动识别”选择“我的坚果云”。";
            if (!Directory.Exists(folderPath))
                return provider + "：目录不存在，请先在云盘客户端中创建或同步该目录。";

            try { Directory.EnumerateFileSystemEntries(folderPath).Take(1).ToList(); }
            catch (Exception exception) { return provider + "：目录当前不可访问（" + exception.Message + "）。"; }

            var processName = GetProcessName(provider);
            if (processName == null)
                return provider + "：目录可访问。云端传输由外部同步工具负责，请确认其状态正常。";

            bool running;
            try { running = Process.GetProcessesByName(processName).Any(); }
            catch { running = false; }
            return provider + "：目录可访问；" + (running ? "客户端正在运行" : "未检测到客户端进程")
                + "。最终上传状态请以客户端托盘图标为准。";
        }

        public static string ResolveUsableFolder(string folderPath)
        {
            if (!IsNutstoreInternalRoot(folderPath)) return folderPath;
            var candidates = new List<CloudSyncFolderCandidate>();
            AddNutstoreFolders(candidates, folderPath);
            return candidates.Count == 1 ? candidates[0].FolderPath : folderPath;
        }

        private static void AddKnownFolder(ICollection<CloudSyncFolderCandidate> result, string root, string name, string provider)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            Add(result, Path.Combine(root, name), provider, "常用目录");
        }

        private static void AddNutstoreFolders(ICollection<CloudSyncFolderCandidate> result, string internalRoot)
        {
            if (!Directory.Exists(internalRoot)) return;
            try
            {
                foreach (var accountRoot in Directory.GetDirectories(internalRoot)
                    .Where(path => int.TryParse(Path.GetFileName(path), out _)))
                {
                    foreach (var cloudFolder in Directory.GetDirectories(accountRoot))
                        Add(result, cloudFolder, "坚果云", "坚果云挂载目录");
                }
            }
            catch { }
        }

        private static bool IsNutstoreInternalRoot(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return false;
            try
            {
                return string.Equals(Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar)), "Nutstore", StringComparison.OrdinalIgnoreCase)
                    && (Directory.Exists(Path.Combine(folderPath, "dlcache1"))
                        || Directory.Exists(Path.Combine(folderPath, "sigs1"))
                        || Directory.Exists(Path.Combine(folderPath, "tmp1")));
            }
            catch { return false; }
        }

        private static void AddDropboxInfo(ICollection<CloudSyncFolderCandidate> result, string infoFile)
        {
            if (!File.Exists(infoFile)) return;
            try
            {
                var json = File.ReadAllText(infoFile);
                foreach (Match match in Regex.Matches(json, "\\\"path\\\"\\s*:\\s*\\\"(?<path>(?:\\\\.|[^\\\"])*)\\\""))
                {
                    var path = Regex.Unescape(match.Groups["path"].Value.Replace("\\/", "/"));
                    Add(result, path, "Dropbox", "Dropbox 配置");
                }
            }
            catch { }
        }

        private static void Add(ICollection<CloudSyncFolderCandidate> result, string folder, string provider, string source)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            result.Add(new CloudSyncFolderCandidate { ProviderName = provider, FolderPath = Path.GetFullPath(folder), Source = source });
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path ?? string.Empty; }
        }

        private static bool Contains(string value, string part)
        {
            return value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetProcessName(string provider)
        {
            if (provider == "OneDrive") return "OneDrive";
            if (provider == "Dropbox") return "Dropbox";
            if (provider == "坚果云") return "Nutstore";
            if (provider == "Syncthing") return "syncthing";
            return null;
        }
    }
}
