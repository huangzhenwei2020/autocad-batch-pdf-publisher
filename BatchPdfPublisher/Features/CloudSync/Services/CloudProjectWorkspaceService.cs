using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudProjectInfo
    {
        public string ProjectName { get; set; }
        public string CloudId { get; set; }
        public bool IsArchived { get; set; }
    }

    public sealed class ProjectConsolidationResult
    {
        public ProjectConsolidationResult()
        {
            MovedProjects = new List<string>();
            Errors = new List<string>();
        }

        public IList<string> MovedProjects { get; private set; }
        public IList<string> Errors { get; private set; }
    }

    public sealed class ProjectConsolidationPreview
    {
        public int ProjectCount { get; set; }
        public long RequiredBytes { get; set; }
        public long AvailableBytes { get; set; }
        public string RequiredText { get { return CloudProjectWorkspaceService.FormatBytes(RequiredBytes); } }
        public string AvailableText { get { return CloudProjectWorkspaceService.FormatBytes(AvailableBytes); } }
    }

    public static class CloudProjectWorkspaceService
    {
        public static string DefaultWorkspaceRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目"); }
        }

        public static string GetWorkspaceRoot(CloudSyncSettings settings = null)
        {
            if (settings == null)
            {
                try { settings = new CloudSyncSettingsStore().LoadSettings(); }
                catch { }
            }
            var configured = settings == null ? null : settings.ProjectWorkspaceRoot;
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultWorkspaceRoot : configured.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string ProjectFolderFor(CloudSyncSettings settings, string projectName)
        {
            return Path.Combine(GetWorkspaceRoot(settings), SafeName(projectName));
        }

        public static bool IsUnderWorkspace(string folder, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(workspaceRoot)) return false;
            try
            {
                var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var path = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static IList<ProjectProfile> ProjectsOutsideWorkspace(IEnumerable<ProjectProfile> projects, string workspaceRoot)
        {
            return (projects ?? Enumerable.Empty<ProjectProfile>()).Where(project => project != null &&
                !IsUnderWorkspace(project.ProjectFolder, workspaceRoot)).ToList();
        }

        public static void ValidateForProjectSync(CloudSyncSettings settings, IEnumerable<ProjectProfile> projects)
        {
            if (settings == null || !settings.SyncProjectFiles) return;
            var root = GetWorkspaceRoot(settings);
            var outside = ProjectsOutsideWorkspace(projects, root);
            if (outside.Count > 0)
                throw new InvalidOperationException("有 " + outside.Count + " 个项目不在统一工作总目录中。请在云同步设置中点击“一键统一目录”后再同步项目文件。");
            foreach (var mapping in settings.ProjectMappings ?? new List<CloudSyncProjectMapping>())
                if (mapping != null && mapping.Enabled && !IsUnderWorkspace(mapping.LocalFolder, root))
                    throw new InvalidOperationException("项目“" + mapping.ProjectName + "”的本机目录不在统一工作总目录中。");
        }

        public static ProjectConsolidationResult ConsolidateAll(PublishPlanStore store, CloudSyncSettings settings)
        {
            if (store == null) throw new ArgumentNullException("store");
            if (settings == null) throw new ArgumentNullException("settings");
            var root = GetWorkspaceRoot(settings);
            Directory.CreateDirectory(root);
            var projects = store.LoadProjects();
            AnalyzeConsolidation(projects, settings);
            var result = new ProjectConsolidationResult();
            foreach (var project in projects.Where(item => item != null && !IsUnderWorkspace(item.ProjectFolder, root)))
            {
                string target = null;
                try
                {
                    var source = Path.GetFullPath(project.ProjectFolder);
                    target = UniqueTarget(root, SafeName(project.Name));
                    if (Directory.Exists(source)) CopyDirectory(source, target);
                    else Directory.CreateDirectory(target);
                    RemapProject(project, source, target);
                    result.MovedProjects.Add(project.Name);
                }
                catch (Exception exception)
                {
                    if (!string.IsNullOrWhiteSpace(target) && IsUnderWorkspace(target, root))
                        try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { }
                    result.Errors.Add(project.Name + "：" + exception.Message);
                }
            }
            store.SaveProjects(projects);
            settings.ProjectMappings = ProjectSyncProjectionStore.BuildMappings(projects, settings.ProjectMappings, settings.ProjectWorkspaceRoot);
            var localNames = new HashSet<string>(projects.Where(item => item != null).Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in settings.ProjectMappings)
            { mapping.Enabled = localNames.Contains(mapping.ProjectName); mapping.SelectionConfirmed = true; }
            return result;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            var prefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    RejectReparsePoint(child); Directory.CreateDirectory(Path.Combine(target, child.Substring(prefix.Length))); pending.Push(child);
                }
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    RejectReparsePoint(file);
                    var destination = Path.Combine(target, file.Substring(prefix.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination, false);
                    if (!string.Equals(Hash(file), Hash(destination), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("复制后文件校验失败：" + Path.GetFileName(file));
                }
            }
        }

        public static ProjectConsolidationPreview AnalyzeConsolidation(IEnumerable<ProjectProfile> projects, CloudSyncSettings settings)
        {
            var root = GetWorkspaceRoot(settings);
            long required = 0;
            var outside = (projects ?? Enumerable.Empty<ProjectProfile>()).Where(item => item != null && !IsUnderWorkspace(item.ProjectFolder, root)).ToList();
            foreach (var project in outside)
            {
                var source = Path.GetFullPath(project.ProjectFolder);
                if (!Directory.Exists(source)) continue;
                if (PathsEqual(root, source) || IsUnderWorkspace(root, source))
                    throw new InvalidOperationException("统一工作总目录不能放在项目“" + project.Name + "”的文件夹内部。");
                required += DirectorySizeWithoutLinks(source);
            }
            var drive = new DriveInfo(Path.GetPathRoot(root));
            var reserve = Math.Max(200L * 1024 * 1024, required / 20);
            if (drive.AvailableFreeSpace < required + reserve)
                throw new IOException("统一目录空间不足，需要约 " + FormatBytes(required + reserve) + "，当前可用 " + FormatBytes(drive.AvailableFreeSpace) + "。");
            return new ProjectConsolidationPreview { ProjectCount = outside.Count, RequiredBytes = required, AvailableBytes = drive.AvailableFreeSpace };
        }

        private static long DirectorySizeWithoutLinks(string root)
        {
            long total = 0; var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)) { RejectReparsePoint(child); pending.Push(child); }
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)) { RejectReparsePoint(file); total += new FileInfo(file).Length; }
            }
            return total;
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("项目中包含链接目录或链接文件，请先改为普通文件夹：" + path);
        }

        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024d * 1024)).ToString("0.##") + " MB";
            return (bytes / 1024d).ToString("0.##") + " KB";
        }

        private static void RemapProject(ProjectProfile project, string source, string target)
        {
            project.ProjectFolder = target;
            project.OutputDirectory = Remap(project.OutputDirectory, source, target);
            project.CadFiles = Remap(project.CadFiles, source, target);
            project.SelectedCadFiles = Remap(project.SelectedCadFiles, source, target);
            foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
                sheet.SourceFile = Remap(sheet.SourceFile, source, target);
        }

        private static List<string> Remap(IEnumerable<string> paths, string source, string target)
        {
            return (paths ?? Enumerable.Empty<string>()).Select(path => Remap(path, source, target)).ToList();
        }

        private static string Remap(string path, string source, string target)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path);
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(target, full.Substring(prefix.Length)) : path;
            }
            catch { return path; }
        }

        private static string UniqueTarget(string root, string name)
        {
            var target = Path.Combine(root, name);
            if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any()) return target;
            for (var number = 2; number < 10000; number++)
            {
                target = Path.Combine(root, name + " (" + number + ")");
                if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any()) return target;
            }
            throw new IOException("无法为项目创建唯一目录。");
        }

        private static string SafeName(string value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "默认项目" : value.Trim();
            return new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        }
    }
}
