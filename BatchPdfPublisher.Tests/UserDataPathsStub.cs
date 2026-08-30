using System.IO;

namespace BatchPdfPublisher.Services
{
    public static class UserDataPaths
    {
        public static string RootDirectory { get { return Path.GetTempPath(); } }
        public static string SettingsDirectory { get { return Path.GetTempPath(); } }
        public static string ProjectsDirectory { get { return Path.GetTempPath(); } }
        public static string FrameTemplatesDirectory { get { return Path.GetTempPath(); } }
        public static string PluginDirectory { get { return Path.GetTempPath(); } }
        public static string SettingsFile(string fileName, params string[] legacy) { return Path.Combine(Path.GetTempPath(), fileName); }
    }
}
