using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Regenerates the existing CAD-compatible view contract beside model.json.</summary>
    public static class BuildingModelViewPublisher
    {
        public sealed class CadPublishResult
        {
            public int ViewCount { get; set; }
            public int PendingCount { get; set; }
            public string PendingFilePath { get; set; }
        }

        public static IReadOnlyList<ViewDocument> Generate(BuildingModelDocument model,
            OpeningTypeLibraryDocument library = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var views = new List<ViewDocument>();
            foreach (var definition in SampleModelFactory.CreateDefaultViews(model.Name))
                views.Add(OrthographicProjector.Project(model, definition, library));
            foreach (var storey in (model.Storeys ?? new List<StoreyModel>()).Where(s => s != null))
                views.Add(OrthographicProjector.Project(model, SampleModelFactory.CreatePlanView(storey), library));
            views.Add(OrthographicProjector.ProjectSchedule(model, library, "门窗表"));
            foreach (var sheet in SampleModelFactory.CreateDefaultSheets(model))
                views.Add(SheetComposer.Compose(views, sheet));
            return views;
        }

        public static int Publish(string modelFilePath, BuildingModelDocument model)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath))
                throw new ArgumentException("模型文件路径不能为空。", nameof(modelFilePath));
            if (!string.Equals(Path.GetFileName(modelFilePath), "model.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("要供 CAD 读取，请先将模型保存为 model.json。");
            var folder = Path.GetDirectoryName(Path.GetFullPath(modelFilePath));
            if (string.IsNullOrWhiteSpace(folder)) throw new IOException("模型目录无效。");
            var libraryPath = Path.Combine(folder, "openings.json");
            var library = File.Exists(libraryPath) ? BuildingModelJson.LoadOpeningLibrary(libraryPath) : null;
            var views = Generate(model, library);
            var viewsFolder = Path.Combine(folder, StudioLaunch.ViewsFolderName);
            foreach (var view in views)
                BuildingModelJson.SaveView(Path.Combine(viewsFolder, view.Id + ".json"), view);
            return views.Count;
        }

        /// <summary>Generate views and mark sheets (or all views when no sheets exist) for LTTZ.</summary>
        public static CadPublishResult PublishToCad(string modelFilePath, BuildingModelDocument model)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath))
                throw new ArgumentException("模型文件路径不能为空。", nameof(modelFilePath));
            var folder = Path.GetDirectoryName(Path.GetFullPath(modelFilePath));
            var modelsRoot = Path.GetDirectoryName(folder);
            if (!string.Equals(Path.GetFileName(modelsRoot), StudioLaunch.ModelFolderName,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先保存到 <项目>/建筑模型/<模型名称>/model.json，CAD 才能定位待落图文件。");
            var count = Publish(modelFilePath, model);
            var entries = StudioLaunch.ListViews(folder);
            if (entries.Count < count)
                throw new IOException("部分生成视图未被 CAD 取件清单识别。");
            var sheets = entries.Where(entry => entry.Kind == ViewKind.Sheet).ToList();
            var marked = sheets.Count > 0 ? sheets : entries;
            if (!StudioLaunch.WritePending(folder, marked.Select(entry => new StudioPendingEntry
                { Id = entry.Id, FilePath = entry.FilePath })))
                throw new IOException("写入 CAD 待落图清单失败。");
            return new CadPublishResult
            {
                ViewCount = count,
                PendingCount = marked.Count,
                PendingFilePath = StudioLaunch.PendingFilePath(folder)
            };
        }
    }
}
