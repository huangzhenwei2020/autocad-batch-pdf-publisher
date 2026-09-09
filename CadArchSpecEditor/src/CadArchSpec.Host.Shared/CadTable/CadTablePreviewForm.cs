using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
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
            _textHeight.Value = Math.Max(_textHeight.Minimum, Math.Min(_textHeight.Maximum, LastTextHeight));
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
                Height = 82,
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
            editorTools.Controls.Add(new Label { Text = "CAD文字样式", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
            _textStyle.Width = 150;
            editorTools.Controls.Add(_textStyle);
            editorTools.Controls.Add(new Label { Text = "纸面字高(mm)", AutoSize = true, Margin = new Padding(8, 8, 0, 0) });
            editorTools.Controls.Add(_textHeight);

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
            _grid.SelectionChanged += (sender, args) => ShowCurrentColumnTitle();

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
            actions.Controls.Add(ButtonFor("重新框选", () => Finish(CadTablePreviewAction.Repick)));

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
                _summary.Text = string.Format("{0} 行 × {1} 列。可编辑文字、粘贴 Excel 区域、调整行列和合并关系。{2}",
                    rows.Count, columns.Count, warningCount == 0 ? string.Empty : "待复核提示 " + warningCount + " 项。 ");
                ShowCurrentColumnTitle();
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
                gridCell.Style.BackColor = Color.FromArgb(235, 238, 241);
                gridCell.ToolTipText = "该位置属于合并单元格";
            }
            else if (rowSpan > 1 || columnSpan > 1)
            {
                gridCell.Style.BackColor = Color.FromArgb(226, 242, 252);
                gridCell.ToolTipText = string.Format("合并区域：{0} 行 × {1} 列", rowSpan, columnSpan);
            }
            gridCell.Style.Alignment = GridAlignment((string)cell["alignment"]);
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
                    cell["displayValue"] = Convert.ToString(_grid.Rows[rowIndex].Cells[columnIndex].Value) ?? string.Empty;
                }
            }
            double scale;
            if (!double.TryParse(_scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0d)
                throw new InvalidOperationException("插入比例必须是大于 0 的数字。");
            _payload["cadInsertOptions"] = new JObject
            {
                ["scale"] = scale,
                ["textStyle"] = _textStyle.Text.Trim(),
                ["textHeightMillimeters"] = (double)_textHeight.Value
            };
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
                foreach (DataGridViewCell cell in _grid.SelectedCells) if (!cell.ReadOnly) cell.Value = string.Empty;
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
                    Cell(startRow + rowOffset, startColumn + columnOffset)["displayValue"] = values[rowOffset][columnOffset];
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
                }
                SelectedAction = action;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
}
