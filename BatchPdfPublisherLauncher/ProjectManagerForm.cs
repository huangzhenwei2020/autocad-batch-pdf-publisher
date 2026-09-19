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
    internal sealed class ProjectManagerForm : Form
    {
        private static readonly Color Navy = Color.FromArgb(18, 52, 91);
        private static readonly Color Cyan = Color.FromArgb(24, 167, 201);
        private static readonly Color Canvas = Color.FromArgb(244, 247, 250);
        private static readonly Color Muted = Color.FromArgb(92, 108, 124);
        private static readonly Color WarnBack = Color.FromArgb(255, 244, 229);
        private static readonly Color WarnText = Color.FromArgb(154, 82, 12);

        private readonly ProjectManagementService _service = new ProjectManagementService();
        private readonly ListBox _projects = new ListBox();
        private readonly Label _cadBanner = new Label();
        private readonly Button _retestButton = new Button();
        private readonly TextBox _nameBox = new TextBox();
        private readonly TextBox _folderBox = new TextBox();
        private readonly Label _cloudLabel = new Label();
        private readonly Label _countLabel = new Label();
        private readonly ListView _files = new ListView();
        private readonly TextBox _newNameBox = new TextBox();
        private readonly TextBox _newFolderBox = new TextBox();
        private readonly CheckBox _renameFolderBox = new CheckBox();
        private readonly CheckBox _recycleBox = new CheckBox();
        private readonly Label _status = new Label();
        private readonly List<Button> _guardedButtons = new List<Button>();
        private List<ProjectProfile> _projectsCache = new List<ProjectProfile>();
        private SplitContainer _split;
        private bool _loading;
        private bool _cadRunning;

        public ProjectManagerForm()
        {
            Text = "万落建筑工具 · 项目管理";
            Icon = LoadIcon();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Canvas;
            Font = new Font("Microsoft YaHei UI", 9F);
            Build();
            RefreshCadState();
            ReloadProjects();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Canvas, Padding = new Padding(16, 14, 16, 12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ── CAD 运行提示条 ────────────────────────────────────────────────
            var banner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = WarnBack, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _cadBanner.AutoSize = true;
            _cadBanner.ForeColor = WarnText;
            _cadBanner.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _cadBanner.Text = "检测到 AutoCAD 正在运行：请先关闭 CAD，否则改名会被 CAD 里的项目列表覆盖。";
            _retestButton.Text = "重新检测";
            _retestButton.AutoSize = true;
            _retestButton.Height = 28;
            _retestButton.FlatStyle = FlatStyle.Flat;
            _retestButton.BackColor = Color.White;
            _retestButton.ForeColor = WarnText;
            _retestButton.FlatAppearance.BorderColor = Color.FromArgb(224, 178, 128);
            _retestButton.Click += (s, e) => { RefreshCadState(); ReloadProjects(); };
            banner.Controls.Add(_cadBanner, 0, 0);
            banner.Controls.Add(_retestButton, 1, 0);
            root.Controls.Add(banner, 0, 0);

            // ── 主体：左项目列表 / 右详情 ─────────────────────────────────────
            // 注意：SplitContainer 的 Panel1MinSize / Panel2MinSize / SplitterDistance
            // 只能在控件已经拿到真实宽度之后设置，构造期设会抛
            // “SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间”。
            // 因此这里只建控件，尺寸在 OnLoad 的 ApplySplitLayout 里给。
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 8, BackColor = Canvas };
            _split = split;
            split.SizeChanged += (s, e) => ApplySplitLayout();
            root.Controls.Add(split, 0, 1);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 0, 8, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(Title("项目列表"), 0, 0);
            _projects.Dock = DockStyle.Fill;
            _projects.IntegralHeight = false;
            _projects.BorderStyle = BorderStyle.FixedSingle;
            _projects.SelectedIndexChanged += (s, e) => ShowSelectedProject();
            left.Controls.Add(_projects, 0, 1);
            var refresh = CreateButton("刷新列表", false);
            refresh.Margin = new Padding(0, 8, 0, 0);
            refresh.Click += (s, e) => ReloadProjects();
            left.Controls.Add(refresh, 0, 2);
            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(16, 12, 16, 12) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Panel2.Controls.Add(right);

            // 工程信息
            right.Controls.Add(Title("项目信息与重命名"), 0, 0);
            var info = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 6) };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.Controls.Add(FieldLabel("项目名称"), 0, 0);
            _nameBox.Dock = DockStyle.Fill;
            _nameBox.Height = 28;
            _nameBox.Margin = new Padding(0, 3, 8, 3);
            info.Controls.Add(_nameBox, 1, 0);
            var rename = CreateButton("重命名项目", true);
            rename.Margin = new Padding(0, 2, 6, 2);
            rename.Click += (s, e) => RenameSelectedProject();
            _guardedButtons.Add(rename);
            info.Controls.Add(rename, 2, 0);
            var openFolder = CreateButton("打开文件夹", false);
            openFolder.Margin = new Padding(0, 2, 0, 2);
            openFolder.Click += (s, e) => OpenProjectFolder();
            info.Controls.Add(openFolder, 3, 0);
            info.Controls.Add(FieldLabel("项目文件夹"), 0, 1);
            _folderBox.Dock = DockStyle.Fill;
            _folderBox.ReadOnly = true;
            _folderBox.BackColor = Color.FromArgb(248, 250, 252);
            _folderBox.Height = 28;
            _folderBox.Margin = new Padding(0, 3, 8, 3);
            info.Controls.Add(_folderBox, 1, 1);
            info.SetColumnSpan(_folderBox, 3);
            _cloudLabel.AutoSize = true;
            _cloudLabel.ForeColor = Muted;
            _cloudLabel.Margin = new Padding(0, 2, 0, 0);
            info.Controls.Add(FieldLabel("同步状态"), 0, 2);
            info.Controls.Add(_cloudLabel, 1, 2);
            info.SetColumnSpan(_cloudLabel, 3);
            _renameFolderBox.Text = "重命名项目时连同磁盘文件夹一起改（推荐）";
            _renameFolderBox.Checked = true;
            _renameFolderBox.AutoSize = true;
            _renameFolderBox.ForeColor = Color.FromArgb(47, 63, 78);
            _renameFolderBox.Margin = new Padding(0, 4, 0, 0);
            info.Controls.Add(_renameFolderBox, 1, 3);
            info.SetColumnSpan(_renameFolderBox, 3);
            right.Controls.Add(info, 0, 1);

            // 新建 / 删除
            var operations = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 6, 0, 6) };
            operations.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            operations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            operations.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            operations.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            operations.Controls.Add(FieldLabel("新建项目"), 0, 0);
            _newNameBox.Dock = DockStyle.Fill;
            _newNameBox.Height = 28;
            _newNameBox.Margin = new Padding(0, 3, 8, 3);
            operations.Controls.Add(_newNameBox, 1, 0);
            var create = CreateButton("新建项目", false);
            create.Margin = new Padding(0, 2, 6, 2);
            create.Click += (s, e) => CreateProject();
            _guardedButtons.Add(create);
            var browse = CreateButton("选择目录…", false);
            browse.Margin = new Padding(0, 2, 0, 2);
            browse.Click += (s, e) => ChooseNewFolder();
            operations.Controls.Add(create, 2, 0);
            operations.Controls.Add(browse, 3, 0);
            operations.Controls.Add(FieldLabel("文件夹"), 0, 1);
            _newFolderBox.Dock = DockStyle.Fill;
            _newFolderBox.Height = 28;
            _newFolderBox.Margin = new Padding(0, 3, 8, 3);
            operations.Controls.Add(_newFolderBox, 1, 1);
            operations.SetColumnSpan(_newFolderBox, 3);
            var folderHint = new Label { Text = "文件夹留空时，使用工作区目录（我的文档\\万落建筑项目）下的同名文件夹。", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 2, 0, 0) };
            operations.Controls.Add(folderHint, 1, 2);
            operations.SetColumnSpan(folderHint, 3);
            _recycleBox.Text = "删除项目时把项目文件夹移到回收站（可还原）";
            _recycleBox.AutoSize = true;
            _recycleBox.ForeColor = Color.FromArgb(47, 63, 78);
            _recycleBox.Margin = new Padding(0, 6, 0, 0);
            operations.Controls.Add(_recycleBox, 1, 3);
            operations.SetColumnSpan(_recycleBox, 2);
            var delete = CreateButton("删除项目", false);
            delete.ForeColor = Color.FromArgb(157, 66, 61);
            delete.Margin = new Padding(0, 2, 0, 2);
            delete.Click += (s, e) => DeleteSelectedProject();
            _guardedButtons.Add(delete);
            operations.Controls.Add(delete, 3, 3);
            right.Controls.Add(operations, 0, 2);

            // 项目文件
            var filesHost = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0, 4, 0, 0) };
            filesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            filesHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filesHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _countLabel.AutoSize = true;
            _countLabel.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            _countLabel.ForeColor = Navy;
            _countLabel.Margin = new Padding(0, 0, 0, 6);
            filesHost.Controls.Add(_countLabel, 0, 0);
            _files.Dock = DockStyle.Fill;
            _files.View = View.Details;
            _files.FullRowSelect = true;
            _files.MultiSelect = false;
            _files.HideSelection = false;
            _files.BorderStyle = BorderStyle.FixedSingle;
            _files.Columns.Add("文件", 380);
            _files.Columns.Add("大小", 84, HorizontalAlignment.Right);
            _files.Columns.Add("状态", 132);
            _files.Columns.Add("修改时间", 132);
            _files.DoubleClick += (s, e) => RenameSelectedFile();
            filesHost.Controls.Add(_files, 0, 1);
            var fileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            var renameFile = CreateButton("重命名文件", true);
            renameFile.Click += (s, e) => RenameSelectedFile();
            _guardedButtons.Add(renameFile);
            var revealFile = CreateButton("打开所在文件夹", false);
            revealFile.Click += (s, e) => RevealSelectedFile();
            var refreshFiles = CreateButton("刷新文件", false);
            refreshFiles.Click += (s, e) => RefreshFiles();
            fileButtons.Controls.Add(renameFile);
            fileButtons.Controls.Add(revealFile);
            fileButtons.Controls.Add(refreshFiles);
            fileButtons.Controls.Add(new Label { Text = "双击文件也可改名；只能改项目文件夹里的文件，外部登记的图纸不受影响。", AutoSize = true, ForeColor = Muted, Margin = new Padding(6, 9, 0, 0) });
            filesHost.Controls.Add(fileButtons, 0, 2);
            right.Controls.Add(filesHost, 0, 3);

            // ── 状态栏 ────────────────────────────────────────────────────────
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.AutoSize = true;
            _status.ForeColor = Muted;
            _status.MaximumSize = new Size(760, 48);
            footer.Controls.Add(_status, 0, 0);
            var close = CreateButton("关闭", false);
            close.DialogResult = DialogResult.OK;
            close.Margin = new Padding(8, 0, 0, 0);
            footer.Controls.Add(close, 1, 0);
            root.Controls.Add(footer, 0, 2);
            CancelButton = close;
            AcceptButton = close;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplySplitLayout();
        }

        /// <summary>按当前宽度给左右分栏设最小宽度与初始位置（宽度不够时保持默认，绝不让它抛异常）。</summary>
        private void ApplySplitLayout()
        {
            if (_split == null) return;
            var width = _split.ClientSize.Width;
            if (width <= 0) return;
            try
            {
                const int panel1Min = 200;
                var panel2Min = Math.Max(240, Math.Min(440, width - panel1Min - _split.SplitterWidth - 40));
                _split.Panel1MinSize = panel1Min;
                _split.Panel2MinSize = panel2Min;
                var max = width - panel2Min - _split.SplitterWidth;
                if (max <= panel1Min) return;
                var desired = (int)(width * 0.28);
                _split.SplitterDistance = Math.Max(panel1Min, Math.Min(max, desired));
            }
            catch (InvalidOperationException) { }
            catch (ArgumentOutOfRangeException) { }
        }

        // ── 状态 ─────────────────────────────────────────────────────────────

        private void RefreshCadState()
        {
            _cadRunning = ProjectManagementService.IsCadRunning();
            _cadBanner.Text = _cadRunning
                ? "检测到 AutoCAD 正在运行：请先关闭 CAD 再改名，否则 CAD 里的项目列表会把改动覆盖回去。"
                : "AutoCAD 未运行，可以安全修改项目。修改完成后进入 CAD 会自动读取新配置。";
            _cadBanner.ForeColor = _cadRunning ? WarnText : Color.FromArgb(32, 106, 62);
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
                _status.Text = "共 " + _projects.Items.Count + " 个项目。项目配置：" + Path.Combine(_service.UserRoot, "通用设置", "项目列表.json");
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
                    _files.Items.Add(item);
                }
                _countLabel.Text = "项目文件（" + entries.Count + " 个）";
            }
            finally { _files.EndUpdate(); }
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
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                MinimumSize = new Size(96, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Cyan : Color.White,
                ForeColor = accent ? Color.White : Navy,
                Font = new Font("Microsoft YaHei UI", 9F, accent ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 2, 6, 2)
            };
            button.FlatAppearance.BorderColor = accent ? Cyan : Color.FromArgb(202, 213, 222);
            button.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(19, 143, 174) : Color.FromArgb(244, 247, 250);
            return button;
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
        private sealed class TextPromptForm : Form
        {
            private readonly TextBox _box = new TextBox();
            public string Value { get { return _box.Text.Trim(); } }

            public TextPromptForm(string title, string caption, string label, string value)
            {
                Text = title;
                AutoScaleMode = AutoScaleMode.Dpi;
                AutoScaleDimensions = new SizeF(96F, 96F);
                ClientSize = new Size(460, 170);
                MinimumSize = new Size(420, 170);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                BackColor = Color.White;
                Font = new Font("Microsoft YaHei UI", 9F);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Padding = new Padding(16, 14, 16, 12) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var captionLabel = new Label { Text = caption, AutoSize = true, ForeColor = Muted, MaximumSize = new Size(400, 40), Margin = new Padding(0, 0, 0, 8) };
                root.Controls.Add(captionLabel, 0, 0);
                root.SetColumnSpan(captionLabel, 2);
                root.Controls.Add(FieldLabel(label), 0, 1);
                _box.Dock = DockStyle.Fill;
                _box.Height = 28;
                _box.Text = value ?? string.Empty;
                _box.Margin = new Padding(0, 3, 0, 3);
                _box.SelectAll();
                root.Controls.Add(_box, 1, 1);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 8, 0, 0) };
                var cancel = CreateButton("取消", false);
                cancel.DialogResult = DialogResult.Cancel;
                var ok = CreateButton("确定", true);
                ok.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                root.Controls.Add(buttons, 0, 3);
                root.SetColumnSpan(buttons, 2);
                Controls.Add(root);
                AcceptButton = ok;
                CancelButton = cancel;
                Shown += (s, e) => { _box.Focus(); _box.SelectAll(); };
            }
        }
    }
}
