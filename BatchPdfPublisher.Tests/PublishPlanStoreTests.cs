using System;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Services;

internal static class PublishPlanStoreTests
{
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoPublishPlanStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var projectFolder = Path.Combine(root, "external-project");
            var store = new PublishPlanStore();
            var project = store.CreateProject("恢复测试", projectFolder);
            project.PlotStyle = "first.ctb";
            store.SaveProject(project);
            project.PlotStyle = "second.ctb";
            store.SaveProject(project);

            var settingsPath = UserDataPaths.SettingsFile("项目列表.json");
            File.WriteAllText(settingsPath, "{broken");
            var recovered = new PublishPlanStore().LoadProjects();
            var restored = recovered.Single(item => item.Name == "恢复测试");
            Assert(restored.PlotStyle == "first.ctb", "project backup was not restored");
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
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
