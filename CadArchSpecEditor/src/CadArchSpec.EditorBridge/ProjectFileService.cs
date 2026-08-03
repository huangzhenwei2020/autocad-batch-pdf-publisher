using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.EditorBridge
{
    public sealed class ProjectSaveResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string SavedAt { get; set; } = string.Empty;
        public string SnapshotPath { get; set; } = string.Empty;
        public IReadOnlyList<string> RecentProjects { get; set; } = Array.Empty<string>();
    }

    public sealed class ProjectLoadResult
    {
        public string FilePath { get; set; } = string.Empty;
        public JObject Workspace { get; set; } = new JObject();
        public IReadOnlyList<string> RecentProjects { get; set; } = Array.Empty<string>();
    }

    public sealed class ProjectSnapshotInfo
    {
        [JsonProperty("filePath")]
        public string FilePath { get; set; } = string.Empty;
        [JsonProperty("fileName")]
        public string FileName { get; set; } = string.Empty;
        [JsonProperty("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;
        [JsonProperty("savedAt")]
        public string SavedAt { get; set; } = string.Empty;
        [JsonProperty("projectName")]
        public string ProjectName { get; set; } = string.Empty;
        [JsonProperty("fieldChangeCount")]
        public int FieldChangeCount { get; set; }
    }

    public sealed class ProjectRestoreResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string SafetySnapshotPath { get; set; } = string.Empty;
        public JObject Workspace { get; set; } = new JObject();
        public IReadOnlyList<ProjectSnapshotInfo> Snapshots { get; set; } =
            Array.Empty<ProjectSnapshotInfo>();
    }

    public sealed class ProjectFileService
    {
        public const string ProjectExtension = ".jzsmproj";
        private const int FileVersion = 1;
        private const int MaximumRecentProjects = 8;
        private readonly string _recentProjectsPath;

        public ProjectFileService(string applicationDataDirectory = null)
        {
            var root = string.IsNullOrWhiteSpace(applicationDataDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CadArchSpecEditor")
                : applicationDataDirectory;
            Directory.CreateDirectory(root);
            _recentProjectsPath = Path.Combine(root, "recent-projects.json");
        }

        public ProjectLoadResult Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("项目文件路径不能为空。", nameof(filePath));
            }

            var normalizedPath = Path.GetFullPath(filePath);
            var root = JObject.Parse(File.ReadAllText(normalizedPath));
            var workspace = root["workspace"] as JObject;
            if (workspace == null)
            {
                // 兼容阶段1直接导出的工作区 JSON。
                workspace = root["schemaVersion"] != null ? root : null;
            }
            if (workspace == null)
            {
                throw new InvalidDataException("文件中没有可识别的建筑设计说明项目数据。");
            }

            var recent = AddRecentProject(normalizedPath);
            return new ProjectLoadResult
            {
                FilePath = normalizedPath,
                Workspace = (JObject)workspace.DeepClone(),
                RecentProjects = recent
            };
        }

        public ProjectSaveResult Save(
            string filePath,
            JObject workspace,
            bool createSnapshot)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("项目文件路径不能为空。", nameof(filePath));
            }
            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            var normalizedPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var savedAt = DateTimeOffset.Now.ToString("O");
            var snapshotPath = string.Empty;
            if (createSnapshot && File.Exists(normalizedPath))
            {
                snapshotPath = CreateSnapshot(normalizedPath);
            }

            var savedWorkspace = (JObject)workspace.DeepClone();
            savedWorkspace["lastSavedAt"] = savedAt;
            var envelope = new JObject
            {
                ["fileFormat"] = "CadArchSpecProject",
                ["fileVersion"] = FileVersion,
                ["savedAt"] = savedAt,
                ["workspace"] = savedWorkspace
            };
            File.WriteAllText(normalizedPath, envelope.ToString(Formatting.Indented));

            return new ProjectSaveResult
            {
                FilePath = normalizedPath,
                SavedAt = savedAt,
                SnapshotPath = snapshotPath,
                RecentProjects = AddRecentProject(normalizedPath)
            };
        }

        public IReadOnlyList<string> GetRecentProjects()
        {
            try
            {
                if (!File.Exists(_recentProjectsPath))
                {
                    return Array.Empty<string>();
                }

                var values = JsonConvert.DeserializeObject<List<string>>(
                    File.ReadAllText(_recentProjectsPath)) ?? new List<string>();
                var existing = values
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumRecentProjects)
                    .ToList();
                if (existing.Count != values.Count)
                {
                    WriteRecentProjects(existing);
                }
                return existing;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public IReadOnlyList<ProjectSnapshotInfo> GetSnapshots(string projectPath)
        {
            var normalizedProjectPath = NormalizeExistingProjectPath(projectPath);
            var historyDirectory = GetHistoryDirectory(normalizedProjectPath);
            if (!Directory.Exists(historyDirectory))
            {
                return Array.Empty<ProjectSnapshotInfo>();
            }

            var projectName = Path.GetFileNameWithoutExtension(normalizedProjectPath);
            return Directory
                .EnumerateFiles(historyDirectory, projectName + "-*" + ProjectExtension)
                .Select(TryReadSnapshotInfo)
                .Where(item => item != null)
                .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
                .Take(100)
                .ToList();
        }

        public ProjectLoadResult LoadSnapshot(string projectPath, string snapshotPath)
        {
            var normalizedProjectPath = NormalizeExistingProjectPath(projectPath);
            var validatedSnapshotPath = ValidateSnapshotPath(normalizedProjectPath, snapshotPath);
            return new ProjectLoadResult
            {
                FilePath = validatedSnapshotPath,
                Workspace = ReadWorkspace(validatedSnapshotPath),
                RecentProjects = GetRecentProjects()
            };
        }

        public ProjectRestoreResult RestoreSnapshot(string projectPath, string snapshotPath)
        {
            var normalizedProjectPath = NormalizeExistingProjectPath(projectPath);
            var validatedSnapshotPath = ValidateSnapshotPath(normalizedProjectPath, snapshotPath);
            // 恢复前先保存当前版本，确保误恢复仍可撤回。
            var safetySnapshotPath = CreateSnapshot(normalizedProjectPath);
            var temporaryPath = normalizedProjectPath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(validatedSnapshotPath, temporaryPath, false);
                // 先完整解析临时文件，再覆盖正式项目。
                ReadWorkspace(temporaryPath);
                File.Copy(temporaryPath, normalizedProjectPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new ProjectRestoreResult
            {
                FilePath = normalizedProjectPath,
                SafetySnapshotPath = safetySnapshotPath,
                Workspace = ReadWorkspace(normalizedProjectPath),
                Snapshots = GetSnapshots(normalizedProjectPath)
            };
        }

        private string CreateSnapshot(string projectPath)
        {
            var historyDirectory = GetHistoryDirectory(projectPath);
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            Directory.CreateDirectory(historyDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var snapshotPath = Path.Combine(
                historyDirectory,
                projectName + "-" + timestamp + ProjectExtension);
            if (File.Exists(snapshotPath))
            {
                snapshotPath = Path.Combine(
                    historyDirectory,
                    projectName + "-" + timestamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ProjectExtension);
            }
            File.Copy(projectPath, snapshotPath, false);
            return snapshotPath;
        }

        private static string GetHistoryDirectory(string projectPath)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            return Path.GetFullPath(Path.Combine(projectDirectory, "." + projectName + ".history"));
        }

        private static string NormalizeExistingProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("请先保存项目，再查看版本历史。", nameof(projectPath));
            }
            var normalizedPath = Path.GetFullPath(projectPath);
            if (!File.Exists(normalizedPath))
            {
                throw new FileNotFoundException("项目文件不存在。", normalizedPath);
            }
            return normalizedPath;
        }

        private static string ValidateSnapshotPath(string projectPath, string snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                throw new ArgumentException("快照路径不能为空。", nameof(snapshotPath));
            }
            var historyDirectory = GetHistoryDirectory(projectPath);
            var normalizedSnapshotPath = Path.GetFullPath(snapshotPath);
            var historyPrefix = historyDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!normalizedSnapshotPath.StartsWith(historyPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(normalizedSnapshotPath), ProjectExtension, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(normalizedSnapshotPath))
            {
                throw new InvalidOperationException("所选文件不是当前项目的有效历史快照。");
            }
            return normalizedSnapshotPath;
        }

        private static ProjectSnapshotInfo TryReadSnapshotInfo(string snapshotPath)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(snapshotPath));
                var workspace = root["workspace"] as JObject;
                if (workspace == null)
                {
                    return null;
                }
                return new ProjectSnapshotInfo
                {
                    FilePath = Path.GetFullPath(snapshotPath),
                    FileName = Path.GetFileName(snapshotPath),
                    CreatedAt = File.GetLastWriteTimeUtc(snapshotPath).ToString("O"),
                    SavedAt = (string)root["savedAt"] ?? string.Empty,
                    ProjectName = (string)workspace["projectName"] ?? string.Empty,
                    FieldChangeCount = (workspace["fieldChanges"] as JArray)?.Count ?? 0
                };
            }
            catch
            {
                // 单个损坏快照不影响其余历史版本。
                return null;
            }
        }

        private static JObject ReadWorkspace(string filePath)
        {
            var root = JObject.Parse(File.ReadAllText(filePath));
            var workspace = root["workspace"] as JObject;
            if (workspace == null)
            {
                workspace = root["schemaVersion"] != null ? root : null;
            }
            if (workspace == null)
            {
                throw new InvalidDataException("文件中没有可识别的建筑设计说明项目数据。");
            }
            return (JObject)workspace.DeepClone();
        }

        private IReadOnlyList<string> AddRecentProject(string filePath)
        {
            var recent = GetRecentProjects()
                .Where(path => !string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase))
                .Prepend(filePath)
                .Take(MaximumRecentProjects)
                .ToList();
            WriteRecentProjects(recent);
            return recent;
        }

        private void WriteRecentProjects(IReadOnlyCollection<string> recent)
        {
            File.WriteAllText(
                _recentProjectsPath,
                JsonConvert.SerializeObject(recent, Formatting.Indented));
        }
    }
}
