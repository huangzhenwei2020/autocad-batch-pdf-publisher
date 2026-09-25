using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// Native WinForms host for AutoCAD 2022.  A populated WPF DataGrid in a
    /// modeless AutoCAD window can terminate acad.exe inside the WPF message
    /// pump on some 2022 installations.  WinForms uses AutoCAD's established
    /// modeless-dialog integration and leaves the publishing model unchanged.
    /// </summary>
    public sealed class PublisherForm : DpiAwareForm
    {
        // Flat, opaque colours keep painting cheap inside AutoCAD while matching
        // the launcher.  Do not use transparency or per-frame gradients here:
        // this window regularly displays hundreds of drawings.
        private static readonly System.Drawing.Color Canvas = System.Drawing.Color.FromArgb(14, 25, 35);
        private static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(20, 35, 47);
        private static readonly System.Drawing.Color SurfaceRaised = System.Drawing.Color.FromArgb(26, 45, 60);
        private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(45, 69, 88);
        private static readonly System.Drawing.Color TextPrimary = System.Drawing.Color.FromArgb(232, 240, 247);
        private static readonly System.Drawing.Color TextSecondary = System.Drawing.Color.FromArgb(145, 165, 182);
        private static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(19, 151, 255);
        private static readonly System.Drawing.Color AccentHover = System.Drawing.Color.FromArgb(45, 166, 255);
        private static readonly System.Drawing.Color AccentPressed = System.Drawing.Color.FromArgb(0, 118, 214);
        private static readonly System.Drawing.Color Success = System.Drawing.Color.FromArgb(77, 205, 108);
        private static readonly System.Drawing.Color Warning = System.Drawing.Color.FromArgb(255, 151, 32);
        private static readonly System.Drawing.Color Danger = System.Drawing.Color.FromArgb(255, 73, 73);
        private const int StandardControlHeight = 36;
        private const int ResizeBorderThickness = 1;
        private readonly PublisherViewModel _viewModel = new PublisherViewModel();
        private readonly ThemedComboBox _projects = new ThemedComboBox();
        private readonly TextBox _newProjectName = new TextBox();
        private readonly Label _projectSummary = new Label();
        private readonly ListBox _buildings = new SmoothListBox();
        private readonly ThemedCheckedListBox _cadFiles = new ThemedCheckedListBox();
        private readonly DataGridView _sheets = new BufferedDataGridView();
        private readonly BindingList<SheetItem> _sheetRows = new BindingList<SheetItem>();
        private readonly BindingSource _sheetSource = new BindingSource();
        private readonly ThemedComboBox _sheetSort = new ThemedComboBox();
        private readonly TextBox _sheetSearch = new TextBox();
        private readonly Label _sheetCount = new Label();
        private readonly ThemedComboBox _plotStyle = new ThemedComboBox();
        private readonly ThemedComboBox _marginMode = new ThemedComboBox();
        private readonly TextBox _outputDirectory = new TextBox();
        private readonly Label _actualOutputDirectories = new Label();
        private readonly ThemedCheckedListBox _publishBuildings = new ThemedCheckedListBox();
        private readonly ToggleSwitch _outputNextToCad = new ToggleSwitch();
        private readonly ToggleSwitch _includeProjectName = new ToggleSwitch();
        private readonly ToggleSwitch _includeBuildingName = new ToggleSwitch();
        private readonly ToggleSwitch _overwriteExisting = new ToggleSwitch();
        private readonly ToggleSwitch _mergeByBuilding = new ToggleSwitch();
        private readonly ToggleSwitch _previewEnabled = new ToggleSwitch();
        private readonly Label _status = new Label();
        private readonly Panel _progressTrack = new Panel();
        private readonly Label _publishProgressText = new Label();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly DwgSheetPreviewPanel _sheetPreview = new DwgSheetPreviewPanel();
        private readonly Label _previewTitle = new Label();
        private readonly Label _previewPosition = new Label();
        private readonly Label _previewPaper = new Label();
        private readonly Label _previewDirection = new Label();
        private readonly Label _previewScale = new Label();
        private readonly Label _previewFrame = new Label();
        private readonly Label _previewNotice = new Label();
        private readonly Label _quickFrameValue = new Label();
        private readonly Label _quickPaperValue = new Label();
        private readonly ThemedComboBox _quickOrientation = new ThemedComboBox();
        private readonly ThemedComboBox _quickScale = new ThemedComboBox();
        private Button _refreshPreviewButton;
        private Button _refreshAllPreviewsButton;
        private DarkGridScrollBar _horizontalGridScrollBar;
        private DarkGridScrollBar _verticalGridScrollBar;
        private ToolStripDropDown _gridChoicePopup;
        private bool _updatingAllPreviews;
        private bool _updatingQuickSheetSettings;
        private bool _refreshing;
        private bool _gridCommitPending;
        private SplitContainer _leftSplitter;
        private SplitContainer _rightSplitter;
        private int _savedLeftPanelWidth = 230;
        private int _savedRightPanelWidth = 420;
        private readonly List<string> _savedSheetColumnOrder = new List<string>();
        private SheetItem _dragCandidate;
        private System.Drawing.Point _dragStart;
        private int _dragTargetRowIndex = -1;
        private bool _dragInsertAfter;
        private int _printPreviewRequestVersion;

        public PublisherForm()
        {
            Text = "万落建筑工具 · 批量 PDF 发布  " + WanluoArchitectureTools.ProductVersion.Display;
            Width = 1460;
            Height = 900;
            MinimumSize = new System.Drawing.Size(1080, 680);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            // The one-pixel frame is visual only. WndProc supplies the resize
            // hit targets without asking Windows to add another non-client frame.
            Padding = new Padding(1);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);

            LoadUiLayoutSettings();
            BuildInterface();
            WireEvents();
            RefreshAll();
        }

        private void BuildInterface()
        {
            BackColor = Border;
            ApplyInputStyle(_projects);
            ApplyInputStyle(_newProjectName);
            ApplyInputStyle(_sheetSearch);
            ApplyInputStyle(_plotStyle);
            ApplyInputStyle(_marginMode);
            ApplyInputStyle(_outputDirectory);
            ApplyInputStyle(_quickOrientation);
            ApplyInputStyle(_quickScale);

            var root = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 4, ColumnCount = 1 };
            root.BackColor = Canvas;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            Controls.Add(root);

            var titleBar = BuildWindowTitleBar();
            root.Controls.Add(titleBar, 0, 0);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 8, 14, 8), BackColor = Surface };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var brand = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
            brand.Controls.Add(new Label { Text = "图纸审校台", ForeColor = TextPrimary, Font = new System.Drawing.Font(Font.FontFamily, 16F, System.Drawing.FontStyle.Bold), AutoSize = true, Location = new System.Drawing.Point(8, 5) });
            brand.Controls.Add(new Label { Text = WanluoArchitectureTools.ProductVersion.Display, ForeColor = TextSecondary, Font = new System.Drawing.Font(Font.FontFamily, 9F), AutoSize = true, Location = new System.Drawing.Point(144, 14) });
            header.Controls.Add(brand, 0, 0);
            var projectBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), BackColor = Surface, WrapContents = false, Margin = Padding.Empty };
            projectBar.Controls.Add(new Label { Text = "当前项目", ForeColor = TextPrimary, AutoSize = false, Width = 74, Height = StandardControlHeight, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = Padding.Empty });
            _projects.Width = 205; _projects.Height = StandardControlHeight; _projects.DropDownStyle = ComboBoxStyle.DropDownList; _projects.Margin = new Padding(0, 0, 8, 0);
            projectBar.Controls.Add(_projects);
            projectBar.Controls.Add(ToolbarButton("项目管理", UiIcon.Gear, OpenProjectManager));
            projectBar.Controls.Add(ToolbarButton("图框登记", UiIcon.Frame, OpenFrameManager));
            projectBar.Controls.Add(ToolbarButton("插入目录", UiIcon.List, OpenCatalogInsert));
            projectBar.Controls.Add(ToolbarButton("存入工程", UiIcon.Save, SaveCurrentCad));
            projectBar.Controls.Add(ToolbarButton("目录打印", UiIcon.Publish, PrintProjectFolder));
            projectBar.SizeChanged += (sender, args) =>
                _projects.Width = Math.Max(190, Math.Min(245, projectBar.ClientSize.Width - 74 - 8 - 5 * 106 - 6));
            header.Controls.Add(projectBar, 1, 0);
            root.Controls.Add(header, 0, 1);

            var body = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Canvas };
            root.Controls.Add(body, 0, 2);

            var leftSplitter = _leftSplitter = new BufferedSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                IsSplitterFixed = false,
                SplitterWidth = 8
            };
            body.Controls.Add(leftSplitter);

            var rightSplitter = _rightSplitter = new BufferedSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                IsSplitterFixed = false,
                SplitterWidth = 8
            };
            leftSplitter.Panel2.Controls.Add(rightSplitter);

            // The left rail has two independently resizable cards. Frame
            // registration is managed in its own window from the top toolbar.
            var cadBuildingSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 8, IsSplitterFixed = false, FixedPanel = FixedPanel.None };

            var cadPane = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 3, ColumnCount = 1, BackColor = Surface };
            cadPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); cadPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); cadPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            cadPane.Controls.Add(SectionHeader("CAD 文件"), 0, 0);
            _cadFiles.CheckOnClick = true;
            _cadFiles.HorizontalScrollbar = false;
            _cadFiles.CustomHorizontalScroll = true;
            var cadListHost = ThemedListHost(_cadFiles, 0);
            cadListHost.Dock = DockStyle.Fill;
            cadListHost.Margin = new Padding(8, 7, 8, 4);
            cadPane.Controls.Add(cadListHost, 0, 1);
            var cadButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(5, 0, 5, 5), Padding = Padding.Empty, BackColor = Surface };
            cadButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cadButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (var row = 0; row < 3; row++) cadButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
            AddCadAction(cadButtons, IconButton("添加文件", UiIcon.Plus, ChooseCadFiles), 0, 0);
            AddCadAction(cadButtons, IconButton("移除文件", UiIcon.Remove, RemoveCadFile), 1, 0);
            AddCadAction(cadButtons, IconButton("全部保存", UiIcon.SaveAll, SaveAllCadFiles), 0, 1);
            AddCadAction(cadButtons, IconAccentButton("扫描当前", UiIcon.Refresh, () => { _viewModel.ScanCommand.Execute(null); RefreshAll(); }), 1, 1);
            AddCadAction(cadButtons, IconAccentButton("扫描所选", UiIcon.List, ScanCheckedCadFiles), 0, 2);
            AddCadAction(cadButtons, IconButton("框选发布", UiIcon.Select, OpenCurrentSelectionPublisher), 1, 2);
            cadPane.Controls.Add(cadButtons, 0, 2);
            var cadCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Surface, BorderColor = Border, CornerRadius = 6 };
            cadCard.Controls.Add(cadPane);
            cadBuildingSplit.Panel1.Controls.Add(cadCard);

            var buildingPane = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, RowCount = 2, ColumnCount = 1, BackColor = Surface };
            buildingPane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); buildingPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            buildingPane.Controls.Add(SectionHeader("子项目"), 0, 0);
            var buildingListHost = ThemedListHost(_buildings, 0);
            buildingListHost.Dock = DockStyle.Fill;
            buildingListHost.Margin = new Padding(8, 7, 8, 4);
            buildingPane.Controls.Add(buildingListHost, 0, 1);
            var buildingCard = new RoundedPanel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Surface, BorderColor = Border, CornerRadius = 6 };
            buildingCard.Controls.Add(buildingPane);
            cadBuildingSplit.Panel2.Controls.Add(buildingCard);
            var leftRail = new BufferedPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, BackColor = Canvas };
            leftRail.Controls.Add(cadBuildingSplit);
            leftSplitter.Panel1.Controls.Add(leftRail);

            var center = new RoundedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8), BackColor = Surface, BorderColor = Border, CornerRadius = 6, Margin = Padding.Empty };
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var sheetHeader = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Surface, Padding = new Padding(10, 4, 10, 6), Margin = Padding.Empty };
            sheetHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); sheetHeader.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var titleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Surface, Margin = Padding.Empty };
            titleRow.Controls.Add(new Label { Text = "图纸列表", AutoSize = false, Width = 96, Height = 32, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Font = new System.Drawing.Font(Font.FontFamily, 11F, System.Drawing.FontStyle.Bold), ForeColor = TextPrimary, Margin = Padding.Empty });
            _sheetCount.AutoSize = true; _sheetCount.ForeColor = TextSecondary; _sheetCount.Margin = new Padding(0, 8, 14, 0); titleRow.Controls.Add(_sheetCount);
            titleRow.Controls.Add(new Label { Text = "拖动行可调整发布顺序", AutoSize = true, ForeColor = TextSecondary, Margin = new Padding(4, 8, 0, 0) });
            _previewEnabled.Text = "在 CAD 中显示当前子项目图框范围";
            _previewEnabled.AutoSize = false;
            _previewEnabled.Width = 330;
            _previewEnabled.Height = 32;
            _previewEnabled.ForeColor = TextPrimary;
            _previewEnabled.Margin = new Padding(18, 0, 0, 0);
            titleRow.Controls.Add(_previewEnabled);
            sheetHeader.Controls.Add(titleRow, 0, 0);
            var sheetTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Surface, Margin = Padding.Empty, Padding = Padding.Empty };
            sheetTools.Controls.Add(ThemedTextInputHost(_sheetSearch, 210, new Padding(0, 0, 8, 0), "搜索图号、图名或关键词..."));
            _sheetSort.DropDownStyle = ComboBoxStyle.DropDownList;
            _sheetSort.Width = 116;
            _sheetSort.Height = StandardControlHeight;
            _sheetSort.Margin = new Padding(0, 0, 8, 0);
            _sheetSort.Items.AddRange(new object[] { "自定义顺序", "图号升序", "图号降序", "图名升序", "图名降序", "按 CAD 文件" });
            _sheetSort.SelectedIndex = 0;
            sheetTools.Controls.Add(_sheetSort);
            _refreshPreviewButton = IconButton("更新预览", UiIcon.Refresh, async () => await GenerateSelectedPrintPreviewAsync());
            _refreshAllPreviewsButton = IconButton("全部预览", UiIcon.List, async () => await GenerateAllPrintPreviewsAsync());
            sheetTools.Controls.Add(_refreshPreviewButton);
            sheetTools.Controls.Add(_refreshAllPreviewsButton);
            sheetTools.Controls.Add(IconButton("上移", UiIcon.Up, () => { _viewModel.MoveUpCommand.Execute(null); RefreshSheets(); }));
            sheetTools.Controls.Add(IconButton("下移", UiIcon.Down, () => { _viewModel.MoveDownCommand.Execute(null); RefreshSheets(); }));
            sheetHeader.Controls.Add(sheetTools, 0, 1);
            center.Controls.Add(sheetHeader, 0, 0);
            ConfigureGrid();
            var gridHost = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Surface };
            gridHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            gridHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            gridHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            gridHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            gridHost.Controls.Add(_sheets, 0, 0);
            _horizontalGridScrollBar = new DarkGridScrollBar(_sheets, false)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(6, 4, 6, 4)
            };
            _verticalGridScrollBar = new DarkGridScrollBar(_sheets, true)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 6, 4, 6)
            };
            gridHost.Controls.Add(_horizontalGridScrollBar, 0, 1);
            gridHost.Controls.Add(_verticalGridScrollBar, 1, 0);
            gridHost.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty }, 1, 1);
            center.Controls.Add(gridHost, 0, 1);
            rightSplitter.Panel1.Controls.Add(center);

            var reviewShell = new RoundedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(6), BackColor = Surface, BorderColor = Border, CornerRadius = 6, Margin = Padding.Empty };
            reviewShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); reviewShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var reviewTabs = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2, BackColor = SurfaceRaised, Padding = new Padding(4), Margin = Padding.Empty };
            ApplyRoundedRegion(reviewTabs, 8);
            reviewTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); reviewTabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var previewTab = SegmentButton("图纸预览", true);
            var settingsTab = SegmentButton("输出设置", false);
            reviewTabs.Controls.Add(previewTab, 0, 0); reviewTabs.Controls.Add(settingsTab, 1, 0);
            reviewShell.Controls.Add(reviewTabs, 0, 0);

            var previewPage = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = Surface, Padding = new Padding(10), Margin = Padding.Empty };
            previewPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            previewPage.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            previewPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            previewPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            previewPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            var previewHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, BackColor = Surface };
            previewHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); previewHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            _previewTitle.Dock = DockStyle.Fill; _previewTitle.ForeColor = TextPrimary; _previewTitle.Font = new System.Drawing.Font(Font.FontFamily, 10.5F, System.Drawing.FontStyle.Bold); _previewTitle.TextAlign = System.Drawing.ContentAlignment.MiddleLeft; _previewTitle.AutoEllipsis = true;
            _previewPosition.Dock = DockStyle.Fill; _previewPosition.ForeColor = TextSecondary; _previewPosition.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            previewHeading.Controls.Add(_previewTitle, 0, 0); previewHeading.Controls.Add(_previewPosition, 1, 0);
            previewPage.Controls.Add(previewHeading, 0, 0);
            _sheetPreview.Dock = DockStyle.Fill; _sheetPreview.Margin = new Padding(0, 4, 0, 8); previewPage.Controls.Add(_sheetPreview, 0, 1);
            ApplyRoundedRegion(_sheetPreview, 8);

            var sheetFacts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 6, 0, 4), BackColor = Surface, Margin = Padding.Empty };
            sheetFacts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); sheetFacts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            sheetFacts.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); sheetFacts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            ConfigureFactLabel(_previewPaper); ConfigureFactLabel(_previewDirection); ConfigureFactLabel(_previewScale); ConfigureFactLabel(_previewFrame);
            sheetFacts.Controls.Add(_previewPaper, 0, 0); sheetFacts.Controls.Add(_previewDirection, 1, 0); sheetFacts.Controls.Add(_previewScale, 0, 1); sheetFacts.Controls.Add(_previewFrame, 1, 1);
            previewPage.Controls.Add(sheetFacts, 0, 2);
            _previewNotice.Dock = DockStyle.Fill; _previewNotice.AutoEllipsis = true; _previewNotice.TextAlign = System.Drawing.ContentAlignment.MiddleLeft; _previewNotice.Padding = new Padding(12, 0, 8, 0); _previewNotice.Margin = new Padding(0, 4, 0, 4); _previewNotice.BackColor = System.Drawing.Color.FromArgb(62, 46, 25); _previewNotice.ForeColor = Warning;
            ApplyRoundedRegion(_previewNotice, 7);
            previewPage.Controls.Add(_previewNotice, 0, 3);

            var quickSettings = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 3, BackColor = Surface, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
            quickSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            quickSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            quickSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            quickSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            quickSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            quickSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            quickSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var quickTitle = SectionLabel("快速设置（当前图纸）"); quickTitle.Dock = DockStyle.Fill; quickSettings.Controls.Add(quickTitle, 0, 0); quickSettings.SetColumnSpan(quickTitle, 4);
            quickSettings.Controls.Add(QuickLabel("图框"), 0, 1);
            quickSettings.Controls.Add(QuickLabel("PDF 尺寸"), 1, 1);
            quickSettings.Controls.Add(QuickLabel("方向"), 2, 1);
            quickSettings.Controls.Add(QuickLabel("比例"), 3, 1);
            ConfigureQuickValueLabel(_quickFrameValue); quickSettings.Controls.Add(_quickFrameValue, 0, 2);
            ConfigureQuickValueLabel(_quickPaperValue); quickSettings.Controls.Add(_quickPaperValue, 1, 2);
            _quickOrientation.DropDownStyle = ComboBoxStyle.DropDownList; _quickOrientation.Items.AddRange(new object[] { "横向", "纵向" });
            _quickOrientation.FlatStyle = FlatStyle.Flat; _quickOrientation.BackColor = SurfaceRaised; _quickOrientation.ForeColor = TextPrimary;
            ((ThemedComboBox)_quickOrientation).SquareCorners = false;
            _quickOrientation.Dock = DockStyle.Fill; _quickOrientation.Margin = new Padding(3);
            quickSettings.Controls.Add(_quickOrientation, 2, 2);
            _quickScale.DropDownStyle = ComboBoxStyle.DropDown;
            _quickScale.Items.AddRange(new object[] { "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:50", "1:75", "1:100", "1:150", "1:200", "1:500" });
            ((ThemedComboBox)_quickScale).SquareCorners = false;
            _quickScale.Dock = DockStyle.Fill; _quickScale.Margin = new Padding(3);
            quickSettings.Controls.Add(_quickScale, 3, 2);
            previewPage.Controls.Add(quickSettings, 0, 4);

            var rightContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 20, ColumnCount = 1, Padding = new Padding(12, 10, 12, 0), BackColor = Surface, Margin = Padding.Empty };
            var right = new DarkScrollHost(rightContent) { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
            ApplyRoundedRegion(right, 8);
            rightContent.Controls.Add(SectionHeader("输出设置")); rightContent.Controls.Add(Label("CAD 打印样式"));
            _plotStyle.DropDownStyle = ComboBoxStyle.DropDown;
            ((ThemedComboBox)_plotStyle).SquareCorners = false;
            _plotStyle.Dock = DockStyle.Top;
            rightContent.Controls.Add(_plotStyle);
            var plotButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            plotButtons.Controls.Add(IconButton("刷新样式", UiIcon.Refresh, () => { _viewModel.RefreshPlotStylesCommand.Execute(null); RefreshPlotStyles(); }));
            plotButtons.Controls.Add(IconButton("收藏样式", UiIcon.Save, () => { _viewModel.SaveFavoritePlotStyleCommand.Execute(null); RefreshPlotStyles(); }));
            rightContent.Controls.Add(plotButtons); rightContent.Controls.Add(Label("白边 / 出血位（单位：mm）"));
            _marginMode.Items.AddRange(new object[] { "自动适配", "无白边（满幅）", "保留 3 mm 白边" });
            _marginMode.Dock = DockStyle.Top; rightContent.Controls.Add(_marginMode); rightContent.Controls.Add(Label("输出目录"));
            var outputFolder = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            outputFolder.Controls.Add(ThemedTextInputHost(_outputDirectory, 144, new Padding(0, 0, 8, 4)));
            outputFolder.Controls.Add(IconButton("选择", UiIcon.Folder, ChooseOutputDirectory)); outputFolder.Controls.Add(IconButton("打开", UiIcon.Open, OpenOutputDirectory));
            rightContent.Controls.Add(outputFolder);
            _outputNextToCad.Text = "输出到各 CAD 文件同级目录"; rightContent.Controls.Add(_outputNextToCad);
            _actualOutputDirectories.AutoSize = true;
            _actualOutputDirectories.MaximumSize = new System.Drawing.Size(310, 44);
            _actualOutputDirectories.ForeColor = TextSecondary;
            _actualOutputDirectories.Padding = new Padding(3, 1, 3, 3);
            rightContent.Controls.Add(_actualOutputDirectories);
            _mergeByBuilding.Text = "每个子项目生成一个 PDF"; _mergeByBuilding.Margin = new Padding(3, 6, 3, 3); rightContent.Controls.Add(_mergeByBuilding);
            var publishBuildingHeader = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
            publishBuildingHeader.Controls.Add(SectionLabel("发布子项目（可多选）"));
            publishBuildingHeader.Controls.Add(IconButton("全选", UiIcon.List, () => SetAllPublishBuildings(true)));
            publishBuildingHeader.Controls.Add(IconButton("清空", UiIcon.Remove, () => SetAllPublishBuildings(false)));
            rightContent.Controls.Add(publishBuildingHeader);
            _publishBuildings.CheckOnClick = true; _publishBuildings.Height = 82; _publishBuildings.Dock = DockStyle.Fill;
            rightContent.Controls.Add(ThemedListHost(_publishBuildings, 82));
            rightContent.Controls.Add(SectionLabel("PDF 文件命名"));
            _includeProjectName.Text = "文件名包含工程名"; rightContent.Controls.Add(_includeProjectName);
            _includeBuildingName.Text = "文件名包含子项目名"; rightContent.Controls.Add(_includeBuildingName);
            _overwriteExisting.Text = "同名 PDF 直接覆盖"; rightContent.Controls.Add(_overwriteExisting);
            var reviewContent = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty };
            reviewContent.Controls.Add(right);
            reviewContent.Controls.Add(previewPage);
            reviewShell.Controls.Add(reviewContent, 0, 1);
            Action<bool> showPreview = show =>
            {
                try
                {
                    reviewContent.SuspendLayout();
                    previewPage.Visible = show;
                    right.Visible = !show;
                    SetSegmentSelection(previewTab, settingsTab, show);
                    if (show) RefreshSelectedSheetReview();
                    reviewContent.ResumeLayout(true);
                }
                catch (Exception exception)
                {
                    reviewContent.ResumeLayout(true);
                    PdfPublisherService.WritePublishDiagnostic("切换批量打印右侧页签失败" + Environment.NewLine + exception + Environment.NewLine);
                    MessageBox.Show(this, "切换面板失败，详细原因已写入日志：" + exception.Message, "批量 PDF 发布", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            previewTab.Click += (sender, args) => showPreview(true);
            settingsTab.Click += (sender, args) => showPreview(false);
            showPreview(true);
            rightSplitter.Panel2.Controls.Add(reviewShell);
            ConfigureSmoothSplitter(leftSplitter);
            ConfigureSmoothSplitter(rightSplitter);
            ConfigureSmoothSplitter(cadBuildingSplit);

            Shown += (sender, args) =>
            {
                var desiredLeft = Clamp(_savedLeftPanelWidth, 210, 280);
                SetSplitterLayoutSafe(leftSplitter, desiredLeft, 198, 760);
                SetSplitterLayoutSafe(
                    cadBuildingSplit,
                    Math.Min(390, Math.Max(280, cadBuildingSplit.Height * 47 / 100)),
                    240,
                    170);

                var desiredRightWidth = Math.Max(390, _savedRightPanelWidth);
                var desiredRightDistance = rightSplitter.Width - desiredRightWidth - rightSplitter.SplitterWidth;
                SetSplitterLayoutSafe(rightSplitter, desiredRightDistance, 480, 350);
                RefreshSelectedSheetReview();
            };

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(10, 8, 10, 8), ColumnCount = 6, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var collapseSource = IconButton("收起侧栏", UiIcon.List, null); collapseSource.Dock = DockStyle.Fill; collapseSource.AutoSize = false; collapseSource.Margin = new Padding(0, 3, 8, 3);
            collapseSource.Click += (sender, args) => { leftSplitter.Panel1Collapsed = !leftSplitter.Panel1Collapsed; collapseSource.Text = leftSplitter.Panel1Collapsed ? "展开侧栏" : "收起侧栏"; };
            footer.Controls.Add(collapseSource, 0, 0);
            _status.AutoSize = false; _status.AutoEllipsis = true; _status.Dock = DockStyle.Fill; _status.ForeColor = TextSecondary; _status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft; _status.Margin = new Padding(0, 0, 12, 0); footer.Controls.Add(_status, 1, 0);
            footer.Controls.Add(new Label { Text = "发布进度", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, ForeColor = TextPrimary, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = Padding.Empty }, 2, 0);
            _progressTrack.Dock = DockStyle.Fill; _progressTrack.Margin = new Padding(0, 19, 12, 19); _progressTrack.BackColor = System.Drawing.Color.Transparent; _progressTrack.Paint += PaintProgressTrack; footer.Controls.Add(_progressTrack, 3, 0);
            _publishProgressText.AutoSize = false; _publishProgressText.Dock = DockStyle.Fill; _publishProgressText.TextAlign = System.Drawing.ContentAlignment.MiddleCenter; _publishProgressText.ForeColor = TextSecondary; _publishProgressText.Text = "已准备 0 / 0"; _publishProgressText.Margin = Padding.Empty; footer.Controls.Add(_publishProgressText, 4, 0);
            var footerPublish = PrimaryButton("发布 PDF", UiIcon.Publish, PublishPdf); footerPublish.Dock = DockStyle.Fill; footerPublish.AutoSize = false; footerPublish.Margin = new Padding(4, 4, 0, 4); footer.Controls.Add(footerPublish, 5, 0);
            root.Controls.Add(footer, 0, 3);
            ApplyDarkControlStyles(this);
            StyleInnerSplitter(cadBuildingSplit);
            ApplyTooltips(this);
        }

        private Control BuildWindowTitleBar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 5,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = System.Drawing.Color.FromArgb(27, 30, 40)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));

            var appMark = CadBrandIcon.CreateTitleMark();
            var caption = new Label
            {
                Text = Text,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(0, 0, 0, 1),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = TextSecondary,
                AutoEllipsis = true
            };
            var minimize = WindowButton("−", () => WindowState = FormWindowState.Minimized);
            var maximize = WindowButton("□", null);
            var close = WindowButton("×", Close, true);
            maximize.Click += (sender, args) =>
            {
                ToggleMaximized();
                maximize.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
            };

            MouseEventHandler beginDrag = (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture();
                SendMessage(Handle, WmNcLeftButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
            };
            EventHandler toggleMaximized = (sender, args) =>
            {
                ToggleMaximized();
                maximize.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
            };
            bar.MouseDown += beginDrag;
            caption.MouseDown += beginDrag;
            appMark.MouseDown += beginDrag;
            bar.DoubleClick += toggleMaximized;
            caption.DoubleClick += toggleMaximized;

            bar.Controls.Add(appMark, 0, 0);
            bar.Controls.Add(caption, 1, 0);
            bar.Controls.Add(minimize, 2, 0);
            bar.Controls.Add(maximize, 3, 0);
            bar.Controls.Add(close, 4, 0);
            return bar;
        }

        private static Button WindowButton(string text, Action action, bool closeButton = false)
        {
            var button = new WindowChromeButton(closeButton)
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = System.Drawing.Color.FromArgb(27, 30, 40),
                ForeColor = TextSecondary,
                Font = new System.Drawing.Font("Segoe UI Symbol", 10F),
                TabStop = false,
                UseVisualStyleBackColor = false
            };
            if (action != null) button.Click += (sender, args) => action();
            return button;
        }

        private void ToggleMaximized()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                return;
            }
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        private static void ConfigureFactLabel(Label label)
        {
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextSecondary;
            label.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.Margin = new Padding(2, 0, 4, 0);
        }

        private static Label QuickLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.BottomLeft, ForeColor = TextSecondary, Margin = new Padding(3, 0, 3, 2) };
        }

        private static void ConfigureQuickValueLabel(Label label)
        {
            label.Dock = DockStyle.Fill;
            label.ForeColor = TextPrimary;
            label.BackColor = Surface;
            label.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            label.Padding = new Padding(6, 0, 3, 0);
            label.Margin = new Padding(3);
            label.AutoEllipsis = true;
        }

        private static Control ThemedListHost(ListBox list, int height)
        {
            var host = new RoundedListHost(list)
            {
                Dock = DockStyle.Top,
                Margin = new Padding(0, 3, 0, 5),
                BackColor = Surface,
                Tag = "themed-list-host"
            };
            if (height > 0) host.Height = height;
            list.BorderStyle = BorderStyle.None;
            list.Margin = Padding.Empty;
            return host;
        }

        private static Control ThemedTextInputHost(TextBox input, int width, Padding margin, string placeholder = null)
        {
            return new RoundedTextInputHost(input, placeholder)
            {
                AutoSize = false,
                Width = width,
                Height = StandardControlHeight,
                MinimumSize = new System.Drawing.Size(0, StandardControlHeight),
                BackColor = SurfaceRaised,
                Margin = margin,
                Tag = "themed-text-input"
            };
        }

        private void ConfigureGrid()
        {
            _sheets.Dock = DockStyle.Fill; _sheets.AutoGenerateColumns = false; _sheets.AllowUserToAddRows = false;
            _sheets.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _sheets.MultiSelect = false;
            _sheets.AllowDrop = true;
            _sheets.AllowUserToOrderColumns = true;
            _sheets.BorderStyle = BorderStyle.None; _sheets.BackgroundColor = Surface;
            _sheets.ScrollBars = ScrollBars.None;
            _sheets.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _sheets.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _sheets.AllowUserToResizeRows = false;
            _sheets.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _sheets.ShowCellToolTips = false;
            _sheets.EnableHeadersVisualStyles = false; _sheets.ColumnHeadersHeight = 36; _sheets.RowTemplate.Height = 31;
            _sheets.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            _sheets.ColumnHeadersDefaultCellStyle.BackColor = SurfaceRaised;
            _sheets.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            _sheets.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold);
            _sheets.DefaultCellStyle.BackColor = Surface;
            _sheets.DefaultCellStyle.ForeColor = TextPrimary;
            _sheets.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(18, 91, 153);
            _sheets.DefaultCellStyle.SelectionForeColor = TextPrimary;
            _sheets.AlternatingRowsDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(25, 37, 49);
            _sheets.GridColor = Border;
            _sheets.RowHeadersVisible = false;
            AddColumn("Order", "#", 38, true);
            _sheets.Columns.Add(new DataGridViewTextBoxColumn { Name = "ReviewStatusColumn", HeaderText = "状态", Width = 86, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            AddComboColumn("Building", "子项目", 88, _viewModel.Buildings.Concat(new[] { "未分组" }));
            AddColumn("SheetNumber", "图号", 90, false); AddColumn("SheetName", "图名", 150, false);
            AddColumn("FrameDisplay", "图框", 78, true);
            AddColumn("OutputPaperSize", "PDF 尺寸", 112, true);
            AddComboColumn("PaperOrientation", "方向", 68, new[] { "横向", "纵向" });
            AddColumn("PrintScale", "打印比例", 82, false);
            AddComboColumn("PlotStyle", "打印样式", 150, PlotStyleChoices());
            AddColumn("SourceFileName", "CAD 文件", 128, true);
            AddColumn("SourceFile", "来源文件", 180, true);
            AddColumn("SourceLayout", "空间", 96, true);
            _sheetSource.DataSource = _sheetRows;
            _sheets.DataSource = _sheetSource;
            _sheets.DataError += (s, e) => e.ThrowException = false;
            foreach (DataGridViewColumn column in _sheets.Columns)
                column.SortMode = column.DataPropertyName == "SheetNumber" || column.DataPropertyName == "SheetName" || column.DataPropertyName == "SourceFileName"
                    ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable;
            RestoreSheetColumnOrder();
            var reviewStatusColumn = _sheets.Columns["ReviewStatusColumn"];
            if (reviewStatusColumn != null) reviewStatusColumn.DisplayIndex = Math.Min(1, _sheets.Columns.Count - 1);
        }

        private void AddColumn(string property, string title, int width, bool readOnly)
        {
            _sheets.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width, ReadOnly = readOnly });
        }

        private void AddComboColumn(string property, string title, int width, IEnumerable<string> items)
        {
            var column = new ThemedGridChoiceColumn
            {
                Name = property + "Column",
                DataPropertyName = property,
                HeaderText = title,
                Width = width,
                ReadOnly = true
            };
            column.SetChoices(items);
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
            _projects.SelectedIndexChanged += (s, e) =>
            {
                if (_refreshing) return;
                _refreshing = true;
                try { _viewModel.SelectedProject = _projects.SelectedItem as ProjectProfile; }
                finally { _refreshing = false; }
                RefreshAll();
            };
            _buildings.SelectedIndexChanged += (s, e) => { if (!_refreshing) _viewModel.SelectedBuilding = _buildings.SelectedItem as string; };
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
            _sheets.SelectionChanged += (s, e) =>
            {
                if (_refreshing) return;
                _printPreviewRequestVersion++;
                _viewModel.SelectedSheet = CurrentSheet();
                RefreshSelectedSheetReview();
            };
            _sheetPreview.DoubleClick += (s, e) => OpenLargePrintPreview();
            _sheetSearch.TextChanged += (s, e) => { if (!_refreshing) RefreshSheets(); };
            _sheetSort.SelectedIndexChanged += (s, e) =>
            {
                if (_refreshing || _sheetSort.SelectedIndex <= 0) return;
                ApplySheetSort(_sheetSort.SelectedIndex);
            };
            _sheets.ColumnHeaderMouseClick += (s, e) =>
            {
                var property = _sheets.Columns[e.ColumnIndex].DataPropertyName;
                if (property == "SheetNumber") SetSheetSort(1);
                else if (property == "SheetName") SetSheetSort(3);
                else if (property == "SourceFileName") SetSheetSort(5);
            };
            _sheets.MouseDown += SheetsMouseDown;
            _sheets.MouseMove += SheetsMouseMove;
            _sheets.DragOver += SheetsDragOver;
            _sheets.DragDrop += SheetsDragDrop;
            _sheets.DragLeave += (s, e) => ClearSheetDragIndicator();
            _sheets.RowPostPaint += SheetsRowPostPaint;
            _sheets.CellPainting += PaintReviewStatusCell;
            _sheets.CellPainting += PaintGridChoiceCell;
            _sheets.CellClick += ShowGridChoicePopup;
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
            _outputDirectory.TextChanged += (s, e) =>
            {
                if (_refreshing) return;
                _viewModel.OutputDirectory = _outputDirectory.Text;
                RefreshActualOutputDirectories();
            };
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
            _quickOrientation.SelectedIndexChanged += (s, e) => CommitQuickSheetSettings(false);
            _quickScale.Leave += (s, e) => CommitQuickSheetSettings(true);
            _quickScale.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                CommitQuickSheetSettings(true);
            };
            _viewModel.PropertyChanged += ViewModelPropertyChanged;
            _viewModel.Frames.CollectionChanged += (s, e) => RefreshFramesSafely();
            FormClosing += (s, e) =>
            {
                if (_viewModel.IsPublishing && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "PDF 正在发布。为避免中断 CAD 打印和 PDF 合并，请等待任务结束后再关闭窗口。", "正在发布", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            FormClosed += (s, e) =>
            {
                _printPreviewRequestVersion++;
                _viewModel.PropertyChanged -= ViewModelPropertyChanged;
                SaveUiLayoutSettings();
                _viewModel.Dispose();
                _sheetSource.Dispose();
            };
        }

        private void ViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => ViewModelPropertyChanged(sender, e))); } catch (ObjectDisposedException) { }
                return;
            }
            if (_refreshing)
            {
                if (e.PropertyName == "Status") _status.Text = _viewModel.Status;
                return;
            }
            if (e.PropertyName == "Status") _status.Text = _viewModel.Status;
            if (e.PropertyName == "Sheets") RefreshSheets();
            if (e.PropertyName == "SelectedBuilding")
            {
                _refreshing = true;
                try { _buildings.SelectedItem = _viewModel.SelectedBuilding; RefreshSheetsCore(); }
                finally { _refreshing = false; }
            }
            if (e.PropertyName == "SelectedSheet") { SelectCurrentSheetRow(); RefreshSelectedSheetReview(); }
            if (e.PropertyName == "PublishProgressValue" || e.PropertyName == "PublishProgressMaximum" || e.PropertyName == "IsPublishing" || e.PropertyName == "ScanProgressValue" || e.PropertyName == "ScanProgressMaximum" || e.PropertyName == "IsScanning") RefreshPublishProgress();
        }

        private void RefreshFramesSafely()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(RefreshFrames));
                else RefreshFrames();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
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
            if (_viewModel.IsScanning) _publishProgressText.Text = "扫描 " + value + " / " + maximum;
            else if (_viewModel.IsPublishing) _publishProgressText.Text = "发布 " + value + " / " + maximum;
            else
            {
                var ready = _viewModel.Sheets.Count(sheet => sheet.MaxX > sheet.MinX && sheet.MaxY > sheet.MinY);
                _publishProgressText.Text = "已准备 " + ready + " / " + _viewModel.Sheets.Count;
            }
            _progressTrack.Invalidate();
        }

        // 进度条的底色、进度色和描边：进度每更新一次就重绘一次，扫描/发布时一秒几十次，
        // 原来每次都 new 两个笔刷加一支画笔，纯属浪费。三个颜色都是常量，提成静态字段。
        private static readonly System.Drawing.SolidBrush ProgressTrackBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(38, 58, 76));
        private static readonly System.Drawing.SolidBrush ProgressScanBrush = new System.Drawing.SolidBrush(Accent);
        private static readonly System.Drawing.SolidBrush ProgressPublishBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(54, 211, 153));
        private static readonly System.Drawing.Pen ProgressBorderPen = new System.Drawing.Pen(Border);

        private void PaintProgressTrack(object sender, PaintEventArgs args)
        {
            var bounds = new System.Drawing.Rectangle(0, Math.Max(0, (_progressTrack.Height - 5) / 2), Math.Max(1, _progressTrack.Width - 1), 5);
            var maximum = _viewModel.IsScanning
                ? Math.Max(_viewModel.ScanProgressMaximum, 1)
                : _viewModel.IsPublishing ? Math.Max(_viewModel.PublishProgressMaximum, 1) : Math.Max(_viewModel.Sheets.Count, 1);
            var value = _viewModel.IsScanning ? _viewModel.ScanProgressValue
                : _viewModel.IsPublishing ? _viewModel.PublishProgressValue
                : _viewModel.Sheets.Count(sheet => sheet.MaxX > sheet.MinX && sheet.MaxY > sheet.MinY);
            var ratio = Math.Max(0, Math.Min(1, value / (double)maximum));
            var fill = _viewModel.IsScanning ? ProgressScanBrush : ProgressPublishBrush;
            args.Graphics.FillRectangle(ProgressTrackBrush, bounds);
            if (ratio > 0) args.Graphics.FillRectangle(fill, new System.Drawing.Rectangle(bounds.X, bounds.Y, Math.Max(1, (int)(bounds.Width * ratio)), bounds.Height));
            args.Graphics.DrawRectangle(ProgressBorderPen, bounds);
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

        private static void PaintListSelection(System.Drawing.Graphics graphics, System.Drawing.Rectangle bounds,
            System.Drawing.Color background, bool selected)
        {
            using (var fill = new System.Drawing.SolidBrush(background)) graphics.FillRectangle(fill, bounds);
            if (!selected || bounds.Width < 8 || bounds.Height < 8) return;
            var selection = new System.Drawing.Rectangle(bounds.Left + 2, bounds.Top + 1,
                bounds.Width - 4, bounds.Height - 2);
            var state = graphics.Save();
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = RoundedRectangle(selection, 5))
            using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(24, 103, 179)))
            using (var accent = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(73, 213, 255)))
            {
                graphics.FillPath(fill, path);
                graphics.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
                graphics.FillRectangle(accent, selection.Left, selection.Top, 5, selection.Height);
            }
            graphics.Restore(state);
        }

        private void RefreshAll()
        {
            _refreshing = true;
            try
            {
                _projects.DataSource = _viewModel.Projects.ToList(); _projects.DisplayMember = "Name"; _projects.SelectedItem = _viewModel.SelectedProject;
                _newProjectName.Text = _viewModel.NewProjectName;
                _projectSummary.Text = _viewModel.SelectedProject == null ? "未选择工程" : _viewModel.SelectedProject.Name;
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
                RefreshSelectedSheetReview();
            }
            finally { _refreshing = false; }
        }

        private void RefreshFrames()
        {
            RefreshAll();
        }

        private void RefreshSheets()
        {
            _refreshing = true; try { RefreshSheetsCore(); } finally { _refreshing = false; }
        }

        private void RefreshSheetsCore()
        {
            var buildingColumn = _sheets.Columns["BuildingColumn"] as ThemedGridChoiceColumn;
            if (buildingColumn != null)
            {
                var choices = _viewModel.Buildings.Concat(new[] { "未分组" }).Distinct().ToList();
                buildingColumn.SetChoices(choices);
            }
            var visible = _viewModel.SheetView.Cast<SheetItem>().ToList();
            var query = (_sheetSearch.Text ?? string.Empty).Trim();
            if (query.Length > 0)
                visible = visible.Where(item => new[]
                {
                    item.Building, item.SheetNumber, item.SheetName, item.FrameDisplay,
                    item.OutputPaperSize, item.SourceFileName, item.SourceLayout
                }.Any(value => !string.IsNullOrWhiteSpace(value)
                    && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
            _sheetCount.Text = "共 " + visible.Count + " 张图纸";
            var rowsChanged = _sheetRows.Count != visible.Count;
            if (!rowsChanged)
                for (var index = 0; index < visible.Count; index++)
                    if (!ReferenceEquals(_sheetRows[index], visible[index])) { rowsChanged = true; break; }

            if (rowsChanged)
            {
                SuspendRedraw(_sheets);
                try
                {
                    _sheetRows.RaiseListChangedEvents = false;
                    _sheetRows.Clear();
                    foreach (var item in visible) _sheetRows.Add(item);
                    _sheetRows.RaiseListChangedEvents = true;
                    _sheetSource.ResetBindings(false);
                }
                finally
                {
                    _sheetRows.RaiseListChangedEvents = true;
                    ResumeRedraw(_sheets);
                }
            }
            if (_viewModel.SelectedSheet != null)
                foreach (DataGridViewRow row in _sheets.Rows) if (ReferenceEquals(row.DataBoundItem, _viewModel.SelectedSheet)) { row.Selected = true; break; }
        }

        private void RefreshSelectedSheetReview()
        {
            if (IsDisposed || Disposing) return;
            var sheet = _viewModel.SelectedSheet ?? CurrentSheet();
            _updatingQuickSheetSettings = true;
            try
            {
                var hasSheet = sheet != null;
                _quickOrientation.Enabled = hasSheet;
                _quickScale.Enabled = hasSheet;
                if (!hasSheet)
                {
                    _previewTitle.Text = "请选择一张图纸";
                    _previewPosition.Text = string.Empty;
                    _previewPaper.Text = "纸张：—";
                    _previewDirection.Text = "方向：—";
                    _previewScale.Text = "比例：—";
                    _previewFrame.Text = "图框：—";
                    _previewNotice.Text = "选择图纸后可在此核对纸张、方向和打印比例。";
                    _previewNotice.BackColor = System.Drawing.Color.FromArgb(30, 53, 68);
                    _previewNotice.ForeColor = TextSecondary;
                    _quickFrameValue.Text = "—";
                    _quickPaperValue.Text = "—";
                    _quickOrientation.SelectedIndex = -1;
                    _quickScale.Text = string.Empty;
                    _sheetPreview.ShowSheet(null, null);
                    return;
                }

                _previewTitle.Text = string.Join("  ", new[] { sheet.SheetNumber, sheet.SheetName }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var visibleIndex = _sheetRows.IndexOf(sheet);
                _previewPosition.Text = visibleIndex >= 0 ? (visibleIndex + 1) + " / " + _sheetRows.Count : string.Empty;
                _previewPaper.Text = "纸张：" + (string.IsNullOrWhiteSpace(sheet.OutputPaperSize) ? "未设置" : sheet.OutputPaperSize);
                _previewDirection.Text = "方向：" + (string.IsNullOrWhiteSpace(sheet.PaperOrientation) ? "未设置" : sheet.PaperOrientation + "（跟随 CAD）");
                _previewScale.Text = "比例：" + (string.IsNullOrWhiteSpace(sheet.PrintScale) ? "未设置" : sheet.PrintScale);
                _previewFrame.Text = "图框：" + (string.IsNullOrWhiteSpace(sheet.FrameDisplay) ? "未登记" : sheet.FrameDisplay);
                _quickFrameValue.Text = string.IsNullOrWhiteSpace(sheet.FrameDisplay) ? "未登记" : sheet.FrameDisplay;
                _quickPaperValue.Text = string.IsNullOrWhiteSpace(sheet.OutputPaperSize) ? "未设置" : sheet.OutputPaperSize;
                _quickOrientation.SelectedItem = sheet.PaperOrientation;
                _quickScale.Text = sheet.PrintScale ?? string.Empty;

                System.Drawing.Color statusColor;
                var reviewStatus = SheetReviewStatus(sheet, out statusColor);
                if (reviewStatus == "正常")
                {
                    _previewNotice.Text = "当前图框范围、纸张和方向已就绪，可以发布。";
                    _previewNotice.BackColor = System.Drawing.Color.FromArgb(26, 61, 48);
                    _previewNotice.ForeColor = Success;
                }
                else
                {
                    _previewNotice.Text = reviewStatus == "空白风险"
                        ? "当前图框没有有效范围，发布前请重新扫描或检查图框登记。"
                        : "当前图纸信息不完整，请核对图框、纸张、方向和比例后再发布。";
                    _previewNotice.BackColor = reviewStatus == "空白风险"
                        ? System.Drawing.Color.FromArgb(66, 35, 35)
                        : System.Drawing.Color.FromArgb(62, 46, 25);
                    _previewNotice.ForeColor = statusColor;
                }
                var previewKey = PrintPreviewKey(sheet);
                _sheetPreview.ShowSheet(sheet, previewKey);
            }
            finally { _updatingQuickSheetSettings = false; }
        }

        private async System.Threading.Tasks.Task GenerateSelectedPrintPreviewAsync()
        {
            if (IsDisposed || Disposing || _updatingAllPreviews) return;
            var sheet = _viewModel.SelectedSheet ?? CurrentSheet();
            if (sheet == null) return;
            var key = PrintPreviewKey(sheet);
            var requestVersion = ++_printPreviewRequestVersion;
            _sheetPreview.ShowLoading(sheet, key);
            string pdfPath = null;
            try
            {
                pdfPath = await _viewModel.CreatePrintPreviewAsync(sheet);
                var bitmap = PdfPreviewRenderer.RenderFirstPage(pdfPath, Math.Max(900, _sheetPreview.Width * 2), Math.Max(700, _sheetPreview.Height * 2));
                if (requestVersion != _printPreviewRequestVersion || IsDisposed || Disposing)
                {
                    bitmap.Dispose();
                    return;
                }
                // Plot preparation may repair a persisted frame range. Cache the
                // preview under the resulting settings so returning to this row
                // can reuse it without plotting again.
                _sheetPreview.ShowPreview(sheet, PrintPreviewKey(sheet), bitmap);
            }
            catch (Exception exception)
            {
                PdfPublisherService.WritePublishDiagnostic("生成单张打印预览失败" + Environment.NewLine + exception + Environment.NewLine);
                if (requestVersion == _printPreviewRequestVersion && !IsDisposed && !Disposing)
                    _sheetPreview.ShowError(sheet, key, exception.Message);
            }
            finally
            {
                try { if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
            }
        }

        private async System.Threading.Tasks.Task GenerateAllPrintPreviewsAsync()
        {
            if (IsDisposed || Disposing || _updatingAllPreviews) return;
            var sheets = _sheetRows.Where(x => x != null).ToList();
            if (sheets.Count == 0) return;
            if (MessageBox.Show(this, "将更新当前列表中的 " + sheets.Count + " 张打印预览。继续吗？",
                    "全部更新预览", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

            _updatingAllPreviews = true;
            _refreshPreviewButton.Enabled = false;
            _refreshAllPreviewsButton.Enabled = false;
            var failures = 0;
            try
            {
                _status.Text = "正在更新打印预览：0 / " + sheets.Count;
                await _viewModel.CreatePrintPreviewsAsync(sheets, (completed, total, sheet, pdfPath, generationError) =>
                {
                    Exception error = generationError;
                    try
                    {
                        if (error == null)
                        {
                            var bitmap = PdfPreviewRenderer.RenderFirstPage(pdfPath,
                                Math.Max(900, _sheetPreview.Width * 2), Math.Max(700, _sheetPreview.Height * 2));
                            var previewKey = PrintPreviewKey(sheet);
                            if (ReferenceEquals(sheet, _viewModel.SelectedSheet))
                                _sheetPreview.ShowPreview(sheet, previewKey, bitmap);
                            else
                                _sheetPreview.CachePreview(sheet, previewKey, bitmap);
                        }
                    }
                    catch (Exception exception) { error = exception; }
                    finally
                    {
                        try { if (!string.IsNullOrWhiteSpace(pdfPath) && File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
                    }
                    if (error != null)
                    {
                        failures++;
                        PdfPublisherService.WritePublishDiagnostic("批量生成打印预览失败：" + sheet.SheetNumber + " "
                            + sheet.SheetName + Environment.NewLine + error + Environment.NewLine);
                        if (ReferenceEquals(sheet, _viewModel.SelectedSheet))
                            _sheetPreview.ShowError(sheet, PrintPreviewKey(sheet), error.Message);
                    }
                    _status.Text = "正在更新打印预览：" + completed + " / " + total
                        + (failures > 0 ? "，失败 " + failures : string.Empty);
                    System.Windows.Forms.Application.DoEvents();
                });
                _status.Text = failures == 0
                    ? "已更新当前列表的 " + sheets.Count + " 张打印预览。"
                    : "打印预览更新完成：成功 " + (sheets.Count - failures) + " 张，失败 " + failures + " 张。";
            }
            finally
            {
                _updatingAllPreviews = false;
                _refreshPreviewButton.Enabled = true;
                _refreshAllPreviewsButton.Enabled = true;
            }
        }

        private void OpenLargePrintPreview()
        {
            var preview = _sheetPreview.ClonePreview();
            if (preview == null) return;
            var sheet = _viewModel.SelectedSheet ?? CurrentSheet();
            using (var viewer = new PrintPreviewZoomForm(preview, sheet)) viewer.ShowDialog(this);
        }

        private static string PrintPreviewKey(SheetItem sheet)
        {
            if (sheet == null) return string.Empty;
            return string.Join("|", new[]
            {
                sheet.SourceFile ?? string.Empty, sheet.SourceLayout ?? string.Empty, sheet.BlockHandle ?? string.Empty,
                sheet.MinX.ToString("R"), sheet.MinY.ToString("R"), sheet.MaxX.ToString("R"), sheet.MaxY.ToString("R"),
                sheet.FrameDisplay ?? string.Empty, sheet.PaperOrientation ?? string.Empty, sheet.PrintScale ?? string.Empty,
                sheet.PlotStyle ?? string.Empty
            });
        }

        private void CommitQuickSheetSettings(bool includeScale)
        {
            if (_refreshing || _updatingQuickSheetSettings) return;
            var sheet = _viewModel.SelectedSheet ?? CurrentSheet();
            if (sheet == null) return;
            if (!string.IsNullOrWhiteSpace(_quickOrientation.Text)) sheet.PaperOrientation = _quickOrientation.Text;
            if (includeScale && !string.IsNullOrWhiteSpace(_quickScale.Text)) sheet.PrintScale = _quickScale.Text.Trim();
            _viewModel.SelectedSheet = sheet;
            _viewModel.ApplySheetEdits();
            _sheetSource.ResetBindings(false);
            RefreshSheets();
            RefreshSelectedSheetReview();
        }

        private void PaintReviewStatusCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _sheets.Columns[e.ColumnIndex].Name != "ReviewStatusColumn") return;
            var sheet = _sheets.Rows[e.RowIndex].DataBoundItem as SheetItem;
            System.Drawing.Color color;
            var status = SheetReviewStatus(sheet, out color);
            e.PaintBackground(e.ClipBounds, true);
            var dotSize = 8;
            var dotX = e.CellBounds.Left + 9;
            var dotY = e.CellBounds.Top + (e.CellBounds.Height - dotSize) / 2;
            using (var brush = new System.Drawing.SolidBrush(color)) e.Graphics.FillEllipse(brush, dotX, dotY, dotSize, dotSize);
            var textBounds = new System.Drawing.Rectangle(dotX + 14, e.CellBounds.Top, Math.Max(0, e.CellBounds.Width - 24), e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics, status, e.CellStyle.Font, textBounds,
                e.CellStyle.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (var pen = new System.Drawing.Pen(_sheets.GridColor))
            {
                e.Graphics.DrawLine(pen, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom);
                e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            }
            e.Handled = true;
        }

        private void PaintGridChoiceCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || !(_sheets.Columns[e.ColumnIndex] is ThemedGridChoiceColumn)) return;
            e.PaintBackground(e.ClipBounds, true);
            var value = Convert.ToString(e.FormattedValue) ?? string.Empty;
            var textBounds = new System.Drawing.Rectangle(
                e.CellBounds.Left + 6, e.CellBounds.Top,
                Math.Max(1, e.CellBounds.Width - 26), e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics, value, e.CellStyle.Font, textBounds,
                e.CellStyle.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var centerX = e.CellBounds.Right - 12;
            var centerY = e.CellBounds.Top + e.CellBounds.Height / 2;
            using (var pen = new System.Drawing.Pen(TextSecondary, 1.3F))
                e.Graphics.DrawLines(pen, new[]
                {
                    new System.Drawing.Point(centerX - 3, centerY - 1),
                    new System.Drawing.Point(centerX, centerY + 2),
                    new System.Drawing.Point(centerX + 3, centerY - 1)
                });
            using (var pen = new System.Drawing.Pen(_sheets.GridColor))
            {
                e.Graphics.DrawLine(pen, e.CellBounds.Right - 1, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Bottom);
                e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            }
            e.Handled = true;
        }

        private void ShowGridChoicePopup(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var column = _sheets.Columns[e.ColumnIndex] as ThemedGridChoiceColumn;
            if (column == null || column.Choices.Count == 0) return;
            var row = _sheets.Rows[e.RowIndex];
            var selectedText = Convert.ToString(row.Cells[e.ColumnIndex].Value) ?? string.Empty;
            var selectedIndex = column.Choices.FindIndex(value => string.Equals(value, selectedText, StringComparison.Ordinal));
            CloseGridChoicePopup();

            var values = column.Choices.Cast<object>().ToList();
            var rowHeight = Math.Max(30, Font.Height + 14);
            var visibleRows = Math.Min(10, values.Count);
            var cellBounds = _sheets.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
            var width = Math.Max(column.Width, 130);
            var list = new PopupSelectionList(values, value => Convert.ToString(value) ?? string.Empty, selectedIndex, rowHeight)
            {
                Size = new System.Drawing.Size(width, visibleRows * rowHeight + 2),
                Font = _sheets.Font
            };
            var popup = new ToolStripDropDown
            {
                AutoSize = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                BackColor = SurfaceRaised,
                DropShadowEnabled = true,
                Size = list.Size
            };
            popup.Items.Add(new ToolStripControlHost(list)
            {
                AutoSize = false,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                Size = list.Size
            });
            _gridChoicePopup = popup;
            list.ItemChosen += index =>
            {
                if (index < 0 || index >= column.Choices.Count) return;
                row.Cells[e.ColumnIndex].Value = column.Choices[index];
                var item = row.DataBoundItem as SheetItem;
                CloseGridChoicePopup();
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    _viewModel.SelectedSheet = item;
                    _viewModel.ApplySheetEdits();
                    RefreshAll();
                }));
            };
            popup.Closed += (closedSender, closedArgs) =>
            {
                if (ReferenceEquals(_gridChoicePopup, popup)) _gridChoicePopup = null;
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(() =>
                    {
                        popup.Dispose();
                        if (!IsDisposed && e.RowIndex < _sheets.Rows.Count && e.ColumnIndex < _sheets.Columns.Count)
                            _sheets.InvalidateCell(e.ColumnIndex, e.RowIndex);
                    }));
                else popup.Dispose();
            };
            popup.Show(_sheets, new System.Drawing.Point(cellBounds.Left, cellBounds.Bottom));
            list.Focus();
        }

        private void CloseGridChoicePopup()
        {
            var popup = _gridChoicePopup;
            _gridChoicePopup = null;
            if (popup == null) return;
            if (!popup.IsDisposed) popup.Close();
        }

        private static string SheetReviewStatus(SheetItem sheet, out System.Drawing.Color color)
        {
            if (sheet == null || sheet.MaxX <= sheet.MinX || sheet.MaxY <= sheet.MinY)
            {
                color = Danger;
                return "空白风险";
            }
            if (string.IsNullOrWhiteSpace(sheet.FrameDisplay) || string.IsNullOrWhiteSpace(sheet.OutputPaperSize)
                || string.IsNullOrWhiteSpace(sheet.PaperOrientation) || string.IsNullOrWhiteSpace(sheet.PrintScale))
            {
                color = Warning;
                return "需检查";
            }
            color = Success;
            return "正常";
        }

        private void SetSheetSort(int selectedIndex)
        {
            if (selectedIndex <= 0 || selectedIndex >= _sheetSort.Items.Count) return;
            if (_sheetSort.SelectedIndex == selectedIndex) ApplySheetSort(selectedIndex);
            else _sheetSort.SelectedIndex = selectedIndex;
        }

        private void ApplySheetSort(int selectedIndex)
        {
            var mode = SheetSortMode.Custom;
            switch (selectedIndex)
            {
                case 1: mode = SheetSortMode.SheetNumberAscending; break;
                case 2: mode = SheetSortMode.SheetNumberDescending; break;
                case 3: mode = SheetSortMode.SheetNameAscending; break;
                case 4: mode = SheetSortMode.SheetNameDescending; break;
                case 5: mode = SheetSortMode.SourceFileAscending; break;
            }
            _viewModel.SortVisibleSheets(mode);
            foreach (DataGridViewColumn column in _sheets.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
            var sortedProperty = selectedIndex <= 2 ? "SheetNumber" : selectedIndex <= 4 ? "SheetName" : "SourceFileName";
            var sortedColumn = _sheets.Columns.Cast<DataGridViewColumn>().FirstOrDefault(column => column.DataPropertyName == sortedProperty);
            if (sortedColumn != null)
                sortedColumn.HeaderCell.SortGlyphDirection = selectedIndex == 2 || selectedIndex == 4 ? SortOrder.Descending : SortOrder.Ascending;
            RefreshSheets();
        }

        private void SheetsMouseDown(object sender, MouseEventArgs e)
        {
            _dragCandidate = null;
            if (e.Button != MouseButtons.Left) return;
            var hit = _sheets.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return;
            _dragCandidate = _sheets.Rows[hit.RowIndex].DataBoundItem as SheetItem;
            _dragStart = e.Location;
        }

        private void SheetsMouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _dragCandidate == null) return;
            var dragSize = SystemInformation.DragSize;
            var dragBounds = new System.Drawing.Rectangle(
                _dragStart.X - dragSize.Width / 2, _dragStart.Y - dragSize.Height / 2, dragSize.Width, dragSize.Height);
            if (dragBounds.Contains(e.Location)) return;
            _sheets.DoDragDrop(_dragCandidate, DragDropEffects.Move);
        }

        private void SheetsDragOver(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(SheetItem)) as SheetItem;
            if (source == null) { e.Effect = DragDropEffects.None; return; }
            var point = _sheets.PointToClient(new System.Drawing.Point(e.X, e.Y));
            if (_sheets.Rows.Count > 0 && point.Y < _sheets.ColumnHeadersHeight + 18 && _sheets.FirstDisplayedScrollingRowIndex > 0)
                _sheets.FirstDisplayedScrollingRowIndex--;
            else if (_sheets.Rows.Count > 0 && point.Y > _sheets.ClientSize.Height - 18)
            {
                var first = _sheets.FirstDisplayedScrollingRowIndex;
                var displayed = _sheets.DisplayedRowCount(false);
                if (first >= 0 && first + displayed < _sheets.Rows.Count) _sheets.FirstDisplayedScrollingRowIndex++;
            }
            var hit = _sheets.HitTest(point.X, point.Y);
            if (hit.Type == DataGridViewHitTestType.ColumnHeader || hit.Type == DataGridViewHitTestType.TopLeftHeader)
            {
                e.Effect = DragDropEffects.None;
                ClearSheetDragIndicator();
                return;
            }
            var target = hit.RowIndex >= 0 ? _sheets.Rows[hit.RowIndex].DataBoundItem as SheetItem : null;
            if (target != null && !string.Equals(source.Building, target.Building, StringComparison.Ordinal))
            {
                e.Effect = DragDropEffects.None;
                ClearSheetDragIndicator();
                return;
            }
            _dragTargetRowIndex = hit.RowIndex < 0 && point.Y > _sheets.ColumnHeadersHeight && _sheets.Rows.Count > 0
                ? _sheets.Rows.Count - 1 : hit.RowIndex;
            _dragInsertAfter = hit.RowIndex < 0 || point.Y > _sheets.GetRowDisplayRectangle(hit.RowIndex, false).Top + _sheets.Rows[hit.RowIndex].Height / 2;
            e.Effect = DragDropEffects.Move;
            _sheets.Invalidate();
        }

        private void SheetsDragDrop(object sender, DragEventArgs e)
        {
            var source = e.Data.GetData(typeof(SheetItem)) as SheetItem;
            var target = _dragTargetRowIndex >= 0 && _dragTargetRowIndex < _sheets.Rows.Count
                ? _sheets.Rows[_dragTargetRowIndex].DataBoundItem as SheetItem : null;
            var insertAfter = _dragInsertAfter;
            ClearSheetDragIndicator();
            if (source == null) return;
            _viewModel.MoveSheet(source, target, insertAfter);
            _refreshing = true;
            try { _sheetSort.SelectedIndex = 0; }
            finally { _refreshing = false; }
            foreach (DataGridViewColumn column in _sheets.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
            RefreshSheets();
        }

        // 这两个画笔是拖动图框列表时的插入指示线，只在被拖到的那一行绘制；
        // 但拖动过程中行会被反复重绘，所以提成静态字段、不再每次 new。
        private static readonly System.Drawing.Pen SheetDragPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(28, 105, 184), 3F);

        private void SheetsRowPostPaint(object sender, DataGridViewRowPostPaintEventArgs e)
        {
            if (e.RowIndex != _dragTargetRowIndex) return;
            var y = _dragInsertAfter ? e.RowBounds.Bottom - 2 : e.RowBounds.Top;
            e.Graphics.DrawLine(SheetDragPen, e.RowBounds.Left, y, e.RowBounds.Right, y);
        }

        private void ClearSheetDragIndicator()
        {
            _dragTargetRowIndex = -1;
            _dragCandidate = null;
            _sheets.Invalidate();
        }

        private static void SuspendRedraw(Control control)
        {
            if (control != null && control.IsHandleCreated)
                SendMessage(control.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        }

        private static void ResumeRedraw(Control control)
        {
            if (control == null || !control.IsHandleCreated) return;
            SendMessage(control.Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            control.Invalidate(true);
        }

        private void SelectCurrentSheetRow()
        {
            if (_refreshing || _sheets.IsDisposed) return;
            foreach (DataGridViewRow row in _sheets.Rows)
            {
                var selected = ReferenceEquals(row.DataBoundItem, _viewModel.SelectedSheet);
                if (row.Selected != selected) row.Selected = selected;
                if (selected && !ReferenceEquals(_sheets.CurrentRow, row))
                    _sheets.CurrentCell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(cell => cell.Visible);
            }
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
                var column = _sheets.Columns["PlotStyleColumn"] as ThemedGridChoiceColumn;
                if (column != null)
                    column.SetChoices(PlotStyleChoices());
            }
            finally { _plotStyle.EndUpdate(); }
        }

        private void CreateProject()
        {
            _viewModel.NewProjectName = _newProjectName.Text; _viewModel.NewProjectCommand.Execute(null); RefreshAll();
        }

        private Button ProjectManagerButton()
        {
            var button = new RoundedButton
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

        private void OpenFrameManager()
        {
            FrameRegistrationManagerForm.CadAction action;
            using (var dialog = new FrameRegistrationManagerForm(_viewModel, RefreshAll))
            {
                dialog.ShowDialog(this);
                action = dialog.RequestedCadAction;
            }
            RefreshAll();
            if (action == FrameRegistrationManagerForm.CadAction.Pick)
                _viewModel.RegisterFrameCommand.Execute(null);
            else if (action == FrameRegistrationManagerForm.CadAction.Create)
                OpenFrameCreation();
        }

        private void SaveCurrentCad()
        {
            string destination, error;
            if (_viewModel.SaveCurrentCadToProjectFolder(out destination, out error)) RefreshAll();
            else MessageBox.Show(this, "保存 CAD 失败：\r\n" + error, "工程文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            using (var dialog = new DpiAwareForm
            {
                Text = "扫描设置",
                Width = 360,
                Height = 430,
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                MinimizeBox = false,
                MaximizeBox = false,
                BackColor = Canvas,
                ForeColor = TextPrimary
            })
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1, BackColor = Canvas };
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.Controls.Add(new Label { Text = "勾选本次要扫描的空间（设置会保存到工程）", AutoSize = true }, 0, 0);
                var spaces = new ThemedCheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BackColor = Surface, ForeColor = TextPrimary };
                spaces.Items.Add("模型空间", _viewModel.ScanModelSpace);
                foreach (var layout in layouts)
                    spaces.Items.Add(layout, _viewModel.ScanAllLayouts || (_viewModel.SelectedProject?.SelectedLayouts?.Contains(layout) ?? false));
                panel.Controls.Add(ThemedListHost(spaces, 0), 0, 1);
                var allLayouts = new ToggleSwitch { Text = "自动扫描所有布局（包括以后新增的布局）", Checked = _viewModel.ScanAllLayouts };
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

        private SheetItem CurrentSheet()
        {
            return _sheets.CurrentRow == null ? null : _sheets.CurrentRow.DataBoundItem as SheetItem;
        }

        internal enum UiIcon { Plus, Remove, Refresh, List, Select, Frame, Gear, Save, SaveAll, Folder, Open, Up, Down, Publish,
            Search, Switch, Copy, Clock, Backup, Trash, Document, Cube, Text }

        private static Button IconButton(string text, UiIcon icon, Action action)
        {
            var button = Button(text, action);
            button.Image = DrawUiIcon(icon, TextSecondary);
            button.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            button.TextImageRelation = TextImageRelation.ImageBeforeText;
            button.Padding = new Padding(7, 2, 7, 2);
            return button;
        }

        private static Button IconAccentButton(string text, UiIcon icon, Action action)
        {
            var button = IconButton(text, icon, action);
            button.BackColor = SurfaceRaised;
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = Accent;
            button.Image = DrawUiIcon(icon, Accent);
            return button;
        }

        private static Button PrimaryButton(string text, UiIcon icon, Action action)
        {
            var button = IconButton(text, icon, action);
            button.BackColor = Accent;
            button.ForeColor = System.Drawing.Color.White;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentPressed;
            button.Image = DrawUiIcon(icon, System.Drawing.Color.White);
            button.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold);
            return button;
        }

        private static Button ToolbarButton(string text, UiIcon icon, Action action)
        {
            var button = IconButton(text, icon, action);
            button.AutoSize = false;
            button.Width = 100;
            button.MinimumSize = new System.Drawing.Size(100, StandardControlHeight);
            button.Height = StandardControlHeight;
            button.Margin = new Padding(0, 0, 6, 0);
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
            var dialog = new CatalogInsertForm(document, _viewModel.Sheets.ToList(), RefreshAfterCatalogInsert);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
        }

        public void OpenCatalogInsertForCommand() => OpenCatalogInsert();

        private void RefreshAfterCatalogInsert()
        {
            // Selecting the CAD insertion point happens after this modeless dialog
            // closes. The main panel can also be closed in that interval.
            if (IsDisposed || Disposing || _cadFiles.IsDisposed || _publishBuildings.IsDisposed) return;
            RefreshAll();
        }

        internal static System.Drawing.Image DrawUiIcon(UiIcon icon, System.Drawing.Color color)
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
                    case UiIcon.Frame:
                        graphics.DrawRectangle(pen, 2, 2, 12, 12);
                        graphics.DrawLine(pen, 5, 2, 5, 5); graphics.DrawLine(pen, 2, 5, 5, 5);
                        graphics.DrawLine(pen, 11, 14, 11, 11); graphics.DrawLine(pen, 11, 11, 14, 11);
                        break;
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
                    case UiIcon.Search:
                        graphics.DrawEllipse(pen, 2, 2, 9, 9); graphics.DrawLine(pen, 10, 10, 15, 15); break;
                    case UiIcon.Switch:
                        graphics.DrawLine(pen, 2, 5, 13, 5); graphics.DrawLine(pen, 10, 2, 13, 5); graphics.DrawLine(pen, 10, 8, 13, 5);
                        graphics.DrawLine(pen, 14, 11, 3, 11); graphics.DrawLine(pen, 6, 8, 3, 11); graphics.DrawLine(pen, 6, 14, 3, 11); break;
                    case UiIcon.Copy:
                        graphics.DrawRectangle(pen, 5, 3, 9, 11); graphics.DrawLine(pen, 2, 11, 2, 1); graphics.DrawLine(pen, 2, 1, 11, 1); break;
                    case UiIcon.Clock:
                        graphics.DrawEllipse(pen, 2, 2, 12, 12); graphics.DrawLine(pen, 8, 4, 8, 8); graphics.DrawLine(pen, 8, 8, 11, 10); break;
                    case UiIcon.Backup:
                        graphics.DrawEllipse(pen, 2, 2, 12, 4); graphics.DrawArc(pen, 2, 4, 12, 4, 0, 180); graphics.DrawArc(pen, 2, 9, 12, 4, 0, 180);
                        graphics.DrawLine(pen, 2, 4, 2, 11); graphics.DrawLine(pen, 14, 4, 14, 11); break;
                    case UiIcon.Trash:
                        graphics.DrawRectangle(pen, 4, 5, 8, 9); graphics.DrawLine(pen, 2, 4, 14, 4); graphics.DrawLine(pen, 6, 2, 10, 2);
                        graphics.DrawLine(pen, 7, 7, 7, 12); graphics.DrawLine(pen, 9, 7, 9, 12); break;
                    case UiIcon.Document:
                        graphics.DrawLines(pen, new[] { new System.Drawing.Point(3, 1), new System.Drawing.Point(10, 1),
                            new System.Drawing.Point(13, 4), new System.Drawing.Point(13, 15),
                            new System.Drawing.Point(3, 15), new System.Drawing.Point(3, 1) });
                        graphics.DrawLine(pen, 10, 1, 10, 4); graphics.DrawLine(pen, 10, 4, 13, 4);
                        graphics.DrawLine(pen, 5, 8, 11, 8); graphics.DrawLine(pen, 5, 11, 10, 11); break;
                    case UiIcon.Cube:
                        graphics.DrawPolygon(pen, new[] { new System.Drawing.Point(8, 1), new System.Drawing.Point(14, 4),
                            new System.Drawing.Point(14, 12), new System.Drawing.Point(8, 15),
                            new System.Drawing.Point(2, 12), new System.Drawing.Point(2, 4) });
                        graphics.DrawLine(pen, 2, 4, 8, 7); graphics.DrawLine(pen, 14, 4, 8, 7);
                        graphics.DrawLine(pen, 8, 7, 8, 15); break;
                    case UiIcon.Text:
                        graphics.DrawLine(pen, 3, 13, 8, 2); graphics.DrawLine(pen, 8, 2, 13, 13);
                        graphics.DrawLine(pen, 5, 9, 11, 9); graphics.DrawLine(pen, 2, 13, 5, 13);
                        graphics.DrawLine(pen, 11, 13, 14, 13); break;
                }
            }
            return bitmap;
        }

        private static Button Button(string text, Action action)
        {
            var button = new RoundedButton
            {
                Text = text,
                AutoSize = true,
                Height = StandardControlHeight,
                Margin = new Padding(3, 0, 3, 4),
                Padding = new Padding(7, 2, 7, 2),
                MinimumSize = new System.Drawing.Size(0, StandardControlHeight),
                FlatStyle = FlatStyle.Flat,
                BackColor = SurfaceRaised,
                ForeColor = TextPrimary,
                Cursor = Cursors.Hand
            };
            button.Tag = text;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(42, 60, 78);
            button.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(24, 36, 48);
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
                case "图框登记": return "管理图框规则，拾取登记并修改纸张、属性映射和排版范围。";
                case "存入工程": return "把当前 DWG 的副本保存到当前工程文件夹。";
                case "目录打印": return "扫描当前工程文件夹中的 DWG，然后按工程设置发布 PDF。";
                case "更新预览": return "重新生成当前图纸的最终 PDF 预览并替换缓存。";
                case "全部预览": return "按 CAD 文件分组，更新当前列表中全部图纸的最终 PDF 预览。";
                case "发布 PDF": return "按当前勾选的子项目、图纸顺序、纸张和打印样式生成 PDF。";
                case "上移":
                case "上移图纸": return "把当前图纸在所属子项目的发布顺序中上移一位。";
                case "下移":
                case "下移图纸": return "把当前图纸在所属子项目的发布顺序中下移一位。";
                case "刷新样式": return "重新读取 AutoCAD 当前可用的 CTB/STB 打印样式列表。";
                case "收藏样式": return "把当前打印样式保存到本工程的常用样式列表。";
                case "选择目录": return "选择本次工程 PDF 的根输出目录。";
                case "选择": return "选择本次工程 PDF 的根输出目录。";
                case "打开目录": return "在 Windows 资源管理器中打开当前 PDF 输出目录。";
                case "打开": return "在 Windows 资源管理器中打开当前 PDF 输出目录。";
                case "全选项目": return "勾选所有子项目，使其全部参与本次 PDF 发布。";
                case "全选": return "勾选所有子项目，使其全部参与本次 PDF 发布。";
                case "取消全选": return "取消所有子项目的发布勾选。";
                case "清空": return "取消所有子项目的发布勾选。";
                default: return label;
            }
        }

        private static Button AccentButton(string text, Action action)
        {
            var button = Button(text, action);
            button.BackColor = SurfaceRaised;
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = Accent;
            return button;
        }

        private static void ApplyInputStyle(Control control)
        {
            control.AutoSize = false;
            control.Height = StandardControlHeight;
            control.Margin = new Padding(0, 0, 8, 4);
            control.BackColor = SurfaceRaised;
            control.ForeColor = TextPrimary;
            var combo = control as ThemedComboBox;
            if (combo != null) combo.Height = StandardControlHeight;
            if (control is TextBox)
            {
                ((TextBox)control).BorderStyle = BorderStyle.FixedSingle;
                control.Region = null;
            }
        }

        private static void AddCadAction(TableLayoutPanel layout, Button button, int column, int row)
        {
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 2, 3, 2);
            layout.Controls.Add(button, column, row);
        }

        // 分隔条上的抓手图案：分隔条在鼠标划过、拖动、窗口缩放时都会重绘，
        // 两个画笔提成静态字段，不再每次重绘 new。
        private static readonly System.Drawing.Pen SplitterGripPen = new System.Drawing.Pen(Accent, 1.5F);

        private static void AddHeightDragIndicator(SplitContainer splitter)
        {
            splitter.BackColor = Canvas;
            splitter.Paint += (sender, args) =>
            {
                var splitterBounds = splitter.SplitterRectangle;
                var centerX = splitterBounds.Left + splitterBounds.Width / 2;
                var centerY = splitterBounds.Top + splitterBounds.Height / 2;
                for (var offset = -6; offset <= 6; offset += 6)
                    args.Graphics.DrawLine(SplitterGripPen, centerX + offset - 2, centerY - 2, centerX + offset + 2, centerY + 2);
            };
        }

        private void ConfigureSmoothSplitter(SplitContainer splitter)
        {
            var dragging = false;
            var redrawTargets = new Control[] { splitter.Panel1, splitter.Panel2 };

            Action beginDrag = () =>
            {
                if (dragging) return;
                dragging = true;
                foreach (var target in redrawTargets)
                {
                    target.SuspendLayout();
                    SetRedraw(target, false);
                }
            };

            Action endDrag = () =>
            {
                if (!dragging) return;
                dragging = false;
                foreach (var target in redrawTargets)
                {
                    if (target == null || target.IsDisposed) continue;
                    target.ResumeLayout(true);
                    SetRedraw(target, true);
                    target.Invalidate(true);
                }
                splitter.Invalidate(true);
                splitter.Update();
                SaveUiLayoutSettings();
            };

            splitter.MouseDown += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left && splitter.SplitterRectangle.Contains(args.Location))
                    beginDrag();
            };
            splitter.MouseUp += (sender, args) => endDrag();
            splitter.MouseCaptureChanged += (sender, args) =>
            {
                if (dragging && Control.MouseButtons == MouseButtons.None) endDrag();
            };
            splitter.Disposed += (sender, args) => endDrag();
        }

        private static void SetRedraw(Control control, bool enabled)
        {
            if (control == null || control.IsDisposed || !control.IsHandleCreated) return;
            SendMessage(control.Handle, WmSetRedraw, enabled ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        }

        private void LoadUiLayoutSettings()
        {
            try
            {
                var path = UiLayoutSettingsPath();
                if (!File.Exists(path)) return;
                var values = File.ReadAllLines(path);
                int width;
                if (values.Length > 0 && int.TryParse(values[0], out width) && width > 0) _savedLeftPanelWidth = width;
                if (values.Length > 1 && int.TryParse(values[1], out width) && width > 0) _savedRightPanelWidth = width;
                if (values.Length > 2)
                    _savedSheetColumnOrder.AddRange(values[2].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch { }
        }

        private void SaveUiLayoutSettings()
        {
            try
            {
                if (_leftSplitter != null && !_leftSplitter.IsDisposed && _leftSplitter.Width > 0)
                    _savedLeftPanelWidth = _leftSplitter.SplitterDistance;
                if (_rightSplitter != null && !_rightSplitter.IsDisposed && _rightSplitter.Width > 0)
                    _savedRightPanelWidth = _rightSplitter.Width - _rightSplitter.SplitterDistance - _rightSplitter.SplitterWidth;
                var columnOrder = _sheets.Columns.Cast<DataGridViewColumn>()
                    .OrderBy(column => column.DisplayIndex)
                    .Select(column => column.DataPropertyName)
                    .Where(name => !string.IsNullOrWhiteSpace(name));
                File.WriteAllLines(UiLayoutSettingsPath(), new[]
                {
                    _savedLeftPanelWidth.ToString(),
                    _savedRightPanelWidth.ToString(),
                    string.Join("|", columnOrder)
                });
            }
            catch { }
        }

        private void RestoreSheetColumnOrder()
        {
            if (_savedSheetColumnOrder.Count == 0) return;
            try
            {
                var nextIndex = 0;
                foreach (var propertyName in _savedSheetColumnOrder)
                {
                    var column = _sheets.Columns.Cast<DataGridViewColumn>()
                        .FirstOrDefault(item => string.Equals(item.DataPropertyName, propertyName, StringComparison.Ordinal));
                    if (column != null) column.DisplayIndex = nextIndex++;
                }
            }
            catch
            {
                _savedSheetColumnOrder.Clear();
            }
        }

        private static string UiLayoutSettingsPath()
        {
            return UserDataPaths.SettingsFile("ui-layout.settings", "BatchPdfPublisher.ui-layout.settings");
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static void SetSplitterLayoutSafe(
            SplitContainer splitter,
            int desiredDistance,
            int desiredPanel1MinSize,
            int desiredPanel2MinSize)
        {
            if (splitter == null || splitter.IsDisposed) return;

            var extent = splitter.Orientation == Orientation.Vertical
                ? splitter.ClientSize.Width
                : splitter.ClientSize.Height;
            var available = extent - splitter.SplitterWidth;
            if (available <= 0) return;

            // At startup WinForms can expose a temporarily narrow split
            // container, especially with high DPI scaling. Keep a small range
            // for the divider even when the requested minimums do not fit.
            var minimumBudget = Math.Max(0, available - 2);
            var panel1MinSize = Math.Max(0, desiredPanel1MinSize);
            var panel2MinSize = Math.Max(0, desiredPanel2MinSize);
            var requestedMinimum = panel1MinSize + panel2MinSize;
            if (requestedMinimum > minimumBudget && requestedMinimum > 0)
            {
                panel1MinSize = (int)((long)minimumBudget * panel1MinSize / requestedMinimum);
                panel2MinSize = minimumBudget - panel1MinSize;
            }

            var minimumDistance = panel1MinSize;
            var maximumDistance = available - panel2MinSize;
            var distance = Clamp(desiredDistance, minimumDistance, maximumDistance);

            // Clear the previous constraints before moving the divider. Saved
            // widths from a larger monitor may otherwise reject the new value.
            splitter.Panel1MinSize = 0;
            splitter.Panel2MinSize = 0;
            splitter.SplitterDistance = distance;
            splitter.Panel1MinSize = Math.Min(panel1MinSize, distance);
            splitter.Panel2MinSize = Math.Min(panel2MinSize, available - distance);
        }

        private static TableLayoutPanel Card(int rows, Padding padding)
        {
            var card = new RoundedTableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = rows,
                ColumnCount = 1,
                Padding = padding,
                BackColor = Surface,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
                BorderColor = Border,
                CornerRadius = 10
            };
            return card;
        }

        private static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3), ForeColor = TextSecondary };
        }

        private static Label SectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = TextPrimary,
                Margin = new Padding(3, 3, 3, 8)
            };
        }

        private static Label SectionHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Height = 36,
                Width = 260,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 8, 0),
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold),
                ForeColor = TextPrimary,
                BackColor = Surface,
                Margin = Padding.Empty
            };
        }

        private static Button SegmentButton(string text, bool selected)
        {
            var button = new RoundedButton
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 1;
            SetSegmentState(button, selected);
            return button;
        }

        private static void SetSegmentSelection(Button files, Button frames, bool filesSelected)
        {
            SetSegmentState(files, filesSelected);
            SetSegmentState(frames, !filesSelected);
        }

        private static void SetSegmentState(Button button, bool selected)
        {
            button.BackColor = selected ? System.Drawing.Color.FromArgb(25, 78, 137) : SurfaceRaised;
            button.ForeColor = selected ? System.Drawing.Color.White : TextSecondary;
            button.FlatAppearance.BorderColor = selected ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = selected ? System.Drawing.Color.FromArgb(30, 90, 155) : System.Drawing.Color.FromArgb(42, 60, 78);
            button.FlatAppearance.MouseDownBackColor = selected ? System.Drawing.Color.FromArgb(21, 67, 119) : System.Drawing.Color.FromArgb(24, 36, 48);
        }

        private static void StyleInnerSplitter(SplitContainer splitter)
        {
            splitter.BackColor = Canvas;
            splitter.Panel1.BackColor = Canvas;
            splitter.Panel2.BackColor = Canvas;
        }

        private static void ApplyDarkControlStyles(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var textBox = child as TextBox;
                if (textBox != null)
                {
                    textBox.BackColor = SurfaceRaised;
                    textBox.ForeColor = TextPrimary;
                    textBox.BorderStyle = textBox.Parent != null
                        && (string.Equals(textBox.Parent.Tag as string, "themed-text-input", StringComparison.Ordinal)
                            || textBox.Parent is ThemedComboBox)
                        ? BorderStyle.None
                        : BorderStyle.FixedSingle;
                }
                var comboBox = child as ComboBox;
                if (comboBox != null)
                {
                    comboBox.BackColor = SurfaceRaised;
                    comboBox.ForeColor = TextPrimary;
                    comboBox.FlatStyle = FlatStyle.Flat;
                }
                var dataGrid = child as DataGridView;
                var listBox = child as ListBox;
                if (listBox != null)
                {
                    listBox.BackColor = Surface;
                    listBox.ForeColor = TextPrimary;
                    listBox.BorderStyle = listBox.Parent != null
                        && string.Equals(listBox.Parent.Tag as string, "themed-list-host", StringComparison.Ordinal)
                        ? BorderStyle.None
                        : BorderStyle.FixedSingle;
                    listBox.IntegralHeight = false;
                }
                var checkBox = child as CheckBox;
                if (checkBox != null)
                {
                    checkBox.ForeColor = checkBox.ForeColor == System.Drawing.SystemColors.ControlText || checkBox.ForeColor == System.Drawing.Color.Black
                        ? TextPrimary : checkBox.ForeColor;
                    checkBox.UseVisualStyleBackColor = false;
                }
                var label = child as Label;
                if (label != null && (label.ForeColor == System.Drawing.SystemColors.ControlText || label.ForeColor == System.Drawing.Color.Black))
                    label.ForeColor = TextPrimary;
                var flow = child as FlowLayoutPanel;
                if (flow != null && flow.BackColor == System.Drawing.SystemColors.Control)
                    flow.BackColor = child.Parent == null ? Surface : child.Parent.BackColor;
                var table = child as TableLayoutPanel;
                if (table != null && table.BackColor == System.Drawing.SystemColors.Control)
                    table.BackColor = child.Parent == null ? Surface : child.Parent.BackColor;
                var splitter = child as SplitContainer;
                if (splitter != null)
                {
                    splitter.BackColor = Canvas;
                    splitter.Panel1.BackColor = Canvas;
                    splitter.Panel2.BackColor = Canvas;
                }
                if (child.HasChildren) ApplyDarkControlStyles(child);
            }
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
            Action update = () =>
            {
                if (control.Width < 2 || control.Height < 2) return;
                using (var path = RoundedRectangle(new System.Drawing.Rectangle(0, 0, control.Width, control.Height), radius))
                    control.Region = new System.Drawing.Region(path);
            };
            control.HandleCreated += (sender, args) => update();
            control.SizeChanged += (sender, args) => update();
            if (control.IsHandleCreated) update();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref message);
                if ((int)message.Result != HtClient) return;

                var raw = message.LParam.ToInt64();
                var screenPoint = new System.Drawing.Point((short)(raw & 0xffff), (short)((raw >> 16) & 0xffff));
                var clientPoint = PointToClient(screenPoint);
                var grip = Math.Max(ResizeBorderThickness, 10 * DeviceDpi / 96);
                var left = clientPoint.X < grip;
                var right = clientPoint.X >= ClientSize.Width - grip;
                var top = clientPoint.Y < grip;
                var bottom = clientPoint.Y >= ClientSize.Height - grip;

                if (left && top) message.Result = (IntPtr)HtTopLeft;
                else if (right && top) message.Result = (IntPtr)HtTopRight;
                else if (left && bottom) message.Result = (IntPtr)HtBottomLeft;
                else if (right && bottom) message.Result = (IntPtr)HtBottomRight;
                else if (left) message.Result = (IntPtr)HtLeft;
                else if (right) message.Result = (IntPtr)HtRight;
                else if (top) message.Result = (IntPtr)HtTop;
                else if (bottom) message.Result = (IntPtr)HtBottom;
                return;
            }
            base.WndProc(ref message);
        }

        private const int WmSetRedraw = 0x000B;
        private const int WmNcHitTest = 0x0084;
        private const int WmNcLeftButtonDown = 0x00A1;
        private const int HtClient = 1;
        private const int HtCaption = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wordParameter, IntPtr longParameter);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private sealed class RoundedButton : Button
        {
            private bool _hovered;
            private bool _pressed;

            public RoundedButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                UseVisualStyleBackColor = false;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent == null ? Canvas : Parent.BackColor);
                var bounds = new System.Drawing.Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                var fillColor = _pressed ? FlatAppearance.MouseDownBackColor
                    : _hovered ? FlatAppearance.MouseOverBackColor : BackColor;
                if (fillColor == System.Drawing.Color.Empty) fillColor = BackColor;
                var borderColor = Focused ? Accent
                    : FlatAppearance.BorderColor == System.Drawing.Color.Empty ? Border : FlatAppearance.BorderColor;
                using (var path = RoundedRectangle(bounds, Math.Min(6, bounds.Height / 2)))
                using (var fill = new System.Drawing.SolidBrush(fillColor))
                using (var pen = new System.Drawing.Pen(borderColor))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(pen, path);
                }

                var content = System.Drawing.Rectangle.Inflate(bounds, -5, -2);
                var color = Enabled ? ForeColor : TextSecondary;
                if (Image != null)
                {
                    var textWidth = TextRenderer.MeasureText(e.Graphics, Text, Font,
                        new System.Drawing.Size(10000, content.Height), TextFormatFlags.NoPadding).Width;
                    var groupWidth = Image.Width + 5 + textWidth;
                    var imageX = content.Left + Math.Max(0, (content.Width - groupWidth) / 2);
                    var imageY = content.Top + (content.Height - Image.Height) / 2;
                    e.Graphics.DrawImage(Image, imageX, imageY, Image.Width, Image.Height);
                    content.X = imageX + Image.Width + 5;
                    content.Width = Math.Max(1, bounds.Right - 5 - content.X);
                }
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                if (TextAlign == System.Drawing.ContentAlignment.MiddleLeft || Image != null) flags |= TextFormatFlags.Left;
                else flags |= TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(e.Graphics, Text, Font, content, color, flags);
            }
        }

        private sealed class WindowChromeButton : Button
        {
            private readonly bool _closeButton;
            private bool _hovered;
            private bool _pressed;

            public WindowChromeButton(bool closeButton)
            {
                _closeButton = closeButton;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var fill = BackColor;
                if (_pressed) fill = _closeButton
                    ? System.Drawing.Color.FromArgb(154, 32, 43)
                    : System.Drawing.Color.FromArgb(38, 46, 58);
                else if (_hovered) fill = _closeButton
                    ? System.Drawing.Color.FromArgb(196, 43, 55)
                    : System.Drawing.Color.FromArgb(47, 55, 68);
                e.Graphics.Clear(fill);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private sealed class ThemedGridChoiceColumn : DataGridViewTextBoxColumn
        {
            private readonly List<string> _choices = new List<string>();
            public List<string> Choices => _choices;

            public void SetChoices(IEnumerable<string> choices)
            {
                _choices.Clear();
                if (choices == null) return;
                _choices.AddRange(choices.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
            }
        }

        internal sealed class ThemedComboBox : BufferedPanel
        {
            private readonly TextBox _editor = new TextBox();
            private readonly ItemCollection _items;
            private ToolStripDropDown _popup;
            private object _dataSource;
            private string _displayMember;
            private int _selectedIndex = -1;
            private ComboBoxStyle _dropDownStyle = ComboBoxStyle.DropDown;
            private bool _syncingText;

            public ThemedComboBox()
            {
                _items = new ItemCollection(this);
                Height = StandardControlHeight;
                MinimumSize = new System.Drawing.Size(0, StandardControlHeight);
                MaximumSize = new System.Drawing.Size(0, StandardControlHeight);
                BackColor = SurfaceRaised;
                ForeColor = TextPrimary;
                Cursor = Cursors.IBeam;
                TabStop = true;
                SetStyle(ControlStyles.Selectable, true);

                _editor.AutoSize = false;
                _editor.BorderStyle = BorderStyle.None;
                _editor.BackColor = SurfaceRaised;
                _editor.ForeColor = TextPrimary;
                _editor.Margin = Padding.Empty;
                _editor.TextChanged += (sender, args) =>
                {
                    if (_syncingText) return;
                    base.Text = _editor.Text;
                    Invalidate();
                };
                _editor.KeyDown += (sender, args) =>
                {
                    if (args.Alt && args.KeyCode == Keys.Down) { ShowDropDown(); args.SuppressKeyPress = true; return; }
                    OnKeyDown(args);
                };
                _editor.Enter += (sender, args) => Invalidate();
                _editor.Leave += (sender, args) => Invalidate();
                _editor.MouseDown += (sender, args) =>
                {
                    if (_dropDownStyle == ComboBoxStyle.DropDownList && args.Button == MouseButtons.Left) ShowDropDown();
                };
                Controls.Add(_editor);
                MouseDown += (sender, args) =>
                {
                    if (args.Button != MouseButtons.Left) return;
                    if (_dropDownStyle == ComboBoxStyle.DropDownList || args.X >= Width - 34)
                    {
                        Focus();
                        ShowDropDown();
                    }
                    else _editor.Focus();
                };
            }

            public ItemCollection Items => _items;
            public bool SquareCorners { get; set; }
            public bool IntegralHeight { get; set; }
            public FlatStyle FlatStyle { get; set; }

            public ComboBoxStyle DropDownStyle
            {
                get => _dropDownStyle;
                set
                {
                    _dropDownStyle = value;
                    _editor.Visible = value != ComboBoxStyle.DropDownList;
                    Cursor = _editor.Visible ? Cursors.IBeam : Cursors.Hand;
                    _editor.Cursor = Cursor;
                    Invalidate();
                }
            }

            public string DisplayMember
            {
                get => _displayMember;
                set { _displayMember = value; SyncEditorText(); }
            }

            public object DataSource
            {
                get => _dataSource;
                set
                {
                    _dataSource = value;
                    _items.Replace(value as IEnumerable);
                    SelectedIndex = _items.Count == 0 ? -1 : 0;
                }
            }

            public int SelectedIndex
            {
                get => _selectedIndex;
                set
                {
                    var next = value < 0 || value >= _items.Count ? -1 : value;
                    if (_selectedIndex == next) { SyncEditorText(); return; }
                    _selectedIndex = next;
                    SyncEditorText();
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public object SelectedItem
            {
                get => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
                set
                {
                    var index = _items.IndexOf(value);
                    SelectedIndex = index;
                }
            }

            public override string Text
            {
                get => _editor == null ? base.Text : _editor.Text;
                set
                {
                    var next = value ?? string.Empty;
                    if (_editor == null) { base.Text = next; return; }
                    if (_editor.Text == next) return;
                    _syncingText = true;
                    try { _editor.Text = next; base.Text = next; }
                    finally { _syncingText = false; }
                    Invalidate();
                }
            }

            public event EventHandler SelectedIndexChanged;
            public void BeginUpdate() { }
            public void EndUpdate() { Invalidate(); }

            protected override void OnLayout(LayoutEventArgs eventArgs)
            {
                base.OnLayout(eventArgs);
                var textHeight = Math.Max(_editor.Font.Height + 4, _editor.PreferredHeight);
                var top = Math.Max(2, (ClientSize.Height - textHeight) / 2);
                _editor.SetBounds(10, top, Math.Max(1, ClientSize.Width - 44),
                    Math.Min(textHeight, ClientSize.Height - top - 2));
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                if (_editor != null) _editor.Font = Font;
                PerformLayout();
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                _editor.Enabled = Enabled;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                var bounds = new System.Drawing.Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var fill = new System.Drawing.SolidBrush(SurfaceRaised))
                using (var pen = new System.Drawing.Pen(Focused || _editor.Focused || (_popup != null && _popup.Visible) ? Accent : Border))
                {
                    if (SquareCorners)
                    {
                        e.Graphics.FillRectangle(fill, bounds);
                        e.Graphics.DrawRectangle(pen, bounds);
                    }
                    else
                    {
                        using (var path = RoundedRectangle(bounds, Math.Min(6, bounds.Height / 2)))
                        {
                            e.Graphics.FillPath(fill, path);
                            e.Graphics.DrawPath(pen, path);
                        }
                    }
                }
                if (_dropDownStyle == ComboBoxStyle.DropDownList)
                    TextRenderer.DrawText(e.Graphics, Text, Font,
                        new System.Drawing.Rectangle(10, 2, Math.Max(1, Width - 48), Math.Max(1, Height - 4)),
                        Enabled ? ForeColor : TextSecondary,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var centerX = Width - 17;
                var centerY = Height / 2;
                using (var pen = new System.Drawing.Pen(Enabled ? TextSecondary : Border, 1.4F))
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new System.Drawing.Point(centerX - 4, centerY - 2),
                        new System.Drawing.Point(centerX, centerY + 2),
                        new System.Drawing.Point(centerX + 4, centerY - 2)
                    });
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (_dropDownStyle != ComboBoxStyle.DropDownList || _items.Count == 0) return;
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                {
                    SelectedIndex = Math.Max(0, Math.Min(_items.Count - 1,
                        SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1)));
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    ShowDropDown();
                    e.Handled = true;
                }
            }

            protected override bool IsInputKey(Keys keyData)
            {
                if (_dropDownStyle == ComboBoxStyle.DropDownList)
                {
                    var key = keyData & Keys.KeyCode;
                    if (key == Keys.Up || key == Keys.Down || key == Keys.Enter || key == Keys.Space)
                        return true;
                }
                return base.IsInputKey(keyData);
            }

            private void ShowDropDown()
            {
                if (!Enabled || _items.Count == 0) return;
                if (_popup != null && !_popup.IsDisposed && _popup.Visible)
                {
                    CloseDropDown();
                    return;
                }
                CloseDropDown();
                var rowHeight = Math.Max(30, Font.Height + 14);
                var visibleRows = Math.Min(10, _items.Count);
                var list = new PopupSelectionList(_items.Values, DisplayText, _selectedIndex, rowHeight)
                {
                    Size = new System.Drawing.Size(Math.Max(Width, 120), visibleRows * rowHeight + 2),
                    Font = Font
                };
                _popup = new ToolStripDropDown
                {
                    AutoSize = false,
                    Padding = Padding.Empty,
                    Margin = Padding.Empty,
                    BackColor = SurfaceRaised,
                    DropShadowEnabled = true,
                    Size = list.Size
                };
                var host = new ToolStripControlHost(list)
                {
                    AutoSize = false,
                    Padding = Padding.Empty,
                    Margin = Padding.Empty,
                    Size = list.Size
                };
                list.ItemChosen += index =>
                {
                    SelectedIndex = index;
                    CloseDropDown();
                    if (_dropDownStyle == ComboBoxStyle.DropDownList) Focus();
                    else _editor.Focus();
                };
                var popup = _popup;
                popup.Closed += (sender, args) =>
                {
                    if (ReferenceEquals(_popup, popup)) _popup = null;
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() =>
                        {
                            popup.Dispose();
                            if (!IsDisposed) Invalidate();
                        }));
                    else popup.Dispose();
                };
                _popup.Items.Add(host);
                _popup.Show(this, new System.Drawing.Point(0, Height));
                list.Focus();
                Invalidate();
            }

            private void CloseDropDown()
            {
                if (_popup == null) return;
                var popup = _popup;
                _popup = null;
                if (!popup.IsDisposed) popup.Close();
                Invalidate();
            }

            private void SyncEditorText()
            {
                if (_editor == null) return;
                Text = DisplayText(SelectedItem);
            }

            private string DisplayText(object item)
            {
                if (item == null) return string.Empty;
                if (!string.IsNullOrWhiteSpace(_displayMember))
                {
                    var property = item.GetType().GetProperty(_displayMember);
                    if (property != null) return Convert.ToString(property.GetValue(item, null)) ?? string.Empty;
                }
                return Convert.ToString(item) ?? string.Empty;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) CloseDropDown();
                base.Dispose(disposing);
            }

            public sealed class ItemCollection
            {
                private readonly ThemedComboBox _owner;
                private readonly List<object> _values = new List<object>();
                internal ItemCollection(ThemedComboBox owner) { _owner = owner; }
                internal IList<object> Values => _values;
                public int Count => _values.Count;
                public object this[int index] => _values[index];
                public int Add(object item) { _values.Add(item); _owner.Invalidate(); return _values.Count - 1; }
                public void AddRange(object[] items) { if (items != null) _values.AddRange(items); _owner.Invalidate(); }
                public void Clear() { _values.Clear(); _owner.SelectedIndex = -1; _owner.Invalidate(); }
                public int IndexOf(object value) { return _values.IndexOf(value); }
                public bool Contains(object value) { return _values.Contains(value); }
                internal void Replace(IEnumerable source)
                {
                    _values.Clear();
                    if (source != null) foreach (var item in source) _values.Add(item);
                    _owner.Invalidate();
                }
            }
        }

        private sealed class PopupSelectionList : Control
        {
            private readonly IList<object> _items;
            private readonly Func<object, string> _display;
            private readonly int _rowHeight;
            private int _hoverIndex;
            private int _scrollIndex;

            public PopupSelectionList(IList<object> items, Func<object, string> display, int selectedIndex, int rowHeight)
            {
                _items = items;
                _display = display;
                _rowHeight = rowHeight;
                _hoverIndex = selectedIndex;
                _scrollIndex = Math.Max(0, Math.Min(selectedIndex, Math.Max(0, items.Count - 10)));
                BackColor = SurfaceRaised;
                ForeColor = TextPrimary;
                TabStop = true;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            }

            public event Action<int> ItemChosen;

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(SurfaceRaised);
                var visibleRows = Math.Max(1, (Height - 2) / _rowHeight);
                for (var row = 0; row < visibleRows; row++)
                {
                    var index = _scrollIndex + row;
                    if (index >= _items.Count) break;
                    var bounds = new System.Drawing.Rectangle(1, 1 + row * _rowHeight, Math.Max(1, Width - 2), _rowHeight);
                    if (index == _hoverIndex)
                        using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(25, 78, 137)))
                            e.Graphics.FillRectangle(fill, bounds);
                    TextRenderer.DrawText(e.Graphics, _display(_items[index]), Font,
                        new System.Drawing.Rectangle(bounds.X + 9, bounds.Y, Math.Max(1, bounds.Width - 18), bounds.Height),
                        TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                using (var pen = new System.Drawing.Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var index = _scrollIndex + Math.Max(0, (e.Y - 1) / _rowHeight);
                if (index >= _items.Count || index == _hoverIndex) return;
                _hoverIndex = index;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                var index = _scrollIndex + Math.Max(0, (e.Y - 1) / _rowHeight);
                if (index >= 0 && index < _items.Count) ItemChosen?.Invoke(index);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                var visibleRows = Math.Max(1, (Height - 2) / _rowHeight);
                var maximum = Math.Max(0, _items.Count - visibleRows);
                _scrollIndex = Math.Max(0, Math.Min(maximum, _scrollIndex + (e.Delta < 0 ? 3 : -3)));
                Invalidate();
            }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Enter || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Enter && _hoverIndex >= 0 && _hoverIndex < _items.Count)
                {
                    ItemChosen?.Invoke(_hoverIndex);
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
                _hoverIndex = Math.Max(0, Math.Min(_items.Count - 1, _hoverIndex + (e.KeyCode == Keys.Down ? 1 : -1)));
                var visibleRows = Math.Max(1, (Height - 2) / _rowHeight);
                if (_hoverIndex < _scrollIndex) _scrollIndex = _hoverIndex;
                if (_hoverIndex >= _scrollIndex + visibleRows) _scrollIndex = _hoverIndex - visibleRows + 1;
                Invalidate();
                e.Handled = true;
            }
        }

        // Keep native wheel/drag/accessibility behaviour and repaint only the
        // scrollbar chrome so no light Windows bars leak into the dark editor.
        private sealed class NativeScrollTheme : NativeWindow
        {
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme> Windows
                = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme>();
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme> Dropdowns
                = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme>();
            private bool _painting;
            private bool _queuedPaint;
            private readonly Control _owner;
            private const int RepaintScrollbars = 0x8000 + 421;
            private readonly Timer _trackingPaint = new Timer { Interval = 30 };

            [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, int message, IntPtr word, IntPtr longValue);
            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr handle, string application, string identifier);
            [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr handle, int identifier, ref ScrollInfo info);
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
            [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr handle);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr deviceContext);
            [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr handle, IntPtr rectangle, IntPtr region, uint flags);
            [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr handle, ref ComboInfo info);

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            private struct ScrollInfo
            {
                public int Size;
                public NativeRect Bounds;
                public int Arrow, ThumbStart, ThumbEnd, Reserved;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] States;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ComboInfo
            {
                public int Size;
                public NativeRect Item, Button;
                public int State;
                public IntPtr Combo, Edit, List;
            }

            private NativeScrollTheme(Control owner, bool popup)
            {
                _owner = owner;
                _trackingPaint.Tick += (sender, args) =>
                {
                    if (Handle == IntPtr.Zero) { _trackingPaint.Stop(); return; }
                    var pointerOverBar = false;
                    foreach (var vertical in new[] { true, false })
                    {
                        ScrollInfo info;
                        NativeRect window;
                        if (Read(Handle, vertical, out info, out window)
                            && System.Drawing.Rectangle.FromLTRB(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom).Contains(Cursor.Position))
                            pointerOverBar = true;
                    }
                    PaintBars(IntPtr.Zero);
                    if (!pointerOverBar && Control.MouseButtons == MouseButtons.None) _trackingPaint.Stop();
                };
                if (!popup)
                {
                    owner.HandleCreated += (sender, args) => Bind(owner.Handle);
                    if (owner.IsHandleCreated) Bind(owner.Handle);
                }
                owner.HandleDestroyed += (sender, args) => { _trackingPaint.Stop(); if (Handle != IntPtr.Zero) ReleaseHandle(); };
                owner.Disposed += (sender, args) => { _trackingPaint.Dispose(); if (Handle != IntPtr.Zero) ReleaseHandle(); };
            }

            private void Bind(IntPtr handle)
            {
                if (Handle == handle) return;
                if (Handle != IntPtr.Zero) ReleaseHandle();
                if (handle == IntPtr.Zero) return;
                AssignHandle(handle);
                SetWindowTheme(handle, string.Empty, string.Empty);
            }

            public static void Attach(Control control)
            {
                var window = Windows.GetValue(control, item => new NativeScrollTheme(item, false));
                if (window.Handle != IntPtr.Zero) RedrawWindow(window.Handle, IntPtr.Zero, IntPtr.Zero, 0x401);
            }

            public static void AttachDropdown(ComboBox combo)
            {
                var info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) };
                if (GetComboBoxInfo(combo.Handle, ref info) && info.List != IntPtr.Zero)
                    Dropdowns.GetValue(combo, item => new NativeScrollTheme(item, true)).Bind(info.List);
            }

            private static bool Read(IntPtr handle, bool vertical, out ScrollInfo info, out NativeRect window)
            {
                info = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), States = new int[6] };
                window = new NativeRect();
                return GetWindowRect(handle, out window)
                    && GetScrollBarInfo(handle, vertical ? -5 : -6, ref info)
                    && (info.States[0] & 0x18000) == 0
                    && info.Bounds.Right > info.Bounds.Left && info.Bounds.Bottom > info.Bounds.Top;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == RepaintScrollbars)
                {
                    _queuedPaint = false;
                    if (!_painting && Handle != IntPtr.Zero) PaintBars(IntPtr.Zero);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == 0xA0 || m.Msg == 0xA1 || m.Msg == 0xA3) _trackingPaint.Start();
                base.WndProc(ref m);
                if (_painting || Handle == IntPtr.Zero) return;
                if ((m.Msg == 5 || m.Msg == 0x47 || m.Msg == 0x7D) && !_queuedPaint)
                {
                    _queuedPaint = true;
                    PostMessage(Handle, RepaintScrollbars, IntPtr.Zero, IntPtr.Zero);
                }
                if (m.Msg == 0x85 || m.Msg == 0xF || m.Msg == 0x114 || m.Msg == 0x115
                    || m.Msg == 0xA0 || m.Msg == 0x200 || m.Msg == 0x202 || m.Msg == 0x20A
                    || m.Msg == 0xA2 || m.Msg == 0x317 || m.Msg == 0x2A2 || m.Msg == 0x113)
                {
                    _painting = true;
                    try { PaintBars(m.Msg == 0x317 ? m.WParam : IntPtr.Zero); }
                    finally { _painting = false; }
                }
            }

            private void PaintBars(IntPtr printDeviceContext)
            {
                var deviceContext = printDeviceContext == IntPtr.Zero ? GetWindowDC(Handle) : printDeviceContext;
                if (deviceContext == IntPtr.Zero) return;
                try
                {
                    using (var graphics = System.Drawing.Graphics.FromHdc(deviceContext))
                    {
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        var vertical = DrawBar(graphics, true);
                        var horizontal = DrawBar(graphics, false);
                        if (!vertical.IsEmpty && !horizontal.IsEmpty)
                            using (var fill = new System.Drawing.SolidBrush(Canvas))
                                graphics.FillRectangle(fill, vertical.Left, horizontal.Top, vertical.Width, horizontal.Height);
                    }
                }
                finally { if (printDeviceContext == IntPtr.Zero) ReleaseDC(Handle, deviceContext); }
            }

            private System.Drawing.Rectangle DrawBar(System.Drawing.Graphics graphics, bool vertical)
            {
                ScrollInfo info;
                NativeRect window;
                if (!Read(Handle, vertical, out info, out window)) return System.Drawing.Rectangle.Empty;
                var bounds = new System.Drawing.Rectangle(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top,
                    info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top);
                var background = _owner == null || _owner.IsDisposed ? Surface : _owner.BackColor;
                using (var fill = new System.Drawing.SolidBrush(background))
                    graphics.FillRectangle(fill, System.Drawing.Rectangle.Inflate(bounds, 1, 1));
                var thickness = vertical ? bounds.Width : bounds.Height;
                var inset = Math.Max(3, thickness / 4);
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 61, 78), 2F))
                {
                    if (vertical)
                        graphics.DrawLine(track, bounds.Left + bounds.Width / 2, bounds.Top + 3, bounds.Left + bounds.Width / 2, bounds.Bottom - 4);
                    else
                        graphics.DrawLine(track, bounds.Left + 3, bounds.Top + bounds.Height / 2, bounds.Right - 4, bounds.Top + bounds.Height / 2);
                }
                var thumb = vertical
                    ? new System.Drawing.Rectangle(bounds.Left + inset, bounds.Top + info.ThumbStart,
                        Math.Max(1, bounds.Width - inset * 2), info.ThumbEnd - info.ThumbStart)
                    : new System.Drawing.Rectangle(bounds.Left + info.ThumbStart, bounds.Top + inset,
                        info.ThumbEnd - info.ThumbStart, Math.Max(1, bounds.Height - inset * 2));
                if (thumb.Width > 0 && thumb.Height > 0 && (info.States[3] & 0x8001) == 0)
                    using (var path = RoundedRectangle(thumb, Math.Max(1, Math.Min(thumb.Width, thumb.Height) / 2)))
                    using (var fill = new System.Drawing.SolidBrush(TextSecondary)) graphics.FillPath(fill, path);
                return bounds;
            }
        }

        private sealed class ThemedCheckedListBox : CheckedListBox
        {
            private int _horizontalOffset;

            public bool CustomHorizontalScroll { get; set; }

            public int HorizontalContentWidth
            {
                get
                {
                    var width = 0;
                    using (var graphics = CreateGraphics())
                        foreach (var item in Items)
                            width = Math.Max(width, TextRenderer.MeasureText(graphics, GetItemText(item), Font).Width + 58);
                    return width;
                }
            }

            public int HorizontalMaximum => Math.Max(0, HorizontalContentWidth - Math.Max(1, ClientSize.Width));

            public int HorizontalOffset
            {
                get => Math.Min(_horizontalOffset, HorizontalMaximum);
                set
                {
                    var next = Math.Max(0, Math.Min(HorizontalMaximum, value));
                    if (_horizontalOffset == next) return;
                    _horizontalOffset = next;
                    Invalidate();
                }
            }

            public ThemedCheckedListBox()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
                ItemHeight = 28;
                CheckOnClick = true;
                IntegralHeight = false;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.Style &= ~0x00300000; // WS_HSCROLL | WS_VSCROLL
                    return parameters;
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (Items.Count == 0 || e.Delta == 0) return;
                var visible = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
                var maximum = Math.Max(0, Items.Count - visible);
                TopIndex = Math.Max(0, Math.Min(maximum, TopIndex + (e.Delta < 0 ? 3 : -3)));
                Invalidate();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                ItemHeight = Math.Max(26, Font.Height + 10);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                var selected = (e.State & DrawItemState.Selected) != 0;
                PaintListSelection(e.Graphics, e.Bounds, BackColor, selected);

                const int switchWidth = 34;
                const int switchHeight = 18;
                var switchX = Math.Max(e.Bounds.Left + 4, e.Bounds.Right - switchWidth - 7);
                var switchY = e.Bounds.Top + Math.Max(1, (e.Bounds.Height - switchHeight) / 2);
                var offset = CustomHorizontalScroll ? HorizontalOffset : 0;
                var textBounds = new System.Drawing.Rectangle(e.Bounds.Left + 6 - offset, e.Bounds.Top,
                    Math.Max(1, switchX - e.Bounds.Left - 12 + offset), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, textBounds,
                    Enabled ? TextPrimary : TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                var isChecked = GetItemChecked(e.Index);
                var switchBounds = new System.Drawing.Rectangle(switchX, switchY, switchWidth, switchHeight);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRectangle(switchBounds, switchHeight / 2))
                using (var fill = new System.Drawing.SolidBrush(isChecked
                    ? System.Drawing.Color.FromArgb(0, 112, 255)
                    : System.Drawing.Color.FromArgb(78, 94, 111)))
                    e.Graphics.FillPath(fill, path);
                var knobX = isChecked ? switchX + switchWidth - switchHeight + 2 : switchX + 2;
                using (var knob = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                    e.Graphics.FillEllipse(knob, knobX, switchY + 2, switchHeight - 4, switchHeight - 4);
            }

            protected override void OnItemCheck(ItemCheckEventArgs ice)
            {
                base.OnItemCheck(ice);
                if (IsHandleCreated) BeginInvoke(new Action(Invalidate));
            }
        }

        private sealed class ToggleSwitch : CheckBox
        {
            public ToggleSwitch()
            {
                AutoSize = false;
                Dock = DockStyle.Top;
                Height = 34;
                MinimumSize = new System.Drawing.Size(90, 34);
                Cursor = Cursors.Hand;
                Margin = new Padding(0, 4, 0, 4);
                ForeColor = TextPrimary;
                BackColor = Surface;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            public override System.Drawing.Size GetPreferredSize(System.Drawing.Size proposedSize)
            {
                var width = proposedSize.Width > 1 && proposedSize.Width < 10000
                    ? proposedSize.Width
                    : Parent == null ? 300 : Math.Max(90, Parent.ClientSize.Width - Parent.Padding.Horizontal);
                return new System.Drawing.Size(width, 34);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                const int switchWidth = 40;
                const int switchHeight = 20;
                var x = Math.Max(2, Width - switchWidth - 2);
                var y = Math.Max(1, (Height - switchHeight) / 2);
                var switchBounds = new System.Drawing.Rectangle(x, y, switchWidth, switchHeight);
                using (var path = RoundedRectangle(switchBounds, switchHeight / 2))
                using (var fill = new System.Drawing.SolidBrush(Checked && Enabled
                    ? System.Drawing.Color.FromArgb(0, 112, 255)
                    : System.Drawing.Color.FromArgb(78, 94, 111)))
                    e.Graphics.FillPath(fill, path);
                var knobX = Checked ? x + switchWidth - switchHeight + 2 : x + 2;
                using (var knob = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                    e.Graphics.FillEllipse(knob, knobX, y + 2, switchHeight - 4, switchHeight - 4);
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new System.Drawing.Rectangle(0, 0, Math.Max(1, x - 10), Height),
                    Enabled ? ForeColor : TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
            }
        }

        internal sealed class DarkGridScrollBar : Control
        {
            private readonly DataGridView _grid;
            private readonly bool _vertical;
            private bool _dragging;
            private bool _hovered;
            private int _dragStart;
            private int _valueStart;

            public DarkGridScrollBar(DataGridView grid, bool vertical)
            {
                _grid = grid;
                _vertical = vertical;
                Margin = Padding.Empty;
                BackColor = Surface;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                _grid.Scroll += (sender, args) => Invalidate();
                _grid.Resize += (sender, args) => Invalidate();
                _grid.ColumnWidthChanged += (sender, args) => Invalidate();
                _grid.ColumnDisplayIndexChanged += (sender, args) => Invalidate();
                _grid.RowsAdded += (sender, args) => Invalidate();
                _grid.RowsRemoved += (sender, args) => Invalidate();
                _grid.DataBindingComplete += (sender, args) => Invalidate();
                _grid.MouseWheel += (sender, args) =>
                {
                    if (IsHandleCreated)
                        BeginInvoke(new Action(Invalidate));
                    else Invalidate();
                };
                _grid.MouseUp += (sender, args) => Invalidate();
                _grid.KeyUp += (sender, args) => Invalidate();
            }

            private int Maximum
            {
                get
                {
                    if (_vertical)
                    {
                        if (_grid.Rows.Count == 0) return 0;
                        var displayed = Math.Max(1, _grid.DisplayedRowCount(false));
                        return Math.Max(0, _grid.Rows.Count - displayed);
                    }
                    var total = _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width);
                    return Math.Max(0, total - Math.Max(1, _grid.DisplayRectangle.Width));
                }
            }

            private int Value
            {
                get
                {
                    if (_vertical)
                        return _grid.Rows.Count == 0 || _grid.FirstDisplayedScrollingRowIndex < 0
                            ? 0 : Math.Min(Maximum, _grid.FirstDisplayedScrollingRowIndex);
                    return Math.Min(Maximum, Math.Max(0, _grid.HorizontalScrollingOffset));
                }
                set
                {
                    var next = Math.Max(0, Math.Min(Maximum, value));
                    if (next == Value) return;
                    try
                    {
                        if (_vertical)
                        {
                            if (_grid.Rows.Count > 0 && next < _grid.Rows.Count)
                                _grid.FirstDisplayedScrollingRowIndex = next;
                        }
                        else _grid.HorizontalScrollingOffset = next;
                    }
                    catch (InvalidOperationException) { }
                    Invalidate();
                }
            }

            private System.Drawing.Rectangle ThumbBounds()
            {
                var maximum = Maximum;
                var length = _vertical ? Height : Width;
                var thickness = _vertical ? Width : Height;
                if (maximum <= 0 || length <= 0 || thickness <= 0) return System.Drawing.Rectangle.Empty;
                var viewport = _vertical ? Math.Max(1, _grid.DisplayedRowCount(false)) : Math.Max(1, _grid.DisplayRectangle.Width);
                var content = _vertical ? Math.Max(1, _grid.Rows.Count) : viewport + maximum;
                var thumbLength = Math.Min(length, Math.Max(36, (int)Math.Round(length * viewport / (double)content)));
                var travel = Math.Max(1, length - thumbLength);
                var position = (int)Math.Round(travel * Value / (double)maximum);
                return _vertical
                    ? new System.Drawing.Rectangle(4, position, Math.Max(1, thickness - 8), thumbLength)
                    : new System.Drawing.Rectangle(position, 4, thumbLength, Math.Max(1, thickness - 8));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                if (Maximum <= 0) return;
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 61, 78), 2F))
                {
                    if (_vertical) e.Graphics.DrawLine(track, Width / 2, 3, Width / 2, Height - 4);
                    else e.Graphics.DrawLine(track, 3, Height / 2, Width - 4, Height / 2);
                }
                var thumb = ThumbBounds();
                if (thumb.IsEmpty) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRectangle(thumb, Math.Max(2, Math.Min(thumb.Width, thumb.Height) / 2)))
                using (var fill = new System.Drawing.SolidBrush(_dragging || _hovered
                    ? System.Drawing.Color.FromArgb(133, 164, 186)
                    : System.Drawing.Color.FromArgb(91, 119, 140)))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hovered = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                if (!_dragging) _hovered = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || Maximum <= 0) return;
                var thumb = ThumbBounds();
                var coordinate = _vertical ? e.Y : e.X;
                if (thumb.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStart = coordinate;
                    _valueStart = Value;
                    Capture = true;
                    return;
                }
                var before = _vertical ? coordinate < thumb.Top : coordinate < thumb.Left;
                var page = _vertical ? Math.Max(1, _grid.DisplayedRowCount(false) - 1) : Math.Max(40, _grid.DisplayRectangle.Width - 40);
                Value += before ? -page : page;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging || Maximum <= 0) return;
                var thumb = ThumbBounds();
                var length = _vertical ? Height : Width;
                var thumbLength = _vertical ? thumb.Height : thumb.Width;
                var travel = Math.Max(1, length - thumbLength);
                var coordinate = _vertical ? e.Y : e.X;
                Value = _valueStart + (int)Math.Round((coordinate - _dragStart) * Maximum / (double)travel);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _dragging = false;
                _hovered = ClientRectangle.Contains(e.Location);
                Capture = false;
                Invalidate();
                base.OnMouseUp(e);
            }
        }

        internal sealed class BufferedDataGridView : DataGridView
        {
            public BufferedDataGridView()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (Rows.Count == 0 || e.Delta == 0) return;
                var current = FirstDisplayedScrollingRowIndex;
                if (current < 0) current = 0;
                var visibleRows = Math.Max(1, DisplayedRowCount(false));
                var rowsPerNotch = Math.Max(3, visibleRows / 3);
                var notches = Math.Max(1, Math.Abs(e.Delta) / SystemInformation.MouseWheelScrollDelta);
                var next = current + (e.Delta < 0 ? 1 : -1) * rowsPerNotch * notches;
                next = Math.Max(0, Math.Min(Math.Max(0, Rows.Count - visibleRows), next));
                if (next != current)
                {
                    try { FirstDisplayedScrollingRowIndex = next; }
                    catch (InvalidOperationException) { }
                }
            }
        }

        internal class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }
        }

        private class BufferedTableLayoutPanel : TableLayoutPanel
        {
            public BufferedTableLayoutPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }
        }

        private sealed class RoundedTextInputHost : BufferedPanel
        {
            private readonly TextBox _input;
            private readonly Label _placeholder;

            public RoundedTextInputHost(TextBox input, string placeholder)
            {
                _input = input;
                Padding = Padding.Empty;
                _input.AutoSize = false;
                _input.BorderStyle = BorderStyle.None;
                _input.BackColor = SurfaceRaised;
                _input.ForeColor = TextPrimary;
                _input.Margin = Padding.Empty;
                Controls.Add(_input);

                if (!string.IsNullOrWhiteSpace(placeholder))
                {
                    _placeholder = new Label
                    {
                        Text = placeholder,
                        AutoSize = false,
                        BackColor = SurfaceRaised,
                        ForeColor = TextSecondary,
                        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                        Cursor = Cursors.IBeam,
                        Margin = Padding.Empty
                    };
                    _placeholder.Click += (sender, args) => _input.Focus();
                    Controls.Add(_placeholder);
                    _placeholder.BringToFront();
                }

                _input.TextChanged += (sender, args) => UpdatePlaceholder();
                _input.Enter += (sender, args) => { UpdatePlaceholder(); Invalidate(); };
                _input.Leave += (sender, args) => { UpdatePlaceholder(); Invalidate(); };
                _input.FontChanged += (sender, args) => PerformLayout();
                UpdatePlaceholder();
            }

            protected override void OnLayout(LayoutEventArgs eventArgs)
            {
                base.OnLayout(eventArgs);
                var textHeight = Math.Max(_input.Font.Height + 4, _input.PreferredHeight);
                var top = Math.Max(2, (ClientSize.Height - textHeight) / 2);
                var bounds = new System.Drawing.Rectangle(10, top,
                    Math.Max(1, ClientSize.Width - 20), Math.Min(textHeight, ClientSize.Height - top - 2));
                _input.Bounds = bounds;
                if (_placeholder != null)
                {
                    _placeholder.Font = _input.Font;
                    _placeholder.SetBounds(bounds.Left, 2, bounds.Width, Math.Max(1, ClientSize.Height - 4));
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                var bounds = new System.Drawing.Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = RoundedRectangle(bounds, Math.Min(6, bounds.Height / 2)))
                using (var fill = new System.Drawing.SolidBrush(SurfaceRaised))
                using (var pen = new System.Drawing.Pen(_input.Focused ? Accent : Border))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(pen, path);
                }
            }

            private void UpdatePlaceholder()
            {
                if (_placeholder != null)
                    _placeholder.Visible = string.IsNullOrEmpty(_input.Text) && !_input.Focused;
            }
        }

        private sealed class RoundedListHost : BufferedPanel
        {
            private readonly ListBox _list;
            private readonly DarkListScrollBar _horizontalScrollBar;
            private readonly DarkVerticalListScrollBar _verticalScrollBar;

            public RoundedListHost(ListBox list)
            {
                Padding = new Padding(7);
                ResizeRedraw = true;
                _list = list;
                _list.Dock = DockStyle.None;
                _verticalScrollBar = new DarkVerticalListScrollBar(_list) { Width = 20 };
                var themed = list as ThemedCheckedListBox;
                if (themed != null && themed.CustomHorizontalScroll)
                    _horizontalScrollBar = new DarkListScrollBar(themed) { Height = 20 };
                Controls.Add(_list);
                Controls.Add(_verticalScrollBar);
                if (_horizontalScrollBar != null) Controls.Add(_horizontalScrollBar);
                _list.Invalidated += (sender, args) => UpdateScrollLayout();
                _list.MouseWheel += (sender, args) => { _verticalScrollBar.Invalidate(); if (_horizontalScrollBar != null) _horizontalScrollBar.Invalidate(); };
            }

            protected override void OnLayout(LayoutEventArgs eventArgs)
            {
                base.OnLayout(eventArgs);
                UpdateScrollLayout();
            }

            private void UpdateScrollLayout()
            {
                if (_list == null || _verticalScrollBar == null) return;
                var innerWidth = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                var innerHeight = Math.Max(1, ClientSize.Height - Padding.Vertical);
                var showVertical = false;
                var showHorizontal = false;
                for (var pass = 0; pass < 3; pass++)
                {
                    var availableWidth = Math.Max(1, innerWidth - (showVertical ? 20 : 0));
                    showHorizontal = _horizontalScrollBar != null
                        && ((ThemedCheckedListBox)_list).HorizontalContentWidth > availableWidth;
                    var availableHeight = Math.Max(1, innerHeight - (showHorizontal ? 20 : 0));
                    var visibleItems = Math.Max(1, availableHeight / Math.Max(1, _list.ItemHeight));
                    showVertical = _list.Items.Count > visibleItems;
                }
                var verticalWidth = showVertical ? 20 : 0;
                var listWidth = Math.Max(1, innerWidth - verticalWidth);
                var barHeight = showHorizontal ? 20 : 0;
                _list.SetBounds(Padding.Left, Padding.Top, listWidth, Math.Max(1, innerHeight - barHeight));
                _verticalScrollBar.Visible = showVertical;
                _verticalScrollBar.SetBounds(Padding.Left + listWidth, Padding.Top, verticalWidth, _list.Height);
                _verticalScrollBar.Invalidate();
                if (_horizontalScrollBar != null)
                {
                    _horizontalScrollBar.Visible = showHorizontal;
                    _horizontalScrollBar.SetBounds(Padding.Left, Padding.Top + _list.Height, listWidth, barHeight);
                    _horizontalScrollBar.Invalidate();
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                var bounds = new System.Drawing.Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = RoundedRectangle(bounds, Math.Min(7, bounds.Height / 2)))
                using (var fill = new System.Drawing.SolidBrush(Surface))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var bounds = new System.Drawing.Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = RoundedRectangle(bounds, Math.Min(7, bounds.Height / 2)))
                using (var pen = new System.Drawing.Pen(Border))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        private sealed class DarkListScrollBar : Control
        {
            private readonly ThemedCheckedListBox _list;
            private bool _dragging;
            private bool _hovered;
            private int _dragStart;
            private int _valueStart;

            public DarkListScrollBar(ThemedCheckedListBox list)
            {
                _list = list;
                BackColor = Surface;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            private int Maximum => _list.HorizontalMaximum;

            private int Value
            {
                get => _list.HorizontalOffset;
                set { _list.HorizontalOffset = value; Invalidate(); }
            }

            private System.Drawing.Rectangle ThumbBounds()
            {
                var maximum = Maximum;
                if (maximum <= 0 || Width <= 0 || Height <= 0) return System.Drawing.Rectangle.Empty;
                var viewport = Math.Max(1, _list.ClientSize.Width);
                var content = viewport + maximum;
                var thumbWidth = Math.Min(Width, Math.Max(36, (int)Math.Round(Width * viewport / (double)content)));
                var travel = Math.Max(1, Width - thumbWidth);
                var x = (int)Math.Round(travel * Value / (double)maximum);
                return new System.Drawing.Rectangle(x, 6, thumbWidth, Math.Max(1, Height - 12));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                if (Maximum <= 0) return;
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 61, 78), 2F))
                    e.Graphics.DrawLine(track, 3, Height / 2, Width - 4, Height / 2);
                var thumb = ThumbBounds();
                if (thumb.IsEmpty) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRectangle(thumb, Math.Max(2, thumb.Height / 2)))
                using (var fill = new System.Drawing.SolidBrush(_dragging || _hovered
                    ? System.Drawing.Color.FromArgb(133, 164, 186)
                    : System.Drawing.Color.FromArgb(91, 119, 140)))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { if (!_dragging) _hovered = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || Maximum <= 0) return;
                var thumb = ThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStart = e.X;
                    _valueStart = Value;
                    Capture = true;
                }
                else Value += e.X < thumb.Left ? -Math.Max(24, _list.ClientSize.Width - 24) : Math.Max(24, _list.ClientSize.Width - 24);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging || Maximum <= 0) return;
                var thumb = ThumbBounds();
                var travel = Math.Max(1, Width - thumb.Width);
                Value = _valueStart + (int)Math.Round((e.X - _dragStart) * Maximum / (double)travel);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _dragging = false;
                _hovered = ClientRectangle.Contains(e.Location);
                Capture = false;
                Invalidate();
                base.OnMouseUp(e);
            }
        }

        private sealed class DarkVerticalListScrollBar : Control
        {
            private readonly ListBox _list;
            private bool _dragging;
            private bool _hovered;
            private int _dragStart;
            private int _valueStart;

            public DarkVerticalListScrollBar(ListBox list)
            {
                _list = list;
                BackColor = Surface;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            private int VisibleItems => Math.Max(1, _list.ClientSize.Height / Math.Max(1, _list.ItemHeight));
            private int Maximum => Math.Max(0, _list.Items.Count - VisibleItems);
            private int Value
            {
                get => Math.Min(Maximum, Math.Max(0, _list.TopIndex));
                set { _list.TopIndex = Math.Max(0, Math.Min(Maximum, value)); _list.Invalidate(); Invalidate(); }
            }

            private System.Drawing.Rectangle ThumbBounds()
            {
                if (Maximum <= 0 || Width <= 0 || Height <= 0) return System.Drawing.Rectangle.Empty;
                var thumbHeight = Math.Min(Height, Math.Max(36, (int)Math.Round(Height * VisibleItems / (double)Math.Max(1, _list.Items.Count))));
                var travel = Math.Max(1, Height - thumbHeight);
                var y = (int)Math.Round(travel * Value / (double)Maximum);
                return new System.Drawing.Rectangle(6, y, Math.Max(1, Width - 12), thumbHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                if (Maximum <= 0) return;
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 61, 78), 2F))
                    e.Graphics.DrawLine(track, Width / 2, 3, Width / 2, Height - 4);
                var thumb = ThumbBounds();
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRectangle(thumb, Math.Max(2, thumb.Width / 2)))
                using (var fill = new System.Drawing.SolidBrush(_dragging || _hovered
                    ? System.Drawing.Color.FromArgb(133, 164, 186)
                    : System.Drawing.Color.FromArgb(91, 119, 140)))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { if (!_dragging) _hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || Maximum <= 0) return;
                var thumb = ThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _dragging = true; _dragStart = e.Y; _valueStart = Value; Capture = true;
                }
                else Value += e.Y < thumb.Top ? -Math.Max(1, VisibleItems - 1) : Math.Max(1, VisibleItems - 1);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging || Maximum <= 0) return;
                var thumb = ThumbBounds();
                Value = _valueStart + (int)Math.Round((e.Y - _dragStart) * Maximum / (double)Math.Max(1, Height - thumb.Height));
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                _dragging = false; _hovered = ClientRectangle.Contains(e.Location); Capture = false; Invalidate(); base.OnMouseUp(e);
            }
        }

        private sealed class SmoothListBox : ListBox
        {
            public SmoothListBox()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
                ItemHeight = 28;
                IntegralHeight = false;
                BorderStyle = BorderStyle.None;
                BackColor = Surface;
                ForeColor = TextPrimary;
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                ItemHeight = Math.Max(26, Font.Height + 10);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                PaintListSelection(e.Graphics, e.Bounds, BackColor,
                    (e.State & DrawItemState.Selected) != 0);
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                    new System.Drawing.Rectangle(e.Bounds.Left + 7, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 14), e.Bounds.Height),
                    Enabled ? ForeColor : TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.Style &= ~0x00300000; // WS_HSCROLL | WS_VSCROLL
                    return parameters;
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (Items.Count == 0 || e.Delta == 0) return;
                var visible = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
                var maximum = Math.Max(0, Items.Count - visible);
                TopIndex = Math.Max(0, Math.Min(maximum, TopIndex + (e.Delta < 0 ? 3 : -3)));
                Invalidate();
            }
        }

        private sealed class DarkScrollHost : BufferedPanel
        {
            private readonly Control _content;
            private readonly DarkContentScrollBar _scrollBar;
            private int _offset;

            public DarkScrollHost(Control content)
            {
                _content = content;
                _content.Dock = DockStyle.None;
                _scrollBar = new DarkContentScrollBar(this) { Width = 20 };
                Controls.Add(_content);
                Controls.Add(_scrollBar);
                AttachWheel(_content);
            }

            public int Maximum => Math.Max(0, _content.GetPreferredSize(new System.Drawing.Size(Math.Max(1, ClientSize.Width - 20), 0)).Height - ClientSize.Height);
            public int Offset
            {
                get => Math.Min(_offset, Maximum);
                set { _offset = Math.Max(0, Math.Min(Maximum, value)); LayoutContent(); Invalidate(); }
            }
            internal int ViewportHeight => Math.Max(1, ClientSize.Height);

            protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutContent(); }

            private void LayoutContent()
            {
                if (_content == null || _scrollBar == null) return;
                var show = Maximum > 0;
                var barWidth = show ? 20 : 0;
                var width = Math.Max(1, ClientSize.Width - barWidth);
                var preferredHeight = Math.Max(ClientSize.Height, _content.GetPreferredSize(new System.Drawing.Size(width, 0)).Height);
                _offset = Math.Max(0, Math.Min(Math.Max(0, preferredHeight - ClientSize.Height), _offset));
                _content.SetBounds(0, -_offset, width, preferredHeight);
                _scrollBar.Visible = show;
                _scrollBar.SetBounds(width, 0, barWidth, ClientSize.Height);
                _scrollBar.Invalidate();
            }

            private void AttachWheel(Control control)
            {
                control.MouseWheel += (sender, args) =>
                {
                    Offset += args.Delta < 0 ? 72 : -72;
                    if (args is HandledMouseEventArgs handled) handled.Handled = true;
                };
                control.ControlAdded += (sender, args) => AttachWheel(args.Control);
                foreach (Control child in control.Controls) AttachWheel(child);
            }
        }

        private sealed class DarkContentScrollBar : Control
        {
            private readonly DarkScrollHost _host;
            private bool _dragging;
            private int _dragStart;
            private int _valueStart;
            public DarkContentScrollBar(DarkScrollHost host) { _host = host; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            private System.Drawing.Rectangle ThumbBounds()
            {
                if (_host.Maximum <= 0) return System.Drawing.Rectangle.Empty;
                var content = _host.ViewportHeight + _host.Maximum;
                var thumbHeight = Math.Min(Height, Math.Max(36, (int)Math.Round(Height * _host.ViewportHeight / (double)content)));
                var y = (int)Math.Round(Math.Max(1, Height - thumbHeight) * _host.Offset / (double)_host.Maximum);
                return new System.Drawing.Rectangle(6, y, Math.Max(1, Width - 12), thumbHeight);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Surface : Parent.BackColor);
                if (_host.Maximum <= 0) return;
                using (var track = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 61, 78), 2F)) e.Graphics.DrawLine(track, Width / 2, 3, Width / 2, Height - 4);
                var thumb = ThumbBounds(); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRectangle(thumb, Math.Max(2, thumb.Width / 2))) using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(91, 119, 140))) e.Graphics.FillPath(fill, path);
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); if (e.Button != MouseButtons.Left || _host.Maximum <= 0) return; var thumb = ThumbBounds();
                if (thumb.Contains(e.Location)) { _dragging = true; _dragStart = e.Y; _valueStart = _host.Offset; Capture = true; }
                else _host.Offset += e.Y < thumb.Top ? -_host.ViewportHeight : _host.ViewportHeight;
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); if (!_dragging || _host.Maximum <= 0) return; var thumb = ThumbBounds();
                _host.Offset = _valueStart + (int)Math.Round((e.Y - _dragStart) * _host.Maximum / (double)Math.Max(1, Height - thumb.Height));
            }
            protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
        }

        private sealed class RoundedPanel : BufferedPanel
        {
            public System.Drawing.Color BorderColor { get; set; }
            public int CornerRadius { get; set; }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                PaintRoundedCardBackground(e.Graphics, this, CornerRadius);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                DrawRoundedBorder(e.Graphics, ClientRectangle, BorderColor, CornerRadius);
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                Invalidate();
            }
        }

        private sealed class RoundedTableLayoutPanel : BufferedTableLayoutPanel
        {
            public System.Drawing.Color BorderColor { get; set; }
            public int CornerRadius { get; set; }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                PaintRoundedCardBackground(e.Graphics, this, CornerRadius);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                DrawRoundedBorder(e.Graphics, ClientRectangle, BorderColor, CornerRadius);
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                Invalidate();
            }
        }

        private static void PaintRoundedCardBackground(System.Drawing.Graphics graphics, Control control, int radius)
        {
            graphics.Clear(control.Parent == null ? Canvas : control.Parent.BackColor);
            if (control.Width < 3 || control.Height < 3) return;
            var bounds = new System.Drawing.Rectangle(1, 1, control.Width - 3, control.Height - 3);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = RoundedRectangle(bounds, Math.Max(2, radius)))
            using (var fill = new System.Drawing.SolidBrush(control.BackColor))
                graphics.FillPath(fill, path);
        }

        private static void DrawRoundedBorder(System.Drawing.Graphics graphics, System.Drawing.Rectangle bounds, System.Drawing.Color color, int radius)
        {
            if (bounds.Width < 3 || bounds.Height < 3) return;
            var rectangle = new System.Drawing.Rectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
            using (var path = RoundedRectangle(rectangle, Math.Max(2, radius)))
            using (var pen = new System.Drawing.Pen(color))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.DrawPath(pen, path);
            }
        }

        private sealed class DwgSheetPreviewPanel : BufferedPanel
        {
            private const int MaximumCachedPreviews = 40;
            private SheetItem _sheet;
            private System.Drawing.Bitmap _preview;
            private string _previewKey;
            private string _message;
            private readonly Dictionary<string, System.Drawing.Bitmap> _previewCache = new Dictionary<string, System.Drawing.Bitmap>(StringComparer.Ordinal);
            private readonly LinkedList<string> _previewOrder = new LinkedList<string>();

            public DwgSheetPreviewPanel()
            {
                BackColor = System.Drawing.Color.FromArgb(18, 29, 39);
                BorderStyle = BorderStyle.None;
                Cursor = Cursors.Default;
            }

            public void ShowSheet(SheetItem sheet, string previewKey)
            {
                _sheet = sheet;
                _previewKey = previewKey;
                _preview = FindCached(previewKey);
                _message = sheet == null || _preview != null ? null : "点击更新预览生成实际打印预览";
                Cursor = _preview == null ? Cursors.Default : Cursors.Hand;
                Invalidate();
            }

            public void ShowLoading(SheetItem sheet, string previewKey)
            {
                ShowSheet(sheet, previewKey);
                _message = "正在按发布设置生成实际打印预览...";
                Invalidate();
            }

            public void ShowPreview(SheetItem sheet, string previewKey, System.Drawing.Bitmap preview)
            {
                _sheet = sheet;
                _previewKey = previewKey;
                StorePreview(previewKey, preview);
                _preview = preview;
                _message = null;
                Cursor = Cursors.Hand;
                Invalidate();
            }

            public void CachePreview(SheetItem sheet, string previewKey, System.Drawing.Bitmap preview)
            {
                StorePreview(previewKey, preview);
                if (!string.Equals(_previewKey, previewKey, StringComparison.Ordinal)) return;
                _sheet = sheet;
                _preview = preview;
                _message = null;
                Cursor = Cursors.Hand;
                Invalidate();
            }

            private void StorePreview(string previewKey, System.Drawing.Bitmap preview)
            {
                System.Drawing.Bitmap previous;
                if (_previewCache.TryGetValue(previewKey, out previous) && !ReferenceEquals(previous, preview)) previous.Dispose();
                _previewCache[previewKey] = preview;
                Touch(previewKey);
                TrimCache();
            }

            public void ShowError(SheetItem sheet, string previewKey, string message)
            {
                ShowSheet(sheet, previewKey);
                _message = "打印预览生成失败\r\n" + message;
                Cursor = Cursors.Default;
                Invalidate();
            }

            public System.Drawing.Bitmap ClonePreview()
            {
                return _preview == null ? null : new System.Drawing.Bitmap(_preview);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var available = new System.Drawing.Rectangle(18, 16, Math.Max(1, Width - 36), Math.Max(1, Height - 34));
                if (_sheet == null)
                {
                    TextRenderer.DrawText(e.Graphics, "选择图纸后显示打印预览", Font, available, TextSecondary,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                    return;
                }

                var aspect = _preview == null
                    ? (string.Equals(_sheet.PaperOrientation, "纵向", StringComparison.Ordinal) ? 0.707 : 1.414)
                    : _preview.Width / (double)Math.Max(1, _preview.Height);
                var page = FitRectangle(available, aspect);
                var shadow = new System.Drawing.Rectangle(page.X + 5, page.Y + 6, page.Width, page.Height);
                using (var shadowBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(7, 13, 18))) e.Graphics.FillRectangle(shadowBrush, shadow);
                using (var pageBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(250, 250, 248))) e.Graphics.FillRectangle(pageBrush, page);
                using (var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(150, 158, 164), 1F)) e.Graphics.DrawRectangle(borderPen, page);

                if (_preview != null && page.Width > 0 && page.Height > 0)
                {
                    e.Graphics.DrawImage(_preview, page);
                    if (!string.IsNullOrWhiteSpace(_message))
                    {
                        var banner = new System.Drawing.Rectangle(page.Left, page.Top, page.Width, Math.Min(38, page.Height));
                        using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(205, 24, 39, 52))) e.Graphics.FillRectangle(brush, banner);
                        TextRenderer.DrawText(e.Graphics, _message.Replace("\r\n", " "), Font, banner, TextPrimary,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    }
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, string.IsNullOrWhiteSpace(_message) ? "点击更新预览生成实际打印预览" : _message, Font, page,
                        System.Drawing.Color.FromArgb(105, 119, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    foreach (var preview in _previewCache.Values) preview.Dispose();
                    _previewCache.Clear();
                    _previewOrder.Clear();
                    _preview = null;
                }
                base.Dispose(disposing);
            }

            private System.Drawing.Bitmap FindCached(string previewKey)
            {
                if (string.IsNullOrWhiteSpace(previewKey)) return null;
                System.Drawing.Bitmap preview;
                if (!_previewCache.TryGetValue(previewKey, out preview)) return null;
                Touch(previewKey);
                return preview;
            }

            private void Touch(string previewKey)
            {
                var node = _previewOrder.Find(previewKey);
                if (node != null) _previewOrder.Remove(node);
                _previewOrder.AddLast(previewKey);
            }

            private void TrimCache()
            {
                while (_previewOrder.Count > MaximumCachedPreviews)
                {
                    var key = _previewOrder.First.Value;
                    _previewOrder.RemoveFirst();
                    System.Drawing.Bitmap expired;
                    if (!_previewCache.TryGetValue(key, out expired)) continue;
                    _previewCache.Remove(key);
                    if (ReferenceEquals(_preview, expired)) _preview = null;
                    expired.Dispose();
                }
            }

            private static System.Drawing.Rectangle FitRectangle(System.Drawing.Rectangle bounds, double aspect)
            {
                var width = bounds.Width;
                var height = (int)Math.Round(width / Math.Max(0.01, aspect));
                if (height > bounds.Height)
                {
                    height = bounds.Height;
                    width = (int)Math.Round(height * aspect);
                }
                return new System.Drawing.Rectangle(
                    bounds.Left + Math.Max(0, (bounds.Width - width) / 2),
                    bounds.Top + Math.Max(0, (bounds.Height - height) / 2),
                    Math.Max(1, width),
                    Math.Max(1, height));
            }

        }

        private sealed class PrintPreviewZoomForm : DpiAwareForm
        {
            private readonly ZoomPreviewPanel _viewer;
            private readonly Label _zoomLabel = new Label();

            public PrintPreviewZoomForm(System.Drawing.Bitmap preview, SheetItem sheet)
            {
                Text = "打印预览" + (sheet == null ? string.Empty : " · " + string.Join("  ", new[] { sheet.SheetNumber, sheet.SheetName }.Where(x => !string.IsNullOrWhiteSpace(x))));
                Width = 1200;
                Height = 820;
                MinimumSize = new System.Drawing.Size(680, 480);
                StartPosition = FormStartPosition.CenterParent;
                BackColor = Canvas;
                Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5F);

                var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Canvas, Padding = Padding.Empty };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Controls.Add(root);

                var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Surface, Padding = new Padding(12, 7, 0, 5) };
                var zoomOut = ViewerButton("−");
                var fit = ViewerButton("适应");
                var zoomIn = ViewerButton("+");
                _zoomLabel.Width = 72;
                _zoomLabel.Height = 32;
                _zoomLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
                _zoomLabel.ForeColor = TextSecondary;
                _zoomLabel.Margin = new Padding(4, 0, 4, 0);
                tools.Controls.Add(zoomOut);
                tools.Controls.Add(_zoomLabel);
                tools.Controls.Add(zoomIn);
                tools.Controls.Add(fit);
                root.Controls.Add(tools, 0, 0);

                _viewer = new ZoomPreviewPanel(preview) { Dock = DockStyle.Fill, Margin = Padding.Empty };
                _viewer.ZoomChanged += (sender, args) => _zoomLabel.Text = Math.Round(_viewer.Zoom * 100d) + "%";
                zoomOut.Click += (sender, args) => _viewer.ZoomBy(1d / 1.2d);
                zoomIn.Click += (sender, args) => _viewer.ZoomBy(1.2d);
                fit.Click += (sender, args) => _viewer.FitToWindow();
                root.Controls.Add(_viewer, 0, 1);
                Shown += (sender, args) => _viewer.FitToWindow();
            }

            private static Button ViewerButton(string text)
            {
                return new RoundedButton
                {
                    Text = text,
                    Width = text.Length > 1 ? 66 : 36,
                    Height = 32,
                    Margin = new Padding(0, 0, 6, 0),
                    BackColor = SurfaceRaised,
                    ForeColor = TextPrimary,
                    FlatAppearance = { BorderColor = Border, MouseOverBackColor = AccentHover, MouseDownBackColor = AccentPressed },
                    TabStop = false
                };
            }
        }

        private sealed class ZoomPreviewPanel : BufferedPanel
        {
            private readonly System.Drawing.Bitmap _image;
            private double _zoom = 1d;
            private System.Drawing.PointF _origin;
            private System.Drawing.Point _dragStart;
            private System.Drawing.PointF _dragOrigin;
            private bool _dragging;

            public ZoomPreviewPanel(System.Drawing.Bitmap image)
            {
                _image = image ?? throw new ArgumentNullException(nameof(image));
                BackColor = System.Drawing.Color.FromArgb(9, 17, 24);
                TabStop = true;
                SetStyle(ControlStyles.Selectable, true);
            }

            public event EventHandler ZoomChanged;
            public double Zoom => _zoom;

            public void FitToWindow()
            {
                var availableWidth = Math.Max(1, ClientSize.Width - 48);
                var availableHeight = Math.Max(1, ClientSize.Height - 48);
                _zoom = Math.Min(availableWidth / (double)_image.Width, availableHeight / (double)_image.Height);
                _zoom = Math.Max(.05d, Math.Min(8d, _zoom));
                CenterImage();
                OnZoomChanged();
            }

            public void ZoomBy(double factor)
            {
                ZoomAt(new System.Drawing.Point(ClientSize.Width / 2, ClientSize.Height / 2), factor);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.InterpolationMode = _zoom >= 1d
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                    : System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                var destination = new System.Drawing.RectangleF(_origin.X, _origin.Y,
                    (float)(_image.Width * _zoom), (float)(_image.Height * _zoom));
                var shadow = destination;
                shadow.Offset(8, 8);
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(4, 9, 13))) e.Graphics.FillRectangle(brush, shadow);
                e.Graphics.DrawImage(_image, destination);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(88, 105, 118))) e.Graphics.DrawRectangle(pen, destination.X, destination.Y, destination.Width, destination.Height);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ZoomAt(e.Location, e.Delta > 0 ? 1.2d : 1d / 1.2d);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                _dragStart = e.Location;
                _dragOrigin = _origin;
                Cursor = Cursors.SizeAll;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging) return;
                _origin = new System.Drawing.PointF(_dragOrigin.X + e.X - _dragStart.X, _dragOrigin.Y + e.Y - _dragStart.Y);
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragging = false;
                Cursor = Cursors.Default;
            }

            protected override void OnDoubleClick(EventArgs e)
            {
                base.OnDoubleClick(e);
                FitToWindow();
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                if (!IsHandleCreated) return;
                KeepImageVisible();
                Invalidate();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _image.Dispose();
                base.Dispose(disposing);
            }

            private void ZoomAt(System.Drawing.Point anchor, double factor)
            {
                var oldZoom = _zoom;
                var newZoom = Math.Max(.05d, Math.Min(8d, oldZoom * factor));
                if (Math.Abs(newZoom - oldZoom) < .0001d) return;
                var imageX = (anchor.X - _origin.X) / oldZoom;
                var imageY = (anchor.Y - _origin.Y) / oldZoom;
                _zoom = newZoom;
                _origin = new System.Drawing.PointF((float)(anchor.X - imageX * newZoom), (float)(anchor.Y - imageY * newZoom));
                KeepImageVisible();
                OnZoomChanged();
            }

            private void CenterImage()
            {
                _origin = new System.Drawing.PointF(
                    (float)((ClientSize.Width - _image.Width * _zoom) / 2d),
                    (float)((ClientSize.Height - _image.Height * _zoom) / 2d));
                Invalidate();
            }

            private void KeepImageVisible()
            {
                var width = _image.Width * _zoom;
                var height = _image.Height * _zoom;
                var minimumVisible = 48d;
                if (width <= ClientSize.Width) _origin.X = (float)((ClientSize.Width - width) / 2d);
                else _origin.X = (float)Math.Min(minimumVisible, Math.Max(ClientSize.Width - width - minimumVisible, _origin.X));
                if (height <= ClientSize.Height) _origin.Y = (float)((ClientSize.Height - height) / 2d);
                else _origin.Y = (float)Math.Min(minimumVisible, Math.Max(ClientSize.Height - height - minimumVisible, _origin.Y));
                Invalidate();
            }

            private void OnZoomChanged()
            {
                ZoomChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        private sealed class BufferedSplitContainer : SplitContainer
        {
            public BufferedSplitContainer()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                UpdateStyles();
            }
        }
    }
}
