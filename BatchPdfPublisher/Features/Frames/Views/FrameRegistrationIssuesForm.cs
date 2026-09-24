using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    internal sealed class FrameRegistrationIssuesForm : DpiAwareForm
    {
        private readonly CadPlainListBox _issues = new IssueListBox();
        private readonly List<Image> _icons = new List<Image>();

        public FrameProjectScanIssue SelectedIssue { get; private set; }
        public bool OpenAllRequested { get; private set; }

        public FrameRegistrationIssuesForm(FrameProjectScanReport report)
        {
            Text = "工程图框检查";
            Width = 680; Height = 470;
            MinimumSize = new Size(580, 390);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(1);
            BackColor = CadDialogTheme.Border;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4,
                BackColor = CadDialogTheme.Canvas };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            Controls.Add(root);
            root.Controls.Add(TitleBar(), 0, 0);
            root.Controls.Add(new Label { Text = "发现重复属性 TAG。请选择问题文件进入块编辑器修改属性定义，修改后执行 ATTSYNC。",
                Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Text, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(18, 0, 18, 0) }, 0, 1);
            var card = CadDialogTheme.Card();
            card.Margin = new Padding(14, 0, 14, 10);
            CadDialogTheme.StyleList(_issues);
            foreach (var issue in report.Issues) _issues.Items.Add(issue);
            if (_issues.Items.Count > 0) _issues.SelectedIndex = 0;
            card.Controls.Add(new CadListHost(_issues) { Padding = new Padding(7) });
            root.Controls.Add(card, 0, 2);
            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(14, 10, 14, 10) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
            buttons.Controls.Add(Button("打开选中并修改", PublisherForm.UiIcon.Open, () => Select(false), true), 1, 0);
            buttons.Controls.Add(Button("全部打开并修改", PublisherForm.UiIcon.List, () => Select(true)), 2, 0);
            buttons.Controls.Add(Button("关闭", PublisherForm.UiIcon.Remove, Close), 3, 0);
            root.Controls.Add(buttons, 0, 3);
        }

        private void Select(bool all)
        {
            SelectedIssue = _issues.SelectedItem as FrameProjectScanIssue;
            if (SelectedIssue == null) return;
            OpenAllRequested = all;
            DialogResult = DialogResult.OK;
        }

        private CadRoundedButton Button(string text, PublisherForm.UiIcon icon, Action action, bool primary = false)
        {
            var button = CadDialogTheme.Button(text, action, primary);
            button.AutoSize = false; button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 0, 5, 0);
            button.Image = PublisherForm.DrawUiIcon(icon, primary ? Color.White : CadDialogTheme.Muted);
            _icons.Add(button.Image);
            return button;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var icon in _icons) icon.Dispose();
            base.Dispose(disposing);
        }

        private Control TitleBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3,
                BackColor = Color.FromArgb(27, 30, 40) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var mark = CadBrandIcon.CreateTitleMark();
            var caption = new Label { Text = Text, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Text, TextAlign = ContentAlignment.MiddleLeft };
            MouseEventHandler drag = (sender, args) => {
                if (args.Button != MouseButtons.Left) return;
                ReleaseCapture(); SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag; mark.MouseDown += drag; caption.MouseDown += drag;
            bar.Controls.Add(mark, 0, 0); bar.Controls.Add(caption, 1, 0);
            var close = new CadChromeButton { Text = "×", Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, BackColor = Color.FromArgb(27, 30, 40),
                IsCloseButton = true };
            close.Click += (sender, args) => Close();
            bar.Controls.Add(close, 2, 0);
            return bar;
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr word, IntPtr data);

        private sealed class IssueListBox : CadPlainListBox
        {
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
                if ((e.State & DrawItemState.Selected) != 0)
                {
                    var bounds = new Rectangle(e.Bounds.Left + 1, e.Bounds.Top + 1,
                        Math.Max(1, e.Bounds.Width - 2), Math.Max(1, e.Bounds.Height - 2));
                    var state = e.Graphics.Save();
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = CadDialogTheme.Rounded(bounds, 5))
                    using (var fill = new SolidBrush(Color.FromArgb(24, 103, 179)))
                    using (var accent = new SolidBrush(Color.FromArgb(73, 213, 255)))
                    {
                        e.Graphics.FillPath(fill, path);
                        e.Graphics.SetClip(path, CombineMode.Intersect);
                        e.Graphics.FillRectangle(accent, bounds.Left, bounds.Top, 4, bounds.Height);
                    }
                    e.Graphics.Restore(state);
                }
                TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                    new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 20), e.Bounds.Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
