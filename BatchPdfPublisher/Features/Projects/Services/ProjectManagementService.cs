using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 项目管理的共享实现：新建 / 重命名项目、重命名项目内文件、删除项目。
    ///
    /// **设计要点（改名为什么不会变成“新项目”）**
    /// 1. 项目的云同步身份是 <see cref="ProjectProfile.CloudId"/>，改名时**保持不变**，
    ///    因此 <c>项目文件/&lt;CloudId&gt;/…</c> 这条同步路径一个字节都不用动，云端不会重新上传；
    /// 2. 批量打印的“扫描结果”存在 <see cref="ProjectProfile.SavedSheets"/> 里，以
    ///    <see cref="SheetCatalogItem.SourceFile"/> 绝对路径为键，重命名时会一并改写，
    ///    因此 CAD 里不需要重新扫描（图框顺序按同一 identity 继续匹配）；
    /// 3. 项目文件夹、图框模板目录、楼梯方案/平面缓存目录都按项目名存放，改名时一起搬。
    ///
    /// 本类**不依赖 AutoCAD**，因此启动器（BatchPdfPublisherLauncher.csproj 源码链接）与
    /// 主插件共用同一份实现。修改本文件时两个工程同时生效，无需改 csproj。
    /// </summary>
    public sealed class ProjectManagementService
    {
        private const string JournalFolderName = "项目管理";

        private readonly PublishPlanStore _store = new PublishPlanStore();
        private string _backupProjectList;
        private string _backupSettings;

        public string UserRoot { get { return UserDataPaths.RootDirectory; } }

        public string WorkspaceRoot
        {
            get { try { return CloudProjectWorkspaceService.GetWorkspaceRoot(); } catch { return null; } }
        }

        public string JournalDirectory { get { return Path.Combine(UserDataPaths.ProjectsDirectory, JournalFolderName); } }

        public List<ProjectProfile> LoadProjects()
        {
            return _store.LoadProjects();
        }

        public ProjectProfile FindProject(string name)
        {
            return FindProject(name, _store.LoadProjects());
        }

        public static ProjectProfile FindProject(string name, IEnumerable<ProjectProfile> projects)
        {
            var clean = (name ?? string.Empty).Trim();
            return (projects ?? Enumerable.Empty<ProjectProfile>()).FirstOrDefault(x => x != null &&
                string.Equals((x.Name ?? string.Empty).Trim(), clean, StringComparison.OrdinalIgnoreCase));
        }

        public string GetProjectFolder(ProjectProfile project)
        {
            return _store.GetProjectFolder(project);
        }

        /// <summary>AutoCAD 是否正在运行。改名必须在 CAD 关闭时做：CAD 内存里那份项目列表会覆盖磁盘改动。</summary>
        public static bool IsCadRunning()
        {
            foreach (var name in new[] { "acad", "acadlt", "acadcore" })
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; }
                catch { }
            }
            return false;
        }

        public string DescribeCloudSync(ProjectProfile project)
        {
            if (project == null) return string.Empty;
            try
            {
                var settings = new CloudSyncSettingsStore().LoadSettings();
                if (!settings.SyncProjectFiles) return "云同步：项目文件同步未开启";
                var cloudId = ProjectSyncProjectionStore.ProjectId(project);
                var mapping = (settings.ProjectMappings ?? new List<CloudSyncProjectMapping>())
                    .FirstOrDefault(x => x != null && string.Equals((x.CloudId ?? string.Empty).Trim(), cloudId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null) return "云同步：未登记（保存后自动登记）";
                return "云同步：" + (mapping.Enabled ? "已开启" : "已关闭") + "（身份 " + cloudId + "）";
            }
            catch (Exception exception) { return "云同步：状态不可用（" + exception.Message + "）"; }
        }

        /// <summary>列出项目文件夹里的文件（相对路径），并标出是否已登记 / 已扫描。</summary>
        public List<ProjectFileEntry> ListFiles(ProjectProfile project)
        {
            var result = new List<ProjectFileEntry>();
            if (project == null) return result;
            var root = GetProjectFolder(project);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;
            var registered = new HashSet<string>((project.CadFiles ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(FullPath), StringComparer.OrdinalIgnoreCase);
            var scanned = new HashSet<string>((project.SavedSheets ?? new List<SheetCatalogItem>()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.SourceFile)).Select(x => FullPath(x.SourceFile)), StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var full = FullPath(path);
                    FileInfo info;
                    try { info = new FileInfo(path); } catch { continue; }
                    result.Add(new ProjectFileEntry
                    {
                        RelativePath = Relative(root, path),
                        FullPath = path,
                        Size = info.Exists ? info.Length : 0,
                        ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                        Registered = registered.Contains(full),
                        Scanned = scanned.Contains(full)
                    });
                    if (result.Count >= 5000) break;
                }
            }
            catch { }
            return result.OrderBy(x => x.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ==================== 方案（执行前预览） ====================

        public ProjectManagementPlan PlanCreateProject(string name, string folder)
        {
            var plan = new ProjectManagementPlan { Action = ProjectManagementAction.CreateProject };
            var clean = (name ?? string.Empty).Trim();
            plan.NewName = clean;
            string error;
            if (!ValidProjectName(clean, out error)) { plan.Blockers.Add(error); return plan; }
            var projects = LoadProjects();
            if (FindProject(clean, projects) != null) { plan.Blockers.Add("已经有同名项目“" + clean + "”。"); return plan; }
            var target = (folder ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                var workspace = WorkspaceRoot;
                if (string.IsNullOrWhiteSpace(workspace)) { plan.Blockers.Add("无法确定项目工作区目录，请手动指定项目文件夹。"); return plan; }
                target = Path.Combine(workspace, FrameTemplatePaths.SafeName(clean, clean));
            }
            else if (!Path.IsPathRooted(target)) { plan.Blockers.Add("项目文件夹必须使用绝对路径。"); return plan; }
            target = FullPath(target);
            if (File.Exists(target)) { plan.Blockers.Add("该路径已被文件占用：" + target); return plan; }
            plan.ProjectFolder = target;
            plan.CloudId = ProjectSyncProjectionStore.StableProjectId(clean);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                plan.Warnings.Add("文件夹已存在且非空，项目会直接登记该目录；里面的图纸需要进入 CAD 后扫描。");
            plan.Warnings.Add("新项目没有任何图框登记，第一次使用时请在 CAD 的“项目管理 → 扫描设置”里确认扫描范围。");
            return plan;
        }

        public ProjectManagementPlan PlanRenameProject(string projectName, string newName, bool renameFolder)
        {
            var plan = new ProjectManagementPlan { Action = ProjectManagementAction.RenameProject, RenameProjectFolder = renameFolder };
            plan.ProjectName = (projectName ?? string.Empty).Trim();
            var clean = (newName ?? string.Empty).Trim();
            plan.NewName = clean;
            var projects = LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) { plan.Blockers.Add("找不到项目“" + plan.ProjectName + "”。"); return plan; }
            string error;
            if (!ValidProjectName(clean, out error)) { plan.Blockers.Add(error); return plan; }
            if (string.Equals(clean, project.Name, StringComparison.OrdinalIgnoreCase)) { plan.Blockers.Add("新名字与现在的名字相同。"); return plan; }
            if (FindProject(clean, projects) != null) { plan.Blockers.Add("已经有同名项目“" + clean + "”，请换一个名字。"); return plan; }

            plan.CloudId = ProjectSyncProjectionStore.ProjectId(project);
            var oldFolder = GetProjectFolder(project);
            plan.ProjectFolder = oldFolder;
            plan.NewProjectFolder = oldFolder;

            if (renameFolder && !string.IsNullOrWhiteSpace(oldFolder))
            {
                var parent = Path.GetDirectoryName(oldFolder);
                if (string.IsNullOrWhiteSpace(parent)) { plan.Blockers.Add("项目文件夹路径无效：" + oldFolder); return plan; }
                var newFolder = Path.Combine(parent, FrameTemplatePaths.SafeName(clean, clean));
                plan.NewProjectFolder = newFolder;
                var shared = projects.Any(x => !ReferenceEquals(x, project) && PathEquals(GetProjectFolder(x), oldFolder));
                if (shared) plan.Blockers.Add("有多个项目登记了同一个文件夹，无法自动改名，请先在 CAD 里把它们分开。");
                else if (!PathEquals(oldFolder, newFolder) && Directory.Exists(newFolder) && !PathEquals(newFolder, oldFolder))
                    plan.Blockers.Add("目标文件夹已经存在：" + newFolder + "。请先改名或清空该目录。");
                else if (Directory.Exists(oldFolder))
                    plan.Moves.Add(new ProjectMovePlan { Kind = "项目文件夹", From = oldFolder, To = newFolder, Required = true });
                else
                    plan.Warnings.Add("项目文件夹当前不存在，只改登记信息：" + oldFolder);
            }

            // 图框模板：目录名带项目名，按文件逐个搬到新目录，避免与已有目录互相覆盖。
            var oldTemplates = FrameTemplatePaths.FolderFor(project.Name);
            var newTemplates = FrameTemplatePaths.FolderFor(clean);
            if (Directory.Exists(oldTemplates) && !PathEquals(oldTemplates, newTemplates))
            {
                foreach (var file in Directory.EnumerateFiles(oldTemplates, "*", SearchOption.TopDirectoryOnly))
                {
                    var destination = Path.Combine(newTemplates, Path.GetFileName(file));
                    if (File.Exists(destination)) { plan.Warnings.Add("目标模板已存在，跳过：" + Path.GetFileName(file)); continue; }
                    plan.Moves.Add(new ProjectMovePlan { Kind = "图框模板", From = file, To = destination, Required = true });
                }
            }

            // 楼梯模块按项目名存放方案与平面缓存，目录能搬就搬（搬不动只警告）。
            AddOptionalFolderMove(plan, "楼梯方案", Path.Combine(UserDataPaths.RootDirectory, "楼梯大样", "项目"), project.Name, clean);
            AddOptionalFolderMove(plan, "楼梯平面缓存", Path.Combine(UserDataPaths.RootDirectory, "楼梯平面缓存"), project.Name, clean);

            CollectRenameReferences(plan, project, oldTemplates, newTemplates);

            if (PreferencesMentionProject(project.Name))
                plan.Warnings.Add("门窗立面的“按工程记忆”设置按项目名存放，改名后需要重新设置；旧数据不会被删除。");
            plan.Warnings.Add("云同步：项目身份（" + plan.CloudId + "）保持不变，项目文件夹不会重新上传；"
                + "图框模板会以新名字上传，云端旧名字的模板副本不会被自动删除。");
            return plan;
        }

        public ProjectManagementPlan PlanRenameFile(string projectName, string relativePath, string newFileName)
        {
            var plan = new ProjectManagementPlan { Action = ProjectManagementAction.RenameFile };
            plan.ProjectName = (projectName ?? string.Empty).Trim();
            plan.FileRelativePath = (relativePath ?? string.Empty).Trim().Replace('/', Path.DirectorySeparatorChar);
            plan.NewName = (newFileName ?? string.Empty).Trim();
            var projects = LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) { plan.Blockers.Add("找不到项目“" + plan.ProjectName + "”。"); return plan; }
            plan.CloudId = ProjectSyncProjectionStore.ProjectId(project);
            var root = GetProjectFolder(project);
            plan.ProjectFolder = root;
            if (string.IsNullOrWhiteSpace(root)) { plan.Blockers.Add("项目文件夹无效。"); return plan; }
            if (plan.FileRelativePath.Length == 0) { plan.Blockers.Add("没有选择文件。"); return plan; }
            var full = Path.Combine(root, plan.FileRelativePath);
            if (!IsUnder(root, full)) { plan.Blockers.Add("只能重命名项目文件夹里的文件：" + relativePath); return plan; }
            if (!File.Exists(full)) { plan.Blockers.Add("文件不存在：" + full); return plan; }
            string error;
            if (!ValidFileName(plan.NewName, out error)) { plan.Blockers.Add(error); return plan; }
            var destination = Path.Combine(Path.GetDirectoryName(full), plan.NewName);
            if (PathEquals(full, destination)) { plan.Blockers.Add("新文件名与现在的名字相同。"); return plan; }
            if (File.Exists(destination)) { plan.Blockers.Add("同目录下已经有“" + plan.NewName + "”。"); return plan; }

            plan.Moves.Add(new ProjectMovePlan { Kind = "图纸文件", From = full, To = destination, Required = true });
            var affected = CountFileReferences(project, full, destination, plan.References);
            if (affected == 0) plan.Warnings.Add("该文件没有登记在项目里（未登记 / 未扫描），改名只影响文件本身。");

            var pending = PendingEntries(plan.CloudId, plan.FileRelativePath);
            if (pending.Count > 0)
                plan.Warnings.Add("该文件有 " + pending.Count + " 条待应用的云端更新，改名后它们会失效；"
                    + "建议先进入 CAD 的云同步中心处理完再改名。");
            plan.Warnings.Add("云同步：改名后的文件会在下次同步时作为新文件上传；"
                + "云端旧文件名的副本不会被自动删除（同步引擎不传播删除），需要清理时请在云同步中心处理。");
            return plan;
        }

        public ProjectManagementPlan PlanDeleteProject(string projectName, bool deleteFolderToRecycleBin)
        {
            var plan = new ProjectManagementPlan { Action = ProjectManagementAction.DeleteProject, DeleteFolderToRecycleBin = deleteFolderToRecycleBin };
            plan.ProjectName = (projectName ?? string.Empty).Trim();
            var projects = LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) { plan.Blockers.Add("找不到项目“" + plan.ProjectName + "”。"); return plan; }
            if (projects.Count <= 1) { plan.Blockers.Add("至少需要保留一个项目，不能删除最后一个。"); return plan; }
            plan.CloudId = ProjectSyncProjectionStore.ProjectId(project);
            plan.ProjectFolder = GetProjectFolder(project);

            var activeName = _store.LoadActiveProjectName();
            if (string.Equals(activeName, project.Name, StringComparison.OrdinalIgnoreCase))
                plan.Warnings.Add("这是当前项目，删除后 CAD 会切换到列表里的第一个项目。");
            plan.Warnings.Add("云同步登记会被关闭，已上传到云端的文件不会被删除；"
                + "该项目会被标记为已归档，同名重建时不会自动从云端恢复旧数据。");
            if (deleteFolderToRecycleBin)
            {
                if (Directory.Exists(plan.ProjectFolder))
                    plan.Warnings.Add("项目文件夹将移到回收站（可还原）：" + plan.ProjectFolder);
                var templates = FrameTemplatePaths.FolderFor(project.Name);
                if (Directory.Exists(templates)) plan.Warnings.Add("图框模板目录将移到回收站：" + templates);
            }
            else
            {
                plan.Warnings.Add("项目文件夹、图框模板都会保留在磁盘上。");
            }
            return plan;
        }

        // ==================== 执行 ====================

        public ProjectManagementResult Execute(ProjectManagementPlan plan)
        {
            var result = new ProjectManagementResult();
            if (plan == null) { result.Errors.Add("没有可执行的方案。"); return result; }
            ProjectManagementPlan current;
            try { current = Replan(plan); }
            catch (Exception exception) { result.Errors.Add("重新校验失败：" + exception.Message); return result; }
            if (!current.CanExecute) { result.Errors.AddRange(current.Blockers); return result; }
            plan = current;

            result.Warnings.AddRange(plan.Warnings);
            result.SafetyBackupPath = TryBackupConfiguration(plan, result);
            var applied = new List<ProjectMovePlan>();
            try
            {
                switch (plan.Action)
                {
                    case ProjectManagementAction.CreateProject: ExecuteCreateProject(plan, result); break;
                    case ProjectManagementAction.RenameProject: ExecuteRenameProject(plan, result, applied); break;
                    case ProjectManagementAction.RenameFile: ExecuteRenameFile(plan, result, applied); break;
                    case ProjectManagementAction.DeleteProject: ExecuteDeleteProject(plan, result); break;
                }
                result.Succeeded = true;
                result.Message = "已完成：" + plan.Summary;
            }
            catch (Exception exception)
            {
                UndoMoves(applied, result);
                RestoreConfiguration(result);
                result.Succeeded = false;
                result.Message = "执行失败，已尽力回滚到操作前的状态。";
                result.Errors.Add(exception.GetType().Name + "：" + exception.Message);
            }
            result.JournalPath = TryWriteJournal(plan, result);
            return result;
        }

        private ProjectManagementPlan Replan(ProjectManagementPlan plan)
        {
            switch (plan.Action)
            {
                case ProjectManagementAction.CreateProject:
                    return PlanCreateProject(plan.NewName, plan.ProjectFolder);
                case ProjectManagementAction.RenameProject:
                    return PlanRenameProject(plan.ProjectName, plan.NewName, plan.RenameProjectFolder);
                case ProjectManagementAction.RenameFile:
                    return PlanRenameFile(plan.ProjectName, plan.FileRelativePath, plan.NewName);
                case ProjectManagementAction.DeleteProject:
                    return PlanDeleteProject(plan.ProjectName, plan.DeleteFolderToRecycleBin);
                default: throw new InvalidOperationException("未知的项目管理操作。");
            }
        }

        private void ExecuteCreateProject(ProjectManagementPlan plan, ProjectManagementResult result)
        {
            var cloudId = ProjectSyncProjectionStore.StableProjectId(plan.NewName);
            // 同名项目以前被删过 → 解除归档并重新打开同步，避免“新项目同步默认是关的”。
            ProjectSyncProjectionStore.SetCloudProjectArchived(cloudId, false);
            EnableProjectSync(plan.NewName, cloudId);
            Directory.CreateDirectory(plan.ProjectFolder);
            result.Steps.Add("已建立项目文件夹：" + plan.ProjectFolder);
            var projects = _store.LoadProjects();
            if (FindProject(plan.NewName, projects) == null)
            {
                projects.Add(new ProjectProfile { Name = plan.NewName, ProjectFolder = plan.ProjectFolder });
                _store.SaveProjects(projects);
                result.Steps.Add("已写入项目列表：" + plan.NewName);
            }
        }

        private void ExecuteRenameProject(ProjectManagementPlan plan, ProjectManagementResult result, List<ProjectMovePlan> applied)
        {
            foreach (var move in plan.Moves) ApplyMove(move, applied);
            var projects = _store.LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) throw new InvalidOperationException("项目列表里已经找不到“" + plan.ProjectName + "”，可能被其他程序改动，已停止。");
            ApplyRenameReferences(project, plan);
            var wasActive = string.Equals((_store.LoadActiveProjectName() ?? string.Empty).Trim(), plan.ProjectName, StringComparison.OrdinalIgnoreCase);
            _store.SaveProjects(projects);
            if (wasActive) _store.SetActiveProject(plan.NewName);
            result.Steps.Add("已改写登记：项目名、文件夹、输出目录、图纸列表与已扫描图纸共 "
                + plan.References.Count + " 处路径");
            if (wasActive) result.Steps.Add("当前项目已切换为：" + plan.NewName);
        }

        private void ExecuteRenameFile(ProjectManagementPlan plan, ProjectManagementResult result, List<ProjectMovePlan> applied)
        {
            foreach (var move in plan.Moves) ApplyMove(move, applied);
            var projects = _store.LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) throw new InvalidOperationException("项目列表里已经找不到“" + plan.ProjectName + "”。");
            var oldFull = FullPath(plan.Moves[0].From);
            var newFull = FullPath(plan.Moves[0].To);
            var changed = RemapFileReferences(project, oldFull, newFull);
            _store.SaveProjects(projects);
            result.Steps.Add("已改写登记路径 " + changed + " 处（图纸列表 / 已扫描图纸）");
            if (changed > 0) result.Steps.Add("批量打印不需要重新扫描：图框扫描结果已经指向新文件名。");
        }

        private void ExecuteDeleteProject(ProjectManagementPlan plan, ProjectManagementResult result)
        {
            var projects = _store.LoadProjects();
            var project = FindProject(plan.ProjectName, projects);
            if (project == null) throw new InvalidOperationException("找不到项目“" + plan.ProjectName + "”。");
            var cloudId = ProjectSyncProjectionStore.ProjectId(project);
            DisableProjectSync(cloudId);
            result.Steps.Add("已关闭该项目在云同步里的登记");
            ProjectSyncProjectionStore.SetCloudProjectArchived(cloudId, true);
            DeleteProjection(cloudId, result);
            if (!_store.DeleteProject(plan.ProjectName))
                throw new InvalidOperationException("删除项目失败：至少要保留一个项目。");
            result.Steps.Add("已从项目列表移除：" + plan.ProjectName);
            if (plan.DeleteFolderToRecycleBin)
            {
                Recycle(plan.ProjectFolder, "项目文件夹", result);
                Recycle(FrameTemplatePaths.FolderFor(project.Name), "图框模板", result);
                Recycle(Path.Combine(UserDataPaths.RootDirectory, "楼梯大样", "项目", FrameTemplatePaths.SafeName(project.Name, "默认项目")), "楼梯方案", result);
                Recycle(Path.Combine(UserDataPaths.RootDirectory, "楼梯平面缓存", FrameTemplatePaths.SafeName(project.Name, "默认项目")), "楼梯平面缓存", result);
            }
        }

        private void ApplyMove(ProjectMovePlan move, List<ProjectMovePlan> applied)
        {
            var directory = Directory.Exists(move.From);
            var file = !directory && File.Exists(move.From);
            if (!directory && !file)
            {
                if (move.Required) throw new FileNotFoundException(move.Kind + "不存在：" + move.From);
                return;
            }
            var targetExists = directory ? Directory.Exists(move.To) : File.Exists(move.To);
            if (targetExists)
            {
                if (move.Required) throw new IOException(move.Kind + "的目标已存在：" + move.To);
                return;
            }
            var parent = Path.GetDirectoryName(move.To);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            if (directory) Directory.Move(move.From, move.To); else File.Move(move.From, move.To);
            applied.Add(move);
        }

        private static void UndoMoves(List<ProjectMovePlan> applied, ProjectManagementResult result)
        {
            for (var index = applied.Count - 1; index >= 0; index--)
            {
                var move = applied[index];
                try
                {
                    if (Directory.Exists(move.To) && !Directory.Exists(move.From)) Directory.Move(move.To, move.From);
                    else if (File.Exists(move.To) && !File.Exists(move.From)) File.Move(move.To, move.From);
                    result.Steps.Add("已回滚移动：" + move.To + " → " + move.From);
                }
                catch (Exception exception) { result.Errors.Add("回滚失败：" + move.To + "（" + exception.Message + "）"); }
            }
        }

        // ==================== 引用改写 ====================

        private void ApplyRenameReferences(ProjectProfile project, ProjectManagementPlan plan)
        {
            project.Name = plan.NewName;
            // 身份必须跟着项目走：老配置里可能还没有持久化的 CloudId，如果这里不显式钉住，
            // 保存时就会按新名字重新派生身份 —— 于是整个工程被当成新项目全量重传。
            if (!string.IsNullOrWhiteSpace(plan.CloudId)) project.CloudId = plan.CloudId;
            if (!string.IsNullOrWhiteSpace(plan.NewProjectFolder)) project.ProjectFolder = plan.NewProjectFolder;
            var oldFolder = plan.ProjectFolder;
            var newFolder = plan.NewProjectFolder;
            if (!string.IsNullOrWhiteSpace(oldFolder) && !PathEquals(oldFolder, newFolder))
            {
                project.OutputDirectory = RemapPath(project.OutputDirectory, oldFolder, newFolder);
                project.CadFiles = RemapPaths(project.CadFiles, oldFolder, newFolder);
                project.SelectedCadFiles = RemapPaths(project.SelectedCadFiles, oldFolder, newFolder);
                foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
                    if (sheet != null) sheet.SourceFile = RemapPath(sheet.SourceFile, oldFolder, newFolder);
            }
            var oldTemplates = FrameTemplatePaths.FolderFor(plan.ProjectName);
            var newTemplates = FrameTemplatePaths.FolderFor(plan.NewName);
            foreach (var frame in project.Frames ?? new List<FrameDefinition>())
            {
                if (frame == null || string.IsNullOrWhiteSpace(frame.TemplateRelativePath)) continue;
                var absolute = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
                if (string.IsNullOrWhiteSpace(absolute)) continue;
                var mapped = RemapPath(absolute, oldTemplates, newTemplates);
                if (!string.Equals(mapped, absolute, StringComparison.OrdinalIgnoreCase))
                    frame.TemplateRelativePath = UserDataPaths.RelativeToRoot(mapped);
            }
        }

        private int RemapFileReferences(ProjectProfile project, string oldPath, string newPath)
        {
            var changed = 0;
            changed += CountChanged(project.CadFiles, oldPath, newPath);
            project.CadFiles = ReplaceExact(project.CadFiles, oldPath, newPath);
            changed += CountChanged(project.SelectedCadFiles, oldPath, newPath);
            project.SelectedCadFiles = ReplaceExact(project.SelectedCadFiles, oldPath, newPath);
            foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
            {
                if (sheet == null || string.IsNullOrWhiteSpace(sheet.SourceFile)) continue;
                if (!PathEquals(sheet.SourceFile, oldPath)) continue;
                sheet.SourceFile = newPath; changed++;
            }
            return changed;
        }

        private static int CountChanged(IEnumerable<string> paths, string oldPath, string newPath)
        {
            return (paths ?? Enumerable.Empty<string>()).Count(x => PathEquals(x, oldPath));
        }

        /// <summary>文件改名前的引用预览：数一数有多少处登记指向这个文件。</summary>
        private static int CountFileReferences(ProjectProfile project, string oldPath, string newPath, List<ProjectReferenceChange> references)
        {
            var count = 0;
            foreach (var path in project.CadFiles ?? new List<string>())
                if (PathEquals(path, oldPath)) { AddReference(references, "图纸列表", path, newPath); count++; }
            foreach (var path in project.SelectedCadFiles ?? new List<string>())
                if (PathEquals(path, oldPath)) { AddReference(references, "勾选图纸", path, newPath); count++; }
            foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
                if (sheet != null && PathEquals(sheet.SourceFile, oldPath)) { AddReference(references, "已扫描图纸", sheet.SourceFile, newPath); count++; }
            return count;
        }

        private static List<string> ReplaceExact(IEnumerable<string> paths, string oldPath, string newPath)
        {
            return (paths ?? Enumerable.Empty<string>())
                .Select(x => PathEquals(x, oldPath) ? newPath : x).ToList();
        }

        private static List<string> RemapPaths(IEnumerable<string> paths, string oldFolder, string newFolder)
        {
            return (paths ?? Enumerable.Empty<string>()).Select(x => RemapPath(x, oldFolder, newFolder)).ToList();
        }

        private void CollectRenameReferences(ProjectManagementPlan plan, ProjectProfile project, string oldTemplates, string newTemplates)
        {
            var oldFolder = plan.ProjectFolder;
            var newFolder = plan.NewProjectFolder;
            if (!string.IsNullOrWhiteSpace(oldFolder) && !PathEquals(oldFolder, newFolder))
            {
                AddReference(plan, "项目文件夹", oldFolder, newFolder);
                if (!string.IsNullOrWhiteSpace(project.OutputDirectory)) AddReference(plan, "输出目录", project.OutputDirectory, RemapPath(project.OutputDirectory, oldFolder, newFolder));
                foreach (var path in project.CadFiles ?? new List<string>())
                    if (PathEquals(path, oldFolder) || IsUnder(oldFolder, path)) AddReference(plan, "图纸列表", path, RemapPath(path, oldFolder, newFolder));
                foreach (var path in project.SelectedCadFiles ?? new List<string>())
                    if (IsUnder(oldFolder, path)) AddReference(plan, "勾选图纸", path, RemapPath(path, oldFolder, newFolder));
                foreach (var sheet in project.SavedSheets ?? new List<SheetCatalogItem>())
                    if (sheet != null && IsUnder(oldFolder, sheet.SourceFile)) AddReference(plan, "已扫描图纸", sheet.SourceFile, RemapPath(sheet.SourceFile, oldFolder, newFolder));
            }
            foreach (var frame in project.Frames ?? new List<FrameDefinition>())
            {
                if (frame == null || string.IsNullOrWhiteSpace(frame.TemplateRelativePath)) continue;
                var absolute = UserDataPaths.ResolveFromRoot(frame.TemplateRelativePath);
                if (string.IsNullOrWhiteSpace(absolute)) continue;
                var mapped = RemapPath(absolute, oldTemplates, newTemplates);
                if (string.Equals(mapped, absolute, StringComparison.OrdinalIgnoreCase)) continue;
                AddReference(plan, "图框模板", frame.TemplateRelativePath, UserDataPaths.RelativeToRoot(mapped));
            }
        }

        private static void AddReference(ProjectManagementPlan plan, string where, string from, string to)
        {
            AddReference(plan.References, where, from, to);
        }

        private static void AddReference(List<ProjectReferenceChange> references, string where, string from, string to)
        {
            references.Add(new ProjectReferenceChange { Where = where, From = from, To = to });
        }

        // ==================== 云同步登记 ====================

        private void DisableProjectSync(string cloudId)
        {
            UpdateMapping(cloudId, null, false);
        }

        private void EnableProjectSync(string projectName, string cloudId)
        {
            UpdateMapping(cloudId, projectName, true);
        }

        private void UpdateMapping(string cloudId, string projectName, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(cloudId)) return;
            try
            {
                var store = new CloudSyncSettingsStore();
                var settings = store.LoadSettings();
                var mappings = settings.ProjectMappings ?? new List<CloudSyncProjectMapping>();
                var mapping = mappings.FirstOrDefault(x => x != null && string.Equals((x.CloudId ?? string.Empty).Trim(), cloudId, StringComparison.OrdinalIgnoreCase));
                if (mapping == null)
                {
                    if (!enabled) return;
                    mapping = new CloudSyncProjectMapping { CloudId = cloudId, ProjectName = projectName };
                    mappings.Add(mapping);
                }
                mapping.Enabled = enabled;
                mapping.SelectionConfirmed = true;
                if (!string.IsNullOrWhiteSpace(projectName)) mapping.ProjectName = projectName;
                settings.ProjectMappings = mappings;
                store.SaveSettings(settings);
            }
            catch { }
        }

        private void DeleteProjection(string cloudId, ProjectManagementResult result)
        {
            if (string.IsNullOrWhiteSpace(cloudId)) return;
            try
            {
                var folder = Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, cloudId);
                if (!Directory.Exists(folder)) return;
                Directory.Delete(folder, true);
                result.Steps.Add("已删除云端项目描述副本：" + cloudId);
            }
            catch (Exception exception) { result.Warnings.Add("云端项目描述副本未能删除：" + exception.Message); }
        }

        private static List<string> PendingEntries(string cloudId, string relativePath)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(cloudId) || string.IsNullOrWhiteSpace(relativePath)) return result;
            try
            {
                var file = Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "pending", "项目文件", cloudId,
                    relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                foreach (var suffix in new[] { ".pending", ".delete-pending" })
                    if (File.Exists(file + suffix)) result.Add(file + suffix);
            }
            catch { }
            return result;
        }

        // ==================== 记录与备份 ====================

        private string TryWriteJournal(ProjectManagementPlan plan, ProjectManagementResult result)
        {
            try
            {
                Directory.CreateDirectory(JournalDirectory);
                var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + JournalFileName(plan) + ".json";
                var path = Path.Combine(JournalDirectory, name);
                var journal = new ProjectJournal
                {
                    Timestamp = DateTime.Now.ToString("O"),
                    Action = plan.ActionTitle,
                    ProjectName = plan.ProjectName,
                    NewName = plan.NewName,
                    CloudId = plan.CloudId,
                    ProjectFolder = plan.ProjectFolder,
                    NewProjectFolder = plan.NewProjectFolder,
                    FileRelativePath = plan.FileRelativePath,
                    DeleteFolderToRecycleBin = plan.DeleteFolderToRecycleBin,
                    Succeeded = result.Succeeded,
                    SafetyBackupPath = result.SafetyBackupPath,
                    Moves = plan.Moves,
                    References = plan.References,
                    Steps = result.Steps,
                    Errors = result.Errors
                };
                using (var stream = File.Create(path))
                    new DataContractJsonSerializer(typeof(ProjectJournal)).WriteObject(stream, journal);
                return path;
            }
            catch { return null; }
        }

        private static string JournalFileName(ProjectManagementPlan plan)
        {
            var raw = plan.Action == ProjectManagementAction.RenameFile ? plan.ProjectName + "-" + plan.FileRelativePath : plan.NewName ?? plan.ProjectName;
            return FrameTemplatePaths.SafeName((raw ?? "操作").Replace(Path.DirectorySeparatorChar, '_'), "操作");
        }

        private string TryBackupConfiguration(ProjectManagementPlan plan, ProjectManagementResult result)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var source = Path.Combine(UserDataPaths.SettingsDirectory, "项目列表.json");
                var backup = source + ".项目管理备份-" + stamp;
                if (File.Exists(source)) File.Copy(source, backup, true);
                var settings = Path.Combine(UserDataPaths.SettingsDirectory, "cloud-sync.settings.json");
                if (File.Exists(settings)) File.Copy(settings, settings + ".项目管理备份-" + stamp, true);
                _backupProjectList = backup;
                _backupSettings = File.Exists(settings) ? settings + ".项目管理备份-" + stamp : null;
                result.Steps.Add("已备份执行前的项目配置");
                return backup;
            }
            catch (Exception exception)
            {
                result.Warnings.Add("执行前备份失败（继续，但失败后无法自动还原）：" + exception.Message);
                return null;
            }
        }

        private void RestoreConfiguration(ProjectManagementResult result)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_backupSettings) && File.Exists(_backupSettings))
                    File.Copy(_backupSettings, Path.Combine(UserDataPaths.SettingsDirectory, "cloud-sync.settings.json"), true);
                if (string.IsNullOrWhiteSpace(_backupProjectList) || !File.Exists(_backupProjectList)) return;
                List<ProjectProfile> restored;
                using (var stream = File.OpenRead(_backupProjectList))
                    restored = (List<ProjectProfile>)new DataContractJsonSerializer(typeof(List<ProjectProfile>)).ReadObject(stream);
                if (restored == null) return;
                _store.SaveProjects(restored);
                result.Steps.Add("已从备份还原项目列表");
            }
            catch (Exception exception) { result.Errors.Add("还原项目列表失败：" + exception.Message); }
        }

        // ==================== 小工具 ====================

        private static void AddOptionalFolderMove(ProjectManagementPlan plan, string kind, string parent, string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(parent)) return;
            var from = Path.Combine(parent, FrameTemplatePaths.SafeName(oldName, "默认项目"));
            var to = Path.Combine(parent, FrameTemplatePaths.SafeName(newName, "默认项目"));
            if (!Directory.Exists(from) || PathEquals(from, to) || Directory.Exists(to)) return;
            plan.Moves.Add(new ProjectMovePlan { Kind = kind, From = from, To = to, Required = false });
        }

        private bool PreferencesMentionProject(string projectName)
        {
            try
            {
                var path = Path.Combine(UserDataPaths.SettingsDirectory, "door-window-elevation-settings.json");
                if (!File.Exists(path)) return false;
                return File.ReadAllText(path, Encoding.UTF8).IndexOf("\"ProjectName\":\"" + projectName + "\"", StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        private static void Recycle(string path, string kind, ProjectManagementResult result)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    result.Steps.Add("已把" + kind + "移到回收站：" + path);
                }
                else if (File.Exists(path))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    result.Steps.Add("已把" + kind + "移到回收站：" + path);
                }
            }
            catch (Exception exception) { result.Warnings.Add(kind + "移入回收站失败：" + exception.Message); }
        }

        private static bool ValidProjectName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "项目名称不能为空。"; return false; }
            if (name.Length > 80) { error = "项目名称不要超过 80 个字符。"; return false; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "项目名称不能包含 \\ / : * ? \" < > | 等字符。"; return false; }
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) { error = "项目名称不能以点或空格结尾。"; return false; }
            return true;
        }

        private static bool ValidFileName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "文件名不能为空。"; return false; }
            if (name.Length > 120) { error = "文件名不要超过 120 个字符。"; return false; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "文件名不能包含 \\ / : * ? \" < > | 等字符。"; return false; }
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) { error = "文件名不能以点或空格结尾。"; return false; }
            return true;
        }

        private static string Relative(string root, string path)
        {
            try
            {
                var prefix = FullPath(root) + Path.DirectorySeparatorChar;
                var full = FullPath(path);
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : Path.GetFileName(path);
            }
            catch { return Path.GetFileName(path); }
        }

        private static bool IsUnder(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
            var full = FullPath(path);
            var prefix = FullPath(root);
            return string.Equals(full, prefix, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathEquals(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(FullPath(left), FullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string FullPath(string path)
        {
            try { return Path.GetFullPath((path ?? string.Empty).Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return (path ?? string.Empty).Trim(); }
        }

        /// <summary>把位于 <paramref name="oldPrefix"/> 下的路径改到 <paramref name="newPrefix"/>；外部路径原样返回。</summary>
        private static string RemapPath(string path, string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(oldPrefix) || string.IsNullOrWhiteSpace(newPrefix)) return path;
            var full = FullPath(path);
            var oldFull = FullPath(oldPrefix);
            var newFull = FullPath(newPrefix);
            if (string.Equals(full, oldFull, StringComparison.OrdinalIgnoreCase)) return newFull;
            var prefix = oldFull + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return newFull + full.Substring(oldFull.Length);
            return path;
        }
    }

    /// <summary>项目管理操作的落盘记录（正向 + 反向信息都在里面，便于人工恢复）。</summary>
    public sealed class ProjectJournal
    {
        public string Timestamp { get; set; }
        public string Action { get; set; }
        public string ProjectName { get; set; }
        public string NewName { get; set; }
        public string CloudId { get; set; }
        public string ProjectFolder { get; set; }
        public string NewProjectFolder { get; set; }
        public string FileRelativePath { get; set; }
        public bool DeleteFolderToRecycleBin { get; set; }
        public bool Succeeded { get; set; }
        public string SafetyBackupPath { get; set; }
        public List<ProjectMovePlan> Moves { get; set; }
        public List<ProjectReferenceChange> References { get; set; }
        public List<string> Steps { get; set; }
        public List<string> Errors { get; set; }
    }
}
