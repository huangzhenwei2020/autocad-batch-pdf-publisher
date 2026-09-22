using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    /// <summary>Centralizes project creation, switching, scan settings and storage.</summary>
    public sealed class ProjectManagerForm : DpiAwareForm
    {
        private readonly PublisherViewModel _viewModel;
        private readonly Action _refreshPublisher;
        private readonly Action _configureScan;
        private readonly ListBox _projects = new ListBox();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _folder = new TextBox();
        private readonly TextBox _autoSaveMinutes = new TextBox { Text = "0" };
        private readonly ToolTip _toolTip = new ToolTip();
        private bool _updatingSelection;
        private bool _folderChosenForNewProject;

        public ProjectManagerForm(PublisherViewModel viewModel, Action refreshPublisher, Action configureScan)
        {
            _viewModel = viewModel;
            _refreshPublisher = refreshPublisher;
            _configureScan = configureScan;
            Text = "项目管理";
            Width = 1040;
            Height = 680;
            MinimumSize = new Size(860, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            SizeGripStyle = SizeGripStyle.Show;
            Build();
            RefreshProjects();
        }

        private void Build()
        {
            BackColor = CadDialogTheme.Canvas;
            ForeColor = CadDialogTheme.Text;
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1, BackColor = CadDialogTheme.Canvas };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            Controls.Add(outer);

            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = CadDialogTheme.Canvas };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(CadDialogTheme.Heading("项目管理"), 0, 0);
            heading.Controls.Add(new Label { Text = "集中管理项目目录、扫描和自动保存", AutoSize = true, ForeColor = CadDialogTheme.Muted, Margin = new Padding(0, 12, 4, 0) }, 1, 0);
            outer.Controls.Add(heading, 0, 0);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CadDialogTheme.Gap));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.Controls.Add(workspace, 0, 1);

            var leftCard = CadDialogTheme.Card();
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = CadDialogTheme.Surface };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(CadDialogTheme.Heading("项目列表"), 0, 0);
            CadDialogTheme.StyleList(_projects);
            left.Controls.Add(new CadListHost(_projects), 0, 1);
            leftCard.Controls.Add(left); workspace.Controls.Add(leftCard, 0, 0);

            var rightCard = CadDialogTheme.Card();
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, RowCount = 8, ColumnCount = 1, BackColor = CadDialogTheme.Surface };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            for (var row = 1; row < 8; row++) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.Controls.Add(CadDialogTheme.Heading("工程信息"), 0, 0);
            var infoGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2, BackColor = CadDialogTheme.Surface };
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            infoGrid.Controls.Add(CadDialogTheme.FieldLabel("工程名称"), 0, 0); infoGrid.Controls.Add(CadDialogTheme.Input(_name), 1, 0); infoGrid.Controls.Add(CadDialogTheme.Button("新建工程", CreateOrSwitch, true), 2, 0);
            infoGrid.Controls.Add(CadDialogTheme.FieldLabel("项目文件夹"), 0, 1); infoGrid.Controls.Add(CadDialogTheme.Input(_folder), 1, 1); infoGrid.Controls.Add(CadDialogTheme.Button("选择目录", ChooseFolder), 2, 1);
            right.Controls.Add(infoGrid, 0, 1);
            right.Controls.Add(CadDialogTheme.Heading("工程操作"), 0, 2);
            var operationButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = CadDialogTheme.Surface };
            operationButtons.Controls.Add(CadDialogTheme.Button("切换项目", SwitchSelected));
            operationButtons.Controls.Add(CadDialogTheme.Button("保存参数", SaveParameters, true));
            operationButtons.Controls.Add(CadDialogTheme.Button("复制迁移项目", MigrateProject));
            operationButtons.Controls.Add(CadDialogTheme.Button("保存 CAD", SaveCurrentCad, true));
            operationButtons.Controls.Add(CadDialogTheme.Button("打开目录", OpenFolder));
            operationButtons.Controls.Add(CadDialogTheme.Button("扫描设置", () => _configureScan()));
            operationButtons.Controls.Add(CadDialogTheme.Button("删除项目", DeleteSelected, false, true));
            right.Controls.Add(operationButtons, 0, 3);
            right.Controls.Add(CadDialogTheme.Heading("自动保存"), 0, 4);
            var autoSaveLine = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, BackColor = CadDialogTheme.Surface };
            autoSaveLine.Controls.Add(new Label { Text = "间隔", Width = 48, Height = CadDialogTheme.ControlHeight, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted, Margin = Padding.Empty });
            autoSaveLine.Controls.Add(CadDialogTheme.Input(_autoSaveMinutes, 82));
            autoSaveLine.Controls.Add(new Label { Text = "分钟（0 表示关闭）", AutoSize = false, Width = 150, Height = CadDialogTheme.ControlHeight, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted, Margin = Padding.Empty });
            autoSaveLine.Controls.Add(CadDialogTheme.Button("应用并同步 CAD", ApplyAutoSave, true));
            autoSaveLine.Controls.Add(CadDialogTheme.Button("立即生成备份", SaveAutoSaveNow));
            right.Controls.Add(autoSaveLine, 0, 5);
            right.Controls.Add(new Label { Text = "备份位置：项目文件夹\\自动保存\\原文件名_自动保存.dwg，可直接用 CAD 打开。", Dock = DockStyle.Top, Height = 38, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
            right.Controls.Add(new Label { Text = "保存参数只更改登记路径，不搬移文件；复制迁移会保留原目录。删除项目不会删除文件夹或 DWG。", Dock = DockStyle.Top, Height = 42, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 7);
            rightCard.Controls.Add(right); workspace.Controls.Add(rightCard, 2, 0);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = CadDialogTheme.Canvas, Padding = new Padding(0, 9, 0, 0) };
            var close = CadDialogTheme.Button("关闭", () => Close()); close.DialogResult = DialogResult.OK;
            bottom.Controls.Add(close); bottom.Controls.Add(CadDialogTheme.Button("保存参数", SaveParameters, true));
            outer.Controls.Add(bottom, 0, 2);
            _projects.SelectedIndexChanged += (sender, args) => UpdateSelection();
            _projects.DoubleClick += (sender, args) => SwitchSelected();
            _folder.TextChanged += (sender, args) => { if (!_updatingSelection) _folderChosenForNewProject = true; };
        }

        private void RefreshProjects()
        {
            _projects.DataSource = null;
            _projects.DataSource = _viewModel.Projects.ToList();
            _projects.DisplayMember = "Name";
            _projects.SelectedItem = _viewModel.SelectedProject;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            var project = _projects.SelectedItem as ProjectProfile ?? _viewModel.SelectedProject;
            if (project == null) return;
            _updatingSelection = true;
            try
            {
                _name.Text = project.Name;
                _folder.Text = _viewModel.GetProjectFolder(project);
                _autoSaveMinutes.Text = Math.Max(0, Math.Min(600, _viewModel.GetProjectAutoSaveMinutes(project))).ToString();
                _folderChosenForNewProject = false;
            }
            finally { _updatingSelection = false; }
        }

        private void CreateOrSwitch()
        {
            var existing = _viewModel.Projects.FirstOrDefault(project => string.Equals(project.Name, (_name.Text ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            var requestedFolder = existing == null && _folderChosenForNewProject ? _folder.Text : null;
            if (_viewModel.CreateOrSelectProject(_name.Text, requestedFolder)) { _refreshPublisher(); RefreshProjects(); }
            else MessageBox.Show(this, _viewModel.Status, "新建工程", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void SwitchSelected()
        {
            var project = _projects.SelectedItem as ProjectProfile;
            if (project == null) return;
            _viewModel.SelectedProject = project; _refreshPublisher(); RefreshProjects();
        }

        private void DeleteSelected()
        {
            var project = _projects.SelectedItem as ProjectProfile;
            if (project == null) return;
            if (MessageBox.Show(this, "删除工程“" + project.Name + "”的插件参数？\r\n项目文件夹与 CAD 文件不会删除。", "确认删除项目", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _viewModel.DeleteProject(project); _refreshPublisher(); RefreshProjects();
        }

        private void SaveParameters()
        {
            var selected = _projects.SelectedItem as ProjectProfile;
            if (selected != null && !ReferenceEquals(selected, _viewModel.SelectedProject)) _viewModel.SelectedProject = selected;
            if (!_viewModel.SetProjectFolder(_folder.Text)) { MessageBox.Show(this, _viewModel.Status, "项目文件夹", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _viewModel.SetProjectAutoSaveMinutes(ReadAutoSaveMinutes());
            _viewModel.SaveProjectParameters(); _refreshPublisher();
        }

        private void ApplyAutoSave()
        {
            var selected = _projects.SelectedItem as ProjectProfile;
            if (selected != null && !ReferenceEquals(selected, _viewModel.SelectedProject)) _viewModel.SelectedProject = selected;
            _viewModel.SetProjectAutoSaveMinutes(ReadAutoSaveMinutes());
            MessageBox.Show(this, _viewModel.Status, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private int ReadAutoSaveMinutes()
        {
            int value;
            return int.TryParse(_autoSaveMinutes.Text, out value) ? Math.Max(0, Math.Min(600, value)) : 0;
        }

        private void SaveAutoSaveNow()
        {
            var selected = _projects.SelectedItem as ProjectProfile;
            if (selected != null && !ReferenceEquals(selected, _viewModel.SelectedProject)) _viewModel.SelectedProject = selected;
            MessageBox.Show(this, _viewModel.SaveProjectAutoSaveNow(), "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择工程文件夹", SelectedPath = _folder.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) { _folder.Text = dialog.SelectedPath; _folderChosenForNewProject = true; }
        }

        private void MigrateProject()
        {
            var selected = _projects.SelectedItem as ProjectProfile;
            if (selected != null && !ReferenceEquals(selected, _viewModel.SelectedProject)) _viewModel.SelectedProject = selected;
            if (MessageBox.Show(this, "将原项目文件完整复制到当前填写的新目录，并把插件切换到新目录？\r\n\r\n新目录必须为空，原目录将保留作为备份。", "复制迁移项目", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            UseWaitCursor = true;
            try
            {
                var succeeded = _viewModel.MigrateProjectFolder(_folder.Text);
                MessageBox.Show(this, _viewModel.Status, "复制迁移项目", MessageBoxButtons.OK,
                    succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (succeeded) { _refreshPublisher(); RefreshProjects(); }
            }
            finally { UseWaitCursor = false; }
        }

        private void SaveCurrentCad()
        {
            string destination, error;
            if (_viewModel.SaveCurrentCadToProjectFolder(out destination, out error)) { _refreshPublisher(); MessageBox.Show(this, "已保存：\r\n" + destination, "项目管理"); }
            else MessageBox.Show(this, "保存当前 CAD 失败：\r\n" + error, "项目管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void OpenFolder()
        {
            var folder = _viewModel.GetProjectFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private static Label Title(string text) => new Label { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 54, 99), Margin = new Padding(0, 0, 0, 8) };
        private static Label FieldLabel(string text) => new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Width = 82, Height = 32, Margin = new Padding(0, 0, 6, 5), AutoEllipsis = true };
        private static void ApplyInputStyle(Control control)
        {
            control.AutoSize = false;
            control.Height = 30;
            control.Margin = new Padding(0, 0, 8, 5);
        }

        private Button Button(string text, Action action, bool accent = false)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(0, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(25, 54, 99), Padding = new Padding(7, 2, 7, 2), Margin = new Padding(0, 0, 6, 5) };
            button.FlatAppearance.BorderColor = accent ? Color.FromArgb(104, 145, 185) : Color.FromArgb(190, 201, 216);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 250);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 235, 246);
            button.Tag = text;
            _toolTip.SetToolTip(button, ButtonDescription(text));
            button.Click += (sender, args) => action(); return button;
        }

        private static string ButtonDescription(string text)
        {
            switch ((text ?? string.Empty).Trim())
            {
                case "新建工程": return "按当前工程名称建立一套新的图框、图纸和输出设置。";
                case "选择目录": return "选择用于保存工程 CAD、自动保存备份和项目资料的文件夹。";
                case "切换项目": return "切换到左侧选中的工程并载入该工程的设置。";
                case "保存参数": return "保存当前工程名称、目录、扫描范围和自动保存间隔。";
                case "复制迁移项目": return "把原项目完整复制到空的新目录，验证成功后切换登记路径，原目录保留。";
                case "保存 CAD": return "把当前正在编辑的 CAD 文件保存到项目文件夹。";
                case "打开目录": return "在文件资源管理器中打开当前项目文件夹。";
                case "扫描设置": return "设置扫描模型空间、布局以及参与扫描的布局名称。";
                case "删除项目": return "仅删除插件中的工程配置，不删除项目文件夹或 DWG。";
                case "应用并同步 CAD": return "保存自动保存间隔，并同步修改 CAD 的 SAVETIME。";
                case "立即生成备份": return "立即为当前项目中已打开的 DWG 生成可直接打开的快照。";
                case "关闭窗口": return "关闭项目管理窗口并返回主界面。";
                default: return text ?? string.Empty;
            }
        }
    }
}
