using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    public abstract class CadAttributeWindow : DpiAwareForm
    {
        private readonly List<Image> _buttonIcons = new List<Image>();
        private readonly Label _caption = new Label();

        protected CadAttributeWindow(string caption, Size size, Size minimum)
        {
            Text = caption;
            Size = size;
            MinimumSize = minimum;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(7);
            BackColor = CadDialogTheme.Canvas;
            TextChanged += (sender, args) => _caption.Text = Text;
        }

        protected Control TitleBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5,
                BackColor = Color.FromArgb(27, 30, 40), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var mark = CadBrandIcon.CreateTitleMark();
            _caption.Text = Text;
            _caption.Dock = DockStyle.Fill;
            _caption.TextAlign = ContentAlignment.MiddleLeft;
            _caption.ForeColor = CadDialogTheme.Text;
            _caption.Margin = Padding.Empty;
            MouseEventHandler drag = (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag;
            mark.MouseDown += drag;
            _caption.MouseDown += drag;
            bar.Controls.Add(mark, 0, 0);
            bar.Controls.Add(_caption, 1, 0);
            bar.Controls.Add(Chrome("−", () => WindowState = FormWindowState.Minimized), 2, 0);
            var maximize = Chrome("□", () => WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized);
            SizeChanged += (sender, args) => maximize.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
            bar.Controls.Add(maximize, 3, 0);
            bar.Controls.Add(Chrome("×", Close, true), 4, 0);
            return bar;
        }

        private CadChromeButton Chrome(string text, Action action, bool close = false)
        {
            var button = new CadChromeButton { Text = text, Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(27, 30, 40), ForeColor = CadDialogTheme.Text,
                Margin = Padding.Empty, TabStop = false, IsCloseButton = close };
            button.Click += (sender, args) => action();
            return button;
        }

        internal CadRoundedButton Command(string text, Action action,
            PublisherForm.UiIcon? icon = null, bool primary = false, bool danger = false)
        {
            var button = CadDialogTheme.Button(text, action, primary, danger);
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Height = CadDialogTheme.ControlHeight;
            button.Margin = Padding.Empty;
            if (icon.HasValue)
            {
                var image = PublisherForm.DrawUiIcon(icon.Value, primary ? Color.White : CadDialogTheme.Muted);
                button.Image = image;
                _buttonIcons.Add(image);
            }
            return button;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(CadDialogTheme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && WindowState != FormWindowState.Maximized)
            {
                var packed = message.LParam.ToInt64();
                var screen = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
                var point = PointToClient(screen);
                var edge = Math.Max(6, DeviceDpi / 12);
                var left = point.X < edge;
                var right = point.X >= ClientSize.Width - edge;
                var top = point.Y < edge;
                var bottom = point.Y >= ClientSize.Height - edge;
                if (top && left) { message.Result = (IntPtr)13; return; }
                if (top && right) { message.Result = (IntPtr)14; return; }
                if (bottom && left) { message.Result = (IntPtr)16; return; }
                if (bottom && right) { message.Result = (IntPtr)17; return; }
                if (left) { message.Result = (IntPtr)10; return; }
                if (right) { message.Result = (IntPtr)11; return; }
                if (top) { message.Result = (IntPtr)12; return; }
                if (bottom) { message.Result = (IntPtr)15; return; }
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var image in _buttonIcons) image.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class CadAttributePrompt : CadAttributeWindow
    {
        private readonly TextBox _value = new TextBox();

        private CadAttributePrompt(string caption, string initial)
            : base(caption, new Size(440, 190), new Size(440, 190))
        {
            MaximizeBox = false;
            MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.None;
            _value.Text = initial ?? string.Empty;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3,
                ColumnCount = 1, BackColor = CadDialogTheme.Canvas };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);
            root.Controls.Add(TitleBar(), 0, 0);
            var field = CadAttributeUi.Field("方案名称", CadAttributeUi.Input(_value));
            field.Margin = new Padding(14, 14, 14, 0);
            root.Controls.Add(field, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4,
                Padding = new Padding(14, 11, 14, 11), BackColor = CadDialogTheme.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            var cancel = Command("取消", () => DialogResult = DialogResult.Cancel);
            var save = Command("保存", () => DialogResult = DialogResult.OK, null, true);
            footer.Controls.Add(cancel, 1, 0);
            footer.Controls.Add(save, 3, 0);
            root.Controls.Add(footer, 0, 2);
            AcceptButton = save;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string caption, string initial)
        {
            using (var prompt = new CadAttributePrompt(caption, initial))
                return prompt.ShowDialog(owner) == DialogResult.OK ? prompt._value.Text.Trim() : null;
        }
    }

    internal static class CadAttributeUi
    {
        public static Label Label(string text, bool heading = false)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, AutoSize = false,
                ForeColor = heading ? CadDialogTheme.Text : CadDialogTheme.Muted,
                Font = heading ? new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) : new Font("Microsoft YaHei UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        }

        public static Control Input(TextBox input, string placeholder = null)
        {
            var host = CadDialogTheme.Input(input);
            host.Dock = DockStyle.Fill;
            host.Margin = Padding.Empty;
            host.Placeholder = placeholder ?? string.Empty;
            return host;
        }

        public static PublisherForm.ThemedComboBox Combo(PublisherForm.ThemedComboBox combo, bool editable = false)
        {
            combo.Dock = DockStyle.Fill;
            combo.Margin = Padding.Empty;
            combo.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            return combo;
        }

        public static Control Field(string label, Control input)
        {
            var cell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight));
            cell.Controls.Add(Label(label), 0, 0);
            input.Dock = DockStyle.Fill;
            input.Margin = Padding.Empty;
            cell.Controls.Add(input, 0, 1);
            return cell;
        }

        public static void StyleGrid(DataGridView grid)
        {
            grid.BorderStyle = BorderStyle.None;
            grid.BackgroundColor = CadDialogTheme.Surface;
            grid.GridColor = CadDialogTheme.Border;
            grid.RowHeadersVisible = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeight = 38;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 36;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.ScrollBars = ScrollBars.None;
            grid.AllowUserToResizeRows = false;
            grid.DefaultCellStyle.BackColor = CadDialogTheme.Surface;
            grid.DefaultCellStyle.ForeColor = CadDialogTheme.Text;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(29, 93, 152);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F);
            grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(23, 39, 52);
            grid.AlternatingRowsDefaultCellStyle.ForeColor = CadDialogTheme.Text;
            grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(29, 93, 152);
            grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.BackColor = CadDialogTheme.Raised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = CadDialogTheme.Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = CadDialogTheme.Raised;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            grid.EditingControlShowing += (sender, args) =>
            {
                if (!(args.Control is TextBox editor)) return;
                editor.BorderStyle = BorderStyle.None;
                editor.BackColor = CadDialogTheme.Raised;
                editor.ForeColor = CadDialogTheme.Text;
            };
            grid.CellPainting += (sender, args) =>
            {
                if (args.RowIndex < 0 || args.ColumnIndex < 0
                    || !(grid.Columns[args.ColumnIndex] is DataGridViewCheckBoxColumn)) return;
                args.PaintBackground(args.CellBounds, true);
                var selected = args.Value is bool value && value;
                var box = new Rectangle(args.CellBounds.Left + (args.CellBounds.Width - 18) / 2,
                    args.CellBounds.Top + (args.CellBounds.Height - 18) / 2, 18, 18);
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = CadDialogTheme.Rounded(box, 4))
                using (var fill = new SolidBrush(selected ? CadDialogTheme.Accent : CadDialogTheme.Surface))
                using (var border = new Pen(selected ? CadDialogTheme.Accent : CadDialogTheme.Muted))
                {
                    args.Graphics.FillPath(fill, path);
                    args.Graphics.DrawPath(border, path);
                }
                if (selected)
                {
                    using (var check = new Pen(Color.White, 2F))
                    {
                        check.StartCap = LineCap.Round;
                        check.EndCap = LineCap.Round;
                        args.Graphics.DrawLines(check, new[] { new Point(box.Left + 4, box.Top + 9),
                            new Point(box.Left + 8, box.Top + 13), new Point(box.Left + 14, box.Top + 5) });
                    }
                }
                args.Handled = true;
            };
            grid.RowPostPaint += (sender, args) =>
            {
                if (args.RowIndex < 0 || !grid.Rows[args.RowIndex].Selected) return;
                using (var accent = new SolidBrush(Color.FromArgb(73, 213, 255)))
                    args.Graphics.FillRectangle(accent, args.RowBounds.Left, args.RowBounds.Top, 3, args.RowBounds.Height);
            };
        }

        public static ToolStripDropDown Menu(Control anchor, IEnumerable<Tuple<string, Action>> entries,
            int width = 190, Point? location = null)
        {
            var actions = entries.ToList();
            var height = Math.Min(328, actions.Count * 40 + 8);
            var content = new TableLayoutPanel { ColumnCount = 1, RowCount = actions.Count,
                Size = new Size(width, actions.Count * 40 + 8), Padding = new Padding(4),
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            for (var i = 0; i < actions.Count; i++)
            {
                content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                var entry = actions[i];
                var button = CadDialogTheme.Button(entry.Item1, null);
                button.AutoSize = false;
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(0, 2, 0, 2);
                content.Controls.Add(button, 0, i);
            }
            var scrollHost = new CadScrollHost(content) { Size = new Size(width, height) };
            var host = new ToolStripControlHost(scrollHost) { AutoSize = false, Size = scrollHost.Size,
                Margin = Padding.Empty, Padding = Padding.Empty };
            var popup = new ToolStripDropDown { AutoClose = true, AutoSize = false,
                Size = scrollHost.Size, BackColor = CadDialogTheme.Surface, Padding = Padding.Empty };
            popup.Items.Add(host);
            for (var i = 0; i < actions.Count; i++)
            {
                var entry = actions[i];
                content.Controls[i].Click += (sender, args) => { popup.Close(); entry.Item2(); };
            }
            var dispatcher = anchor.FindForm() ?? anchor;
            popup.Closed += (sender, args) =>
            {
                if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) { popup.Dispose(); return; }
                dispatcher.BeginInvoke((Action)(() => popup.Dispose()));
            };
            var point = location ?? new Point(0, anchor.Height);
            popup.Show(anchor, point.X, point.Y);
            return popup;
        }
    }

    internal sealed class CadAttributeGridHost : Panel
    {
        private readonly DataGridView _grid;
        private readonly PublisherForm.DarkGridScrollBar _vertical;
        private readonly PublisherForm.DarkGridScrollBar _horizontal;
        private bool _layingOut;

        public CadAttributeGridHost(DataGridView grid)
        {
            _grid = grid;
            BackColor = CadDialogTheme.Surface;
            Margin = Padding.Empty;
            DoubleBuffered = true;
            _vertical = new PublisherForm.DarkGridScrollBar(grid, true);
            _horizontal = new PublisherForm.DarkGridScrollBar(grid, false);
            Controls.Add(grid);
            Controls.Add(_vertical);
            Controls.Add(_horizontal);
            grid.RowsAdded += (sender, args) => LayoutGrid();
            grid.RowsRemoved += (sender, args) => LayoutGrid();
            grid.ColumnWidthChanged += (sender, args) => LayoutGrid();
            grid.DataBindingComplete += (sender, args) => LayoutGrid();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutGrid();
        }

        private void LayoutGrid()
        {
            if (_layingOut || ClientSize.Width < 30 || ClientSize.Height < 30) return;
            _layingOut = true;
            try
            {
                var totalWidth = _grid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible).Sum(column => column.Width);
                var vertical = _grid.RowCount * _grid.RowTemplate.Height + _grid.ColumnHeadersHeight > ClientSize.Height;
                var horizontal = totalWidth > ClientSize.Width - (vertical ? 16 : 0);
                if (horizontal && !vertical)
                    vertical = _grid.RowCount * _grid.RowTemplate.Height + _grid.ColumnHeadersHeight > ClientSize.Height - 16;
                var width = Math.Max(1, ClientSize.Width - (vertical ? 16 : 0));
                var height = Math.Max(1, ClientSize.Height - (horizontal ? 16 : 0));
                _grid.SetBounds(0, 0, width, height);
                _vertical.Visible = vertical;
                _horizontal.Visible = horizontal;
                if (vertical) _vertical.SetBounds(width, 0, 16, height);
                if (horizontal) _horizontal.SetBounds(0, height, width, 16);
                _vertical.Invalidate();
                _horizontal.Invalidate();
            }
            finally { _layingOut = false; }
        }
    }
}
