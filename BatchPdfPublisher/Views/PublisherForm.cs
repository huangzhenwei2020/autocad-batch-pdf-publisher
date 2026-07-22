using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
        private readonly CheckedListBox _cadFiles = new CheckedListBox();
        private readonly ListBox _frames = new ListBox();
        private readonly DataGridView _sheets = new DataGridView();
        private readonly ComboBox _plotStyle = new ComboBox();
        private readonly ComboBox _marginMode = new ComboBox();
        private readonly TextBox _outputDirectory = new TextBox();
        private readonly Label _actualOutputDirectories = new Label();
        private readonly CheckedListBox _publishBuildings = new CheckedListBox();
        private readonly CheckBox _outputNextToCad = new CheckBox();
        private readonly CheckBox _includeProjectName = new CheckBox();
        private readonly CheckBox _includeBuildingName = new CheckBox();
        private readonly CheckBox _overwriteExisting = new CheckBox();
        private readonly CheckBox _mergeByBuilding = new CheckBox();
        private readonly CheckBox _previewEnabled = new CheckBox();
        private readonly Label _status = new Label();
        private readonly ProgressBar _publishProgress = new ProgressBar();
        private readonly Label _publishProgressText = new Label();
        private readonly ToolTip _toolTip = new ToolTip();
        private bool _refreshing;
        private bool _gridCommitPending;

        public PublisherForm()
        {
            Text = "批量 PDF 发布  v0.6.5";
            Width = 1240;
            Height = 760;
            MinimumSize = new System.Drawing.Size(840, 540);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            SizeGripStyle = SizeGripStyle.Show;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

            BuildInterface();
            WireEvents();
            RefreshAll();
        }

        private void BuildInterface()
        {
            BackColor = System.Drawing.Color.FromArgb(242, 245, 249);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0), RowCount = 4, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = Padding.Empty, BackColor = System.Drawing.Color.White };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "批量 PDF 发布", ForeColor = System.Drawing.Color.White, Font = new System.Drawing.Font(Font.FontFamily, 15F, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(0, 8) });
            header.Controls.Add(heading, 0, 0);
            var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            header.Controls.Add(headerActions, 1, 0);
            root.Controls.Add(header, 0, 0);

            var projectBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 9, 18, 7), BackColor = System.Drawing.Color.White, WrapContents = false };
            projectBar.Controls.Add(new Label { Text = "工程名称", AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = new Padding(2, 7, 10, 0) });
            _projects.Width = 190; _projects.DropDownStyle = ComboBoxStyle.DropDownList;
            _newProjectName.Width = 160; _newProjectName.Margin = new Padding(14, 3, 4, 3);
            projectBar.Controls.Add(_projects); projectBar.Controls.Add(_newProjectName);
            projectBar.Controls.Add(Button("新建工程", CreateProject));
            projectBar.Controls.Add(Button("扫描设置", ConfigureScanScope));
            projectBar.Controls.Add(Button("保存工程参数", () => _viewModel.SaveProjectCommand.Execute(null)));
            root.Controls.Add(projectBar, 0, 1);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 10) };
            root.Controls.Add(body, 0, 2);

            var leftSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                IsSplitterFixed = false,
                SplitterWidth = 7
            };
            body.Controls.Add(leftSplitter);

            var rightSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                IsSplitterFixed = false,
                SplitterWidth = 7
            };
            leftSplitter.Panel2.Controls.Add(rightSplitter);

            var left = Card(10, new Padding(12)); left.Margin = new Padding(0, 0, 10, 0);
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 21));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            left.Controls.Add(SectionLabel("工程 CAD 文件（双击打开）"), 0, 0); _cadFiles.Dock = DockStyle.Fill; _cadFiles.CheckOnClick = true; _cadFiles.HorizontalScrollbar = true; left.Controls.Add(_cadFiles, 0, 1);
            var cadButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            cadButtons.Controls.Add(Button("添加", ChooseCadFiles)); cadButtons.Controls.Add(Button("移除", RemoveCadFile));
            cadButtons.Controls.Add(AccentButton("扫描当前", () => { _viewModel.ScanCommand.Execute(null); RefreshAll(); }));
            cadButtons.Controls.Add(AccentButton("扫描所选", ScanCheckedCadFiles));
            cadButtons.Controls.Add(Button("框选发布当前文件", OpenCurrentSelectionPublisher)); left.Controls.Add(cadButtons, 0, 2);
            left.Controls.Add(SectionLabel("子项目名称"), 0, 3); _buildings.Dock = DockStyle.Fill; left.Controls.Add(_buildings, 0, 4);
            var frameToggle = new CheckBox { Text = "图框登记（展开后可修改）", AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), ForeColor = System.Drawing.Color.FromArgb(25, 45, 78), Margin = new Padding(3, 6, 3, 3) };
            left.Controls.Add(frameToggle, 0, 5); _frames.Dock = DockStyle.Fill; _frames.HorizontalScrollbar = true; _frames.Visible = false; left.Controls.Add(_frames, 0, 6);
            var frameButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            frameButtons.Controls.Add(AccentButton("拾取并登记", () => _viewModel.RegisterFrameCommand.Execute(null)));
            frameButtons.Controls.Add(Button("修改", EditFrame)); frameButtons.Controls.Add(Button("删除", RemoveFrame));
            frameButtons.Visible = false;
            left.Controls.Add(frameButtons, 0, 7);
            var saveFrames = Button("保存当前图框库", () => _viewModel.SaveFrameLibraryCommand.Execute(null)); saveFrames.Visible = false; left.Controls.Add(saveFrames, 0, 8);
            left.RowStyles[6].SizeType = SizeType.Absolute; left.RowStyles[6].Height = 0; left.RowStyles[7].SizeType = SizeType.Absolute; left.RowStyles[7].Height = 0; left.RowStyles[8].SizeType = SizeType.Absolute; left.RowStyles[8].Height = 0;
            frameToggle.CheckedChanged += (s, e) => { _frames.Visible = frameToggle.Checked; frameButtons.Visible = frameToggle.Checked; saveFrames.Visible = frameToggle.Checked; left.RowStyles[6].Height = frameToggle.Checked ? 150 : 0; left.RowStyles[7].Height = frameToggle.Checked ? 36 : 0; left.RowStyles[8].Height = frameToggle.Checked ? 36 : 0; };
            left.Margin = Padding.Empty;
            leftSplitter.Panel1.Controls.Add(left);

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
            center.Margin = Padding.Empty;
            rightSplitter.Panel1.Controls.Add(center);

            var right = Card(20, new Padding(14));
            right.Controls.Add(SectionLabel("输出设置")); right.Controls.Add(Label("CAD 打印样式"));
            _plotStyle.DropDownStyle = ComboBoxStyle.DropDown; _plotStyle.Dock = DockStyle.Top;
            right.Controls.Add(_plotStyle);
            var plotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            plotButtons.Controls.Add(Button("刷新 CAD 样式", () => { _viewModel.RefreshPlotStylesCommand.Execute(null); RefreshPlotStyles(); }));
            plotButtons.Controls.Add(Button("收藏当前样式", () => { _viewModel.SaveFavoritePlotStyleCommand.Execute(null); RefreshPlotStyles(); }));
            right.Controls.Add(plotButtons); right.Controls.Add(Label("白边 / 出血位（单位：mm）"));
            _marginMode.Items.AddRange(new object[] { "自动适配", "无白边（满幅）", "保留 3 mm 白边" });
            _marginMode.Dock = DockStyle.Top; right.Controls.Add(_marginMode); right.Controls.Add(Label("输出目录"));
            var outputFolder = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
            _outputDirectory.Width = 150; outputFolder.Controls.Add(_outputDirectory);
            outputFolder.Controls.Add(Button("选择", ChooseOutputDirectory)); outputFolder.Controls.Add(Button("打开", OpenOutputDirectory));
            right.Controls.Add(outputFolder);
            _outputNextToCad.Text = "输出到各 CAD 文件同级目录"; _outputNextToCad.AutoSize = true; right.Controls.Add(_outputNextToCad);
            _actualOutputDirectories.AutoSize = true;
            _actualOutputDirectories.MaximumSize = new System.Drawing.Size(270, 66);
            _actualOutputDirectories.ForeColor = System.Drawing.Color.FromArgb(77, 99, 128);
            _actualOutputDirectories.Padding = new Padding(3, 1, 3, 3);
            right.Controls.Add(_actualOutputDirectories);
            _mergeByBuilding.Text = "每个子项目生成一个 PDF"; _mergeByBuilding.AutoSize = true; _mergeByBuilding.Margin = new Padding(3, 12, 3, 3); right.Controls.Add(_mergeByBuilding);
            var publishBuildingHeader = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
            publishBuildingHeader.Controls.Add(SectionLabel("发布子项目（可多选）"));
            publishBuildingHeader.Controls.Add(Button("全选", () => SetAllPublishBuildings(true)));
            publishBuildingHeader.Controls.Add(Button("取消全选", () => SetAllPublishBuildings(false)));
            right.Controls.Add(publishBuildingHeader);
            _publishBuildings.CheckOnClick = true; _publishBuildings.Height = 106; _publishBuildings.Dock = DockStyle.Top; right.Controls.Add(_publishBuildings);
            right.Controls.Add(SectionLabel("PDF 文件命名"));
            _includeProjectName.Text = "文件名包含工程名"; _includeProjectName.AutoSize = true; right.Controls.Add(_includeProjectName);
            _includeBuildingName.Text = "文件名包含子项目名"; _includeBuildingName.AutoSize = true; right.Controls.Add(_includeBuildingName);
            _overwriteExisting.Text = "同名 PDF 直接覆盖"; _overwriteExisting.AutoSize = true; right.Controls.Add(_overwriteExisting);
            rightSplitter.Panel2.Controls.Add(right);

            body.Resize += (sender, args) =>
            {
                // Keep both side panels usable on small or high-DPI displays;
                // users can still drag either splitter to their preferred width.
                if (leftSplitter.Width > leftSplitter.Panel1MinSize + leftSplitter.Panel2MinSize + leftSplitter.SplitterWidth)
                    leftSplitter.SplitterDistance = Math.Min(leftSplitter.SplitterDistance, Math.Max(leftSplitter.Panel1MinSize, leftSplitter.Width / 3));
                if (rightSplitter.Width > rightSplitter.Panel1MinSize + rightSplitter.Panel2MinSize + rightSplitter.SplitterWidth)
                    rightSplitter.SplitterDistance = Math.Min(rightSplitter.SplitterDistance, rightSplitter.Width - rightSplitter.Panel2MinSize - rightSplitter.SplitterWidth);
            };
            Shown += (sender, args) =>
            {
                leftSplitter.Panel1MinSize = 180;
                leftSplitter.Panel2MinSize = Math.Min(480, Math.Max(180, leftSplitter.Width - 180 - leftSplitter.SplitterWidth));
                leftSplitter.SplitterDistance = Math.Min(250, Math.Max(leftSplitter.Panel1MinSize, leftSplitter.Width / 4));

                rightSplitter.Panel1MinSize = Math.Min(320, Math.Max(160, rightSplitter.Width - 190 - rightSplitter.SplitterWidth));
                rightSplitter.Panel2MinSize = Math.Min(190, Math.Max(140, rightSplitter.Width - rightSplitter.Panel1MinSize - rightSplitter.SplitterWidth));
                rightSplitter.SplitterDistance = Math.Max(rightSplitter.Panel1MinSize, rightSplitter.Width - 260 - rightSplitter.SplitterWidth);
            };

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.White, Padding = new Padding(16, 6, 16, 4), ColumnCount = 4 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.Dock = DockStyle.Fill; _status.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); footer.Controls.Add(_status, 0, 0);
            _publishProgress.Dock = DockStyle.Fill; _publishProgress.Minimum = 0; _publishProgress.Maximum = 1; _publishProgress.Value = 0; _publishProgress.Style = ProgressBarStyle.Continuous; footer.Controls.Add(_publishProgress, 1, 0);
            _publishProgressText.AutoSize = true; _publishProgressText.MinimumSize = new System.Drawing.Size(82, 0); _publishProgressText.Padding = new Padding(8, 0, 2, 0); _publishProgressText.Dock = DockStyle.Fill; _publishProgressText.TextAlign = System.Drawing.ContentAlignment.MiddleRight; _publishProgressText.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); _publishProgressText.Text = "0 / 0"; footer.Controls.Add(_publishProgressText, 2, 0);
            footer.Controls.Add(AccentButton("发布 PDF", PublishPdf), 3, 0);
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
            AddColumn("Order", "序", 42, true); AddComboColumn("Building", "子项目", 100, _viewModel.Buildings.Concat(new[] { "未分组" }));
            AddColumn("SheetNumber", "图号", 90, false); AddColumn("SheetName", "图名", 150, false);
            AddColumn("FrameDisplay", "图框", 78, true);
            AddColumn("OutputPaperSize", "PDF 尺寸", 112, true);
            AddComboColumn("PaperOrientation", "方向", 68, new[] { "横向", "纵向" });
            AddColumn("PrintScale", "打印比例", 82, false);
            AddComboColumn("PlotStyle", "打印样式", 150, PlotStyleChoices());
            AddColumn("SourceFileName", "CAD 文件", 128, true);
            AddColumn("SourceFile", "来源文件", 180, true);
            AddColumn("SourceLayout", "空间", 96, true);
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
            _cadFiles.ItemCheck += (s, e) =>
            {
                if (_refreshing || e.Index < 0) return;
                BeginInvoke(new Action(() =>
                {
                    var item = _cadFiles.Items[e.Index] as CadFileItem;
                    if (item != null) _viewModel.SetCadFileSelected(item.Path, _cadFiles.GetItemChecked(e.Index));
                    RefreshActualOutputDirectories();
                }));
            };
            _cadFiles.DoubleClick += (s, e) =>
            {
                var item = _cadFiles.SelectedItem as CadFileItem;
                if (item != null) _viewModel.OpenCadFile(item.Path);
            };
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
            _outputDirectory.TextChanged += (s, e) => { if (!_refreshing) _viewModel.OutputDirectory = _outputDirectory.Text; RefreshActualOutputDirectories(); };
            _mergeByBuilding.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.MergeByBuilding = _mergeByBuilding.Checked; };
            _outputNextToCad.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.OutputNextToCadFile = _outputNextToCad.Checked; RefreshActualOutputDirectories(); };
            _includeProjectName.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.IncludeProjectNameInFileName = _includeProjectName.Checked; };
            _includeBuildingName.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.IncludeBuildingNameInFileName = _includeBuildingName.Checked; };
            _overwriteExisting.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.OverwriteExistingPdf = _overwriteExisting.Checked; };
            _publishBuildings.ItemCheck += (s, e) =>
            {
                if (_refreshing || e.Index < 0) return;
                BeginInvoke(new Action(() =>
                {
                    var item = _publishBuildings.Items[e.Index] as BuildingPublishItem;
                    if (item != null) _viewModel.SetPublishBuilding(item.Name, _publishBuildings.GetItemChecked(e.Index));
                }));
            };
            _previewEnabled.CheckedChanged += (s, e) => { if (!_refreshing) _viewModel.PreviewEnabled = _previewEnabled.Checked; };
            _viewModel.PropertyChanged += ViewModelPropertyChanged;
            _viewModel.Frames.CollectionChanged += (s, e) => BeginInvoke(new Action(RefreshFrames));
            FormClosed += (s, e) => _viewModel.Dispose();
        }

        private void ViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Status") _status.Text = _viewModel.Status;
            if (e.PropertyName == "Sheets") RefreshSheets();
            if (e.PropertyName == "SelectedBuilding")
            {
                _refreshing = true;
                try { _buildings.SelectedItem = _viewModel.SelectedBuilding; RefreshSheetsCore(); }
                finally { _refreshing = false; }
            }
            if (e.PropertyName == "SelectedSheet") RefreshSheets();
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
                _cadFiles.Items.Clear();
                foreach (var item in _viewModel.CadFiles) _cadFiles.Items.Add(item, item.IsSelected);
                RefreshPlotStyles(); _marginMode.Text = _viewModel.MarginMode;
                _outputDirectory.Text = _viewModel.OutputDirectory; _mergeByBuilding.Checked = _viewModel.MergeByBuilding;
                _outputNextToCad.Checked = _viewModel.OutputNextToCadFile;
                RefreshActualOutputDirectories();
                _includeProjectName.Checked = _viewModel.IncludeProjectNameInFileName;
                _includeBuildingName.Checked = _viewModel.IncludeBuildingNameInFileName;
                _overwriteExisting.Checked = _viewModel.OverwriteExistingPdf;
                _publishBuildings.Items.Clear();
                foreach (var item in _viewModel.PublishBuildings) _publishBuildings.Items.Add(item, item.IsSelected);
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
            var buildingColumn = _sheets.Columns["BuildingColumn"] as DataGridViewComboBoxColumn;
            if (buildingColumn != null)
            {
                buildingColumn.Items.Clear();
                foreach (var name in _viewModel.Buildings.Concat(new[] { "未分组" }).Distinct()) buildingColumn.Items.Add(name);
            }
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

        private void ChooseCadFiles()
        {
            using (var dialog = new OpenFileDialog { Filter = "AutoCAD 图纸 (*.dwg)|*.dwg", Multiselect = true, Title = "选择要扫描的 CAD 文件" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _viewModel.AddCadFiles(dialog.FileNames);
                RefreshAll();
            }
        }

        private void RemoveCadFile()
        {
            var item = _cadFiles.SelectedItem as CadFileItem;
            if (item == null) return;
            _viewModel.RemoveCadFile(item.Path);
            RefreshAll();
        }

        private void ScanCheckedCadFiles()
        {
            // Read CheckedItems at click time. This avoids relying on the
            // deferred ItemCheck event when the user checks a file and
            // immediately presses “扫描所选”.
            var paths = _cadFiles.CheckedItems.Cast<CadFileItem>().Select(x => x.Path).ToList();
            foreach (CadFileItem item in _cadFiles.Items)
                _viewModel.SetCadFileSelected(item.Path, paths.Contains(item.Path, StringComparer.OrdinalIgnoreCase));
            _viewModel.ScanCadFiles(paths);
            RefreshAll();
        }

        private void SetAllPublishBuildings(bool selected)
        {
            for (var index = 0; index < _publishBuildings.Items.Count; index++) _publishBuildings.SetItemChecked(index, selected);
        }

        private void ConfigureScanScope()
        {
            var layouts = _viewModel.GetActiveLayoutNames();
            if (layouts.Count == 0 && !_viewModel.ScanModelSpace)
            {
                MessageBox.Show(this, "当前图纸没有可选空间。", "扫描设置");
                return;
            }
            using (var dialog = new Form
            {
                Text = "扫描设置",
                Width = 360,
                Height = 430,
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                MinimizeBox = false,
                MaximizeBox = false
            })
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.Controls.Add(new Label { Text = "勾选本次要扫描的空间（设置会保存到工程）", AutoSize = true }, 0, 0);
                var spaces = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
                spaces.Items.Add("模型空间", _viewModel.ScanModelSpace);
                foreach (var layout in layouts)
                    spaces.Items.Add(layout, _viewModel.ScanAllLayouts || (_viewModel.SelectedProject?.SelectedLayouts?.Contains(layout) ?? false));
                panel.Controls.Add(spaces, 0, 1);
                var allLayouts = new CheckBox { Text = "自动扫描所有布局（包括以后新增的布局）", AutoSize = true, Checked = _viewModel.ScanAllLayouts };
                panel.Controls.Add(allLayouts, 0, 2);
                var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var ok = Button("确定", () => dialog.DialogResult = DialogResult.OK);
                var cancel = Button("取消", () => dialog.DialogResult = DialogResult.Cancel);
                actions.Controls.Add(ok); actions.Controls.Add(cancel); panel.Controls.Add(actions, 0, 3);
                dialog.Controls.Add(panel);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var selected = new List<string>();
                for (var i = 1; i < spaces.Items.Count; i++) if (spaces.GetItemChecked(i)) selected.Add((string)spaces.Items[i]);
                _viewModel.SetScanScope(spaces.GetItemChecked(0), selected, allLayouts.Checked);
            }
        }

        private void ChooseOutputDirectory()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择 PDF 输出目录", SelectedPath = _outputDirectory.Text })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) _outputDirectory.Text = dialog.SelectedPath;
            }
        }

        private void OpenOutputDirectory()
        {
            var folder = _outputDirectory.Text;
            if (_outputNextToCad.Checked)
            {
                var firstCad = _viewModel.SelectedProject?.CadFiles?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstCad)) folder = Path.GetDirectoryName(firstCad);
            }
            if (string.IsNullOrWhiteSpace(folder)) { MessageBox.Show(this, "请先选择输出目录或扫描 CAD 文件。", "批量 PDF 发布"); return; }
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void RefreshActualOutputDirectories()
        {
            _outputDirectory.Enabled = !_outputNextToCad.Checked;
            if (!_outputNextToCad.Checked)
            {
                _actualOutputDirectories.Text = "实际输出：" + (string.IsNullOrWhiteSpace(_outputDirectory.Text) ? "尚未选择" : _outputDirectory.Text);
                return;
            }
            var directories = _viewModel.CadFiles
                .Where(x => x.IsSelected && !string.IsNullOrWhiteSpace(x.Path))
                .Select(x => Path.GetDirectoryName(x.Path))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (directories.Count == 0)
            {
                _actualOutputDirectories.Text = "实际输出：请先勾选 CAD 文件";
                return;
            }
            _actualOutputDirectories.Text = "实际输出：\r\n" + string.Join("\r\n", directories.Take(2))
                + (directories.Count > 2 ? "\r\n…另有 " + (directories.Count - 2) + " 个目录" : string.Empty);
            _toolTip.SetToolTip(_actualOutputDirectories, string.Join(Environment.NewLine, directories));
        }

        private void OpenCurrentSelectionPublisher()
        {
            var dialog = new CurrentSelectionPublishForm(_viewModel.Frames.ToList(), _plotStyle.Text, _marginMode.Text);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
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
                Padding = new Padding(5, 2, 5, 2),
                MinimumSize = new System.Drawing.Size(0, 26),
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
