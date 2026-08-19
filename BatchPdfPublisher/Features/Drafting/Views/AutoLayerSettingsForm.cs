using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class AutoLayerSettingsForm : DpiAwareForm
    {
        private readonly CheckBox _enabled = new CheckBox { Text = "更新比例时自动归层", AutoSize = true };
        private readonly CheckBox _applyTextStyles = new CheckBox { Text = "更新比例时自动切换文字样式", AutoSize = true };
        private readonly ComboBox _text = Combo();
        private readonly ComboBox _attribute = Combo();
        private readonly ComboBox _dimension = Combo();
        private readonly ComboBox _textStyle = ComboList();
        private readonly ComboBox _attributeTextStyle = ComboList();

        public AutoLayerSettingsForm()
        {
            Text = "自动归层与文字样式设置"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(540, 410); MinimumSize = new Size(480, 380); Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White; AutoScroll = true;
            _enabled.Location = new Point(24, 22);
            var profile = DraftingStandardService.LoadProfile(); var layers = profile.Layers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _text.Items.AddRange(layers); _attribute.Items.AddRange(layers); _dimension.Items.AddRange(layers);
            AddRow("普通文字图层", _text, 65); AddRow("属性文字图层", _attribute, 108); AddRow("标注图层", _dimension, 151);
            _applyTextStyles.Location = new Point(24, 205);
            var styles = profile.TextStyles.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); _textStyle.Items.AddRange(styles); _attributeTextStyle.Items.AddRange(styles);
            AddRow("普通文字样式", _textStyle, 245); AddRow("属性文字样式", _attributeTextStyle, 288);
            var note = new Label { Text = "两个开关互不影响：可以只切换文字样式而保持原图层，也可以同时自动归层。文字样式来自 BZS 制图标准。", Location = new Point(24, 334), Size = new Size(490, 38), ForeColor = Color.FromArgb(70, 80, 95) };
            var cancel = Button("取消", 426); cancel.DialogResult = DialogResult.Cancel; var save = Button("保存", 326); save.Click += Save;
            Controls.AddRange(new Control[] { _enabled, _applyTextStyles, note, cancel, save }); AcceptButton = save; CancelButton = cancel;
            LoadValues();
        }
        private void AddRow(string label, Control input, int y) { Controls.Add(new Label { Text = label, Location = new Point(24, y + 5), AutoSize = true }); input.Location = new Point(145, y); input.Size = new Size(330, 28); Controls.Add(input); }
        private void LoadValues() { var x = AutoLayerSettings.Load(); _enabled.Checked = x.Enabled; _applyTextStyles.Checked = x.ApplyTextStyles; _text.Text = x.TextLayer; _attribute.Text = x.AttributeLayer; _dimension.Text = x.DimensionLayer; _textStyle.Text = x.TextStyle; _attributeTextStyle.Text = x.AttributeTextStyle; }
        private void Save(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_text.Text) || string.IsNullOrWhiteSpace(_attribute.Text) || string.IsNullOrWhiteSpace(_dimension.Text)) { MessageBox.Show(this, "图层名称不能为空。", "自动归层"); return; }
            if (_applyTextStyles.Checked && (string.IsNullOrWhiteSpace(_textStyle.Text) || string.IsNullOrWhiteSpace(_attributeTextStyle.Text))) { MessageBox.Show(this, "请选择普通文字样式和属性文字样式。", "文字样式"); return; }
            new AutoLayerSettings { Enabled = _enabled.Checked, ApplyTextStyles = _applyTextStyles.Checked, TextLayer = _text.Text.Trim(), AttributeLayer = _attribute.Text.Trim(), DimensionLayer = _dimension.Text.Trim(), TextStyle = _textStyle.Text.Trim(), AttributeTextStyle = _attributeTextStyle.Text.Trim() }.Save(); DialogResult = DialogResult.OK; Close();
        }
        private static ComboBox Combo() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDown }; }
        private static ComboBox ComboList() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; }
        private static Button Button(string text, int x) { return new Button { Text = text, Location = new Point(x, 374), Size = new Size(88, 32) }; }
    }
}
