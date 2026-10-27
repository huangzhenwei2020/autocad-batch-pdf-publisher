using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using BatchPdfPublisher.Services;
using ObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;

namespace BatchPdfPublisher.Views
{
    internal sealed class LineCorrectionForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly NumericUpDown _angle = new NumericUpDown { Minimum = 0, Maximum = 44.9m, DecimalPlaces = 2, Increment = .1m, Value = 1, Width = 88 };
        private readonly CheckBox _horizontal = new CheckBox { Text = "校正水平", Checked = true, AutoSize = true };
        private readonly CheckBox _vertical = new CheckBox { Text = "校正垂直", Checked = true, AutoSize = true };
        private readonly CheckBox _flatten = new CheckBox { Text = "顶点 Z 归零", Checked = true, AutoSize = true };
        private readonly DataGridView _grid = new DataGridView();
        private readonly LineCorrectionPreview _preview = new LineCorrectionPreview();
        private readonly Label _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Label _selection = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Button _apply;
        private readonly TableLayoutPanel _layout, _toolbar;
        private readonly Control _previewHost;
        private readonly Button _previewToggle;
        private readonly ToolTip _tips = new ToolTip();
        private readonly List<Image> _images = new List<Image>();
        private bool _refreshing, _compactPreview, _updatingLayout;
        internal ObjectId[] SelectedIds { get; private set; } = new ObjectId[0];
        internal LineCorrectionOptions Options { get { return new LineCorrectionOptions { ToleranceDegrees = (double)_angle.Value,
            Horizontal = _horizontal.Checked, Vertical = _vertical.Checked, FlattenZ = _flatten.Checked }; } }

