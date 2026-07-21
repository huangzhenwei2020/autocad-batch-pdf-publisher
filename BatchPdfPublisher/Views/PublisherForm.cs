using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// Native WinForms host for AutoCAD 2022.  A populated WPF DataGrid in a
    /// modeless AutoCAD window can terminate acad.exe inside the WPF message
    /// pump on some 2022 installations.  WinForms uses AutoCAD's established
    /// modeless-dialog integration and leaves the publishing model unchanged.
    /// </summary>
    public sealed class PublisherForm : Form
    {
        private readonly PublisherViewModel _viewModel = new PublisherViewModel();
        private readonly ComboBox _projects = new ComboBox();
        private readonly TextBox _newProjectName = new TextBox();
        private readonly ListBox _buildings = new ListBox();
        private readonly ListBox _frames = new ListBox();
        private readonly DataGridView _sheets = new DataGridView();
        private readonly ComboBox _plotStyle = new ComboBox();
        private readonly ComboBox _marginMode = new ComboBox();
        private readonly TextBox _outputDirectory = new TextBox();
        private readonly CheckBox _mergeByBuilding = new CheckBox();
        private readonly CheckBox _previewEnabled = new CheckBox();
        private readonly Label _status = new Label();
        private readonly ProgressBar _publishProgress = new ProgressBar();
        private readonly Label _publishProgressText = new Label();
        private bool _refreshing;
        private bool _gridCommitPending;

        public PublisherForm()
        {
            Text = "批量 PDF 发布";
            Width = 1240;
            Height = 760;
            MinimumSize = new System.Drawing.Size(1020, 600);
            StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

            BuildInterface();
            WireEvents();
            RefreshAll();
        }

        private void BuildInterface()
        {
            BackColor = System.Drawing.Color.FromArgb(242, 245, 249);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0), RowCount = 4, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20, 8, 20, 8), BackColor = System.Drawing.Color.FromArgb(22, 39, 65) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "批量 PDF 发布", ForeColor = System.Drawing.Color.White, Font = new System.Drawing.Font(Font.FontFamily, 15F, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(0, 8) });
            header.Controls.Add(heading, 0, 0);
            var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            headerActions.Controls.Add(AccentButton("扫描当前图纸", () => { _viewModel.ScanCommand.Execute(null); RefreshAll(); }));
            var publish = AccentButton("发布 PDF", PublishPdf); headerActions.Controls.Add(publish);
            header.Controls.Add(headerActions, 1, 0);
            root.Controls.Add(header, 0, 0);

            var projectBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 9, 18, 7), BackColor = System.Drawing.Color.White, WrapContents = false };
            projectBar.Controls.Add(new Label { Text = "工程名称", AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = new Padding(2, 7, 10, 0) });
            _projects.Width = 190; _projects.DropDownStyle = ComboBoxStyle.DropDownList;
            _newProjectName.Width = 160; _newProjectName.Margin = new Padding(14, 3, 4, 3);
            projectBar.Controls.Add(_projects); projectBar.Controls.Add(_newProjectName);
            projectBar.Controls.Add(Button("新建工程", CreateProject));
            projectBar.Controls.Add(Button("保存工程参数", () => _viewModel.SaveProjectCommand.Execute(null)));
            root.Controls.Add(projectBar, 0, 1);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 12, 14, 10) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            root.Controls.Add(body, 0, 2);

            var left = Card(7, new Padding(12)); left.Margin = new Padding(0, 0, 10, 0);
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            left.Controls.Add(SectionLabel("子项目名称"), 0, 0); _buildings.Dock = DockStyle.Fill; left.Controls.Add(_buildings, 0, 1);
            left.Controls.Add(SectionLabel("图框登记（双击修改）"), 0, 2); _frames.Dock = DockStyle.Fill; _frames.HorizontalScrollbar = true; left.Controls.Add(_frames, 0, 3);
            var frameButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            frameButtons.Controls.Add(AccentButton("拾取并登记", () => _viewModel.RegisterFrameCommand.Execute(null)));
            frameButtons.Controls.Add(Button("修改", EditFrame)); frameButtons.Controls.Add(Button("删除", RemoveFrame));
            left.Controls.Add(frameButtons, 0, 4);
            left.Controls.Add(Button("保存当前图框库", () => _viewModel.SaveFrameLibraryCommand.Execute(null)), 0, 5);
            body.Controls.Add(left, 0, 0);

            var center = Card(3, new Padding(12)); center.Margin = new Padding(0, 0, 10, 0);
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var sheetHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            sheetHeader.Controls.Add(SectionLabel("图纸列表"));
            _previewEnabled.Text = "显示当前子项目预览"; _previewEnabled.AutoSize = true; _previewEnabled.Margin = new Padding(16, 6, 8, 3);
            sheetHeader.Controls.Add(_previewEnabled);
            sheetHeader.Controls.Add(Button("上移", () => { _viewModel.MoveUpCommand.Execute(null); RefreshSheets(); }));
            sheetHeader.Controls.Add(Button("下移", () => { _viewModel.MoveDownCommand.Execute(null); RefreshSheets(); }));
            center.Controls.Add(sheetHeader, 0, 0);
            ConfigureGrid(); center.Controls.Add(_sheets, 0, 1);
            body.Controls.Add(center, 1, 0);

            var right = Card(12, new Padding(14)); right.AutoSize = true;
            right.Controls.Add(SectionLabel("输出设置")); right.Controls.Add(Label("CAD 打印样式"));
            _plotStyle.DropDownStyle = ComboBoxStyle.DropDown; _plotStyle.Dock = DockStyle.Top;
            right.Controls.Add(_plotStyle);
            var plotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            plotButtons.Controls.Add(Button("刷新 CAD 样式", () => { _viewModel.RefreshPlotStylesCommand.Execute(null); RefreshPlotStyles(); }));
            plotButtons.Controls.Add(Button("收藏当前样式", () => { _viewModel.SaveFavoritePlotStyleCommand.Execute(null); RefreshPlotStyles(); }));
            right.Controls.Add(plotButtons); right.Controls.Add(Label("白边 / 出血位"));
            _marginMode.Items.AddRange(new object[] { "自动适配", "无白边（满幅）", "保留 3 mm 白边" });
            _marginMode.Dock = DockStyle.Top; right.Controls.Add(_marginMode); right.Controls.Add(Label("输出目录")); _outputDirectory.Dock = DockStyle.Top; right.Controls.Add(_outputDirectory);
            _mergeByBuilding.Text = "每个子项目生成一个 PDF"; _mergeByBuilding.AutoSize = true; _mergeByBuilding.Margin = new Padding(3, 12, 3, 3); right.Controls.Add(_mergeByBuilding);
            body.Controls.Add(right, 2, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.White, Padding = new Padding(16, 8, 16, 6), ColumnCount = 3 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            _status.Dock = DockStyle.Fill; _status.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); footer.Controls.Add(_status, 0, 0);
            _publishProgress.Dock = DockStyle.Fill; _publishProgress.Minimum = 0; _publishProgress.Maximum = 1; _publishProgress.Value = 0; _publishProgress.Style = ProgressBarStyle.Continuous; footer.Controls.Add(_publishProgress, 1, 0);
            _publishProgressText.Dock = DockStyle.Fill; _publishProgressText.TextAlign = System.Drawing.ContentAlignment.MiddleRight; _publishProgressText.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); _publishProgressText.Text = "0 / 0"; footer.Controls.Add(_publishProgressText, 2, 0);
            root.Controls.Add(footer, 0, 3);
        }

        private void ConfigureGrid()
        {
            _sheets.Dock = DockStyle.Fill; _sheets.AutoGenerateColumns = false; _sheets.AllowUserToAddRows = false;
            _sheets.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _sheets.MultiSelect = false;
            _sheets.BorderStyle = BorderStyle.None; _sheets.BackgroundColor = System.Drawing.Color.White;
            _sheets.EnableHeadersVisualStyles = false; _sheets.ColumnHeadersHeight = 34; _sheets.RowTemplate.Height = 30;
            _sheets.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(231, 237, 246);
            _sheets.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(31, 48, 74);
            _sheets.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold);
            _sheets.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(214, 231, 251);
            _sheets.DefaultCellStyle.SelectionForeColor = System.Drawing.Color.FromArgb(20, 36, 60);
            _sheets.AlternatingRowsDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(248, 250, 253);
            AddColumn("Order", "序", 42, true); AddColumn("Building", "子项目", 88, false);
            AddColumn("SheetNumber", "图号", 90, false); AddColumn("SheetName", "图名", 150, false);
            AddColumn("FrameDisplay", "图框", 78, true);
            AddColumn("OutputPaperSize", "PDF 尺寸", 112, true);
            AddComboColumn("PaperOrientation", "方向", 68, new[] { "横向", "纵向" });
            AddColumn("PrintScale", "打印比例", 82, false);
            AddComboColumn("PlotStyle", "打印样式", 150, PlotStyleChoices());
            AddColumn("SourceFile", "来源文件", 180, true);
            _sheets.DataError += (s, e) => e.ThrowException = false;
        }

        private void AddColumn(string property, string title, int width, bool readOnly)
        {
            _sheets.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width, ReadOnly = readOnly });
        }

        private void AddComboColumn(string property, string title, int width, IEnumerable<string> items)
        {
            var column = new DataGridViewComboBoxColumn
            {
                Name = property + "Column",
                DataPropertyName = property,
                HeaderText = title,
                Width = width,
                FlatStyle = FlatStyle.Flat,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton
            };
            foreach (var item in items.Distinct(StringComparer.OrdinalIgnoreCase)) column.Items.Add(item);
            _sheets.Columns.Add(column);
        }

        private IEnumerable<string> PlotStyleChoices()
        {
            return new[] { "使用输出设置" }
                .Concat(_viewModel.AvailablePlotStyles)
                .Concat(_viewModel.Sheets.Select(x => x.PlotStyle).Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private void WireEvents()
        {
            _projects.SelectedIndexChanged += (s, e) => { if (_refreshing) return; _viewModel.SelectedProject = _projects.SelectedItem as ProjectProfile; RefreshAll(); };
            _buildings.SelectedIndexChanged += (s, e) => { if (_refreshing) return; _viewModel.SelectedBuilding = _buildings.SelectedItem as string; RefreshSheets(); };
            _frames.SelectedIndexChanged += (s, e) => { if (!_refreshing) _viewModel.SelectedFrame = _frames.SelectedItem as FrameDefinition; };
            _frames.DoubleClick += (s, e) => EditFrame();
            _sheets.SelectionChanged += (s, e) => { if (_refreshing) return; _viewModel.SelectedSheet = CurrentSheet(); };
            _sheets.CellEndEdit += (s, e) =>
            {
                if (_refreshing || _gridCommitPending) return;
                var item = _sheets.Rows[e.RowIndex].DataBoundItem as SheetItem;
                _gridCommitPending = true;
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        _viewModel.SelectedSheet = item;
                        _viewModel.ApplySheetEdits();
                        RefreshAll();
                    }
                    finally { _gridCommitPending = false; }
                }));
            };
            _plotStyle.TextChanged += (s, e) => { if (!_refreshing) _viewModel.PlotStyle = _plotStyle.Text; };
            _marginMode.TextChanged += (s, e) => { if (!_refreshing) _viewModel.MarginMode = _marginMode.Text; };
            _outputDirectory.TextChanged += (s, e) => { if (!_refreshing) _viewModel.OutputDirectory = _outputDirectory.Text; };
            _mergeByBuilding.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.MergeByBuilding = _mergeByBuilding.Checked; };
            _previewEnabled.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.PreviewEnabled = _previewEnabled.Checked; };
            _viewModel.PropertyChanged += ViewModelPropertyChanged;
            _viewModel.Frames.CollectionChanged += (s, e) => BeginInvoke(new Action(RefreshFrames));
            FormClosed += (s, e) => _viewModel.Dispose();
        }

        private void ViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Status") _status.Text = _viewModel.Status;
            if (e.PropertyName == "PublishProgressValue" || e.PropertyName == "PublishProgressMaximum" || e.PropertyName == "IsPublishing") RefreshPublishProgress();
        }

        private void RefreshPublishProgress()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshPublishProgress)); return; }
            var maximum = Math.Max(_viewModel.PublishProgressMaximum, 1);
            var value = Math.Min(Math.Max(_viewModel.PublishProgressValue, 0), maximum);
            _publishProgress.Maximum = maximum;
            _publishProgress.Value = value;
            _publishProgressText.Text = value + " / " + ( _viewModel.IsPublishing ? maximum : value == 0 ? 0 : maximum);
            _publishProgress.Refresh();
            _publishProgressText.Refresh();
            _status.Refresh();
        }

        private void RefreshAll()
        {
            _refreshing = true;
            try
            {
                _projects.DataSource = _viewModel.Projects.ToList(); _projects.DisplayMember = "Name"; _projects.SelectedItem = _viewModel.SelectedProject;
                _newProjectName.Text = _viewModel.NewProjectName;
                RefreshFramesCore();
                _buildings.DataSource = _viewModel.Buildings.ToList(); _buildings.SelectedItem = _viewModel.SelectedBuilding;
                RefreshPlotStyles(); _marginMode.Text = _viewModel.MarginMode;
                _outputDirectory.Text = _viewModel.OutputDirectory; _mergeByBuilding.Checked = _viewModel.MergeByBuilding;
                _previewEnabled.Checked = _viewModel.PreviewEnabled;
                _status.Text = _viewModel.Status;
                RefreshPublishProgress();
                RefreshSheetsCore();
            }
            finally { _refreshing = false; }
        }

        private void RefreshFrames()
        {
            _refreshing = true; try { RefreshFramesCore(); _status.Text = _viewModel.Status; } finally { _refreshing = false; }
        }

        private void RefreshFramesCore()
        {
            _frames.DataSource = _viewModel.Frames.ToList(); _frames.DisplayMember = "DisplayName"; _frames.SelectedItem = _viewModel.SelectedFrame;
        }

        private void RefreshSheets()
        {
            _refreshing = true; try { RefreshSheetsCore(); } finally { _refreshing = false; }
        }

        private void RefreshSheetsCore()
        {
            var visible = _viewModel.SheetView.Cast<SheetItem>().ToList();
            _sheets.DataSource = new BindingList<SheetItem>(visible);
            if (_viewModel.SelectedSheet != null)
                foreach (DataGridViewRow row in _sheets.Rows) if (ReferenceEquals(row.DataBoundItem, _viewModel.SelectedSheet)) { row.Selected = true; break; }
        }

        private void RefreshPlotStyles()
        {
            var selected = _viewModel.PlotStyle;
            _plotStyle.BeginUpdate();
            try
            {
                _plotStyle.Items.Clear();
                foreach (var style in _viewModel.AvailablePlotStyles) _plotStyle.Items.Add(style);
                _plotStyle.Text = selected ?? string.Empty;
                var column = _sheets.Columns["PlotStyleColumn"] as DataGridViewComboBoxColumn;
                if (column != null)
                {
                    column.Items.Clear();
                    foreach (var style in PlotStyleChoices().Distinct(StringComparer.OrdinalIgnoreCase)) column.Items.Add(style);
                }
            }
            finally { _plotStyle.EndUpdate(); }
        }

        private void CreateProject()
        {
            _viewModel.NewProjectName = _newProjectName.Text; _viewModel.NewProjectCommand.Execute(null); RefreshAll();
        }

        public void ScanDrawing()
        {
            _viewModel.ScanCommand.Execute(null);
            RefreshAll();
        }

        public void PublishPdf()
        {
            Validate();
            _sheets.EndEdit();
            _viewModel.PlotStyle = _plotStyle.Text;
            _viewModel.OutputDirectory = _outputDirectory.Text;
            _viewModel.PublishCommand.Execute(null);
        }

        private void EditFrame()
        {
            _viewModel.SelectedFrame = _frames.SelectedItem as FrameDefinition; _viewModel.EditFrameCommand.Execute(null); RefreshFrames();
        }

        private void RemoveFrame()
        {
            _viewModel.SelectedFrame = _frames.SelectedItem as FrameDefinition; _viewModel.RemoveFrameCommand.Execute(null); RefreshFrames();
        }

        private SheetItem CurrentSheet()
        {
            return _sheets.CurrentRow == null ? null : _sheets.CurrentRow.DataBoundItem as SheetItem;
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(3),
                Padding = new Padding(8, 3, 8, 3),
                MinimumSize = new System.Drawing.Size(0, 31),
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.FromArgb(31, 48, 74),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(190, 201, 216);
            if (action != null) button.Click += (s, e) => action();
            return button;
        }

        private static Button AccentButton(string text, Action action)
        {
            var button = Button(text, action);
            button.BackColor = System.Drawing.Color.FromArgb(34, 116, 210);
            button.ForeColor = System.Drawing.Color.White;
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(34, 116, 210);
            return button;
        }

        private static TableLayoutPanel Card(int rows, Padding padding)
        {
            return new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = rows,
                ColumnCount = 1,
                Padding = padding,
                BackColor = System.Drawing.Color.White,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
                , BorderStyle = BorderStyle.FixedSingle
            };
        }

        private static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        }

        private static Label SectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.FromArgb(25, 45, 78),
                Margin = new Padding(3, 3, 3, 8)
            };
        }
    }
}
