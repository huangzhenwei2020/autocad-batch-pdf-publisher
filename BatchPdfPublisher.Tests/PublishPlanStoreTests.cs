using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

internal static class PublishPlanStoreTests
{
    private static void Main()
    {
        PaperSizeCatalogTests.RunAll();
        CuixPackageBuilderTests.RunAll();
        LayerCommandLispTests.RunAll();
        RibbonPanelPlannerTests.RunAll();
        RibbonIconThemeTests.RunAll();
        DraftingLayerRolesTests.RunAll();
        var root = Path.Combine(Path.GetTempPath(), "WanluoPublishPlanStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var projectFolder = Path.Combine(root, "external-project");
            var store = new PublishPlanStore();
            var project = store.CreateProject("恢复测试", projectFolder);
            Assert(FramePageNumber.FindTag(new[] { "图号", "page-number" }) == "page-number", "page tag normalization failed");
            Assert(FramePageNumber.FindTag(new[] { "SHEETNO", "图号" }) == null, "drawing number treated as page number");
            var pageProject = store.CreateProject("页码保存", Path.Combine(root, "pages"));
            pageProject.Frames.Add(new FrameDefinition { BlockName = "TK", PaperSize = "A3", PageNumberAttributeTag = "页码", DefaultPageNumber = "01" });
            pageProject.SavedSheets.Add(new SheetCatalogItem { PageNumber = "003", SourceLayout = "布局2", SourceLayoutOrder = 2 });
            store.SaveProject(pageProject);
            var pageRestored = store.LoadProjects().Single(p => p.Name == "页码保存");
            Assert(pageRestored.Frames.Single().PageNumberAttributeTag == "页码" && pageRestored.Frames.Single().DefaultPageNumber == "01"
                && pageRestored.SavedSheets.Single().PageNumber == "003" && pageRestored.SavedSheets.Single().SourceLayoutOrder == 2, "page mapping or layout order lost on save");
            Assert(pageRestored.SheetSort == "PageNumberAscending", "default page ordering was lost");
            pageRestored.BuildingOrder = new List<string> { "八层", "四层" };
            pageRestored.CadFiles = new List<string> { "b.dwg", "a.dwg" };
            pageRestored.SheetSort = "Custom";
            store.SaveProject(pageRestored);
            var reorderedProject = store.LoadProjects().Single(p => p.Name == pageRestored.Name);
            Assert(reorderedProject.BuildingOrder.SequenceEqual(pageRestored.BuildingOrder)
                && reorderedProject.CadFiles.SequenceEqual(pageRestored.CadFiles) && reorderedProject.SheetSort == "Custom", "manual publication ordering lost on save");
            Console.WriteLine("PASS PageNumberTagAndProjectRoundtrip");
            project.PlotStyle = "first.ctb";
            project.Frames.Add(new FrameDefinition
            {
                BlockName = "旧图框", PaperSize = "A1", PaperOrientation = "横向",
                TemplateSourceDrawingPath = Path.Combine(root, "source-frame.dwg"),
                PrintScaleAttributeTag = "比例", DefaultPrintScale = "1:100",
                HasLayoutRange = true, LayoutLeftMargin = 20, Note = "原备注"
            });
            store.SaveProject(project);
            project.PlotStyle = "second.ctb";
            store.SaveProject(project);

            var settingsPath = UserDataPaths.SettingsFile("项目列表.json");
            File.WriteAllText(settingsPath, "{broken");
            var recovered = new PublishPlanStore().LoadProjects();
            var restored = recovered.Single(item => item.Name == "恢复测试");
            Assert(restored.PlotStyle == "first.ctb", "project backup was not restored");
            var restoredFrame = restored.Frames.Single();
            Assert(restoredFrame.RegistrationId == null && restoredFrame.TemplateRelativePath == null
                && restoredFrame.TemplateSourceDrawingPath == Path.Combine(root, "source-frame.dwg")
                && restoredFrame.PrintScaleAttributeTag == "比例" && restoredFrame.DefaultPrintScale == "1:100"
                && restoredFrame.HasLayoutRange && restoredFrame.LayoutLeftMargin == 20 && restoredFrame.Note == "原备注",
                "legacy frame metadata or source drawing was not preserved");
            Console.WriteLine("PASS PreservesLegacyFrameSourceAndSettings");
            Assert(Directory.GetFiles(Path.GetDirectoryName(settingsPath), "项目列表.json.corrupt-*").Length == 1,
                "corrupt project file was not quarantined");
            Assert(!string.IsNullOrWhiteSpace(PublishPlanStore.LastRecoveryNotice), "project recovery notice missing");
            Console.WriteLine("PASS RecoversCorruptProjectListFromBackup");

            var workspace = Path.Combine(root, "统一工作目录");
            var cloudSettings = new CloudSyncSettings { ProjectWorkspaceRoot = workspace, SyncProjectFiles = true };
            new CloudSyncSettingsStore().SaveSettings(cloudSettings);
            var source = Path.Combine(root, "外部项目");
            var drawing = Path.Combine(source, "CAD", "一层平面.dwg");
            Directory.CreateDirectory(Path.GetDirectoryName(drawing));
            File.WriteAllText(drawing, "drawing");
            var migrate = store.CreateProject("目录归拢测试", source);
            migrate.CadFiles.Add(drawing);
            migrate.OutputDirectory = Path.Combine(source, "PDF输出");
            store.SaveProject(migrate);
            var rejected = false;
            try { CloudProjectWorkspaceService.ValidateForProjectSync(cloudSettings, store.LoadProjects()); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "project outside workspace was not rejected");
            var consolidation = CloudProjectWorkspaceService.ConsolidateAll(store, cloudSettings);
            Assert(consolidation.Errors.Count == 0 && consolidation.MovedProjects.Contains("目录归拢测试"),
                "project consolidation failed: " + string.Join("; ", consolidation.Errors));
            var moved = store.LoadProjects().Single(item => item.Name == "目录归拢测试");
            Assert(CloudProjectWorkspaceService.IsUnderWorkspace(moved.ProjectFolder, workspace), "project was not moved under workspace");
            Assert(File.Exists(moved.CadFiles.Single()), "drawing was not copied to consolidated project");
            Assert(File.Exists(drawing), "source drawing should be retained after consolidation");
            Console.WriteLine("PASS ConsolidatesProjectsWithoutDeletingSource");

            var nestedRejected = false;
            try
            {
                CloudProjectWorkspaceService.ConsolidateAll(store, new CloudSyncSettings
                {
                    ProjectWorkspaceRoot = Path.Combine(moved.ProjectFolder, "错误的内部总目录"), SyncProjectFiles = true
                });
            }
            catch (InvalidOperationException) { nestedRejected = true; }
            Assert(nestedRejected, "workspace nested inside a source project was accepted");
            Console.WriteLine("PASS RejectsWorkspaceInsideProjectFolder");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
