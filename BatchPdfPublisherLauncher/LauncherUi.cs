using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisherLauncher
{
    internal class AdaptiveForm : Form
    {
        private readonly Timer _resizeTimer = new Timer { Interval = 120 };
        private bool _interactiveResize;
        private bool _splitCaption;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        internal static void InitializeDpiAwareness()
        {
            // CAD COM discovery can create hidden windows before the first WinForms
            // control. Establish DPI awareness before that first native window exists.
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); }
            catch (EntryPointNotFoundException) { /* .NET 4.8 app.config supplies the older-OS fallback. */ }
        }
        internal void UseSplitCaption()
        {
            _splitCaption = true;
            // There is no native frame to repaint on activation. Explicit non-client
            // hit testing below supplies the eight resize directions for this window.
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(LogicalToDeviceUnits(6));
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_splitCaption) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (!_splitCaption) return;
            foreach (Control child in Controls)
                if (child is LauncherShell shell)
                    using (var brush = new SolidBrush(LauncherTheme.Sidebar)) e.Graphics.FillRectangle(brush, 0, 0, child.Left + shell.SidebarWidth, ClientSize.Height);
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        protected override void WndProc(ref Message m)
        {
            if (_splitCaption && m.Msg == 0x83) { m.Result = IntPtr.Zero; return; }
            // Activation must update native state without repainting a standard frame
            // over the custom caption (including return from an owned dialog).
            if (_splitCaption && m.Msg == 0x86) { m.Result = DefWindowProc(Handle, m.Msg, m.WParam, (IntPtr)(-1)); return; }
            if (_splitCaption && m.Msg == 0x85) { m.Result = IntPtr.Zero; return; }
            if (_splitCaption && m.Msg == 0x84)
            {
                long packed = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                int edge = LogicalToDeviceUnits(6);
                bool left = p.X < edge, right = p.X >= ClientSize.Width - edge, top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                int hit = WindowState != FormWindowState.Normal ? 1 : top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
                m.Result = (IntPtr)hit;
                return;
            }
            base.WndProc(ref m);
            if (!_splitCaption) return;
            if (m.Msg == 0x24)
            {
                var screen = Screen.FromHandle(Handle);
                var info = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(MinMaxInfo));
                info.MaxPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left, screen.WorkingArea.Top - screen.Bounds.Top);
                info.MaxSize = new Point(screen.WorkingArea.Width, screen.WorkingArea.Height);
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, m.LParam, false);
            }
        }
        internal AdaptiveForm()
        {
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = LauncherUi.Canvas;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            LauncherTheme.Changed += ThemeChanged;
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                if (!_interactiveResize || IsDisposed) return;
                ResumeLayout(true);
                SuspendLayout();
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Debounce the expensive nested AutoSize tables while the border is moving.
            // A short pause refreshes the content; releasing the border always flushes immediately.
            if (_interactiveResize) { _resizeTimer.Stop(); _resizeTimer.Start(); }
        }

        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            if (_interactiveResize) return;
            _interactiveResize = true;
            SuspendLayout();
            _resizeTimer.Start();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            if (_interactiveResize)
            {
                _resizeTimer.Stop();
                _interactiveResize = false;
                ResumeLayout(true);
            }
            base.OnResizeEnd(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LauncherTheme.Apply(this);
            FitWorkingArea();
        }

        private void ThemeChanged(object sender, EventArgs e) { LauncherTheme.Apply(this); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LauncherTheme.Changed -= ThemeChanged;
                _resizeTimer.Dispose();
                if (_interactiveResize) { _interactiveResize = false; ResumeLayout(false); }
            }
            base.Dispose(disposing);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            if (IsHandleCreated) BeginInvoke(new Action(FitWorkingArea));
        }

        internal void FitWorkingArea()
        {
            if (IsDisposed) return;
            var work = Screen.FromControl(this).WorkingArea;
            var gap = Math.Min(16, Math.Min(work.Width, work.Height) / 20);
            var available = new Size(Math.Max(1, work.Width - gap * 2), Math.Max(1, work.Height - gap * 2));
            MinimumSize = new Size(Math.Min(LogicalToDeviceUnits(400), available.Width), Math.Min(LogicalToDeviceUnits(320), available.Height));
            if (WindowState != FormWindowState.Normal) return;
            var size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            Bounds = new Rectangle(
                Math.Max(work.Left + gap, Math.Min(Left, work.Right - gap - size.Width)),
                Math.Max(work.Top + gap, Math.Min(Top, work.Bottom - gap - size.Height)), size.Width, size.Height);
        }
    }

    internal static class LauncherUi
    {
        internal static readonly Color Canvas = Color.FromArgb(243, 245, 249);
        internal static readonly Color Ink = Color.FromArgb(28, 39, 57);
        internal static readonly Color Muted = Color.FromArgb(104, 116, 134);
        internal static readonly Color Accent = Color.FromArgb(46, 103, 232);
        internal static readonly Color Line = Color.FromArgb(222, 228, 237);

        internal static TableLayoutPanel Stack(int padding = 0)
        {
            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 0, Padding = new Padding(padding), Margin = Padding.Empty, BackColor = Color.Transparent };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return stack;
        }

        internal static void Add(TableLayoutPanel stack, Control control)
        {
            var row = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(control, 0, row);
        }

        internal static Label Text(string text, float size = 9.5F, bool bold = false, Color? color = null)
        {
            return new WrapLabel { Text = text, Tag = color.HasValue ? "muted" : null, AutoSize = true, Dock = DockStyle.Top, ForeColor = color ?? Ink,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 8) };
        }

        internal static Button Button(string text, bool primary = false)
        {
            var button = new RoundedButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 38), Padding = new Padding(14, 7, 14, 7),
                BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 4, 8, 4),
                Font = new Font("Microsoft YaHei UI", 9.5F, primary ? FontStyle.Bold : FontStyle.Regular), UseVisualStyleBackColor = false };
            button.Tag = primary ? "primary" : "button";
            button.FlatAppearance.BorderColor = primary ? Accent : Line;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(35, 84, 203) : Canvas;
            return button;
        }

        internal static FlowLayoutPanel Actions(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
            flow.Controls.AddRange(controls);
            return flow;
        }

        internal static Control RoundedHost(Control content)
        {
            var host = new Surface { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Padding = new Padding(8), Margin = Padding.Empty };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Dock = DockStyle.Fill; content.Margin = Padding.Empty; host.Controls.Add(content, 0, 0);
            content.SizeChanged += (s, e) => RoundWindow(content, 6);
            content.HandleCreated += (s, e) => RoundWindow(content, 6);
            return host;
        }
        internal static void RoundWindow(Control control, int radius)
        {
            if (control.Width < 2 || control.Height < 2) return;
            using (var path = RoundedButton.Rounded(new Rectangle(0, 0, control.Width, control.Height), Math.Min(control.LogicalToDeviceUnits(radius), Math.Min(control.Width, control.Height) / 2)))
            { var previous = control.Region; control.Region = new Region(path); if (previous != null) previous.Dispose(); }
        }
        internal static Panel Scroll(Control content)
        {
            var panel = new BufferedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = Canvas };
            panel.Controls.Add(content);
            return panel;
        }

        internal static TableLayoutPanel Card(string title, string description)
        {
            var card = new Surface { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 0, Padding = new Padding(22) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.BackColor = Color.White;
            card.Margin = new Padding(0, 0, 0, 16);
            Add(card, Text(title, 13, true));
            if (!string.IsNullOrWhiteSpace(description)) Add(card, Text(description, 9.5F, false, Muted));
            return card;
        }

        internal static void InlineField(TableLayoutPanel stack, string caption, Control input)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize)); row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = Text(caption); label.Anchor = AnchorStyles.Left; label.Dock = DockStyle.None; label.Margin = new Padding(0, 5, 8, 0);
            input.Dock = DockStyle.Top; input.Margin = new Padding(0, 3, 0, 3);
            row.Controls.Add(label, 0, 0); row.Controls.Add(input, 1, 0); bool? compact = null;
            row.SizeChanged += (s, e) => {
                bool next = row.Width < Math.Max(row.LogicalToDeviceUnits(320), row.Font.Height * 22);
                if (compact == next) return; compact = next; row.SuspendLayout();
                row.SetColumn(input, next ? 0 : 1); row.SetRow(input, next ? 1 : 0); row.SetColumnSpan(input, next ? 2 : 1);
                row.ColumnStyles[0].Width = next ? 0 : row.LogicalToDeviceUnits(110); row.SetColumnSpan(label, next ? 2 : 1); row.ResumeLayout();
            };
            Add(stack, row);
        }
        internal static void Field(TableLayoutPanel stack, string caption, Control input)
        {
            var label = Text(caption, 9.5F, true);
            label.Margin = new Padding(0, 8, 0, 6);
            Add(stack, label);
            input.Dock = DockStyle.Top;
            input.Margin = new Padding(0, 0, 0, 8);
            if (input is TextBoxBase textBox)
            {
                textBox.BorderStyle = BorderStyle.None;
                textBox.Margin = Padding.Empty;
                var frame = new Surface { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 1,
                    Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 0, 0, 8) };
                frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                frame.Controls.Add(input, 0, 0); Add(stack, frame);
            }
            else Add(stack, input);
        }
    }

    internal sealed class Surface : TableLayoutPanel
    {
        internal Surface() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var backdrop = Parent;
            while (backdrop != null && backdrop.BackColor.A < 255) backdrop = backdrop.Parent;
            e.Graphics.Clear(backdrop == null ? LauncherUi.Canvas : backdrop.BackColor);
            if (Width < 2 || Height < 2) return;
            var diameter = Math.Min(LogicalToDeviceUnits(20), Math.Min(Width - 1, Height - 1));
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(BackColor))
            using (var pen = new Pen(LauncherTheme.Border))
            {
                path.AddArc(0, 0, diameter, diameter, 180, 90);
                path.AddArc(Width - 1 - diameter, 0, diameter, diameter, 270, 90);
                path.AddArc(Width - 1 - diameter, Height - 1 - diameter, diameter, diameter, 0, 90);
                path.AddArc(0, Height - 1 - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal class WrapLabel : Label
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var width = proposedSize.Width;
            if (width <= 1 || width > 10000) width = Parent == null ? 360 : Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal;
            return base.GetPreferredSize(new Size(Math.Max(40, width), 0));
        }
    }

    internal class WrapCheckBox : CheckBox
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var width = proposedSize.Width;
            if (width <= 1 || width > 10000) width = Parent == null ? 360 : Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal;
            width = Math.Max(40, width);
            var checkWidth = Math.Max(20, Font.Height + 6);
            var measured = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(40, width - checkWidth), 0), TextFormatFlags.WordBreak);
            return new Size(Math.Min(width, measured.Width + checkWidth), Math.Max(Font.Height + 8, measured.Height + 8));
        }
    }
}

