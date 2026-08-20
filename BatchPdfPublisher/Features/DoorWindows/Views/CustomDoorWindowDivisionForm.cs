using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
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
            Width = 1040; Height = 720; MinimumSize = new Size(820, 560); Font = new Font("Microsoft YaHei UI", 9F);
            _activeEditor = _editor; Build(); LoadLayout(); ValidateLayout();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10), BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 184)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var splitVertical = ButtonFor("竖向分隔当前框"); splitVertical.Click += (s, e) => ActiveEditor().SplitSelected(true); tools.Controls.Add(splitVertical);
            var splitHorizontal = ButtonFor("横向分隔当前框"); splitHorizontal.Click += (s, e) => ActiveEditor().SplitSelected(false); tools.Controls.Add(splitHorizontal);
            var merge = ButtonFor("合并所选框"); merge.Click += (s, e) => { if (!ActiveEditor().MergeSelected()) MessageBox.Show(this, "请用 Shift 选择当前面中能组成完整矩形的相邻框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(merge);
            var remove = ButtonFor("删除/恢复当前框"); remove.Click += (s, e) => { if (!ActiveEditor().ToggleSelectedDeleted()) MessageBox.Show(this, "当前面至少要保留一个未删除的窗格。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(remove);
            var equalWidth = ButtonFor("所选同行等宽"); equalWidth.Click += (s, e) => { if (!ActiveEditor().EqualizeSelectedWidths()) ShowEqualizeHint(); }; tools.Controls.Add(equalWidth);
            var equalHeight = ButtonFor("所选同列等高"); equalHeight.Click += (s, e) => { if (!ActiveEditor().EqualizeSelectedHeights()) ShowEqualizeHint(); }; tools.Controls.Add(equalHeight);
            var center = ButtonFor("所选居中"); center.Click += (s, e) => { if (!ActiveEditor().CenterSelected()) MessageBox.Show(this, "请选择当前面中同一行连续面板后再居中。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }; tools.Controls.Add(center);
            var reset = ButtonFor("恢复完整外框"); reset.Click += (s, e) => { if (MessageBox.Show(this, "恢复后当前面的分格会被清除，是否继续？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes) ActiveEditor().ResetToFullFrame(); }; tools.Controls.Add(reset); header.Controls.Add(tools, 0, 0);
            var properties = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            properties.Controls.Add(LabelFor("当前框宽")); properties.Controls.Add(_selectedWidth); properties.Controls.Add(LabelFor("高")); properties.Controls.Add(_selectedHeight);
            _opening.Items.AddRange(Openings.Cast<object>().ToArray()); properties.Controls.Add(LabelFor("开启")); properties.Controls.Add(_opening);
            _material.Items.AddRange(Materials.Cast<object>().ToArray()); properties.Controls.Add(LabelFor("材质")); properties.Controls.Add(_material);
            _isDoor.Enabled = (_source.ElevationType ?? string.Empty).Contains("门"); properties.Controls.Add(_isDoor);
            properties.Controls.Add(LabelFor("门套")); _doorFrameType.Items.AddRange(new object[] { "N型", "口型" }); properties.Controls.Add(_doorFrameType); properties.Controls.Add(_hasDoorFrame); properties.Controls.Add(_doorFrameWidth); properties.Controls.Add(LabelFor("mm")); header.Controls.Add(properties, 0, 1);
            var construction = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            construction.Controls.Add(_hasInstallationGap); construction.Controls.Add(_installationGap); construction.Controls.Add(LabelFor("mm"));
            construction.Controls.Add(_hasOuterFrame); construction.Controls.Add(_outerFrameWidth); construction.Controls.Add(LabelFor("mm"));
            construction.Controls.Add(_hasMullion); construction.Controls.Add(_mullionWidth); construction.Controls.Add(LabelFor("mm（开启/推拉相邻处自动按 2 倍处理）")); header.Controls.Add(construction, 0, 2);
            var bay = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            _bayLeftSide.Items.AddRange(new object[] { "墙", "窗" }); _bayRightSide.Items.AddRange(new object[] { "墙", "窗" });
            bay.Controls.Add(LabelFor("凸窗左转折")); bay.Controls.Add(_bayLeftSide); bay.Controls.Add(_bayLeftDepth); bay.Controls.Add(LabelFor("mm"));
            bay.Controls.Add(LabelFor("右转折")); bay.Controls.Add(_bayRightSide); bay.Controls.Add(_bayRightDepth); bay.Controls.Add(LabelFor("mm（可设：墙+窗、窗+窗、窗+墙）")); header.Controls.Add(bay, 0, 3);
            header.Controls.Add(new Label { Text = "单击选择；Shift+单击增加或取消选择；拖动分隔线调整。宽度从左到右、高度从上到下确定，右下角面板承接最终剩余尺寸。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
            root.Controls.Add(header, 0, 0);
            _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _faces.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _faces.Controls.Add(FaceGroup("左转折面", _leftEditor), 0, 0); _faces.Controls.Add(FaceGroup("正面", _editor), 1, 0); _faces.Controls.Add(FaceGroup("右转折面", _rightEditor), 2, 0);
            root.Controls.Add(_faces, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _message.Margin = new Padding(3, 12, 0, 0); footer.Controls.Add(_message, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var save = ButtonFor("保存门窗分格"); save.Click += (s, e) => SaveAndClose(); actions.Controls.Add(save);
            var cancel = ButtonFor("取消"); cancel.Click += (s, e) => Close(); actions.Controls.Add(cancel); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);

            WireEditor(_leftEditor); WireEditor(_editor); WireEditor(_rightEditor);
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
        private static NumericUpDown ProfileBox() { return new NumericUpDown { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Increment = 5, Width = 68, Height = 28, Value = 50 }; }
        private static NumericUpDown DepthBox() { return new NumericUpDown { Minimum = 50, Maximum = 5000, DecimalPlaces = 1, Increment = 50, Width = 78, Height = 28, Value = 600 }; }
        private static CheckBox OptionBox(string text) { return new CheckBox { Text = text, Checked = true, AutoSize = true, Margin = new Padding(10, 7, 2, 0) }; }
        private static decimal ClampDecimal(double value, NumericUpDown box) { return Math.Max(box.Minimum, Math.Min(box.Maximum, (decimal)value)); }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 7, 3, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Padding = new Padding(8, 0, 8, 0) }; }
    }
}
