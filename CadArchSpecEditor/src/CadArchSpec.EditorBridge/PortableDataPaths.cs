using System;
using System.IO;
using System.Reflection;

namespace CadArchSpec.EditorBridge
{
    public static class PortableDataPaths
    {
        public static string Root
        {
            get
            {
                var package = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (string.IsNullOrWhiteSpace(package) || !Directory.Exists(package)) package = FindPackageRoot();
                var path = Path.Combine(package, "用户配置文件", "建筑设计说明");
                Directory.CreateDirectory(path); return path;
            }
        }
        public static string DirectoryFor(string name) { var path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
        private static string FindPackageRoot()
        {
            var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            var current = new DirectoryInfo(location);
            for (var i = 0; current != null && i < 7; i++, current = current.Parent)
            {
                var marker = Path.Combine(current.FullName, "portable-root.txt");
                try { if (File.Exists(marker)) { var root = File.ReadAllText(marker).Trim(); if (Directory.Exists(root)) return root; } } catch { }
                if (File.Exists(Path.Combine(current.FullName, "万落建筑工具启动器.exe"))) return current.FullName;
            }
            return location;
        }
    }
}
