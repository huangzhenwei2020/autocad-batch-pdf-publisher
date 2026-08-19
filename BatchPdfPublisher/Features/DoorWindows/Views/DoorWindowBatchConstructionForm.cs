using System.Drawing;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowBatchConstructionForm : DpiAwareForm
    {
        private readonly CheckBox _applyGap = ApplyBox(); private readonly CheckBox _gapEnabled = EnabledBox(); private readonly NumericUpDown _gap = Number(20);
        private readonly CheckBox _applyOuter = ApplyBox(); private readonly CheckBox _outerEnabled = EnabledBox(); private readonly NumericUpDown _outer = Number(50);
        private readonly CheckBox _applyMullion = ApplyBox(); private readonly CheckBox _mullionEnabled = EnabledBox(); private readonly NumericUpDown _mullion = Number(50);
        private readonly CheckBox _applyDoorType = ApplyBox(); private readonly ComboBox _doorType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        public bool ApplyGap { get { return _applyGap.Checked; } } public bool GapEnabled { get { return _gapEnabled.Checked; } } public double Gap { get { return (double)_gap.Value; } }
        public bool ApplyOuter { get { return _applyOuter.Checked; } } public bool OuterEnabled { get { return _outerEnabled.Checked; } } public double Outer { get { return (double)_outer.Value; } }
        public bool ApplyMullion { get { return _applyMullion.Checked; } } public bool MullionEnabled { get { return _mullionEnabled.Checked; } } public double Mullion { get { return (double)_mullion.Value; } }
        public bool ApplyDoorType { get { return _applyDoorType.Checked; } } public string DoorType { get { return (string)_doorType.SelectedItem; } }

        public DoorWindowBatchConstructionForm()
        {
            Text = "批量修改门窗构造"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(535, 245); MinimumSize = new Size(470, 245); Font = new Font("Microsoft YaHei UI", 9F);
            _doorType.Items.AddRange(new object[] { "N型", "口型" }); _doorType.SelectedIndex = 0;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 14, 16, 4), ColumnCount = 5, RowCount = 5 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.Controls.Add(new Label { Text = "应用", AutoSize = true }, 0, 0); table.Controls.Add(new Label { Text = "项目", AutoSize = true }, 1, 0); table.Controls.Add(new Label { Text = "启用", AutoSize = true }, 2, 0); table.Controls.Add(new Label { Text = "尺寸 (mm)", AutoSize = true }, 3, 0);
            AddRow(table, 1, _applyGap, "安装缝", _gapEnabled, _gap); AddRow(table, 2, _applyOuter, "外框", _outerEnabled, _outer); AddRow(table, 3, _applyMullion, "分隔框", _mullionEnabled, _mullion);
            table.Controls.Add(_applyDoorType, 0, 4); table.Controls.Add(new Label { Text = "门套形式", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 1, 4); table.Controls.Add(_doorType, 2, 4); table.SetColumnSpan(_doorType, 2);
            Controls.Add(table);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 8, 12, 8), FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "应用", DialogResult = DialogResult.OK, Width = 82, Height = 30 }; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82, Height = 30 };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
        private static void AddRow(TableLayoutPanel table, int row, CheckBox apply, string name, CheckBox enabled, Control size) { table.Controls.Add(apply, 0, row); table.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 1, row); table.Controls.Add(enabled, 2, row); table.Controls.Add(size, 3, row); }
        private static CheckBox ApplyBox() { return new CheckBox { AutoSize = true, Margin = new Padding(3, 7, 3, 3) }; }
        private static CheckBox EnabledBox() { return new CheckBox { Text = "生成", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }; }
        private static NumericUpDown Number(decimal value) { return new NumericUpDown { Minimum = 0, Maximum = 500, DecimalPlaces = 1, Value = value, Width = 100 }; }
    }
}
