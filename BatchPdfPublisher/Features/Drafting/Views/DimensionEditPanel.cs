using System;
using System.Drawing;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    internal sealed class DimensionEditPanel : DpiAwareForm
    {
        internal readonly Label Status = new Label { AutoSize = true, MaximumSize = new Size(270, 0), Padding = new Padding(4), ForeColor = Color.LightBlue };
        internal readonly CheckBox ClearLiteral = new CheckBox { Text = "本次允许清除固定文字覆写", AutoSize = true, Margin = new Padding(6, 12, 0, 4) };
        internal DimensionEditPanel(Action<string> run)
        {
            Text = "尺寸编辑 · 当前 CAD 视口"; ClientSize = new Size(300, 480); MinimumSize = new Size(310, 320);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9); BackColor = CadDialogTheme.Canvas; ForeColor = CadDialogTheme.Text;
            AutoScaleMode = AutoScaleMode.Dpi;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            Controls.Add(flow);
            flow.Controls.Add(new Label { Text = "尺寸编辑模式", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(5, 3, 0, 8) });
            flow.Controls.Add(Status);
            var items = new[] { "选择尺寸串|select", "新建连续尺寸|new", "＋ 增补界点|add", "◇ 整串尺寸线移动|line", "□ 移动界点|node", "数值改段长（可输入 0）|length", "合并：删除内部界点|merge", "删除该段（留空）|delete", "解除成串关系|release" };
            foreach (var item in items)
            {
                var pair = item.Split('|'); var action = pair[1];
                var button = new Button { Text = pair[0], Width = 258, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = CadDialogTheme.Raised, ForeColor = CadDialogTheme.Text, Margin = new Padding(4, 3, 4, 3) };
                button.Click += (s, e) => run(action); flow.Controls.Add(button);
            }
            flow.Controls.Add(ClearLiteral);
            flow.Controls.Add(new Label { Text = "双击高亮尺寸线／文字：连续增补\n点方形界点／菱形夹点后，点目标位置提交。\nEsc 取消当前预览；Ctrl+Z 撤销。\n仅编辑非注释性原生线性尺寸。", AutoSize = true, MaximumSize = new Size(262, 0), ForeColor = CadDialogTheme.Muted, Margin = new Padding(5, 10, 0, 8) });
            var close = new Button { Text = "退出编辑模式", Width = 258, Height = 32 }; close.Click += (s, e) => Close(); flow.Controls.Add(close);
        }
    }
}
