using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class AutoLayerSettingsForm : Form
    {
        private readonly CheckBox _enabled = new CheckBox { Text = "更新比例时自动归层", AutoSize = true };
        private readonly ComboBox _text = Combo();
        private readonly ComboBox _attribute = Combo();
        private readonly ComboBox _dimension = Combo();

        public AutoLayerSettingsForm()
        {
            Text = "自动归层设置"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(500, 270); Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White;
            _enabled.Location = new Point(24, 22);
            var profile = DraftingStandardService.LoadProfile(); var layers = profile.Layers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _text.Items.AddRange(layers); _attribute.Items.AddRange(layers); _dimension.Items.AddRange(layers);
            AddRow("普通文字图层", _text, 65); AddRow("属性文字图层", _attribute, 108); AddRow("标注图层", _dimension, 151);
            var note = new Label { Text = "关闭自动归层后，更新比例只调整文字样式和标注样式，原图层保持不变。图层名称可从插件图层中选择，也可以手工输入。", Location = new Point(24, 190), Size = new Size(450, 38), ForeColor = Color.FromArgb(70, 80, 95) };
            var cancel = Button("取消", 386); cancel.DialogResult = DialogResult.Cancel; var save = Button("保存", 286); save.Click += Save;
            Controls.AddRange(new Control[] { _enabled, note, cancel, save }); AcceptButton = save; CancelButton = cancel;
            LoadValues();
        }
        private void AddRow(string label, Control input, int y) { Controls.Add(new Label { Text = label, Location = new Point(24, y + 5), AutoSize = true }); input.Location = new Point(145, y); input.Size = new Size(330, 28); Controls.Add(input); }
        private void LoadValues() { var x = AutoLayerSettings.Load(); _enabled.Checked = x.Enabled; _text.Text = x.TextLayer; _attribute.Text = x.AttributeLayer; _dimension.Text = x.DimensionLayer; }
        private void Save(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_text.Text) || string.IsNullOrWhiteSpace(_attribute.Text) || string.IsNullOrWhiteSpace(_dimension.Text)) { MessageBox.Show(this, "图层名称不能为空。", "自动归层"); return; }
            new AutoLayerSettings { Enabled = _enabled.Checked, TextLayer = _text.Text.Trim(), AttributeLayer = _attribute.Text.Trim(), DimensionLayer = _dimension.Text.Trim() }.Save(); DialogResult = DialogResult.OK; Close();
        }
        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDown }; }
        private static Button Button(string text, int x) { return new Button { Text = text, Location = new Point(x, 232), Size = new Size(88, 32) }; }
    }
}
