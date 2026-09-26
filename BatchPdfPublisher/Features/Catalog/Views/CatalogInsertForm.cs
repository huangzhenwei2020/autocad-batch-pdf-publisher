using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;

namespace BatchPdfPublisher.Views
{
    public sealed class CatalogInsertForm : DpiAwareForm
    {
        private const int SectionTitleHeight = 34;
        private const int InputRowHeight = CadDialogTheme.ControlHeight + 8;
        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly IList<SheetItem> _sheets;
        private readonly Action _done;
        private readonly CadToggleCheckedListBox _buildings = new CadToggleCheckedListBox();
        private readonly TextBox _buildingFilter = new TextBox();
        private readonly Label _summary = new Label();
        private readonly Label _buildingSummary = new Label();
        private readonly Label _selectedBuildingCount = new Label();
        private readonly Label _pageSummary = new Label();
        private readonly List<string> _buildingNames = new List<string>();
        private readonly Dictionary<string, bool> _buildingChecked = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _buildingSheetCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly CheckBox[] _columnChecks = { Check("序号", true), Check("图号", true), Check("图名", true), Check("图框", true), Check("比例", true) };
        private readonly TextBox _rows = Box("30"), _rowHeight = Box("7");
        private readonly PublisherForm.ThemedComboBox _textHeight = Preset("1.5", "2.5", "3.5", "5", "7", "10", "14", "20");
        private readonly PublisherForm.ThemedComboBox _insertScale = RatioPreset("1:1", "1:20", "1:50", "1:100", "1:200", "1:500");
        private readonly TextBox[] _widthBoxes = { Box("20"), Box("30"), Box("70"), Box("24"), Box("24") };
        private readonly PublisherForm.ThemedComboBox _font = new PublisherForm.ThemedComboBox();
        private readonly CadRoundedButton _color = new CadRoundedButton();
        private readonly CatalogPreviewControl _preview = new CatalogPreviewControl();
        private CadRoundedButton _previousPage;
        private CadRoundedButton _nextPage;
        private int _previewPage;
        private bool _updatingBuildings;
        private AcColor _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);

