using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    /// <summary>Centralizes project creation, switching, scan settings and storage.</summary>
    public sealed class ProjectManagerForm : Form
    {
        private readonly PublisherViewModel _viewModel;
        private readonly Action _refreshPublisher;
        private readonly Action _configureScan;
        private readonly ListBox _projects = new ListBox();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _folder = new TextBox();

        public ProjectManagerForm(PublisherViewModel viewModel, Action refreshPublisher, Action configureScan)
        {
            _viewModel = viewModel;
            _refreshPublisher = refreshPublisher;
            _configureScan = configureScan;
            Text = "项目管理";
            Width = 720;
            Height = 460;
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ApplyInputStyle(_name);
            Build();
            RefreshProjects();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(Title("工程列表"), 0, 0); _projects.Dock = DockStyle.Fill; left.Controls.Add(_projects, 0, 1);
            root.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 0, 0), RowCount = 8, ColumnCount = 1 };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.Controls.Add(Title("项目管理"), 0, 0);
            var nameLine = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
            nameLine.Controls.Add(new Label { Text = "工程名称", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            _name.Width = 240; nameLine.Controls.Add(_name); nameLine.Controls.Add(Button("新建 / 切换", CreateOrSwitch, true)); right.Controls.Add(nameLine, 0, 1);
            var folderLine = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = new Padding(0, 8, 0, 5) };
            folderLine.Controls.Add(new Label { Text = "项目文件夹", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
            _folder.Width = 300; ApplyInputStyle(_folder); folderLine.Controls.Add(_folder); folderLine.Controls.Add(Button("选择", ChooseFolder)); right.Controls.Add(folderLine, 0, 2);
            var projectActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            projectActions.Controls.Add(Button("切换选中项目", SwitchSelected)); projectActions.Controls.Add(Button("删除项目", DeleteSelected));
            projectActions.Controls.Add(Button("保存工程参数", SaveParameters, true)); right.Controls.Add(projectActions, 0, 3);
            var cadActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            cadActions.Controls.Add(Button("保存当前 CAD 到项目文件夹", SaveCurrentCad, true)); cadActions.Controls.Add(Button("打开项目文件夹", OpenFolder)); right.Controls.Add(cadActions, 0, 4);
            var scanActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            scanActions.Controls.Add(Button("扫描设置", () => _configureScan())); right.Controls.Add(scanActions, 0, 5);
            right.Controls.Add(new Label { Text = "说明：删除项目只删除插件中的项目参数，项目文件夹和其中的 DWG 会保留。", AutoSize = true, ForeColor = Color.FromArgb(110, 110, 110), MaximumSize = new Size(430, 48), Margin = new Padding(0, 14, 0, 0) }, 0, 6);
            root.Controls.Add(right, 1, 0);

            var close = Button("关闭", () => Close()); close.DialogResult = DialogResult.OK;
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            bottom.Controls.Add(close); root.Controls.Add(bottom, 1, 1);
            _projects.SelectedIndexChanged += (sender, args) => UpdateSelection();
            _projects.DoubleClick += (sender, args) => SwitchSelected();
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
            _name.Text = project.Name;
            _folder.Text = _viewModel.GetProjectFolder(project);
        }

        private void CreateOrSwitch()
        {
            if (_viewModel.CreateOrSelectProject(_name.Text)) { _refreshPublisher(); RefreshProjects(); }
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
            _viewModel.SetProjectFolder(_folder.Text);
            _viewModel.SaveProjectParameters(); _refreshPublisher();
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择工程文件夹", SelectedPath = _folder.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
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
        private static void ApplyInputStyle(Control control)
        {
            control.AutoSize = false;
            control.Height = 30;
            control.Margin = new Padding(0, 0, 8, 5);
        }

        private static Button Button(string text, Action action, bool accent = false)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(0, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(25, 54, 99), Padding = new Padding(7, 2, 7, 2), Margin = new Padding(0, 0, 6, 5) };
            button.FlatAppearance.BorderColor = accent ? Color.FromArgb(104, 145, 185) : Color.FromArgb(190, 201, 216);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 244, 250);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 235, 246);
            button.Click += (sender, args) => action(); return button;
        }
    }
}
