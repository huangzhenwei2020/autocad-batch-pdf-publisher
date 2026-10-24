using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    internal sealed class LineCorrectionForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly NumericUpDown _angle = new NumericUpDown { Minimum = 0, Maximum = 44.9m, DecimalPlaces = 2, Increment = .1m, Value = 1, Width = 90 };
        private readonly CheckBox _horizontal = new CheckBox { Text = "校正水平", Checked = true, AutoSize = true };
        private readonly CheckBox _vertical = new CheckBox { Text = "校正垂直", Checked = true, AutoSize = true };
        private readonly CheckBox _flatten = new CheckBox { Text = "顶点 Z 归零", Checked = true, AutoSize = true };
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button _apply = new Button { Text = "应用校正", AutoSize = true, Height = 32, Enabled = false };
        internal ObjectId[] SelectedIds { get; private set; } = new ObjectId[0];
        internal LineCorrectionOptions Options { get { return new LineCorrectionOptions { ToleranceDegrees = (double)_angle.Value,
            Horizontal = _horizontal.Checked, Vertical = _vertical.Checked, FlattenZ = _flatten.Checked }; } }

        internal LineCorrectionForm(Document document)
        {
            _document = document;
            Text = "线条校正"; Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(800, 480); MinimumSize = new Size(620, 400);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            row.Controls.Add(new Label { Text = "角度容差", AutoSize = true, Margin = new Padding(0, 5, 8, 0) });
            row.Controls.Add(_angle); row.Controls.Add(new Label { Text = "度", AutoSize = true, Margin = new Padding(4, 5, 20, 0) });
            row.Controls.Add(_horizontal); row.Controls.Add(_vertical); row.Controls.Add(_flatten); top.Controls.Add(row, 0, 0);
            top.Controls.Add(new Label { Text = "坐标系：世界坐标（WCS XY）    旋转基点：各线段中点", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            var selectRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            var select = new Button { Text = "选择对象", Width = 100, Height = 30 }; select.Click += (s, e) => SelectObjects(); selectRow.Controls.Add(select);
            top.Controls.Add(selectRow, 0, 2); root.Controls.Add(top, 0, 0);
            _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "对象", DataPropertyName = "Handle", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = "Type", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "校正段数", DataPropertyName = "Segments", Width = 70 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Z 归零顶点", DataPropertyName = "VerticesZ", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最大位移", DataPropertyName = "Movement", Width = 85, ToolTipText = "图形单位，包含 Z 向位移" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 170,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            root.Controls.Add(_grid, 0, 1); root.Controls.Add(_status, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = false };
            var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Width = 90, Height = 32 };
            _apply.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(close); buttons.Controls.Add(_apply); root.Controls.Add(buttons, 0, 3);
            Controls.Add(root); CancelButton = close; AcceptButton = _apply;
            _angle.ValueChanged += (s, e) => RefreshPreview(); _horizontal.CheckedChanged += (s, e) => RefreshPreview();
            _vertical.CheckedChanged += (s, e) => RefreshPreview(); _flatten.CheckedChanged += (s, e) => RefreshPreview();
            var implied = document.Editor.SelectImplied();
            if (implied.Status == PromptStatus.OK) SelectedIds = implied.Value.GetObjectIds();
            RefreshPreview();
        }

        private void SelectObjects()
        {
            try
            {
                using (_document.Editor.StartUserInteraction(this))
                {
                    var result = _document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n选择需要校正的直线或多段线：" });
                    if (result.Status == PromptStatus.OK) SelectedIds = result.Value.GetObjectIds();
                }
                RefreshPreview();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RefreshPreview()
        {
            var plans = LineCorrectionService.Analyze(_document.Database, SelectedIds, Options);
            _grid.DataSource = plans;
            var changed = plans.Count(p => p.Result != null && p.Result.Changed);
            _status.Text = "已选 " + plans.Count + " 个 · 待修改 " + changed + " 个 · 校正 " + plans.Sum(p => p.Segments)
                + " 段 · Z 归零 " + plans.Sum(p => p.VerticesZ) + " 个顶点";
            _apply.Enabled = changed > 0;
        }
    }
}
