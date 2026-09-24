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

        internal static GraphicsPath Rounded(Rectangle bounds, int radius, bool roundLeft, bool roundRight)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(2, radius * 2);
            var left = bounds.Left;
            var top = bounds.Top;
            var right = bounds.Right;
            var bottom = bounds.Bottom;
            if (roundLeft) path.AddArc(left, top, diameter, diameter, 180, 90);
            else path.AddLine(left, top, left, top);
            path.AddLine(left + (roundLeft ? radius : 0), top, right - (roundRight ? radius : 0), top);
            if (roundRight) path.AddArc(right - diameter, top, diameter, diameter, 270, 90);
            path.AddLine(right, top + (roundRight ? radius : 0), right, bottom - (roundRight ? radius : 0));
            if (roundRight) path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
            path.AddLine(right - (roundRight ? radius : 0), bottom, left + (roundLeft ? radius : 0), bottom);
            if (roundLeft) path.AddArc(left, bottom - diameter, diameter, diameter, 90, 90);
            path.AddLine(left, bottom - (roundLeft ? radius : 0), left, top + (roundLeft ? radius : 0));
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? CadDialogTheme.Canvas : Parent.BackColor);
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var fill = new SolidBrush(CadDialogTheme.Surface))
                e.Graphics.FillPath(fill, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var pen = new Pen(CadDialogTheme.Border)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class CadRoundedButton : Button
    {
        public Color BorderColor { get; set; }
        public bool PreserveBackColorOnInteraction { get; set; }
        public bool ConnectedLeft { get; set; }
        public bool ConnectedRight { get; set; }
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
            var bounds = new Rectangle(ConnectedLeft ? 0 : 1, 1,
                Math.Max(1, Width - (ConnectedLeft ? 0 : 1) - (ConnectedRight ? 0 : 2)), Math.Max(1, Height - 3));
            var fillColor = PreserveBackColorOnInteraction ? BackColor
                : _pressed ? Color.FromArgb(16, 91, 153) : _hovered ? Color.FromArgb(42, 60, 78) : BackColor;
            using (var path = ConnectedLeft || ConnectedRight
                ? CadDialogTheme.Rounded(bounds, 6, !ConnectedLeft, !ConnectedRight)
                : CadDialogTheme.Rounded(bounds, 6))
            using (var fill = new SolidBrush(fillColor))
            using (var pen = new Pen(BorderColor == Color.Empty ? CadDialogTheme.Border : BorderColor))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
            if (Image == null)
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : CadDialogTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            var textSize = TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, bounds.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var iconWidth = Math.Min(16, Image.Width);
            var gap = 8;
            var available = Math.Max(0, bounds.Width - 20 - iconWidth - gap);
            var groupWidth = iconWidth + gap + Math.Min(textSize.Width, available);
            var iconX = bounds.Left + Math.Max(10, (bounds.Width - groupWidth) / 2);
            e.Graphics.DrawImage(Image, new Rectangle(iconX, bounds.Top + (bounds.Height - 16) / 2, 16, 16));
            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(iconX + iconWidth + gap, bounds.Top, Math.Max(1, bounds.Right - iconX - iconWidth - gap - 8), bounds.Height),
                Enabled ? ForeColor : CadDialogTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class CadChromeButton : Button
    {
        public bool IsCloseButton { get; set; }
        private bool _hovered;
        private bool _pressed;

        public CadChromeButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var fill = _hovered ? IsCloseButton ? Color.FromArgb(180, 50, 60) : Color.FromArgb(43, 53, 67)
                : BackColor;
            if (_pressed) fill = IsCloseButton ? Color.FromArgb(145, 38, 49) : Color.FromArgb(32, 42, 55);
            e.Graphics.Clear(fill);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, CadDialogTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class CadTextInputHost : Panel
    {
        private readonly TextBox _input;
        private readonly Label _placeholder;
        private Image _leadingIcon;

        public Image LeadingIcon
        {
            get => _leadingIcon;
            set { _leadingIcon = value; PerformLayout(); Invalidate(); }
        }

        public string Placeholder
        {
            get => _placeholder.Text;
            set { _placeholder.Text = value ?? string.Empty; UpdatePlaceholder(); }
        }

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

            _placeholder = new Label
            {
                AutoSize = false,
                BackColor = CadDialogTheme.Raised,
                ForeColor = CadDialogTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.IBeam,
                Margin = Padding.Empty
            };
            _placeholder.Click += (sender, args) => _input.Focus();
            Controls.Add(_placeholder);
            _placeholder.BringToFront();
            _input.TextChanged += (sender, args) => UpdatePlaceholder();
            _input.Enter += (sender, args) => UpdatePlaceholder();
            _input.Leave += (sender, args) =>
            {
                if (IsHandleCreated) BeginInvoke(new Action(UpdatePlaceholder));
                else UpdatePlaceholder();
            };
            UpdatePlaceholder();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var textHeight = Math.Max(_input.Font.Height + 4, _input.PreferredHeight);
            var top = Math.Max(2, (Height - textHeight) / 2);
            var left = _leadingIcon == null ? 10 : 34;
            _input.SetBounds(left, top, Math.Max(1, Width - left - 10), Math.Min(textHeight, Height - top - 2));
            if (_placeholder != null)
            {
                _placeholder.Font = _input.Font;
                _placeholder.SetBounds(left, 2, Math.Max(1, Width - left - 10), Math.Max(1, Height - 4));
            }
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
            if (_leadingIcon != null)
                e.Graphics.DrawImage(_leadingIcon, new Rectangle(10, (Height - 16) / 2, 16, 16));
        }

        private void UpdatePlaceholder()
        {
            if (_placeholder == null) return;
            _placeholder.Visible = !string.IsNullOrWhiteSpace(_placeholder.Text) &&
                                   string.IsNullOrEmpty(_input.Text) && !_input.Focused;
        }
    }

    internal sealed class CadListHost : Panel
    {
        private readonly ListBox _list;
        private readonly CadVerticalScrollBar _scrollBar;

        public CadListHost(Control list)
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(3);
            BackColor = CadDialogTheme.Surface;
            DoubleBuffered = true;
            _list = list as ListBox;
            list.Dock = _list == null ? DockStyle.Fill : DockStyle.None;
            list.Margin = Padding.Empty;
            Controls.Add(list);
            if (_list != null)
            {
                _scrollBar = new CadVerticalScrollBar(_list) { Width = 20 };
                Controls.Add(_scrollBar);
                _list.Invalidated += (sender, args) => UpdateScrollLayout();
                _list.MouseWheel += (sender, args) => _scrollBar.Invalidate();
            }
            HandleCreated += (sender, args) => UpdateRoundedRegion();
            SizeChanged += (sender, args) => UpdateRoundedRegion();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            UpdateScrollLayout();
        }

        private void UpdateScrollLayout()
        {
            if (_list == null || _scrollBar == null) return;
            var innerHeight = Math.Max(1, ClientSize.Height - Padding.Vertical);
            var visible = Math.Max(1, innerHeight / Math.Max(1, _list.ItemHeight));
            var show = _list.Items.Count > visible;
            var barWidth = show ? 20 : 0;
            _list.SetBounds(Padding.Left, Padding.Top,
                Math.Max(1, ClientSize.Width - Padding.Horizontal - barWidth), innerHeight);
            _scrollBar.Visible = show;
            _scrollBar.SetBounds(ClientSize.Width - Padding.Right - barWidth, Padding.Top, barWidth, innerHeight);
            _scrollBar.Invalidate();
        }

        private void UpdateRoundedRegion()
        {
            if (Width < 2 || Height < 2) return;
            using (var path = CadDialogTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var fill = new SolidBrush(CadDialogTheme.Surface))
                e.Graphics.FillPath(fill, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = CadDialogTheme.Rounded(bounds, 7))
            using (var pen = new Pen(CadDialogTheme.Border)) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class CadScrollHost : Panel
    {
        private readonly Control _content;
        private readonly CadContentScrollBar _scrollBar;
        private int _offset;
        private int _maximum;
        private bool _layingOut;

        public CadScrollHost(Control content)
        {
            _content = content;
            BackColor = CadDialogTheme.Surface;
            DoubleBuffered = true;
            _scrollBar = new CadContentScrollBar(this) { Width = 20 };
            content.Dock = DockStyle.None;
            content.Margin = Padding.Empty;
            Controls.Add(content);
            Controls.Add(_scrollBar);
            content.Layout += (sender, args) => LayoutContent();
            AttachWheel(content);
        }

        public int Maximum => _maximum;
        public int Offset
        {
            get => _offset;
            set
            {
                var next = Math.Max(0, Math.Min(_maximum, value));
                if (_offset == next) return;
                _offset = next;
                _content.Top = -_offset;
                _scrollBar.Invalidate();
            }
        }

        public int ViewportHeight => Math.Max(1, ClientSize.Height);

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutContent();
        }

        private void LayoutContent()
        {
            if (_layingOut || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            _layingOut = true;
            try
            {
                var width = Math.Max(1, ClientSize.Width - 20);
                _content.Width = width;
                _content.PerformLayout();
                var height = Math.Max(ClientSize.Height, _content.GetPreferredSize(new Size(width, 0)).Height);
                _maximum = Math.Max(0, height - ClientSize.Height);
                if (_maximum == 0)
                {
                    width = ClientSize.Width;
                    _content.Width = width;
                    _content.PerformLayout();
                    height = Math.Max(ClientSize.Height, _content.GetPreferredSize(new Size(width, 0)).Height);
                    _maximum = Math.Max(0, height - ClientSize.Height);
                }
                _offset = Math.Min(_offset, _maximum);
                _content.SetBounds(0, -_offset, width, height);
                _scrollBar.Visible = _maximum > 0;
                _scrollBar.SetBounds(width, 0, _maximum > 0 ? 20 : 0, ClientSize.Height);
                _scrollBar.Invalidate();
            }
            finally { _layingOut = false; }
        }

        private void AttachWheel(Control control)
        {
            control.MouseWheel += (sender, args) =>
            {
                Offset += args.Delta < 0 ? 72 : -72;
                if (args is HandledMouseEventArgs handled) handled.Handled = true;
            };
            control.ControlAdded += (sender, args) => AttachWheel(args.Control);
            foreach (Control child in control.Controls) AttachWheel(child);
        }

        private sealed class CadContentScrollBar : Control
        {
            private readonly CadScrollHost _host;
            private bool _dragging;
            private bool _hovered;
            private int _dragStart;
            private int _offsetStart;

            public CadContentScrollBar(CadScrollHost host)
            {
                _host = host;
                BackColor = CadDialogTheme.Surface;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            private Rectangle ThumbBounds()
            {
                if (_host.Maximum <= 0 || Height <= 0) return Rectangle.Empty;
                var contentHeight = _host.ViewportHeight + _host.Maximum;
                var thumbHeight = Math.Min(Height, Math.Max(36,
                    (int)Math.Round(Height * _host.ViewportHeight / (double)contentHeight)));
                var travel = Math.Max(1, Height - thumbHeight);
                var top = (int)Math.Round(travel * _host.Offset / (double)_host.Maximum);
                return new Rectangle(6, top, Math.Max(1, Width - 12), thumbHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
                if (_host.Maximum <= 0) return;
                using (var track = new Pen(Color.FromArgb(40, 61, 78), 2F))
                    e.Graphics.DrawLine(track, Width / 2, 3, Width / 2, Height - 4);
                var thumb = ThumbBounds();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = CadDialogTheme.Rounded(thumb, Math.Max(2, thumb.Width / 2)))
                using (var fill = new SolidBrush(_dragging || _hovered
                    ? Color.FromArgb(133, 164, 186) : Color.FromArgb(91, 119, 140)))
                    e.Graphics.FillPath(fill, path);
            }

            protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { if (!_dragging) _hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || _host.Maximum <= 0) return;
                var thumb = ThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStart = e.Y;
                    _offsetStart = _host.Offset;
                    Capture = true;
                }
                else _host.Offset += e.Y < thumb.Top ? -_host.ViewportHeight : _host.ViewportHeight;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!_dragging || _host.Maximum <= 0) return;
                var thumb = ThumbBounds();
                _host.Offset = _offsetStart + (int)Math.Round((e.Y - _dragStart) * _host.Maximum /
                    (double)Math.Max(1, Height - thumb.Height));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _dragging = false;
                _hovered = ClientRectangle.Contains(e.Location);
                Capture = false;
                Invalidate();
                base.OnMouseUp(e);
            }
        }
    }

    internal sealed class CadToggleCheckedListBox : CheckedListBox
    {
        public Func<object, string> DetailTextProvider { get; set; }

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

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style &= ~0x00300000; // WS_HSCROLL | WS_VSCROLL
                return parameters;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Items.Count == 0 || e.Delta == 0) return;
            var visible = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
            var maximum = Math.Max(0, Items.Count - visible);
            TopIndex = Math.Max(0, Math.Min(maximum, TopIndex + (e.Delta < 0 ? 3 : -3)));
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            using (var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(16, 126, 214) : BackColor))
                e.Graphics.FillRectangle(fill, e.Bounds);
            const int switchWidth = 34, switchHeight = 18;
            var x = e.Bounds.Right - switchWidth - 7;
            var y = e.Bounds.Top + (e.Bounds.Height - switchHeight) / 2;
            var detail = DetailTextProvider == null ? string.Empty : DetailTextProvider(Items[e.Index]);
            var detailWidth = string.IsNullOrWhiteSpace(detail) ? 0 : Math.Min(74, TextRenderer.MeasureText(detail, Font).Width + 8);
            if (detailWidth > 0)
                TextRenderer.DrawText(e.Graphics, detail, Font,
                    new Rectangle(x - detailWidth - 4, e.Bounds.Top, detailWidth, e.Bounds.Height),
                    CadDialogTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.Left + 7, e.Bounds.Top, Math.Max(1, x - detailWidth - e.Bounds.Left - 16), e.Bounds.Height),
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

    internal class CadPlainListBox : ListBox
    {
        public CadPlainListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 30;
            IntegralHeight = false;
            BorderStyle = BorderStyle.None;
            BackColor = CadDialogTheme.Surface;
            ForeColor = CadDialogTheme.Text;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style &= ~0x00300000;
                return parameters;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Items.Count == 0 || e.Delta == 0) return;
            var visible = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
            var maximum = Math.Max(0, Items.Count - visible);
            TopIndex = Math.Max(0, Math.Min(maximum, TopIndex + (e.Delta < 0 ? 3 : -3)));
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Color.FromArgb(16, 126, 214) : BackColor))
                e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.Left + 7, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 14), e.Bounds.Height),
                Enabled ? ForeColor : CadDialogTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class CadVerticalScrollBar : Control
    {
        private readonly ListBox _list;
        private bool _dragging;
        private bool _hovered;
        private int _dragStart;
        private int _valueStart;

        public CadVerticalScrollBar(ListBox list)
        {
            _list = list;
            BackColor = CadDialogTheme.Surface;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private int VisibleItems => Math.Max(1, _list.ClientSize.Height / Math.Max(1, _list.ItemHeight));
        private int Maximum => Math.Max(0, _list.Items.Count - VisibleItems);
        private int Value
        {
            get => Math.Min(Maximum, Math.Max(0, _list.TopIndex));
            set { _list.TopIndex = Math.Max(0, Math.Min(Maximum, value)); _list.Invalidate(); Invalidate(); }
        }

        private Rectangle ThumbBounds()
        {
            if (Maximum <= 0 || Width <= 0 || Height <= 0) return Rectangle.Empty;
            var thumbHeight = Math.Min(Height, Math.Max(36, (int)Math.Round(Height * VisibleItems / (double)Math.Max(1, _list.Items.Count))));
            var y = (int)Math.Round(Math.Max(1, Height - thumbHeight) * Value / (double)Maximum);
            return new Rectangle(6, y, Math.Max(1, Width - 12), thumbHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
            if (Maximum <= 0) return;
            using (var track = new Pen(Color.FromArgb(40, 61, 78), 2F))
                e.Graphics.DrawLine(track, Width / 2, 3, Width / 2, Height - 4);
            var thumb = ThumbBounds();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = CadDialogTheme.Rounded(thumb, Math.Max(2, thumb.Width / 2)))
            using (var fill = new SolidBrush(_dragging || _hovered ? Color.FromArgb(133, 164, 186) : Color.FromArgb(91, 119, 140)))
                e.Graphics.FillPath(fill, path);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (!_dragging) _hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Maximum <= 0) return;
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location)) { _dragging = true; _dragStart = e.Y; _valueStart = Value; Capture = true; }
            else Value += e.Y < thumb.Top ? -Math.Max(1, VisibleItems - 1) : Math.Max(1, VisibleItems - 1);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || Maximum <= 0) return;
            var thumb = ThumbBounds();
            Value = _valueStart + (int)Math.Round((e.Y - _dragStart) * Maximum / (double)Math.Max(1, Height - thumb.Height));
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false; _hovered = ClientRectangle.Contains(e.Location); Capture = false; Invalidate(); base.OnMouseUp(e);
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
