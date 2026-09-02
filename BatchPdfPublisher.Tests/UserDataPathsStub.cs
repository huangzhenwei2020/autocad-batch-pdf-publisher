using System.IO;

namespace BatchPdfPublisher.Services
{
    public static class UserDataPaths
    {
        public static string TestRootDirectory { get; set; }
        public static string RootDirectory { get { return string.IsNullOrWhiteSpace(TestRootDirectory) ? Path.GetTempPath() : TestRootDirectory; } }
        public static string SettingsDirectory { get { return Path.Combine(RootDirectory, "通用设置"); } }
        public static string ProjectsDirectory { get { return Path.Combine(RootDirectory, "项目配置"); } }
        public static string FrameTemplatesDirectory { get { return Path.Combine(RootDirectory, "图框模板"); } }
        public static string LogsDirectory { get { var path = Path.Combine(RootDirectory, "Logs"); Directory.CreateDirectory(path); return path; } }
        public static string PluginDirectory { get { return RootDirectory; } }
        public static string SettingsFile(string fileName, params string[] legacy) { Directory.CreateDirectory(SettingsDirectory); return Path.Combine(SettingsDirectory, fileName); }
    }

    public static class CloudSyncCoordinator
    {
        public static void Reload() { }
        public static void QueueReload(bool synchronizeAfterReload) { }
        public static void RequestSynchronization(bool immediate) { }
    }
}
