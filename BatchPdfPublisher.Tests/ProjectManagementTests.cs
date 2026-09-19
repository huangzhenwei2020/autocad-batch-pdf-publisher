using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

/// <summary>
/// 项目管理（在 AutoCAD 之外改名 / 新建 / 删除项目）的护栏测试。
///
/// 核心承诺只有两条，这里逐条钉住：
/// 1) 改项目名**不改变云同步身份 CloudId** —— 否则同步引擎会把整个项目当成新项目全量重传；
/// 2) 改文件夹 / 改文件名时，批量打印的扫描结果（SavedSheets[].SourceFile）跟着改 —— 否则
///    CAD 里必须重新扫描才能打印。
/// </summary>
internal static class ProjectManagementTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoProjectManagementTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RenamesProjectWithoutChangingCloudIdentity(root);
            RenamesLegacyProjectWithoutPersistedCloudId(root);
            RenamesFileAndKeepsScanResults(root);
            BlocksUnsafeOperations(root);
            CreatesAndDeletesProjects(root);
            RollsBackWhenMoveFails(root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void RenamesProjectWithoutChangingCloudIdentity(string root)
    {
        var context = Prepare(root, "rename-project");
        var project = context.Project;
        var oldFolder = project.ProjectFolder;
        var newFolder = Path.Combine(Path.GetDirectoryName(oldFolder), "乙项目");
        var oldCloudId = project.CloudId;
        Assert(!string.IsNullOrWhiteSpace(oldCloudId), "项目缺少 CloudId");
        var oldTemplate = Path.Combine(UserDataPaths.FrameTemplatesDirectory, "甲项目", "A3_A3_BPP.dwg");
        Assert(File.Exists(oldTemplate), "测试前置：图框模板没有建立");

        var service = new ProjectManagementService();
        var plan = service.PlanRenameProject("甲项目", "乙项目", true);
        Assert(plan.CanExecute, "改名方案被拦住：" + string.Join("；", plan.Blockers.ToArray()));
        Assert(plan.Moves.Any(x => x.Kind == "项目文件夹"), "没有计划搬移项目文件夹");
        Assert(plan.References.Any(x => x.Where == "已扫描图纸"), "没有计划改写扫描结果里的路径");

        var result = service.Execute(plan);
        Assert(result.Succeeded, "改名失败：" + result.Describe());
        Assert(!string.IsNullOrWhiteSpace(result.JournalPath) && File.Exists(result.JournalPath), "没有写操作记录");

        Assert(!Directory.Exists(oldFolder), "旧项目文件夹还在");
        Assert(Directory.Exists(newFolder), "新项目文件夹没有建立");
        Assert(File.Exists(Path.Combine(newFolder, "CAD", "图1.dwg")), "图纸没有跟着搬");

        var reloaded = service.LoadProjects().Single(x => x.Name == "乙项目");
        Assert(reloaded.CloudId == oldCloudId, "CloudId 变了：改名会被当成新项目全量重传");
        Assert(PathEquals(reloaded.ProjectFolder, newFolder), "ProjectFolder 没有改写");
        Assert(reloaded.CadFiles.All(x => x.StartsWith(newFolder, StringComparison.OrdinalIgnoreCase)), "图纸列表没有改写");
        Assert(reloaded.SelectedCadFiles.All(x => x.StartsWith(newFolder, StringComparison.OrdinalIgnoreCase)), "勾选图纸没有改写");
        Assert(reloaded.SavedSheets.Count == 2 && reloaded.SavedSheets.All(x => x.SourceFile.StartsWith(newFolder, StringComparison.OrdinalIgnoreCase)),
            "扫描结果仍指向旧路径 —— CAD 里会被迫重新扫描");
        Assert(reloaded.OutputDirectory.StartsWith(newFolder, StringComparison.OrdinalIgnoreCase), "输出目录没有改写");
        Assert(reloaded.Frames.All(x => x.TemplateRelativePath.IndexOf("乙项目", StringComparison.Ordinal) >= 0), "图框模板路径没有改写");
        Assert(File.Exists(Path.Combine(UserDataPaths.FrameTemplatesDirectory, "乙项目", "A3_A3_BPP.dwg")), "图框模板没有跟着搬到新目录");
        Assert(File.ReadAllText(Path.Combine(UserDataPaths.SettingsDirectory, "当前项目.txt")).Trim() == "乙项目", "当前项目指针没有跟着改");

        // 云同步：投影目录名就是 CloudId，必须原地不动，内容改成新名字。
        var projection = Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, oldCloudId, "项目.json");
        Assert(File.Exists(projection), "投影文件被搬走了 —— 云端会多出一份旧项目");
        Assert(File.ReadAllText(projection).IndexOf("乙项目", StringComparison.Ordinal) >= 0, "投影内容没有更新项目名");
        Assert(!Directory.EnumerateDirectories(ProjectSyncProjectionStore.ProjectionDirectory)
            .Any(x => Path.GetFileName(x) != oldCloudId && !Path.GetFileName(x).StartsWith("乙项目", StringComparison.Ordinal)),
            "同步目录里出现了多余的 CloudId 目录");

        var mapping = new CloudSyncSettingsStore().LoadSettings().ProjectMappings.Single(x => x.CloudId == oldCloudId);
        Assert(mapping.ProjectName == "乙项目", "云同步映射的项目名没有更新");
        Assert(PathEquals(mapping.LocalFolder, newFolder), "云同步映射的本地目录没有更新");
        Assert(mapping.Enabled, "改名把云同步关掉了");

        Console.WriteLine("PASS RenamesProjectWithoutChangingCloudIdentity");
    }

    /// <summary>
    /// 老配置（`项目列表.json` 里还没有 CloudId 字段）改名时，身份必须仍然来自**旧名字**，
    /// 否则同步引擎会认为这是一个新项目。
    /// </summary>
    private static void RenamesLegacyProjectWithoutPersistedCloudId(string root)
    {
        var context = Prepare(root, "legacy");
        var expected = ProjectSyncProjectionStore.StableProjectId("甲项目");
        var projectsPath = Path.Combine(UserDataPaths.SettingsDirectory, "项目列表.json");
        // 把 CloudId 从配置里抹掉，模拟升级前写下的项目列表。
        var json = File.ReadAllText(projectsPath);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, "\"CloudId\":\"[^\"]*\",?", string.Empty);
        Assert(stripped.IndexOf("CloudId", StringComparison.Ordinal) < 0, "测试前置：CloudId 没被抹掉");
        File.WriteAllText(projectsPath, stripped);

        var service = new ProjectManagementService();
        var plan = service.PlanRenameProject("甲项目", "戊项目", true);
        Assert(plan.CanExecute, "改名方案被拦住：" + string.Join("；", plan.Blockers.ToArray()));
        Assert(plan.CloudId == expected, "没有沿用旧名字派生的身份");
        var result = service.Execute(plan);
        Assert(result.Succeeded, "改名失败：" + result.Describe());

        var reloaded = service.LoadProjects().Single(x => x.Name == "戊项目");
        Assert(reloaded.CloudId == expected, "老配置改名后身份变成了新名字派生值 —— 会被当成新项目全量重传");
        Assert(File.Exists(Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, expected, "项目.json")), "投影没有留在原身份目录");
        Assert(!Directory.Exists(Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, ProjectSyncProjectionStore.StableProjectId("戊项目"))),
            "多出了一个按新名字派生的同步目录");

        Console.WriteLine("PASS RenamesLegacyProjectWithoutPersistedCloudId");
    }

    private static void RenamesFileAndKeepsScanResults(string root)
    {
        var context = Prepare(root, "rename-file");
        var project = context.Project;
        var service = new ProjectManagementService();
        var plan = service.PlanRenameFile(project.Name, Path.Combine("CAD", "图1.dwg"), "首层平面.dwg");
        Assert(plan.CanExecute, "文件改名方案被拦住：" + string.Join("；", plan.Blockers.ToArray()));
        Assert(plan.References.Count >= 2, "没有识别出需要改写的登记路径");

        var result = service.Execute(plan);
        Assert(result.Succeeded, "文件改名失败：" + result.Describe());

        var folder = project.ProjectFolder;
        Assert(!File.Exists(Path.Combine(folder, "CAD", "图1.dwg")), "旧文件名还在");
        Assert(File.Exists(Path.Combine(folder, "CAD", "首层平面.dwg")), "新文件名没有生成");
        Assert(File.Exists(Path.Combine(folder, "CAD", "图2.dwg")), "同目录其它文件被误动");

        var reloaded = service.LoadProjects().Single(x => x.Name == "甲项目");
        var expected = Path.Combine(folder, "CAD", "首层平面.dwg");
        Assert(reloaded.CloudId == project.CloudId, "改文件名不该改变 CloudId");
        Assert(reloaded.CadFiles.Any(x => PathEquals(x, expected)), "图纸列表没有改成新文件名");
        Assert(reloaded.SelectedCadFiles.Any(x => PathEquals(x, expected)), "勾选图纸没有改成新文件名");
        Assert(reloaded.SavedSheets.Count == 2 && reloaded.SavedSheets.All(x => PathEquals(x.SourceFile, expected)),
            "扫描结果没有跟着改名 —— 批量打印会报“发布所需的 CAD 文件不存在”");
        Assert(!reloaded.CadFiles.Any(x => x.EndsWith("图1.dwg", StringComparison.OrdinalIgnoreCase)), "图纸列表里还残留旧文件名");

        Console.WriteLine("PASS RenamesFileAndKeepsScanResults");
    }

    private static void BlocksUnsafeOperations(string root)
    {
        var context = Prepare(root, "guards");
        var service = new ProjectManagementService();
        // 造一个同名项目，用来验证“改成已存在的名字”会被拦住。
        var store = new PublishPlanStore();
        var projects = store.LoadProjects();
        projects.Add(new ProjectProfile { Name = "已存在项目", ProjectFolder = Path.Combine(CloudProjectWorkspaceService.GetWorkspaceRoot(), "已存在项目") });
        store.SaveProjects(projects);

        var duplicate = service.PlanRenameProject("甲项目", "已存在项目", true);
        Assert(!duplicate.CanExecute, "允许改成已存在的项目名");

        var same = service.PlanRenameProject("甲项目", "甲项目", true);
        Assert(!same.CanExecute, "允许改成同一个名字");

        var invalid = service.PlanRenameProject("甲项目", "新/项目", true);
        Assert(!invalid.CanExecute, "允许项目名包含路径分隔符");

        var outside = service.PlanRenameFile("甲项目", Path.Combine("..", "别处.dwg"), "改名.dwg");
        Assert(!outside.CanExecute, "允许重命名项目文件夹之外的文件");

        var conflict = service.PlanRenameFile("甲项目", Path.Combine("CAD", "图1.dwg"), "图2.dwg");
        Assert(!conflict.CanExecute, "允许改成同目录下已存在的文件名");

        var missing = service.PlanRenameProject("不存在的项目", "随便", true);
        Assert(!missing.CanExecute, "不存在的项目也能生成可执行方案");

        Console.WriteLine("PASS BlocksUnsafeProjectOperations");
    }

    private static void CreatesAndDeletesProjects(string root)
    {
        var context = Prepare(root, "create-delete");
        var service = new ProjectManagementService();

        var create = service.PlanCreateProject("丙项目", null);
        Assert(create.CanExecute, "新建方案被拦住：" + string.Join("；", create.Blockers.ToArray()));
        var created = service.Execute(create);
        Assert(created.Succeeded, "新建失败：" + created.Describe());
        Assert(Directory.Exists(create.ProjectFolder), "新建项目没有建立文件夹");
        var project = service.LoadProjects().Single(x => x.Name == "丙项目");
        Assert(project.CloudId == ProjectSyncProjectionStore.StableProjectId("丙项目"), "新建项目的 CloudId 不是按名字派生的");
        var mapping = new CloudSyncSettingsStore().LoadSettings().ProjectMappings.SingleOrDefault(x => x.CloudId == project.CloudId);
        Assert(mapping != null && mapping.Enabled, "新建项目没有登记到云同步");

        var delete = service.PlanDeleteProject("丙项目", false);
        Assert(delete.CanExecute, "删除方案被拦住：" + string.Join("；", delete.Blockers.ToArray()));
        var deleted = service.Execute(delete);
        Assert(deleted.Succeeded, "删除失败：" + deleted.Describe());
        Assert(service.LoadProjects().All(x => x.Name != "丙项目"), "删除后项目还在列表里");
        Assert(Directory.Exists(create.ProjectFolder), "没有勾选回收站时不该删除文件夹");
        Assert(!Directory.Exists(Path.Combine(ProjectSyncProjectionStore.ProjectionDirectory, project.CloudId)), "投影目录没有清理");
        Assert(ProjectSyncProjectionStore.IsCloudProjectArchived(project.CloudId), "删除的项目没有标记归档，会被云端重新导回来");
        var after = new CloudSyncSettingsStore().LoadSettings().ProjectMappings.SingleOrDefault(x => x.CloudId == project.CloudId);
        Assert(after != null && !after.Enabled, "删除的项目仍在同步");

        var last = service.PlanDeleteProject("甲项目", false);
        Assert(!last.CanExecute, "允许删除最后一个项目");

        Console.WriteLine("PASS CreatesAndDeletesProjectsAndKeepsCloudStateConsistent");
    }

    private static void RollsBackWhenMoveFails(string root)
    {
        var context = Prepare(root, "rollback");
        var service = new ProjectManagementService();
        var oldFolder = context.Project.ProjectFolder;
        var locked = Path.Combine(oldFolder, "CAD", "图1.dwg");
        var plan = service.PlanRenameProject("甲项目", "丁项目", true);
        Assert(plan.CanExecute, "改名方案被拦住");

        ProjectManagementResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = service.Execute(plan);

        Assert(!result.Succeeded, "文件被占用时改名居然成功了");
        Assert(service.LoadProjects().Any(x => x.Name == "甲项目"), "失败后项目名没有回滚");
        Assert(Directory.Exists(oldFolder), "失败后旧文件夹没有恢复");
        Assert(!Directory.Exists(Path.Combine(Path.GetDirectoryName(oldFolder), "丁项目")), "失败后还留下了新文件夹");
        Assert(File.Exists(locked), "失败后图纸丢失");

        Console.WriteLine("PASS RollsBackWhenRenameMoveFails");
    }

    private sealed class ProjectContext
    {
        public ProjectProfile Project { get; set; }
    }

    /// <summary>造一份和 CAD 落盘形态一致的项目状态：项目列表 + 投影 + 云同步映射 + 图框模板。</summary>
    private static ProjectContext Prepare(string root, string name)
    {
        var userRoot = Path.Combine(root, name, "用户配置");
        Directory.CreateDirectory(userRoot);
        UserDataPaths.TestRootDirectory = userRoot;
        var workspace = CloudProjectWorkspaceService.GetWorkspaceRoot();
        Directory.CreateDirectory(workspace);

        var folder = Path.Combine(workspace, "甲项目");
        var cad = Path.Combine(folder, "CAD");
        Directory.CreateDirectory(cad);
        var first = Path.Combine(cad, "图1.dwg");
        var second = Path.Combine(cad, "图2.dwg");
        File.WriteAllText(first, "dwg-one");
        File.WriteAllText(second, "dwg-two");
        var templateFolder = Path.Combine(UserDataPaths.FrameTemplatesDirectory, "甲项目");
        Directory.CreateDirectory(templateFolder);
        File.WriteAllText(Path.Combine(templateFolder, "A3_A3_BPP.dwg"), "template");

        var project = new ProjectProfile
        {
            Name = "甲项目",
            ProjectFolder = folder,
            OutputDirectory = Path.Combine(folder, "PDF输出"),
            CadFiles = new List<string> { first, second },
            SelectedCadFiles = new List<string> { first },
            SavedSheets = new List<SheetCatalogItem>
            {
                new SheetCatalogItem { SheetName = "平面", SourceFile = first, SourceLayout = "模型空间", BlockHandle = "1" },
                new SheetCatalogItem { SheetName = "总平面", SourceFile = first, SourceLayout = "模型空间", BlockHandle = "2" }
            },
            Frames = new List<FrameDefinition>
            {
                new FrameDefinition { BlockName = "A3_BPP", PaperSize = "A3", TemplateRelativePath = Path.Combine("图框模板", "甲项目", "A3_A3_BPP.dwg") }
            }
        };
        new CloudSyncSettingsStore().SaveSettings(new CloudSyncSettings
        {
            SyncProjectFiles = true,
            ProjectWorkspaceRoot = workspace
        });
        // 用产品代码落盘：项目列表 + 投影 + 云同步映射一次生成，等价于 CAD 侧保存后的状态。
        var store = new PublishPlanStore();
        store.SaveProjects(new List<ProjectProfile> { project });
        store.SetActiveProject(project.Name);
        var saved = store.LoadProjects().Single(x => x.Name == "甲项目");
        return new ProjectContext { Project = saved };
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
