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
            VerifyCompanyFrames(store);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void VerifyCompanyFrames(PublishPlanStore store)
    {
        var source = store.CreateProject("登记来源");
        Directory.CreateDirectory(FrameTemplatePaths.FolderFor(source.Name));
        var template = Path.Combine(FrameTemplatePaths.FolderFor(source.Name), "A3_TK.dwg");
        File.WriteAllText(template, "公司甲模板");
        var registered = new FrameDefinition
        {
            BlockName = "TK",
            DefinitionSignature = "company-a-a3",
            PaperSize = "A3",
            PaperOrientation = "横向",
            TemplateRelativePath = UserDataPaths.RelativeToRoot(template),
            TemplateSourceDrawingPath = "source.dwg",
            DefaultBuilding = "原项目子项目",
            DefaultSheetNumber = "PM-01",
            DefaultSheetName = "原项目图名",
            DefaultPageNumber = "01",
            PageNumberAttributeTag = "PAGE",
            DefaultPrintScale = "1:75",
            HasLayoutRange = true,
            LayoutLeftMargin = 20
        };
        source.Frames.Add(registered);
        store.SaveProject(source);
        var firstCompany = store.SaveCompanyFrames(" 公司甲 ", source.Frames);
        var overrideFrame = new FrameDefinition { BlockName = "TK", DefinitionSignature = "company-a-a3", PaperSize = "A3", DefaultPrintScale = "1:50" };
        var variant = new FrameDefinition { BlockName = "TK", DefinitionSignature = "company-a-a4", PaperSize = "A4", TemplateRelativePath = registered.TemplateRelativePath };
        var secondCompany = store.SaveCompanyFrames("公司乙", new[] { new FrameDefinition
            { BlockName = "TK", DefinitionSignature = "company-b", PaperSize = "A3", TemplateRelativePath = registered.TemplateRelativePath } });
        var extended = store.SaveCompanyFrames("公司甲", new[] { variant });
        Assert(extended.Id == firstCompany.Id && extended.Frames.Count == 2, "existing company must keep its identity and other variants");
        var companyFolder = Path.GetDirectoryName(FrameTemplatePaths.ExistingSnapshot(firstCompany.Frames.Single()));
        var snapshotCount = Directory.GetFiles(companyFolder, "*.dwg").Length;
        var missing = new FrameDefinition { BlockName = "缺图块", PaperSize = "A4" };
        var missingTemplateRejected = false;
        try { store.SaveCompanyFrames("无模板", new[] { registered, missing }); }
        catch (InvalidOperationException) { missingTemplateRejected = true; }
        Assert(missingTemplateRejected && !store.LoadCompanyFrameLibraries().Any(item => item.Name == "无模板")
            && Directory.GetFiles(companyFolder, "*.dwg").Length == snapshotCount, "incomplete batch left a company record or stray template");
        var projectA = store.CreateProject("使用甲");
        projectA.CompanyFrameLibraryId = firstCompany.Id;
        store.SaveProject(projectA);
        var projectB = store.CreateProject("使用乙");
        projectB.CompanyFrameLibraryId = secondCompany.Id;
        store.SaveProject(projectB);
        var legacy = store.CreateProject("没有公司图框");
        legacy.Frames.Add(overrideFrame);
        store.SaveProject(legacy);

        File.WriteAllText(template, "源项目后来修改的图框");
        File.Delete(template);
        var reload = new PublishPlanStore();
        var reloaded = reload.LoadProjects();
        projectA = reloaded.Single(project => project.Name == projectA.Name);
        projectB = reloaded.Single(project => project.Name == projectB.Name);
        var firstFrames = reload.ResolveFrames(projectA);
        var shared = firstFrames.Single(frame => frame.DefinitionSignature == registered.DefinitionSignature);
        Assert(projectA.Frames.Count == 0 && firstFrames.Count == 2 && shared.IsCompanyFrame, "company registrations were not resolved independently");
        Assert(File.ReadAllText(UserDataPaths.ResolveFromRoot(shared.TemplateRelativePath)) == "公司甲模板",
            "company template should remain available after removing the project template");
        Assert(shared.DefaultPrintScale == "1:75" && shared.PageNumberAttributeTag == "PAGE" && shared.HasLayoutRange
            && shared.LayoutLeftMargin == 20 && shared.TemplateSourceDrawingPath == null && shared.DefaultBuilding == null
            && shared.DefaultSheetName == null && shared.DefaultSheetNumber == null && shared.DefaultPageNumber == null,
            "company registration must keep reusable rules and omit source project values");
        Assert(reload.ResolveFrames(projectB).Single().DefinitionSignature == "company-b", "other company frames leaked into project");
        Assert(reload.ResolveFrames(reloaded.Single(project => project.Name == legacy.Name)).Single().DefaultPrintScale == "1:50",
            "legacy project frames changed");
        var original = reloaded.Single(project => project.Name == source.Name).Frames.Single();
        Assert(original.DefaultBuilding == registered.DefaultBuilding && original.TemplateRelativePath == registered.TemplateRelativePath,
            "saving company library changed the source project");

        projectA.Frames.Add(overrideFrame);
        var combined = reload.ResolveFrames(projectA);
        Assert(combined.Count == 2 && combined.Single(frame => frame.DefinitionSignature == registered.DefinitionSignature) == overrideFrame,
            "project registration did not take precedence over the same company variant");
        var updated = reload.SaveCompanyFrames("公司甲", new[] { overrideFrame });
        Assert(updated.Id == firstCompany.Id && updated.Frames.Count == 2
            && updated.Frames.Single(frame => frame.RegistrationId == shared.RegistrationId).TemplateRelativePath == shared.TemplateRelativePath,
            "updating the same variant without a cached template discarded its existing usable template");
        projectA.CompanyFrameLibraryId = "unavailable-company";
        Assert(reload.ResolveFrames(projectA).Single() == overrideFrame, "missing company library should fall back to project frames");
        Console.WriteLine("PASS ReusesCompanyFrameLibrariesAcrossProjects");

        projectA.CompanyFrameLibraryId = firstCompany.Id;
        var scanSources = reload.GetFrameScanSources(projectA);
        Assert(scanSources.Count == 4 && scanSources[0].CompanyId == null && scanSources[0].Name.Contains("公司甲")
            && scanSources[1].CompanyId == string.Empty, "scan source choices did not distinguish project default and project-only");
        Assert(reload.ResolveScanFrames(projectA, string.Empty).Single() == overrideFrame, "project-only scan included company frames");
        Assert(reload.ResolveScanFrames(projectA, secondCompany.Id).Single().DefinitionSignature == "company-b",
            "company-only scan included frames from the project or its default company");
        Assert(reload.ResolveScanFrames(projectA, null).Count == 3 && projectA.CompanyFrameLibraryId == firstCompany.Id,
            "a temporary scan changed the default company or discarded the shared alias record");
        var insertion = reload.ResolveInsertionFrames(legacy);
        Assert(insertion.Count == 4 && insertion.Count(frame => frame.IsCompanyFrame && frame.SelectionName.Contains("公司甲")) == 2
            && insertion.Count(frame => frame.IsCompanyFrame && frame.SelectionName.Contains("公司乙")) == 1,
            "project without a default library could not pick from every company");
        var missingCompanyRejected = false;
        try { reload.ResolveScanFrames(projectA, "unavailable-company"); }
        catch (InvalidOperationException) { missingCompanyRejected = true; }
        Assert(missingCompanyRejected, "an explicit missing company should not replace saved scan results with fallback matches");

        var recoveredFrame = insertion.Single(frame => frame.DefinitionSignature == "company-b");
        File.Delete(FrameTemplatePaths.ExistingSnapshot(recoveredFrame));
        Assert(reload.ResolveScanFrames(projectB, null).Single().RegistrationId == recoveredFrame.RegistrationId,
            "missing legacy snapshot made its registration unreadable");
        var recoveredPath = reload.SaveCompanyFrameTemplate(recoveredFrame, path => File.WriteAllText(path, "公司乙模板"));
        var restoredTemplate = reload.ResolveScanFrames(projectB, null).Single();
        Assert(restoredTemplate.TemplateRelativePath == recoveredPath
            && File.ReadAllText(UserDataPaths.ResolveFromRoot(recoveredPath)) == "公司乙模板" && projectB.Frames.Count == 0,
            "shared template recovery changed the project or failed to update the company library");
        Console.WriteLine("PASS SelectsCompanyScanScopeAndGlobalInsertionFrames");
        Console.WriteLine("PASS RequiresCompanyTemplateSnapshotAndRollsBackIncompleteBatch");

        var repairedWhilePromptWasOpen = false;
        try { reload.DeleteMissingCompanyFrame(recoveredFrame); }
        catch (InvalidOperationException) { repairedWhilePromptWasOpen = true; }
        Assert(repairedWhilePromptWasOpen && reload.ResolveScanFrames(projectB, null).Count == 1,
            "a stale delete prompt removed a company frame that was already repaired");
        File.Delete(UserDataPaths.ResolveFromRoot(recoveredPath));
        reload.DeleteMissingCompanyFrame(restoredTemplate);
        var companyAfterRemoval = reload.LoadCompanyFrameLibraries().Single(item => item.Id == secondCompany.Id);
        Assert(companyAfterRemoval.Frames.Count == 0
            && reload.LoadProjects().Single(item => item.Name == projectB.Name).CompanyFrameLibraryId == secondCompany.Id,
            "deleting the final frame changed the identity or projects selecting that library");
        Assert(reload.ResolveInsertionFrames(legacy).All(frame => frame.RegistrationId != restoredTemplate.RegistrationId)
            && reload.ResolveScanFrames(projectA, firstCompany.Id).Count == 2
            && reload.ResolveScanFrames(legacy, string.Empty).Single().BlockName == restoredTemplate.BlockName,
            "deletion did not remove exactly the selected company registration");
        Assert(File.ReadAllText(Path.Combine(companyFolder, "公司图框.json.bak")).Contains(restoredTemplate.RegistrationId),
            "previous company catalog was not retained before deletion");
        Console.WriteLine("PASS DeletesOnlyTheSelectedMissingCompanyFrame");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