        internal LineCorrectionForm(Document document)
        {
            _document = document;
            Text = "线条校正"; Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = CadDialogTheme.Canvas; ForeColor = CadDialogTheme.Text;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(960, 660); MinimumSize = new Size(620, 400); MinimizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 10), ColumnCount = 1, RowCount = 7 };
            _layout = root;
            foreach (var height in new[] { 40, 66, 40 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.Controls.Add(BuildHeader(), 0, 0); root.Controls.Add(BuildOptions(), 0, 1);
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            _toolbar = toolbar;
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            toolbar.Controls.Add(ButtonFor("选择对象", PublisherForm.UiIcon.Select, SelectObjects), 0, 0); toolbar.Controls.Add(_selection, 1, 0);
            _previewToggle = ButtonFor("预览", PublisherForm.UiIcon.Switch, () => { _compactPreview = !_compactPreview; UpdateResponsiveLayout(); });
            toolbar.Controls.Add(_previewToggle, 2, 0);
            toolbar.Controls.Add(ButtonFor("恢复默认", PublisherForm.UiIcon.Refresh, ResetOptions), 3, 0); root.Controls.Add(toolbar, 0, 2);
            _previewHost = BuildPreview(); root.Controls.Add(_previewHost, 0, 3); ConfigureGrid(); root.Controls.Add(_grid, 0, 4); root.Controls.Add(_status, 0, 5);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            footer.Controls.Add(new Label { Text = "预览未应用", ForeColor = CadDialogTheme.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _apply = ButtonFor("应用校正", PublisherForm.UiIcon.Publish, () => { DialogResult = DialogResult.OK; Close(); }, true);
            _apply.Enabled = false; footer.Controls.Add(_apply, 1, 0);
            var close = ButtonFor("取消", PublisherForm.UiIcon.Remove, () => Close()); close.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(close, 2, 0); root.Controls.Add(footer, 0, 6); Controls.Add(root); CancelButton = close; AcceptButton = _apply;
            root.SizeChanged += (s, e) => UpdateResponsiveLayout(); UpdateResponsiveLayout();
            _tips.SetToolTip(_angle, "与水平或垂直方向的最大偏差，单位为度。");
            _tips.SetToolTip(_flatten, "仅处理当前所选对象的顶点，不改变未选择对象。");
            _angle.ValueChanged += (s, e) => RefreshPreview(); _horizontal.CheckedChanged += (s, e) => RefreshPreview();
            _vertical.CheckedChanged += (s, e) => RefreshPreview(); _flatten.CheckedChanged += (s, e) => RefreshPreview();
            var implied = document.Editor.SelectImplied(); if (implied.Status == PromptStatus.OK) SelectedIds = implied.Value.GetObjectIds();
            RefreshPreview(); ActiveControl = _grid;
        }

        private Control BuildHeader()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(RibbonIconAssets.ForFeature("line_correction")));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream); stream.Position = 0;
                using (var source = Image.FromStream(stream)) _images.Add(new Bitmap(source));
            }
            row.Controls.Add(new PictureBox { Image = _images.Last(), SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 10, 6) }, 0, 0);
            row.Controls.Add(new Label { Text = "线条校正", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), Margin = Padding.Empty }, 1, 0);
            row.Controls.Add(new Label { Text = Path.GetFileName(_document.Name), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true, ForeColor = CadDialogTheme.Muted }, 2, 0); return row;
        }

        private Control BuildOptions()
        {
            var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 3, 0, 4), BackColor = CadDialogTheme.Surface };
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(10, 6, 0, 0), Margin = Padding.Empty };
            row.Controls.Add(new Label { Text = "角度偏差", Width = 68, Height = 27, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty });
            _angle.BackColor = CadDialogTheme.Raised; _angle.ForeColor = CadDialogTheme.Text; _angle.BorderStyle = BorderStyle.FixedSingle;
            _angle.Margin = new Padding(0, 1, 0, 0); row.Controls.Add(_angle);
            row.Controls.Add(new Label { Text = "°", Width = 28, Height = 27, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 12, 0) });
            foreach (var check in new[] { _horizontal, _vertical, _flatten })
            { check.Margin = new Padding(0, 5, 14, 0); check.ForeColor = CadDialogTheme.Text; row.Controls.Add(check); }
            options.Controls.Add(row, 0, 0);
            options.Controls.Add(new Label { Text = "坐标系   WCS XY       基点   线段中点", Dock = DockStyle.Fill,
                Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft, ForeColor = CadDialogTheme.Muted, Margin = Padding.Empty }, 0, 1); return options;
        }

        private Control BuildPreview()
        {
            var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 8), BackColor = CadDialogTheme.Surface };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            host.Controls.Add(new Label { Text = "图形预览", Dock = DockStyle.Fill, Padding = new Padding(10, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                var mode = new RadioButton { Text = new[] { "XY", "XZ", "YZ" }[i], Appearance = Appearance.Button, Width = 44, Height = 27,
                    TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 1, 3, 0), Checked = i == 0 };
                mode.FlatAppearance.CheckedBackColor = Color.FromArgb(30, 89, 119); mode.FlatAppearance.BorderColor = CadDialogTheme.Border;
                mode.CheckedChanged += (s, e) => { if (mode.Checked) { _preview.Projection = index; _preview.Invalidate(); } }; modes.Controls.Add(mode);
            }
            host.Controls.Add(modes, 1, 0); _preview.Dock = DockStyle.Fill; _preview.Margin = Padding.Empty;
            host.Controls.Add(_preview, 0, 1); host.SetColumnSpan(_preview, 2); return host;
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.Margin = Padding.Empty; _grid.ReadOnly = true; _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false; _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.BackgroundColor = CadDialogTheme.Surface; _grid.BorderStyle = BorderStyle.None; _grid.GridColor = CadDialogTheme.Border;
            _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.ColumnHeadersHeight = 32; _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = CadDialogTheme.Raised, ForeColor = CadDialogTheme.Text, SelectionBackColor = CadDialogTheme.Raised };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = CadDialogTheme.Surface, ForeColor = CadDialogTheme.Text,
                SelectionBackColor = Color.FromArgb(28, 72, 101), SelectionForeColor = Color.White, Padding = new Padding(3, 5, 3, 5) };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "对象", DataPropertyName = "Handle", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", DataPropertyName = "Type", Width = 88,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "校正段数", DataPropertyName = "Segments", Width = 76 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "归零顶点", DataPropertyName = "VerticesZ", Width = 76 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最大位移", DataPropertyName = "Movement", Width = 86, ToolTipText = "图形单位，包含 Z 向位移" });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "对象明细 / 状态", DataPropertyName = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 170,
                DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            _grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex == 1 && e.Value != null)
                { var value = e.Value.ToString(); e.Value = value == "LINE" ? "直线" : value == "LWPOLYLINE" || value == "POLYLINE" ? "多段线" : value; e.FormattingApplied = true; }
                if (e.ColumnIndex == 5 && e.RowIndex >= 0)
                {
                    var plan = _grid.Rows[e.RowIndex].DataBoundItem as LineCorrectionPlan;
                    e.CellStyle.ForeColor = plan == null || plan.Result == null || !string.IsNullOrEmpty(plan.Result.Warning)
                        ? LineCorrectionPreview.WarningColor : plan.Result.Changed ? LineCorrectionPreview.ResultColor : CadDialogTheme.Muted;
                }
            };
            _grid.SelectionChanged += (s, e) => UpdatePreviewSelection();
            _grid.CurrentCellChanged += (s, e) => UpdatePreviewSelection();
        }

        private void UpdatePreviewSelection()
        {
            _preview.Highlight = _grid.CurrentRow == null ? null : _grid.CurrentRow.DataBoundItem as LineCorrectionPlan;
            _preview.Invalidate();
        }

        private void UpdateResponsiveLayout()
        {
            if (_updatingLayout) return;
            _updatingLayout = true;
            try
            {
                var dpi = DeviceDpi / 96f; var compact = ClientSize.Height < 520 * dpi;
                _toolbar.ColumnStyles[2].Width = compact ? 96 * dpi : 0;
                _previewToggle.Visible = compact; _previewToggle.Text = _compactPreview ? "明细" : "预览";
                _tips.SetToolTip(_previewToggle, _compactPreview ? "显示对象明细" : "显示图形预览");
                _previewHost.Visible = !compact || _compactPreview; _grid.Visible = !compact || !_compactPreview;
                var fixedHeight = new[] { 0, 1, 2, 5, 6 }.Sum(i => _layout.RowStyles[i].Height) + _layout.Padding.Vertical;
                _layout.RowStyles[3].Height = compact ? (_compactPreview ? Math.Max(0, _layout.Height - fixedHeight) : 0)
                    : Math.Max(128 * dpi, Math.Min(210 * dpi, _layout.Height - 330 * dpi));
            }
            finally { _updatingLayout = false; }
        }

        private Button ButtonFor(string text, PublisherForm.UiIcon icon, Action action, bool accent = false)
        {
            var button = CadDialogTheme.Button(text, action, accent); button.AutoSize = false; button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 3, 6, 3); button.MinimumSize = Size.Empty;
            var image = PublisherForm.DrawUiIcon(icon, accent ? Color.White : CadDialogTheme.Muted); _images.Add(image); button.Image = image; return button;
        }

        private void ResetOptions()
        {
            _refreshing = true;
            try { _angle.Value = 1; _horizontal.Checked = true; _vertical.Checked = true; _flatten.Checked = true; }
            finally { _refreshing = false; }
            RefreshPreview();
        }

        private void SelectObjects()
        {
            try
            {
                using (_document.Editor.StartUserInteraction(this))
                {
                    var result = _document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n选择需要校正的直线或多段线：" });
                    if (result.Status == PromptStatus.OK) SelectedIds = result.Value.GetObjectIds();
                }
                RefreshPreview();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void RefreshPreview()
        {
            if (_refreshing) return;
            var plans = LineCorrectionService.Analyze(_document.Database, SelectedIds, Options);
            _angle.Enabled = _horizontal.Checked || _vertical.Checked; _grid.DataSource = plans; _preview.Plans = plans; _preview.Invalidate();
            var changed = plans.Count(p => p.Result != null && p.Result.Changed);
            var attention = plans.Count(p => p.Result == null || !string.IsNullOrEmpty(p.Result.Warning));
            _selection.Text = "已选 " + plans.Count + " 个对象";
            _status.Text = "待修改 " + changed + " 个    校正 " + plans.Sum(p => p.Segments) + " 段    Z 归零 " + plans.Sum(p => p.VerticesZ)
                + " 个顶点" + (attention > 0 ? "    注意 " + attention + " 项" : "");
            _status.ForeColor = attention > 0 ? LineCorrectionPreview.WarningColor : CadDialogTheme.Muted; _apply.Enabled = changed > 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tips.Dispose(); foreach (var image in _images) image.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class LineCorrectionPreview : Control
    {
        internal static readonly Color ResultColor = Color.FromArgb(75, 214, 187);
        internal static readonly Color WarningColor = Color.FromArgb(239, 190, 111);
        internal IList<LineCorrectionPlan> Plans = new List<LineCorrectionPlan>();
        internal LineCorrectionPlan Highlight;
        internal int Projection;
        internal LineCorrectionPreview()
        { BackColor = Color.FromArgb(17, 29, 38); ForeColor = CadDialogTheme.Muted; DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var dpi = DeviceDpi / 96f;
            using (var before = new Pen(WarningColor, dpi) { DashStyle = DashStyle.Dash })
            using (var after = new Pen(ResultColor, 2 * dpi))
            {
                g.DrawLine(before, 12 * dpi, 13 * dpi, 32 * dpi, 13 * dpi);
                TextRenderer.DrawText(g, "原线", Font, new Point((int)(38 * dpi), (int)(4 * dpi)), ForeColor);
                g.DrawLine(after, 100 * dpi, 13 * dpi, 120 * dpi, 13 * dpi);
                TextRenderer.DrawText(g, "校正后", Font, new Point((int)(126 * dpi), (int)(4 * dpi)), ForeColor);
                var plans = Plans.Where(p => !p.HasArcs && p.Original != null && p.Result != null).ToList();
                if (plans.Count == 0)
                {
                    TextRenderer.DrawText(g, Plans.Count == 0 ? "未选择对象" : "当前对象无直线预览", Font, ClientRectangle, ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return;
                }
                Func<CorrectionPoint, double> x = p => Projection == 2 ? p.Y : p.X;
                Func<CorrectionPoint, double> y = p => Projection == 0 ? p.Y : p.Z;
                var points = plans.SelectMany(p => p.Original.Concat(p.Result.Points)).ToList();
                var minX = points.Min(x); var maxX = points.Max(x); var minY = points.Min(y); var maxY = points.Max(y);
                var rangeX = Math.Max(1e-6, maxX - minX); var rangeY = Math.Max(1e-6, maxY - minY);
                var available = new RectangleF(30 * dpi, 30 * dpi, Math.Max(1, Width - 60 * dpi), Math.Max(1, Height - 43 * dpi));
                var scale = Math.Min(available.Width / rangeX, available.Height / rangeY);
                if (double.IsInfinity(rangeX) || double.IsInfinity(rangeY) || double.IsNaN(scale)) return;
                Func<CorrectionPoint, PointF> map = p => new PointF((float)(available.Left + available.Width / 2 + (x(p) - minX - rangeX / 2) * scale),
                    (float)(available.Top + available.Height / 2 - (y(p) - minY - rangeY / 2) * scale));
                foreach (var plan in plans.OrderBy(p => ReferenceEquals(p, Highlight) ? 1 : 0))
                {
                    var selected = ReferenceEquals(plan, Highlight); after.Color = selected ? Color.White : ResultColor; after.Width = (selected ? 2.5f : 1.5f) * dpi;
                    DrawPath(g, before, plan.Original, plan.Closed, map); DrawPath(g, after, plan.Result.Points, plan.Closed, map);
                    if (plan.Original.Count == 2)
                    {
                        var a = plan.Original[0]; var b = plan.Original[1]; var center = map(new CorrectionPoint(a.X / 2 + b.X / 2, a.Y / 2 + b.Y / 2, a.Z / 2 + b.Z / 2));
                        using (var fill = new SolidBrush(BackColor)) g.FillEllipse(fill, center.X - 3 * dpi, center.Y - 3 * dpi, 6 * dpi, 6 * dpi);
                        g.DrawEllipse(after, center.X - 3 * dpi, center.Y - 3 * dpi, 6 * dpi, 6 * dpi);
                    }
                }
            }
        }

        private static void DrawPath(Graphics graphics, Pen pen, IList<CorrectionPoint> points, bool closed, Func<CorrectionPoint, PointF> map)
        {
            for (var i = 1; i < points.Count; i++) graphics.DrawLine(pen, map(points[i - 1]), map(points[i]));
            if (closed && points.Count > 2) graphics.DrawLine(pen, map(points[points.Count - 1]), map(points[0]));
        }
    }
}
