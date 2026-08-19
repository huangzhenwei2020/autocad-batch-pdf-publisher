using BatchPdfPublisher.Services;
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    public sealed class TianzhengDimensionSettingsForm : DpiAwareForm
    {
        private readonly TextBox _inner = new TextBox(), _spacing = new TextBox(), _axis = new TextBox();
        private readonly ComboBox _presets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox _dimension = new CheckBox(), _cadDimension = new CheckBox(), _axisEnabled = new CheckBox();
        public TianzhengDimensionSettingsForm()
        {
            Text = "标注比例设置（1:1 基准）"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(500, 335); MinimumSize = new Size(440, 320); Font = new Font("Microsoft YaHei UI", 9F); AutoScroll = true;
            var x = TianzhengDimensionSettings.Load(); _inner.Text = F(x.InnerExtensionLength); _spacing.Text = F(x.DimensionSpacing); _axis.Text = F(x.AxisLeaderLength); _dimension.Checked = x.ApplyDimensionGeometry; _cadDimension.Checked = x.ApplyCadDimensionGeometry; _axisEnabled.Checked = x.ApplyAxisLeader;
            foreach (var preset in TianzhengDimensionSettings.LoadPresets()) _presets.Items.Add(preset);
            _presets.SelectedIndexChanged += (s,e) => { var p = _presets.SelectedItem as TianzhengDimensionSettings.Preset; if (p != null) { _inner.Text=F(p.Inner); _spacing.Text=F(p.Spacing); _axis.Text=F(p.AxisLeader); } };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 8 }; root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            var presetPanel = new FlowLayoutPanel { Dock=DockStyle.Fill, AutoSize=true }; presetPanel.Controls.Add(_presets); var savePreset = new Button { Text="保存为预设", AutoSize=true }; savePreset.Click += SavePreset; var deletePreset = new Button { Text="删除预设", AutoSize=true }; deletePreset.Click += DeletePreset; presetPanel.Controls.Add(savePreset); presetPanel.Controls.Add(deletePreset);
            root.Controls.Add(new Label { Text="标注预设", AutoSize=true, Anchor=AnchorStyles.Left }, 0, 3); root.Controls.Add(presetPanel, 1, 3);
            Add(root, 0, "最内层尺寸界线长", _inner); Add(root, 1, "相邻尺寸线间距", _spacing); Add(root, 2, "轴号引线长度", _axis);
            _cadDimension.Text = "更新普通 CAD 标注"; _cadDimension.AutoSize = true; root.Controls.Add(_cadDimension, 1, 4);
            _dimension.Text = "更新天正标注几何参数"; _dimension.AutoSize = true; root.Controls.Add(_dimension, 1, 5); _axisEnabled.Text = "更新天正轴号线长度"; _axisEnabled.AutoSize = true; root.Controls.Add(_axisEnabled, 1, 6);
            var note = new Label { Text = "普通 CAD 标注按尺寸线所在行分层：最内层界线长取“最内层尺寸界线长”，每向外一层加一个“相邻尺寸线间距”。", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(265, 0) }; root.Controls.Add(note, 0, 4); root.SetRowSpan(note, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft }; var save = new Button { Text = "保存", Size = new Size(85, 30) }; save.Click += Save; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(85, 30) }; buttons.Controls.Add(save); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 7); root.SetColumnSpan(buttons, 2); Controls.Add(root); AcceptButton = save; CancelButton = cancel;
        }
        private void Save(object sender, EventArgs e) { double b,c,d; if(!P(_inner,out b)||!P(_spacing,out c)||!P(_axis,out d)){MessageBox.Show(this,"请输入有效的非负 1:1 长度。",Text);return;} new TianzhengDimensionSettings{InnerExtensionLength=b,DimensionSpacing=c,AxisLeaderLength=d,ApplyDimensionGeometry=_dimension.Checked,ApplyCadDimensionGeometry=_cadDimension.Checked,ApplyAxisLeader=_axisEnabled.Checked}.Save(); DialogResult=DialogResult.OK; Close(); }
        private void SavePreset(object sender, EventArgs e) { double a,b,c; if(!P(_inner,out a)||!P(_spacing,out b)||!P(_axis,out c)){MessageBox.Show(this,"请先输入有效数值。",Text);return;} var name=Microsoft.VisualBasic.Interaction.InputBox("请输入预设名称：","保存标注预设","常用标注").Trim(); if(string.IsNullOrWhiteSpace(name)) return; TianzhengDimensionSettings.SavePreset(new TianzhengDimensionSettings.Preset{Name=name,Inner=a,Spacing=b,AxisLeader=c}); _presets.Items.Clear(); foreach(var p in TianzhengDimensionSettings.LoadPresets()) _presets.Items.Add(p); }
        private void DeletePreset(object sender, EventArgs e) { var p=_presets.SelectedItem as TianzhengDimensionSettings.Preset; if(p==null)return; TianzhengDimensionSettings.DeletePreset(p.Name); _presets.Items.Remove(p); }
        private static void Add(TableLayoutPanel p,int row,string label,Control value){p.Controls.Add(new Label{Text=label+"（mm）",AutoSize=true,Anchor=AnchorStyles.Left},0,row);value.Dock=DockStyle.Fill;p.Controls.Add(value,1,row);}
        private static bool P(TextBox x,out double v){return double.TryParse(x.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out v)&&v>=0;}
        private static string F(double v){return v.ToString("0.###",CultureInfo.InvariantCulture);}
    }
}
