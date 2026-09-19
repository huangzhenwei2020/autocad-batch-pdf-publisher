using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 图框模板的存放约定：<c>用户配置文件\图框模板\&lt;项目名&gt;\&lt;纸张&gt;_&lt;块名&gt;.dwg</c>。
    ///
    /// 这里只做纯文件操作，**不依赖 AutoCAD**，因此主插件与启动器共用同一份实现
    /// （启动器 BatchPdfPublisherLauncher.csproj 以源码链接方式编译本文件）。
    /// 依赖 AutoCAD 的读块/写块部分留在 <see cref="FrameTemplateStore"/>。
    /// </summary>
    public static class FrameTemplatePaths
    {
        /// <summary>
        /// 把图框模板搬到与当前项目名匹配的目录，并同步改写 <c>TemplateRelativePath</c>。
        /// 项目改名后由本方法负责跟随（原来写在 FrameTemplateStore 里，逻辑逐字保留）。
        /// </summary>
        public static bool MakeReadable(IEnumerable<ProjectProfile> projects)
        {
            var changed = false;
            foreach (var project in projects ?? new ProjectProfile[0])
            foreach (var frame in project.Frames ?? new List<FrameDefinition>())
            {
                var source = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
                var destination = ReadablePath(project.Name, frame, source);
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    if (!File.Exists(destination)) File.Move(source, destination); else File.Delete(source);
                    frame.TemplateRelativePath = UserDataPaths.RelativeToRoot(destination); changed = true;
                }
                catch { }
            }
            return changed;
        }

        /// <summary>某个项目的图框模板目录（绝对路径），不保证已存在。</summary>
        public static string FolderFor(string projectName)
        {
            return Path.Combine(UserDataPaths.FrameTemplatesDirectory, SafeName(projectName, "默认项目"));
        }

        /// <summary>模板在用户配置目录内的相对路径（<c>FrameDefinition.TemplateRelativePath</c> 的形式）。</summary>
        public static string RelativeTemplatePath(string projectName, string fileName)
        {
            return Path.Combine("图框模板", SafeName(projectName, "默认项目"), fileName ?? string.Empty);
        }

        public static string ReadablePath(string projectName, FrameDefinition frame, string currentPath)
        {
            var folder = FolderFor(projectName);
            Directory.CreateDirectory(folder);
            var baseName = SafeName((frame.PaperDisplay ?? "图框") + "_" + (frame.BlockName ?? "未命名图框"), "图框");
            if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(currentPath)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(currentPath).StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) return currentPath;
            var candidate = Path.Combine(folder, baseName + ".dwg"); var version = 2;
            while (File.Exists(candidate) && !SameFile(candidate, currentPath)) candidate = Path.Combine(folder, baseName + "_版本" + version++ + ".dwg");
            return candidate;
        }

        public static bool SameFile(string left, string right) { try { return !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); } catch { return false; } }

        public static string SafeName(string value, string fallback)
        {
            var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        /// <summary>旧的模板目录里是否还有文件（图框模板目录改名前的安全检查）。</summary>
        public static bool HasFiles(string folder)
        {
            try { return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(); }
            catch { return false; }
        }
    }
}
