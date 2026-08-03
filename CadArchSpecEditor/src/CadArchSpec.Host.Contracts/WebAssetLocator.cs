using System;
using System.IO;

namespace CadArchSpec.Host.Contracts
{
    public static class WebAssetLocator
    {
        public static string Find(string assemblyLocation)
        {
            if (string.IsNullOrWhiteSpace(assemblyLocation))
            {
                throw new ArgumentException("宿主程序集路径不能为空。", nameof(assemblyLocation));
            }

            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                throw new InvalidOperationException("无法确定宿主程序集目录。");
            }

            var assetsPath = Path.Combine(assemblyDirectory, "Web");
            var indexPath = Path.Combine(assetsPath, "index.html");
            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException(
                    "未找到编辑器静态资源。请确认 Web 文件夹与宿主 DLL 一起部署。",
                    indexPath);
            }

            return assetsPath;
        }
    }
}
