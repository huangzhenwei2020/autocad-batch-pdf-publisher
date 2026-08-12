using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public enum DrawingScaleAction { None, Selection }

    public sealed class DrawingScaleForm : Form
    {
        private readonly ListBox _scales = new ListBox();
        private readonly TextBox _current = new TextBox();
        private readonly CheckBox _autoLayer = new CheckBox { Text = "自动归层", AutoSize = true };
        public int TargetScale { get; private set; }
        public DrawingScaleAction SelectedAction { get; private set; }

        public DrawingScaleForm(Document document)
        {
            Text = "万落_制图比例修改"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(250, 595); Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White;
            _scales.Location = new Point(10, 10); _scales.Size = new Size(230, 365); _scales.IntegralHeight = false;
            foreach (var scale in new[] { 1, 2, 4, 5, 8, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100, 120, 150, 200, 300, 500, 800, 1000 }) _scales.Items.Add("1:" + scale);
            var currentLabel = new Label { Text = "当前出图比例", Location = new Point(10, 386), AutoSize = true };
            _current.Location = new Point(10, 407); _current.Size = new Size(230, 27); _current.ReadOnly = false; int tianzhengScale; _current.Text = TianzhengScaleService.TryGetCurrentScale(out tianzhengScale) ? "1:" + tianzhengScale : LoadLastScale();
            var selected = _scales.Items.IndexOf(_current.Text); if (selected >= 0) _scales.SelectedIndex = selected; else _scales.SelectedIndex = Math.Max(0, _scales.Items.IndexOf("1:100"));
            if (selected < 0) _current.Text = TianzhengScaleService.TryGetCurrentScale(out tianzhengScale) ? "1:" + tianzhengScale : LoadLastScale();
            _scales.SelectedIndexChanged += delegate { if (_scales.SelectedItem != null) _current.Text = Convert.ToString(_scales.SelectedItem); };
            _scales.DoubleClick += delegate { if (_scales.SelectedItem != null) { _current.Text = Convert.ToString(_scales.SelectedItem); UpdateScale(_scales, EventArgs.Empty); } };
            _autoLayer.Location = new Point(10, 440); _autoLayer.Checked = AutoLayerSettings.Load().Enabled;
            var hint = new Label { Text = TianzhengScaleService.IsLoaded() ? "已检测到天正：同步当前比例，并更新所选天正标注。" : "未检测到天正：按普通 CAD 对象更新比例。", Location = new Point(10, 465), Size = new Size(230, 38), ForeColor = Color.FromArgb(75, 85, 100) };
            var dimension = MakeButton("标注设置", 10, 510, 110); dimension.Click += OpenDimensionSettings;
            var standards = MakeButton("自动归层设置", 130, 510, 110); standards.Click += OpenStandards;
            var update = MakeButton("更新比例", 130, 551, 110); update.Click += UpdateScale;
            Controls.AddRange(new Control[] { _scales, currentLabel, _current, _autoLayer, hint, dimension, standards, update }); AcceptButton = update;
        }

        private void OpenStandards(object sender, EventArgs e)
        {
            using (var form = new AutoLayerSettingsForm()) if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form) == DialogResult.OK) _autoLayer.Checked = AutoLayerSettings.Load().Enabled;
        }
        private void OpenDimensionSettings(object sender, EventArgs e)
        {
            using (var form = new TianzhengDimensionSettingsForm()) Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
        }
        private void UpdateScale(object sender, EventArgs e)
        {
            int scale; if (!TryParseScale(_current.Text, out scale)) { MessageBox.Show(this, "请输入有效的目标比例，例如 1:75 或 75。", "比例管理"); _current.Focus(); _current.SelectAll(); return; }
            var layers = AutoLayerSettings.Load(); layers.Enabled = _autoLayer.Checked; layers.Save(); TargetScale = scale; SelectedAction = DrawingScaleAction.Selection; SaveLastScale("1:" + scale); DialogResult = DialogResult.OK; Close();
        }
        public static bool TryParseScale(string value, out int scale)
        {
            var text = (value ?? string.Empty).Trim().Replace("：", ":"); var colon = text.LastIndexOf(':'); if (colon >= 0) text = text.Substring(colon + 1);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out scale) && scale > 0;
        }
        private static string SettingsPath { get { return UserDataPaths.SettingsFile("drawing-scale.settings"); } }
        private static string LoadLastScale() { try { var value = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : string.Empty; int scale; return TryParseScale(value, out scale) ? "1:" + scale : "1:100"; } catch { return "1:100"; } }
        private static void SaveLastScale(string value) { try { File.WriteAllText(SettingsPath, value); } catch { } }
        private static Button MakeButton(string text, int x, int y, int width) { return new Button { Text = text, Location = new Point(x, y), Size = new Size(width, 34), FlatStyle = FlatStyle.Standard }; }
    }
}