namespace BatchPdfPublisherLauncher
{
    internal static class LauncherTheme
    {
        private static bool _dark = Read();
        internal static bool Dark { get { return _dark; } }
        internal static event EventHandler Changed;
        internal static Color Background { get { return Dark ? Color.FromArgb(25, 33, 43) : Color.FromArgb(251, 253, 255); } }
        internal static Color Card { get { return Dark ? Color.FromArgb(29, 39, 50) : Color.White; } }
        internal static Color Sidebar { get { return Dark ? Color.FromArgb(41, 53, 67) : Color.FromArgb(233, 240, 247); } }
        internal static Color Foreground { get { return Dark ? Color.FromArgb(239, 245, 254) : Color.FromArgb(18, 32, 59); } }
        internal static Color Secondary { get { return Dark ? Color.FromArgb(175, 191, 211) : Color.FromArgb(108, 127, 155); } }
        internal static Color Border { get { return Dark ? Color.FromArgb(64, 80, 98) : Color.FromArgb(220, 229, 240); } }
        private static bool Read() { try { return File.ReadAllText(Path.Combine(UserDataPaths.SettingsDirectory, "launcher-theme.txt")).Trim() == "dark"; } catch { return false; } }
        internal static void Set(bool dark)
        {
            _dark = dark;
            try { File.WriteAllText(Path.Combine(UserDataPaths.SettingsDirectory, "launcher-theme.txt"), dark ? "dark" : "light"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (Changed != null) Changed(null, EventArgs.Empty);
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private static void ApplyTitleBar(Form form)
        {
            try
            {
                int value = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, 20, ref value, 4) != 0) DwmSetWindowAttribute(form.Handle, 19, ref value, 4);
                int caption = ColorTranslator.ToWin32(Background), text = ColorTranslator.ToWin32(Foreground), border = ColorTranslator.ToWin32(Border);
                DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
                DwmSetWindowAttribute(form.Handle, 34, ref border, 4);
            }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        internal static void Apply(Control root)
        {
            var tag = root.Tag as string;
            if (tag == "primary") { root.BackColor = Color.FromArgb(0, 105, 244); root.ForeColor = Color.White; }
            else if (tag == "nav-active") { root.BackColor = Dark ? Color.FromArgb(29, 72, 129) : Color.FromArgb(211, 234, 255); root.ForeColor = Dark ? Color.FromArgb(132, 201, 255) : Color.FromArgb(0, 94, 226); }
            else
            {
                root.BackColor = tag == "sidebar" ? Sidebar : root is Surface || root is TextBoxBase || root is ListBox || root is ListView || root is ComboBox || root is Button ? Card : root is TabPage || root is TabControl ? Background : root.Parent != null && !(root is Form) ? root.Parent.BackColor : Background;
                root.ForeColor = tag == "muted" ? Secondary : Foreground;
            }
            if (root is ThemeButton theme) root.BackColor = theme.IsSelected ? (Dark ? Color.FromArgb(29, 72, 129) : Color.FromArgb(220, 240, 255)) : Background;
            if (tag == "success") root.ForeColor = Dark ? Color.FromArgb(132, 216, 168) : Color.FromArgb(32, 106, 62);
            if (tag == "warning") root.ForeColor = Dark ? Color.FromArgb(255, 205, 135) : Color.FromArgb(154, 82, 12);
            if (root is Button b) { b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Dark ? Color.FromArgb(44, 71, 101) : Color.FromArgb(226, 240, 254); }
            if (root is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
            if (root is ListView || root is ListBox || (root is ScrollableControl scroll && scroll.AutoScroll)) NativeScrollTheme.Attach(root);
            foreach (Control child in root.Controls) Apply(child);
            if (root is Form form && form.IsHandleCreated) ApplyTitleBar(form);
            root.Invalidate();
        }
    }

    internal class RoundedButton : Button
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            int available = Parent == null ? 10000 : Math.Max(40, Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal);
            var natural = base.GetPreferredSize(proposedSize);
            if (natural.Width <= available) return natural;
            var measured = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(20, available - Padding.Horizontal), 0), TextFormatFlags.WordBreak);
            return new Size(available, Math.Max(MinimumSize.Height, measured.Height + Padding.Vertical + 8));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? LauncherTheme.Background : Parent.BackColor);
            var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using (var path = Rounded(rect, Math.Min(LogicalToDeviceUnits(8), rect.Height / 2)))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(Focused ? LauncherUi.Accent : LauncherTheme.Border))
            {
                e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
                TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? ForeColor : LauncherTheme.Secondary, (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
        }
        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = Math.Max(1, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
    }

    internal sealed class ThemeButton : RoundedButton
    {
        private readonly bool _moon;
        internal bool IsSelected { get { return LauncherTheme.Dark == _moon; } }
        internal ThemeButton(bool moon)
        {
            _moon = moon; AutoSize = false; Size = new Size(44, 42); Margin = new Padding(4, 0, 0, 0);
            FlatStyle = FlatStyle.Flat; Cursor = Cursors.Hand; AccessibleName = moon ? "夜间模式" : "日间模式";
            Click += (s, e) => LauncherTheme.Set(moon);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // Theme colors are assigned outside painting to avoid paint invalidation loops.
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = Math.Min(Width, Height) / 42f, x = Width / 2f, y = Height / 2f;
            using (var pen = new Pen(LauncherTheme.Dark == _moon ? Color.DodgerBlue : LauncherTheme.Foreground, 1.6f * unit))
            {
                if (_moon) { g.DrawArc(pen, x - 9 * unit, y - 10 * unit, 19 * unit, 20 * unit, 60, 250); g.DrawArc(pen, x - 2 * unit, y - 12 * unit, 17 * unit, 20 * unit, 100, 155); }
                else { g.DrawEllipse(pen, x - 5 * unit, y - 5 * unit, 10 * unit, 10 * unit); for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; g.DrawLine(pen, x + (float)Math.Cos(a) * 8 * unit, y + (float)Math.Sin(a) * 8 * unit, x + (float)Math.Cos(a) * 12 * unit, y + (float)Math.Sin(a) * 12 * unit); } }
            }
        }
    }

    internal sealed class ToggleSwitch : WrapCheckBox
    {
        internal ToggleSwitch() { AutoSize = true; Dock = DockStyle.Top; Cursor = Cursors.Hand; Margin = new Padding(0, 7, 0, 7); }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 1 && proposedSize.Width < 10000 ? proposedSize.Width : Parent == null ? 300 : Parent.ClientSize.Width - Parent.Padding.Horizontal;
            int knob = Math.Max(44, Font.Height * 2); var text = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(40, w - knob - 16), 0), TextFormatFlags.WordBreak);
            return new Size(w, Math.Max(text.Height + 10, Font.Height + 16));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Math.Max(20, Font.Height + 2), w = h * 2, x = Width - w - 2, y = (Height - h) / 2;
            using (var path = RoundedButton.Rounded(new Rectangle(x, y, w, h), h / 2))
            using (var brush = new SolidBrush(Checked && Enabled ? Color.FromArgb(0, 112, 255) : LauncherTheme.Dark ? Color.FromArgb(78, 94, 111) : Color.FromArgb(167, 182, 198))) e.Graphics.FillPath(brush, path);
            using (var white = new SolidBrush(Color.White)) e.Graphics.FillEllipse(white, Checked ? x + w - h + 2 : x + 2, y + 2, h - 4, h - 4);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(1, x - 12), Height), Enabled ? ForeColor : LauncherTheme.Secondary, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }

    internal sealed class Blueprint : Control
    {
        internal Blueprint() { Size = new Size(115, 150); DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(Width / 110f, Height / 145f); g.ScaleTransform(s, s);
            using (var fill = new SolidBrush(LauncherTheme.Dark ? Color.FromArgb(28, 60, 96) : Color.FromArgb(231, 243, 255)))
            using (var pen = new Pen(Color.FromArgb(76, 156, 242), 2))
            using (var grid = new Pen(Color.FromArgb(100, 133, 191, 247), .7f))
            { g.FillRectangle(fill, 4, 4, 100, 132); for (int i = 18; i < 100; i += 16) g.DrawLine(grid, i, 14, i, 126); for (int i = 18; i < 130; i += 16) g.DrawLine(grid, 12, i, 94, i);
              g.DrawLines(pen, new[] {new Point(25, 18),new Point(74, 18),new Point(92, 38),new Point(92, 125),new Point(25, 125),new Point(25, 18)});
              g.DrawLines(pen, new[] {new Point(74, 18),new Point(74, 38),new Point(92, 38)});
              g.DrawLines(pen, new[] {new Point(36, 112),new Point(57, 60),new Point(79, 112)}); g.DrawLine(pen, 44, 94, 70, 94); }
        }
    }

    internal sealed class LauncherShell : UserControl
    {
        internal int SidebarWidth { get { return _sidebar.Width; } }
        internal readonly Panel Body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        internal readonly Panel SidebarContent = new Panel { Dock = DockStyle.Fill, Tag = "sidebar", Margin = new Padding(0, 14, 0, 8) };
        private readonly TableLayoutPanel _layout;
        private readonly Panel _sidebar;
        private readonly PictureBox _brand;
        private readonly SplitCaption _caption;
        internal LauncherShell(string selected, Action home, Action projects, Action settings)
        {
            Dock = DockStyle.Fill;
            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178)); _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _caption = new SplitCaption(); _layout.Controls.Add(_caption, 0, 0); _layout.SetColumnSpan(_caption, 2);
            _sidebar = new BufferedScrollPanel { Name = "Sidebar", Dock = DockStyle.Fill, AutoScroll = true, Tag = "sidebar", Padding = new Padding(12, 24, 12, 12), Margin = Padding.Empty };
            var rows = new TableLayoutPanel { Dock = DockStyle.Top, Tag = "sidebar", ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var branding = new TableLayoutPanel { Name = "BrandHeader", Dock = DockStyle.Fill, ColumnCount = 2, Tag = "sidebar", Margin = Padding.Empty };
            branding.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); branding.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _brand = new PictureBox { Image = LoadBrand(), Tag = "sidebar", SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = new Label { Text = "万落建筑工具", Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Tag = "sidebar", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(8, 0, 0, 0) };
            branding.Controls.Add(_brand, 0, 0); branding.Controls.Add(title, 1, 0); rows.Controls.Add(branding, 0, 0);
            var nav = LauncherUi.Stack(); nav.Tag = "sidebar"; nav.Margin = new Padding(0, 16, 0, 0);
            LauncherUi.Add(nav, Nav("▷   启动工作台", "启动工作台", home, selected == "home"));
            LauncherUi.Add(nav, Nav("▱   项目管理", "项目管理", projects, selected == "projects"));
            LauncherUi.Add(nav, Nav("⚙   设置", "设置", settings, selected == "settings"));
            rows.Controls.Add(nav, 0, 1); rows.Controls.Add(SidebarContent, 0, 2);
            var version = LauncherUi.Text("v" + typeof(LauncherShell).Assembly.GetName().Version.ToString(3), 9); version.Tag = "sidebar"; version.Margin = Padding.Empty; rows.Controls.Add(version, 0, 3);
            _sidebar.Controls.Add(rows); _layout.Controls.Add(_sidebar, 0, 1); _layout.Controls.Add(Body, 1, 1); Controls.Add(_layout);
            ParentChanged += (s, e) => { var form = FindForm() as AdaptiveForm; if (form != null) form.UseSplitCaption(); };
            SizeChanged += (s, e) => Adapt(); DpiChangedAfterParent += (s, e) => Adapt();
            bool arranging = false;
            Action arrangeSidebar = () => {
                if (arranging) return;
                arranging = true;
                try {
                    float factor = Math.Max(DeviceDpi / 96f, Font.SizeInPoints / 9.5f);
                    bool compact = _sidebar.Width < 150 * factor; title.Visible = !compact;
                    branding.ColumnStyles[0].Width = compact ? Math.Max(1, _sidebar.ClientSize.Width - _sidebar.Padding.Horizontal) : 44 * factor;
                    int minimum = (int)rows.RowStyles[0].Height + nav.GetPreferredSize(new Size(Math.Max(1, _sidebar.ClientSize.Width - _sidebar.Padding.Horizontal), 0)).Height + nav.Margin.Vertical + version.PreferredHeight + SidebarContent.Margin.Vertical;
                    if (selected == "projects") minimum += Math.Max(LogicalToDeviceUnits(160), Font.Height * 8);
                    int available = _sidebar.ClientSize.Height - _sidebar.Padding.Vertical;
                    bool scroll = minimum > available;
                    _sidebar.AutoScroll = scroll;
                    if (scroll) NativeScrollTheme.Attach(_sidebar);
                    rows.Dock = scroll ? DockStyle.Top : DockStyle.Fill;
                    if (scroll) rows.Height = minimum;
                } finally { arranging = false; }
            };
            _sidebar.SizeChanged += (s, e) => arrangeSidebar();
            FontChanged += (s, e) => { Adapt(); arrangeSidebar(); };

        }
        internal static Control PageHeading(string title, string subtitle)
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var heading = LauncherUi.Stack();
            LauncherUi.Add(heading, new PageTitleLabel { Name = "PageTitle", Text = title, Font = new Font("Microsoft YaHei UI", 23, FontStyle.Bold), Height = 52, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty });
            LauncherUi.Add(heading, LauncherUi.Text(subtitle, 10, false, LauncherUi.Muted));
            header.Controls.Add(heading, 0, 0); header.Controls.Add(ThemeControls(), 1, 0); return header;
        }
        private Button Nav(string text, string name, Action action, bool active)
        {
            var button = LauncherUi.Button(text); button.AccessibleName = name; button.Dock = DockStyle.Top; button.MinimumSize = new Size(0, 44); button.Margin = new Padding(0, 5, 0, 5); button.Tag = active ? "nav-active" : "sidebar";
            button.Click += (s, e) => { if (action != null) action(); }; return button;
        }
        private void Adapt()
        {
            _layout.ColumnStyles[0].Width = Math.Min(Math.Max(LogicalToDeviceUnits(178), 178 * Font.SizeInPoints / 9.5f), Math.Max(1, ClientSize.Width * .27f));
            _caption.Divider = (int)_layout.ColumnStyles[0].Width; _caption.Invalidate();
            if (FindForm() is AdaptiveForm form) form.Invalidate();
        }
        internal static FlowLayoutPanel ThemeControls() { var flow = LauncherUi.Actions(new ThemeButton(false), new ThemeButton(true)); flow.WrapContents = false; flow.Dock = DockStyle.None; return flow; }
        internal static Image LoadBrand()
        {
            var assembly = typeof(LauncherShell).Assembly;
            using (var stream = assembly.GetManifestResourceStream("BatchPdfPublisherLauncher.BatchPdfPublisherIcon.png")) return stream == null ? null : new Bitmap(stream);
        }
        protected override void Dispose(bool disposing) { if (disposing && _brand.Image != null) _brand.Image.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class PageTitleLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            int width = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine).Width;
            float ratio = Math.Min(1, Math.Max(.65f, (ClientSize.Width - 4f) / Math.Max(1, width)));
            using (var font = new Font(Font.FontFamily, Font.Size * ratio, Font.Style))
                TextRenderer.DrawText(e.Graphics, Text, font, ClientRectangle, ForeColor, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    internal sealed class SplitCaption : Control
    {
        internal int Divider = 178;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        internal SplitCaption()
        {
            Dock = DockStyle.Fill; Margin = Padding.Empty; DoubleBuffered = true;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            foreach (var action in new[] { "最小化", "最大化", "关闭窗口" })
            {
                var button = new CaptionButton(action); buttons.Controls.Add(button);
                button.Click += (s, e) => { var f = FindForm(); if (f == null) return; if (action == "关闭窗口") f.Close(); else if (action == "最小化") f.WindowState = FormWindowState.Minimized; else f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; };
            }
            Controls.Add(buttons);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); var form = FindForm(); if (e.Button != MouseButtons.Left || form == null) return;
            if (e.Clicks == 2) { form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; return; }
            ReleaseCapture(); SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(LauncherTheme.Background);
            using (var brush = new SolidBrush(LauncherTheme.Sidebar)) e.Graphics.FillRectangle(brush, 0, 0, Divider, Height);
            TextRenderer.DrawText(e.Graphics, "万落建筑工具", Font, new Rectangle(LogicalToDeviceUnits(12), 0, Math.Max(1, Divider - 16), Height), LauncherTheme.Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    internal sealed class CaptionButton : Button
    {
        private bool _hover;
        internal CaptionButton(string action) { AccessibleName = action; Size = new Size(44, 32); Margin = Padding.Empty; TabStop = false; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool close = AccessibleName == "关闭窗口";
            e.Graphics.Clear(_hover ? (close ? Color.FromArgb(196, 43, 28) : LauncherTheme.Sidebar) : LauncherTheme.Background);
            using (var pen = new Pen(_hover && close ? Color.White : LauncherTheme.Secondary, Math.Max(1, DeviceDpi / 96f)))
            {
                int r = LogicalToDeviceUnits(4), x = Width / 2, y = Height / 2;
                if (close) { e.Graphics.DrawLine(pen, x-r, y-r, x+r, y+r); e.Graphics.DrawLine(pen, x+r, y-r, x-r, y+r); }
                else if (AccessibleName == "最小化") e.Graphics.DrawLine(pen, x-r, y, x+r, y);
                else { if (FindForm() != null && FindForm().WindowState == FormWindowState.Maximized) e.Graphics.DrawRectangle(pen, x-r+2, y-r-2, r*2, r*2); e.Graphics.DrawRectangle(pen, x-r, y-r, r*2, r*2); }
            }
        }
    }

}

namespace BatchPdfPublisherLauncher
{
    // Keep native dropdown, keyboard navigation and accessibility; paint its closed face in both themes.
    internal sealed class ThemedComboBox : ComboBox
    {
        internal ThemedComboBox() { DrawMode = DrawMode.OwnerDrawFixed; DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; ItemHeight = Font.Height + 10; }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style &= ~0x00800000; cp.ExStyle &= ~0x00000200; return cp; } }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LauncherUi.RoundWindow(this, 6); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); LauncherUi.RoundWindow(this, 6); }
        protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); NativeScrollTheme.AttachDropdown(this); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = Font.Height + LogicalToDeviceUnits(10); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? (LauncherTheme.Dark ? Color.FromArgb(29, 72, 129) : Color.FromArgb(220, 240, 255)) : LauncherTheme.Card)) e.Graphics.FillRectangle(brush, e.Bounds);
            var text = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 12), e.Bounds.Height), LauncherTheme.Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void WndProc(ref Message m)
        {
            // The native combo reserves a non-client rectangular frame even in FlatStyle.Flat.
            // Use the whole window as client area; our paint routine owns its sole border.
            if (m.Msg == 0x83 || m.Msg == 0x85) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
            if (m.Msg != 0xF && m.Msg != 0x317 && m.Msg != 0x318 && m.Msg != 0x85) return;
            using (var g = (m.Msg == 0x317 || m.Msg == 0x318) && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; using (var backdrop = new SolidBrush(Parent == null ? LauncherTheme.Card : Parent.BackColor)) g.FillRectangle(backdrop, ClientRectangle);
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundedButton.Rounded(rect, Math.Min(LogicalToDeviceUnits(6), Height / 2)))
                using (var fill = new SolidBrush(LauncherTheme.Card))
                using (var pen = new Pen(Focused ? LauncherUi.Accent : LauncherTheme.Border)) { g.FillPath(fill, path); g.DrawPath(pen, path); }
                int pad = LogicalToDeviceUnits(10), arrow = LogicalToDeviceUnits(24);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(pad, 0, Math.Max(1, Width - pad - arrow), Height), Enabled ? LauncherTheme.Foreground : LauncherTheme.Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int x = Width - arrow / 2 - 4, y = Height / 2;
                using (var pen = new Pen(LauncherTheme.Foreground, 1.4f)) g.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
            }
        }
    }
}

