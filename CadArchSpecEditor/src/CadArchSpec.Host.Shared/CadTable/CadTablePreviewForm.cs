using System;
using System.Drawing;
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

        public CadTablePreviewAction SelectedAction { get; private set; }
        public JObject Payload { get { return _payload; } }

        public CadTablePreviewForm(JObject payload)
        {
            _payload = payload == null ? new JObject() : (JObject)payload.DeepClone();
            Text = "CAD 表格识别预览";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(980, 650);
            MinimumSize = new Size(720, 480);
            Font = new Font("Microsoft YaHei UI", 9f);
            BuildLayout();
            LoadTable();
        }

        private void BuildLayout()
        {
            _summary.Dock = DockStyle.Top;
            _summary.Height = 42;
            _summary.Padding = new Padding(10, 10, 10, 6);
            _summary.ForeColor = Color.FromArgb(65, 75, 85);

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.RowHeadersWidth = 55;

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            actions.Controls.Add(ButtonFor("关闭", () => Finish(CadTablePreviewAction.Cancel)));
            actions.Controls.Add(ButtonFor("重新插入 CAD", () => Finish(CadTablePreviewAction.InsertCad)));
            actions.Controls.Add(ButtonFor("导出 Excel", () => Finish(CadTablePreviewAction.ExportExcel)));
            actions.Controls.Add(ButtonFor("重新框选", () => Finish(CadTablePreviewAction.Repick)));

            Controls.Add(_grid);
            Controls.Add(_summary);
            Controls.Add(actions);
        }

        private void LoadTable()
        {
            var table = _payload["table"] as JObject ?? new JObject();
            var columns = (table["columns"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var rows = (table["rows"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            _grid.Columns.Clear();
            _grid.Rows.Clear();

            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var source = columns[columnIndex];
                var title = (string)source["title"];
                var width = Math.Max(70, Math.Min(260, (int)Math.Round(((double?)source["widthMillimeters"] ?? 36d) * 3d)));
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "column" + columnIndex,
                    HeaderText = string.IsNullOrWhiteSpace(title) ? "列" + (columnIndex + 1) : title,
                    Width = width,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
            }

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var cells = (rows[rowIndex]["cells"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                var values = Enumerable.Range(0, columns.Count)
                    .Select(index => index < cells.Count ? (object)((string)cells[index]["displayValue"] ?? string.Empty) : string.Empty)
                    .ToArray();
                var gridRowIndex = _grid.Rows.Add(values);
                _grid.Rows[gridRowIndex].HeaderCell.Value = (rowIndex + 1).ToString();
                for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, columns.Count); columnIndex++)
                {
                    var rowSpan = (int?)cells[columnIndex]["rowSpan"] ?? 1;
                    var columnSpan = (int?)cells[columnIndex]["columnSpan"] ?? 1;
                    var gridCell = _grid.Rows[gridRowIndex].Cells[columnIndex];
                    if (rowSpan == 0 || columnSpan == 0)
                    {
                        gridCell.ReadOnly = true;
                        gridCell.Style.BackColor = Color.FromArgb(235, 238, 241);
                        gridCell.ToolTipText = "该位置属于合并单元格";
                    }
                    else if (rowSpan > 1 || columnSpan > 1)
                    {
                        gridCell.Style.BackColor = Color.FromArgb(226, 242, 252);
                        gridCell.ToolTipText = string.Format("合并区域：{0} 行 × {1} 列", rowSpan, columnSpan);
                    }
                }
            }

            var warnings = _payload["warnings"] as JArray;
            var warningCount = warnings == null ? 0 : warnings.Count;
            _summary.Text = string.Format("识别结果：{0} 行 × {1} 列。可直接修改文字；浅灰色为合并单元格覆盖区域。{2}",
                rows.Count, columns.Count, warningCount == 0 ? string.Empty : "待复核提示 " + warningCount + " 项。 ");
        }

        private void CommitEdits()
        {
            _grid.EndEdit();
            var table = _payload["table"] as JObject;
            var rows = (table == null ? null : table["rows"] as JArray) ?? new JArray();
            for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, _grid.Rows.Count); rowIndex++)
            {
                var row = rows[rowIndex] as JObject;
                var cells = row == null ? null : row["cells"] as JArray;
                if (cells == null) continue;
                for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, _grid.Columns.Count); columnIndex++)
                {
                    var cell = cells[columnIndex] as JObject;
                    if (cell == null || (int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                    cell["displayValue"] = Convert.ToString(_grid.Rows[rowIndex].Cells[columnIndex].Value) ?? string.Empty;
                }
            }
        }

        private void Finish(CadTablePreviewAction action)
        {
            if (action == CadTablePreviewAction.ExportExcel || action == CadTablePreviewAction.InsertCad) CommitEdits();
            SelectedAction = action;
            Close();
        }

        private static Button ButtonFor(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(6, 2, 0, 2) };
            button.Click += (sender, args) => action();
            return button;
        }
    }
}
