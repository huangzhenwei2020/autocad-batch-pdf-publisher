using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class CustomDoorWindowDivisionForm : Form
    {
        private static readonly string[] Openings = { "固定", "左平开", "右平开", "左推拉", "右推拉", "双向推拉", "上悬", "下悬", "百叶" };
        private static readonly string[] Materials = { "玻璃", "实板", "百叶", "无" };
        private readonly DoorWindowScheduleItem _source;
        private readonly DoorWindowLayoutEditorControl _editor = new DoorWindowLayoutEditorControl();
        private readonly NumericUpDown _selectedWidth = SizeBox();
        private readonly NumericUpDown _selectedHeight = SizeBox();
        private readonly ComboBox _opening = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Height = 28 };
        private readonly ComboBox _material = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 82, Height = 28 };
        private readonly CheckBox _isDoor = new CheckBox { Text = "当前面板为门", AutoSize = true, Margin = new Padding(10, 7, 4, 0) };
        private readonly CheckBox _hasInstallationGap = OptionBox("安装缝");
        private readonly NumericUpDown _installationGap = ProfileBox();
        private readonly CheckBox _hasOuterFrame = OptionBox("外框");
        private readonly NumericUpDown _outerFrameWidth = ProfileBox();
        private readonly CheckBox _hasMullion = OptionBox("分隔框");
        private readonly NumericUpDown _mullionWidth = ProfileBox();
        private readonly ComboBox _doorFrameType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72, Height = 28 };
        private readonly Label _message = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private bool _updating;

        public CustomDoorWindowDivisionForm(DoorWindowScheduleItem source)
        {
            _source = source ?? throw new ArgumentNullException("source");
            Text = "门窗分格编辑 — " + (source.Code ?? "未编号"); StartPosition = FormStartPosition.CenterParent;
            Width = 1040; Height = 720; MinimumSize = new Size(820, 560); Font = new Font("Microsoft YaHei UI", 9F);
            Build(); LoadLayout(); ValidateLayout();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10), BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var splitVertical = ButtonFor("竖向分隔当前框"); splitVertical.Click += (s, e) => _editor.SplitSelected(true); tools.Controls.Add(splitVertical);
            var splitHorizontal = ButtonFor("横向分隔当前框"); splitHorizontal.Click += (s, e) => _editor.SplitSelected(false); tools.Controls.Add(splitHorizontal);
            var merge = ButtonFor("合并相邻框"); merge.Click += (s, e) => { if (!_editor.MergeSelected()) MessageBox.Show(this, "当前框没有可直接合并的完整相邻框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(merge);
            var remove = ButtonFor("删除/恢复当前框"); remove.Click += (s, e) => { if (!_editor.ToggleSelectedDeleted()) MessageBox.Show(this, "至少要保留一个未删除的门窗面板。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(remove);
            var equalWidth = ButtonFor("所选同行等宽"); equalWidth.Click += (s, e) => { if (!_editor.EqualizeSelectedWidths()) ShowEqualizeHint(); }; tools.Controls.Add(equalWidth);
            var equalHeight = ButtonFor("所选同列等高"); equalHeight.Click += (s, e) => { if (!_editor.EqualizeSelectedHeights()) ShowEqualizeHint(); }; tools.Controls.Add(equalHeight);
            var reset = ButtonFor("恢复完整外框"); reset.Click += (s, e) => { if (MessageBox.Show(this, "恢复后现有分格会被清除，是否继续？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) _editor.ResetToFullFrame(); }; tools.Controls.Add(reset); header.Controls.Add(tools, 0, 0);
            var properties = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            properties.Controls.Add(LabelFor("当前框宽")); properties.Controls.Add(_selectedWidth); properties.Controls.Add(LabelFor("高")); properties.Controls.Add(_selectedHeight);
            _opening.Items.AddRange(Openings.Cast<object>().ToArray()); properties.Controls.Add(LabelFor("开启")); properties.Controls.Add(_opening);
            _material.Items.AddRange(Materials.Cast<object>().ToArray()); properties.Controls.Add(LabelFor("材质")); properties.Controls.Add(_material);
            _isDoor.Enabled = _source.ElevationType == "门联窗"; properties.Controls.Add(_isDoor);
            properties.Controls.Add(LabelFor("门套")); _doorFrameType.Items.AddRange(new object[] { "N型", "口型" }); properties.Controls.Add(_doorFrameType); header.Controls.Add(properties, 0, 1);
            var construction = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            construction.Controls.Add(_hasInstallationGap); construction.Controls.Add(_installationGap); construction.Controls.Add(LabelFor("mm"));
            construction.Controls.Add(_hasOuterFrame); construction.Controls.Add(_outerFrameWidth); construction.Controls.Add(LabelFor("mm"));
            construction.Controls.Add(_hasMullion); construction.Controls.Add(_mullionWidth); construction.Controls.Add(LabelFor("mm（开启/推拉相邻处自动按 2 倍处理）")); header.Controls.Add(construction, 0, 2);
            header.Controls.Add(new Label { Text = "单击选择；Shift+单击增加或取消选择；拖动分隔线调整。宽度从左到右、高度从上到下确定，右下角面板承接最终剩余尺寸。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
            root.Controls.Add(header, 0, 0); root.Controls.Add(_editor, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _message.Margin = new Padding(3, 12, 0, 0); footer.Controls.Add(_message, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var save = ButtonFor("保存门窗分格"); save.Click += (s, e) => SaveAndClose(); actions.Controls.Add(save);
            var cancel = ButtonFor("取消"); cancel.Click += (s, e) => Close(); actions.Controls.Add(cancel); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);

            _editor.LayoutChanged += (s, e) => ValidateLayout(); _editor.SelectedCellChanged += (s, e) => LoadSelectedCell();
            _selectedWidth.ValueChanged += (s, e) => { if (_updating) return; if (!_editor.SetSelectedWidth((double)_selectedWidth.Value)) ShowRemainderHint("宽度"); };
            _selectedHeight.ValueChanged += (s, e) => { if (_updating) return; if (!_editor.SetSelectedHeight((double)_selectedHeight.Value)) ShowRemainderHint("高度"); };
            _opening.SelectedIndexChanged += (s, e) => { if (!_updating) _editor.SetSelectedOpening(Convert.ToString(_opening.SelectedItem)); };
            _material.SelectedIndexChanged += (s, e) => { if (!_updating) _editor.SetSelectedMaterial(Convert.ToString(_material.SelectedItem)); };
            _isDoor.CheckedChanged += (s, e) => { if (!_updating) _editor.SetSelectedDoor(_isDoor.Checked); };
            _hasInstallationGap.CheckedChanged += (s, e) => { _installationGap.Enabled = _hasInstallationGap.Checked; if (!_updating) ResizeForConstructionChange(); };
            _installationGap.ValueChanged += (s, e) => { if (!_updating) ResizeForConstructionChange(); };
            _hasOuterFrame.CheckedChanged += (s, e) => { _outerFrameWidth.Enabled = _hasOuterFrame.Checked; if (!_updating) UpdateConstructionPreview(); };
            _hasMullion.CheckedChanged += (s, e) => { _mullionWidth.Enabled = _hasMullion.Checked; if (!_updating) UpdateConstructionPreview(); };
            _outerFrameWidth.ValueChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _mullionWidth.ValueChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _doorFrameType.SelectedIndexChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
        }

        private void LoadLayout()
        {
            _updating = true;
            _hasInstallationGap.Checked = _source.HasInstallationGap; _installationGap.Value = ClampDecimal(_source.InstallationGap, _installationGap); _installationGap.Enabled = _hasInstallationGap.Checked;
            _hasOuterFrame.Checked = _source.HasOuterFrame; _hasMullion.Checked = _source.HasMullion;
            _doorFrameType.SelectedItem = string.IsNullOrWhiteSpace(_source.DoorFrameType) ? "N型" : _source.DoorFrameType; if (_doorFrameType.SelectedIndex < 0) _doorFrameType.SelectedIndex = 0;
            var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d;
            var width = Math.Max(1d, _source.Width - gap * 2d); var height = Math.Max(1d, _source.Height - gap * 2d);
            var layout = DoorWindowElevationGeometryBuilder.ParseCellLayout(_source.CustomCellLayout);
            if (layout.Count == 0) layout = ConvertExistingLayout(width, height);
            _outerFrameWidth.Value = ClampDecimal(_source.OuterFrameWidth > 0 ? _source.OuterFrameWidth : 50d, _outerFrameWidth);
            _mullionWidth.Value = ClampDecimal(_source.MullionWidth > 0 ? _source.MullionWidth : 50d, _mullionWidth);
            _outerFrameWidth.Enabled = _hasOuterFrame.Checked; _mullionWidth.Enabled = _hasMullion.Checked;
            _editor.LoadLayout(width, height, layout); _updating = false; UpdateConstructionPreview(); LoadSelectedCell();
        }

        private List<DoorWindowLayoutCell> ConvertExistingLayout(double width, double height)
        {
            try
            {
                var temporary = Copy(_source);
                if (temporary.DivisionPreset == "自定义") { temporary.CustomColumnWidths = null; temporary.CustomRowHeights = null; }
                var geometry = DoorWindowElevationGeometryBuilder.Build(temporary);
                var modes = (temporary.CellOpeningModes ?? string.Empty).Split('|'); var result = new List<DoorWindowLayoutCell>();
                for (var index = 0; index < geometry.Cells.Count; index++)
                {
                    var cell = geometry.Cells[index]; var mode = index < modes.Length && Openings.Contains(modes[index]) ? modes[index] : modes.Length > index && modes[index] == "推拉" ? "右推拉" : Openings.Contains(temporary.OpeningMode) ? temporary.OpeningMode : temporary.OpeningMode == "推拉" ? "右推拉" : "固定";
                    result.Add(new DoorWindowLayoutCell { Left = cell.Left - geometry.FrameLeft, Bottom = cell.Bottom - geometry.FrameBottom, Right = cell.Right - geometry.FrameLeft, Top = cell.Top - geometry.FrameBottom, Opening = mode, Material = string.IsNullOrWhiteSpace(cell.Material) ? "无" : cell.Material, IsDoor = cell.IsDoor });
                }
                return result;
            }
            catch { return new List<DoorWindowLayoutCell> { new DoorWindowLayoutCell { Left = 0, Bottom = 0, Right = width, Top = height, Opening = "固定", Material = "无", IsDoor = _source.ElevationType == "门联窗" } }; }
        }

        private void LoadSelectedCell()
        {
            var cell = _editor.SelectedCell; _updating = true;
            try
            {
                if (cell == null) { _selectedWidth.Enabled = _selectedHeight.Enabled = _opening.Enabled = _material.Enabled = _isDoor.Enabled = false; return; }
                _selectedWidth.Enabled = _selectedHeight.Enabled = true; _opening.Enabled = _material.Enabled = !cell.IsDeleted; _isDoor.Enabled = (_source.ElevationType == "门联窗" || _source.ElevationType == "门") && !cell.IsDeleted;
                _selectedWidth.Value = ClampDecimal(cell.Right - cell.Left, _selectedWidth); _selectedHeight.Value = ClampDecimal(cell.Top - cell.Bottom, _selectedHeight);
                _opening.SelectedItem = Openings.Contains(cell.Opening) ? cell.Opening : cell.Opening == "推拉" ? "右推拉" : "固定"; _material.SelectedItem = Materials.Contains(cell.Material) ? cell.Material : "无"; _isDoor.Checked = cell.IsDoor;
            }
            finally { _updating = false; }
        }

        private void ValidateLayout()
        {
            try
            {
                var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d;
                DoorWindowElevationGeometryBuilder.ValidateCellLayout(_editor.Cells.ToList(), Math.Max(1d, _source.Width - gap * 2d), Math.Max(1d, _source.Height - gap * 2d));
                _message.Text = "分格有效。当前选择 " + _editor.SelectionCount + " 个面板；Shift+单击可多选或减选。"; _message.ForeColor = Color.FromArgb(20, 112, 65);
            }
            catch (Exception exception) { _message.Text = exception.Message; _message.ForeColor = Color.Firebrick; }
            LoadSelectedCell();
        }

        private void ShowRemainderHint(string dimension)
        {
            _message.Text = "当前框位于右下角末端，它的" + dimension + "由前面分隔线和总尺寸自动确定；宽度请拖左侧分隔线，高度请拖上侧分隔线。"; _message.ForeColor = Color.FromArgb(165, 95, 15); LoadSelectedCell();
        }

        private void ShowEqualizeHint() { _message.Text = "请按住 Shift 选择至少两个连续的同行或同列面板，再执行均分。"; _message.ForeColor = Color.FromArgb(165, 95, 15); }

        private void SaveAndClose()
        {
            ValidateLayout(); if (_message.ForeColor == Color.Firebrick) { MessageBox.Show(this, _message.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _source.DivisionPreset = "自定义"; _source.OpeningMode = "自定义";
            _source.CustomCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(_editor.OrderedCells);
            _source.CellOpeningModes = string.Join("|", _editor.OrderedCells.Select(x => x.Opening ?? "固定"));
            _source.CustomColumnRatios = _source.CustomRowRatios = "1"; _source.CustomColumnWidths = _source.CustomRowHeights = null;
            _source.HasInstallationGap = _hasInstallationGap.Checked; _source.InstallationGap = (double)_installationGap.Value;
            _source.HasOuterFrame = _hasOuterFrame.Checked; _source.OuterFrameWidth = (double)_outerFrameWidth.Value;
            _source.HasMullion = _hasMullion.Checked; _source.MullionWidth = (double)_mullionWidth.Value; _source.DoorFrameType = Convert.ToString(_doorFrameType.SelectedItem) ?? "N型";
            var gap = _source.HasInstallationGap ? _source.InstallationGap : 0d; var clearWidth = _source.Width - gap * 2d; var door = _editor.Cells.FirstOrDefault(x => x.IsDoor && !x.IsDeleted);
            if (door != null) { _source.DoorPlacement = Math.Abs((door.Left + door.Right) / 2d - clearWidth / 2d) < 1d ? "居中" : door.Left < clearWidth / 2d ? "靠左" : "靠右"; _source.DoorEdgeDistance = _source.DoorPlacement == "靠右" ? Math.Max(0d, clearWidth - door.Right) : door.Left; }
            DialogResult = DialogResult.OK; Close();
        }

        private static DoorWindowScheduleItem Copy(DoorWindowScheduleItem x)
        {
            return new DoorWindowScheduleItem { Code = x.Code, Width = x.Width, Height = x.Height, HasInstallationGap = x.HasInstallationGap, InstallationGap = x.InstallationGap, HasOuterFrame = x.HasOuterFrame, OuterFrameWidth = x.OuterFrameWidth, HasMullion = x.HasMullion, MullionWidth = x.MullionWidth, DoorFrameType = x.DoorFrameType, ElevationType = x.ElevationType, DivisionPreset = x.DivisionPreset, OpeningMode = x.OpeningMode, CustomColumnRatios = x.CustomColumnRatios, CustomRowRatios = x.CustomRowRatios, CustomColumnWidths = x.CustomColumnWidths, CustomRowHeights = x.CustomRowHeights, CustomCellLayout = null, CellOpeningModes = x.CellOpeningModes, DoorPlacement = x.DoorPlacement, DoorEdgeDistance = x.DoorEdgeDistance };
        }

        private void ResizeForConstructionChange()
        {
            var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d; var width = _source.Width - gap * 2d; var height = _source.Height - gap * 2d;
            if (width <= 1d || height <= 1d) { _message.Text = "安装缝不能大于门窗洞口尺寸。"; _message.ForeColor = Color.Firebrick; return; }
            _editor.ResizeLayout(width, height); UpdateConstructionPreview();
        }

        private void UpdateConstructionPreview()
        {
            _editor.SetInstallationGap(_hasInstallationGap.Checked, (double)_installationGap.Value);
            _editor.SetProfileWidths((double)_outerFrameWidth.Value, (double)_mullionWidth.Value);
            _editor.SetConstruction(_hasOuterFrame.Checked, _hasMullion.Checked, Convert.ToString(_doorFrameType.SelectedItem));
        }

        private static NumericUpDown SizeBox() { return new NumericUpDown { Minimum = 1, Maximum = 100000, DecimalPlaces = 1, Increment = 10, Width = 82, Height = 28 }; }
        private static NumericUpDown ProfileBox() { return new NumericUpDown { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Increment = 5, Width = 68, Height = 28, Value = 50 }; }
        private static CheckBox OptionBox(string text) { return new CheckBox { Text = text, Checked = true, AutoSize = true, Margin = new Padding(10, 7, 2, 0) }; }
        private static decimal ClampDecimal(double value, NumericUpDown box) { return Math.Max(box.Minimum, Math.Min(box.Maximum, (decimal)value)); }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 7, 3, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Padding = new Padding(8, 0, 8, 0) }; }
    }
}
