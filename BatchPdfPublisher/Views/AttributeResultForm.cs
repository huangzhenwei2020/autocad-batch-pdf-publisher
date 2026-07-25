using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class AttributeResultForm : Form
    {
        private readonly List<AttributeApplyDetail> _details;
        private readonly Action<AttributeTarget> _locate;
        private readonly DataGridView _grid = new DataGridView();

        public AttributeResultForm(IEnumerable<AttributeApplyDetail> details, Action<AttributeTarget> locate)
        {
            _details = details?.ToList() ?? new List<AttributeApplyDetail>(); _locate = locate;
            Text = "批量属性写入结果"; Width = 760; Height = 480; StartPosition = FormStartPosition.CenterParent;
            Build();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); Controls.Add(root);
            _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.AutoGenerateColumns = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.BackgroundColor = System.Drawing.Color.White;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "结果", DataPropertyName = "Status", Width = 65 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "块名", DataPropertyName = "BlockName", Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性标记", DataPropertyName = "Tag", Width = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原值", DataPropertyName = "OldValue", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "新值", DataPropertyName = "NewValue", Width = 120 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", DataPropertyName = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.DataSource = Rows(_details);
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) _locate?.Invoke((_grid.Rows[e.RowIndex].DataBoundItem as ResultRow)?.Detail?.Target); };
            root.Controls.Add(_grid, 0, 0);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (s, e) => Close(); bottom.Controls.Add(close);
            var export = new Button { Text = "导出结果 CSV", AutoSize = true }; export.Click += (s, e) => Export(); bottom.Controls.Add(export);
            var failures = new Button { Text = "只看失败", AutoSize = true }; failures.Click += (s, e) => _grid.DataSource = Rows(_details.Where(x => x.Status == "失败")); bottom.Controls.Add(failures);
            var all = new Button { Text = "显示全部", AutoSize = true }; all.Click += (s, e) => _grid.DataSource = Rows(_details); bottom.Controls.Add(all); root.Controls.Add(bottom, 0, 1);
        }

        private static List<ResultRow> Rows(IEnumerable<AttributeApplyDetail> details) => details.Select(x => new ResultRow { Detail = x, Status = x.Status, BlockName = x.Target?.BlockName, Tag = x.Target?.Tag, OldValue = x.OldValue, NewValue = x.NewValue, Message = x.Message }).ToList();
        private void Export()
        {
            var lines = new List<string> { "结果,块名,属性标记,原值,新值,说明" };
            lines.AddRange(_details.Select(x => CsvExportService.Cell(x.Status) + "," + CsvExportService.Cell(x.Target?.BlockName) + "," + CsvExportService.Cell(x.Target?.Tag) + "," + CsvExportService.Cell(x.OldValue) + "," + CsvExportService.Cell(x.NewValue) + "," + CsvExportService.Cell(x.Message)));
            if (CsvExportService.Save(this, "属性修改结果_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv", lines, out var path)) CsvExportService.Reveal(path);
        }
        private sealed class ResultRow { public AttributeApplyDetail Detail { get; set; } public string Status { get; set; } public string BlockName { get; set; } public string Tag { get; set; } public string OldValue { get; set; } public string NewValue { get; set; } public string Message { get; set; } }
    }
}