namespace BatchPdfPublisherLauncher
{
    internal sealed class ProjectShortcutButton : RoundedButton
    {
        internal string FolderPath { get; set; }
        internal ProjectShortcutButton() { AutoSize = true; Dock = DockStyle.Top; Margin = Padding.Empty; Padding = new Padding(16, 10, 16, 10); Cursor = Cursors.Hand; }
        public override Size GetPreferredSize(Size proposedSize) { return new Size(Parent == null ? 300 : Math.Max(40, Parent.ClientSize.Width - Parent.Padding.Horizontal), Math.Max(LogicalToDeviceUnits(62), Font.Height * 2 + Padding.Vertical)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(LauncherTheme.Card);
            int gap = LogicalToDeviceUnits(16), icon = LogicalToDeviceUnits(24), x = gap + icon + gap;
            using (var fill = new SolidBrush(Color.FromArgb(30, 139, 255))) { g.FillRectangle(fill, gap, Height / 2 - 9, icon / 2, 5); using (var shape = Rounded(new Rectangle(gap, Height / 2 - 5, icon, 17), 3)) g.FillPath(fill, shape); }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, gap / 2, Math.Max(1, Width - x - gap), Font.Height + 6), LauncherTheme.Foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, FolderPath ?? "默认项目文件夹", Font, new Rectangle(x, gap / 2 + Font.Height + 6, Math.Max(1, Width - x - gap), Font.Height + 4), LauncherTheme.Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (var pen = new Pen(LauncherTheme.Border)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(2, 2, Width - 4, Height - 4));
        }
    }
}

