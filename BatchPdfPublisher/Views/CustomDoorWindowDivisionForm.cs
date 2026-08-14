using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class CustomDoorWindowDivisionForm : Form
    {
        private sealed class CellSetting { public int Row { get; set; } public int Column { get; set; } public string Opening { get; set; } }
        private static readonly string[] Openings = { "固定", "左平开", "右平开", "推拉", "上悬", "下悬", "百叶" };
        private readonly DoorWindowScheduleItem _source;
        private readonly DoorWindowScheduleItem _working;
        private readonly NumericUpDown _columns = new NumericUpDown { Minimum = 1, Maximum = 6, Value = 1, Width = 58 };
        private readonly NumericUpDown _rows = new NumericUpDown { Minimum = 1, Maximum = 4, Value = 1, Width = 58 };
        private readonly TextBox _columnRatios = new TextBox { Width = 170 };
        private readonly TextBox _rowRatios = new TextBox { Width = 170 };
        private readonly DataGridView _grid = new DataGridView();
        private readonly BindingList<CellSetting> _cells = new BindingList<CellSetting>();
        private readonly DoorWindowElevationPreviewControl _preview = new DoorWindowElevationPreviewControl();
        private readonly Label _message = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private bool _updating;

        public CustomDoorWindowDivisionForm(DoorWindowScheduleItem source)
        {
            _source = source ?? throw new ArgumentNullException("source"); _working = Copy(source);
            Text = "自定义门窗分格 — " + (source.Code ?? "未编号"); StartPosition = FormStartPosition.CenterParent; Width = 940; Height = 650; MinimumSize = new Size(780, 520); Font = new Font("Microsoft YaHei UI", 9F);
            Build(); LoadFromSource(); RefreshPreview();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10), BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            settings.Controls.Add(LabelFor("竖向列数")); settings.Controls.Add(_columns); settings.Controls.Add(LabelFor("列宽比例")); settings.Controls.Add(_columnRatios);
            settings.Controls.Add(LabelFor("横向行数")); settings.Controls.Add(_rows); settings.Controls.Add(LabelFor("行高比例（自下而上）")); settings.Controls.Add(_rowRatios);
            settings.Controls.Add(new Label { Text = "比例可填 1,1,2；不要求合计为 100。每个格子可分别设置开启。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 8, 0, 0) });
            root.Controls.Add(settings, 0, 0);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
            ConfigureGrid(); split.Panel1.Controls.Add(_grid);
            var previewBox = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            previewBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); previewBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewBox.Controls.Add(new Label { Text = "自定义结果预览", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.FromArgb(245, 247, 250) }, 0, 0); previewBox.Controls.Add(_preview, 0, 1); split.Panel2.Controls.Add(previewBox);
            root.Controls.Add(split, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); _message.Margin = new Padding(3, 12, 0, 0); footer.Controls.Add(_message, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var save = ButtonFor("保存自定义分格"); save.Click += (s, e) => SaveAndClose(); actions.Controls.Add(save); var cancel = ButtonFor("取消"); cancel.Click += (s, e) => Close(); actions.Controls.Add(cancel); footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);
            Shown += (s, e) => { if (split.ClientSize.Width > 600) split.SplitterDistance = Math.Max(330, split.ClientSize.Width / 2); };
            _columns.ValueChanged += (s, e) => RebuildCells(); _rows.ValueChanged += (s, e) => RebuildCells(); _columnRatios.TextChanged += (s, e) => RefreshPreview(); _rowRatios.TextChanged += (s, e) => RefreshPreview();
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false; _grid.BackgroundColor = Color.White; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "行（自下而上）", DataPropertyName = "Row", Width = 105, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "列（从左到右）", DataPropertyName = "Column", Width = 105, ReadOnly = true });
            var opening = new DataGridViewComboBoxColumn { HeaderText = "该扇开启方式", DataPropertyName = "Opening", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FlatStyle = FlatStyle.Flat }; opening.Items.AddRange(Openings); _grid.Columns.Add(opening); _grid.DataSource = _cells;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => { if (e.RowIndex >= 0) RefreshPreview(); }; _grid.DataError += (s, e) => e.ThrowException = false;
        }

        private void LoadFromSource()
        {
            var columns = 1; var rows = 1; var columnRatios = "1"; var rowRatios = "1";
            if (_source.DivisionPreset == "自定义") { columns = Math.Max(1, DoorWindowElevationGeometryBuilder.ParseRatios(_source.CustomColumnRatios).Count); rows = Math.Max(1, DoorWindowElevationGeometryBuilder.ParseRatios(_source.CustomRowRatios).Count); columnRatios = _source.CustomColumnRatios; rowRatios = _source.CustomRowRatios; }
            else if (_source.DivisionPreset == "双扇等分") { columns = 2; columnRatios = "1,1"; }
            else if (_source.DivisionPreset == "三扇等分") { columns = 3; columnRatios = "1,1,1"; }
            else if (_source.DivisionPreset == "上亮") { rows = 2; rowRatios = "72,28"; }
            else if (_source.DivisionPreset == "侧亮" || _source.DivisionPreset == "门联窗") { columns = 2; columnRatios = _source.DivisionPreset == "门联窗" ? "58,42" : "68,32"; }
            else if (_source.DivisionPreset == "上亮+侧亮") { columns = 2; rows = 2; columnRatios = "68,32"; rowRatios = "72,28"; }
            _updating = true; _columns.Value = columns; _rows.Value = rows; _columnRatios.Text = columnRatios; _rowRatios.Text = rowRatios; _updating = false;
            RebuildCells(_source.CellOpeningModes);
        }

        private void RebuildCells(string serialized = null)
        {
            if (_updating) return; var previous = (serialized ?? SerializeOpenings()).Split('|'); var count = (int)_columns.Value * (int)_rows.Value;
            _updating = true; _cells.Clear();
            for (var row = 1; row <= (int)_rows.Value; row++) for (var column = 1; column <= (int)_columns.Value; column++)
            {
                var index = (row - 1) * (int)_columns.Value + column - 1; var mode = index < previous.Length && Openings.Contains(previous[index]) ? previous[index] : DefaultOpening(index, count);
                _cells.Add(new CellSetting { Row = row, Column = column, Opening = mode });
            }
            _updating = false; EnsureRatioCount(_columnRatios, (int)_columns.Value); EnsureRatioCount(_rowRatios, (int)_rows.Value); RefreshPreview();
        }

        private string DefaultOpening(int index, int count)
        {
            if (_source.OpeningMode == "双扇平开" && count >= 2) return index % 2 == 0 ? "右平开" : "左平开";
            return Openings.Contains(_source.OpeningMode) ? _source.OpeningMode : "固定";
        }

        private static void EnsureRatioCount(TextBox box, int count)
        { if (DoorWindowElevationGeometryBuilder.ParseRatios(box.Text).Count != count) box.Text = string.Join(",", Enumerable.Repeat("1", count)); }

        private void RefreshPreview()
        {
            if (_updating) return; _working.DivisionPreset = "自定义"; _working.OpeningMode = "自定义"; _working.CustomColumnRatios = _columnRatios.Text.Trim(); _working.CustomRowRatios = _rowRatios.Text.Trim(); _working.CellOpeningModes = SerializeOpenings();
            var valid = DoorWindowElevationGeometryBuilder.ParseRatios(_working.CustomColumnRatios).Count == (int)_columns.Value && DoorWindowElevationGeometryBuilder.ParseRatios(_working.CustomRowRatios).Count == (int)_rows.Value && _cells.All(x => Openings.Contains(x.Opening));
            _message.Text = valid ? "参数有效；保存后主清单和 CAD 生成都会采用此分格。" : "比例数量应与行列数一致，并且每个格子都要选择开启方式。"; _message.ForeColor = valid ? Color.FromArgb(20, 112, 65) : Color.Firebrick; _preview.ShowItem(valid ? _working : null);
        }

        private void SaveAndClose()
        {
            RefreshPreview(); if (_message.ForeColor == Color.Firebrick) { MessageBox.Show(this, _message.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _source.DivisionPreset = "自定义"; _source.OpeningMode = "自定义"; _source.CustomColumnRatios = _working.CustomColumnRatios; _source.CustomRowRatios = _working.CustomRowRatios; _source.CellOpeningModes = _working.CellOpeningModes; DialogResult = DialogResult.OK; Close();
        }

        private string SerializeOpenings() { return string.Join("|", _cells.Select(x => x.Opening ?? "固定")); }
        private static DoorWindowScheduleItem Copy(DoorWindowScheduleItem x) { return new DoorWindowScheduleItem { Code = x.Code, Width = x.Width, Height = x.Height, InstallationGap = x.InstallationGap, ElevationType = x.ElevationType, DivisionPreset = x.DivisionPreset, OpeningMode = x.OpeningMode, CustomColumnRatios = x.CustomColumnRatios, CustomRowRatios = x.CustomRowRatios, CellOpeningModes = x.CellOpeningModes }; }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(8, 7, 4, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Padding = new Padding(8, 0, 8, 0) }; }
    }
}
