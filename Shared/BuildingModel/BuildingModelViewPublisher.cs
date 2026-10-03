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
            OpeningTypeLibraryDocument library = null, Action checkCancellation = null,
            Action<int,int,string> reportProgress = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if(library?.Types?.Count>0){model=BuildingModelJson.FromJson(BuildingModelJson.ToJson(model));model.OpeningTypes=OpeningConstruction.Library(model,library).Types;}
            var definitions=DrawingViewCatalogue.Resolve(model);
            var defaultSheets=SampleModelFactory.CreateDefaultSheets(model);
            model = StandardStoreyLayout.Materialize(model);
            var views = new List<ViewDocument>();
            foreach (var definition in definitions)
            {
                checkCancellation?.Invoke();
                reportProgress?.Invoke(views.Count+1,definitions.Count,definition.Title);
                views.Add(OrthographicProjector.Project(model, definition, library));
            }
            reportProgress?.Invoke(definitions.Count,definitions.Count,"正在排版并保存图纸");
            var placed = new HashSet<string>();
            foreach (var sheet in defaultSheets)
            {
                sheet.ViewIds = sheet.ViewIds.Where(id => views.Any(v => v.Id == id)).ToList();
                if (sheet.ViewIds.Count == 0) continue;
                sheet.Number = "建施-" + (views.Count(v => v.Kind == ViewKind.Sheet) + 1).ToString("00");
                checkCancellation?.Invoke();
                views.Add(SheetComposer.ComposeModelSpace(views, sheet));
                foreach (var id in sheet.ViewIds) placed.Add(id);
            }
            foreach (var view in views.Where(v => v.Kind != ViewKind.Sheet && !placed.Contains(v.Id)).ToArray())
            {
                checkCancellation?.Invoke();
                views.Add(SheetComposer.ComposeModelSpace(views, new SheetDefinitionModel {
                    Id = "sheet-" + view.Id, Title = view.Title, Number = "建施-" + (views.Count(v => v.Kind == ViewKind.Sheet) + 1).ToString("00"),
                    ViewIds = new List<string> { view.Id } }));
            }
            return views;
        }

        public static int Publish(string modelFilePath, BuildingModelDocument model)
        {
            return PublishViews(modelFilePath, model).Count;
        }

        private static IReadOnlyList<ViewDocument> PublishViews(string modelFilePath, BuildingModelDocument model)
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
            var manifest = Path.Combine(folder, ".generated-view-ids.txt");
            var previous = File.Exists(manifest) ? File.ReadAllLines(manifest) : Array.Empty<string>();
            var current = new HashSet<string>(views.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
            if (current.Any(id => !SafeViewId(id)))
                throw new InvalidDataException("图纸编号含有无效文件名字符。");
            foreach (var view in views)
                BuildingModelJson.SaveView(Path.Combine(viewsFolder, view.Id + ".json"), view);
            foreach (var id in previous.Where(id => SafeViewId(id) && !current.Contains(id)))
                File.Delete(Path.Combine(viewsFolder, id + ".json"));
            var temporary = manifest + ".tmp";
            File.WriteAllLines(temporary, current);
            if (File.Exists(manifest)) File.Replace(temporary, manifest, null);
            else File.Move(temporary, manifest);
            return views;
        }

        private static bool SafeViewId(string id) => !string.IsNullOrWhiteSpace(id)
            && id != "." && id != ".." && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && id.IndexOf('/') < 0 && id.IndexOf('\\') < 0;

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
            var generated = PublishViews(modelFilePath, model);
            var count = generated.Count;
            var active = new HashSet<string>(generated.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
            var entries = StudioLaunch.ListViews(folder).Where(entry => active.Contains(entry.Id)).ToList();
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
