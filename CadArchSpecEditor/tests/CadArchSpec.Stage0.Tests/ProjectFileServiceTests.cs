using System;
using System.IO;
using CadArchSpec.EditorBridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CadArchSpec.Stage0.Tests
{
    public sealed class ProjectFileServiceTests
    {
        [Fact]
        public void SavesLoadsAndTracksAProject()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var service = new ProjectFileService(Path.Combine(root, "app-data"));
                var projectPath = Path.Combine(root, "办公楼" + ProjectFileService.ProjectExtension);
                var workspace = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["projectName"] = "办公楼"
                };

                var saved = service.Save(projectPath, workspace, false);
                var loaded = service.Load(projectPath);

                Assert.True(File.Exists(saved.FilePath));
                Assert.Equal("办公楼", (string)loaded.Workspace["projectName"]);
                Assert.Equal(projectPath, loaded.RecentProjects[0]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void CreatesSnapshotBeforeOverwriting()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var service = new ProjectFileService(Path.Combine(root, "app-data"));
                var projectPath = Path.Combine(root, "项目" + ProjectFileService.ProjectExtension);
                service.Save(projectPath, new JObject { ["projectName"] = "版本一" }, false);

                var result = service.Save(
                    projectPath,
                    new JObject { ["projectName"] = "版本二" },
                    true);

                Assert.False(string.IsNullOrWhiteSpace(result.SnapshotPath));
                Assert.True(File.Exists(result.SnapshotPath));
                Assert.Equal(
                    "版本一",
                    (string)JObject.Parse(File.ReadAllText(result.SnapshotPath))["workspace"]?["projectName"]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void PreservesArchivedReviewRecordsInProjectFile()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var service = new ProjectFileService(Path.Combine(root, "app-data"));
                var projectPath = Path.Combine(root, "审查记录" + ProjectFileService.ProjectExtension);
                var workspace = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["projectName"] = "审查记录项目",
                    ["reviewRecords"] = new JArray
                    {
                        new JObject
                        {
                            ["recordId"] = "review-1",
                            ["projectFingerprint"] = "fnv1a32-12345678",
                            ["result"] = new JObject
                            {
                                ["packageVersion"] = "0.2.0",
                                ["issues"] = new JArray()
                            }
                        }
                    }
                };

                service.Save(projectPath, workspace, false);
                var loaded = service.Load(projectPath);

                Assert.Equal(
                    "0.2.0",
                    (string)loaded.Workspace["reviewRecords"]?[0]?["result"]?["packageVersion"]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void ListsLoadsAndRestoresSnapshotsWithSafetyCopy()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var service = new ProjectFileService(Path.Combine(root, "app-data"));
                var projectPath = Path.Combine(root, "项目" + ProjectFileService.ProjectExtension);
                service.Save(projectPath, new JObject { ["projectName"] = "版本一" }, false);
                service.Save(projectPath, new JObject { ["projectName"] = "版本二" }, true);

                var snapshots = service.GetSnapshots(projectPath);
                Assert.Single(snapshots);
                Assert.Equal("版本一", service.LoadSnapshot(projectPath, snapshots[0].FilePath).Workspace["projectName"]);

                var restored = service.RestoreSnapshot(projectPath, snapshots[0].FilePath);
                Assert.Equal("版本一", restored.Workspace["projectName"]);
                Assert.True(File.Exists(restored.SafetySnapshotPath));
                Assert.Equal("版本二", service.LoadSnapshot(projectPath, restored.SafetySnapshotPath).Workspace["projectName"]);
                Assert.Equal(2, restored.Snapshots.Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void RejectsSnapshotOutsideCurrentProjectHistory()
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var service = new ProjectFileService(Path.Combine(root, "app-data"));
                var projectPath = Path.Combine(root, "项目" + ProjectFileService.ProjectExtension);
                var unrelatedPath = Path.Combine(root, "其他" + ProjectFileService.ProjectExtension);
                service.Save(projectPath, new JObject { ["projectName"] = "项目" }, false);
                service.Save(unrelatedPath, new JObject { ["projectName"] = "其他" }, false);

                Assert.Throws<InvalidOperationException>(() =>
                    service.LoadSnapshot(projectPath, unrelatedPath));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "CadArchSpecEditor.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
