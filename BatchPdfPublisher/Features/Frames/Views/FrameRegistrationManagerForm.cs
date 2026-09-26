using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public sealed class FrameRegistrationManagerForm : DpiAwareForm
    {
        public enum CadAction { None, Pick, Create }

        private readonly PublisherViewModel _viewModel;
        private readonly Action _refreshPublisher;
        private readonly ListBox _frames = new FrameListBox();
        private readonly TextBox _search = new TextBox();
        private readonly Label _count = new Label();
        private readonly Label _notice = new Label();
        private readonly Label _range = new Label();
        private readonly Label _scan = new Label();
        private readonly Dictionary<string, ReadonlyValue> _values = new Dictionary<string, ReadonlyValue>();
        private readonly List<Image> _icons = new List<Image>();
        private bool _refreshing;

        public CadAction RequestedCadAction { get; private set; }

        public FrameRegistrationManagerForm(PublisherViewModel viewModel, Action refreshPublisher)
        {
            _viewModel = viewModel;
            _refreshPublisher = refreshPublisher;
            Text = "图框登记";
            Width = 1220;
            Height = 850;
            MinimumSize = new Size(1040, 710);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(1);
            Build();
            RefreshFrames();
        }

        private void Build()
        {
            BackColor = CadDialogTheme.Border;
            ForeColor = CadDialogTheme.Text;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);
            root.Controls.Add(BuildTitleBar(), 0, 0);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(20, 12, 20, 12), Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            heading.Controls.Add(new Label { Text = "图框登记", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Text }, 0, 0);
            heading.Controls.Add(new Label { Text = "当前工程：" + (_viewModel.SelectedProject?.Name ?? "未选择"), Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight, ForeColor = CadDialogTheme.Muted }, 1, 0);
            root.Controls.Add(heading, 0, 1);

            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Padding = new Padding(12), Margin = Padding.Empty, BackColor = CadDialogTheme.Canvas };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 286));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(workspace, 0, 2);

            var leftCard = CadDialogTheme.Card();
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1,
                Margin = Padding.Empty, BackColor = CadDialogTheme.Surface };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var leftTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            leftTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            leftTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            leftTitle.Controls.Add(Section("已登记图框"), 0, 0);
            _count.Dock = DockStyle.Fill; _count.TextAlign = ContentAlignment.MiddleRight;
            _count.ForeColor = CadDialogTheme.Muted;
            leftTitle.Controls.Add(_count, 1, 0);
            left.Controls.Add(leftTitle, 0, 0);
            var searchHost = CadDialogTheme.Input(_search);
            searchHost.Placeholder = "搜索图块名称或备注...";
            searchHost.LeadingIcon = MakeIcon(PublisherForm.UiIcon.Search, CadDialogTheme.Muted);
            searchHost.Margin = new Padding(0, 0, 0, 12);
            left.Controls.Add(searchHost, 0, 1);
            CadDialogTheme.StyleList(_frames);
            _frames.ItemHeight = 44;
            var listHost = new CadListHost(_frames) { Padding = new Padding(7) };
            left.Controls.Add(listHost, 0, 2);
            left.Controls.Add(new Label { Text = "选择一项查看或修改登记", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted }, 0, 3);
            leftCard.Controls.Add(left);
            workspace.Controls.Add(leftCard, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspace.Controls.Add(right, 2, 0);
            var actionsCard = CadDialogTheme.Card();
            actionsCard.Padding = new Padding(12);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            foreach (var width in new[] { 140, 140, 140, 150 }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            actions.Controls.Add(ActionButton("拾取登记", PublisherForm.UiIcon.Select, Pick, true), 0, 0);
            actions.Controls.Add(ActionButton("创建图框", PublisherForm.UiIcon.Plus, Create), 1, 0);
            actions.Controls.Add(ActionButton("修改信息", PublisherForm.UiIcon.Gear, Edit), 2, 0);
            actions.Controls.Add(ActionButton("保存图框库", PublisherForm.UiIcon.Save, SaveLibrary), 3, 0);
            actions.Controls.Add(ActionButton("删除登记", PublisherForm.UiIcon.Trash, Delete, false, true), 5, 0);
            actionsCard.Controls.Add(actions);
            right.Controls.Add(actionsCard, 0, 0);

            var detailCard = CadDialogTheme.Card();
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            for (var i = 0; i < 3; i++) detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            for (var i = 0; i < 4; i++) detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            detail.Controls.Add(Section("▤  基本信息"), 0, 0);
            detail.Controls.Add(PairRow("图块名称", "block", "用户备注", "note"), 0, 1);
            detail.Controls.Add(PairRow("纸张规格", "paper", "加长", "extension"), 0, 2);
            detail.Controls.Add(PairRow("纸张方向", "orientation", "默认比例", "scale"), 0, 3);
            _notice.Dock = DockStyle.Fill; _notice.TextAlign = ContentAlignment.MiddleLeft;
            _notice.Padding = new Padding(12, 0, 12, 0); _notice.Margin = new Padding(0, 6, 0, 6);
            _notice.BackColor = Color.FromArgb(29, 60, 57); _notice.ForeColor = Color.FromArgb(132, 223, 176);
            detail.Controls.Add(_notice, 0, 4);
            detail.Controls.Add(Section("☷  属性映射与默认值"), 0, 5);
            detail.Controls.Add(MappingRow("", "CAD 属性标签", "检测值 / 手工默认值", true), 0, 6);
            detail.Controls.Add(MappingRow("子项目名称", "buildingTag", "buildingValue"), 0, 7);
            detail.Controls.Add(MappingRow("图号", "numberTag", "numberValue"), 0, 8);
            detail.Controls.Add(MappingRow("图名", "nameTag", "nameValue"), 0, 9);
            detail.Controls.Add(MappingRow("打印比例", "scaleTag", "scaleValue"), 0, 10);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 8, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var rangePane = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = CadDialogTheme.Surface };
            rangePane.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            rangePane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rangePane.Controls.Add(Section("▧  排版范围"), 0, 0);
            _range.Dock = DockStyle.Fill; _range.ForeColor = CadDialogTheme.Muted;
            _range.TextAlign = ContentAlignment.TopLeft; _range.AutoEllipsis = true;
            rangePane.Controls.Add(_range, 0, 1);
            bottom.Controls.Add(rangePane, 0, 0);
            var scanPane = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = CadDialogTheme.Surface };
            scanPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            scanPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            scanPane.Controls.Add(Section("⊙  工程图框检查"), 0, 0);
            _scan.Dock = DockStyle.Fill; _scan.ForeColor = CadDialogTheme.Muted;
            _scan.TextAlign = ContentAlignment.TopLeft;
            _scan.Text = "修改登记时检查同名图块和重复属性 TAG。";
            scanPane.Controls.Add(_scan, 0, 1);
            bottom.Controls.Add(scanPane, 1, 0);
            detail.Controls.Add(bottom, 0, 11);
            detailCard.Controls.Add(detail);
            right.Controls.Add(detailCard, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(16, 8, 16, 8), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            footer.Controls.Add(new Label { Text = "拾取图框或排版范围时将返回 CAD 画布。", Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            footer.Controls.Add(ActionButton("关闭", PublisherForm.UiIcon.Remove, Close), 1, 0);
            root.Controls.Add(footer, 0, 3);
            _search.TextChanged += (sender, args) => RefreshFrames();
            _frames.SelectedIndexChanged += (sender, args) => { if (!_refreshing) ShowSelection(); };
            _frames.DoubleClick += (sender, args) => Edit();
        }

        private TableLayoutPanel PairRow(string first, string firstKey, string second, string secondKey)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.Controls.Add(Field(first), 0, 0);
            row.Controls.Add(Value(firstKey), 1, 0);
            row.Controls.Add(Field(second), 2, 0);
            row.Controls.Add(Value(secondKey), 3, 0);
            return row;
        }

        private TableLayoutPanel MappingRow(string heading, string tag, string value, bool labels = false)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            row.Controls.Add(Field(heading), 0, 0);
            row.Controls.Add(labels ? (Control)Field(tag) : Value(tag), 1, 0);
            row.Controls.Add(labels ? (Control)Field(value) : Value(value), 2, 0);
            return row;
        }

        private ReadonlyValue Value(string key)
        {
            var value = new ReadonlyValue { Dock = DockStyle.Fill, Margin = new Padding(3, 2, 8, 5) };
            _values[key] = value;
            return value;
        }

        private static Label Field(string text) => new Label { Text = text, Dock = DockStyle.Fill,
            ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            Margin = new Padding(3, 0, 5, 0) };

        private Label Section(string text) => new Label { Text = text, Dock = DockStyle.Fill,
            ForeColor = CadDialogTheme.Text, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };

        private CadRoundedButton ActionButton(string text, PublisherForm.UiIcon icon, Action action,
            bool primary = false, bool danger = false)
        {
            var button = CadDialogTheme.Button(text, action, primary, danger);
            button.AutoSize = false; button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 1, 5, 1);
            button.Image = MakeIcon(icon, primary ? Color.White : danger ? CadDialogTheme.Danger : CadDialogTheme.Muted);
            return button;
        }

        private Image MakeIcon(PublisherForm.UiIcon icon, Color color)
        {
            var image = PublisherForm.DrawUiIcon(icon, color);
            _icons.Add(image);
            return image;
        }

        private Control BuildTitleBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5,
                BackColor = Color.FromArgb(27, 30, 40), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var mark = CadBrandIcon.CreateTitleMark();
            var caption = new Label { Text = "图框登记", Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Text, TextAlign = ContentAlignment.MiddleLeft };
            MouseEventHandler drag = (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture(); SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag; caption.MouseDown += drag; mark.MouseDown += drag;
            bar.Controls.Add(mark, 0, 0); bar.Controls.Add(caption, 1, 0);
            bar.Controls.Add(ChromeButton("−", () => WindowState = FormWindowState.Minimized), 2, 0);
            bar.Controls.Add(ChromeButton("□", () => WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized), 3, 0);
            bar.Controls.Add(ChromeButton("×", Close), 4, 0);
            return bar;
        }

        private static Button ChromeButton(string text, Action action)
        {
            var button = new CadChromeButton { Text = text, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, BackColor = Color.FromArgb(27, 30, 40),
                Margin = Padding.Empty, TabStop = false, IsCloseButton = text == "×" };
            button.Click += (sender, args) => action();
            return button;
        }

        private void RefreshFrames()
        {
            var selected = _frames.SelectedItem as FrameDefinition ?? _viewModel.SelectedFrame;
            var query = (_search.Text ?? string.Empty).Trim();
            var frames = _viewModel.Frames.Where(frame => string.IsNullOrWhiteSpace(query)
                || (frame.BlockName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (frame.Note ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _refreshing = true;
            try
            {
                _frames.BeginUpdate();
                _frames.Items.Clear();
                foreach (var frame in frames) _frames.Items.Add(frame);
                _frames.SelectedItem = selected == null ? frames.FirstOrDefault()
                    : frames.FirstOrDefault(frame => !string.IsNullOrWhiteSpace(selected.RegistrationId)
                        && string.Equals(frame.RegistrationId, selected.RegistrationId, StringComparison.OrdinalIgnoreCase))
                        ?? frames.FirstOrDefault(frame => string.Equals(frame.BlockName, selected.BlockName, StringComparison.OrdinalIgnoreCase))
                        ?? frames.FirstOrDefault();
                _frames.EndUpdate();
                _count.Text = "共 " + _viewModel.Frames.Count + " 项";
            }
            finally { _refreshing = false; }
            ShowSelection();
        }

        private void ShowSelection()
        {
            var frame = _frames.SelectedItem as FrameDefinition;
            _viewModel.SelectedFrame = frame;
            Set("block", frame?.BlockName);
            Set("note", frame?.Note);
            Set("paper", frame?.PaperSize);
            Set("extension", string.IsNullOrWhiteSpace(frame?.Extension) ? "无加长" : frame.Extension);
            Set("orientation", frame?.PaperOrientation);
            Set("scale", frame?.DefaultPrintScale);
            Set("buildingTag", frame?.BuildingAttributeTag);
            Set("buildingValue", frame?.DefaultBuilding);
            Set("numberTag", frame?.SheetNumberAttributeTag);
            Set("numberValue", frame?.DefaultSheetNumber);
            Set("nameTag", frame?.SheetNameAttributeTag);
            Set("nameValue", frame?.DefaultSheetName);
            Set("scaleTag", frame?.PrintScaleAttributeTag);
            Set("scaleValue", frame?.DefaultPrintScale);
            _notice.Text = frame == null ? "请选择一项登记，或从 CAD 图中拾取新图框。"
                : "当前图框规则已加载。修改前将检查 CAD 图块及属性标签。";
            _range.Text = frame == null ? "尚未选择图框" : Services.FrameLayoutRangeService.Describe(frame);
        }

        private void Set(string key, string text) { _values[key].Text = string.IsNullOrWhiteSpace(text) ? "—" : text; }

        private void Pick() { RequestedCadAction = CadAction.Pick; Close(); }
        private void Create() { RequestedCadAction = CadAction.Create; Close(); }

        private void Edit()
        {
            if (_frames.SelectedItem == null) { MessageBox.Show(this, "请先选择图框登记。", "图框登记"); return; }
            _viewModel.SelectedFrame = _frames.SelectedItem as FrameDefinition;
            _viewModel.EditFrameCommand.Execute(null);
            RefreshFrames(); _refreshPublisher();
        }

        private void Delete()
        {
            var frame = _frames.SelectedItem as FrameDefinition;
            if (frame == null) return;
            if (MessageBox.Show(this, "从图框库删除“" + frame.BlockName + "”？不会删除 CAD 中的图块。",
                "删除图框登记", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _viewModel.SelectedFrame = frame;
            _viewModel.RemoveFrameCommand.Execute(null);
            RefreshFrames(); _refreshPublisher();
        }

        private void SaveLibrary()
        {
            _viewModel.SaveFrameLibraryCommand.Execute(null);
            _notice.Text = _viewModel.Status;
            _refreshPublisher();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var icon in _icons) icon.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr word, IntPtr data);

        private sealed class ReadonlyValue : Control
        {
            public ReadonlyValue() { Height = CadDialogTheme.ControlHeight; BackColor = CadDialogTheme.Raised;
                ForeColor = CadDialogTheme.Text; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(Parent == null ? CadDialogTheme.Surface : Parent.BackColor);
                var bounds = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = CadDialogTheme.Rounded(bounds, 6))
                using (var fill = new SolidBrush(BackColor))
                using (var pen = new Pen(CadDialogTheme.Border))
                { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path); }
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(11, 0, Math.Max(1, Width - 22), Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        }

        private sealed class FrameListBox : CadPlainListBox
        {
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                using (var fill = new SolidBrush(BackColor)) e.Graphics.FillRectangle(fill, e.Bounds);
                var frame = Items[e.Index] as FrameDefinition;
                if (frame == null) return;
                if ((e.State & DrawItemState.Selected) != 0)
                {
                    var bounds = new Rectangle(e.Bounds.Left + 1, e.Bounds.Top + 1, e.Bounds.Width - 2, e.Bounds.Height - 2);
                    var state = e.Graphics.Save();
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = CadDialogTheme.Rounded(bounds, 5))
                    using (var selected = new SolidBrush(Color.FromArgb(24, 103, 179)))
                    using (var mark = new SolidBrush(Color.FromArgb(73, 213, 255)))
                    { e.Graphics.FillPath(selected, path); e.Graphics.SetClip(path, CombineMode.Intersect);
                        e.Graphics.FillRectangle(mark, bounds.Left, bounds.Top, 5, bounds.Height); }
                    e.Graphics.Restore(state);
                }
                var x = e.Bounds.Left + 10;
                TextRenderer.DrawText(e.Graphics, frame.BlockName ?? "未命名图框", Font,
                    new Rectangle(x, e.Bounds.Top + 3, Math.Max(1, e.Bounds.Width - 20), 21), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var details = (frame.PaperSize ?? "—") + (string.IsNullOrWhiteSpace(frame.Extension) ? "" : "+" + frame.Extension)
                    + " · " + (frame.PaperOrientation ?? "—") + " · " + (frame.DefaultPrintScale ?? "—");
                TextRenderer.DrawText(e.Graphics, details, Font,
                    new Rectangle(x, e.Bounds.Top + 22, Math.Max(1, e.Bounds.Width - 20), 18), CadDialogTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }
}
