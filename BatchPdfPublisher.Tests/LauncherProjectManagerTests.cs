using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisherLauncher;

/// <summary>
/// 启动器“项目管理”窗口的冒烟测试：真正把窗体构造出来（STA），检查控件装配、
/// 项目列表装载、CAD 运行锁与按钮文案是否齐全。
///
/// 窗体构造函数会读项目列表并渲染文件列表，是最容易在重构后悄悄炸掉的地方；
/// 这里用临时用户目录（UserDataPathsStub），不会碰到真实用户配置。
/// </summary>
internal static class LauncherProjectManagerTests
{
    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoLauncherFormTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var workspace = CloudProjectWorkspaceService.GetWorkspaceRoot();
            Directory.CreateDirectory(workspace);
            var folder = Path.Combine(workspace, "冒烟项目");
            Directory.CreateDirectory(Path.Combine(folder, "CAD"));
            File.WriteAllText(Path.Combine(folder, "CAD", "平面.dwg"), "dwg");
            new CloudSyncSettingsStore().SaveSettings(new CloudSyncSettings { SyncProjectFiles = true, ProjectWorkspaceRoot = workspace });
            new PublishPlanStore().SaveProjects(new List<ProjectProfile>
            {
                new ProjectProfile { Name = "冒烟项目", ProjectFolder = folder, CadFiles = new List<string> { Path.Combine(folder, "CAD", "平面.dwg") } }
            });

            using (var form = new ProjectManagerForm())
            {
                // 真正显示一次：OnLoad 里才设分栏尺寸，构造函数不显示就测不到那条路径。
                form.Show();
                Application.DoEvents();
                Assert(form.Text.IndexOf("项目管理", StringComparison.Ordinal) >= 0, "窗口标题里没有“项目管理”");
                var texts = Descendants(form).OfType<Control>().Select(control => control.Text ?? string.Empty).ToList();
                foreach (var expected in new[] { "新建项目", "重命名项目", "删除项目", "重命名文件", "关闭", "刷新列表" })
                    Assert(texts.Any(text => text == expected), "缺少按钮：" + expected);
                Assert(texts.Any(text => text.IndexOf("AutoCAD", StringComparison.Ordinal) >= 0), "缺少 CAD 运行状态提示");

                var list = Descendants(form).OfType<ListBox>().Single();
                Assert(list.Items.Count == 1 && Convert.ToString(list.Items[0]) == "冒烟项目", "项目列表没有装载出来");

                var files = Descendants(form).OfType<ListView>().Single();
                Assert(files.Items.Count == 1 && files.Items[0].Text.EndsWith("平面.dwg", StringComparison.OrdinalIgnoreCase), "项目文件列表没有装载出来");
                var entry = files.Items[0].Tag as ProjectFileEntry;
                Assert(entry != null && entry.Registered, "文件条目缺少登记状态");

                var status = Descendants(form).OfType<Label>().Select(label => label.Text ?? string.Empty)
                    .FirstOrDefault(text => text.IndexOf("通用设置", StringComparison.Ordinal) >= 0);
                Assert(status != null, "状态栏没有显示项目配置路径");

                // CAD 在运行时所有写操作都必须锁住（这里只验证锁定逻辑本身，不依赖本机有没有开 CAD）。
                var service = new ProjectManagementService();
                Assert(service.LoadProjects().Count == 1, "服务没有读到测试项目");
                foreach (var button in Descendants(form).OfType<Button>().Where(x => x.Text == "新建项目" || x.Text == "删除项目" || x.Text == "重命名项目"))
                    Assert(button.Enabled == !ProjectManagementService.IsCadRunning(),
                        "CAD 运行状态与按钮可用状态不一致：" + button.Text);
                form.Close();
            }

            Console.WriteLine("PASS LauncherProjectManagerFormBuildsAndBinds");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
