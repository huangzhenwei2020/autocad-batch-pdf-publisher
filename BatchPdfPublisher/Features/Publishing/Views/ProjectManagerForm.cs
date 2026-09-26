using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly ListBox _projects = new ProjectListBox();
        private readonly TextBox _search = new TextBox();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _folder = new TextBox();
        private readonly TextBox _autoSaveMinutes = new TextBox { Text = "0" };
        private readonly List<Image> _icons = new List<Image>();
        private bool _updatingSelection;
        private bool _folderChosenForNewProject;

        public ProjectManagerForm(PublisherViewModel viewModel, Action refreshPublisher, Action configureScan)
        {
            _viewModel = viewModel;
            _refreshPublisher = refreshPublisher;
            _configureScan = configureScan;
            Text = "项目管理";
            Width = 1100;
            Height = 670;
            MinimumSize = new Size(980, 600);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(1);
            Build();
            RefreshProjects();
            Shown += (sender, args) => ActiveControl = _projects;
        }

        private void Build()
        {
            BackColor = CadDialogTheme.Border;
            ForeColor = CadDialogTheme.Text;
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 3, ColumnCount = 1, BackColor = CadDialogTheme.Canvas };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Controls.Add(outer);
            outer.Controls.Add(BuildTitleBar(), 0, 0);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = CadDialogTheme.Canvas, Padding = new Padding(12, 12, 12, 0), Margin = Padding.Empty };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 284));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.Controls.Add(workspace, 0, 1);

            var leftCard = CadDialogTheme.Card();
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(CadDialogTheme.Heading("项目列表"), 0, 0);
            var searchHost = CadDialogTheme.Input(_search);
            searchHost.Placeholder = "搜索项目...";
            searchHost.LeadingIcon = MakeIcon(PublisherForm.UiIcon.Search, CadDialogTheme.Muted);
            searchHost.Margin = new Padding(0, 0, 0, 12);
            left.Controls.Add(searchHost, 0, 1);
            CadDialogTheme.StyleList(_projects);
            _projects.Font = new Font(Font.FontFamily, 10F);
            _projects.ItemHeight = 34;
            left.Controls.Add(new CadListHost(_projects), 0, 2);
            leftCard.Controls.Add(left); workspace.Controls.Add(leftCard, 0, 0);

            var rightStack = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
            rightStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            rightStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspace.Controls.Add(rightStack, 2, 0);

            var infoCard = CadDialogTheme.Card();
            var info = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            info.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            info.Controls.Add(SectionHeading("工程信息", PublisherForm.UiIcon.List), 0, 0);
            var infoGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            infoGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            infoGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            infoGrid.Controls.Add(Field("工程名称"), 0, 0);
            infoGrid.Controls.Add(Input(_name), 1, 0);
            infoGrid.Controls.Add(Button("新建工程", CreateOrSwitch, true, dock: true, icon: PublisherForm.UiIcon.Plus), 2, 0);
            infoGrid.Controls.Add(Field("项目文件夹"), 0, 1);
            infoGrid.Controls.Add(Input(_folder), 1, 1);
            infoGrid.Controls.Add(Button("选择目录", ChooseFolder, dock: true, icon: PublisherForm.UiIcon.Folder), 2, 1);
            info.Controls.Add(infoGrid, 0, 1);
            info.SetRowSpan(infoGrid, 2);
            var primaryActions = Row();
            primaryActions.Padding = new Padding(0, 6, 0, 0);
            primaryActions.Controls.Add(Button("切换项目", SwitchSelected, icon: PublisherForm.UiIcon.Switch));
            primaryActions.Controls.Add(Button("保存参数", SaveParameters, true, icon: PublisherForm.UiIcon.Gear));
            info.Controls.Add(primaryActions, 0, 3);
            infoCard.Controls.Add(info);
            rightStack.Controls.Add(infoCard, 0, 0);

            var toolsCard = CadDialogTheme.Card();
            var tools = new TableLayoutPanel { Dock = DockStyle.None, AutoSize = true, ColumnCount = 1, RowCount = 7, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            tools.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            tools.Controls.Add(SectionHeading("工程操作", PublisherForm.UiIcon.Gear), 0, 0);
            var operationButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            for (var column = 0; column < 4; column++) operationButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            operationButtons.Controls.Add(GridButton("保存 CAD", SaveCurrentCad, PublisherForm.UiIcon.Save, true), 0, 0);
            operationButtons.Controls.Add(GridButton("打开目录", OpenFolder, PublisherForm.UiIcon.Folder), 1, 0);
            operationButtons.Controls.Add(GridButton("扫描设置", () => _configureScan(), PublisherForm.UiIcon.Select), 2, 0);
            var migrate = GridButton("复制迁移项目", MigrateProject, PublisherForm.UiIcon.Copy);
            migrate.Margin = new Padding(0, 0, 0, 14);
            operationButtons.Controls.Add(migrate, 3, 0);
            tools.Controls.Add(operationButtons, 0, 1);
            tools.Controls.Add(SectionHeading("自动保存", PublisherForm.UiIcon.Clock), 0, 2);
            var autoSaveLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
            autoSaveLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
            autoSaveLine.Controls.Add(new Label { Text = "间隔", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted, Margin = new Padding(0, 0, 0, 14) }, 0, 0);
            var interval = Input(_autoSaveMinutes, 0);
            interval.Margin = new Padding(0, 0, 8, 14);
            autoSaveLine.Controls.Add(interval, 1, 0);
            autoSaveLine.Controls.Add(new Label { Text = "分钟（0 为关闭）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted, Margin = new Padding(0, 0, 0, 14) }, 2, 0);
            var apply = GridButton("应用并同步 CAD", ApplyAutoSave, PublisherForm.UiIcon.Switch, true);
            var backup = GridButton("立即生成备份", SaveAutoSaveNow, PublisherForm.UiIcon.Backup);
            apply.Margin = new Padding(0, 0, 8, 14);
            backup.Margin = new Padding(0, 0, 0, 14);
            autoSaveLine.Controls.Add(apply, 4, 0);
            autoSaveLine.Controls.Add(backup, 5, 0);
            tools.Controls.Add(autoSaveLine, 0, 3);
            tools.Controls.Add(new Label { Text = "备份位置：项目文件夹\\自动保存", Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 4);
            tools.Controls.Add(new Panel { Dock = DockStyle.Fill, Height = 1, BackColor = CadDialogTheme.Border, Margin = Padding.Empty }, 0, 5);
            var configuration = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            configuration.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 162));
            var configText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            configText.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            configText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            configText.Controls.Add(SectionHeading("项目配置", PublisherForm.UiIcon.Trash, CadDialogTheme.Danger), 0, 0);
            configText.Controls.Add(new Label { Text = "删除后仅移除插件参数，不会删除项目文件夹或 CAD 文件。", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft, ForeColor = CadDialogTheme.Muted, Margin = new Padding(26, 0, 0, 0), AutoEllipsis = true }, 0, 1);
            configuration.Controls.Add(configText, 0, 0);
            var delete = Button("删除项目", DeleteSelected, danger: true, icon: PublisherForm.UiIcon.Trash);
            delete.Width = 152;
            delete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            delete.Margin = new Padding(0, 14, 0, 0);
            configuration.Controls.Add(delete, 1, 0);
            tools.Controls.Add(configuration, 0, 6);
            toolsCard.Controls.Add(new CadScrollHost(tools) { Dock = DockStyle.Fill });
            rightStack.Controls.Add(toolsCard, 0, 2);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = CadDialogTheme.Canvas, Padding = new Padding(12, 8, 12, 0), Margin = Padding.Empty };
            var close = Button("关闭", Close); close.DialogResult = DialogResult.OK;
            bottom.Controls.Add(close);
            outer.Controls.Add(bottom, 0, 2);
            _search.TextChanged += (sender, args) => RefreshProjectList();
            _projects.SelectedIndexChanged += (sender, args) => UpdateSelection();
            _projects.DoubleClick += (sender, args) => SwitchSelected();
            _folder.TextChanged += (sender, args) => { if (!_updatingSelection) _folderChosenForNewProject = true; };
        }

        private Control BuildTitleBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Color.FromArgb(27, 30, 40), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var mark = CadBrandIcon.CreateTitleMark();
            var caption = new Label { Text = "项目管理", Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Text, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            var minimize = ChromeButton("−", () => WindowState = FormWindowState.Minimized);
            var maximize = ChromeButton("□", ToggleMaximized);
            var close = ChromeButton("×", Close);
            MouseEventHandler drag = (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag;
            caption.MouseDown += drag;
            mark.MouseDown += drag;
            caption.DoubleClick += (sender, args) => ToggleMaximized();
            bar.Controls.Add(mark, 0, 0);
            bar.Controls.Add(caption, 1, 0);
            bar.Controls.Add(minimize, 2, 0);
            bar.Controls.Add(maximize, 3, 0);
            bar.Controls.Add(close, 4, 0);
            return bar;
        }

        private Button ChromeButton(string text, Action action)
        {
            var button = new ProjectChromeButton { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
                BackColor = Color.FromArgb(27, 30, 40), ForeColor = CadDialogTheme.Muted,
                Font = new Font("Segoe UI Symbol", 10F), TabStop = false };
            button.Click += (sender, args) => action();
            return button;
        }

        private sealed class ProjectChromeButton : Button
        {
            private bool _hovered;

            public ProjectChromeButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(_hovered ? CadDialogTheme.Raised : BackColor);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void ToggleMaximized()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref message);
                if ((int)message.Result != 1) return;
                var raw = message.LParam.ToInt64();
                var point = PointToClient(new Point((short)(raw & 0xffff), (short)((raw >> 16) & 0xffff)));
                var grip = Math.Max(1, 10 * DeviceDpi / 96);
                var left = point.X < grip;
                var right = point.X >= ClientSize.Width - grip;
                var top = point.Y < grip;
                var bottom = point.Y >= ClientSize.Height - grip;
                if (left && top) message.Result = (IntPtr)13;
                else if (right && top) message.Result = (IntPtr)14;
                else if (left && bottom) message.Result = (IntPtr)16;
                else if (right && bottom) message.Result = (IntPtr)17;
                else if (left) message.Result = (IntPtr)10;
                else if (right) message.Result = (IntPtr)11;
                else if (top) message.Result = (IntPtr)12;
                else if (bottom) message.Result = (IntPtr)15;
                return;
            }
            base.WndProc(ref message);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wordParameter, IntPtr longParameter);

        private static Label Field(string text)
        {
            var label = CadDialogTheme.FieldLabel(text);
            label.Margin = new Padding(0, 0, 8, 12);
            return label;
        }

        private static CadTextInputHost Input(TextBox input, int width = 0)
        {
            var host = CadDialogTheme.Input(input, width);
            host.Margin = width > 0 ? new Padding(0, 0, 8, 0) : new Padding(0, 0, 12, 12);
            return host;
        }

        private Image MakeIcon(PublisherForm.UiIcon icon, Color color)
        {
            var image = PublisherForm.DrawUiIcon(icon, color);
            _icons.Add(image);
            return image;
        }

        private Control SectionHeading(string text, PublisherForm.UiIcon icon, Color? color = null)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new PictureBox { Image = MakeIcon(icon, color ?? CadDialogTheme.Accent), SizeMode = PictureBoxSizeMode.CenterImage,
                Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            row.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Text,
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold), Margin = Padding.Empty }, 1, 0);
            return row;
        }

        private CadRoundedButton Button(string text, Action action, bool accent = false, bool danger = false,
            bool dock = false, PublisherForm.UiIcon? icon = null)
        {
            var button = CadDialogTheme.Button(text, action, accent, danger);
            button.AutoSize = false;
            button.Height = CadDialogTheme.ControlHeight;
            button.Width = dock ? 100 : Math.Max(124, TextRenderer.MeasureText(text, button.Font).Width + (icon.HasValue ? 54 : 30));
            button.Dock = dock ? DockStyle.Fill : DockStyle.None;
            button.Margin = dock ? new Padding(0, 0, 0, 12) : new Padding(0, 0, 8, 0);
            if (icon.HasValue) button.Image = MakeIcon(icon.Value, accent ? Color.White : danger ? CadDialogTheme.Danger : CadDialogTheme.Muted);
            return button;
        }

        private CadRoundedButton GridButton(string text, Action action, PublisherForm.UiIcon icon, bool accent = false)
        {
            var button = Button(text, action, accent, dock: true, icon: icon);
            button.Margin = new Padding(0, 0, 8, 14);
            return button;
        }

        private static FlowLayoutPanel Row(bool wrap = false)
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = wrap, BackColor = CadDialogTheme.Surface,
                Margin = Padding.Empty, Padding = Padding.Empty };
        }

        private sealed class ProjectListBox : CadPlainListBox
        {
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
                var selected = (e.State & DrawItemState.Selected) != 0;
                if (selected)
                {
                    var bounds = new Rectangle(e.Bounds.Left + 1, e.Bounds.Top + 1, e.Bounds.Width - 2, e.Bounds.Height - 2);
                    var state = e.Graphics.Save();
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = CadDialogTheme.Rounded(bounds, 5))
                    using (var fill = new SolidBrush(Color.FromArgb(24, 103, 179)))
                    using (var accent = new SolidBrush(Color.FromArgb(73, 213, 255)))
                    {
                        e.Graphics.FillPath(fill, path);
                        e.Graphics.SetClip(path, CombineMode.Intersect);
                        e.Graphics.FillRectangle(accent, bounds.Left, bounds.Top, 5, bounds.Height);
                    }
                    e.Graphics.Restore(state);
                }
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                    new Rectangle(e.Bounds.Left + 9, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 18), e.Bounds.Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var icon in _icons) icon.Dispose();
            base.Dispose(disposing);
        }

        private void RefreshProjects()
        {
            RefreshProjectList();
            UpdateSelection();
        }

        private void RefreshProjectList()
        {
            var selected = _projects.SelectedItem as ProjectProfile ?? _viewModel.SelectedProject;
            var query = (_search.Text ?? string.Empty).Trim();
            var projects = _viewModel.Projects
                .Where(project => string.IsNullOrEmpty(query) || project.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .ToList();
            _updatingSelection = true;
            try
            {
                _projects.BeginUpdate();
                _projects.DataSource = null;
                _projects.DisplayMember = "Name";
                _projects.DataSource = projects;
                _projects.SelectedItem = projects.FirstOrDefault(project => ReferenceEquals(project, selected));
                _projects.EndUpdate();
            }
            finally { _updatingSelection = false; }
        }

        private void UpdateSelection()
        {
            if (_updatingSelection) return;
            var project = _projects.SelectedItem as ProjectProfile;
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
            if (_viewModel.CreateOrSelectProject(_name.Text, requestedFolder))
            {
                _search.Clear();
                _refreshPublisher();
                RefreshProjects();
            }
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

    }
}
