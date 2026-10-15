using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class RoomFloorSettingsForm : DpiAwareForm
    {
        private sealed class FloorRow
        {
            public string FloorName { get; set; }
            public int FloorCount { get; set; }
            public int PerFloorCount { get; set; }
            public List<RoomStatisticsItem> Items { get; set; }
        }
        private readonly BindingList<FloorRow> _rows = new BindingList<FloorRow>();
        private readonly DataGridView _grid = new DataGridView();
        public List<RoomStatisticsItem> Result { get; private set; }

        public RoomFloorSettingsForm(IEnumerable<RoomStatisticsItem> items)
        {
            Text = "房间统计·楼层设置"; Width = 700; Height = 430; MinimumSize = new Size(600, 360);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9F);
            foreach (var group in items.GroupBy(x => string.IsNullOrWhiteSpace(x.FloorName) ? (x.FloorSource ?? LegacySource(x.Source)) : "floor:" + x.FloorName.Trim(), StringComparer.CurrentCultureIgnoreCase))
                _rows.Add(new FloorRow { FloorName = RoomStatistics.Floor(group.First()), FloorCount = RoomStatistics.Layers(group.First()), PerFloorCount = group.Sum(x => x.Count), Items = group.ToList() });
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.BackgroundColor = Color.White;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "楼层/标准层", DataPropertyName = "FloorName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "代表层数", DataPropertyName = "FloorCount", Width = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单层候选房间数", DataPropertyName = "PerFloorCount", Width = 140, ReadOnly = true });
            _grid.DataSource = _rows;
            _grid.CellEndEdit += (s,e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) { int count; if (FloorStatisticsRules.TryParseFloorCount(_rows[e.RowIndex].FloorName, out count)) { _rows[e.RowIndex].FloorCount = count; _grid.Refresh(); } } };
            _grid.DataError += (s,e) => { e.ThrowException = false; MessageBox.Show(this, "代表层数请输入 1～999 的整数。", Text); };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            var apply = new Button { Text = "应用楼层", AutoSize = true };
            var remove = new Button { Text = "删除选中楼层", AutoSize = true };
            apply.Click += (s,e) => Apply(); remove.Click += (s,e) => { foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(x => x.Index).ToList()) if (row.Index < _rows.Count) _rows.RemoveAt(row.Index); };
            footer.Controls.Add(cancel); footer.Controls.Add(apply); footer.Controls.Add(remove);
            Controls.Add(_grid); Controls.Add(footer); CancelButton = cancel;
        }

        private static string LegacySource(string source)
        {
            var parts = (source ?? "").Split(':');
            return parts.Length >= 3 && parts[1] == "table" ? string.Join(":", parts.Take(3)) : source;
        }

        private void Apply()
        {
            try
            {
                if (!_grid.EndEdit()) return;
                BindingContext[_rows].EndCurrentEdit();
                var result = new List<RoomStatisticsItem>();
                foreach (var row in _rows)
                {
                    int count; if (FloorStatisticsRules.TryParseFloorCount(row.FloorName, out count)) row.FloorCount = count;
                    result.AddRange(row.Items.Select(x => RoomStatistics.WithFloor(x, row.FloorName, row.FloorCount)));
                }
                int duplicates; Result = RoomStatistics.Combine(result, out duplicates, true);
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
