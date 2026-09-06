using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class CustomDoorWindowDivisionForm : DpiAwareForm
    {
        private static readonly string[] Openings = { "固定", "左平开", "右平开", "左推拉", "右推拉", "双向推拉", "上悬", "下悬", "百叶" };
        private static readonly string[] Materials = { "玻璃", "实板", "百叶", "无" };
        private readonly DoorWindowScheduleItem _source;
        private readonly DoorWindowLayoutEditorControl _editor = new DoorWindowLayoutEditorControl { MinimumSize = new Size(300, 260) };
        private readonly DoorWindowLayoutEditorControl _leftEditor = new DoorWindowLayoutEditorControl { MinimumSize = new Size(170, 260) };
        private readonly DoorWindowLayoutEditorControl _rightEditor = new DoorWindowLayoutEditorControl { MinimumSize = new Size(170, 260) };
        private readonly TableLayoutPanel _faces = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0), BackColor = Color.White };
        private DoorWindowLayoutEditorControl _activeEditor;
        private readonly NumericUpDown _selectedWidth = SizeBox();
        private readonly NumericUpDown _selectedHeight = SizeBox();
        private readonly NumericUpDown _mouseSnapStep = SnapStepBox();
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
        private readonly CheckBox _hasDoorFrame = OptionBox("门边框");
        private readonly NumericUpDown _doorFrameWidth = ProfileBox();
        private readonly ComboBox _bayLeftSide = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56, Height = 28 };
        private readonly NumericUpDown _bayLeftDepth = DepthBox();
        private readonly ComboBox _bayRightSide = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56, Height = 28 };
        private readonly NumericUpDown _bayRightDepth = DepthBox();
        private readonly Label _message = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private bool _updating;

        public CustomDoorWindowDivisionForm(DoorWindowScheduleItem source)
        {
            _source = source ?? throw new ArgumentNullException("source");
            Text = "门窗分格编辑 — " + (source.Code ?? "未编号"); StartPosition = FormStartPosition.CenterParent;
            Width = 1320; Height = 820; MinimumSize = new Size(1000, 680); Font = new Font("Microsoft YaHei UI", 9F);
            _activeEditor = _editor; Build(); LoadLayout(); ValidateLayout();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10), BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2 };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var settingsPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0), BackColor = Color.FromArgb(248, 250, 252) };
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 5, ColumnCount = 1, Padding = new Padding(8) };
            for (var row = 0; row < 5; row++) header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var tools = VerticalButtonRow();
            var splitVertical = ButtonFor("竖向分隔当前框"); splitVertical.Click += (s, e) => ActiveEditor().SplitSelected(true); tools.Controls.Add(splitVertical);
            var splitHorizontal = ButtonFor("横向分隔当前框"); splitHorizontal.Click += (s, e) => ActiveEditor().SplitSelected(false); tools.Controls.Add(splitHorizontal);
            var merge = ButtonFor("合并所选框"); merge.Click += (s, e) => { if (!ActiveEditor().MergeSelected()) MessageBox.Show(this, "请用 Shift 选择当前面中能组成完整矩形的相邻框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(merge);
            var remove = ButtonFor("删除/恢复当前框"); remove.Click += (s, e) => { if (!ActiveEditor().ToggleSelectedDeleted()) MessageBox.Show(this, "当前面至少要保留一个未删除的窗格。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(remove);
            var equalWidth = ButtonFor("所选同行等宽"); equalWidth.Click += (s, e) => { if (!ActiveEditor().EqualizeSelectedWidths()) ShowEqualizeHint(); }; tools.Controls.Add(equalWidth);
            var equalHeight = ButtonFor("所选同列等高"); equalHeight.Click += (s, e) => { if (!ActiveEditor().EqualizeSelectedHeights()) ShowEqualizeHint(); }; tools.Controls.Add(equalHeight);
            var center = ButtonFor("所选居中"); center.Click += (s, e) => { if (!ActiveEditor().CenterSelected()) MessageBox.Show(this, "请选择当前面中同一行连续面板后再居中。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(center);
            var reset = ButtonFor("恢复完整外框"); reset.Click += (s, e) => { if (MessageBox.Show(this, "恢复后当前面的分格会被清除，是否继续？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) ActiveEditor().ResetToFullFrame(); }; tools.Controls.Add(reset); FormatVerticalButtons(tools, 380); header.Controls.Add(tools, 0, 0);
            var properties = SettingsGrid(2, 7);
            properties.ColumnStyles.Clear(); properties.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); properties.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            properties.Controls.Add(GridLabel("当前框宽"), 0, 0); properties.Controls.Add(_selectedWidth, 1, 0);
            properties.Controls.Add(GridLabel("当前框高"), 0, 1); properties.Controls.Add(_selectedHeight, 1, 1);
            _opening.Items.AddRange(Openings.Cast<object>().ToArray()); properties.Controls.Add(GridLabel("开启方式"), 0, 2); properties.Controls.Add(_opening, 1, 2);
            _material.Items.AddRange(Materials.Cast<object>().ToArray()); properties.Controls.Add(GridLabel("材质"), 0, 3); properties.Controls.Add(_material, 1, 3);
            _isDoor.Enabled = (_source.ElevationType ?? string.Empty).Contains("门"); properties.Controls.Add(_isDoor, 0, 4); properties.SetColumnSpan(_isDoor, 2);
            properties.Controls.Add(GridLabel("门套类型"), 0, 5); _doorFrameType.Items.AddRange(new object[] { "N型", "口型" }); properties.Controls.Add(_doorFrameType, 1, 5);
            _hasDoorFrame.Text = "门边框宽（mm）"; properties.Controls.Add(_hasDoorFrame, 0, 6); properties.Controls.Add(_doorFrameWidth, 1, 6);
            header.Controls.Add(Section("当前面板参数", properties), 0, 1);

            var construction = SettingsGrid(3, 5);
            construction.ColumnStyles.Clear(); construction.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); construction.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); construction.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            construction.Controls.Add(_hasInstallationGap, 0, 0); construction.Controls.Add(_installationGap, 1, 0); construction.Controls.Add(GridLabel("mm"), 2, 0);
            construction.Controls.Add(_hasOuterFrame, 0, 1); construction.Controls.Add(_outerFrameWidth, 1, 1); construction.Controls.Add(GridLabel("mm"), 2, 1);
            construction.Controls.Add(_hasMullion, 0, 2); construction.Controls.Add(_mullionWidth, 1, 2); construction.Controls.Add(GridLabel("mm"), 2, 2);
            var mullionNote = new Label { Text = "相邻分隔框结合处按 2 倍宽度计算。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 0, 3, 5) };
            construction.Controls.Add(mullionNote, 0, 3); construction.SetColumnSpan(mullionNote, 3);
            construction.Controls.Add(GridLabel("鼠标移动步长"), 0, 4); construction.Controls.Add(_mouseSnapStep, 1, 4); construction.Controls.Add(GridLabel("mm（0 表示不吸附）"), 2, 4);
            header.Controls.Add(Section("构造尺寸", construction), 0, 2);

            var bay = SettingsGrid(4, 2);
            bay.ColumnStyles.Clear(); bay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); bay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65)); bay.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); bay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _bayLeftSide.Items.AddRange(new object[] { "墙", "窗" }); _bayRightSide.Items.AddRange(new object[] { "墙", "窗" });
            bay.Controls.Add(GridLabel("凸窗左转折"), 0, 0); bay.Controls.Add(_bayLeftSide, 1, 0); bay.Controls.Add(_bayLeftDepth, 2, 0); bay.Controls.Add(GridLabel("mm"), 3, 0);
            bay.Controls.Add(GridLabel("右转折"), 0, 1); bay.Controls.Add(_bayRightSide, 1, 1); bay.Controls.Add(_bayRightDepth, 2, 1); bay.Controls.Add(GridLabel("mm"), 3, 1);
            header.Controls.Add(Section("凸窗转折（墙+窗 / 窗+窗 / 窗+墙）", bay), 0, 3);
            header.Controls.Add(new Label { Text = "单击选择，Shift+单击可多选或取消。\r\n拖动分隔线调整；宽度由左到右、高度由上到下确定。", AutoSize = true, MaximumSize = new Size(390, 0), Dock = DockStyle.Top, ForeColor = Color.DimGray, Padding = new Padding(3, 6, 3, 8) }, 0, 4);
            settingsPanel.Controls.Add(header); workspace.Controls.Add(settingsPanel, 0, 0);
            _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _faces.Controls.Add(FaceGroup("左转折面", _leftEditor), 0, 0); _faces.Controls.Add(FaceGroup("正面", _editor), 1, 0); _faces.Controls.Add(FaceGroup("右转折面", _rightEditor), 2, 0);
            workspace.Controls.Add(_faces, 1, 0); root.Controls.Add(workspace, 0, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _message.Margin = new Padding(3, 12, 0, 0); footer.Controls.Add(_message, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var save = ButtonFor("保存门窗分格"); save.Click += (s, e) => SaveAndClose(); actions.Controls.Add(save);
            var cancel = ButtonFor("取消"); cancel.Click += (s, e) => Close(); actions.Controls.Add(cancel); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 1); Controls.Add(root);

            WireEditor(_leftEditor); WireEditor(_editor); WireEditor(_rightEditor);
            _mouseSnapStep.Value = ClampDecimal(LoadMouseSnapStep(), _mouseSnapStep); ApplyMouseSnapStep();
            _mouseSnapStep.ValueChanged += (s, e) => { ApplyMouseSnapStep(); SaveMouseSnapStep((double)_mouseSnapStep.Value); };
            _selectedWidth.ValueChanged += (s, e) => { if (_updating) return; if (!ActiveEditor().SetSelectedWidth((double)_selectedWidth.Value)) ShowRemainderHint("宽度"); };
            _selectedHeight.ValueChanged += (s, e) => { if (_updating) return; if (!ActiveEditor().SetSelectedHeight((double)_selectedHeight.Value)) ShowRemainderHint("高度"); };
            _opening.SelectedIndexChanged += (s, e) => { if (!_updating) ActiveEditor().SetSelectedOpening(Convert.ToString(_opening.SelectedItem)); };
            _material.SelectedIndexChanged += (s, e) => { if (!_updating) ActiveEditor().SetSelectedMaterial(Convert.ToString(_material.SelectedItem)); };
            _isDoor.CheckedChanged += (s, e) => { if (!_updating) ActiveEditor().SetSelectedDoor(_isDoor.Checked); };
            _hasInstallationGap.CheckedChanged += (s, e) => { _installationGap.Enabled = _hasInstallationGap.Checked; if (!_updating) ResizeForConstructionChange(); };
            _installationGap.ValueChanged += (s, e) => { if (!_updating) ResizeForConstructionChange(); };
            _hasOuterFrame.CheckedChanged += (s, e) => { _outerFrameWidth.Enabled = _hasOuterFrame.Checked; if (!_updating) UpdateConstructionPreview(); };
            _hasMullion.CheckedChanged += (s, e) => { _mullionWidth.Enabled = _hasMullion.Checked; if (!_updating) UpdateConstructionPreview(); };
            _outerFrameWidth.ValueChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _mullionWidth.ValueChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _doorFrameType.SelectedIndexChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _hasDoorFrame.CheckedChanged += (s, e) => { _doorFrameWidth.Enabled = _hasDoorFrame.Checked; if (!_updating) UpdateConstructionPreview(); };
            _doorFrameWidth.ValueChanged += (s, e) => { if (!_updating) UpdateConstructionPreview(); };
            _bayLeftSide.SelectedIndexChanged += (s, e) => { if (!_updating) UpdateBayFaces(); };
            _bayRightSide.SelectedIndexChanged += (s, e) => { if (!_updating) UpdateBayFaces(); };
            _bayLeftDepth.ValueChanged += (s, e) => { if (!_updating) ResizeBayFace(_leftEditor, (double)_bayLeftDepth.Value); };
            _bayRightDepth.ValueChanged += (s, e) => { if (!_updating) ResizeBayFace(_rightEditor, (double)_bayRightDepth.Value); };
        }

        private void LoadLayout()
        {
            _updating = true;
            _hasInstallationGap.Checked = _source.HasInstallationGap; _installationGap.Value = ClampDecimal(_source.InstallationGap, _installationGap); _installationGap.Enabled = _hasInstallationGap.Checked;
            _hasOuterFrame.Checked = _source.HasOuterFrame; _hasMullion.Checked = _source.HasMullion;
            var bayEnabled = string.Equals(_source.ElevationType, "凸窗", StringComparison.Ordinal);
            _bayLeftSide.SelectedItem = string.Equals(_source.BayLeftSide, "窗", StringComparison.Ordinal) ? "窗" : "墙";
            _bayRightSide.SelectedItem = string.Equals(_source.BayRightSide, "窗", StringComparison.Ordinal) ? "窗" : "墙";
            _bayLeftDepth.Value = ClampDecimal(_source.BayLeftDepth > 0d ? _source.BayLeftDepth : 600d, _bayLeftDepth);
            _bayRightDepth.Value = ClampDecimal(_source.BayRightDepth > 0d ? _source.BayRightDepth : 600d, _bayRightDepth);
            _bayLeftSide.Enabled = _bayRightSide.Enabled = _bayLeftDepth.Enabled = _bayRightDepth.Enabled = bayEnabled;
            _doorFrameType.SelectedItem = string.IsNullOrWhiteSpace(_source.DoorFrameType) ? "N型" : _source.DoorFrameType; if (_doorFrameType.SelectedIndex < 0) _doorFrameType.SelectedIndex = 0;
            _hasDoorFrame.Checked = _source.DoorFrameWidth > 0d; _doorFrameWidth.Value = ClampDecimal(_source.DoorFrameWidth > 0d ? _source.DoorFrameWidth : 50d, _doorFrameWidth); _doorFrameWidth.Enabled = _hasDoorFrame.Checked;
            var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d;
            var width = Math.Max(1d, _source.Width - gap * 2d); var height = Math.Max(1d, _source.Height - gap * 2d);
            var layout = DoorWindowElevationGeometryBuilder.ParseCellLayout(_source.CustomCellLayout);
            if (layout.Count == 0) layout = ConvertExistingLayout(width, height);
            _outerFrameWidth.Value = ClampDecimal(_source.OuterFrameWidth > 0 ? _source.OuterFrameWidth : 50d, _outerFrameWidth);
            _mullionWidth.Value = ClampDecimal(_source.MullionWidth > 0 ? _source.MullionWidth : 50d, _mullionWidth);
            _outerFrameWidth.Enabled = _hasOuterFrame.Checked; _mullionWidth.Enabled = _hasMullion.Checked;
            _editor.LoadLayout(width, height, layout); _updating = false; UpdateConstructionPreview(); LoadSelectedCell();
            LoadBayFace(_leftEditor, _source.BayLeftCellLayout, (double)_bayLeftDepth.Value, height);
            LoadBayFace(_rightEditor, _source.BayRightCellLayout, (double)_bayRightDepth.Value, height);
            UpdateBayFaces();
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
            var active = ActiveEditor(); var cell = active.SelectedCell; _updating = true;
            try
            {
                if (cell == null) { _selectedWidth.Enabled = _selectedHeight.Enabled = _opening.Enabled = _material.Enabled = _isDoor.Enabled = false; return; }
                _selectedWidth.Enabled = _selectedHeight.Enabled = true; _opening.Enabled = _material.Enabled = !cell.IsDeleted; _isDoor.Enabled = ((_source.ElevationType ?? string.Empty).Contains("门")) && !cell.IsDeleted;
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
                if (_leftEditor.Enabled) DoorWindowElevationGeometryBuilder.ValidateCellLayout(_leftEditor.Cells.ToList(), (double)_bayLeftDepth.Value, Math.Max(1d, _source.Height - gap * 2d));
                if (_rightEditor.Enabled) DoorWindowElevationGeometryBuilder.ValidateCellLayout(_rightEditor.Cells.ToList(), (double)_bayRightDepth.Value, Math.Max(1d, _source.Height - gap * 2d));
                _message.Text = FaceNameInstance(ActiveEditor()) + "分格有效。当前选择 " + ActiveEditor().SelectionCount + " 个面板；Shift+单击可多选或减选。"; _message.ForeColor = Color.FromArgb(20, 112, 65);
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
            _source.HasMullion = _hasMullion.Checked; _source.MullionWidth = (double)_mullionWidth.Value; _source.DoorFrameType = Convert.ToString(_doorFrameType.SelectedItem) ?? "N型"; _source.DoorFrameWidth = _hasDoorFrame.Checked ? (double)_doorFrameWidth.Value : 0d;
            _source.BayLeftSide = Convert.ToString(_bayLeftSide.SelectedItem) ?? "墙";
            _source.BayRightSide = Convert.ToString(_bayRightSide.SelectedItem) ?? "墙";
            _source.BayLeftDepth = (double)_bayLeftDepth.Value;
            _source.BayRightDepth = (double)_bayRightDepth.Value;
            _source.BayLeftCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(_leftEditor.OrderedCells);
            _source.BayRightCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(_rightEditor.OrderedCells);
            var gap = _source.HasInstallationGap ? _source.InstallationGap : 0d; var clearWidth = _source.Width - gap * 2d; var door = _editor.Cells.FirstOrDefault(x => x.IsDoor && !x.IsDeleted);
            if (door != null) { _source.DoorPlacement = Math.Abs((door.Left + door.Right) / 2d - clearWidth / 2d) < 1d ? "居中" : door.Left < clearWidth / 2d ? "靠左" : "靠右"; _source.DoorEdgeDistance = _source.DoorPlacement == "靠右" ? Math.Max(0d, clearWidth - door.Right) : door.Left; }
            DialogResult = DialogResult.OK; Close();
        }

        private static DoorWindowScheduleItem Copy(DoorWindowScheduleItem x)
        {
            return new DoorWindowScheduleItem { Code = x.Code, Width = x.Width, Height = x.Height, HasInstallationGap = x.HasInstallationGap, InstallationGap = x.InstallationGap, HasOuterFrame = x.HasOuterFrame, OuterFrameWidth = x.OuterFrameWidth, HasMullion = x.HasMullion, MullionWidth = x.MullionWidth, DoorFrameType = x.DoorFrameType, DoorFrameWidth = x.DoorFrameWidth, ElevationType = x.ElevationType, DivisionPreset = x.DivisionPreset, OpeningMode = x.OpeningMode, CustomColumnRatios = x.CustomColumnRatios, CustomRowRatios = x.CustomRowRatios, CustomColumnWidths = x.CustomColumnWidths, CustomRowHeights = x.CustomRowHeights, CustomCellLayout = null, CellOpeningModes = x.CellOpeningModes, DoorPlacement = x.DoorPlacement, DoorEdgeDistance = x.DoorEdgeDistance, BayLeftSide = x.BayLeftSide, BayRightSide = x.BayRightSide, BayLeftDepth = x.BayLeftDepth, BayRightDepth = x.BayRightDepth, BayLeftCellLayout = x.BayLeftCellLayout, BayRightCellLayout = x.BayRightCellLayout };
        }

        private void ResizeForConstructionChange()
        {
            var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d; var width = _source.Width - gap * 2d; var height = _source.Height - gap * 2d;
            if (width <= 1d || height <= 1d) { _message.Text = "安装缝不能大于门窗洞口尺寸。"; _message.ForeColor = Color.Firebrick; return; }
            _editor.ResizeLayout(width, height);
            _leftEditor.ResizeLayout((double)_bayLeftDepth.Value, height);
            _rightEditor.ResizeLayout((double)_bayRightDepth.Value, height);
            UpdateConstructionPreview();
        }

        private void UpdateConstructionPreview()
        {
            _editor.SetInstallationGap(_hasInstallationGap.Checked, (double)_installationGap.Value);
            _editor.SetProfileWidths((double)_outerFrameWidth.Value, (double)_mullionWidth.Value);
            var doorFrameWidth = _hasDoorFrame.Checked ? (double)_doorFrameWidth.Value : 0d;
            _editor.SetConstruction(_hasOuterFrame.Checked, _hasMullion.Checked, Convert.ToString(_doorFrameType.SelectedItem), doorFrameWidth);
            foreach (var editor in new[] { _leftEditor, _rightEditor })
            {
                editor.SetInstallationGap(false, 0d); editor.SetProfileWidths((double)_outerFrameWidth.Value, (double)_mullionWidth.Value);
                editor.SetConstruction(_hasOuterFrame.Checked, _hasMullion.Checked, Convert.ToString(_doorFrameType.SelectedItem), doorFrameWidth);
            }
        }

        private DoorWindowLayoutEditorControl ActiveEditor() { return _activeEditor ?? _editor; }
        private void ApplyMouseSnapStep() { var step = (double)_mouseSnapStep.Value; _editor.SetSnapStep(step); _leftEditor.SetSnapStep(step); _rightEditor.SetSnapStep(step); }
        private void WireEditor(DoorWindowLayoutEditorControl editor)
        {
            editor.LayoutChanged += (s, e) => { _activeEditor = editor; ValidateLayout(); };
            editor.SelectedCellChanged += (s, e) => { _activeEditor = editor; LoadSelectedCell(); };
        }
        private static GroupBox FaceGroup(string title, Control editor)
        { var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(5), Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) }; editor.Font = new Font("Microsoft YaHei UI", 9F); group.Controls.Add(editor); return group; }
        private string FaceNameInstance(DoorWindowLayoutEditorControl editor) { return editor == _leftEditor ? "左转折面" : editor == _rightEditor ? "右转折面" : "正面"; }
        private void LoadBayFace(DoorWindowLayoutEditorControl editor, string value, double width, double height)
        {
            var cells = DoorWindowElevationGeometryBuilder.ParseCellLayout(value);
            if (cells.Count == 0) cells.Add(new DoorWindowLayoutCell { Left = 0d, Bottom = 0d, Right = width, Top = height, Opening = "固定", Material = "玻璃" });
            else
            {
                var oldWidth = cells.Max(cell => cell.Right);
                var oldHeight = cells.Max(cell => cell.Top);
                var scaleX = oldWidth > .01d ? width / oldWidth : 1d;
                var scaleY = oldHeight > .01d ? height / oldHeight : 1d;
                foreach (var cell in cells)
                {
                    cell.Left *= scaleX; cell.Right *= scaleX;
                    cell.Bottom *= scaleY; cell.Top *= scaleY;
                }
            }
            editor.LoadLayout(width, height, cells);
        }
        private void UpdateBayFaces()
        {
            var isBay = string.Equals(_source.ElevationType, "凸窗", StringComparison.Ordinal);
            _leftEditor.Parent.Visible = isBay;
            _rightEditor.Parent.Visible = isBay;
            _faces.ColumnStyles[0].Width = isBay ? 25F : 0F;
            _faces.ColumnStyles[1].Width = isBay ? 50F : 100F;
            _faces.ColumnStyles[2].Width = isBay ? 25F : 0F;
            _leftEditor.Enabled = isBay && Convert.ToString(_bayLeftSide.SelectedItem) == "窗";
            _rightEditor.Enabled = isBay && Convert.ToString(_bayRightSide.SelectedItem) == "窗";
            _leftEditor.BackColor = _leftEditor.Enabled ? Color.White : Color.FromArgb(235, 235, 235);
            _rightEditor.BackColor = _rightEditor.Enabled ? Color.White : Color.FromArgb(235, 235, 235);
            if (!_activeEditor.Enabled) _activeEditor = _editor;
            ValidateLayout();
        }
        private void ResizeBayFace(DoorWindowLayoutEditorControl editor, double width)
        {
            var gap = _hasInstallationGap.Checked ? (double)_installationGap.Value : 0d;
            editor.ResizeLayout(Math.Max(1d, width), Math.Max(1d, _source.Height - gap * 2d)); ValidateLayout();
        }

        private static NumericUpDown SizeBox() { return new NumericUpDown { Minimum = 1, Maximum = 100000, DecimalPlaces = 1, Increment = 10, Width = 82, Height = 28 }; }
        private static NumericUpDown SnapStepBox() { return new NumericUpDown { Minimum = 0, Maximum = 1000, DecimalPlaces = 2, Increment = 1, Width = 68, Height = 28, Value = 5 }; }
        private static NumericUpDown ProfileBox() { return new NumericUpDown { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Increment = 5, Width = 68, Height = 28, Value = 50 }; }
        private static NumericUpDown DepthBox() { return new NumericUpDown { Minimum = 50, Maximum = 5000, DecimalPlaces = 1, Increment = 50, Width = 78, Height = 28, Value = 600 }; }
        private static FlowLayoutPanel VerticalButtonRow() { return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(0, 2, 0, 2) }; }
        private static void FormatVerticalButtons(FlowLayoutPanel panel, int width) { foreach (Control control in panel.Controls) { var button = control as Button; if (button == null) continue; button.AutoSize = false; button.Width = width; button.Height = 31; button.Margin = new Padding(3, 3, 3, 4); } }
        private static TableLayoutPanel SettingsGrid(int columns, int rows)
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = columns, RowCount = rows, Margin = new Padding(0), Padding = new Padding(2) };
            for (var column = 0; column < columns; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
            for (var row = 0; row < rows; row++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return grid;
        }
        private static GroupBox Section(string title, Control content)
        {
            var section = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(7, 7, 7, 5), Margin = new Padding(0, 0, 0, 7) };
            section.Controls.Add(content);
            section.MinimumSize = new Size(0, content.GetPreferredSize(new Size(390, 0)).Height + 32);
            return section;
        }
        private static Label GridLabel(string text) { return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }; }
        private static CheckBox OptionBox(string text) { return new CheckBox { Text = text, Checked = true, AutoSize = true, Margin = new Padding(10, 7, 2, 0) }; }
        private static decimal ClampDecimal(double value, NumericUpDown box) { return Math.Max(box.Minimum, Math.Min(box.Maximum, (decimal)value)); }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 7, 3, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Padding = new Padding(8, 0, 8, 0) }; }
        private static string SnapSettingsPath { get { return UserDataPaths.SettingsFile("门窗分格编辑.ini", "door-window-editor.ini"); } }
        private static double LoadMouseSnapStep()
        {
            try { if (File.Exists(SnapSettingsPath)) { var text = File.ReadAllText(SnapSettingsPath); var index = text.IndexOf('='); if (index >= 0) text = text.Substring(index + 1); double value; if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0d && value <= 1000d) return value; } } catch { }
            return 5d;
        }
        private static void SaveMouseSnapStep(double value) { try { File.WriteAllText(SnapSettingsPath, "鼠标拖动步长毫米=" + value.ToString("0.##", CultureInfo.InvariantCulture)); } catch { } }
    }
}
