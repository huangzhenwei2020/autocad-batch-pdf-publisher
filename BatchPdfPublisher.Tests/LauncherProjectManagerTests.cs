using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll")] private static extern IntPtr GetWindowTheme(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    private static IntPtr Coordinates(Point point) { return (IntPtr)((point.Y << 16) | (point.X & 0xffff)); }
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.Combine(Path.GetTempPath(), "WanluoLauncherFormTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var workspace = Path.Combine(root, "workspace");
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
                form.ShowInTaskbar = false;
                form.Opacity = 0;
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
                    .FirstOrDefault(text => text.IndexOf("共用项目配置", StringComparison.Ordinal) >= 0);
                Assert(status != null, "状态栏没有显示项目共享状态");

                // CAD 在运行时所有写操作都必须锁住（这里只验证锁定逻辑本身，不依赖本机有没有开 CAD）。
                var service = new ProjectManagementService();
                Assert(service.LoadProjects().Count == 1, "服务没有读到测试项目");
                foreach (var button in Descendants(form).OfType<Button>().Where(x => x.Text == "新建项目" || x.Text == "删除项目" || x.Text == "重命名项目"))
                    Assert(button.Enabled == !ProjectManagementService.IsCadRunning(),
                        "CAD 运行状态与按钮可用状态不一致：" + button.Text);
                var preview = Environment.GetEnvironmentVariable("WANLUO_UI_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(preview)) Directory.CreateDirectory(preview);
                var tabs = Descendants(form).OfType<TabControl>().Single();
                var shell = Descendants(form).OfType<LauncherShell>().Single();
                Assert(Descendants(shell.SidebarContent).Contains(list), "项目列表必须位于左侧导航下方");
                foreach (var width in new[] { 1120, 760, 480 })
                {
                    form.ClientSize = new Size(width, 720);
                    Application.DoEvents();
                    AssertLeftNavigation(form);
                    Assert(tabs.Width > shell.Body.Width - 64, "右侧详情没有占满腾出的空间");
                    for (var page = 0; page < tabs.TabCount; page++)
                    {
                        tabs.SelectedIndex = page;
                        Application.DoEvents();
                        foreach (var button in Descendants(tabs.SelectedTab).OfType<Button>())
                            Assert(button.Right <= button.Parent.ClientSize.Width && button.Width > 60, "按钮被横向裁切：" + button.Text + " width=" + width);
                        if (!string.IsNullOrEmpty(preview))
                            using (var bitmap = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                                bitmap.Save(Path.Combine(preview, "projects-" + width + "-" + page + ".png"));
                            }
                    }
                }
                var brand = Descendants(form).Single(c => c.Name == "BrandHeader");
                var title = Descendants(form).Single(c => c.Name == "PageTitle");
                Assert(brand.Height == title.Height && Math.Abs(brand.PointToScreen(Point.Empty).Y-title.PointToScreen(Point.Empty).Y)<=1, "品牌和页面标题应同高对齐");
                Assert((GetWindowLongPtr(form.Handle, -16).ToInt64() & 0x00C40000L) == 0, "自绘窗口不能重新带回系统标题/厚边框");
                var titleOffset = form.PointToClient(title.PointToScreen(Point.Empty));
                for (int activation = 0; activation < 6; activation++)
                {
                    SendMessage(form.Handle, 0x86, (IntPtr)(activation % 2), IntPtr.Zero);
                    form.Location = new Point(form.Left + (activation % 2 == 0 ? 8 : -8), form.Top);
                    Application.DoEvents();
                    Assert(form.Size == form.ClientSize, "激活或移动后重新出现非客户区边框");
                    Assert(form.PointToClient(title.PointToScreen(Point.Empty)) == titleOffset, "激活或移动后标题位置发生偏移");
                }
                foreach (var edge in new[] { new Point(1, form.Height/2), new Point(form.Width-1, form.Height/2), new Point(form.Width/2, 1), new Point(form.Width/2, form.Height-1) })
                    Assert(!Descendants(form).OfType<LauncherShell>().Single().Bounds.Contains(edge), "子控件挡住了拉伸边缘");
                Assert(SendMessage(form.Handle, 0x84, IntPtr.Zero, Coordinates(form.PointToScreen(new Point(1, 1)))).ToInt32() == 13, "左上角必须支持拖动缩放");
                Assert(SendMessage(form.Handle, 0x84, IntPtr.Zero, Coordinates(form.PointToScreen(new Point(form.ClientSize.Width-1, form.ClientSize.Height-1)))).ToInt32() == 17, "右下角必须支持拖动缩放");
                Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "最大化").PerformClick(); Application.DoEvents();
                Assert(form.Bounds == Screen.FromControl(form).WorkingArea, "最大化不应覆盖任务栏");
                Descendants(form).OfType<Button>().Single(b => b.AccessibleName == "最大化").PerformClick(); Application.DoEvents();
                Assert(form.WindowState == FormWindowState.Normal, "标题按钮必须支持还原窗口");

                foreach (var dark in new[] { true, false })
                {
                    LauncherTheme.Set(dark);
                    Application.DoEvents();
                    Assert(form.BackColor == LauncherTheme.Background, "主题没有应用到当前窗口");
                    Assert(File.ReadAllText(Path.Combine(UserDataPaths.SettingsDirectory, "launcher-theme.txt")) == (dark ? "dark" : "light"), "主题没有保存");
                    Assert(list.BackColor == LauncherTheme.Card && list.ForeColor == LauncherTheme.Foreground, "项目列表主题未更新");
                    if (!string.IsNullOrEmpty(preview))
                    {
                        form.ClientSize = new Size(1120, 760); tabs.SelectedIndex = 0; Application.DoEvents();
                        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(preview, "projects-theme-" + dark + ".png")); }
                    }
                }
                // Scrolling a long native list must not rebuild the outer page layout.
                tabs.SelectedIndex = 0;
                files.BeginUpdate();
                for (int i = 0; i < 1000; i++)
                {
                    var row = new ListViewItem("CAD\\建筑施工图_长文件名_" + i + ".dwg");
                    row.SubItems.Add("128.45 MB"); row.SubItems.Add("已登记"); row.SubItems.Add("2026-09-19 13:00"); files.Items.Add(row);
                }
                files.EndUpdate(); Application.DoEvents();
                LauncherTheme.Set(true); form.ClientSize = new Size(650, 650); Application.DoEvents();
                Assert(GetWindowTheme(files.Handle) == IntPtr.Zero, "滚动条不能再由系统悬停动画覆盖");
                foreach (var vertical in new[] { true, false })
                {
                    var bar = NativeScrollTheme.BarBounds(files, vertical);
                    Assert(!bar.IsEmpty, "悬停检查需要两个方向的滚动条");
                    var sample = new Point(bar.Left + 1, bar.Top + bar.Height / 2);
                    if (!vertical) sample = new Point(bar.Left + bar.Width / 2, bar.Top + 1);
                    foreach (var message in new[] { 0xA0, 0x2A2 })
                    {
                        SendMessage(files.Handle, message, (IntPtr)(vertical ? 7 : 6), Coordinates(files.PointToScreen(sample)));
                        var until = DateTime.UtcNow.AddMilliseconds(150);
                        while (DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        var dc = GetWindowDC(files.Handle);
                        try { var pixel = GetPixel(dc, sample.X, sample.Y); Assert(pixel != 0xffffffff && (pixel & 255) < 100 && ((pixel >> 8) & 255) < 100, "悬停/移出滚动条出现白底"); }
                        finally { ReleaseDC(files.Handle, dc); }
                    }
                }
                Console.WriteLine("PASS native scrollbar hover/leave colors and disabled theme animations");
                using (var bitmap = new Bitmap(files.Width, files.Height))
                {
                    files.DrawToBitmap(bitmap, new Rectangle(Point.Empty, files.Size));
                    foreach (var vertical in new[] { true, false })
                    {
                        var bar = NativeScrollTheme.BarBounds(files, vertical);
                        Assert(!bar.IsEmpty, "测试应覆盖横向和纵向滚动条");
                        var sample = bitmap.GetPixel(vertical ? bar.Left + 1 : bar.Left + bar.Width / 2, vertical ? bar.Top + bar.Height / 2 : bar.Top + 1);
                        Assert(sample.R < 100 && sample.G < 100 && sample.B < 120, "夜间滚动条仍有白底");
                    }
                    if (!string.IsNullOrEmpty(preview)) bitmap.Save(Path.Combine(preview, "dark-table-both-scrollbars.png"));
                }
                int pageLayouts = 0; LayoutEventHandler recordLayout = (s, e) => pageLayouts++;
                form.Layout += recordLayout;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 30; i++) { files.EnsureVisible(i % 2 == 0 ? files.Items.Count - 1 : 0); files.Update(); Application.DoEvents(); }
                timer.Stop(); form.Layout -= recordLayout;
                Assert(pageLayouts == 0, "滚动文件列表不应触发整页重新布局");
                Assert(tabs.Dock == DockStyle.Fill && !(tabs.Parent is Panel scrollingPanel && scrollingPanel.AutoScroll), "项目管理不应嵌套整页滚动");
                Console.WriteLine("PASS 1001 file rows, 30 scroll jumps: " + timer.ElapsedMilliseconds + " ms, outer layouts=" + pageLayouts);
                // Exercise the same enter/exit size-move hooks used by dragging the window border.
                var beginResize = typeof(AdaptiveForm).GetMethod("OnResizeBegin", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var endResize = typeof(AdaptiveForm).GetMethod("OnResizeEnd", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                pageLayouts = 0; form.Layout += recordLayout; timer.Restart();
                beginResize.Invoke(form, new object[] { EventArgs.Empty });
                try { for (int i = 0; i < 80; i++) { form.ClientSize = new Size(980 + i * 2, 600 + i); Application.DoEvents(); } }
                finally { endResize.Invoke(form, new object[] { EventArgs.Empty }); }
                timer.Stop(); form.Layout -= recordLayout;
                Assert(pageLayouts < 40, "拖动窗口时没有合并高频布局计算：" + pageLayouts);
                AssertLeftNavigation(form);
                var finalShell = Descendants(form).OfType<LauncherShell>().Single();
                Assert(finalShell.Bounds == form.DisplayRectangle, "松手后没有更新到最终窗口尺寸");
                Assert(form.Padding.All >= 6, "窗口需要保留不被子控件覆盖的拉伸边缘");
                Assert(form.FormBorderStyle == FormBorderStyle.None, "自绘标题栏不应保留系统边框");
                Console.WriteLine("PASS 80 resize steps: " + timer.ElapsedMilliseconds + " ms, layouts=" + pageLayouts);
                form.Close();
            }

            Console.WriteLine("PASS LauncherProjectManagerFormBuildsAndBinds");
            foreach (var scale in new[] { 1F, 1.25F, 1.5F, 1.75F, 2F, 2.5F, 3F })
            {
                using (var form = new ProjectManagerForm())
                {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    // Stress enlarged controls/fonts without changing the user's monitor settings.
                    // This supplements, rather than substitutes for, real per-monitor DPI testing.
                    var fonts = Descendants(form).Concat(new Control[] { form }).ToDictionary(c => c, c => c.Font);
                    form.Scale(new SizeF(scale, scale));
                    foreach (var entry in fonts) entry.Key.Font = new Font(entry.Value.FontFamily, entry.Value.Size * scale, entry.Value.Style);
                    form.FitWorkingArea();
                    form.ClientSize = new Size(1280, 720);
                    var tabs = Descendants(form).OfType<TabControl>().Single();
                    for (var page = 0; page < tabs.TabCount; page++)
                    {
                        tabs.SelectedIndex = page;
                        Application.DoEvents();
                        foreach (var button in Descendants(tabs.SelectedTab).OfType<Button>())
                            Assert(button.Right <= button.Parent.ClientSize.Width, "放大后按钮横向裁切：" + scale + " " + button.Text);
                    }
                    AssertLeftNavigation(form);
                    Assert(form.CancelButton != null, "缩放后必须仍支持 Esc 关闭");
                }
                Console.WriteLine("PASS LauncherLayoutScaleStress " + scale);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void AssertLeftNavigation(Form form)
    {
        var shell = Descendants(form).OfType<LauncherShell>().Single();
        var layout = shell.Controls.OfType<TableLayoutPanel>().Single();
        var sidebar = layout.GetControlFromPosition(0, 1);
        Assert(sidebar.Visible && sidebar.Width > 0, "确认稿要求左侧导航始终可见");
        Assert(sidebar.Left < shell.Body.Left && sidebar.Right <= shell.Body.Left, "导航必须在内容左边且不能重叠");
        foreach (var name in new[] { "启动工作台", "项目管理", "设置" })
            Assert(Descendants(sidebar).OfType<Button>().Any(b => b.Visible && b.AccessibleName == name), "左栏缺少入口：" + name);
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
