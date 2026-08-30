using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Collections.Generic;
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
                return Path.Combine(PortableRoot, "项目", SafeName(ActiveProjectName),
                    "最近使用方案.json");
            }
        }

        public string ActiveProjectName
        {
            get { return TryGetActiveProjectName() ?? "默认项目"; }
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
                        if (project != null) return BindToActiveProject(project);
                    }
                    // One-time, non-destructive migration from the historical
                    // C-drive location. The old file remains as a recovery copy.
                    var projectRoot = Path.Combine(PortableRoot, "项目");
                    var hasMigratedProject = Directory.Exists(projectRoot)
                        && Directory.GetFiles(projectRoot, "最近使用方案.json",
                            SearchOption.AllDirectories).Length > 0;
                    var legacyPath = hasMigratedProject ? null
                        : new[] { LegacyPortableFilePath, LegacyFilePath }
                            .FirstOrDefault(path => !string.Equals(FilePath, path,
                                StringComparison.OrdinalIgnoreCase) && File.Exists(path));
                    if (!string.IsNullOrWhiteSpace(legacyPath))
                    {
                        var project = _serializer.Deserialize<StairProjectDefinition>(
                            File.ReadAllText(legacyPath));
                        if (project != null)
                        {
                            BindToActiveProject(project);
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
            return BindToActiveProject(StairProjectDefinition.CreateDefault());
        }

        public void Save(StairProjectDefinition project)
        {
            if (project == null) return;
            BindToActiveProject(project);
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

        public IList<string> LoadSchemeNames()
        {
            try
            {
                lock (FileSync)
                {
                    if (!Directory.Exists(SchemeDirectory)) return new List<string>();
                    return Directory.GetFiles(SchemeDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
            }
            catch { return new List<string>(); }
        }

        public void SaveScheme(string name, StairProjectDefinition project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var schemeName = SafeSchemeName(name);
            lock (FileSync)
            {
                if (!Directory.Exists(SchemeDirectory)) Directory.CreateDirectory(SchemeDirectory);
                var copy = _serializer.Deserialize<StairProjectDefinition>(_serializer.Serialize(project));
                // CAD handles, cropped plan caches and their source drawings are
                // project-local evidence, not reusable stair parameters.
                copy.PlanSources = new List<StairPlanSourceDefinition>();
                WriteAtomically(SchemePath(schemeName), _serializer.Serialize(copy));
            }
        }

        public StairProjectDefinition LoadScheme(string name)
        {
            var path = SchemePath(SafeSchemeName(name));
            lock (FileSync)
            {
                if (!File.Exists(path)) return null;
                var project = _serializer.Deserialize<StairProjectDefinition>(File.ReadAllText(path));
                if (project == null) return null;
                project.PlanSources = new List<StairPlanSourceDefinition>();
                return BindToActiveProject(project);
            }
        }

        public void DeleteScheme(string name)
        {
            var path = SchemePath(SafeSchemeName(name));
            lock (FileSync)
                if (File.Exists(path)) File.Delete(path);
        }

        private static string PortableRoot
        {
            get
            {
                var packageRoot = Environment.GetEnvironmentVariable(
                    "WANLUO_ARCHITECTURE_TOOLS_ROOT");
                if (!string.IsNullOrWhiteSpace(packageRoot))
                    return Path.Combine(packageRoot, "用户配置文件", "楼梯大样");
                return Path.GetDirectoryName(LegacyFilePath);
            }
        }

        private static string LegacyPortableFilePath
        {
            get { return Path.Combine(PortableRoot, "最近使用方案.json"); }
        }

        private static string SchemeDirectory
        {
            get { return Path.Combine(PortableRoot, "方案库"); }
        }

        private static string SchemePath(string name)
        {
            return Path.Combine(SchemeDirectory, name + ".json");
        }

        private static string SafeSchemeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("方案名称不能为空。");
            var result = value.Trim();
            foreach (var character in Path.GetInvalidFileNameChars())
                result = result.Replace(character, '_');
            result = result.Trim('.', ' ');
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("方案名称无效。");
            return result.Length > 80 ? result.Substring(0, 80) : result;
        }

        private static void WriteAtomically(string path, string contents)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, contents);
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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

        private StairProjectDefinition BindToActiveProject(StairProjectDefinition project)
        {
            if (project != null) project.ProjectName = ActiveProjectName;
            return project;
        }

        private static string TryGetActiveProjectName()
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(
                        "BatchPdfPublisher.Services.PublishPlanStore", false))
                    .FirstOrDefault(value => value != null);
                if (type == null) return null;
                var store = Activator.CreateInstance(type);
                var project = type.GetMethod("GetActiveProject").Invoke(store, null);
                var property = project == null ? null : project.GetType().GetProperty("Name");
                var name = property == null ? null : property.GetValue(project, null) as string;
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch { return null; }
        }

        private static string SafeName(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? "默认项目" : value.Trim();
            foreach (var character in Path.GetInvalidFileNameChars())
                result = result.Replace(character, '_');
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }
    }
}
