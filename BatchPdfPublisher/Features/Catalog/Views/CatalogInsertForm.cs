using System;
using System.Drawing;
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
        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly System.Collections.Generic.IList<SheetItem> _sheets;
        private readonly Action _done;
        private readonly CadToggleCheckedListBox _buildings = new CadToggleCheckedListBox();
        private readonly CheckBox[] _columnChecks = { Check("序号", true), Check("图号", true), Check("图名", true), Check("图框", true), Check("比例", true) };
        private readonly TextBox _rows = Box("30"), _rowHeight = Box("7");
        private readonly PublisherForm.ThemedComboBox _textHeight = Preset("1.5", "2.5", "3.5", "5", "7", "10", "14", "20");
        // 目录插入比例是图纸比例：1:20、1:50、1:100；可直接编辑输入自定义比例。
        private readonly PublisherForm.ThemedComboBox _insertScale = RatioPreset("1:1", "1:20", "1:50", "1:100", "1:200", "1:500");
        private readonly TextBox[] _widthBoxes = { Box("20"), Box("30"), Box("70"), Box("24"), Box("24") };
        private readonly PublisherForm.ThemedComboBox _font = new PublisherForm.ThemedComboBox();
        private readonly CadRoundedButton _color = new CadRoundedButton();
        private AcColor _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);

        public CatalogInsertForm(Document document, System.Collections.Generic.IList<SheetItem> sheets, Action done)
        {
            _document = document; _sheets = sheets; _done = done;
            Text = "插入图纸目录"; Width = 980; Height = 700; MinimumSize = new Size(820, 580); StartPosition = FormStartPosition.CenterParent;
            _documentBinding = new ModelessDocumentBinding(this, document);
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; SizeGripStyle = SizeGripStyle.Show; Build();
        }

        private void Build()
        {
            BackColor = CadDialogTheme.Canvas;
            ForeColor = CadDialogTheme.Text;
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12), BackColor = CadDialogTheme.Canvas };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            Controls.Add(outer);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = CadDialogTheme.Canvas };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(CadDialogTheme.Heading("插入图纸目录"), 0, 0);
            heading.Controls.Add(new Label { Text = "选择子项目并设置目录排版", AutoSize = true, ForeColor = CadDialogTheme.Muted, Margin = new Padding(0, 12, 4, 0) }, 1, 0);
            outer.Controls.Add(heading, 0, 0);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = CadDialogTheme.Canvas };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CadDialogTheme.Gap)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.Controls.Add(workspace, 0, 1);

            var buildingCard = CadDialogTheme.Card();
            var buildingPicker = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = CadDialogTheme.Surface };
            buildingPicker.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); buildingPicker.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); buildingPicker.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            buildingPicker.Controls.Add(CadDialogTheme.Heading("选择子项目"), 0, 0);
            var buildingActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CadDialogTheme.Surface };
            buildingActions.Controls.Add(CadDialogTheme.Button("全部选择", () => SetAllBuildings(true)));
            buildingActions.Controls.Add(CadDialogTheme.Button("全部取消", () => SetAllBuildings(false)));
            buildingPicker.Controls.Add(buildingActions, 0, 1);
            foreach (var name in _sheets.Select(s => string.IsNullOrWhiteSpace(s.Building) ? "未分组" : s.Building).Distinct()) _buildings.Items.Add(name, true);
            buildingPicker.Controls.Add(new CadListHost(_buildings), 0, 2);
            buildingCard.Controls.Add(buildingPicker); workspace.Controls.Add(buildingCard, 0, 0);

            var settingsCard = CadDialogTheme.Card();
            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, RowCount = 9, ColumnCount = 1, BackColor = CadDialogTheme.Surface };
            settings.Controls.Add(CadDialogTheme.Heading("目录内容"), 0, 0);
            var columnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = CadDialogTheme.Surface };
            foreach (var check in _columnChecks) { check.Width = 110; columnPanel.Controls.Add(check); }
            settings.Controls.Add(columnPanel, 0, 1);
            settings.Controls.Add(CadDialogTheme.Heading("分页与尺寸"), 0, 2);
            var metrics = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, BackColor = CadDialogTheme.Surface };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            metrics.Controls.Add(CadDialogTheme.FieldLabel("每页行数"), 0, 0); metrics.Controls.Add(CadDialogTheme.Input(_rows), 1, 0);
            metrics.Controls.Add(CadDialogTheme.FieldLabel("行高"), 2, 0); metrics.Controls.Add(CadDialogTheme.Input(_rowHeight), 3, 0);
            settings.Controls.Add(metrics, 0, 3);
            settings.Controls.Add(new Label { Text = "列宽", Dock = DockStyle.Top, Height = 28, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
            var widths = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, BackColor = CadDialogTheme.Surface };
            var labels = new[] { "序号", "图号", "图名", "图框", "比例" };
            for (var i = 0; i < 5; i++)
            {
                widths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
                var cell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 0, 8, 0) };
                cell.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Top, Height = 24, ForeColor = CadDialogTheme.Muted }, 0, 0);
                cell.Controls.Add(CadDialogTheme.Input(_widthBoxes[i]), 0, 1);
                widths.Controls.Add(cell, i, 0);
            }
            settings.Controls.Add(widths, 0, 5);
            settings.Controls.Add(CadDialogTheme.Heading("文字与插入"), 0, 6);
            var typography = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 2, BackColor = CadDialogTheme.Surface };
            typography.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); typography.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); typography.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); typography.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _textHeight.DropDownStyle = ComboBoxStyle.DropDownList; _insertScale.DropDownStyle = ComboBoxStyle.DropDown;
            typography.Controls.Add(CadDialogTheme.FieldLabel("文字高度"), 0, 0); typography.Controls.Add(_textHeight, 1, 0);
            typography.Controls.Add(CadDialogTheme.FieldLabel("插入比例"), 2, 0); typography.Controls.Add(_insertScale, 3, 0);
            _font.DropDownStyle = ComboBoxStyle.DropDownList; _font.Items.AddRange(FrameCreationService.GetTextStyleNames(_document)); _font.SelectedItem = DraftingStandardService.GetTextStyleName(DraftingStandardProfile.BodyTextKey); if (_font.SelectedIndex < 0 && _font.Items.Count > 0) _font.SelectedIndex = 0;
            typography.Controls.Add(CadDialogTheme.FieldLabel("字体样式"), 0, 1); typography.Controls.Add(_font, 1, 1);
            typography.Controls.Add(CadDialogTheme.FieldLabel("颜色"), 2, 1);
            _color.Text = string.Empty; _color.Width = 52; _color.Height = CadDialogTheme.ControlHeight; _color.MinimumSize = new Size(52, CadDialogTheme.ControlHeight); _color.BackColor = DisplayColor(_acColor); _color.BorderColor = CadDialogTheme.Border; _color.Click += (s, e) => ChooseColor();
            var colorHost = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Surface }; colorHost.Controls.Add(_color); typography.Controls.Add(colorHost, 3, 1);
            settings.Controls.Add(typography, 0, 7);
            settings.Controls.Add(new Label { Text = "插入后可在 CAD 中继续调整表格位置和样式。", Dock = DockStyle.Top, Height = 36, ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 8);
            settingsCard.Controls.Add(settings); workspace.Controls.Add(settingsCard, 2, 0);
            LoadSettings();

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 9, 0, 0), BackColor = CadDialogTheme.Canvas };
            actions.Controls.Add(CadDialogTheme.Button("关闭", Close)); actions.Controls.Add(CadDialogTheme.Button("插入目录", Insert, true)); outer.Controls.Add(actions, 0, 2);
        }

        private void SetAllBuildings(bool selected)
        {
            for (var index = 0; index < _buildings.Items.Count; index++)
                _buildings.SetItemChecked(index, selected);
        }

        private void Insert()
        {
            if (!int.TryParse(_rows.Text, out var rows) || rows < 1 || !double.TryParse(_rowHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rowHeight) || rowHeight <= 0 || !double.TryParse(_textHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var textHeight) || textHeight <= 0 || !TryParseDrawingScale(_insertScale.Text, out var scale))
            { MessageBox.Show(this, "行数、行高、文字高度必须有效；图纸比例请输入例如 1:20、1:50 或 1:100。", "插入目录"); return; }
            var widths = _widthBoxes.Select(x => double.TryParse(x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0).ToArray();
            if (widths.Any(x => x <= 0)) { MessageBox.Show(this, "请分别填写五列的正数列宽。", "插入目录"); return; }
            var selected = _buildings.CheckedItems.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedSheets = _sheets.Where(s => selected.Contains(string.IsNullOrWhiteSpace(s.Building) ? "未分组" : s.Building)).ToList();
            if (selectedSheets.Count == 0) { MessageBox.Show(this, "请至少选择一个子项目。", "插入目录"); return; }
            if (!_columnChecks.Any(x => x.Checked)) { MessageBox.Show(this, "请至少勾选一列目录内容。", "插入目录"); return; }
            SaveSettings();
            try { BatchPdfPublisher.Commands.StartCatalogInsert(selectedSheets, new CatalogSettings { IncludeBuilding = _columnChecks[0].Checked, IncludeNumber = _columnChecks[1].Checked, IncludeName = _columnChecks[2].Checked, IncludePaper = _columnChecks[3].Checked, IncludeScale = _columnChecks[4].Checked, RowsPerPage = rows, RowHeight = rowHeight, TextHeight = textHeight, Scale = scale, ColumnWidths = widths, Font = _font.Text, Color = _acColor }, _done); Close(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowError(Exception ex) { BeginInvoke(new Action(() => MessageBox.Show(this, "插入目录失败：\n" + ex.Message, "插入目录", MessageBoxButtons.OK, MessageBoxIcon.Error))); }

        private void ChooseColor()
        {
            var dialog = new AcColorDialog();
            dialog.Color = _acColor;
            if (dialog.ShowDialog() == DialogResult.OK) { _acColor = dialog.Color; _color.BackColor = DisplayColor(_acColor); }
        }

        private static Color DisplayColor(AcColor color)
        {
            if (color == null) return Color.White;
            if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor) return Color.FromArgb(color.Red, color.Green, color.Blue);
            var aci = color.ColorIndex; var map = new[] { Color.Black, Color.Red, Color.Yellow, Color.Green, Color.Cyan, Color.Blue, Color.Magenta, Color.White, Color.Gray, Color.LightGray };
            return aci >= 0 && aci < map.Length ? map[aci] : Color.White;
        }
        private static TextBox Box(string text) { return new TextBox { Text = text }; }
        private static CheckBox Check(string text, bool value) { return new CadToggleSwitch { Text = text, Checked = value }; }
        private static PublisherForm.ThemedComboBox Preset(params string[] values) { var box = new PublisherForm.ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
        private static PublisherForm.ThemedComboBox RatioPreset(params string[] values) { var box = new PublisherForm.ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 120 }; box.Items.AddRange(values); box.SelectedIndex = 3; return box; }
        private static bool TryParseDrawingScale(string text, out double scale)
        {
            scale = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim().Replace('：', ':').Replace('／', '/');
            var separator = value.IndexOf(':'); if (separator < 0) separator = value.IndexOf('/');
            if (separator >= 0)
            {
                var leftText = value.Substring(0, separator).Trim(); var rightText = value.Substring(separator + 1).Trim();
                if (!double.TryParse(leftText, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) || !double.TryParse(rightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var right) || left <= 0 || right <= 0) return false;
                scale = right / left; return scale > 0;
            }
            // 兼容旧版保存的纯数字比例；新界面推荐使用 1:N。
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0;
        }
        private void SaveSettings() { try { var path = UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings"); var vals = new[] { _rows.Text, _rowHeight.Text, string.Join(",", _widthBoxes.Select(x => x.Text)), _textHeight.Text, _insertScale.Text, _font.Text, _acColor.ColorMethod.ToString(), _acColor.ColorIndex.ToString(), string.Join("", _columnChecks.Select(x => x.Checked ? "1" : "0")) }; System.IO.File.WriteAllLines(path, vals); } catch { } }
        private void LoadSettings() { try { var path = UserDataPaths.SettingsFile("catalog.settings", "BatchPdfPublisher.catalog.settings"); if (!System.IO.File.Exists(path)) return; var vals = System.IO.File.ReadAllLines(path); if (vals.Length > 0) _rows.Text = vals[0]; if (vals.Length > 1) _rowHeight.Text = vals[1]; if (vals.Length > 2) { var widths = vals[2].Split(','); for (var i = 0; i < _widthBoxes.Length && i < widths.Length; i++) _widthBoxes[i].Text = widths[i]; } if (vals.Length > 3 && _textHeight.Items.Contains(vals[3])) _textHeight.SelectedItem = vals[3]; if (vals.Length > 4 && _insertScale.Items.Contains(vals[4])) _insertScale.SelectedItem = vals[4]; if (vals.Length > 5 && _font.Items.Contains(vals[5])) _font.SelectedItem = vals[5]; if (vals.Length > 6 && vals[6].IndexOf("ByAci", StringComparison.OrdinalIgnoreCase) >= 0 && vals.Length > 7 && short.TryParse(vals[7], out var aci)) { _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci); _color.BackColor = DisplayColor(_acColor); } if (vals.Length > 8) for (var i = 0; i < _columnChecks.Length && i < vals[8].Length; i++) _columnChecks[i].Checked = vals[8][i] == '1'; } catch { } }
        private static Label FieldLabel(string text) { return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Height = 32, Margin = new Padding(0, 2, 8, 2), AutoEllipsis = true }; }
        private static void Add(TableLayoutPanel root, string label, Control control, int row) { root.Controls.Add(FieldLabel(label), 0, row); control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 2, 0, 2); root.Controls.Add(control, 1, row); }
        private static Button Button(string text, EventHandler action, bool accent = false) { var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(0, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(25, 54, 99), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3) }; button.FlatAppearance.BorderColor = accent ? Color.FromArgb(104, 145, 185) : Color.FromArgb(190, 201, 216); button.Click += action; return button; }
    }
}
