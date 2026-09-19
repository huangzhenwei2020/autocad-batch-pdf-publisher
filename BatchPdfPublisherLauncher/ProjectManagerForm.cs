using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisherLauncher
{
    /// <summary>
    /// 启动器里的“项目管理”窗口：不进入 CAD 就能新建 / 重命名 / 删除项目，
    /// 以及重命名项目文件夹里的文件。
    ///
    /// 所有实际改动都由 <see cref="ProjectManagementService"/> 完成（与主插件共用同一份源码），
    /// 因此 CAD 再次打开时项目、扫描结果、云同步登记都是自洽的。
    ///
    /// 前提：AutoCAD 必须关闭。CAD 内存里有一份项目列表，边跑边改会被它覆盖回去。
    /// </summary>
    internal sealed class ProjectManagerForm : AdaptiveForm
    {
        private static readonly Color Navy = Color.FromArgb(18, 52, 91);
        private static readonly Color Cyan = Color.FromArgb(24, 167, 201);
        private static readonly Color Canvas = Color.FromArgb(244, 247, 250);
        private static readonly Color Muted = Color.FromArgb(92, 108, 124);
        private static readonly Color WarnBack = Color.FromArgb(255, 244, 229);
        private static readonly Color WarnText = Color.FromArgb(154, 82, 12);

        private readonly ProjectManagementService _service = new ProjectManagementService();
        private readonly ListBox _projects = new ListBox();
        private readonly ToolTip _projectTip = new ToolTip();
        private readonly Label _cadBanner = new WrapLabel();
        private readonly Button _retestButton = new RoundedButton();
        private readonly TextBox _nameBox = new TextBox();
        private readonly TextBox _folderBox = new TextBox();
        private readonly Label _cloudLabel = new WrapLabel();
        private readonly Label _countLabel = new WrapLabel();
        private readonly ListView _files = new BufferedListView();
        private readonly TextBox _newNameBox = new TextBox();
        private readonly TextBox _newFolderBox = new TextBox();
        private readonly CheckBox _renameFolderBox = new WrapCheckBox();
        private readonly CheckBox _recycleBox = new WrapCheckBox();
        private readonly Label _status = new WrapLabel();
        private readonly List<Button> _guardedButtons = new List<Button>();
        private List<ProjectProfile> _projectsCache = new List<ProjectProfile>();
        private bool _loading;
        private bool _cadRunning;

        public ProjectManagerForm()
        {
            Text = "万落建筑工具 · 项目管理";
            Icon = LoadIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(1120, 760);
            MinimumSize = new Size(420, 360);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            Build();
            RefreshCadState();
            ReloadProjects();
        }

        protected override void Dispose(bool disposing) { if (disposing) _projectTip.Dispose(); base.Dispose(disposing); }
        private void ShowAppearance() { using (var form = new AdaptiveForm { Text = "颜色模式", ClientSize = new Size(300, 140) }) { var body = LauncherUi.Stack(24); LauncherUi.Add(body, LauncherUi.Text("颜色模式", 16, true)); LauncherUi.Add(body, LauncherShell.ThemeControls()); form.Controls.Add(body); form.ShowDialog(this); } }
        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24), BackColor = Canvas };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var shell = new LauncherShell("projects", () => Close(), () => { }, () => ShowAppearance());
            Controls.Add(shell); shell.Body.Controls.Add(root);
            root.Controls.Add(LauncherShell.PageHeading("项目管理", "选择项目，查看文件和项目信息。"), 0, 0);
            var banner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Color.FromArgb(233, 239, 252), Padding = new Padding(12), Margin = new Padding(0, 6, 0, 14) };
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cadBanner.AutoSize = true;
            _cadBanner.Dock = DockStyle.Fill;
            _cadBanner.Margin = new Padding(0, 0, 12, 0);
            _retestButton.Text = "重新检测";
            _retestButton.AutoSize = true;
            _retestButton.Padding = new Padding(8, 5, 8, 5);
            _retestButton.FlatStyle = FlatStyle.Flat;
            _retestButton.FlatAppearance.BorderColor = LauncherUi.Line;
            _retestButton.BackColor = Color.White;
            _retestButton.Click += (s, e) => { RefreshCadState(); ReloadProjects(); };
            banner.Controls.Add(_cadBanner, 0, 0);
            banner.Controls.Add(_retestButton, 1, 0);
            root.Controls.Add(banner, 0, 1);

            var left = new Surface { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White, Padding = new Padding(8), Margin = Padding.Empty };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(LauncherUi.Text("我的项目", 12, true), 0, 0);
            _projects.Dock = DockStyle.Fill;
            _projects.IntegralHeight = false;
            _projects.BorderStyle = BorderStyle.None;
            _projects.HorizontalScrollbar = false;
            _projects.DrawMode = DrawMode.OwnerDrawFixed;
            _projects.FontChanged += (s, e) => _projects.ItemHeight = _projects.Font.Height + LogicalToDeviceUnits(20);
            _projects.DrawItem += (s, e) => {
                if (e.Index < 0) return; bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var fill = new SolidBrush(selected ? LauncherTheme.Sidebar : LauncherTheme.Card)) e.Graphics.FillRectangle(fill, e.Bounds);
                if (selected) using (var pen = new Pen(LauncherUi.Accent, 3)) e.Graphics.DrawLine(pen, e.Bounds.Left + 2, e.Bounds.Top + 6, e.Bounds.Left + 2, e.Bounds.Bottom - 6);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(_projects.Items[e.Index]), _projects.Font, new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 16), e.Bounds.Height), LauncherTheme.Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            _projects.Font = new Font("Microsoft YaHei UI", 10.5F);
            _projects.SelectedIndexChanged += (s, e) => ShowSelectedProject();
            _projects.MouseMove += (s, e) => {
                int index = _projects.IndexFromPoint(e.Location);
                string name = index >= 0 ? Convert.ToString(_projects.Items[index]) : string.Empty;
                if (_projectTip.GetToolTip(_projects) != name) _projectTip.SetToolTip(_projects, name);
            };
            left.Controls.Add(LauncherUi.RoundedHost(_projects), 0, 1);
            var refresh = CreateButton("刷新列表", false);
            refresh.Click += (s, e) => ReloadProjects();
            left.Controls.Add(refresh, 0, 2);
            shell.SidebarContent.Controls.Add(left);

            var tabs = new ThemedTabControl { Dock = DockStyle.Fill, Padding = new Point(12, 8), Multiline = false, Margin = Padding.Empty };
            tabs.DrawItem += (s, e) => { using (var brush = new SolidBrush(e.Index == tabs.SelectedIndex ? LauncherTheme.Card : LauncherTheme.Sidebar)) e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, LauncherTheme.Foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
            root.Controls.Add(tabs, 0, 2);
            var filePage = new TabPage("项目文件") { BackColor = Color.White, Padding = new Padding(0, 12, 0, 0) };
            var infoPage = new TabPage("项目信息") { BackColor = Canvas };
            var newPage = new TabPage("新建项目") { BackColor = Canvas };
            tabs.TabPages.AddRange(new[] { filePage, infoPage, newPage });

            var filesHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            filesHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            filesHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _countLabel.AutoSize = true;
            _countLabel.Dock = DockStyle.Top;
            _countLabel.ForeColor = Navy;
            _countLabel.Margin = new Padding(0, 0, 0, 12);
            filesHost.Controls.Add(_countLabel, 0, 0);
            _files.Dock = DockStyle.Fill;
            _files.View = View.Details;
            _files.FullRowSelect = true;
            _files.MultiSelect = false;
            _files.HideSelection = false;
            _files.BorderStyle = BorderStyle.None;
            _files.Columns.Add("文件", 260);
            _files.OwnerDraw = true;
            _files.DrawColumnHeader += (s, e) => { using (var brush = new SolidBrush(LauncherTheme.Sidebar)) e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header.Text, _files.Font, e.Bounds, LauncherTheme.Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.Left); };
            _files.DrawSubItem += (s, e) => e.DrawDefault = true;
            _files.DrawItem += (s, e) => { if (_files.View != View.Details) e.DrawDefault = true; };
            _files.Columns.Add("大小", 76, HorizontalAlignment.Right);
            _files.Columns.Add("状态", 108);
            _files.Columns.Add("修改时间", 130);
            _files.DoubleClick += (s, e) => RenameSelectedFile();
            _files.SizeChanged += (s, e) => SizeFileColumns();
            _files.FontChanged += (s, e) => MeasureFileColumns();
            _files.ShowItemToolTips = true;
            filesHost.Controls.Add(LauncherUi.RoundedHost(_files), 0, 1);
            var renameFile = CreateButton("重命名文件", true);
            renameFile.Click += (s, e) => RenameSelectedFile();
            _guardedButtons.Add(renameFile);
            var reveal = CreateButton("打开所在文件夹", false);
            reveal.Click += (s, e) => RevealSelectedFile();
            var refreshFiles = CreateButton("刷新文件", false);
            refreshFiles.Click += (s, e) => RefreshFiles();
            filesHost.Controls.Add(LauncherUi.Actions(renameFile, reveal, refreshFiles), 0, 2);
            filePage.Controls.Add(filesHost);

            var info = LauncherUi.Card("项目信息", "选中左侧项目后，可在这里修改名称或打开项目文件夹。");
            infoPage.Controls.Add(LauncherUi.Scroll(info));
            LauncherUi.Field(info, "项目名称", _nameBox);
            _folderBox.ReadOnly = true;
            _folderBox.BackColor = Canvas;
            LauncherUi.Field(info, "项目文件夹", _folderBox);
            _cloudLabel.AutoSize = true;
            _cloudLabel.ForeColor = Muted;
            LauncherUi.Field(info, "同步状态", _cloudLabel);
            _renameFolderBox.Text = "重命名时同步修改磁盘文件夹名称";
            _renameFolderBox.Checked = true;
            _renameFolderBox.AutoSize = true;
            _renameFolderBox.Dock = DockStyle.Top;
            LauncherUi.Add(info, _renameFolderBox);
            var rename = CreateButton("重命名项目", true);
            rename.Click += (s, e) => RenameSelectedProject();
            _guardedButtons.Add(rename);
            var open = CreateButton("打开文件夹", false);
            open.Click += (s, e) => OpenProjectFolder();
            LauncherUi.Add(info, LauncherUi.Actions(rename, open));
            var danger = LauncherUi.Text("移除项目", 11, true);
            danger.Margin = new Padding(0, 24, 0, 8);
            LauncherUi.Add(info, danger);
            _recycleBox.Text = "同时将项目文件夹移到回收站（可还原）";
            _recycleBox.AutoSize = true;
            _recycleBox.Dock = DockStyle.Top;
            LauncherUi.Add(info, _recycleBox);
            var delete = CreateButton("删除项目", false);
            delete.ForeColor = Color.FromArgb(165, 61, 65);
            delete.Click += (s, e) => DeleteSelectedProject();
            _guardedButtons.Add(delete);
            LauncherUi.Add(info, LauncherUi.Actions(delete));

            var createCard = LauncherUi.Card("建立新的项目", "项目名称与文件夹统一管理，后续可直接在 CAD 中使用。");
            newPage.Controls.Add(LauncherUi.Scroll(createCard));
            LauncherUi.Field(createCard, "项目名称", _newNameBox);
            LauncherUi.Field(createCard, "项目文件夹（可选）", _newFolderBox);
            LauncherUi.Add(createCard, LauncherUi.Text("留空时，在工作区中创建同名文件夹。", 9.5F, false, Muted));
            var browse = CreateButton("选择目录…", false);
            browse.Click += (s, e) => ChooseNewFolder();
            var create = CreateButton("新建项目", true);
            create.Click += (s, e) => CreateProject();
            _guardedButtons.Add(create);
            LauncherUi.Add(createCard, LauncherUi.Actions(create, browse));

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 12, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.AutoSize = true;
            _status.Dock = DockStyle.Fill;
            _status.ForeColor = Muted;
            _status.Margin = new Padding(0, 0, 12, 0);
            footer.Controls.Add(_status, 0, 0);
            var close = CreateButton("关闭", false);
            close.DialogResult = DialogResult.OK;
            footer.Controls.Add(close, 1, 0);
            root.Controls.Add(footer, 0, 3);
            CancelButton = close;
        }

        private void RefreshCadState()
        {
            _cadRunning = ProjectManagementService.IsCadRunning();
            _cadBanner.Text = _cadRunning
                ? "检测到 AutoCAD 正在运行：请先关闭 CAD 再改名，否则 CAD 里的项目列表会把改动覆盖回去。"
                : "AutoCAD 未运行 · 可以编辑项目";
            _cadBanner.Tag = _cadRunning ? "warning" : "success";
            LauncherTheme.Apply(_cadBanner);
            foreach (var button in _guardedButtons) button.Enabled = !_cadRunning;
            _status.Text = _cadRunning ? "已锁定：请先关闭 AutoCAD。" : "准备就绪。";
        }

        private void ReloadProjects()
        {
            try
            {
                var selectedName = SelectedProjectName();
                _loading = true;
                _projectsCache = _service.LoadProjects();
                _projects.Items.Clear();
                foreach (var project in _projectsCache.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    _projects.Items.Add(project.Name);
                if (_projects.Items.Count > 0)
                    _projects.SelectedIndex = Math.Max(0, _projects.Items.IndexOf(selectedName ?? string.Empty));
                _status.Text = "共 " + _projects.Items.Count + " 个项目 · 与 CAD 共用项目配置";
                _status.AccessibleDescription = Path.Combine(_service.UserRoot, "通用设置", "项目列表.json");
            }
            catch (Exception exception)
            {
                _status.Text = "读取项目列表失败：" + exception.Message;
            }
            finally { _loading = false; }
            ShowSelectedProject();
        }

        private void ShowSelectedProject()
        {
            if (_loading) return;
            _files.Items.Clear();
            var project = CurrentProject();
            if (project == null)
            {
                _nameBox.Text = string.Empty;
                _folderBox.Text = string.Empty;
                _cloudLabel.Text = string.Empty;
                _countLabel.Text = "项目文件";
                return;
            }
            _nameBox.Text = project.Name;
            _folderBox.Text = _service.GetProjectFolder(project);
            _cloudLabel.Text = _service.DescribeCloudSync(project);
            RefreshFiles();
        }

        private void RefreshFiles()
        {
            var project = CurrentProject();
            _files.BeginUpdate();
            try
            {
                _files.Items.Clear();
                if (project == null) { _countLabel.Text = "项目文件"; return; }
                var entries = _service.ListFiles(project);
                foreach (var entry in entries)
                {
                    var item = new ListViewItem(entry.RelativePath);
                    item.SubItems.Add(entry.SizeText);
                    item.SubItems.Add(entry.StateText);
                    item.SubItems.Add(entry.ModifiedUtc == DateTime.MinValue ? string.Empty : entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                    item.Tag = entry;
                    item.ToolTipText = entry.RelativePath + "\n" + entry.SizeText + " · " + entry.StateText + "\n" + item.SubItems[3].Text;
                    _files.Items.Add(item);
                }
                _countLabel.Text = "项目文件（" + entries.Count + " 个）";
            }
            finally { _files.EndUpdate(); MeasureFileColumns(); }
        }

        private readonly int[] _metadataWidths = new int[3];
        private bool _sizingColumns;
        private void MeasureFileColumns()
        {
            if (_files.Columns.Count < 4) return;
            for (int column = 1; column < 4; column++)
            {
                int width = TextRenderer.MeasureText(_files.Columns[column].Text, _files.Font).Width;
                foreach (ListViewItem item in _files.Items)
                    if (item.SubItems.Count > column) width = Math.Max(width, TextRenderer.MeasureText(item.SubItems[column].Text, _files.Font).Width);
                _metadataWidths[column - 1] = width + LogicalToDeviceUnits(18);
            }
            SizeFileColumns();
        }
        private void SizeFileColumns()
        {
            if (_files.Columns.Count < 4 || _sizingColumns) return;
            _sizingColumns = true;
            try
            {
                int remaining = _files.ClientSize.Width;
                for (int column = 1; column < 4; column++)
                {
                    int width = Math.Max(40, _metadataWidths[column - 1]);
                    if (_files.Columns[column].Width != width) _files.Columns[column].Width = width;
                    remaining -= width;
                }
                int first = Math.Max(LogicalToDeviceUnits(190), remaining);
                if (_files.Columns[0].Width != first) _files.Columns[0].Width = first;
            }
            finally { _sizingColumns = false; }
        }
        private ProjectProfile CurrentProject()
        {
            var name = SelectedProjectName();
            return string.IsNullOrWhiteSpace(name) ? null : ProjectManagementService.FindProject(name, _projectsCache);
        }

        private string SelectedProjectName()
        {
            return _projects.SelectedItem as string;
        }

        // ── 操作 ─────────────────────────────────────────────────────────────

        private void CreateProject()
        {
            if (Blocked()) return;
            var plan = _service.PlanCreateProject(_newNameBox.Text, _newFolderBox.Text);
            if (!Confirm(plan, "新建项目")) return;
            var result = _service.Execute(plan);
            Report(result, "新建项目");
            if (result.Succeeded) { _newNameBox.Text = string.Empty; _newFolderBox.Text = string.Empty; }
            ReloadProjects();
        }

        private void RenameSelectedProject()
        {
            if (Blocked()) return;
            var project = CurrentProject();
            if (project == null) { MessageBox.Show(this, "请先在左侧选择项目。", "重命名项目", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var plan = _service.PlanRenameProject(project.Name, _nameBox.Text, _renameFolderBox.Checked);
            if (!Confirm(plan, "重命名项目")) { ShowSelectedProject(); return; }
            var result = _service.Execute(plan);
            Report(result, "重命名项目");
            ReloadProjects();
            SelectProject(plan.NewName);
        }

        private void DeleteSelectedProject()
        {
            if (Blocked()) return;
            var project = CurrentProject();
            if (project == null) { MessageBox.Show(this, "请先在左侧选择项目。", "删除项目", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var plan = _service.PlanDeleteProject(project.Name, _recycleBox.Checked);
            if (!Confirm(plan, "删除项目")) return;
            var result = _service.Execute(plan);
            Report(result, "删除项目");
            ReloadProjects();
        }

        private void RenameSelectedFile()
        {
            if (Blocked()) return;
            var project = CurrentProject();
            var entry = SelectedFile();
            if (project == null || entry == null) { MessageBox.Show(this, "请先在上方列表选择要改名的文件。", "重命名文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var current = Path.GetFileName(entry.RelativePath);
            string entered;
            using (var prompt = new TextPromptForm("重命名文件", "文件：" + entry.RelativePath, "新文件名", current))
                if (prompt.ShowDialog(this) != DialogResult.OK) return; else entered = prompt.Value;
            var plan = _service.PlanRenameFile(project.Name, entry.RelativePath, entered);
            if (!Confirm(plan, "重命名文件")) return;
            var result = _service.Execute(plan);
            Report(result, "重命名文件");
            RefreshFiles();
        }

        private void OpenProjectFolder()
        {
            var project = CurrentProject();
            if (project == null) return;
            OpenInExplorer(_service.GetProjectFolder(project));
        }

        private void RevealSelectedFile()
        {
            var entry = SelectedFile();
            if (entry == null) return;
            OpenInExplorer(Path.GetDirectoryName(entry.FullPath));
        }

        private void ChooseNewFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择新项目的文件夹（留空则用工作区目录下的同名文件夹）" })
                if (dialog.ShowDialog(this) == DialogResult.OK) _newFolderBox.Text = dialog.SelectedPath;
        }

        private bool Blocked()
        {
            RefreshCadState();
            if (!_cadRunning) return false;
            MessageBox.Show(this, "AutoCAD 正在运行，请先关闭 CAD 再执行项目管理操作。\r\n\r\n"
                + "原因：CAD 内存里保存着一份项目列表，正在运行的 CAD 会在保存时把它写回磁盘，覆盖这里的改动。",
                "请先关闭 AutoCAD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        private ProjectFileEntry SelectedFile()
        {
            if (_files.SelectedItems.Count == 0) return null;
            return _files.SelectedItems[0].Tag as ProjectFileEntry;
        }

        private void SelectProject(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var index = _projects.Items.IndexOf(name);
            if (index >= 0) _projects.SelectedIndex = index;
        }

        // ── 确认与结果 ───────────────────────────────────────────────────────

        private bool Confirm(ProjectManagementPlan plan, string title)
        {
            if (!plan.CanExecute)
            {
                MessageBox.Show(this, string.Join("\r\n", plan.Blockers.ToArray()), title + " · 无法执行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var lines = new List<string> { plan.Summary, string.Empty };
            if (plan.Moves.Count > 0)
            {
                lines.Add("将搬移 " + plan.Moves.Count + " 项：");
                foreach (var move in plan.Moves.Take(6)) lines.Add("  · " + move.ToString());
                if (plan.Moves.Count > 6) lines.Add("  · …另外 " + (plan.Moves.Count - 6) + " 项");
                lines.Add(string.Empty);
            }
            if (plan.References.Count > 0)
            {
                lines.Add("将改写 " + plan.References.Count + " 处登记路径：");
                foreach (var reference in plan.References.Take(5)) lines.Add("  · " + reference.Where + "：" + reference.From);
                if (plan.References.Count > 5) lines.Add("  · …另外 " + (plan.References.Count - 5) + " 处");
                lines.Add(string.Empty);
            }
            foreach (var warning in plan.Warnings) lines.Add("注意：" + warning);
            lines.Add(string.Empty);
            lines.Add("确认执行吗？（会自动备份执行前的项目配置）");
            return MessageBox.Show(this, string.Join("\r\n", lines.ToArray()), title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void Report(ProjectManagementResult result, string title)
        {
            if (result == null) return;
            _status.Text = result.Succeeded ? ("已完成：" + title) : ("失败：" + title);
            MessageBox.Show(this, result.Describe(), title, MessageBoxButtons.OK,
                result.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        // ── 控件工厂 ─────────────────────────────────────────────────────────

        private static Label Title(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold), ForeColor = Navy, Margin = new Padding(0, 0, 0, 8) };
        }

        private static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Height = 28, Margin = new Padding(0, 3, 6, 3), ForeColor = Color.FromArgb(47, 63, 78) };
        }

        private static Button CreateButton(string text, bool accent)
        {
            return LauncherUi.Button(text, accent);
        }

        private static void OpenInExplorer(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception exception) { MessageBox.Show("打开目录失败：" + exception.Message, "项目管理", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
        }

        /// <summary>输入一行文字的简单对话框（改名用）。</summary>
        private sealed class TextPromptForm : AdaptiveForm
        {
            private readonly TextBox _box = new TextBox();
            public string Value { get { return _box.Text.Trim(); } }

            public TextPromptForm(string title, string caption, string label, string value)
            {
                Text = title;
                ClientSize = new Size(520, 300);
                MinimumSize = new Size(360, 240);
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var content = LauncherUi.Stack(20);
                LauncherUi.Add(content, LauncherUi.Text(caption, 9.5F, false, Muted));
                _box.Text = value ?? string.Empty;
                LauncherUi.Field(content, label, _box);
                root.Controls.Add(LauncherUi.Scroll(content), 0, 0);
                var cancel = CreateButton("取消", false);
                cancel.DialogResult = DialogResult.Cancel;
                var ok = CreateButton("确定", true);
                ok.DialogResult = DialogResult.OK;
                var actions = LauncherUi.Actions(ok, cancel);
                actions.Padding = new Padding(20, 8, 20, 8);
                root.Controls.Add(actions, 0, 1);
                Controls.Add(root);
                AcceptButton = ok;
                CancelButton = cancel;
                Shown += (s, e) => { _box.Focus(); _box.SelectAll(); };
            }
        }
    }
}