namespace BatchPdfPublisherLauncher
{
    internal sealed class ThemedTabControl : TabControl
    {
        private bool _fitting;
        internal ThemedTabControl() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); FitTabs(); }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); FitTabs(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitTabs(); }
        private void FitTabs()
        {
            if (TabCount == 0 || _fitting) return;
            _fitting = true;
            try {
                SizeMode = TabSizeMode.Fixed;
                var size = new Size(Math.Max(1, (ClientSize.Width - LogicalToDeviceUnits(8)) / TabCount), Math.Max(LogicalToDeviceUnits(38), Font.Height + LogicalToDeviceUnits(16)));
                if (ItemSize != size) ItemSize = size;
            } finally { _fitting = false; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(LauncherTheme.Background);
            for (int i = 0; i < TabCount; i++)
            {
                var rect = GetTabRect(i);
                using (var fill = new SolidBrush(i == SelectedIndex ? LauncherTheme.Card : LauncherTheme.Sidebar)) e.Graphics.FillRectangle(fill, rect);
                TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, rect, LauncherTheme.Foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (i == SelectedIndex) using (var pen = new Pen(LauncherUi.Accent, 2)) e.Graphics.DrawLine(pen, rect.Left + 8, rect.Bottom - 2, rect.Right - 8, rect.Bottom - 2);
            }
        }
    }
}

