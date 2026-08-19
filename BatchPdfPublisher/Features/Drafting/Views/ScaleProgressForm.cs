using System;
using System.Drawing;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class ScaleProgressForm : DpiAwareForm
    {
        private readonly Label _stage;
        private readonly ProgressBar _progress;
        private DateTime _lastPaint = DateTime.MinValue;

        public ScaleProgressForm()
        {
            Text = "正在更新比例";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            ControlBox = false;
            TopMost = true;
            ClientSize = new Size(430, 92);

            _stage = new Label { Left = 16, Top = 14, Width = 398, Height = 24, Text = "正在准备……" };
            _progress = new ProgressBar { Left = 16, Top = 45, Width = 398, Height = 20, Minimum = 0, Maximum = 100 };
            Controls.Add(_stage);
            Controls.Add(_progress);
        }

        public void ReportRange(string stage, int current, int total, int startPercent, int endPercent, bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && current < total && (now - _lastPaint).TotalMilliseconds < 45d) return;
            _lastPaint = now;
            var safeTotal = Math.Max(1, total);
            var range = Math.Max(0, endPercent - startPercent);
            var percent = startPercent + (int)Math.Round(Math.Min(current, safeTotal) * range / (double)safeTotal);
            percent = Math.Max(0, Math.Min(100, percent));
            _stage.Text = stage + "  " + Math.Min(current, safeTotal) + " / " + safeTotal;
            _progress.Value = percent;
            Refresh();
            System.Windows.Forms.Application.DoEvents();
        }

        public void ReportStage(string stage, int percent)
        {
            _stage.Text = stage;
            _progress.Value = Math.Max(0, Math.Min(100, percent));
            Refresh();
            System.Windows.Forms.Application.DoEvents();
        }
    }
}
