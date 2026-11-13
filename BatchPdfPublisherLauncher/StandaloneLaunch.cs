using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BatchPdfPublisherLauncher
{
    internal static class StandaloneLaunch
    {
        public static ProcessStartInfo Create(string packageRoot, string projectFolder)
        {
            var executable = Path.Combine(packageRoot, "独立工作台", "万落独立工作台.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("独立工作台组件尚未就绪，请使用完整发布包。", executable);
            var arguments = "--package-root " + Quote(Path.GetFullPath(packageRoot));
            if (!string.IsNullOrWhiteSpace(projectFolder)) arguments += " --project " + Quote(Path.GetFullPath(projectFolder));
            return new ProcessStartInfo(executable, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) };
        }

        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
