using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal static class CadDialogTheme
    {
        public const int ControlHeight = 36;
        public const int Gap = 8;
        public static readonly Color Canvas = Color.FromArgb(14, 25, 35);
        public static readonly Color Surface = Color.FromArgb(20, 35, 47);
        public static readonly Color Raised = Color.FromArgb(26, 45, 60);
        public static readonly Color Border = Color.FromArgb(45, 69, 88);
        public static readonly Color Text = Color.FromArgb(232, 240, 247);
        public static readonly Color Muted = Color.FromArgb(145, 165, 182);
        public static readonly Color Accent = Color.FromArgb(19, 151, 255);
        public static readonly Color Danger = Color.FromArgb(255, 73, 73);

        public static CadCardPanel Card() => new CadCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14), Margin = Padding.Empty };

        public static Label Heading(string text) => new Label
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = false, Height = 36,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Text,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), Margin = Padding.Empty
        };

        public static Label FieldLabel(string text) => new Label
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted,
            Margin = new Padding(0, 0, 8, 8), AutoEllipsis = true
        };

        public static CadTextInputHost Input(TextBox input, int width = 0)
        {
            var host = new CadTextInputHost(input) { Dock = width > 0 ? DockStyle.None : DockStyle.Fill, Width = width > 0 ? width : 100 };
            return host;
        }

        public static CadRoundedButton Button(string text, Action action, bool accent = false, bool danger = false)
        {
            var button = new CadRoundedButton
            {
                Text = text, Height = ControlHeight, MinimumSize = new Size(0, ControlHeight), AutoSize = true,
                Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0, 0, 8, 8),
                BackColor = accent ? Accent : Raised, ForeColor = danger ? Danger : Text,
                BorderColor = danger ? Danger : accent ? Accent : Border, Cursor = Cursors.Hand
            };
            if (action != null) button.Click += (sender, args) => action();
            return button;
        }

        public static void StyleList(ListBox list)
        {
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Surface;
            list.ForeColor = Text;
            list.IntegralHeight = false;
            list.Font = new Font("Microsoft YaHei UI", 9F);
        }

        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(2, radius * 2);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class CadCardPanel : Panel
    {
        public CadCardPanel()
        {
            BackColor = CadDialogTheme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var pen = new Pen(CadDialogTheme.Border)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class CadRoundedButton : Button
    {
        public Color BorderColor { get; set; }
        private bool _hovered;
        private bool _pressed;

        public CadRoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? CadDialogTheme.Canvas : Parent.BackColor);
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            var fillColor = _pressed ? Color.FromArgb(16, 91, 153) : _hovered ? Color.FromArgb(42, 60, 78) : BackColor;
            using (var path = CadDialogTheme.Rounded(bounds, 6))
            using (var fill = new SolidBrush(fillColor))
            using (var pen = new Pen(BorderColor == Color.Empty ? CadDialogTheme.Border : BorderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : CadDialogTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class CadTextInputHost : Panel
    {
        private readonly TextBox _input;
        public CadTextInputHost(TextBox input)
        {
            _input = input;
            Height = CadDialogTheme.ControlHeight;
            MinimumSize = new Size(0, CadDialogTheme.ControlHeight);
            MaximumSize = new Size(0, CadDialogTheme.ControlHeight);
            BackColor = CadDialogTheme.Raised;
            Margin = new Padding(0, 0, 8, 8);
            DoubleBuffered = true;
            _input.AutoSize = false;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = CadDialogTheme.Raised;
            _input.ForeColor = CadDialogTheme.Text;
            _input.Margin = Padding.Empty;
            _input.Enter += (sender, args) => Invalidate();
            _input.Leave += (sender, args) => Invalidate();
            Controls.Add(_input);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var textHeight = Math.Max(_input.Font.Height + 4, _input.PreferredHeight);
            var top = Math.Max(2, (Height - textHeight) / 2);
            _input.SetBounds(10, top, Math.Max(1, Width - 20), Math.Min(textHeight, Height - top - 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 6))
            using (var fill = new SolidBrush(CadDialogTheme.Raised))
            using (var pen = new Pen(_input.Focused ? CadDialogTheme.Accent : CadDialogTheme.Border))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path); }
        }
    }

    internal sealed class CadListHost : Panel
    {
        public CadListHost(Control list)
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(3);
            BackColor = CadDialogTheme.Surface;
            DoubleBuffered = true;
            list.Dock = DockStyle.Fill;
            list.Margin = Padding.Empty;
            Controls.Add(list);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var pen = new Pen(CadDialogTheme.Border)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class CadToggleCheckedListBox : CheckedListBox
    {
        public CadToggleCheckedListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 30;
            CheckOnClick = true;
            IntegralHeight = false;
            BorderStyle = BorderStyle.None;
            BackColor = CadDialogTheme.Surface;
            ForeColor = CadDialogTheme.Text;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            using (var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(16, 126, 214) : BackColor))
                e.Graphics.FillRectangle(fill, e.Bounds);
            const int switchWidth = 34, switchHeight = 18;
            var x = e.Bounds.Right - switchWidth - 7;
            var y = e.Bounds.Top + (e.Bounds.Height - switchHeight) / 2;
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.Left + 7, e.Bounds.Top, Math.Max(1, x - e.Bounds.Left - 12), e.Bounds.Height),
                ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CadDialogTheme.Rounded(new Rectangle(x, y, switchWidth, switchHeight), switchHeight / 2))
            using (var track = new SolidBrush(GetItemChecked(e.Index) ? Color.FromArgb(0, 112, 255) : Color.FromArgb(78, 94, 111)))
                e.Graphics.FillPath(track, path);
            var knobX = GetItemChecked(e.Index) ? x + switchWidth - switchHeight + 2 : x + 2;
            using (var knob = new SolidBrush(Color.White)) e.Graphics.FillEllipse(knob, knobX, y + 2, switchHeight - 4, switchHeight - 4);
        }

        protected override void OnItemCheck(ItemCheckEventArgs ice)
        {
            base.OnItemCheck(ice);
            if (IsHandleCreated) BeginInvoke(new Action(Invalidate));
        }
    }

    internal sealed class CadToggleSwitch : CheckBox
    {
        public CadToggleSwitch()
        {
            AutoSize = false;
            Height = 34;
            MinimumSize = new Size(100, 34);
            ForeColor = CadDialogTheme.Text;
            BackColor = CadDialogTheme.Surface;
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, 10, 6);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            const int switchWidth = 38, switchHeight = 20;
            var x = Math.Max(2, Width - switchWidth - 2);
            var y = (Height - switchHeight) / 2;
            using (var path = CadDialogTheme.Rounded(new Rectangle(x, y, switchWidth, switchHeight), switchHeight / 2))
            using (var track = new SolidBrush(Checked ? Color.FromArgb(0, 112, 255) : Color.FromArgb(78, 94, 111)))
                e.Graphics.FillPath(track, path);
            var knobX = Checked ? x + switchWidth - switchHeight + 2 : x + 2;
            using (var knob = new SolidBrush(Color.White)) e.Graphics.FillEllipse(knob, knobX, y + 2, switchHeight - 4, switchHeight - 4);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(1, x - 8), Height),
                Enabled ? ForeColor : CadDialogTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
