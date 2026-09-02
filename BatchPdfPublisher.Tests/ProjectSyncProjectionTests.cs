using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

internal static class ProjectSyncProjectionTests
{
    private static void Main()
    {
        try { Execute(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception);
            Environment.ExitCode = 1;
        }
    }

    private static void Execute()
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoProjectProjectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstDevice = Path.Combine(root, "device-a");
            var externalProject = Path.Combine(root, "external-a");
            Directory.CreateDirectory(firstDevice); Directory.CreateDirectory(externalProject);
            UserDataPaths.TestRootDirectory = firstDevice;
            var sourceDwg = Path.Combine(externalProject, "CAD", "plan.dwg");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceDwg)); File.WriteAllText(sourceDwg, "dwg");
            var project = new ProjectProfile
            {
                Name = "山水湾",
                ProjectFolder = externalProject,
                OutputDirectory = Path.Combine(externalProject, "PDF输出"),
                CadFiles = new List<string> { sourceDwg, Path.Combine(root, "unrelated.dwg") },
                SelectedCadFiles = new List<string> { sourceDwg },
                SavedSheets = new List<SheetCatalogItem> { new SheetCatalogItem { SheetName = "平面", SourceFile = sourceDwg } }
            };
            ProjectSyncProjectionStore.Export(new[] { project });
            var projection = Directory.GetFiles(ProjectSyncProjectionStore.ProjectionDirectory, "项目.json", SearchOption.AllDirectories).Single();
            var json = File.ReadAllText(projection);
            Assert(!json.Contains(externalProject), "projection leaked the first device absolute path");
            Assert(json.Contains("$PROJECT$") && json.Contains("plan.dwg"), "project-relative DWG token missing");

            var secondDevice = Path.Combine(root, "device-b");
            UserDataPaths.TestRootDirectory = secondDevice;
            var secondProjection = Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, Path.GetFileName(Path.GetDirectoryName(projection)), "项目.json");
            Directory.CreateDirectory(Path.GetDirectoryName(secondProjection)); File.Copy(projection, secondProjection);
            var imported = new List<ProjectProfile>();
            Assert(!ProjectSyncProjectionStore.MergeInto(imported), "unselected cloud project should not be imported");
            new CloudSyncSettingsStore().SaveSettings(new CloudSyncSettings
            {
                ProjectMappings = new List<CloudSyncProjectMapping>
                {
                    new CloudSyncProjectMapping
                    {
                        ProjectName = project.Name,
                        CloudId = Path.GetFileName(Path.GetDirectoryName(projection)),
                        LocalFolder = CloudProjectWorkspaceService.ProjectFolderFor(null, project.Name),
                        Enabled = true
                    }
                }
            });
            Assert(ProjectSyncProjectionStore.MergeInto(imported), "projection was not imported");
            var restored = imported.Single();
            Assert(restored.ProjectFolder.StartsWith(CloudProjectWorkspaceService.GetWorkspaceRoot(), StringComparison.OrdinalIgnoreCase), "second device mapping was not in workspace root");
            Assert(restored.CadFiles.Single().EndsWith(Path.Combine("CAD", "plan.dwg"), StringComparison.OrdinalIgnoreCase), "relative DWG was not restored");
            Assert(!restored.CadFiles.Any(path => path.Contains("unrelated.dwg")), "external machine path should not cross devices");
            var cloudId = Path.GetFileName(Path.GetDirectoryName(projection));
            ProjectSyncProjectionStore.SetCloudProjectArchived(cloudId, true);
            Assert(ProjectSyncProjectionStore.DiscoverCloudProjects().Count == 0, "archived project remained in the normal cloud list");
            Assert(ProjectSyncProjectionStore.DiscoverCloudProjects(true).Single().IsArchived, "archived project was not available for recovery");
            Assert(!ProjectSyncProjectionStore.BuildMappings(imported, new[] { new CloudSyncProjectMapping { ProjectName = project.Name, CloudId = cloudId, Enabled = true } })
                .Single(mapping => mapping.ProjectName == project.Name).Enabled,
                "archived project remained enabled for file synchronization");
            ProjectSyncProjectionStore.SetCloudProjectArchived(cloudId, false);
            Assert(ProjectSyncProjectionStore.DiscoverCloudProjects().Count == 1, "restored project did not return to the cloud list");
            Console.WriteLine("PASS PortableProjectProjection");
            Console.WriteLine("PASS ImportsOnlySelectedCloudProject");
            Console.WriteLine("PASS ArchivesCloudProjectsWithoutDeletingFiles");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
