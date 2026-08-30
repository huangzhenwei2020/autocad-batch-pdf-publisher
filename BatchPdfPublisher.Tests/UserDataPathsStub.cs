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
        public static string PluginDirectory { get { return RootDirectory; } }
        public static string SettingsFile(string fileName, params string[] legacy) { return Path.Combine(Path.GetTempPath(), fileName); }
    }
}
