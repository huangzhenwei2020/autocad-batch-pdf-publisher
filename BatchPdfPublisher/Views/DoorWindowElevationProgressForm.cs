using System;
using System.Drawing;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowElevationProgressForm : Form
    {
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Top, Height = 22, Minimum = 0 };
        private readonly Label _label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Label _count = new Label { Dock = DockStyle.Right, Width = 84, TextAlign = ContentAlignment.MiddleRight };

        public DoorWindowElevationProgressForm(int total)
        {
            Text = "正在插入门窗立面"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false; ShowInTaskbar = false; TopMost = true; Width = 500; Height = 122; Font = new Font("Microsoft YaHei UI", 9F);
            _progress.Maximum = Math.Max(1, total);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var labels = new Panel { Dock = DockStyle.Fill }; labels.Controls.Add(_label); labels.Controls.Add(_count); root.Controls.Add(labels, 0, 0); root.Controls.Add(_progress, 0, 1); Controls.Add(root);
            Report(0, total, "等待指定插入点…");
        }

        public void Report(int completed, int total, string code)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, int, string>(Report), completed, total, code); return; }
            _progress.Maximum = Math.Max(1, total); _progress.Value = Math.Max(0, Math.Min(_progress.Maximum, completed));
            _label.Text = completed <= 0 ? code : "正在生成：" + (string.IsNullOrWhiteSpace(code) ? "未编号" : code);
            _count.Text = Math.Max(0, completed) + " / " + Math.Max(0, total); Refresh(); Application.DoEvents();
        }
    }
}
