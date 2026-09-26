using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.BuildingModel
{
    /// <summary>Regenerates the existing CAD-compatible view contract beside model.json.</summary>
    public static class BuildingModelViewPublisher
    {
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
    }
}
