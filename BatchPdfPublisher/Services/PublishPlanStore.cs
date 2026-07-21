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
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }

        public void SetActiveProject(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            File.WriteAllText(ActiveProjectPath(), name.Trim());
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
            var project = new ProjectProfile { Name = cleanName };
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
            using (var stream = File.Create(ProjectsPath()))
                new DataContractJsonSerializer(typeof(List<ProjectProfile>)).WriteObject(stream, projects);
        }

        private static void Normalize(List<ProjectProfile> projects)
        {
            if (projects == null) return;
            projects.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
            foreach (var project in projects)
            {
                if (project.Frames == null) project.Frames = new List<FrameDefinition>();
                if (string.IsNullOrWhiteSpace(project.PlotStyle)) project.PlotStyle = "monochrome.ctb";
                if (string.IsNullOrWhiteSpace(project.MarginMode)) project.MarginMode = "自动适配";
                if (string.IsNullOrWhiteSpace(project.OutputDirectory)) project.OutputDirectory = "D:\\PDF输出";
                if (project.FavoritePlotStyles == null) project.FavoritePlotStyles = new List<string>();
            }
            if (projects.Count == 0) projects.Add(new ProjectProfile { Name = "默认项目" });
        }

        private static List<FrameDefinition> LoadLegacyFrames()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFramesFileName);
            if (!File.Exists(path)) return new List<FrameDefinition>();
            try
            {
                using (var stream = File.OpenRead(path))
                    return (List<FrameDefinition>)new DataContractJsonSerializer(typeof(List<FrameDefinition>)).ReadObject(stream);
            }
            catch { return new List<FrameDefinition>(); }
        }

        private static string ProjectsPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProjectsFileName);
        private static string ActiveProjectPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ActiveProjectFileName);
    }
}
