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
        private readonly NumericUpDown _autoSaveMinutes = new NumericUpDown();
        private readonly ToolTip _toolTip = new ToolTip();
        private bool _updatingSelection;
        private bool _folderChosenForNewProject;

        public ProjectManagerForm(PublisherViewModel viewModel, Action refreshPublisher, Action configureScan)
        {
            _viewModel = viewModel;
            _refreshPublisher = refreshPublisher;
            _configureScan = configureScan;
            Text = "项目管理";
            Width = 900;
            Height = 570;
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            SizeGripStyle = SizeGripStyle.Show;
            ApplyInputStyle(_name);
            Build();
            RefreshProjects();
        }

        private void Build()
        {
            BackColor = Color.FromArgb(247, 249, 252);
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 2, ColumnCount = 1 };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(outer);
            var split = new SplitContainer { Size = new Size(850, 470), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, SplitterDistance = 245, Panel1MinSize = 190, Panel2MinSize = 470 };
            outer.Controls.Add(split, 0, 0);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 0, 12, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(Title("工程列表"), 0, 0);
            _projects.Dock = DockStyle.Fill; _projects.IntegralHeight = false; left.Controls.Add(_projects, 0, 1);
            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(14, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.Controls.Add(Title("项目管理"), 0, 0);

            var information = new GroupBox { Text = "工程信息", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 10) };
            var infoGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2 };
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85)); infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            infoGrid.Controls.Add(FieldLabel("工程名称"), 0, 0); _name.Dock = DockStyle.Fill; infoGrid.Controls.Add(_name, 1, 0); infoGrid.Controls.Add(Button("新建工程", CreateOrSwitch, true), 2, 0);
            infoGrid.Controls.Add(FieldLabel("项目文件夹"), 0, 1); ApplyInputStyle(_folder); _folder.Dock = DockStyle.Fill; infoGrid.Controls.Add(_folder, 1, 1); infoGrid.Controls.Add(Button("选择目录", ChooseFolder), 2, 1);
            information.Controls.Add(infoGrid); right.Controls.Add(information, 0, 1);

            var operations = new GroupBox { Text = "工程操作", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0, 0, 0, 10) };
            var operationButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            operationButtons.Controls.Add(Button("切换项目", SwitchSelected)); operationButtons.Controls.Add(Button("保存参数", SaveParameters, true));
            operationButtons.Controls.Add(Button("保存 CAD", SaveCurrentCad, true)); operationButtons.Controls.Add(Button("打开目录", OpenFolder));
            operationButtons.Controls.Add(Button("扫描设置", () => _configureScan())); operationButtons.Controls.Add(Button("删除项目", DeleteSelected));
            operations.Controls.Add(operationButtons); right.Controls.Add(operations, 0, 2);

            var autoSave = new GroupBox { Text = "自动保存", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 10) };
            var autoSaveGrid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1 };
            var autoSaveLine = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            autoSaveLine.Controls.Add(new Label { Text = "间隔", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            _autoSaveMinutes.Minimum = 0; _autoSaveMinutes.Maximum = 600; _autoSaveMinutes.Width = 70; _autoSaveMinutes.Height = 30;
            autoSaveLine.Controls.Add(_autoSaveMinutes);
            autoSaveLine.Controls.Add(new Label { Text = "分钟（0 表示关闭）", AutoSize = true, Margin = new Padding(3, 7, 10, 0) });
            autoSaveLine.Controls.Add(Button("应用并同步 CAD", ApplyAutoSave, true)); autoSaveLine.Controls.Add(Button("立即生成备份", SaveAutoSaveNow));
            autoSaveGrid.Controls.Add(autoSaveLine, 0, 0);
            autoSaveGrid.Controls.Add(new Label { Text = "备份位置：项目文件夹\\自动保存\\原文件名_自动保存.dwg，可直接用 CAD 打开。", AutoSize = true, MaximumSize = new Size(560, 42), ForeColor = Color.FromArgb(105, 105, 105), Margin = new Padding(0, 6, 0, 0) }, 0, 1);
            autoSave.Controls.Add(autoSaveGrid); right.Controls.Add(autoSave, 0, 3);
            right.Controls.Add(new Label { Text = "删除项目只删除插件参数，项目文件夹及其中的 DWG 不会删除。", AutoSize = true, ForeColor = Color.FromArgb(105, 105, 105), Margin = new Padding(2, 3, 0, 0) }, 0, 4);
            split.Panel2.Controls.Add(right);

            var close = Button("关闭窗口", () => Close()); close.DialogResult = DialogResult.OK;
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            bottom.Controls.Add(close); outer.Controls.Add(bottom, 0, 1);
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
                _autoSaveMinutes.Value = Math.Max(_autoSaveMinutes.Minimum, Math.Min(_autoSaveMinutes.Maximum, _viewModel.GetProjectAutoSaveMinutes(project)));
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
            _viewModel.SetProjectAutoSaveMinutes((int)_autoSaveMinutes.Value);
            _viewModel.SaveProjectParameters(); _refreshPublisher();
        }

        private void ApplyAutoSave()
        {
            var selected = _projects.SelectedItem as ProjectProfile;
            if (selected != null && !ReferenceEquals(selected, _viewModel.SelectedProject)) _viewModel.SelectedProject = selected;
            _viewModel.SetProjectAutoSaveMinutes((int)_autoSaveMinutes.Value);
            MessageBox.Show(this, _viewModel.Status, "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
