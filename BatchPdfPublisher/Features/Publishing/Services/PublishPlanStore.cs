using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    public sealed class PublishPlanStore
    {
        public static event Action FramesChanged;
        public static string LastRecoveryNotice { get; private set; }
        private static readonly object FileWriteSync = new object();
        private const string ProjectsFileName = "项目列表.json";
        private const string ActiveProjectFileName = "当前项目.txt";
        private const string LegacyFramesFileName = "BatchPdfPublisher.frames.json";
        private const string CompanyFrameFolder = "公司图框库";

        public List<ProjectProfile> LoadProjects()
        {
            return LoadProjects(true);
        }

        public List<ProjectProfile> LoadProjects(bool persistNormalization)
        {
            var path = ProjectsPath();
            if (File.Exists(path))
            {
                try
                {
                    var projects = ReadProjects(path);
                    var normalizedProjectFolders = projects != null && projects.Any(x => x != null && NeedsProjectFolderNormalization(x.ProjectFolder));
                    var normalizedFrameDirections = Normalize(projects);
                    var projected = ProjectSyncProjectionStore.MergeInto(projects);
                    var framePathsChanged = persistNormalization && FrameTemplatePaths.MakeReadable(projects);
                    if (persistNormalization && (framePathsChanged || normalizedProjectFolders || normalizedFrameDirections || projected)) SaveProjects(projects);
                    return projects;
                }
                catch (Exception primaryFailure)
                {
                    var backup = path + ".bak";
                    try
                    {
                        if (File.Exists(backup))
                        {
                            var recovered = ReadProjects(backup);
                            Quarantine(path);
                            WriteAtomically(path, stream =>
                                new DataContractJsonSerializer(typeof(List<ProjectProfile>)).WriteObject(stream, recovered));
                            ReportRecovery("项目配置已从备份恢复：" + path);
                            Normalize(recovered);
                            ProjectSyncProjectionStore.MergeInto(recovered);
                            return recovered;
                        }
                    }
                    catch (Exception backupFailure)
                    {
                        ReportRecovery("项目配置和备份都无法读取：" + primaryFailure.Message + "；" + backupFailure.Message);
                    }
                    Quarantine(path);
                    ReportRecovery("项目配置无法读取，已保留损坏文件并建立临时默认项目：" + primaryFailure.Message);
                }
            }

            var migrated = new ProjectProfile { Name = "默认项目", Frames = LoadLegacyFrames() };
            var defaults = new List<ProjectProfile> { migrated };
            ProjectSyncProjectionStore.MergeInto(defaults);
            if (persistNormalization)
            {
                SaveProjects(defaults);
                SetActiveProject(migrated.Name);
            }
            return defaults;
        }

        public string LoadActiveProjectName()
        {
            var path = ActiveProjectPath();
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty; }
            catch { return string.Empty; }
        }

        public void SetActiveProject(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            WriteTextAtomically(ActiveProjectPath(), name.Trim());
        }

        public ProjectProfile GetActiveProject()
        {
            var projects = LoadProjects();
            var activeName = LoadActiveProjectName();
            return projects.FirstOrDefault(x => string.Equals(x.Name, activeName, StringComparison.OrdinalIgnoreCase)) ?? projects[0];
        }

        public ProjectProfile CreateProject(string name, string projectFolder = null)
        {
            var cleanName = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleanName)) throw new ArgumentException("项目名称不能为空。", nameof(name));
            var projects = LoadProjects();
            var existing = projects.FirstOrDefault(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var project = new ProjectProfile { Name = cleanName, ProjectFolder = ResolveProjectFolder(projectFolder, cleanName) };
            Directory.CreateDirectory(project.ProjectFolder);
            projects.Add(project);
            SaveProjects(projects);
            SetActiveProject(cleanName);
            return project;
        }

        public void SaveProject(ProjectProfile project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.Name)) return;
            var projects = LoadProjects();
            var index = projects.FindIndex(x => string.Equals(x.Name, project.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) projects.Add(project);
            else projects[index] = project;
            SaveProjects(projects);
            SetActiveProject(project.Name);
        }

        public bool DeleteProject(string name)
        {
            var cleanName = (name ?? string.Empty).Trim();
            var projects = LoadProjects();
            if (projects.Count <= 1) return false;
            var removed = projects.RemoveAll(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveProjects(projects);
            var active = LoadActiveProjectName();
            if (string.Equals(active, cleanName, StringComparison.OrdinalIgnoreCase)) SetActiveProject(projects[0].Name);
            return true;
        }

        public string GetProjectFolder(ProjectProfile project)
        {
            if (project == null) return string.Empty;
            if (string.IsNullOrWhiteSpace(project.ProjectFolder)) project.ProjectFolder = DefaultProjectFolder(project.Name);
            return project.ProjectFolder;
        }

        public List<FrameDefinition> LoadFrames()
        {
            var project = GetActiveProject();
            return project.Frames ?? new List<FrameDefinition>();
        }

        public List<FrameDefinition> LoadEffectiveFrames() => ResolveFrames(GetActiveProject());

        public List<FrameDefinition> LoadInsertionFrames() => ResolveInsertionFrames(GetActiveProject());

        public List<FrameDefinition> ResolveInsertionFrames(ProjectProfile project)
        {
            var frames = RegisteredProjectFrames(project);
            return frames.Concat(LoadCompanyFrameLibraries()
                .OrderByDescending(company => company.Id == project?.CompanyFrameLibraryId)
                .ThenBy(company => company.Name)
                .SelectMany(company => company.Frames)).ToList();
        }

        public List<FrameScanSource> GetFrameScanSources(ProjectProfile project)
        {
            var companies = LoadCompanyFrameLibraries();
            var preferred = companies.FirstOrDefault(company => company.Id == project?.CompanyFrameLibraryId);
            var choices = new List<FrameScanSource>
            {
                new FrameScanSource(null, preferred == null ? "跟随项目设置" : "项目设置 · " + preferred.Name),
                new FrameScanSource(string.Empty, "仅扫描本项目图框")
            };
            choices.AddRange(companies.OrderBy(company => company.Name)
                .Select(company => new FrameScanSource(company.Id, "仅扫描 · " + company.Name)));
            return choices;
        }

        public List<FrameDefinition> ResolveScanFrames(ProjectProfile project, string companyId)
        {
            if (companyId == null)
            {
                var frames = RegisteredProjectFrames(project);
                if (string.IsNullOrWhiteSpace(project?.CompanyFrameLibraryId)) return frames;
                var preferred = LoadCompanyFrameLibraries().FirstOrDefault(item => item.Id == project.CompanyFrameLibraryId);
                // Keep shared records available for their DWG aliases even when a
                // project registration overrides the shared attribute mappings.
                if (preferred != null) frames.AddRange(preferred.Frames);
                return frames;
            }
            if (companyId.Length == 0) return RegisteredProjectFrames(project);
            var company = LoadCompanyFrameLibraries().FirstOrDefault(item => item.Id == companyId);
            if (company == null) throw new InvalidOperationException("所选公司图框库不存在，请重新选择扫描图框范围。");
            return company.Frames.ToList();
        }

        private static List<FrameDefinition> RegisteredProjectFrames(ProjectProfile project) =>
            (project?.Frames ?? new List<FrameDefinition>())
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)).ToList();

        public List<FrameDefinition> ResolveFrames(ProjectProfile project)
        {
            var frames = RegisteredProjectFrames(project);
            if (string.IsNullOrWhiteSpace(project?.CompanyFrameLibraryId)) return frames;
            var company = LoadCompanyFrameLibraries().FirstOrDefault(item => item.Id == project.CompanyFrameLibraryId);
            if (company == null) return frames;
            frames.AddRange(company.Frames.Where(shared => !frames.Any(local => SameFrameVariant(local, shared))));
            return frames;
        }

        public List<CompanyFrameLibrary> LoadCompanyFrameLibraries()
        {
            var path = Path.Combine(UserDataPaths.RootDirectory, CompanyFrameFolder, "公司图框.json");
            if (!File.Exists(path)) return new List<CompanyFrameLibrary>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var libraries = (List<CompanyFrameLibrary>)new DataContractJsonSerializer(typeof(List<CompanyFrameLibrary>)).ReadObject(stream);
                foreach (var library in libraries)
                    foreach (var frame in library.Frames)
                    {
                        frame.IsCompanyFrame = true;
                        frame.CompanyName = library.Name;
                    }
                return libraries;
            }
        }

        public CompanyFrameLibrary SaveCompanyFrames(string companyName, IEnumerable<FrameDefinition> sourceFrames)
        {
            var name = (companyName ?? string.Empty).Trim();
            if (name.Length == 0) throw new ArgumentException("请输入公司名称。", nameof(companyName));
            var selected = (sourceFrames ?? Enumerable.Empty<FrameDefinition>())
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)).ToList();
            if (selected.Count == 0) throw new ArgumentException("没有可保存的项目图框。", nameof(sourceFrames));

            var folder = Path.Combine(UserDataPaths.RootDirectory, CompanyFrameFolder);
            Directory.CreateDirectory(folder);
            using (var writeLock = File.Open(Path.Combine(folder, ".write.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                var libraries = LoadCompanyFrameLibraries();
                var library = libraries.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (library == null)
                {
                    library = new CompanyFrameLibrary { Id = Guid.NewGuid().ToString("N"), Name = name };
                    libraries.Add(library);
                }
                var copiedFiles = new List<string>();
                try
                {
                    foreach (var original in selected)
                    {
                        var shared = original.Copy();
                        var previous = FindCompanyFrame(library, original);
                        var index = library.Frames.IndexOf(previous);
                        shared.RegistrationId = previous?.RegistrationId ?? Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(shared.DefinitionSignature)) shared.DefinitionSignature = previous?.DefinitionSignature;
                        if (string.IsNullOrWhiteSpace(shared.AttributeTagSignature)) shared.AttributeTagSignature = previous?.AttributeTagSignature;
                        shared.IsCompanyFrame = true;
                        shared.CompanyName = library.Name;
                        shared.TemplateSourceDrawingPath = null;
                        shared.DefaultBuilding = null;
                        shared.DefaultSheetName = null;
                        shared.DefaultSheetNumber = null;
                        shared.DefaultPageNumber = null;
                        var source = FrameTemplatePaths.ExistingSnapshot(original);
                        var existingCompanySnapshot = FrameTemplatePaths.ExistingSnapshot(previous);
                        if (source != null && !FrameTemplatePaths.SameFile(source, existingCompanySnapshot))
                        {
                            var copy = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".dwg");
                            copiedFiles.Add(copy);
                            File.Copy(source, copy);
                            shared.TemplateRelativePath = UserDataPaths.RelativeToRoot(copy);
                        }
                        else if (existingCompanySnapshot != null)
                        {
                            shared.TemplateRelativePath = previous.TemplateRelativePath;
                        }
                        else
                        {
                            throw new InvalidOperationException("图框「" + original.DisplayName + "」缺少模板 DWG。请补齐后再存入公司图框库。");
                        }
                        if (index < 0) library.Frames.Add(shared);
                        else library.Frames[index] = shared;
                    }
                    WriteAtomically(Path.Combine(folder, "公司图框.json"), stream =>
                        new DataContractJsonSerializer(typeof(List<CompanyFrameLibrary>)).WriteObject(stream, libraries));
                    return library;
                }
                catch
                {
                    foreach (var copy in copiedFiles)
                        try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
        }

        internal static FrameDefinition FindCompanyFrame(CompanyFrameLibrary library, FrameDefinition original)
        {
            if (library == null) return null;
            var matches = library.Frames.Where(frame => SameFrameVariant(frame, original)).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException("「" + original.DisplayName + "」对应多个同名版本，请先在 CAD 中补齐此图框的模板。");
            return matches.FirstOrDefault();
        }

        internal FrameDefinition UpdateCompanyFrame(FrameDefinition original, Action<FrameDefinition> update)
            => EditCompanyFrame(original, (library, frame) => update(frame));

        internal void DeleteMissingCompanyFrame(FrameDefinition original)
        {
            EditCompanyFrame(original, (library, frame) =>
            {
                if (FrameTemplatePaths.ExistingSnapshot(frame) != null)
                    throw new InvalidOperationException("此图框的模板已补齐，请返回列表后重新插入。");
                library.Frames.Remove(frame);
            });
        }

        private FrameDefinition EditCompanyFrame(FrameDefinition original, Action<CompanyFrameLibrary, FrameDefinition> edit)
        {
            if (original?.IsCompanyFrame != true || string.IsNullOrWhiteSpace(original.RegistrationId))
                throw new InvalidOperationException("请选择有效的公司图框登记。");
            var folder = Path.Combine(UserDataPaths.RootDirectory, CompanyFrameFolder);
            using (var writeLock = File.Open(Path.Combine(folder, ".write.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                var libraries = LoadCompanyFrameLibraries();
                var library = libraries.FirstOrDefault(item => string.Equals(item.Name, original.CompanyName, StringComparison.OrdinalIgnoreCase)
                    && item.Frames.Any(candidate => candidate.RegistrationId == original.RegistrationId));
                var frame = library?.Frames.FirstOrDefault(item => item.RegistrationId == original.RegistrationId);
                if (frame == null || !SameFrameVariant(frame, original))
                    throw new InvalidOperationException("所选公司图框登记已变化，请重新打开图框窗口。");
                edit(library, frame);
                WriteAtomically(Path.Combine(folder, "公司图框.json"), stream =>
                    new DataContractJsonSerializer(typeof(List<CompanyFrameLibrary>)).WriteObject(stream, libraries));
                return frame;
            }
        }

        internal string SaveCompanyFrameTemplate(FrameDefinition frame, Action<string> writeTemplate, string definitionSignature = null)
        {
            var path = Path.Combine(UserDataPaths.RootDirectory, CompanyFrameFolder, Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                return UpdateCompanyFrame(frame, stored =>
                {
                    writeTemplate(path);
                    if (FrameTemplatePaths.ExistingSnapshot(new FrameDefinition { TemplateRelativePath = path }) == null)
                        throw new IOException("图框模板 DWG 未生成。");
                    stored.TemplateRelativePath = UserDataPaths.RelativeToRoot(path);
                    if (string.IsNullOrWhiteSpace(stored.DefinitionSignature)) stored.DefinitionSignature = definitionSignature;
                }).TemplateRelativePath;
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        internal static bool IsCompanyTemplatePath(string path) =>
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.Combine(UserDataPaths.RootDirectory, CompanyFrameFolder), StringComparison.OrdinalIgnoreCase);

        internal static bool SameFrameVariant(FrameDefinition left, FrameDefinition right)
        {
            if (!string.Equals(left.BlockName, right.BlockName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(left.DefinitionSignature) && !string.IsNullOrWhiteSpace(right.DefinitionSignature))
                return string.Equals(left.DefinitionSignature, right.DefinitionSignature, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(left.AttributeTagSignature) && !string.IsNullOrWhiteSpace(right.AttributeTagSignature)
                && !string.Equals(left.AttributeTagSignature, right.AttributeTagSignature, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(left.PaperDisplay, right.PaperDisplay, StringComparison.OrdinalIgnoreCase);
        }

        public void SaveFrames(List<FrameDefinition> frames)
        {
            var project = GetActiveProject();
            project.Frames = frames ?? new List<FrameDefinition>();
            SaveProject(project);
            FramesChanged?.Invoke();
        }

        public void SaveProjects(List<ProjectProfile> projects)
        {
            Normalize(projects);
            // CloudId 必须在写盘前定下来：项目改名后要从旧映射/旧目录继承身份，
            // 否则同步引擎会把整个项目当成新项目全量重传。
            ProjectSyncProjectionStore.AssignIdentities(projects);
            var path = ProjectsPath();
            WriteAtomically(path, stream =>
                new DataContractJsonSerializer(typeof(List<ProjectProfile>)).WriteObject(stream, projects));
            ProjectSyncProjectionStore.Export(projects);
            ProjectSyncProjectionStore.RefreshMappings(projects);
            CloudSyncCoordinator.RequestSynchronization(false);
        }

        private static void WriteTextAtomically(string path, string value)
        {
            WriteAtomically(path, stream =>
            {
                // leaveOpen=true：WriteAtomically 需要在委托返回后对同一个流 Flush(true)。
                // 默认 StreamWriter 会连底层 FileStream 一起关闭，导致 BPP 构造阶段报
                // “无法访问已关闭的文件”。
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, 1024, true)) writer.Write(value ?? string.Empty);
            });
        }

        private static void WriteAtomically(string path, Action<Stream> write)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("项目配置文件路径无效。");
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new IOException("项目配置文件目录无效。");
            Directory.CreateDirectory(directory);

            lock (FileWriteSync)
            {
                var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        write(stream);
                        stream.Flush(true);
                    }
                    if (new FileInfo(temporary).Length == 0) throw new IOException("项目配置暂存文件为空。");

                    if (!File.Exists(path))
                    {
                        File.Move(temporary, path);
                        return;
                    }

                    var backup = path + ".bak";
                    try
                    {
                        File.Replace(temporary, path, backup, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithRollback(temporary, path, backup);
                    }
                    catch (IOException)
                    {
                        ReplaceWithRollback(temporary, path, backup);
                    }
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private static void ReplaceWithRollback(string temporary, string path, string backup)
        {
            var rollback = backup + "." + Guid.NewGuid().ToString("N") + ".rollback";
            File.Copy(path, rollback, true);
            try
            {
                File.Copy(rollback, backup, true);
                File.Copy(temporary, path, true);
            }
            catch
            {
                try { if (File.Exists(rollback)) File.Copy(rollback, path, true); } catch { }
                throw;
            }
            finally
            {
                try { if (File.Exists(rollback)) File.Delete(rollback); } catch { }
            }
        }

        private static List<ProjectProfile> ReadProjects(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var projects = (List<ProjectProfile>)new DataContractJsonSerializer(typeof(List<ProjectProfile>)).ReadObject(stream);
                if (projects == null) throw new InvalidDataException("项目配置内容为空。");
                return projects;
            }
        }

        private static void Quarantine(string path)
        {
            if (!File.Exists(path)) return;
            var target = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            try { File.Move(path, target); }
            catch { try { File.Copy(path, target, false); File.Delete(path); } catch { } }
        }

        private static void ReportRecovery(string message)
        {
            LastRecoveryNotice = message;
            try
            {
                File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "data-recovery.log"),
                    DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static bool Normalize(List<ProjectProfile> projects)
        {
            if (projects == null) return false;
            var changed = false;
            projects.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
            foreach (var project in projects)
            {
                if (string.IsNullOrWhiteSpace(project.ProjectFolder))
                {
                    project.ProjectFolder = DefaultProjectFolder(project.Name);
                }
                else if (!Path.IsPathRooted(project.ProjectFolder)) project.ProjectFolder = Path.GetFullPath(Path.Combine(UserDataPaths.RootDirectory, project.ProjectFolder));
                if (project.Frames == null) project.Frames = new List<FrameDefinition>();
                foreach (var frame in project.Frames.Where(x => x != null))
                {
                    var direction = PaperSizeCatalog.DefaultOrientation(frame.PaperSize);
                    if (!string.Equals(frame.PaperOrientation, direction, StringComparison.OrdinalIgnoreCase))
                    {
                        frame.PaperOrientation = direction;
                        changed = true;
                    }
                }
                if (string.IsNullOrWhiteSpace(project.PlotStyle)) project.PlotStyle = "monochrome.ctb";
                if (string.IsNullOrWhiteSpace(project.MarginMode)) project.MarginMode = "自动适配";
                if (string.IsNullOrWhiteSpace(project.OutputDirectory)) project.OutputDirectory = Path.Combine(project.ProjectFolder, "PDF输出");
                if (project.FavoritePlotStyles == null) project.FavoritePlotStyles = new List<string>();
                if (project.SavedSheets == null) project.SavedSheets = new List<SheetCatalogItem>();
                if (project.CadFiles == null) project.CadFiles = new List<string>();
                if (project.SelectedCadFiles == null) project.SelectedCadFiles = new List<string>();
                if (project.SelectedPublishBuildings == null) project.SelectedPublishBuildings = new List<string>();
                if (project.SelectedLayouts == null) project.SelectedLayouts = new List<string>();
            }
            if (projects.Count == 0) projects.Add(new ProjectProfile { Name = "默认项目" });
            return changed;
        }

        private static List<FrameDefinition> LoadLegacyFrames()
        {
            var path = UserDataPaths.SettingsFile(LegacyFramesFileName, LegacyFramesFileName);
            if (!File.Exists(path)) return new List<FrameDefinition>();
            try
            {
                using (var stream = File.OpenRead(path))
                    return (List<FrameDefinition>)new DataContractJsonSerializer(typeof(List<FrameDefinition>)).ReadObject(stream);
            }
            catch { return new List<FrameDefinition>(); }
        }

        private static string ProjectsPath() => UserDataPaths.SettingsFile(ProjectsFileName, "BatchPdfPublisher.projects.json");
        private static string ActiveProjectPath() => UserDataPaths.SettingsFile(ActiveProjectFileName, "BatchPdfPublisher.active-project.txt");
        private static string DefaultProjectFolder(string projectName)
        {
            var name = string.IsNullOrWhiteSpace(projectName) ? "默认项目" : projectName.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return Path.Combine(CloudProjectWorkspaceService.GetWorkspaceRoot(), name);
        }
        private static string ResolveProjectFolder(string requestedFolder, string projectName)
        {
            if (string.IsNullOrWhiteSpace(requestedFolder)) return DefaultProjectFolder(projectName);
            var trimmed = requestedFolder.Trim();
            if (!Path.IsPathRooted(trimmed)) throw new IOException("项目文件夹必须使用绝对路径。");
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        private static bool NeedsProjectFolderNormalization(string path)
        {
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path);
        }
    }
}
