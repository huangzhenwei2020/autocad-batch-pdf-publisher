using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PublishPlanStore
    {
        public static event Action FramesChanged;
        private static readonly object FileWriteSync = new object();
        private const string ProjectsFileName = "BatchPdfPublisher.projects.json";
        private const string ActiveProjectFileName = "BatchPdfPublisher.active-project.txt";
        private const string LegacyFramesFileName = "BatchPdfPublisher.frames.json";

        public List<ProjectProfile> LoadProjects()
        {
            var path = ProjectsPath();
            if (File.Exists(path))
            {
                try
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var projects = (List<ProjectProfile>)new DataContractJsonSerializer(typeof(List<ProjectProfile>)).ReadObject(stream);
                        Normalize(projects);
                        return projects;
                    }
                }
                catch
                {
                    // A malformed project file should not prevent the plugin from opening.
                }
            }

            var migrated = new ProjectProfile { Name = "默认项目", Frames = LoadLegacyFrames() };
            var defaults = new List<ProjectProfile> { migrated };
            SaveProjects(defaults);
            SetActiveProject(migrated.Name);
            return defaults;
        }

        public string LoadActiveProjectName()
        {
            var path = ActiveProjectPath();
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty; }
            catch { return string.Empty; }
        }

        public void SetActiveProject(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            WriteTextAtomically(ActiveProjectPath(), name.Trim());
        }

        public ProjectProfile GetActiveProject()
        {
            var projects = LoadProjects();
            var activeName = LoadActiveProjectName();
            return projects.FirstOrDefault(x => string.Equals(x.Name, activeName, StringComparison.OrdinalIgnoreCase)) ?? projects[0];
        }

        public ProjectProfile CreateProject(string name)
        {
            var cleanName = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanName)) throw new ArgumentException("项目名称不能为空。", nameof(name));
            var projects = LoadProjects();
            var existing = projects.FirstOrDefault(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var project = new ProjectProfile { Name = cleanName, ProjectFolder = DefaultProjectFolder(cleanName) };
            projects.Add(project);
            SaveProjects(projects);
            SetActiveProject(cleanName);
            return project;
        }

        public void SaveProject(ProjectProfile project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Name)) return;
            var projects = LoadProjects();
            var index = projects.FindIndex(x => string.Equals(x.Name, project.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) projects.Add(project);
            else projects[index] = project;
            SaveProjects(projects);
            SetActiveProject(project.Name);
        }

        public bool DeleteProject(string name)
        {
            var cleanName = (name ?? string.Empty).Trim();
            var projects = LoadProjects();
            if (projects.Count <= 1) return false;
            var removed = projects.RemoveAll(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveProjects(projects);
            var active = LoadActiveProjectName();
            if (string.Equals(active, cleanName, StringComparison.OrdinalIgnoreCase)) SetActiveProject(projects[0].Name);
            return true;
        }

        public string GetProjectFolder(ProjectProfile project)
        {
            if (project == null) return string.Empty;
            if (string.IsNullOrWhiteSpace(project.ProjectFolder)) project.ProjectFolder = DefaultProjectFolder(project.Name);
            return project.ProjectFolder;
        }

        public List<FrameDefinition> LoadFrames()
        {
            var project = GetActiveProject();
            return project.Frames ?? new List<FrameDefinition>();
        }

        public void SaveFrames(List<FrameDefinition> frames)
        {
            var project = GetActiveProject();
            project.Frames = frames ?? new List<FrameDefinition>();
            SaveProject(project);
            FramesChanged?.Invoke();
        }

        private void SaveProjects(List<ProjectProfile> projects)
        {
            Normalize(projects);
            var path = ProjectsPath();
            WriteAtomically(path, stream =>
                new DataContractJsonSerializer(typeof(List<ProjectProfile>)).WriteObject(stream, projects));
        }

        private static void WriteTextAtomically(string path, string value)
        {
            WriteAtomically(path, stream =>
            {
                // leaveOpen=true：WriteAtomically 需要在委托返回后对同一个流 Flush(true)。
                // 默认 StreamWriter 会连底层 FileStream 一起关闭，导致 BPP 构造阶段报
                // “无法访问已关闭的文件”。
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, true)) writer.Write(value ?? string.Empty);
            });
        }

        private static void WriteAtomically(string path, Action<Stream> write)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("项目配置文件路径无效。");
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("项目配置文件目录无效。");
            Directory.CreateDirectory(directory);

            lock (FileWriteSync)
            {
                var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        write(stream);
                        stream.Flush(true);
                    }
                    if (new FileInfo(temporary).Length == 0) throw new IOException("项目配置暂存文件为空。");

                    if (!File.Exists(path))
                    {
                        File.Move(temporary, path);
                        return;
                    }

                    var backup = path + ".bak";
                    try
                    {
                        File.Replace(temporary, path, backup, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithRollback(temporary, path, backup);
                    }
                    catch (IOException)
                    {
                        ReplaceWithRollback(temporary, path, backup);
                    }
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private static void ReplaceWithRollback(string temporary, string path, string backup)
        {
            var rollback = backup + "." + Guid.NewGuid().ToString("N") + ".rollback";
            File.Copy(path, rollback, true);
            try
            {
                File.Copy(temporary, path, true);
                File.Copy(path, backup, true);
            }
            catch
            {
                try { if (File.Exists(rollback)) File.Copy(rollback, path, true); } catch { }
                throw;
            }
            finally
            {
                try { if (File.Exists(rollback)) File.Delete(rollback); } catch { }
            }
        }

        private static void Normalize(List<ProjectProfile> projects)
        {
            if (projects == null) return;
            projects.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
            foreach (var project in projects)
            {
                if (string.IsNullOrWhiteSpace(project.ProjectFolder)) project.ProjectFolder = DefaultProjectFolder(project.Name);
                if (project.Frames == null) project.Frames = new List<FrameDefinition>();
                if (string.IsNullOrWhiteSpace(project.PlotStyle)) project.PlotStyle = "monochrome.ctb";
                if (string.IsNullOrWhiteSpace(project.MarginMode)) project.MarginMode = "自动适配";
                if (string.IsNullOrWhiteSpace(project.OutputDirectory)) project.OutputDirectory = Path.Combine(project.ProjectFolder, "PDF输出");
                if (project.FavoritePlotStyles == null) project.FavoritePlotStyles = new List<string>();
                if (project.SavedSheets == null) project.SavedSheets = new List<SheetCatalogItem>();
                if (project.CadFiles == null) project.CadFiles = new List<string>();
                if (project.SelectedCadFiles == null) project.SelectedCadFiles = new List<string>();
                if (project.SelectedPublishBuildings == null) project.SelectedPublishBuildings = new List<string>();
                if (project.SelectedLayouts == null) project.SelectedLayouts = new List<string>();
            }
            if (projects.Count == 0) projects.Add(new ProjectProfile { Name = "默认项目" });
        }

        private static List<FrameDefinition> LoadLegacyFrames()
        {
            var path = UserDataPaths.SettingsFile(LegacyFramesFileName, LegacyFramesFileName);
            if (!File.Exists(path)) return new List<FrameDefinition>();
            try
            {
                using (var stream = File.OpenRead(path))
                    return (List<FrameDefinition>)new DataContractJsonSerializer(typeof(List<FrameDefinition>)).ReadObject(stream);
            }
            catch { return new List<FrameDefinition>(); }
        }

        private static string ProjectsPath() => UserDataPaths.SettingsFile(ProjectsFileName, ProjectsFileName);
        private static string ActiveProjectPath() => UserDataPaths.SettingsFile(ActiveProjectFileName, ActiveProjectFileName);
        private static string DefaultProjectFolder(string projectName)
        {
            var name = string.IsNullOrWhiteSpace(projectName) ? "默认项目" : projectName.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return Path.Combine(UserDataPaths.ProjectsDirectory, name);
        }
    }
}