        public CatalogInsertForm(Document document, IList<SheetItem> sheets, Action done)
        {
            _document = document;
            _sheets = sheets ?? new List<SheetItem>();
            _done = done;
            Text = "插入图纸目录";
            Width = 1180;
            Height = 720;
            MinimumSize = new Size(1000, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = CadDialogTheme.Canvas;
            ForeColor = CadDialogTheme.Text;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            SizeGripStyle = SizeGripStyle.Show;
            _documentBinding = new ModelessDocumentBinding(this, document);
            Build();
        }

        private void Build()
        {
            SuspendLayout();
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(14, 10, 14, 10),
                BackColor = CadDialogTheme.Canvas,
                Margin = Padding.Empty
            }.Buffered();
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            Controls.Add(outer);

            outer.Controls.Add(BuildHeader(), 0, 0);

            var workspace = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                BackColor = CadDialogTheme.Canvas,
                Margin = Padding.Empty
            }.Buffered();
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CadDialogTheme.Gap));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CadDialogTheme.Gap));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39F));
            workspace.Controls.Add(BuildBuildingCard(), 0, 0);
            workspace.Controls.Add(BuildSettingsCard(), 2, 0);
            workspace.Controls.Add(BuildPreviewCard(), 4, 0);
            outer.Controls.Add(workspace, 0, 1);
            outer.Controls.Add(BuildFooter(), 0, 2);

            LoadBuildingData();
            LoadSettings();
            WirePreviewEvents();
            ApplyBuildingFilter();
            RefreshPreview(true);
            UiPerformance.BufferedTree(this);
            ResumeLayout(true);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty }.Buffered();
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = CadDialogTheme.Heading("插入图纸目录");
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            _summary.AutoSize = true;
            _summary.ForeColor = CadDialogTheme.Muted;
            _summary.TextAlign = ContentAlignment.MiddleRight;
            _summary.Margin = new Padding(0, 14, 4, 0);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(_summary, 1, 0);
            return header;
        }

        private Control BuildBuildingCard()
        {
            var card = CadDialogTheme.Card();
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, InputRowHeight));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight + 10));

            var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            titleRow.Controls.Add(CadDialogTheme.Heading("选择子项目"), 0, 0);
            _selectedBuildingCount.AutoSize = true;
            _selectedBuildingCount.ForeColor = CadDialogTheme.Muted;
            _selectedBuildingCount.TextAlign = ContentAlignment.MiddleRight;
            _selectedBuildingCount.Margin = new Padding(0, 10, 0, 0);
            titleRow.Controls.Add(_selectedBuildingCount, 1, 0);
            layout.Controls.Add(titleRow, 0, 0);

            _buildingFilter.TextChanged += (sender, args) => ApplyBuildingFilter();
            var filterHost = CadDialogTheme.Input(_buildingFilter);
            filterHost.Placeholder = "搜索子项目...";
            filterHost.Margin = new Padding(0, 2, 0, 6);
            layout.Controls.Add(filterHost, 0, 1);

            _buildings.Dock = DockStyle.Fill;
            _buildings.DetailTextProvider = item =>
            {
                var key = item == null ? string.Empty : item.ToString();
                return _buildingSheetCounts.TryGetValue(key, out var count) ? count + " 张" : string.Empty;
            };
            _buildings.ItemCheck += BuildingItemCheck;
            var listHost = new CadListHost(_buildings) { Margin = new Padding(0, 0, 0, 6) };
            layout.Controls.Add(listHost, 0, 2);

            _buildingSummary.Dock = DockStyle.Fill;
            _buildingSummary.ForeColor = CadDialogTheme.Muted;
            _buildingSummary.TextAlign = ContentAlignment.MiddleLeft;
            _buildingSummary.Margin = Padding.Empty;
            layout.Controls.Add(_buildingSummary, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var selectAll = CadDialogTheme.Button("全部选择", () => SetAllBuildings(true));
            var clearAll = CadDialogTheme.Button("全部取消", () => SetAllBuildings(false));
            selectAll.AutoSize = false;
            clearAll.AutoSize = false;
            selectAll.Dock = DockStyle.Fill;
            clearAll.Dock = DockStyle.Fill;
            selectAll.Margin = new Padding(0, 4, 4, 6);
            clearAll.Margin = new Padding(4, 4, 0, 6);
            actions.Controls.Add(selectAll, 0, 0);
            actions.Controls.Add(clearAll, 1, 0);
            layout.Controls.Add(actions, 0, 4);

            card.Controls.Add(layout);
            return card;
        }

        private Control BuildSettingsCard()
        {
            var card = CadDialogTheme.Card();
            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 10, ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, SectionTitleHeight));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, SectionTitleHeight));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, InputRowHeight));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, SectionTitleHeight));
            settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(CadDialogTheme.Heading("目录设置"), 0, 0);
            var reset = CadDialogTheme.Button("恢复默认", RestoreDefaults);
            reset.BackColor = CadDialogTheme.Surface;
            reset.BorderColor = CadDialogTheme.Surface;
            reset.ForeColor = CadDialogTheme.Accent;
            reset.Margin = new Padding(0);
            heading.Controls.Add(reset, 1, 0);
            settings.Controls.Add(heading, 0, 0);

            settings.Controls.Add(SectionHeading("包含的列"), 0, 1);
            var columnPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            for (var i = 0; i < _columnChecks.Length; i++)
            {
                columnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
                _columnChecks[i].Dock = DockStyle.Fill;
                _columnChecks[i].MinimumSize = new Size(0, 34);
                _columnChecks[i].Margin = new Padding(0, 0, i == _columnChecks.Length - 1 ? 0 : 6, 2);
                columnPanel.Controls.Add(_columnChecks[i], i, 0);
            }
            settings.Controls.Add(columnPanel, 0, 2);
            settings.Controls.Add(Divider(), 0, 3);

            settings.Controls.Add(SectionHeading("分页与尺寸"), 0, 4);
            settings.Controls.Add(BuildMetricsRow(), 0, 5);
            settings.Controls.Add(BuildWidthRow(), 0, 6);
            settings.Controls.Add(Divider(), 0, 7);

            settings.Controls.Add(SectionHeading("文字与样式"), 0, 8);
            settings.Controls.Add(BuildTypographyPanel(), 0, 9);
            card.Controls.Add(settings);
            return card;
        }

        private Control BuildMetricsRow()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.Controls.Add(CadDialogTheme.FieldLabel("每页行数"), 0, 0);
            row.Controls.Add(InputHost(_rows), 1, 0);
            row.Controls.Add(CadDialogTheme.FieldLabel("行高"), 2, 0);
            row.Controls.Add(InputHost(_rowHeight), 3, 0);
            return row;
        }

        private Control BuildWidthRow()
        {
            var widths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            widths.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            widths.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight));
            var labels = new[] { "序号宽", "图号宽", "图名宽", "图框宽", "比例宽" };
            for (var i = 0; i < labels.Length; i++)
            {
                widths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
                var margin = i == labels.Length - 1 ? 0 : 6;
                widths.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, margin, 0) }, i, 0);
                var host = InputHost(_widthBoxes[i]);
                host.Margin = new Padding(0, 0, margin, 0);
                widths.Controls.Add(host, i, 1);
            }
            return widths;
        }

        private Control BuildTypographyPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = InputRowHeight * 2, ColumnCount = 4, RowCount = 2, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, InputRowHeight));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, InputRowHeight));

            _textHeight.DropDownStyle = ComboBoxStyle.DropDownList;
            _insertScale.DropDownStyle = ComboBoxStyle.DropDown;
            _font.DropDownStyle = ComboBoxStyle.DropDownList;
            _font.Items.AddRange(FrameCreationService.GetTextStyleNames(_document));
            _font.SelectedItem = DraftingStandardService.GetTextStyleName(DraftingStandardProfile.BodyTextKey);
            if (_font.SelectedIndex < 0 && _font.Items.Count > 0) _font.SelectedIndex = 0;

            panel.Controls.Add(CadDialogTheme.FieldLabel("文字高度"), 0, 0);
            panel.Controls.Add(ComboHost(_textHeight), 1, 0);
            panel.Controls.Add(CadDialogTheme.FieldLabel("图纸比例"), 2, 0);
            panel.Controls.Add(ComboHost(_insertScale), 3, 0);
            panel.Controls.Add(CadDialogTheme.FieldLabel("字体样式"), 0, 1);
            panel.Controls.Add(ComboHost(_font), 1, 1);
            panel.Controls.Add(CadDialogTheme.FieldLabel("颜色"), 2, 1);

            _color.Text = string.Empty;
            _color.Dock = DockStyle.Fill;
            _color.AutoSize = false;
            _color.Height = CadDialogTheme.ControlHeight;
            _color.MinimumSize = new Size(0, CadDialogTheme.ControlHeight);
            _color.BackColor = DisplayColor(_acColor);
            _color.PreserveBackColorOnInteraction = true;
            _color.BorderColor = CadDialogTheme.Border;
            _color.Margin = new Padding(0, 0, 8, 8);
            _color.Click += (sender, args) => ChooseColor();
            panel.Controls.Add(_color, 3, 1);
            return panel;
        }

        private Control BuildPreviewCard()
        {
            var card = CadDialogTheme.Card();
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty }.Buffered();
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            title.Controls.Add(CadDialogTheme.Heading("目录预览"), 0, 0);
            _pageSummary.AutoSize = true;
            _pageSummary.ForeColor = CadDialogTheme.Muted;
            _pageSummary.TextAlign = ContentAlignment.MiddleRight;
            _pageSummary.Margin = new Padding(0, 11, 8, 0);
            title.Controls.Add(_pageSummary, 1, 0);
            _previousPage = PageButton("‹", -1);
            _nextPage = PageButton("›", 1);
            title.Controls.Add(_previousPage, 2, 0);
            title.Controls.Add(_nextPage, 3, 0);
            layout.Controls.Add(title, 0, 0);

            _preview.Dock = DockStyle.Fill;
            _preview.Margin = Padding.Empty;
            layout.Controls.Add(_preview, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty }.Buffered();
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(new Label { Text = "目录将插入当前图纸，完成后仍可在 CAD 中调整位置。", Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            var close = CadDialogTheme.Button("关闭", Close);
            var insert = CadDialogTheme.Button("插入目录", Insert, true);
            close.MinimumSize = new Size(92, CadDialogTheme.ControlHeight);
            insert.MinimumSize = new Size(110, CadDialogTheme.ControlHeight);
            close.Margin = new Padding(8, 6, 0, 0);
            insert.Margin = new Padding(8, 6, 0, 0);
            actions.Controls.Add(close);
            actions.Controls.Add(insert);
            footer.Controls.Add(actions, 1, 0);
            return footer;
        }

        private void LoadBuildingData()
        {
            foreach (var group in _sheets.GroupBy(sheet => BuildingName(sheet)))
            {
                _buildingNames.Add(group.Key);
                _buildingChecked[group.Key] = true;
                _buildingSheetCounts[group.Key] = group.Count();
            }
        }

        private void WirePreviewEvents()
        {
            foreach (var check in _columnChecks) check.CheckedChanged += (sender, args) => RefreshPreview(true);
            _rows.TextChanged += (sender, args) => RefreshPreview(true);
            _rowHeight.TextChanged += (sender, args) => RefreshPreview(false);
            foreach (var box in _widthBoxes) box.TextChanged += (sender, args) => RefreshPreview(false);
            _textHeight.TextChanged += (sender, args) => RefreshPreview(false);
            _insertScale.TextChanged += (sender, args) => RefreshPreview(false);
            _font.TextChanged += (sender, args) => RefreshPreview(false);
        }

        private void BuildingItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _buildings.Items.Count) return;
            _buildingChecked[_buildings.Items[e.Index].ToString()] = e.NewValue == CheckState.Checked;
            if (_updatingBuildings) return;
            UpdateSummary(false);
            RefreshPreview(true, false);
        }

        private void ApplyBuildingFilter()
        {
            SyncVisibleBuildingStates();
            var keyword = (_buildingFilter.Text ?? string.Empty).Trim();
            _buildings.BeginUpdate();
            _updatingBuildings = true;
            try
            {
                _buildings.Items.Clear();
                foreach (var name in _buildingNames)
                {
                    if (keyword.Length > 0 && name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _buildings.Items.Add(name, !_buildingChecked.TryGetValue(name, out var selected) || selected);
                }
            }
            finally { _updatingBuildings = false; _buildings.EndUpdate(); }
            UpdateSummary(false);
            RefreshPreview(true, false);
        }

        private void UpdateSummary(bool syncVisible = true)
        {
            if (syncVisible) SyncVisibleBuildingStates();
            var selectedNames = new HashSet<string>(_buildingChecked.Where(pair => pair.Value).Select(pair => pair.Key), StringComparer.Ordinal);
            var sheetCount = _sheets.Count(sheet => selectedNames.Contains(BuildingName(sheet)));
            _summary.Text = "已选 " + selectedNames.Count + " 个子项目 / 共 " + sheetCount + " 张图纸";
            _buildingSummary.Text = "共 " + selectedNames.Count + " 个子项目    " + sheetCount + " 张图纸";
            _selectedBuildingCount.Text = "已选择 " + selectedNames.Count + " / " + _buildingNames.Count;
        }

        private void SetAllBuildings(bool selected)
        {
            foreach (var name in _buildingNames) _buildingChecked[name] = selected;
            _buildings.BeginUpdate();
            _updatingBuildings = true;
            try { for (var index = 0; index < _buildings.Items.Count; index++) _buildings.SetItemChecked(index, selected); }
            finally { _updatingBuildings = false; _buildings.EndUpdate(); }
            UpdateSummary(false);
            RefreshPreview(true, false);
        }

        private void SyncVisibleBuildingStates()
        {
            for (var index = 0; index < _buildings.Items.Count; index++)
                _buildingChecked[_buildings.Items[index].ToString()] = _buildings.GetItemChecked(index);
        }

        private IList<SheetItem> SelectedSheets()
        {
            var selected = new HashSet<string>(_buildingChecked.Where(pair => pair.Value).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
            return _sheets.Where(sheet => selected.Contains(BuildingName(sheet))).ToList();
        }

        private void RefreshPreview(bool resetPage, bool syncVisible = true)
        {
            if (syncVisible) SyncVisibleBuildingStates();
            if (resetPage) _previewPage = 0;
            var rows = int.TryParse(_rows.Text, out var parsedRows) && parsedRows > 0 ? parsedRows : 30;
            var widths = _widthBoxes.Select(box => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 1D).ToArray();
            _preview.SetContent(SelectedSheets(), _columnChecks.Select(check => check.Checked).ToArray(), widths, rows, _previewPage, DisplayColor(_acColor));
            _previewPage = _preview.PageIndex;
            _pageSummary.Text = _preview.PageCount == 0 ? "0 / 0" : "第 " + (_previewPage + 1) + " 页 / 共 " + _preview.PageCount + " 页";
            _previousPage.Enabled = _previewPage > 0;
            _nextPage.Enabled = _previewPage + 1 < _preview.PageCount;
        }

        private void ChangePreviewPage(int delta)
        {
            _previewPage += delta;
            RefreshPreview(false);
        }

        private void RestoreDefaults()
        {
            _rows.Text = "30";
            _rowHeight.Text = "7";
            var defaults = new[] { "20", "30", "70", "24", "24" };
            for (var i = 0; i < _widthBoxes.Length; i++) _widthBoxes[i].Text = defaults[i];
            for (var i = 0; i < _columnChecks.Length; i++) _columnChecks[i].Checked = true;
            _textHeight.SelectedItem = "3.5";
            _insertScale.SelectedItem = "1:100";
            _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
            _color.BackColor = DisplayColor(_acColor);
            RefreshPreview(true);
        }

        private void Insert()
        {
            if (!int.TryParse(_rows.Text, out var rows) || rows < 1 ||
                !double.TryParse(_rowHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rowHeight) || rowHeight <= 0 ||
                !double.TryParse(_textHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var textHeight) || textHeight <= 0 ||
                !TryParseDrawingScale(_insertScale.Text, out var scale))
            {
                MessageBox.Show(this, "行数、行高、文字高度必须有效；图纸比例请输入例如 1:20、1:50 或 1:100。", "插入目录");
                return;
            }
            var widths = _widthBoxes.Select(box => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0).ToArray();
            if (widths.Any(value => value <= 0)) { MessageBox.Show(this, "请分别填写五列的正数列宽。", "插入目录"); return; }
            SyncVisibleBuildingStates();
            var selectedSheets = SelectedSheets();
            if (selectedSheets.Count == 0) { MessageBox.Show(this, "请至少选择一个子项目。", "插入目录"); return; }
            if (!_columnChecks.Any(check => check.Checked)) { MessageBox.Show(this, "请至少勾选一列目录内容。", "插入目录"); return; }
            SaveSettings();
            try
            {
                BatchPdfPublisher.Commands.StartCatalogInsert(selectedSheets, new CatalogSettings
                {
                    IncludeBuilding = _columnChecks[0].Checked,
                    IncludeNumber = _columnChecks[1].Checked,
                    IncludeName = _columnChecks[2].Checked,
                    IncludePaper = _columnChecks[3].Checked,
                    IncludeScale = _columnChecks[4].Checked,
                    RowsPerPage = rows,
                    RowHeight = rowHeight,
                    TextHeight = textHeight,
                    Scale = scale,
                    ColumnWidths = widths,
                    Font = _font.Text,
                    Color = _acColor
                }, _done);
                Close();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowError(Exception ex)
        {
            var show = new Action(() => MessageBox.Show(this, "插入目录失败：\n" + ex.Message, "插入目录", MessageBoxButtons.OK, MessageBoxIcon.Error));
            if (IsHandleCreated) BeginInvoke(show); else show();
        }

        private void ChooseColor()
        {
            var dialog = new AcColorDialog { Color = _acColor };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            _acColor = dialog.Color;
            _color.BackColor = DisplayColor(_acColor);
            RefreshPreview(false);
        }

        private void SaveSettings()
        {
            try
            {
                var path = UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings");
                var values = new[] { _rows.Text, _rowHeight.Text, string.Join(",", _widthBoxes.Select(box => box.Text)), _textHeight.Text, _insertScale.Text, _font.Text, _acColor.ColorMethod.ToString(), _acColor.ColorIndex.ToString(), string.Join("", _columnChecks.Select(check => check.Checked ? "1" : "0")) };
                System.IO.File.WriteAllLines(path, values);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                var path = UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings");
                if (!System.IO.File.Exists(path)) return;
                var values = System.IO.File.ReadAllLines(path);
                if (values.Length > 0) _rows.Text = values[0];
                if (values.Length > 1) _rowHeight.Text = values[1];
                if (values.Length > 2)
                {
                    var widths = values[2].Split(',');
                    for (var i = 0; i < _widthBoxes.Length && i < widths.Length; i++) _widthBoxes[i].Text = widths[i];
                }
                if (values.Length > 3 && _textHeight.Items.Contains(values[3])) _textHeight.SelectedItem = values[3];
                if (values.Length > 4) _insertScale.Text = values[4];
                if (values.Length > 5 && _font.Items.Contains(values[5])) _font.SelectedItem = values[5];
                if (values.Length > 6 && values[6].IndexOf("ByAci", StringComparison.OrdinalIgnoreCase) >= 0 && values.Length > 7 && short.TryParse(values[7], out var aci))
                {
                    _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
                    _color.BackColor = DisplayColor(_acColor);
                }
                if (values.Length > 8)
                    for (var i = 0; i < _columnChecks.Length && i < values[8].Length; i++) _columnChecks[i].Checked = values[8][i] == '1';
            }
            catch { }
        }

        private static Control SectionHeading(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, AutoSize = false, ForeColor = CadDialogTheme.Text, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        }

        private static Control Divider()
        {
            return new Panel { Dock = DockStyle.Fill, Height = 1, BackColor = CadDialogTheme.Border, Margin = Padding.Empty };
        }

        private static CadTextInputHost InputHost(TextBox box)
        {
            var host = CadDialogTheme.Input(box);
            host.Margin = new Padding(0, 0, 8, 8);
            return host;
        }

        private static Control ComboHost(PublisherForm.ThemedComboBox combo)
        {
            combo.Dock = DockStyle.Fill;
            combo.Margin = new Padding(0, 0, 8, 8);
            return combo;
        }

        private CadRoundedButton PageButton(string text, int delta)
        {
            var button = CadDialogTheme.Button(text, () => ChangePreviewPage(delta));
            button.Dock = DockStyle.Fill;
            button.AutoSize = false;
            button.Padding = Padding.Empty;
            button.Margin = new Padding(2, 4, 0, 4);
            return button;
        }

        private static Color DisplayColor(AcColor color)
        {
            if (color == null) return Color.White;
            if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor) return Color.FromArgb(color.Red, color.Green, color.Blue);
            var index = color.ColorIndex;
            var map = new[] { Color.Black, Color.Red, Color.Yellow, Color.Green, Color.Cyan, Color.Blue, Color.Magenta, Color.White, Color.Gray, Color.LightGray };
            return index >= 0 && index < map.Length ? map[index] : Color.White;
        }

        private static string BuildingName(SheetItem sheet) => string.IsNullOrWhiteSpace(sheet.Building) ? "未分组" : sheet.Building;
        private static TextBox Box(string text) => new TextBox { Text = text };
        private static CheckBox Check(string text, bool value) => new CadToggleSwitch { Text = text, Checked = value };
        private static PublisherForm.ThemedComboBox Preset(params string[] values) { var box = new PublisherForm.ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
        private static PublisherForm.ThemedComboBox RatioPreset(params string[] values) { var box = new PublisherForm.ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 120 }; box.Items.AddRange(values); box.SelectedIndex = 3; return box; }

        private static bool TryParseDrawingScale(string text, out double scale)
        {
            scale = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim().Replace('：', ':').Replace('／', '/');
            var separator = value.IndexOf(':');
            if (separator < 0) separator = value.IndexOf('/');
            if (separator >= 0)
            {
                var leftText = value.Substring(0, separator).Trim();
                var rightText = value.Substring(separator + 1).Trim();
                if (!double.TryParse(leftText, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) || !double.TryParse(rightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var right) || left <= 0 || right <= 0) return false;
                scale = right / left;
                return scale > 0;
            }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0;
        }

        private sealed class CatalogPreviewControl : Control
        {
            private readonly List<PreviewPage> _pages = new List<PreviewPage>();
            private bool[] _included = new bool[5];
            private double[] _widths = new double[5];
            private Color _ink = Color.White;

            public int PageIndex { get; private set; }
            public int PageCount => _pages.Count;

            public CatalogPreviewControl()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Color.FromArgb(10, 24, 34);
                ForeColor = CadDialogTheme.Text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                HandleCreated += (sender, args) => UpdateRoundedRegion();
                SizeChanged += (sender, args) => UpdateRoundedRegion();
            }

            private void UpdateRoundedRegion()
            {
                if (Width < 2 || Height < 2) return;
                using (var path = CadDialogTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 7))
                {
                    var previous = Region;
                    Region = new Region(path);
                    if (previous != null) previous.Dispose();
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
            }

            public void SetContent(IList<SheetItem> sheets, bool[] included, double[] widths, int rowsPerPage, int pageIndex, Color ink)
            {
                _pages.Clear();
                _included = included ?? new bool[5];
                _widths = widths ?? new double[5];
                _ink = ink;
                var pageSize = Math.Max(1, rowsPerPage);
                foreach (var group in (sheets ?? new List<SheetItem>()).GroupBy(BuildingName))
                {
                    var groupSheets = group.ToList();
                    for (var start = 0; start < groupSheets.Count; start += pageSize)
                        _pages.Add(new PreviewPage(group.Key, groupSheets.Skip(start).Take(pageSize).ToList(), start));
                }
                PageIndex = _pages.Count == 0 ? 0 : Math.Max(0, Math.Min(pageIndex, _pages.Count - 1));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.Clear(BackColor);
                var shell = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = CadDialogTheme.Rounded(shell, 7))
                using (var fill = new SolidBrush(Color.FromArgb(9, 22, 31)))
                using (var pen = new Pen(CadDialogTheme.Border))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(pen, path);
                }

                if (_pages.Count == 0 || !_included.Any(value => value))
                {
                    TextRenderer.DrawText(e.Graphics, _pages.Count == 0 ? "请选择至少一个子项目" : "请至少启用一列目录内容", Font, shell, CadDialogTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }

                var page = _pages[PageIndex];
                var canvas = new Rectangle(shell.Left + 14, shell.Top + 14, Math.Max(1, shell.Width - 28), Math.Max(1, shell.Height - 28));
                var totalRows = Math.Max(6, Math.Min(page.Sheets.Count, 10));
                var titleHeight = Math.Max(32, Math.Min(48, canvas.Height / 8));
                var tableHeight = Math.Max(120, Math.Min(canvas.Height - titleHeight - 18, (totalRows + 1) * 28));
                var tableTop = canvas.Top + titleHeight;
                var table = new Rectangle(canvas.Left, tableTop, canvas.Width, tableHeight);
                var rowHeight = Math.Max(18F, table.Height / (float)(totalRows + 1));
                var lineColor = _ink.GetBrightness() < 0.2F ? Color.FromArgb(235, 212, 0) : _ink;
                using (var pen = new Pen(lineColor, 1F))
                using (var titleFont = new Font("Microsoft YaHei UI", Math.Max(10F, Math.Min(16F, canvas.Width / 30F)), FontStyle.Regular))
                using (var textFont = new Font("Microsoft YaHei UI", Math.Max(7F, Math.Min(9F, canvas.Width / 48F)), FontStyle.Regular))
                {
                    TextRenderer.DrawText(e.Graphics, page.Building + " 图纸目录", titleFont, new Rectangle(canvas.Left, canvas.Top, canvas.Width, titleHeight), lineColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    e.Graphics.DrawRectangle(pen, table);
                    for (var row = 1; row <= totalRows; row++)
                    {
                        var y = table.Top + (int)Math.Round(rowHeight * row);
                        e.Graphics.DrawLine(pen, table.Left, y, table.Right, y);
                    }

                    var activeIndexes = Enumerable.Range(0, 5).Where(index => index < _included.Length && _included[index]).ToArray();
                    var activeWidths = activeIndexes.Select(index => index < _widths.Length && _widths[index] > 0 ? _widths[index] : 1D).ToArray();
                    var totalWidth = activeWidths.Sum();
                    var columns = new List<Rectangle>();
                    var x = table.Left;
                    for (var column = 0; column < activeIndexes.Length; column++)
                    {
                        var right = column == activeIndexes.Length - 1 ? table.Right : x + (int)Math.Round(table.Width * activeWidths[column] / totalWidth);
                        columns.Add(new Rectangle(x, table.Top, Math.Max(1, right - x), table.Height));
                        if (column > 0) e.Graphics.DrawLine(pen, x, table.Top, x, table.Bottom);
                        x = right;
                    }

                    var headers = new[] { "序号", "图号", "图名", "图框", "比例" };
                    for (var column = 0; column < columns.Count; column++)
                        DrawCellText(e.Graphics, headers[activeIndexes[column]], textFont, lineColor, new Rectangle(columns[column].Left + 2, table.Top + 1, Math.Max(1, columns[column].Width - 4), Math.Max(1, (int)rowHeight - 2)), true);

                    var visibleCount = Math.Min(page.Sheets.Count, totalRows);
                    for (var row = 0; row < visibleCount; row++)
                    {
                        var sheet = page.Sheets[row];
                        var values = new[] { (page.StartIndex + row + 1).ToString(), sheet.SheetNumber, sheet.SheetName, sheet.FrameDisplay, sheet.PrintScale };
                        for (var column = 0; column < columns.Count; column++)
                        {
                            var cell = new Rectangle(columns[column].Left + 3, table.Top + (int)Math.Round(rowHeight * (row + 1)) + 1, Math.Max(1, columns[column].Width - 6), Math.Max(1, (int)rowHeight - 2));
                            DrawCellText(e.Graphics, values[activeIndexes[column]] ?? string.Empty, textFont, lineColor, cell, activeIndexes[column] != 2);
                        }
                    }
                }
            }

            private static void DrawCellText(Graphics graphics, string text, Font font, Color color, Rectangle bounds, bool centered)
            {
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                flags |= centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
                TextRenderer.DrawText(graphics, text ?? string.Empty, font, bounds, color, flags);
            }

            private sealed class PreviewPage
            {
                public PreviewPage(string building, IList<SheetItem> sheets, int startIndex) { Building = building; Sheets = sheets; StartIndex = startIndex; }
                public string Building { get; }
                public IList<SheetItem> Sheets { get; }
                public int StartIndex { get; }
            }
        }
    }
}
