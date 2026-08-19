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
        private readonly CheckBox _dimension = new CheckBox(), _cadDimension = new CheckBox(), _axisEnabled = new CheckBox();
        public TianzhengDimensionSettingsForm()
        {
            Text = "标注比例设置（1:1 基准）"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(500, 335); MinimumSize = new Size(440, 320); Font = new Font("Microsoft YaHei UI", 9F); AutoScroll = true;
            var x = TianzhengDimensionSettings.Load(); _inner.Text = F(x.InnerExtensionLength); _spacing.Text = F(x.DimensionSpacing); _axis.Text = F(x.AxisLeaderLength); _dimension.Checked = x.ApplyDimensionGeometry; _cadDimension.Checked = x.ApplyCadDimensionGeometry; _axisEnabled.Checked = x.ApplyAxisLeader;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 7 }; root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            Add(root, 0, "最内层尺寸界线长", _inner); Add(root, 1, "相邻尺寸线间距", _spacing); Add(root, 2, "轴号引线长度", _axis);
            _cadDimension.Text = "更新普通 CAD 标注"; _cadDimension.AutoSize = true; root.Controls.Add(_cadDimension, 1, 3);
            _dimension.Text = "更新天正标注几何参数"; _dimension.AutoSize = true; root.Controls.Add(_dimension, 1, 4); _axisEnabled.Text = "更新天正轴号线长度"; _axisEnabled.AutoSize = true; root.Controls.Add(_axisEnabled, 1, 5);
            var note = new Label { Text = "普通 CAD 标注按尺寸线所在行分层：最内层界线长取“最内层尺寸界线长”，每向外一层加一个“相邻尺寸线间距”；同一行的尺寸线视为同一层。天正标注继续按原尺寸界线长度自动分层。", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(265, 0) }; root.Controls.Add(note, 0, 3); root.SetRowSpan(note, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft }; var save = new Button { Text = "保存", Size = new Size(85, 30) }; save.Click += Save; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(85, 30) }; buttons.Controls.Add(save); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2); Controls.Add(root); AcceptButton = save; CancelButton = cancel;
        }
        private void Save(object sender, EventArgs e) { double b,c,d; if(!P(_inner,out b)||!P(_spacing,out c)||!P(_axis,out d)){MessageBox.Show(this,"请输入有效的非负 1:1 长度。",Text);return;} new TianzhengDimensionSettings{InnerExtensionLength=b,DimensionSpacing=c,AxisLeaderLength=d,ApplyDimensionGeometry=_dimension.Checked,ApplyCadDimensionGeometry=_cadDimension.Checked,ApplyAxisLeader=_axisEnabled.Checked}.Save(); DialogResult=DialogResult.OK; Close(); }
        private static void Add(TableLayoutPanel p,int row,string label,Control value){p.Controls.Add(new Label{Text=label+"（mm）",AutoSize=true,Anchor=AnchorStyles.Left},0,row);value.Dock=DockStyle.Fill;p.Controls.Add(value,1,row);}
        private static bool P(TextBox x,out double v){return double.TryParse(x.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out v)&&v>=0;}
        private static string F(double v){return v.ToString("0.###",CultureInfo.InvariantCulture);}
    }
}
