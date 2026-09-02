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
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
