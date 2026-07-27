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
    public sealed class CatalogInsertForm : Form
    {
        private readonly Document _document;
        private readonly System.Collections.Generic.IList<SheetItem> _sheets;
        private readonly Action _done;
        private readonly CheckedListBox _buildings = new CheckedListBox();
        private readonly CheckBox[] _columnChecks = { Check("序号", true), Check("图号", true), Check("图名", true), Check("图框", true), Check("比例", true) };
        private readonly TextBox _rows = Box("30"), _rowHeight = Box("7");
        private readonly ComboBox _textHeight = Preset("1.5", "2.5", "3.5", "5", "7", "10", "14", "20");
        // 目录插入比例是图纸比例：1:20、1:50、1:100；可直接编辑输入自定义比例。
        private readonly ComboBox _insertScale = RatioPreset("1:1", "1:20", "1:50", "1:100", "1:200", "1:500");
        private readonly TextBox[] _widthBoxes = { Box("20"), Box("30"), Box("70"), Box("24"), Box("24") };
        private readonly ComboBox _font = new ComboBox();
        private readonly Button _color = new Button();
        private AcColor _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);

        public CatalogInsertForm(Document document, System.Collections.Generic.IList<SheetItem> sheets, Action done)
        {
            _document = document; _sheets = sheets; _done = done;
            Text = "插入图纸目录"; Width = 760; Height = 650; MinimumSize = new Size(660, 540); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; SizeGripStyle = SizeGripStyle.Show; Build();
        }

        private void Build()
        {
            BackColor = Color.FromArgb(247, 249, 252);
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(16) };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(outer);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (var row = 1; row < 8; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.Controls.Add(root, 0, 0);
            var selectionLabel = FieldLabel("选择子项目"); selectionLabel.TextAlign = ContentAlignment.TopLeft; selectionLabel.Padding = new Padding(0, 8, 0, 0);
            root.Controls.Add(selectionLabel, 0, 0);
            var buildingPicker = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
            buildingPicker.RowStyles.Add(new RowStyle(SizeType.AutoSize)); buildingPicker.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var buildingActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            buildingActions.Controls.Add(Button("全部勾选", (s, e) => SetAllBuildings(true)));
            buildingActions.Controls.Add(Button("全部取消", (s, e) => SetAllBuildings(false)));
            buildingPicker.Controls.Add(buildingActions, 0, 0);
            _buildings.CheckOnClick = true; _buildings.IntegralHeight = false; _buildings.MinimumSize = new Size(0, 130); _buildings.Dock = DockStyle.Fill;
            foreach (var name in _sheets.Select(s => string.IsNullOrWhiteSpace(s.Building) ? "未分组" : s.Building).Distinct()) _buildings.Items.Add(name, true);
            buildingPicker.Controls.Add(_buildings, 0, 1);
            root.Controls.Add(buildingPicker, 1, 0);
            root.Controls.Add(FieldLabel("目录列"), 0, 1); var columnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true }; columnPanel.Controls.AddRange(_columnChecks); root.Controls.Add(columnPanel, 1, 1);
            Add(root, "每页行数", _rows, 2); Add(root, "行高", _rowHeight, 3);
            root.Controls.Add(FieldLabel("列宽（分别设置）"), 0, 4);
            var widths = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            var labels = new[] { "序号", "图号", "图名", "图框", "比例" };
            for (var i = 0; i < _widthBoxes.Length; i++) { var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 8, 0) }; panel.Controls.Add(new Label { Text = labels[i], AutoSize = true, Margin = new Padding(0, 7, 3, 0) }); panel.Controls.Add(_widthBoxes[i]); widths.Controls.Add(panel); }
            root.Controls.Add(widths, 1, 4);
            Add(root, "文字高度", _textHeight, 5); Add(root, "插入比例", _insertScale, 6);
            LoadSettings();
            root.Controls.Add(FieldLabel("字体样式 / 颜色"), 0, 7);
            var styleLine = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            _font.DropDownStyle = ComboBoxStyle.DropDownList; _font.Width = 210; _font.Items.AddRange(FrameCreationService.GetTextStyleNames(_document)); _font.SelectedItem = "黑体"; if (_font.SelectedIndex < 0 && _font.Items.Count > 0) _font.SelectedIndex = 0;
            _color.Text = string.Empty; _color.Width = 32; _color.Height = 30; _color.MinimumSize = new Size(32, 30); _color.Margin = new Padding(3, 0, 3, 0); _color.FlatStyle = FlatStyle.Flat; _color.BackColor = DisplayColor(_acColor); _color.Click += (s, e) => ChooseColor(); styleLine.Controls.Add(_font); styleLine.Controls.Add(_color);
            root.Controls.Add(styleLine, 1, 7);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            actions.Controls.Add(Button("关闭", (s, e) => Close())); actions.Controls.Add(Button("插入目录", (s, e) => Insert(), true)); outer.Controls.Add(actions, 0, 1);
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
        private static TextBox Box(string text) { return new TextBox { Text = text, Height = 30, Width = 70 }; }
        private static CheckBox Check(string text, bool value) { return new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3, 5, 10, 3) }; }
        private static ComboBox Preset(params string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Height = 30 }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
        private static ComboBox RatioPreset(params string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 100, Height = 30, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems }; box.Items.AddRange(values); box.SelectedIndex = 3; return box; }
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
        private void SaveSettings() { try { var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher.catalog.settings"); var vals = new[] { _rows.Text, _rowHeight.Text, string.Join(",", _widthBoxes.Select(x => x.Text)), _textHeight.Text, _insertScale.Text, _font.Text, _acColor.ColorMethod.ToString(), _acColor.ColorIndex.ToString(), string.Join("", _columnChecks.Select(x => x.Checked ? "1" : "0")) }; System.IO.File.WriteAllLines(path, vals); } catch { } }
        private void LoadSettings() { try { var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatchPdfPublisher.catalog.settings"); if (!System.IO.File.Exists(path)) return; var vals = System.IO.File.ReadAllLines(path); if (vals.Length > 0) _rows.Text = vals[0]; if (vals.Length > 1) _rowHeight.Text = vals[1]; if (vals.Length > 2) { var widths = vals[2].Split(','); for (var i = 0; i < _widthBoxes.Length && i < widths.Length; i++) _widthBoxes[i].Text = widths[i]; } if (vals.Length > 3 && _textHeight.Items.Contains(vals[3])) _textHeight.SelectedItem = vals[3]; if (vals.Length > 4 && _insertScale.Items.Contains(vals[4])) _insertScale.SelectedItem = vals[4]; if (vals.Length > 5 && _font.Items.Contains(vals[5])) _font.SelectedItem = vals[5]; if (vals.Length > 6 && vals[6].IndexOf("ByAci", StringComparison.OrdinalIgnoreCase) >= 0 && vals.Length > 7 && short.TryParse(vals[7], out var aci)) { _acColor = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci); _color.BackColor = DisplayColor(_acColor); } if (vals.Length > 8) for (var i = 0; i < _columnChecks.Length && i < vals[8].Length; i++) _columnChecks[i].Checked = vals[8][i] == '1'; } catch { } }
        private static Label FieldLabel(string text) { return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Height = 32, Margin = new Padding(0, 2, 8, 2), AutoEllipsis = true }; }
        private static void Add(TableLayoutPanel root, string label, Control control, int row) { root.Controls.Add(FieldLabel(label), 0, row); control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 2, 0, 2); root.Controls.Add(control, 1, row); }
        private static Button Button(string text, EventHandler action, bool accent = false) { var button = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(0, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(25, 54, 99), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3) }; button.FlatAppearance.BorderColor = accent ? Color.FromArgb(104, 145, 185) : Color.FromArgb(190, 201, 216); button.Click += action; return button; }
    }
}
