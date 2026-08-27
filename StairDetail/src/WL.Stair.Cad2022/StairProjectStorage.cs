using System;
using System.IO;
using System.Web.Script.Serialization;
using WL.Stair.Core.Domain;

namespace WL.Stair.Cad2022
{
    internal sealed class StairProjectStorage
    {
        private static readonly object FileSync = new object();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public string FilePath
        {
            get
            {
                var packageRoot = Environment.GetEnvironmentVariable("WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (!string.IsNullOrWhiteSpace(packageRoot))
                    return Path.Combine(packageRoot, "用户配置文件", "楼梯大样", "最近使用方案.json");
                return LegacyFilePath;
            }
        }

        private string LastLayoutFramePath
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(FilePath),
                    "上次排版图框.txt");
            }
        }

        private static string LegacyFilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WanLuoArchitecture",
                    "StairDesigner",
                    "last-project.json");
            }
        }

        public StairProjectDefinition LoadOrDefault()
        {
            try
            {
                lock (FileSync)
                {
                    if (File.Exists(FilePath))
                    {
                        var project = _serializer.Deserialize<StairProjectDefinition>(File.ReadAllText(FilePath));
                        if (project != null) return project;
                    }
                    // One-time, non-destructive migration from the historical
                    // C-drive location. The old file remains as a recovery copy.
                    if (!string.Equals(FilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(LegacyFilePath))
                    {
                        var project = _serializer.Deserialize<StairProjectDefinition>(
                            File.ReadAllText(LegacyFilePath));
                        if (project != null)
                        {
                            Save(project);
                            return project;
                        }
                    }
                }
            }
            catch
            {
                // A damaged local preset must not prevent the editor from opening.
            }
            return StairProjectDefinition.CreateDefault();
        }

        public void Save(StairProjectDefinition project)
        {
            if (project == null) return;
            lock (FileSync)
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                // Write beside the target and replace it in one filesystem
                // operation. A power loss must not leave a half-written preset.
                var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporaryPath, _serializer.Serialize(project));
                    if (File.Exists(FilePath))
                        File.Replace(temporaryPath, FilePath, null);
                    else
                        File.Move(temporaryPath, FilePath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
        }

        public string LoadLastLayoutFrameId()
        {
            try
            {
                lock (FileSync)
                    return File.Exists(LastLayoutFramePath)
                        ? File.ReadAllText(LastLayoutFramePath).Trim()
                        : string.Empty;
            }
            catch { return string.Empty; }
        }

        public void SaveLastLayoutFrameId(string registrationId)
        {
            if (string.IsNullOrWhiteSpace(registrationId)) return;
            lock (FileSync)
            {
                var directory = Path.GetDirectoryName(LastLayoutFramePath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(LastLayoutFramePath, registrationId.Trim());
            }
        }
    }
}
