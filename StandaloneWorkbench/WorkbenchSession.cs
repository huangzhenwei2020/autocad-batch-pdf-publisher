using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace Wanluo.StandaloneWorkbench
{
    internal static class WorkbenchRuntime
    {
        public static string PackageRoot { get; set; }
        public static string ReaderPath => Path.Combine(AppContext.BaseDirectory, "读图引擎", "WanluoDrawingReader.exe");
        public static ProcessStartInfo EditorStart(string modelPath)
        {
            var executable = Path.Combine(PackageRoot, "建筑模型", "万落建筑模型.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到建筑模型编辑器，请使用完整安装包。", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(Path.GetFullPath(modelPath));
            var project = BuildingModelCatalog.InferProject(modelPath);
            if (project != null)
            {
                start.ArgumentList.Add("--cad-project");
                start.ArgumentList.Add(project);
            }
            return start;
        }
    }

    internal sealed class WorkbenchSession
    {
        private readonly PublishPlanStore _store = new PublishPlanStore();
        public ProjectProfile Project { get; private set; }
        public BuildingModelDocument Model { get; private set; }
        public string ModelPath { get; private set; }
        private string _modelHash;

        public List<ProjectProfile> Projects() => _store.LoadProjects(false);
        public string ProjectFolder => Project == null ? null : _store.GetProjectFolder(Project);

        public void SelectProject(ProjectProfile project)
        {
            Project = project ?? throw new ArgumentNullException(nameof(project));
            Model = null;
            ModelPath = null;
            _modelHash = null;
            _store.SetActiveProject(project.Name);
        }

        public ProjectProfile CreateProject(string name, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请选择项目文件夹。");
            var full = Path.GetFullPath(folder);
            var existing = Projects().FirstOrDefault(project => string.Equals(project.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null && !string.Equals(Path.GetFullPath(_store.GetProjectFolder(existing)).TrimEnd('\\'), full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("同名项目已关联其他文件夹，请调整项目名称或打开该项目。");
            var created = _store.CreateProject(name, full);
            SelectProject(created);
            return created;
        }

        public List<BuildingModelCatalogEntry> Models() => Project == null ? new List<BuildingModelCatalogEntry>() : BuildingModelCatalog.List(ProjectFolder, ModelPath);

        public void OpenModel(string path)
        {
            var hash = BuildingModelProjectWorkflow.ModelFileHash(path);
            var model = BuildingModelJson.LoadModel(path);
            if (BuildingModelProjectWorkflow.ModelFileHash(path) != hash)
                throw new IOException("模型在读取时发生变化，请重新打开。");
            Model = model;
            ModelPath = Path.GetFullPath(path);
            _modelHash = hash;
        }

        public string NewModel(string name, bool replace)
        {
            if (Project == null) throw new InvalidOperationException("请先选择项目。");
            BuildingModelProjectWorkflow.ValidateName(name);
            var path = BuildingModelJson.ModelFilePath(ProjectFolder, name);
            var model = BuildingModelProjectWorkflow.Empty(name);
            if (File.Exists(path) && replace)
                BuildingModelProjectWorkflow.ReplaceExisting(path, model, BuildingModelProjectWorkflow.ModelFileHash(path));
            else BuildingModelProjectWorkflow.CreateNew(path, model);
            BuildingModelCatalog.Register(ProjectFolder, path);
            OpenModel(path);
            return path;
        }

        public void SaveModel()
        {
            if (Model == null) throw new InvalidOperationException("请先打开或新建模型。");
            BuildingModelProjectWorkflow.SaveExisting(ModelPath, Model, _modelHash);
            _modelHash = BuildingModelProjectWorkflow.ModelFileHash(ModelPath);
        }

        public void SaveCopy(string name)
        {
            if (Model == null || Project == null) throw new InvalidOperationException("请选择项目和模型。");
            var target = BuildingModelJson.ModelFilePath(ProjectFolder, name);
            BuildingModelProjectWorkflow.CreateCopy(ModelPath, target, Model, name);
            BuildingModelCatalog.Register(ProjectFolder, target);
            OpenModel(target);
        }

        public void AddDrawing(string path)
        {
            if (Project == null) return;
            var latest = Projects().FirstOrDefault(project => project.CloudId == Project.CloudId && project.Name == Project.Name);
            if (latest == null) throw new IOException("项目已在其他窗口调整，请重新选择项目。");
            var full = Path.GetFullPath(path);
            if (!latest.CadFiles.Any(file => string.Equals(Path.GetFullPath(file), full, StringComparison.OrdinalIgnoreCase)))
            {
                latest.CadFiles.Add(full);
                _store.SaveProject(latest);
            }
            Project = latest;
        }
    }
}
