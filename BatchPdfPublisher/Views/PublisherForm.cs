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
        private readonly Label _projectSummary = new Label();
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
        private readonly Label _sheetCountText = new Label();
        private readonly Panel _progressTrack = new Panel();
        private readonly Label _publishProgressText = new Label();
        private readonly ToolTip _toolTip = new ToolTip();
        private bool _refreshing;
        private bool _gridCommitPending;

        public PublisherForm()
        {
            Text = "批量 PDF 发布  v0.8.2";
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
            ApplyInputStyle(_projects);
            ApplyInputStyle(_newProjectName);
            ApplyInputStyle(_plotStyle);
            ApplyInputStyle(_marginMode);
            ApplyInputStyle(_outputDirectory);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0), RowCount = 4, ColumnCount = 1 };
            // The native window title bar is the app title.  The approved
            // design keeps the project actions in a white row directly below
            // it; the internal decorative header therefore remains collapsed.
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = Padding.Empty, BackColor = System.Drawing.Color.FromArgb(24, 49, 84) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var heading = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 6, 0, 0) };
            heading.Controls.Add(new Label { Text = "批量 PDF 发布", ForeColor = System.Drawing.Color.White, Font = new System.Drawing.Font(Font.FontFamily, 17F, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(24, 5) });
            heading.Controls.Add(new Label { Text = "图框登记 · 自动排序 · PDF 预览", ForeColor = System.Drawing.Color.FromArgb(194, 211, 233), Font = new System.Drawing.Font(Font.FontFamily, 9F), AutoSize = true, Location = new System.Drawing.Point(26, 32) });
            header.Controls.Add(heading, 0, 0);
            var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 10, 14, 0), BackColor = System.Drawing.Color.FromArgb(24, 49, 84) };
            headerActions.Controls.Add(IconAccentButton("扫描当前", UiIcon.Refresh, () => { _viewModel.ScanCommand.Execute(null); RefreshAll(); }));
            headerActions.Controls.Add(IconAccentButton("发布 PDF", UiIcon.Publish, PublishPdf));
            header.Controls.Add(headerActions, 1, 0);
            root.Controls.Add(header, 0, 0);

            var projectBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 6, 18, 5), BackColor = System.Drawing.Color.White, WrapContents = false };
            projectBar.Controls.Add(new Label { Text = "当前项目：", AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = new Padding(0, 8, 8, 0) });
            _projects.Width = 270; _projects.DropDownStyle = ComboBoxStyle.DropDownList; _projects.Margin = new Padding(0, 1, 10, 0);
            projectBar.Controls.Add(_projects);
            projectBar.Controls.Add(IconButton("插入目录", UiIcon.List, OpenCatalogInsert));
            projectBar.Controls.Add(IconButton("存入工程", UiIcon.Save, SaveCurrentCad));
            projectBar.Controls.Add(IconButton("目录打印", UiIcon.Publish, PrintProjectFolder));
            projectBar.Controls.Add(IconButton("自动保存", UiIcon.Folder, SyncAutoSave));
            var projectManagerButton = ProjectManagerButton(); projectManagerButton.Margin = new Padding(0, 1, 0, 0); projectBar.Controls.Add(projectManagerButton);
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

            // The left rail has three independently resizable sections.  The
            // lower splitter defaults to the project/building list being the
            // largest, while CAD files and frame definitions remain usable.
            var cadBuildingSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 32, IsSplitterFixed = false, FixedPanel = FixedPanel.None };
            var buildingFrameSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 32, IsSplitterFixed = false, FixedPanel = FixedPanel.None };
            AddHeightDragIndicator(cadBuildingSplit);
            AddHeightDragIndicator(buildingFrameSplit);
            cadBuildingSplit.Panel2.Controls.Add(buildingFrameSplit);

            var cadPane = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 3, ColumnCount = 1 };
            cadPane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); cadPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); cadPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cadPane.Controls.Add(SectionHeader("工程 CAD 文件（双击打开）"), 0, 0);
            _cadFiles.Dock = DockStyle.Fill; _cadFiles.Margin = new Padding(8, 8, 8, 4); _cadFiles.CheckOnClick = true; _cadFiles.HorizontalScrollbar = true; cadPane.Controls.Add(_cadFiles, 0, 1);
            var cadButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(8, 0, 8, 6) };
            cadButtons.Controls.Add(IconButton("添加文件", UiIcon.Plus, ChooseCadFiles)); cadButtons.Controls.Add(IconButton("移除文件", UiIcon.Remove, RemoveCadFile));
            cadButtons.Controls.Add(IconButton("全部保存", UiIcon.SaveAll, SaveAllCadFiles));
            cadButtons.Controls.Add(IconAccentButton("扫描当前", UiIcon.Refresh, () => { _viewModel.ScanCommand.Execute(null); RefreshAll(); }));
            cadButtons.Controls.Add(IconAccentButton("扫描所选", UiIcon.List, ScanCheckedCadFiles));
            cadButtons.Controls.Add(IconButton("框选发布", UiIcon.Select, OpenCurrentSelectionPublisher)); cadPane.Controls.Add(cadButtons, 0, 2);
            cadBuildingSplit.Panel1.Controls.Add(cadPane);

            var buildingPane = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 3, ColumnCount = 1 };
            buildingPane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); buildingPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); buildingPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buildingPane.Controls.Add(SectionHeader("子项目名称"), 0, 0); _buildings.Dock = DockStyle.Fill; _buildings.Margin = new Padding(8, 8, 8, 4); buildingPane.Controls.Add(_buildings, 0, 1);
            var frameToggle = new CheckBox
            {
                Text = "▶ 图框登记（点击展开后可修改）",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 40,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 6, 0),
                CheckAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Font = new System.Drawing.Font(Font.FontFamily, 9F, System.Drawing.FontStyle.Bold),
                AutoEllipsis = true,
                ForeColor = System.Drawing.Color.FromArgb(31, 48, 74),
                BackColor = System.Drawing.Color.FromArgb(231, 237, 246),
                Margin = Padding.Empty
            };
            ApplyRoundedRegion(frameToggle, 4);
            buildingPane.Controls.Add(frameToggle, 0, 2);
            buildingFrameSplit.Panel1.Controls.Add(buildingPane);

            var framePane = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 4, ColumnCount = 1 };
            framePane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); framePane.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); framePane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); framePane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            framePane.RowStyles[0] = new RowStyle(SizeType.Absolute, 0);
            _frames.Dock = DockStyle.Fill; _frames.Margin = new Padding(8, 8, 8, 4); _frames.HorizontalScrollbar = true; framePane.Controls.Add(_frames, 0, 1);
            var frameButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(8, 0, 8, 4) };
            frameButtons.Controls.Add(IconAccentButton("拾取登记", UiIcon.Select, () => _viewModel.RegisterFrameCommand.Execute(null)));
            frameButtons.Controls.Add(IconButton("创建图框", UiIcon.Plus, OpenFrameCreation));
            frameButtons.Controls.Add(IconButton("修改图框", UiIcon.Gear, EditFrame)); frameButtons.Controls.Add(IconButton("删除图框", UiIcon.Remove, RemoveFrame)); framePane.Controls.Add(frameButtons, 0, 2);
            framePane.Controls.Add(IconButton("保存图框", UiIcon.Save, () => _viewModel.SaveFrameLibraryCommand.Execute(null)), 0, 3);
            buildingFrameSplit.Panel2.Controls.Add(framePane);
            buildingFrameSplit.Panel2Collapsed = true;
            frameToggle.CheckedChanged += (sender, args) =>
            {
                buildingFrameSplit.Panel2Collapsed = !frameToggle.Checked;
                frameToggle.Text = frameToggle.Checked ? "▼ 图框登记（点击折叠）" : "▶ 图框登记（点击展开后可修改）";
                if (frameToggle.Checked && buildingFrameSplit.Height > 360)
                {
                    var preferredBuildingHeight = Math.Max(buildingFrameSplit.Panel1MinSize, buildingFrameSplit.Height * 3 / 5);
                    buildingFrameSplit.SplitterDistance = Math.Min(preferredBuildingHeight, buildingFrameSplit.Height - buildingFrameSplit.Panel2MinSize - buildingFrameSplit.SplitterWidth);
                }
            };
            var leftRail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0), BorderStyle = BorderStyle.FixedSingle, BackColor = System.Drawing.Color.White };
            ApplyRoundedRegion(leftRail, 5);
            leftRail.Controls.Add(cadBuildingSplit);
            leftSplitter.Panel1.Controls.Add(leftRail);

            var center = Card(3, new Padding(12, 12, 12, 0)); center.Margin = new Padding(0, 0, 10, 0);
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var sheetHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, Height = 30, WrapContents = false, BackColor = System.Drawing.Color.FromArgb(231, 237, 246), Padding = Padding.Empty };
            var sheetTitle = SectionHeader("图纸列表"); sheetTitle.Width = 220; sheetTitle.Height = 30; sheetHeader.Controls.Add(sheetTitle);
            _previewEnabled.Text = "显示当前子项目预览"; _previewEnabled.AutoSize = true; _previewEnabled.ForeColor = System.Drawing.Color.FromArgb(31, 48, 74); _previewEnabled.Checked = false; _previewEnabled.Margin = new Padding(16, 4, 8, 2);
            sheetHeader.Controls.Add(_previewEnabled);
            sheetHeader.Controls.Add(IconButton("更新预览", UiIcon.Refresh, () => _viewModel.RefreshPreview()));
            sheetHeader.Controls.Add(IconButton("上移图纸", UiIcon.Up, () => { _viewModel.MoveUpCommand.Execute(null); RefreshSheets(); }));
            sheetHeader.Controls.Add(IconButton("下移图纸", UiIcon.Down, () => { _viewModel.MoveDownCommand.Execute(null); RefreshSheets(); }));
            center.Controls.Add(sheetHeader, 0, 0);
            ConfigureGrid(); center.Controls.Add(_sheets, 0, 1);
            center.Margin = Padding.Empty;
            rightSplitter.Panel1.Controls.Add(center);

            var right = Card(20, new Padding(12, 12, 12, 0));
            right.AutoScroll = true;
            right.Controls.Add(SectionHeader("输出设置")); right.Controls.Add(Label("CAD 打印样式"));
            _plotStyle.DropDownStyle = ComboBoxStyle.DropDown; _plotStyle.Dock = DockStyle.Top;
            right.Controls.Add(_plotStyle);
            var plotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            plotButtons.Controls.Add(IconButton("刷新样式", UiIcon.Refresh, () => { _viewModel.RefreshPlotStylesCommand.Execute(null); RefreshPlotStyles(); }));
            plotButtons.Controls.Add(IconButton("收藏样式", UiIcon.Save, () => { _viewModel.SaveFavoritePlotStyleCommand.Execute(null); RefreshPlotStyles(); }));
            right.Controls.Add(plotButtons); right.Controls.Add(Label("白边 / 出血位（单位：mm）"));
            _marginMode.Items.AddRange(new object[] { "自动适配", "无白边（满幅）", "保留 3 mm 白边" });
            _marginMode.Dock = DockStyle.Top; right.Controls.Add(_marginMode); right.Controls.Add(Label("输出目录"));
            var outputFolder = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            _outputDirectory.Width = 150; outputFolder.Controls.Add(_outputDirectory);
            outputFolder.Controls.Add(IconButton("选择目录", UiIcon.Folder, ChooseOutputDirectory)); outputFolder.Controls.Add(IconButton("打开目录", UiIcon.Open, OpenOutputDirectory));
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
            publishBuildingHeader.Controls.Add(IconButton("全选项目", UiIcon.List, () => SetAllPublishBuildings(true)));
            publishBuildingHeader.Controls.Add(IconButton("取消全选", UiIcon.Remove, () => SetAllPublishBuildings(false)));
            right.Controls.Add(publishBuildingHeader);
            _publishBuildings.CheckOnClick = true; _publishBuildings.Height = 106; _publishBuildings.Dock = DockStyle.Top; right.Controls.Add(_publishBuildings);
            right.Controls.Add(SectionLabel("PDF 文件命名"));
            _includeProjectName.Text = "文件名包含工程名"; _includeProjectName.AutoSize = true; right.Controls.Add(_includeProjectName);
            _includeBuildingName.Text = "文件名包含子项目名"; _includeBuildingName.AutoSize = true; right.Controls.Add(_includeBuildingName);
            _overwriteExisting.Text = "同名 PDF 直接覆盖"; _overwriteExisting.AutoSize = true; right.Controls.Add(_overwriteExisting);
            rightSplitter.Panel2.Controls.Add(right);

            Shown += (sender, args) =>
            {
                leftSplitter.Panel1MinSize = Math.Min(320, Math.Max(240, leftSplitter.Width - 470 - leftSplitter.SplitterWidth));
                leftSplitter.Panel2MinSize = Math.Min(470, Math.Max(320, leftSplitter.Width - leftSplitter.Panel1MinSize - leftSplitter.SplitterWidth));
                leftSplitter.SplitterDistance = Math.Min(330, Math.Max(leftSplitter.Panel1MinSize, leftSplitter.Width / 4));
                // CAD file list keeps a practical default height; the project
                // list receives all remaining height while frame registration
                // is collapsed. Both dividers remain user-draggable.
                cadBuildingSplit.Panel1MinSize = 220; cadBuildingSplit.Panel2MinSize = 180;
                cadBuildingSplit.SplitterDistance = Math.Min(330, Math.Max(240, cadBuildingSplit.Height / 3));
                buildingFrameSplit.Panel1MinSize = 150; buildingFrameSplit.Panel2MinSize = 180;

                rightSplitter.Panel1MinSize = Math.Min(420, Math.Max(180, rightSplitter.Width - 300 - rightSplitter.SplitterWidth));
                rightSplitter.Panel2MinSize = Math.Min(300, Math.Max(210, rightSplitter.Width - rightSplitter.Panel1MinSize - rightSplitter.SplitterWidth));
                rightSplitter.SplitterDistance = Math.Max(rightSplitter.Panel1MinSize, rightSplitter.Width - rightSplitter.Panel2MinSize - rightSplitter.SplitterWidth);
            };

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.White, Padding = new Padding(16, 8, 16, 8), ColumnCount = 3, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _status.AutoSize = false; _status.AutoEllipsis = true; _status.Dock = DockStyle.Fill; _status.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft; _status.Margin = new Padding(0, 0, 12, 0); footer.Controls.Add(_status, 0, 0);
            _progressTrack.Dock = DockStyle.Fill; _progressTrack.Margin = new Padding(0, 22, 12, 22); _progressTrack.BackColor = System.Drawing.Color.Transparent; ApplyRoundedRegion(_progressTrack, 8); _progressTrack.Paint += PaintProgressTrack; footer.Controls.Add(_progressTrack, 1, 0);
            var publishArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            publishArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); publishArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            publishArea.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); publishArea.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _sheetCountText.AutoSize = false; _sheetCountText.AutoEllipsis = true; _sheetCountText.Dock = DockStyle.Fill; _sheetCountText.TextAlign = System.Drawing.ContentAlignment.MiddleRight; _sheetCountText.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); publishArea.Controls.Add(_sheetCountText, 0, 0); publishArea.SetColumnSpan(_sheetCountText, 2);
            _publishProgressText.AutoSize = false; _publishProgressText.Dock = DockStyle.Fill; _publishProgressText.TextAlign = System.Drawing.ContentAlignment.MiddleRight; _publishProgressText.ForeColor = System.Drawing.Color.FromArgb(65, 84, 110); _publishProgressText.Text = "0 / 0"; publishArea.Controls.Add(_publishProgressText, 0, 1);
            var footerPublish = IconAccentButton("发布 PDF", UiIcon.Publish, PublishPdf); footerPublish.Dock = DockStyle.Fill; footerPublish.AutoSize = false; footerPublish.Margin = new Padding(8, 3, 0, 3); publishArea.Controls.Add(footerPublish, 1, 1);
            footer.Controls.Add(publishArea, 2, 0);
            root.Controls.Add(footer, 0, 3);
            ApplyTooltips(this);
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
            if (e.PropertyName == "PublishProgressValue" || e.PropertyName == "PublishProgressMaximum" || e.PropertyName == "IsPublishing" || e.PropertyName == "ScanProgressValue" || e.PropertyName == "ScanProgressMaximum" || e.PropertyName == "IsScanning") RefreshPublishProgress();
        }

        private void RefreshPublishProgress()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshPublishProgress)); return; }
            var maximum = _viewModel.IsScanning
                ? Math.Max(_viewModel.ScanProgressMaximum, 1)
                : _viewModel.IsPublishing ? Math.Max(_viewModel.PublishProgressMaximum, 1) : Math.Max(_viewModel.Sheets.Count, 1);
            var value = _viewModel.IsScanning ? _viewModel.ScanProgressValue : _viewModel.PublishProgressValue;
            value = Math.Min(Math.Max(value, 0), maximum);
            _publishProgressText.Text = value + " / " + maximum;
            _sheetCountText.Text = "共 " + _viewModel.Sheets.Count + " 张图纸";
            _progressTrack.Invalidate();
            _progressTrack.Update();
            _publishProgressText.Refresh();
            _sheetCountText.Refresh();
            _status.Refresh();
        }

        private void PaintProgressTrack(object sender, PaintEventArgs args)
        {
            var bounds = new System.Drawing.Rectangle(0, 2, Math.Max(1, _progressTrack.Width - 1), Math.Max(12, _progressTrack.Height - 5));
            var maximum = _viewModel.IsScanning
                ? Math.Max(_viewModel.ScanProgressMaximum, 1)
                : _viewModel.IsPublishing ? Math.Max(_viewModel.PublishProgressMaximum, 1) : Math.Max(_viewModel.Sheets.Count, 1);
            var value = _viewModel.IsScanning ? _viewModel.ScanProgressValue : _viewModel.PublishProgressValue;
            var ratio = Math.Max(0, Math.Min(1, value / (double)maximum));
            using (var track = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(232, 236, 242)))
            using (var fill = new System.Drawing.SolidBrush(_viewModel.IsScanning ? System.Drawing.Color.FromArgb(64, 139, 220) : System.Drawing.Color.FromArgb(40, 165, 72)))
            using (var border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(205, 213, 224)))
            {
                args.Graphics.FillRectangle(track, bounds);
                if (ratio > 0) args.Graphics.FillRectangle(fill, new System.Drawing.Rectangle(bounds.X, bounds.Y, Math.Max(1, (int)(bounds.Width * ratio)), bounds.Height));
                args.Graphics.DrawRectangle(border, bounds);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(System.Drawing.Rectangle rectangle, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void RefreshAll()
        {
            _refreshing = true;
            try
            {
                _projects.DataSource = _viewModel.Projects.ToList(); _projects.DisplayMember = "Name"; _projects.SelectedItem = _viewModel.SelectedProject;
                _newProjectName.Text = _viewModel.NewProjectName;
                _projectSummary.Text = _viewModel.SelectedProject == null ? "未选择工程" : _viewModel.SelectedProject.Name;
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
                _sheetCountText.Text = "共 " + _viewModel.Sheets.Count + " 张图纸";
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

        private Button ProjectManagerButton()
        {
            var button = new Button
            {
                Width = 38,
                Height = 30,
                Margin = new Padding(2, 2, 2, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.White,
                Image = DrawProjectManagerIcon(),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(157, 181, 214);
            ApplyRoundedRegion(button, 4);
            _toolTip.SetToolTip(button, "项目管理：新建、删除、切换工程，扫描设置、保存参数和保存当前 CAD 到项目文件夹");
            button.Click += (sender, args) => OpenProjectManager();
            return button;
        }

        // Code-drawn folder + gear icon: crisp at the WinForms DPI scaling size
        // and avoids an external bitmap dependency in the AutoCAD plug-in.
        private static System.Drawing.Image DrawProjectManagerIcon()
        {
            var bitmap = new System.Drawing.Bitmap(22, 22);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var folder = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(36, 116, 210)))
            using (var tab = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(89, 157, 232)))
            using (var gear = new System.Drawing.Pen(System.Drawing.Color.FromArgb(23, 67, 122), 2f))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillRectangle(tab, 2, 4, 9, 5);
                graphics.FillRectangle(folder, 2, 7, 15, 10);
                graphics.DrawRectangle(gear, 2, 7, 15, 10);
                graphics.DrawEllipse(gear, 13, 13, 7, 7);
                graphics.DrawLine(gear, 16.5f, 11, 16.5f, 13);
                graphics.DrawLine(gear, 16.5f, 20, 16.5f, 22);
                graphics.DrawLine(gear, 11, 16.5f, 13, 16.5f);
                graphics.DrawLine(gear, 20, 16.5f, 22, 16.5f);
            }
            return bitmap;
        }

        private void OpenProjectManager()
        {
            using (var dialog = new ProjectManagerForm(_viewModel, RefreshAll, ConfigureScanScope))
                dialog.ShowDialog(this);
            RefreshAll();
        }

        private void SaveCurrentCad()
        {
            string destination, error;
            if (_viewModel.SaveCurrentCadToProjectFolder(out destination, out error)) RefreshAll();
            else MessageBox.Show(this, "保存 CAD 失败：\r\n" + error, "工程文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SyncAutoSave()
        {
            var count = _viewModel.SyncAutoSaveFiles();
            MessageBox.Show(this, count == 0 ? "自动保存目录已是最新。" : "已归档 " + count + " 个自动保存文件。", "自动保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void PrintProjectFolder()
        {
            var folder = _viewModel.GetProjectFolder();
            if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder)) { MessageBox.Show(this, "当前工程目录不存在。", "目录打印", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var files = System.IO.Directory.EnumerateFiles(folder, "*.dwg", System.IO.SearchOption.AllDirectories)
                .Where(x => x.IndexOf(System.IO.Path.DirectorySeparatorChar + "自动保存" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (files.Count == 0) { MessageBox.Show(this, "工程目录中没有 DWG 文件。", "目录打印", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            _viewModel.AddCadFiles(files);
            _viewModel.ScanCadFiles(files);
            RefreshAll();
            PublishPdf();
        }

        public void ScanDrawing()
        {
            _viewModel.ScanCommand.Execute(null);
            RefreshAll();
        }

        public void PublishPdf()
        {
            Validate();
            _sheets.CommitEdit(DataGridViewDataErrorContexts.Commit);
            _sheets.EndEdit();
            // CellEndEdit is queued to avoid rebinding the grid during an edit.
            // Commit the pending building change synchronously before creating
            // the publish plan, otherwise the old sub-project is still used.
            _viewModel.ApplySheetEdits();
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

        private void SaveAllCadFiles()
        {
            var result = _viewModel.SaveAllOpenCadFiles();
            MessageBox.Show(this, result, "全部保存", MessageBoxButtons.OK,
                result.IndexOf("失败", StringComparison.Ordinal) >= 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
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

        private enum UiIcon { Plus, Remove, Refresh, List, Select, Gear, Save, SaveAll, Folder, Open, Up, Down, Publish }

        private static Button IconButton(string text, UiIcon icon, Action action)
        {
            var button = Button(text, action);
            button.Image = DrawUiIcon(icon, System.Drawing.Color.FromArgb(40, 115, 205));
            button.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
            button.Padding = new Padding(7, 2, 7, 2);
            return button;
        }

        private static Button IconAccentButton(string text, UiIcon icon, Action action)
        {
            var button = IconButton(text, icon, action);
            button.BackColor = System.Drawing.Color.White;
            button.ForeColor = System.Drawing.Color.FromArgb(25, 54, 99);
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(104, 145, 185);
            button.Image = DrawUiIcon(icon, System.Drawing.Color.FromArgb(40, 115, 205));
            return button;
        }

        private void OpenFrameCreation()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var dialog = new FrameCreationForm(document, RefreshFrames);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
        }

        public void OpenFrameCreationForCommand() => OpenFrameCreation();

        private void OpenCatalogInsert()
        {
            var document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null || _viewModel.Sheets.Count == 0) { MessageBox.Show(this, "请先扫描当前工程的图纸。", "插入目录", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var dialog = new CatalogInsertForm(document, _viewModel.Sheets.ToList(), RefreshAll);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
        }

        public void OpenCatalogInsertForCommand() => OpenCatalogInsert();

        private static System.Drawing.Image DrawUiIcon(UiIcon icon, System.Drawing.Color color)
        {
            var bitmap = new System.Drawing.Bitmap(16, 16);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var pen = new System.Drawing.Pen(color, 1.8F))
            using (var brush = new System.Drawing.SolidBrush(color))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                switch (icon)
                {
                    case UiIcon.Plus: graphics.DrawLine(pen, 8, 3, 8, 13); graphics.DrawLine(pen, 3, 8, 13, 8); break;
                    case UiIcon.Remove: graphics.DrawLine(pen, 3, 8, 13, 8); break;
                    case UiIcon.Refresh: graphics.DrawArc(pen, 2, 2, 11, 11, 35, 285); graphics.FillPolygon(brush, new[] { new System.Drawing.Point(12, 2), new System.Drawing.Point(14, 2), new System.Drawing.Point(13, 5) }); break;
                    case UiIcon.List:
                        for (var row = 0; row < 3; row++) { graphics.FillRectangle(brush, 2, 3 + row * 5, 2, 2); graphics.DrawLine(pen, 6, 4 + row * 5, 14, 4 + row * 5); } break;
                    case UiIcon.Select: graphics.DrawRectangle(pen, 3, 3, 10, 10); graphics.DrawLine(pen, 1, 8, 5, 8); graphics.DrawLine(pen, 11, 8, 15, 8); break;
                    case UiIcon.Gear:
                        graphics.DrawEllipse(pen, 4, 4, 8, 8); graphics.DrawEllipse(pen, 7, 7, 2, 2);
                        for (var angle = 0; angle < 360; angle += 45) { var radians = angle * Math.PI / 180.0; var x1 = 8 + (int)(Math.Cos(radians) * 5); var y1 = 8 + (int)(Math.Sin(radians) * 5); var x2 = 8 + (int)(Math.Cos(radians) * 7); var y2 = 8 + (int)(Math.Sin(radians) * 7); graphics.DrawLine(pen, x1, y1, x2, y2); } break;
                    case UiIcon.Save: graphics.DrawRectangle(pen, 2, 2, 12, 12); graphics.DrawRectangle(pen, 5, 2, 6, 4); graphics.DrawRectangle(pen, 5, 9, 6, 5); break;
                    case UiIcon.SaveAll:
                        graphics.DrawRectangle(pen, 1, 3, 10, 11); graphics.DrawRectangle(pen, 4, 3, 5, 3); graphics.DrawRectangle(pen, 4, 9, 5, 5);
                        graphics.DrawRectangle(pen, 5, 1, 10, 11); graphics.DrawLine(pen, 11, 1, 11, 5); break;
                    case UiIcon.Folder: graphics.DrawLine(pen, 2, 5, 6, 5); graphics.DrawLine(pen, 6, 5, 8, 3); graphics.DrawRectangle(pen, 2, 5, 12, 8); break;
                    case UiIcon.Open: graphics.DrawRectangle(pen, 3, 3, 10, 10); graphics.DrawLine(pen, 8, 8, 14, 2); graphics.DrawLine(pen, 10, 2, 14, 2); graphics.DrawLine(pen, 14, 2, 14, 6); break;
                    case UiIcon.Up: graphics.DrawLine(pen, 8, 13, 8, 3); graphics.DrawLine(pen, 4, 7, 8, 3); graphics.DrawLine(pen, 12, 7, 8, 3); break;
                    case UiIcon.Down: graphics.DrawLine(pen, 8, 3, 8, 13); graphics.DrawLine(pen, 4, 9, 8, 13); graphics.DrawLine(pen, 12, 9, 8, 13); break;
                    case UiIcon.Publish: graphics.DrawRectangle(pen, 3, 2, 10, 12); graphics.DrawLine(pen, 6, 5, 10, 5); graphics.DrawLine(pen, 6, 8, 10, 8); graphics.DrawLine(pen, 6, 11, 9, 11); break;
                }
            }
            return bitmap;
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                Margin = new Padding(3, 0, 3, 4),
                Padding = new Padding(7, 2, 7, 2),
                MinimumSize = new System.Drawing.Size(0, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.FromArgb(31, 48, 74),
                Cursor = Cursors.Hand
            };
            button.Tag = text;
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(190, 201, 216);
            button.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(239, 244, 250);
            button.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(226, 235, 246);
            if (action != null) button.Click += (s, e) => action();
            return button;
        }

        private void ApplyTooltips(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var button = child as Button;
                if (button != null && button.Tag is string label)
                    _toolTip.SetToolTip(button, TooltipFor(label.Trim()));
                if (child.HasChildren) ApplyTooltips(child);
            }
        }

        private static string TooltipFor(string label)
        {
            switch (label)
            {
                case "添加文件": return "把一个或多个 DWG 加入当前工程文件清单。";
                case "移除文件": return "从工程中移除选中的 DWG，并清除它的图纸和空子项目记录；不删除磁盘文件。";
                case "全部保存": return "检查当前工程 CAD 文件列表，只保存其中已经在 AutoCAD 打开并正在编辑的 DWG；工程外图纸不处理。";
                case "扫描当前": return "读取当前激活 DWG 中的已登记图框，更新图纸列表。";
                case "扫描所选": return "批量读取工程列表中已勾选 DWG 的图框和图纸信息。";
                case "框选发布": return "在当前 DWG 中手动框选图框，只发布本次选中的图纸。";
                case "批量改属性": return "框选多个带属性图块，按属性标记预览并批量写入新值。";
                case "拾取登记": return "在当前 CAD 中选择一个已有图框块，登记它的纸张、方向、比例和属性字段。";
                case "创建图框": return "按指定纸张、方向和比例在当前 CAD 中创建标准图框块。";
                case "修改图框": return "修改选中图框登记的纸张、加长尺寸、方向、比例和属性映射。";
                case "删除图框": return "从插件图框库删除选中的登记规则；不删除 CAD 中的实际图框。";
                case "保存图框": return "保存当前图框库，供后续扫描其他 DWG 时继续识别使用。";
                case "插入目录": return "根据当前图纸顺序生成目录表，并插入到 CAD 图纸中。";
                case "存入工程": return "把当前 DWG 的副本保存到当前工程文件夹。";
                case "目录打印": return "扫描当前工程文件夹中的 DWG，然后按工程设置发布 PDF。";
                case "自动保存": return "把 AutoCAD/TArch 的自动保存文件复制到工程的“自动保存”目录作为备份。";
                case "更新预览": return "按当前激活 DWG 和当前布局重新显示图框；布局图框只在对应布局显示，模型空间图框只在模型空间显示。";
                case "发布 PDF": return "按当前勾选的子项目、图纸顺序、纸张和打印样式生成 PDF。";
                case "上移图纸": return "把当前图纸在所属子项目的发布顺序中上移一位。";
                case "下移图纸": return "把当前图纸在所属子项目的发布顺序中下移一位。";
                case "刷新样式": return "重新读取 AutoCAD 当前可用的 CTB/STB 打印样式列表。";
                case "收藏样式": return "把当前打印样式保存到本工程的常用样式列表。";
                case "选择目录": return "选择本次工程 PDF 的根输出目录。";
                case "打开目录": return "在 Windows 资源管理器中打开当前 PDF 输出目录。";
                case "全选项目": return "勾选所有子项目，使其全部参与本次 PDF 发布。";
                case "取消全选": return "取消所有子项目的发布勾选。";
                default: return label;
            }
        }

        private static Button AccentButton(string text, Action action)
        {
            var button = Button(text, action);
            button.BackColor = System.Drawing.Color.White;
            button.ForeColor = System.Drawing.Color.FromArgb(25, 54, 99);
            button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(104, 145, 185);
            return button;
        }

        private static void ApplyInputStyle(Control control)
        {
            control.AutoSize = false;
            control.Height = 30;
            control.Margin = new Padding(0, 0, 8, 4);
            var combo = control as ComboBox;
            if (combo != null) combo.IntegralHeight = false;
        }

        private static void AddHeightDragIndicator(SplitContainer splitter)
        {
            splitter.BackColor = System.Drawing.Color.FromArgb(248, 250, 253);
            splitter.Paint += (sender, args) =>
            {
                var y = splitter.Panel1.Height;
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, 195, 214)))
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(56, 105, 168)))
                using (var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(248, 250, 253)))
                using (var font = new System.Drawing.Font("Microsoft YaHei UI", 8F))
                {
                    args.Graphics.DrawLine(pen, 8, y + splitter.SplitterWidth / 2, splitter.Width - 8, y + splitter.SplitterWidth / 2);
                    var caption = "↕  拖动调整高度  ↕";
                    var size = args.Graphics.MeasureString(caption, font);
                    var x = Math.Max(8, (splitter.Width - size.Width) / 2);
                    args.Graphics.FillRectangle(background, x - 4, y + 5, size.Width + 8, size.Height + 2);
                    args.Graphics.DrawString(caption, font, brush, x, y + 5);
                }
            };
        }

        private static TableLayoutPanel Card(int rows, Padding padding)
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = rows,
                ColumnCount = 1,
                Padding = padding,
                BackColor = System.Drawing.Color.White,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None
                , BorderStyle = BorderStyle.FixedSingle
            };
            return card;
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

        private static Label SectionHeader(string text)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                Height = 30,
                Width = 260,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 8, 0),
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.FromArgb(31, 48, 74),
                BackColor = System.Drawing.Color.FromArgb(231, 237, 246),
                Margin = Padding.Empty
            };
            return label;
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
            // Intentionally left square: the publisher uses a compact, native
            // WinForms layout and avoids clipped corners at different DPI scales.
        }
    }
}