namespace BatchPdfPublisherLauncher
{
    internal sealed class BufferedScrollPanel : Panel
    {
        internal BufferedScrollPanel() { DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    }
    internal sealed class BufferedListView : ListView
    {
        internal BufferedListView() { DoubleBuffered = true; }
    }
}

namespace BatchPdfPublisherLauncher
{
    // Paint only native scrollbar chrome. Windows retains hit testing, dragging, wheel and accessibility.
    internal sealed class NativeScrollTheme : NativeWindow
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme> Windows = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme>();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme> Dropdowns = new System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeScrollTheme>();
        private bool _painting;
        private bool _queuedPaint;
        private const int RepaintScrollbars = 0x8000 + 421;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        private readonly Timer _trackingPaint = new Timer { Interval = 30 };
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string id);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ScrollInfo
        {
            public int Size; public Rect Bounds; public int Arrow, ThumbStart, ThumbEnd, Reserved;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 6)] public int[] States;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ComboInfo { public int Size; public Rect Item, Button; public int State; public IntPtr Combo, Edit, List; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr hwnd, int id, ref ScrollInfo info);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboInfo info);
        private NativeScrollTheme(Control owner, bool popup)
        {
            _trackingPaint.Tick += (s, e) => {
                if (Handle == IntPtr.Zero) { _trackingPaint.Stop(); return; }
                bool over = false;
                foreach (bool vertical in new[] { true, false }) {
                    ScrollInfo info; Rect rect;
                    if (Read(Handle, vertical, out info, out rect) && Rectangle.FromLTRB(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom).Contains(Cursor.Position)) over = true;
                }
                PaintBars(IntPtr.Zero);
                if (!over && Control.MouseButtons == MouseButtons.None) _trackingPaint.Stop();
            };
            if (!popup) { owner.HandleCreated += (s, e) => Bind(owner.Handle); if (owner.IsHandleCreated) Bind(owner.Handle); }
            owner.HandleDestroyed += (s, e) => { _trackingPaint.Stop(); if (Handle != IntPtr.Zero) ReleaseHandle(); };
            owner.Disposed += (s, e) => { _trackingPaint.Dispose(); if (Handle != IntPtr.Zero) ReleaseHandle(); };
        }
        private void Bind(IntPtr hwnd) { if (Handle == hwnd) return; if (Handle != IntPtr.Zero) ReleaseHandle(); if (hwnd != IntPtr.Zero) { AssignHandle(hwnd); SetWindowTheme(hwnd, "", ""); } }
        internal static void Attach(Control control)
        {
            var window = Windows.GetValue(control, c => new NativeScrollTheme(c, false));
            if (window.Handle != IntPtr.Zero) RedrawWindow(window.Handle, IntPtr.Zero, IntPtr.Zero, 0x401);
        }
        internal static void AttachDropdown(ComboBox combo)
        {
            var info = new ComboInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ComboInfo)) };
            if (GetComboBoxInfo(combo.Handle, ref info) && info.List != IntPtr.Zero)
                Dropdowns.GetValue(combo, c => new NativeScrollTheme(c, true)).Bind(info.List);
        }
        internal static Rectangle BarBounds(Control control, bool vertical)
        {
            ScrollInfo info; Rect window;
            return Read(control.Handle, vertical, out info, out window) ? new Rectangle(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top) : Rectangle.Empty;
        }
        private static bool Read(IntPtr hwnd, bool vertical, out ScrollInfo info, out Rect window)
        {
            info = new ScrollInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ScrollInfo)), States = new int[6] };
            window = new Rect();
            return GetWindowRect(hwnd, out window) && GetScrollBarInfo(hwnd, vertical ? -5 : -6, ref info) && (info.States[0] & 0x18000) == 0 && info.Bounds.Right > info.Bounds.Left && info.Bounds.Bottom > info.Bounds.Top;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == RepaintScrollbars) { _queuedPaint = false; if (!_painting && Handle != IntPtr.Zero) PaintBars(IntPtr.Zero); m.Result = IntPtr.Zero; return; }
            // Native scrollbar tracking runs a nested message loop. Keep repainting during
            // hover/drag there too; disabling UXTheme prevents delayed white hover animations.
            if (m.Msg == 0xA0 || m.Msg == 0xA1 || m.Msg == 0xA3) _trackingPaint.Start();
            base.WndProc(ref m);
            if (_painting || Handle == IntPtr.Zero) return;
            if ((m.Msg == 5 || m.Msg == 0x47 || m.Msg == 0x7D) && !_queuedPaint)
            { _queuedPaint = true; PostMessage(Handle, RepaintScrollbars, IntPtr.Zero, IntPtr.Zero); }
            if (m.Msg == 0x85 || m.Msg == 0xF || m.Msg == 0x114 || m.Msg == 0x115 || m.Msg == 0xA0 || m.Msg == 0x200 || m.Msg == 0x202 || m.Msg == 0x20A || m.Msg == 0xA2 || m.Msg == 0x317 || m.Msg == 0x2A2 || m.Msg == 0x113)
            {
                _painting = true;
                try { PaintBars(m.Msg == 0x317 ? m.WParam : IntPtr.Zero); }
                finally { _painting = false; }
            }
        }
        private void PaintBars(IntPtr printDc)
        {
            IntPtr dc = printDc == IntPtr.Zero ? GetWindowDC(Handle) : printDc;
            if (dc == IntPtr.Zero) return;
            try
            {
                using (var g = Graphics.FromHdc(dc))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle vertical = DrawBar(g, true), horizontal = DrawBar(g, false);
                    if (!vertical.IsEmpty && !horizontal.IsEmpty)
                        using (var fill = new SolidBrush(LauncherTheme.Sidebar)) g.FillRectangle(fill, vertical.Left, horizontal.Top, vertical.Width, horizontal.Height);
                }
            }
            finally { if (printDc == IntPtr.Zero) ReleaseDC(Handle, dc); }
        }
        private Rectangle DrawBar(Graphics g, bool vertical)
        {
            ScrollInfo info; Rect window;
            if (!Read(Handle, vertical, out info, out window)) return Rectangle.Empty;
            var rect = new Rectangle(info.Bounds.Left - window.Left, info.Bounds.Top - window.Top, info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top);
            using (var fill = new SolidBrush(LauncherTheme.Sidebar)) g.FillRectangle(fill, Rectangle.Inflate(rect, 1, 1));
            int thickness = vertical ? rect.Width : rect.Height, inset = Math.Max(3, thickness / 4);
            var thumb = vertical ? new Rectangle(rect.Left + inset, rect.Top + info.ThumbStart, Math.Max(1, rect.Width - inset * 2), info.ThumbEnd - info.ThumbStart)
                                 : new Rectangle(rect.Left + info.ThumbStart, rect.Top + inset, info.ThumbEnd - info.ThumbStart, Math.Max(1, rect.Height - inset * 2));
            if (thumb.Width > 0 && thumb.Height > 0 && (info.States[3] & 0x8001) == 0)
                using (var path = RoundedButton.Rounded(thumb, Math.Max(1, Math.Min(thumb.Width, thumb.Height) / 2)))
                using (var fill = new SolidBrush(LauncherTheme.Secondary)) g.FillPath(fill, path);
            int arrow = Math.Max(3, thickness / 5);
            using (var pen = new Pen(LauncherTheme.Secondary, 1.4f))
            {
                for (int end = 0; end < 2; end++)
                {
                    int cx = vertical ? rect.Left + rect.Width / 2 : end == 0 ? rect.Left + info.Arrow / 2 : rect.Right - info.Arrow / 2;
                    int cy = vertical ? end == 0 ? rect.Top + info.Arrow / 2 : rect.Bottom - info.Arrow / 2 : rect.Top + rect.Height / 2;
                    int d = end == 0 ? -1 : 1;
                    g.DrawLines(pen, vertical ? new[] { new Point(cx - arrow, cy - d * arrow / 2), new Point(cx, cy + d * arrow / 2), new Point(cx + arrow, cy - d * arrow / 2) }
                                              : new[] { new Point(cx - d * arrow / 2, cy - arrow), new Point(cx + d * arrow / 2, cy), new Point(cx - d * arrow / 2, cy + arrow) });
                }
            }
            return rect;
        }
    }
}
