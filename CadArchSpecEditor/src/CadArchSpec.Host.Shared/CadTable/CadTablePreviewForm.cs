using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CadArchSpec.CadTable;
using Newtonsoft.Json.Linq;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal enum CadTablePreviewAction
    {
        Cancel,
        Repick,
        ExportExcel,
        InsertCad
    }

    internal sealed class CadTablePreviewForm : Form
    {
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _summary = new Label();
        private readonly JObject _payload;
        private readonly ComboBox _scale = Combo(new[] { "1", "10", "20", "25", "50", "100" });
        private readonly ComboBox _textStyle = Combo(null);
        private readonly ComboBox _insertType = Combo(new[] { "AutoCAD 原生表格", "天正表格（T20）" });
        private readonly CheckBox _originalCadSize = new CheckBox
        {
            Text = "保持原 CAD 尺寸",
            AutoSize = true,
            Margin = new Padding(8, 7, 3, 0)
        };
        private readonly CheckBox _noFill = new CheckBox
        {
            Text = "无底色",
            AutoSize = true,
            Checked = true,
            Margin = new Padding(3, 7, 3, 0)
        };
        private readonly Button _borderColor = new Button { Text = "边框颜色", AutoSize = true, Height = 30 };
        private readonly Button _fillColor = new Button { Text = "单元格底色", AutoSize = true, Height = 30 };
        private readonly Button _textColor = new Button { Text = "文字颜色", AutoSize = true, Height = 30 };
        private readonly TextBox _columnTitle = new TextBox { Width = 100, Margin = new Padding(3, 4, 3, 2) };
        private readonly NumericUpDown _textHeight = new NumericUpDown
        {
            DecimalPlaces = 1,
            Minimum = .5m,
            Maximum = 100,
            Increment = .5m,
            Value = 3.5m,
            Width = 64
        };
        private bool _loading;
        private static string LastScale = "1";
        private static string LastTextStyle = string.Empty;
        private static decimal LastTextHeight = 3.5m;
        private static bool LastOriginalCadSize;
        private static int? LastBorderColor;
        private static int? LastFillColor;
        private static int? LastTextColor;
        private static string LastInsertType = "AutoCAD 原生表格";
        private int? _borderColorRgb;
        private int? _fillColorRgb;
        private int? _textColorRgb;
        private bool _redirectingMergeSelection;
        private FormulaBuilderForm _formulaBuilder;

        public CadTablePreviewAction SelectedAction { get; private set; }
        public JObject Payload { get { return _payload; } }

        public CadTablePreviewForm(JObject payload, IEnumerable<string> textStyles, string currentTextStyle)
        {
            _payload = payload == null ? new JObject() : (JObject)payload.DeepClone();
            Text = "CAD 表格编辑与预览";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(800, 520);
            Font = new Font("Microsoft YaHei UI", 9f);
            foreach (var style in (textStyles ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)))
                _textStyle.Items.Add(style);
            _textStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            var preferredStyle = !string.IsNullOrWhiteSpace(LastTextStyle) && _textStyle.Items.Contains(LastTextStyle)
                ? LastTextStyle : currentTextStyle;
            _textStyle.Text = string.IsNullOrWhiteSpace(preferredStyle) ? "Standard" : preferredStyle;
            _scale.Text = LastScale;
            _insertType.Text = LastInsertType;
            _textHeight.Value = Math.Max(_textHeight.Minimum, Math.Min(_textHeight.Maximum, LastTextHeight));
            _originalCadSize.Checked = LastOriginalCadSize && (bool?)_payload["hasOriginalCadSize"] == true;
            _borderColorRgb = LastBorderColor;
            _fillColorRgb = LastFillColor;
            _textColorRgb = LastTextColor;
            _noFill.Checked = !_fillColorRgb.HasValue;
            BuildLayout();
            NormalizePayload();
            LoadTable();
        }

        private void BuildLayout()
        {
            _summary.Dock = DockStyle.Top;
            _summary.Height = 38;
            _summary.Padding = new Padding(10, 9, 10, 5);
            _summary.ForeColor = Color.FromArgb(65, 75, 85);

            var editorTools = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 116,
                Padding = new Padding(8, 6, 8, 4),
                WrapContents = true,
                AutoScroll = true
            };
            editorTools.Controls.Add(ButtonFor("添加行", AddRow));
            editorTools.Controls.Add(ButtonFor("删除行", DeleteRows));
            editorTools.Controls.Add(ButtonFor("添加列", AddColumn));
            editorTools.Controls.Add(ButtonFor("删除列", DeleteColumns));
            editorTools.Controls.Add(Separator());
            editorTools.Controls.Add(ButtonFor("合并单元格", MergeSelection));
            editorTools.Controls.Add(ButtonFor("取消合并", UnmergeSelection));
            editorTools.Controls.Add(ButtonFor("关联单元格", LinkSelection));
            editorTools.Controls.Add(ButtonFor("取消关联", UnlinkSelection));
            editorTools.Controls.Add(ButtonFor("fx 公式", ShowFormulaBuilder));
            editorTools.Controls.Add(Separator());
            editorTools.Controls.Add(ButtonFor("左对齐", () => SetAlignment("left")));
            editorTools.Controls.Add(ButtonFor("居中", () => SetAlignment("center")));
            editorTools.Controls.Add(ButtonFor("右对齐", () => SetAlignment("right")));
            editorTools.Controls.Add(Separator());
            editorTools.Controls.Add(new Label { Text = "当前列标题", AutoSize = true, Margin = new Padding(6, 8, 0, 0) });
            editorTools.Controls.Add(_columnTitle);
            editorTools.Controls.Add(ButtonFor("修改列名", UpdateColumnTitle));
            editorTools.Controls.Add(Separator());
            editorTools.Controls.Add(new Label { Text = "插入比例 1:", AutoSize = true, Margin = new Padding(6, 8, 0, 0) });
            _scale.Width = 68;
            editorTools.Controls.Add(_scale);
            editorTools.Controls.Add(_originalCadSize);
            editorTools.Controls.Add(new Label { Text = "插入类型", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
            _insertType.Width = 145;
            _insertType.DropDownStyle = ComboBoxStyle.DropDownList;
            editorTools.Controls.Add(_insertType);
            editorTools.Controls.Add(new Label { Text = "CAD文字样式", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
            _textStyle.Width = 150;
            editorTools.Controls.Add(_textStyle);
            editorTools.Controls.Add(new Label { Text = "纸面字高(mm)", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
            editorTools.Controls.Add(_textHeight);
            editorTools.Controls.Add(Separator());
            editorTools.Controls.Add(_borderColor);
            editorTools.Controls.Add(_fillColor);
            editorTools.Controls.Add(_noFill);
            editorTools.Controls.Add(_textColor);
            editorTools.Controls.Add(ButtonFor("恢复默认颜色", ResetColors));
            _borderColor.Click += (sender, args) => PickColor(ref _borderColorRgb, _borderColor, false);
            _fillColor.Click += (sender, args) => PickColor(ref _fillColorRgb, _fillColor, true);
            _textColor.Click += (sender, args) => PickColor(ref _textColorRgb, _textColor, false);
            _noFill.CheckedChanged += (sender, args) =>
            {
                if (_noFill.Checked) _fillColorRgb = null;
                else if (!_fillColorRgb.HasValue) _fillColorRgb = Color.White.ToArgb() & 0xFFFFFF;
                UpdateColorButtons();
                ApplyGridColors();
            };
            _originalCadSize.CheckedChanged += (sender, args) => UpdateSizeControls();
            UpdateColorButtons();
            UpdateSizeControls();

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.AllowUserToResizeColumns = true;
            _grid.AllowUserToResizeRows = true;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(51, 122, 183);
            _grid.RowHeadersWidth = 55;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = true;
            _grid.KeyDown += GridKeyDown;
            _grid.CellBeginEdit += GridCellBeginEdit;
            _grid.CellEndEdit += GridCellEndEdit;
            _grid.SelectionChanged += (sender, args) =>
            {
                ShowCurrentColumnTitle();
                if (_formulaBuilder != null && !_formulaBuilder.IsDisposed)
                    _formulaBuilder.UpdateSelection(SelectedRangeAddress());
                _grid.Invalidate();
            };
            _grid.CellPainting += GridCellPainting;
            _grid.Paint += GridPaint;
            _grid.CellMouseDown += GridCellMouseDown;
            _grid.CellDoubleClick += GridCellDoubleClick;
            _grid.EditingControlShowing += GridEditingControlShowing;
            _grid.Scroll += (sender, args) => _grid.Invalidate();
            _grid.ColumnWidthChanged += (sender, args) => _grid.Invalidate();
            _grid.RowHeightChanged += (sender, args) => _grid.Invalidate();

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            actions.Controls.Add(ButtonFor("关闭", () => Finish(CadTablePreviewAction.Cancel)));
            actions.Controls.Add(ButtonFor("按设置插入 CAD", () => Finish(CadTablePreviewAction.InsertCad)));
            actions.Controls.Add(ButtonFor("导出 Excel", () => Finish(CadTablePreviewAction.ExportExcel)));
            actions.Controls.Add(ButtonFor("拾取现有表格修改", () => Finish(CadTablePreviewAction.Repick)));

            Controls.Add(_grid);
            Controls.Add(editorTools);
            Controls.Add(_summary);
            Controls.Add(actions);
        }

        private void NormalizePayload()
        {
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            if (columns.Count == 0) columns.Add(CreateColumn(0));
            if (rows.Count == 0) rows.Add(CreateRow(columns));
            foreach (var row in rows.OfType<JObject>())
            {
                var cells = EnsureArray(row, "cells");
                while (cells.Count < columns.Count) cells.Add(CreateCell(cells.Count));
                while (cells.Count > columns.Count) cells.RemoveAt(cells.Count - 1);
            }
        }

        private void LoadTable()
        {
            RecalculateFormulas();
            _loading = true;
            try
            {
                var table = EnsureTable();
                var columns = EnsureArray(table, "columns").OfType<JObject>().ToList();
                var rows = EnsureArray(table, "rows").OfType<JObject>().ToList();
                _grid.Columns.Clear();
                _grid.Rows.Clear();
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var source = columns[columnIndex];
                    var title = (string)source["title"];
                    var width = Math.Max(55, Math.Min(360, (int)Math.Round(((double?)source["widthMillimeters"] ?? 36d) * 3d)));
                    _grid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        Name = "column" + columnIndex,
                        HeaderText = string.IsNullOrWhiteSpace(title) ? ColumnName(columnIndex) : title,
                        Width = width,
                        SortMode = DataGridViewColumnSortMode.NotSortable
                    });
                }
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var cells = EnsureArray(rows[rowIndex], "cells").OfType<JObject>().ToList();
                    var values = Enumerable.Range(0, columns.Count)
                        .Select(index => index < cells.Count ? (object)((string)cells[index]["displayValue"] ?? string.Empty) : string.Empty)
                        .ToArray();
                    var gridRowIndex = _grid.Rows.Add(values);
                    _grid.Rows[gridRowIndex].HeaderCell.Value = (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
                    _grid.Rows[gridRowIndex].Height = Math.Max(22, Math.Min(240,
                        (int)Math.Round(((double?)rows[rowIndex]["heightMillimeters"] ?? 8d) * 3d)));
                    for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, columns.Count); columnIndex++)
                        ApplyCellAppearance(gridRowIndex, columnIndex, cells[columnIndex]);
                }
                var warnings = _payload["warnings"] as JArray;
                var warningCount = warnings == null ? 0 : warnings.Count;
                _summary.Text = string.Format("{0} 行 × {1} 列。公式示例：=A1、=SUM(B2:B8)；关联组内任意一格修改都会同步。{2}",
                    rows.Count, columns.Count, warningCount == 0 ? string.Empty : "待复核提示 " + warningCount + " 项。 ");
                ShowCurrentColumnTitle();
                ApplyGridColors();
            }
            finally { _loading = false; }
        }

        private void ApplyCellAppearance(int rowIndex, int columnIndex, JObject cell)
        {
            var rowSpan = (int?)cell["rowSpan"] ?? 1;
            var columnSpan = (int?)cell["columnSpan"] ?? 1;
            var gridCell = _grid.Rows[rowIndex].Cells[columnIndex];
            gridCell.ReadOnly = rowSpan == 0 || columnSpan == 0;
            gridCell.Style.BackColor = Color.White;
            gridCell.ToolTipText = string.Empty;
            if (gridCell.ReadOnly)
            {
                gridCell.ToolTipText = "该位置属于合并单元格，点击可编辑主单元格";
            }
            else if (rowSpan > 1 || columnSpan > 1)
            {
                gridCell.ToolTipText = string.Format("合并区域：{0} 行 × {1} 列", rowSpan, columnSpan);
            }
            if (!string.IsNullOrWhiteSpace((string)cell["linkGroupId"]))
                gridCell.ToolTipText = (gridCell.ToolTipText + " 关联单元格：修改组内任意一格会同步").Trim();
            gridCell.Style.Alignment = GridAlignment((string)cell["alignment"]);
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge)) return;

            // The grid still owns hit testing and editing. Its merged cells are
            // painted as one region below, so suppress every internal border.
            using (var brush = new SolidBrush(_grid.BackgroundColor))
                e.Graphics.FillRectangle(brush, e.CellBounds);
            e.Handled = true;
        }

        private void GridPaint(object sender, PaintEventArgs e)
        {
            foreach (var merge in MergeRanges().ToList())
            {
                var rectangle = MergeDisplayRectangle(merge);
                if (rectangle.Width <= 0 || rectangle.Height <= 0 || !rectangle.IntersectsWith(_grid.ClientRectangle)) continue;
                var selected = MergeIsSelected(merge);
                var anchorStyle = _grid.Rows[merge[0]].Cells[merge[2]].InheritedStyle;
                var background = selected ? anchorStyle.SelectionBackColor : anchorStyle.BackColor;
                var foreground = selected ? anchorStyle.SelectionForeColor : anchorStyle.ForeColor;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, rectangle);
                using (var pen = new Pen(_grid.GridColor)) e.Graphics.DrawRectangle(pen,
                    rectangle.Left, rectangle.Top, Math.Max(0, rectangle.Width - 1), Math.Max(0, rectangle.Height - 1));

                var anchor = Cell(merge[0], merge[2]);
                var text = Convert.ToString(_grid.Rows[merge[0]].Cells[merge[2]].Value) ?? string.Empty;
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                var alignment = (string)anchor["alignment"];
                if (string.Equals(alignment, "left", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Left;
                else if (string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Right;
                else flags |= TextFormatFlags.HorizontalCenter;
                var textBounds = Rectangle.Inflate(rectangle, -5, -2);
                TextRenderer.DrawText(e.Graphics, text, _grid.Font, textBounds, foreground, flags);
            }
        }

        private void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _redirectingMergeSelection) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge) ||
                e.RowIndex == merge[0] && e.ColumnIndex == merge[2]) return;
            _redirectingMergeSelection = true;
            BeginInvoke(new Action(() =>
            {
                try { SelectCell(merge[0], merge[2]); }
                finally { _redirectingMergeSelection = false; }
            }));
        }

        private void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge)) return;
            BeginInvoke(new Action(() =>
            {
                SelectCell(merge[0], merge[2]);
                _grid.BeginEdit(true);
            }));
        }

        private void GridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (_grid.CurrentCell == null) return;
            int[] merge;
            if (!TryFindMerge(_grid.CurrentCell.RowIndex, _grid.CurrentCell.ColumnIndex, out merge)) return;
            BeginInvoke(new Action(() =>
            {
                var rectangle = Rectangle.Inflate(MergeDisplayRectangle(merge), -2, -2);
                if (rectangle.Width > 0 && rectangle.Height > 0) e.Control.Bounds = rectangle;
            }));
        }

        private Rectangle MergeDisplayRectangle(int[] merge)
        {
            var first = _grid.GetCellDisplayRectangle(merge[2], merge[0], true);
            var last = _grid.GetCellDisplayRectangle(merge[3], merge[1], true);
            return Rectangle.FromLTRB(first.Left, first.Top, last.Right, last.Bottom);
        }

        private bool MergeIsSelected(int[] merge)
        {
            for (var row = merge[0]; row <= merge[1]; row++)
                for (var column = merge[2]; column <= merge[3]; column++)
                    if (_grid.Rows[row].Cells[column].Selected) return true;
            return false;
        }

        private bool TryFindMerge(int row, int column, out int[] result)
        {
            result = MergeRanges().FirstOrDefault(range =>
                row >= range[0] && row <= range[1] && column >= range[2] && column <= range[3]);
            return result != null;
        }

        private void CommitEdits()
        {
            if (_loading) return;
            _grid.EndEdit();
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            for (var columnIndex = 0; columnIndex < Math.Min(columns.Count, _grid.Columns.Count); columnIndex++)
            {
                var column = columns[columnIndex] as JObject;
                if (column == null) continue;
                column["title"] = _grid.Columns[columnIndex].HeaderText;
                column["widthMillimeters"] = Math.Max(8d, _grid.Columns[columnIndex].Width / 3d);
            }
            for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, _grid.Rows.Count); rowIndex++)
            {
                var row = rows[rowIndex] as JObject;
                var cells = row == null ? null : row["cells"] as JArray;
                if (cells == null) continue;
                row["heightMillimeters"] = Math.Max(4d, _grid.Rows[rowIndex].Height / 3d);
                for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, _grid.Columns.Count); columnIndex++)
                {
                    var cell = cells[columnIndex] as JObject;
                    if (cell == null || (int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                    var gridValue = Convert.ToString(_grid.Rows[rowIndex].Cells[columnIndex].Value) ?? string.Empty;
                    if (gridValue.TrimStart().StartsWith("=", StringComparison.Ordinal))
                        cell["formula"] = gridValue.Trim();
                    else if (string.IsNullOrWhiteSpace((string)cell["formula"]) ||
                        !string.Equals(gridValue, (string)cell["displayValue"] ?? string.Empty, StringComparison.Ordinal))
                    {
                        cell["formula"] = string.Empty;
                        cell["displayValue"] = gridValue;
                    }
                }
            }
            double scale;
            if (!double.TryParse(_scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0d)
                throw new InvalidOperationException("插入比例必须是大于 0 的数字。");
            _payload["cadInsertOptions"] = new JObject
            {
                ["scale"] = scale,
                ["useOriginalCadSize"] = _originalCadSize.Checked,
                ["insertType"] = _insertType.SelectedIndex == 1 ? "tianzheng" : "autocad",
                ["textStyle"] = _textStyle.Text.Trim(),
                ["textHeightMillimeters"] = (double)_textHeight.Value,
                ["borderColorRgb"] = _borderColorRgb.HasValue ? (JToken)_borderColorRgb.Value : JValue.CreateNull(),
                ["fillColorRgb"] = _fillColorRgb.HasValue ? (JToken)_fillColorRgb.Value : JValue.CreateNull(),
                ["textColorRgb"] = _textColorRgb.HasValue ? (JToken)_textColorRgb.Value : JValue.CreateNull()
            };
            RecalculateFormulas();
            _payload["rowCount"] = rows.Count;
            _payload["columnCount"] = columns.Count;
        }

        private void AddRow()
        {
            CommitEdits();
            var table = EnsureTable();
            UnmergeAll();
            var rows = EnsureArray(table, "rows");
            var index = _grid.CurrentCell == null ? rows.Count : _grid.CurrentCell.RowIndex + 1;
            rows.Insert(index, CreateRow(EnsureArray(table, "columns")));
            LoadTable();
            SelectCell(index, 0);
        }

        private void DeleteRows()
        {
            var indices = _grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => cell.RowIndex).Distinct().OrderByDescending(value => value).ToList();
            if (indices.Count == 0 && _grid.CurrentCell != null) indices.Add(_grid.CurrentCell.RowIndex);
            if (indices.Count == 0) return;
            CommitEdits();
            var rows = EnsureArray(EnsureTable(), "rows");
            if (rows.Count <= indices.Count) { MessageBox.Show(this, "表格至少保留一行。", Text); return; }
            UnmergeAll();
            foreach (var index in indices.Where(index => index >= 0 && index < rows.Count)) rows.RemoveAt(index);
            LoadTable();
        }

        private void AddColumn()
        {
            CommitEdits();
            var table = EnsureTable();
            UnmergeAll();
            var columns = EnsureArray(table, "columns");
            var index = _grid.CurrentCell == null ? columns.Count : _grid.CurrentCell.ColumnIndex + 1;
            columns.Insert(index, CreateColumn(index));
            foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) EnsureArray(row, "cells").Insert(index, CreateCell(index));
            ReindexColumns();
            LoadTable();
            SelectCell(0, index);
        }

        private void DeleteColumns()
        {
            var indices = _grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => cell.ColumnIndex).Distinct().OrderByDescending(value => value).ToList();
            if (indices.Count == 0 && _grid.CurrentCell != null) indices.Add(_grid.CurrentCell.ColumnIndex);
            if (indices.Count == 0) return;
            CommitEdits();
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            if (columns.Count <= indices.Count) { MessageBox.Show(this, "表格至少保留一列。", Text); return; }
            UnmergeAll();
            foreach (var index in indices.Where(index => index >= 0 && index < columns.Count))
            {
                columns.RemoveAt(index);
                foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) EnsureArray(row, "cells").RemoveAt(index);
            }
            ReindexColumns();
            LoadTable();
        }

        private void MergeSelection()
        {
            if (_grid.SelectedCells.Count < 2) return;
            CommitEdits();
            var selected = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
            var top = selected.Min(cell => cell.RowIndex);
            var bottom = selected.Max(cell => cell.RowIndex);
            var left = selected.Min(cell => cell.ColumnIndex);
            var right = selected.Max(cell => cell.ColumnIndex);
            if (selected.Count != (bottom - top + 1) * (right - left + 1))
            {
                MessageBox.Show(this, "请选择一个连续的矩形区域。", Text);
                return;
            }
            UnmergeIntersecting(top, bottom, left, right);
            var anchor = Cell(top, left);
            anchor["rowSpan"] = bottom - top + 1;
            anchor["columnSpan"] = right - left + 1;
            for (var row = top; row <= bottom; row++)
                for (var column = left; column <= right; column++)
                    if (row != top || column != left)
                    {
                        Cell(row, column)["rowSpan"] = 0;
                        Cell(row, column)["columnSpan"] = 0;
                    }
            LoadTable();
            SelectCell(top, left);
        }

        private void LinkSelection()
        {
            if (_grid.CurrentCell == null || _grid.SelectedCells.Count < 2)
            {
                MessageBox.Show(this, "请先选中两个或更多单元格；建立关联时采用当前单元格的内容。", Text);
                return;
            }
            CommitEdits();
            var sourceRow = _grid.CurrentCell.RowIndex;
            var sourceColumn = _grid.CurrentCell.ColumnIndex;
            var source = Cell(sourceRow, sourceColumn);
            var groupId = (string)source["linkGroupId"];
            if (string.IsNullOrWhiteSpace(groupId)) groupId = "link-" + Guid.NewGuid().ToString("N");
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["linkGroupId"] = groupId;
                cell["formula"] = (string)source["formula"] ?? string.Empty;
                cell["displayValue"] = (string)source["displayValue"] ?? string.Empty;
            }
            LoadTable();
            SelectCell(sourceRow, sourceColumn);
        }

        private void UnlinkSelection()
        {
            if (_grid.SelectedCells.Count == 0) return;
            CommitEdits();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
                Cell(selected.RowIndex, selected.ColumnIndex)["linkGroupId"] = string.Empty;
            LoadTable();
        }

        private void ShowFormulaBuilder()
        {
            if (_grid.CurrentCell == null)
            {
                MessageBox.Show(this, "请先选择要写入公式的目标单元格。", Text);
                return;
            }
            if (_formulaBuilder != null && !_formulaBuilder.IsDisposed)
            {
                _formulaBuilder.Activate();
                return;
            }
            CommitEdits();
            var targetRow = _grid.CurrentCell.RowIndex;
            var targetColumn = _grid.CurrentCell.ColumnIndex;
            var target = Cell(targetRow, targetColumn);
            var existing = (string)target["formula"] ?? string.Empty;
            _formulaBuilder = new FormulaBuilderForm(targetRow, targetColumn, existing,
                () => SelectedRangeAddress(), formula => ApplyFormula(targetRow, targetColumn, formula));
            _formulaBuilder.FormClosed += (sender, args) => _formulaBuilder = null;
            _formulaBuilder.Show(this);
            _formulaBuilder.UpdateSelection(SelectedRangeAddress());
        }

        private void ApplyFormula(int row, int column, string formula)
        {
            if (row < 0 || column < 0 || row >= _grid.Rows.Count || column >= _grid.Columns.Count) return;
            var cell = Cell(row, column);
            var value = (formula ?? string.Empty).Trim();
            if (!value.StartsWith("=", StringComparison.Ordinal)) value = "=" + value;
            cell["formula"] = value;
            PropagateLinkedCell(cell);
            RecalculateFormulas();
            RefreshCalculatedValues();
            SelectCell(row, column);
        }

        private string SelectedRangeAddress()
        {
            if (_grid.SelectedCells.Count == 0) return string.Empty;
            var selected = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
            var top = selected.Min(cell => cell.RowIndex);
            var bottom = selected.Max(cell => cell.RowIndex);
            var left = selected.Min(cell => cell.ColumnIndex);
            var right = selected.Max(cell => cell.ColumnIndex);
            var first = SpreadsheetFormulaEngine.CellAddress(top, left);
            var last = SpreadsheetFormulaEngine.CellAddress(bottom, right);
            return first == last ? first : first + ":" + last;
        }

        private void UnmergeSelection()
        {
            if (_grid.CurrentCell == null) return;
            CommitEdits();
            var row = _grid.CurrentCell.RowIndex;
            var column = _grid.CurrentCell.ColumnIndex;
            foreach (var merge in MergeRanges())
            {
                if (row >= merge[0] && row <= merge[1] && column >= merge[2] && column <= merge[3])
                {
                    Unmerge(merge);
                    LoadTable();
                    SelectCell(merge[0], merge[2]);
                    return;
                }
            }
        }

        private void SetAlignment(string alignment)
        {
            CommitEdits();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["alignment"] = alignment;
                selected.Style.Alignment = GridAlignment(alignment);
            }
        }

        private void ShowCurrentColumnTitle()
        {
            if (_grid.CurrentCell == null) return;
            _columnTitle.Text = _grid.Columns[_grid.CurrentCell.ColumnIndex].HeaderText;
        }

        private void UpdateColumnTitle()
        {
            if (_grid.CurrentCell == null || string.IsNullOrWhiteSpace(_columnTitle.Text)) return;
            _grid.Columns[_grid.CurrentCell.ColumnIndex].HeaderText = _columnTitle.Text.Trim();
            CommitEdits();
        }

        private void GridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                PasteClipboard();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                foreach (DataGridViewCell cell in _grid.SelectedCells)
                    if (!cell.ReadOnly)
                    {
                        cell.Value = string.Empty;
                        var target = Cell(cell.RowIndex, cell.ColumnIndex);
                        target["formula"] = string.Empty;
                        target["displayValue"] = string.Empty;
                        PropagateLinkedCell(target);
                    }
                RecalculateFormulas();
                RefreshCalculatedValues();
                e.Handled = true;
            }
        }

        private void PasteClipboard()
        {
            if (_grid.CurrentCell == null || !Clipboard.ContainsText()) return;
            CommitEdits();
            var lines = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var values = lines.Select(line => line.Split('\t')).ToArray();
            var startRow = _grid.CurrentCell.RowIndex;
            var startColumn = _grid.CurrentCell.ColumnIndex;
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            var neededColumns = startColumn + values.Max(row => row.Length);
            while (columns.Count < neededColumns)
            {
                columns.Add(CreateColumn(columns.Count));
                foreach (var row in rows.OfType<JObject>()) EnsureArray(row, "cells").Add(CreateCell(columns.Count - 1));
            }
            while (rows.Count < startRow + values.Length) rows.Add(CreateRow(columns));
            UnmergeIntersecting(startRow, startRow + values.Length - 1, startColumn, neededColumns - 1);
            for (var rowOffset = 0; rowOffset < values.Length; rowOffset++)
                for (var columnOffset = 0; columnOffset < values[rowOffset].Length; columnOffset++)
                {
                    var target = Cell(startRow + rowOffset, startColumn + columnOffset);
                    var value = values[rowOffset][columnOffset] ?? string.Empty;
                    target["formula"] = value.TrimStart().StartsWith("=", StringComparison.Ordinal) ? value.Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace((string)target["formula"])) target["displayValue"] = value;
                    PropagateLinkedCell(target);
                }
            LoadTable();
            SelectCell(startRow, startColumn);
        }

        private void UnmergeIntersecting(int top, int bottom, int left, int right)
        {
            foreach (var merge in MergeRanges().Where(range => range[0] <= bottom && range[1] >= top && range[2] <= right && range[3] >= left).ToList()) Unmerge(merge);
        }

        private void UnmergeAll()
        {
            foreach (var merge in MergeRanges().ToList()) Unmerge(merge);
        }

        private IEnumerable<int[]> MergeRanges()
        {
            var table = EnsureTable();
            var rows = EnsureArray(table, "rows");
            var columns = EnsureArray(table, "columns");
            for (var row = 0; row < rows.Count; row++)
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = Cell(row, column);
                    var rowSpan = (int?)cell["rowSpan"] ?? 1;
                    var columnSpan = (int?)cell["columnSpan"] ?? 1;
                    if (rowSpan > 1 || columnSpan > 1)
                        yield return new[] { row, Math.Min(rows.Count - 1, row + rowSpan - 1), column, Math.Min(columns.Count - 1, column + columnSpan - 1) };
                }
        }

        private void Unmerge(int[] range)
        {
            for (var row = range[0]; row <= range[1]; row++)
                for (var column = range[2]; column <= range[3]; column++)
                {
                    Cell(row, column)["rowSpan"] = 1;
                    Cell(row, column)["columnSpan"] = 1;
                }
        }

        private void ReindexColumns()
        {
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            for (var index = 0; index < columns.Count; index++)
            {
                var key = "column" + (index + 1);
                ((JObject)columns[index])["key"] = key;
                foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) ((JObject)EnsureArray(row, "cells")[index])["columnKey"] = key;
            }
        }

        private JObject EnsureTable()
        {
            var table = _payload["table"] as JObject;
            if (table != null) return table;
            table = new JObject();
            _payload["table"] = table;
            return table;
        }

        private static JArray EnsureArray(JObject owner, string name)
        {
            var array = owner[name] as JArray;
            if (array != null) return array;
            array = new JArray();
            owner[name] = array;
            return array;
        }

        private JObject Cell(int row, int column)
        {
            return (JObject)((JArray)((JObject)EnsureArray(EnsureTable(), "rows")[row])["cells"])[column];
        }

        private static JObject CreateColumn(int index)
        {
            return new JObject
            {
                ["key"] = "column" + (index + 1),
                ["title"] = ColumnName(index),
                ["unit"] = string.Empty,
                ["widthMillimeters"] = 36d,
                ["decimalPlaces"] = 0,
                ["required"] = false
            };
        }

        private static JObject CreateRow(JArray columns)
        {
            var cells = new JArray();
            for (var index = 0; index < columns.Count; index++) cells.Add(CreateCell(index));
            return new JObject
            {
                ["rowId"] = "row-" + Guid.NewGuid().ToString("N"),
                ["rowType"] = "Data",
                ["keepTogether"] = true,
                ["cells"] = cells
            };
        }

        private static JObject CreateCell(int columnIndex)
        {
            return new JObject
            {
                ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"),
                ["columnKey"] = "column" + (columnIndex + 1),
                ["displayValue"] = string.Empty,
                ["numericValue"] = JValue.CreateNull(),
                ["unit"] = string.Empty,
                ["fieldPath"] = string.Empty,
                ["formula"] = string.Empty,
                ["linkGroupId"] = string.Empty,
                ["state"] = "unknown",
                ["source"] = "手工编辑",
                ["sourceHandles"] = new JArray(),
                ["rowSpan"] = 1,
                ["columnSpan"] = 1,
                ["alignment"] = "center"
            };
        }

        private static string ColumnName(int index)
        {
            var value = index + 1;
            var name = string.Empty;
            while (value > 0) { value--; name = (char)('A' + value % 26) + name; value /= 26; }
            return name;
        }

        private void SelectCell(int row, int column)
        {
            if (row < 0 || column < 0 || row >= _grid.Rows.Count || column >= _grid.Columns.Count) return;
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[row].Cells[column];
            _grid.CurrentCell.Selected = true;
        }

        private void Finish(CadTablePreviewAction action)
        {
            try
            {
                if (action == CadTablePreviewAction.ExportExcel || action == CadTablePreviewAction.InsertCad) CommitEdits();
                if (action == CadTablePreviewAction.ExportExcel || action == CadTablePreviewAction.InsertCad)
                {
                    LastScale = _scale.Text;
                    LastTextStyle = _textStyle.Text.Trim();
                    LastTextHeight = _textHeight.Value;
                    LastOriginalCadSize = _originalCadSize.Checked;
                    LastBorderColor = _borderColorRgb;
                    LastFillColor = _fillColorRgb;
                    LastTextColor = _textColorRgb;
                    LastInsertType = _insertType.Text;
                }
                SelectedAction = action;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void GridCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (_loading) return;
            var formula = (string)Cell(e.RowIndex, e.ColumnIndex)["formula"];
            if (!string.IsNullOrWhiteSpace(formula)) _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = formula;
        }

        private void GridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var cell = Cell(e.RowIndex, e.ColumnIndex);
            var value = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
            if (value.TrimStart().StartsWith("=", StringComparison.Ordinal)) cell["formula"] = value.Trim();
            else { cell["formula"] = string.Empty; cell["displayValue"] = value; }
            PropagateLinkedCell(cell);
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) { RecalculateFormulas(); RefreshCalculatedValues(); }
            }));
        }

        private void RecalculateFormulas()
        {
            var table = EnsureTable();
            var rows = EnsureArray(table, "rows").OfType<JObject>().ToList();
            var columns = EnsureArray(table, "columns");
            if (rows.Count == 0 || columns.Count == 0) return;
            var contents = new string[rows.Count, columns.Count];
            for (var row = 0; row < rows.Count; row++)
            {
                var cells = EnsureArray(rows[row], "cells");
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = cells[column] as JObject;
                    var formula = (string)cell?["formula"];
                    contents[row, column] = string.IsNullOrWhiteSpace(formula)
                        ? (string)cell?["displayValue"] ?? string.Empty : formula;
                }
            }
            var calculated = SpreadsheetFormulaEngine.Calculate(contents);
            for (var row = 0; row < rows.Count; row++)
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = Cell(row, column);
                    if (!string.IsNullOrWhiteSpace((string)cell["formula"])) cell["displayValue"] = calculated[row, column];
                    double numeric;
                    cell["numericValue"] = double.TryParse(calculated[row, column], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out numeric) ? (JToken)numeric : JValue.CreateNull();
                }
        }

        private void PropagateLinkedCell(JObject source)
        {
            var groupId = (string)source["linkGroupId"];
            if (string.IsNullOrWhiteSpace(groupId)) return;
            foreach (var row in EnsureArray(EnsureTable(), "rows").OfType<JObject>())
                foreach (var target in EnsureArray(row, "cells").OfType<JObject>())
                {
                    if (ReferenceEquals(target, source) || !string.Equals((string)target["linkGroupId"], groupId,
                        StringComparison.Ordinal)) continue;
                    target["formula"] = (string)source["formula"] ?? string.Empty;
                    target["displayValue"] = (string)source["displayValue"] ?? string.Empty;
                }
        }

        private void RefreshCalculatedValues()
        {
            _loading = true;
            try
            {
                for (var row = 0; row < _grid.Rows.Count; row++)
                    for (var column = 0; column < _grid.Columns.Count; column++)
                        _grid.Rows[row].Cells[column].Value = (string)Cell(row, column)["displayValue"] ?? string.Empty;
                _grid.Invalidate();
            }
            finally { _loading = false; }
        }

        private void PickColor(ref int? target, Button button, bool allowNone)
        {
            using (var dialog = new ColorDialog { FullOpen = true })
            {
                if (target.HasValue) dialog.Color = Color.FromArgb(unchecked((int)0xFF000000) | target.Value);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                target = dialog.Color.ToArgb() & 0xFFFFFF;
                if (allowNone) _noFill.Checked = false;
            }
            UpdateColorButtons();
            ApplyGridColors();
        }

        private void UpdateColorButtons()
        {
            SetColorButton(_borderColor, _borderColorRgb, "边框颜色");
            SetColorButton(_fillColor, _fillColorRgb, "单元格底色");
            SetColorButton(_textColor, _textColorRgb, "文字颜色");
        }

        private void ResetColors()
        {
            _borderColorRgb = null;
            _fillColorRgb = null;
            _textColorRgb = null;
            _noFill.Checked = true;
            UpdateColorButtons();
            ApplyGridColors();
        }

        private static void SetColorButton(Button button, int? rgb, string text)
        {
            button.Text = text;
            button.BackColor = rgb.HasValue ? Color.FromArgb(unchecked((int)0xFF000000) | rgb.Value) : SystemColors.Control;
            button.ForeColor = rgb.HasValue && Color.FromArgb(rgb.Value).GetBrightness() < .45f ? Color.White : Color.Black;
            button.UseVisualStyleBackColor = !rgb.HasValue;
        }

        private void ApplyGridColors()
        {
            var fill = _fillColorRgb.HasValue ? Color.FromArgb(unchecked((int)0xFF000000) | _fillColorRgb.Value) : Color.White;
            var text = _textColorRgb.HasValue ? Color.FromArgb(unchecked((int)0xFF000000) | _textColorRgb.Value) : Color.Black;
            var border = _borderColorRgb.HasValue ? Color.FromArgb(unchecked((int)0xFF000000) | _borderColorRgb.Value) : SystemColors.ControlDark;
            _grid.BackgroundColor = fill;
            _grid.DefaultCellStyle.BackColor = fill;
            _grid.DefaultCellStyle.ForeColor = text;
            _grid.GridColor = border;
            foreach (DataGridViewRow row in _grid.Rows)
                foreach (DataGridViewCell cell in row.Cells) { cell.Style.BackColor = fill; cell.Style.ForeColor = text; }
            _grid.Invalidate();
        }

        private void UpdateSizeControls()
        {
            var available = (bool?)_payload["hasOriginalCadSize"] == true;
            _originalCadSize.Enabled = available;
            if (!available) _originalCadSize.Checked = false;
            _scale.Enabled = !_originalCadSize.Checked;
            _textHeight.Enabled = !_originalCadSize.Checked;
            _originalCadSize.Text = available ? "保持原 CAD 尺寸" : "原 CAD 尺寸不可用";
        }

        private static DataGridViewContentAlignment GridAlignment(string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)) return DataGridViewContentAlignment.MiddleLeft;
            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)) return DataGridViewContentAlignment.MiddleRight;
            return DataGridViewContentAlignment.MiddleCenter;
        }

        private static ComboBox Combo(IEnumerable<string> values)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Height = 28, Margin = new Padding(3, 3, 3, 2) };
            if (values != null) combo.Items.AddRange(values.Cast<object>().ToArray());
            return combo;
        }

        private static Control Separator()
        {
            return new Label { Text = "|", AutoSize = true, ForeColor = Color.LightGray, Margin = new Padding(5, 8, 5, 0) };
        }

        private static Button ButtonFor(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 30, Margin = new Padding(3, 2, 0, 2) };
            button.Click += (sender, args) => action();
            return button;
        }
    }

    internal sealed class FormulaBuilderForm : Form
    {
        private readonly ComboBox _function = new ComboBox();
        private readonly TextBox _formula = new TextBox();
        private readonly Label _selection = new Label();
        private readonly Func<string> _selectedRange;
        private readonly Action<string> _apply;

        public FormulaBuilderForm(int targetRow, int targetColumn, string existingFormula,
            Func<string> selectedRange, Action<string> apply)
        {
            _selectedRange = selectedRange;
            _apply = apply;
            Text = "fx 插入公式";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            ClientSize = new Size(470, 220);
            Font = new Font("Microsoft YaHei UI", 9f);

            var target = SpreadsheetFormulaEngine.CellAddress(targetRow, targetColumn);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(LabelFor("目标单元格"), 0, 0);
            layout.Controls.Add(new Label { Text = target, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 1, 0);
            layout.Controls.Add(LabelFor("函数"), 0, 1);
            _function.Dock = DockStyle.Fill;
            _function.DropDownStyle = ComboBoxStyle.DropDownList;
            _function.Items.AddRange(new object[]
            {
                "直接引用", "SUM 求和", "AVERAGE 平均值", "MIN 最小值", "MAX 最大值",
                "COUNT 数量", "ROUND 四舍五入", "IF 条件"
            });
            _function.SelectedIndex = 1;
            layout.Controls.Add(_function, 1, 1);

            layout.Controls.Add(LabelFor("当前选区"), 0, 2);
            var rangePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            _selection.AutoSize = false;
            _selection.Width = 215;
            _selection.Height = 30;
            _selection.TextAlign = ContentAlignment.MiddleLeft;
            rangePanel.Controls.Add(_selection);
            rangePanel.Controls.Add(Button("使用选区", UseSelection));
            layout.Controls.Add(rangePanel, 1, 2);

            layout.Controls.Add(LabelFor("公式"), 0, 3);
            _formula.Dock = DockStyle.Fill;
            _formula.Text = string.IsNullOrWhiteSpace(existingFormula) ? "=" : existingFormula;
            layout.Controls.Add(_formula, 1, 3);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 5, 0, 0)
            };
            actions.Controls.Add(Button("应用", Apply));
            actions.Controls.Add(Button("关闭", Close));
            layout.Controls.Add(actions, 0, 4);
            layout.SetColumnSpan(actions, 2);
            Controls.Add(layout);
        }

        public void UpdateSelection(string range)
        {
            _selection.Text = string.IsNullOrWhiteSpace(range) ? "未选择" : range;
        }

        private void UseSelection()
        {
            var range = _selectedRange() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(range)) return;
            var first = range.Split(':')[0];
            switch (_function.SelectedIndex)
            {
                case 0: _formula.Text = "=" + first; break;
                case 1: _formula.Text = "=SUM(" + range + ")"; break;
                case 2: _formula.Text = "=AVERAGE(" + range + ")"; break;
                case 3: _formula.Text = "=MIN(" + range + ")"; break;
                case 4: _formula.Text = "=MAX(" + range + ")"; break;
                case 5: _formula.Text = "=COUNT(" + range + ")"; break;
                case 6: _formula.Text = "=ROUND(" + first + ",2)"; break;
                case 7: _formula.Text = "=IF(" + first + ">0,1,0)"; break;
            }
            _formula.Focus();
            _formula.SelectionStart = _formula.TextLength;
        }

        private void Apply()
        {
            if (string.IsNullOrWhiteSpace(_formula.Text) || _formula.Text.Trim() == "=")
            {
                MessageBox.Show(this, "请先选择单元格范围并生成公式。", Text);
                return;
            }
            _apply(_formula.Text.Trim());
        }

        private static Label LabelFor(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 29, Margin = new Padding(3, 1, 3, 1) };
            button.Click += (sender, args) => action();
            return button;
        }
    }
}
