using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace BatchPdfPublisher.Views
{
    /// <summary>Creates frame blocks and inserts previously registered frames.</summary>
    public sealed class FrameCreationForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly Action _refresh;
        private readonly List<FrameDefinition> _registeredFrames;
        private readonly List<Image> _icons = new List<Image>();
        private readonly ToolTip _toolTip = new ToolTip();

        private readonly PublisherForm.ThemedComboBox _paper = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _extension = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _orientation = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _property = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _font = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _height = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _widthFactor = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _insertScale = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _registeredSelection = new PublisherForm.ThemedComboBox();
        private readonly TextBox _remark = new TextBox();
        private readonly CadToggleSwitch _register = new CadToggleSwitch();
        private readonly CadRoundedButton _colorButton;
        private readonly FrameListBox _frameList = new FrameListBox();
        private readonly Label _frameCount = new Label();
        private readonly Label _emptyFrames = new Label();
        private readonly Label _frameNote = new Label();
        private readonly Label _framePaper = new Label();
        private readonly Label _frameOrientation = new Label();
        private readonly Label _frameRange = new Label();
        private readonly Label _previewDimensions = new Label();
        private readonly Label _previewSummary = new Label();
        private readonly Label _windowCaption = new Label();
        private readonly Label _footerHint = new Label();
        private readonly CadRoundedButton _newTabButton;
        private readonly CadRoundedButton _registeredTabButton;
        private readonly CadRoundedButton _insertRegisteredButton;
        private readonly CadRoundedButton _rangeButton;
        private readonly FramePaperPreview _paperPreview;
        private readonly CadCardPanel _newPage;
        private readonly CadCardPanel _registeredPage;
        private readonly Panel _pageHost;

        private bool _loading;
        private bool _synchronizingFrameSelection;

        public FrameCreationForm(Document document, Action refresh)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _refresh = refresh;
            _registeredFrames = new PublishPlanStore().LoadFrames()
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)).ToList();

            Text = "创建 / 插入图框";
            Width = 960;
            Height = 640;
            MinimumSize = new Size(940, 600);
            MaximumSize = new Size(1280, 900);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 11F);
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(7);
            BackColor = CadDialogTheme.Canvas;

            _colorButton = CadDialogTheme.Button(string.Empty, ChooseColor);
            _colorButton.AutoSize = false;
            _colorButton.Size = new Size(CadDialogTheme.ControlHeight, CadDialogTheme.ControlHeight);
            _colorButton.MinimumSize = _colorButton.Size;
            _colorButton.MaximumSize = _colorButton.Size;
            _colorButton.Margin = Padding.Empty;
            _colorButton.PreserveBackColorOnInteraction = true;

            _paperPreview = new FramePaperPreview(GetPaperSize);
            _newPage = CadDialogTheme.Card();
            _registeredPage = CadDialogTheme.Card();
            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Canvas,
                Margin = new Padding(6, 0, 6, 4) };
            _newTabButton = CreateTabButton("新建图框", PublisherForm.UiIcon.Document, false);
            _registeredTabButton = CreateTabButton("已登记图框", PublisherForm.UiIcon.Document, false);
            _insertRegisteredButton = ActionButton("插入登记图框", PublisherForm.UiIcon.Down, InsertRegisteredFrame, true);
            _rangeButton = ActionButton("写入排版范围", PublisherForm.UiIcon.Select, WriteLayoutRange);

            _documentBinding = new ModelessDocumentBinding(this, document);
            Build();
            LoadSettings();
            ReloadRegisteredFrames(_preferredFrameId);
            SetPage(false);
            _windowCaption.Text = Text;
            TextChanged += (sender, args) => _windowCaption.Text = Text;
            FormClosed += (sender, args) => SaveSettings();
        }

        private void Build()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = CadDialogTheme.Canvas,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);

            root.Controls.Add(BuildTitleBar(), 0, 0);
            root.Controls.Add(BuildHeading(), 0, 1);
            root.Controls.Add(BuildTaskTabs(), 0, 2);

            BuildNewPage();
            BuildRegisteredPage();
            _pageHost.Controls.Add(_newPage);
            _pageHost.Controls.Add(_registeredPage);
            root.Controls.Add(_pageHost, 0, 3);
            root.Controls.Add(BuildFooter(), 0, 4);

            _paper.SelectedIndexChanged += (sender, args) =>
            {
                if (_loading) return;
                RefreshExtensionChoices();
                UpdateOrientation();
                UpdatePreview();
            };
            _extension.SelectedIndexChanged += (sender, args) => UpdatePreview();
            _orientation.SelectedIndexChanged += (sender, args) => UpdatePreview();
            _frameList.SelectedIndexChanged += (sender, args) => UpdateSelectedFrame();
            _registeredSelection.SelectedIndexChanged += (sender, args) =>
            {
                if (_synchronizingFrameSelection) return;
                _frameList.SelectedIndex = _registeredSelection.SelectedIndex;
            };
            _newTabButton.Click += (sender, args) => SetPage(false);
            _registeredTabButton.Click += (sender, args) => SetPage(true);
            _toolTip.SetToolTip(_colorButton, "选择 AutoCAD 文字颜色");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var border = new Pen(CadDialogTheme.Border))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        private Control BuildTitleBar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                BackColor = Color.FromArgb(27, 30, 40),
                Margin = Padding.Empty
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));

            var mark = CadBrandIcon.CreateTitleMark();
            _windowCaption.Dock = DockStyle.Fill;
            _windowCaption.ForeColor = CadDialogTheme.Text;
            _windowCaption.TextAlign = ContentAlignment.MiddleLeft;
            MouseEventHandler drag = (sender, args) =>
            {
                if (args.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag;
            mark.MouseDown += drag;
            _windowCaption.MouseDown += drag;
            bar.Controls.Add(mark, 0, 0);
            bar.Controls.Add(_windowCaption, 1, 0);
            bar.Controls.Add(ChromeButton("−", () => WindowState = FormWindowState.Minimized), 2, 0);

            var maximize = ChromeButton("□", ToggleMaximize);
            SizeChanged += (sender, args) => maximize.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
            bar.Controls.Add(maximize, 3, 0);
            bar.Controls.Add(ChromeButton("×", Close, true), 4, 0);
            return bar;
        }

        private Control BuildHeading()
        {
            var heading = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                BackColor = CadDialogTheme.Surface,
                Padding = new Padding(20, 3, 16, 3),
                Margin = Padding.Empty
            };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.Controls.Add(new Label
            {
                Text = "创建与插入图框",
                Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Text,
                Font = new Font(Font.FontFamily, 17F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            heading.Controls.Add(new Label
            {
                Text = "当前图纸：" + DrawingName(),
                Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            }, 1, 0);
            return heading;
        }

        private Control BuildTaskTabs()
        {
            var tabs = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = CadDialogTheme.Canvas,
                Padding = new Padding(6, 8, 6, 8),
                Margin = Padding.Empty
            };
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _newTabButton.ConnectedRight = true;
            _registeredTabButton.ConnectedLeft = true;
            StyleTabButton(_newTabButton, true);
            StyleTabButton(_registeredTabButton, false);
            tabs.Controls.Add(_newTabButton, 0, 0);
            tabs.Controls.Add(_registeredTabButton, 1, 0);
            return tabs;
        }

        private void BuildNewPage()
        {
            _newPage.Padding = new Padding(10, 6, 10, 10);
            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var columns = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = CadDialogTheme.Surface,
                Margin = Padding.Empty
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));

            columns.Controls.Add(new CadScrollHost(BuildNewFrameSettings())
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            }, 0, 0);
            columns.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Border, Margin = Padding.Empty }, 1, 0);
            columns.Controls.Add(BuildPaperPreviewPane(), 2, 0);
            page.Controls.Add(columns, 0, 0);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0, 8, 0, 8) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            actions.Controls.Add(ActionButton("创建图框块", PublisherForm.UiIcon.Cube, () => RunCad(CreateBlock), true), 0, 0);
            actions.Controls.Add(ActionButton("插入属性文字", PublisherForm.UiIcon.Text, () => RunCad(InsertProperty)), 2, 0);
            actions.Controls.Add(ActionButton("插入纸张边框矩形", PublisherForm.UiIcon.Frame, () => RunCad(InsertBorder)), 4, 0);
            page.Controls.Add(actions, 0, 1);
            _newPage.Controls.Add(page);
        }

        private Control BuildNewFrameSettings()
        {
            var form = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 8,
                BackColor = CadDialogTheme.Surface,
                Margin = Padding.Empty
            };
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            form.Controls.Add(TwoFieldRow(
                InlineFieldCell("纸张规格", ConfigureCombo(_paper, new[] { "A0", "A1", "A2", "A3", "A4" }, ComboBoxStyle.DropDownList)),
                InlineFieldCell("加长", ConfigureCombo(_extension, new[] { "无加长" }, ComboBoxStyle.DropDownList), 52)), 0, 0);
            form.Controls.Add(TwoFieldRow(
                InlineFieldCell("图纸方向", ConfigureCombo(_orientation, new[] { "横向", "纵向" }, ComboBoxStyle.DropDownList)),
                InlineFieldCell("备注", ConfigureInput(_remark, "自建图框"), 52)), 0, 1);

            _register.Text = "创建后登记为图框";
            _register.Dock = DockStyle.Fill;
            _register.Checked = true;
            _register.Margin = new Padding(0, 0, 8, 0);
            form.Controls.Add(_register, 0, 2);
            form.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Border, Margin = new Padding(0, 6, 8, 7) }, 0, 3);
            form.Controls.Add(SectionHeading("属性文字", PublisherForm.UiIcon.List), 0, 4);

            _property.DropDownStyle = ComboBoxStyle.DropDown;
            form.Controls.Add(InlineFieldCell("属性项",
                ConfigureCombo(_property, new[] { "工程名称", "子项目名称", "图纸名称", "设计编号", "设计阶段", "图号", "序号", "纸张", "比例", "日期", "版本" }, ComboBoxStyle.DropDown)), 0, 5);

            _font.Items.AddRange(FrameCreationService.GetTextStyleNames(_document).Cast<object>().ToArray());
            _font.DropDownStyle = ComboBoxStyle.DropDownList;
            var preferredFont = DraftingStandardService.GetTextStyleName(DraftingStandardProfile.BodyTextKey);
            if (string.IsNullOrWhiteSpace(preferredFont) || !_font.Items.Values.Any(item => string.Equals(Convert.ToString(item), preferredFont, StringComparison.OrdinalIgnoreCase)))
                preferredFont = _font.Items.Count > 0 ? Convert.ToString(_font.Items.Values.First()) : string.Empty;
            Select(_font, preferredFont);

            _height.DropDownStyle = ComboBoxStyle.DropDown;
            _height.Items.AddRange(new object[] { "1.5", "2.5", "3.5", "5", "7", "10", "14", "20" });
            _widthFactor.DropDownStyle = ComboBoxStyle.DropDownList;
            _widthFactor.Items.AddRange(new object[] { "0.5", "0.7", "1" });

            var textOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            var optionCells = new[]
            {
                FieldCell("文字样式", _font),
                FieldCell("文字高度", _height),
                FieldCell("宽度因子", _widthFactor),
                ColorCell()
            };
            bool? stackedOptions = null;
            var optionDpi = 0;
            Action arrangeOptions = () =>
            {
                var stacked = textOptions.ClientSize.Width > 0 && textOptions.ClientSize.Width < 464 * DeviceDpi / 96F;
                if (stackedOptions == stacked && optionDpi == DeviceDpi) return;
                stackedOptions = stacked;
                optionDpi = DeviceDpi;
                ArrangeTextOptions(textOptions, form, optionCells, stacked);
            };
            textOptions.SizeChanged += (sender, args) => arrangeOptions();
            form.Controls.Add(textOptions, 0, 6);
            arrangeOptions();
            return form;
        }

        private void ArrangeTextOptions(TableLayoutPanel options, TableLayoutPanel form, Control[] cells, bool stacked)
        {
            var scale = DeviceDpi / 96F;
            options.SuspendLayout();
            form.SuspendLayout();
            try
            {
                options.Controls.Clear();
                options.ColumnStyles.Clear();
                options.RowStyles.Clear();
                options.ColumnCount = stacked ? 3 : 7;
                options.RowCount = stacked ? 2 : 1;
                form.RowStyles[6].Height = (stacked ? 124 : 62) * scale;
                if (stacked)
                {
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                    options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                    options.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                    options.Controls.Add(cells[0], 0, 0);
                    options.Controls.Add(cells[1], 2, 0);
                    options.Controls.Add(cells[2], 0, 1);
                    options.Controls.Add(cells[3], 2, 1);
                }
                else
                {
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6 * scale));
                    options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64 * scale));
                    options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    options.Controls.Add(cells[0], 0, 0);
                    options.Controls.Add(cells[1], 2, 0);
                    options.Controls.Add(cells[2], 4, 0);
                    options.Controls.Add(cells[3], 6, 0);
                }
            }
            finally
            {
                options.ResumeLayout(true);
                form.ResumeLayout(true);
            }
        }

        private Control BuildPaperPreviewPane()
        {
            var preview = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = CadDialogTheme.Surface,
                Margin = new Padding(12, 0, 0, 0)
            };
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preview.SizeChanged += (sender, args) =>
            {
                var height = Math.Max(160, Math.Min(260, preview.ClientSize.Height - 80));
                if (Math.Abs(preview.RowStyles[1].Height - height) > 1)
                    preview.RowStyles[1].Height = height;
            };
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(new Label { Text = "图纸预览", Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Text, Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            _previewDimensions.Dock = DockStyle.Fill;
            _previewDimensions.ForeColor = CadDialogTheme.Muted;
            _previewDimensions.TextAlign = ContentAlignment.MiddleRight;
            _previewDimensions.Margin = Padding.Empty;
            heading.Controls.Add(_previewDimensions, 1, 0);
            preview.Controls.Add(heading, 0, 0);
            _paperPreview.Dock = DockStyle.Fill;
            _paperPreview.Margin = new Padding(0, 4, 0, 4);
            preview.Controls.Add(_paperPreview, 0, 1);
            _previewSummary.Dock = DockStyle.Fill;
            _previewSummary.ForeColor = CadDialogTheme.Muted;
            _previewSummary.TextAlign = ContentAlignment.MiddleLeft;
            _previewSummary.AutoEllipsis = true;
            _previewSummary.Margin = Padding.Empty;
            preview.Controls.Add(_previewSummary, 0, 2);
            return preview;
        }

        private void BuildRegisteredPage()
        {
            _registeredPage.Padding = new Padding(10, 6, 10, 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            var listPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 0, 12, 0) };
            listPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            listPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var listHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            listHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listHeading.Controls.Add(SectionHeading("已登记图框列表", PublisherForm.UiIcon.Frame), 0, 0);
            _frameCount.Dock = DockStyle.Fill;
            _frameCount.ForeColor = CadDialogTheme.Muted;
            _frameCount.TextAlign = ContentAlignment.MiddleRight;
            listHeading.Controls.Add(_frameCount, 1, 0);
            listPane.Controls.Add(listHeading, 0, 0);

            CadDialogTheme.StyleList(_frameList);
            _frameList.Font = new Font(Font.FontFamily, 11F);
            _frameList.ItemHeight = 74;
            var listArea = new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            listArea.Controls.Add(new CadListHost(_frameList) { Padding = new Padding(5) });
            _emptyFrames.Text = "当前项目还没有登记图框。\r\n切换到“新建图框”创建并登记。";
            _emptyFrames.Dock = DockStyle.Fill;
            _emptyFrames.ForeColor = CadDialogTheme.Muted;
            _emptyFrames.TextAlign = ContentAlignment.MiddleCenter;
            _emptyFrames.Visible = false;
            listArea.Controls.Add(_emptyFrames);
            listPane.Controls.Add(listArea, 0, 1);
            layout.Controls.Add(listPane, 0, 0);
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Border, Margin = Padding.Empty }, 1, 0);
            layout.Controls.Add(BuildRegisteredDetails(), 2, 0);
            _registeredPage.Controls.Add(layout);
        }

        private Control BuildRegisteredDetails()
        {
            var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(12, 0, 0, 0) };
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            details.Controls.Add(SectionHeading("图框信息", PublisherForm.UiIcon.List), 0, 0);

            _registeredSelection.DropDownStyle = ComboBoxStyle.DropDownList;
            _registeredSelection.DisplayMember = nameof(FrameDefinition.DisplayName);
            details.Controls.Add(InlineFieldCell("选择图框", _registeredSelection, 128), 0, 1);
            details.Controls.Add(InlineFieldCell("插入比例",
                ConfigureCombo(_insertScale, new[] { "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:50", "1:100", "1:150", "1:200" }, ComboBoxStyle.DropDown), 128), 0, 2);
            details.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Border,
                Margin = new Padding(0, 5, 0, 6) }, 0, 3);

            var info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 4; row++) info.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            AddInfoLine(info, 0, "纸张规格", _framePaper);
            AddInfoLine(info, 1, "方向", _frameOrientation);
            AddInfoLine(info, 2, "排版范围", _frameRange);
            AddInfoLine(info, 3, "备注", _frameNote);
            details.Controls.Add(info, 0, 4);

            var buttonRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0, 8, 0, 8) };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttonRow.Controls.Add(_insertRegisteredButton, 0, 0);
            buttonRow.Controls.Add(_rangeButton, 2, 0);
            details.Controls.Add(buttonRow, 0, 6);
            return details;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(12, 6, 12, 6), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
            _footerHint.Dock = DockStyle.Fill;
            _footerHint.ForeColor = CadDialogTheme.Muted;
            _footerHint.TextAlign = ContentAlignment.MiddleLeft;
            _footerHint.AutoEllipsis = true;
            footer.Controls.Add(_footerHint, 0, 0);
            var closeButton = ActionButton("关闭", PublisherForm.UiIcon.Remove, Close);
            closeButton.Margin = new Padding(3, 0, 3, 0);
            footer.Controls.Add(closeButton, 1, 0);
            return footer;
        }

        private Control SectionHeading(string text, PublisherForm.UiIcon icon)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var image = MakeIcon(icon, CadDialogTheme.Accent);
            row.Controls.Add(new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.CenterImage,
                Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
            row.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Text,
                Font = new Font(Font.FontFamily, 11.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty }, 1, 0);
            return row;
        }

        private Control FieldCell(string label, Control control)
        {
            var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight));
            cell.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty }, 0, 0);
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            cell.Controls.Add(control, 0, 1);
            return cell;
        }

        private Control InlineFieldCell(string label, Control control, int labelWidth = 88)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(0, 6, 8, 6), Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true, Margin = Padding.Empty }, 0, 0);
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            row.Controls.Add(control, 1, 0);
            return row;
        }

        private Control TwoFieldRow(Control first, Control second)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(first, 0, 0);
            row.Controls.Add(second, 2, 0);
            return row;
        }

        private Control ColorCell()
        {
            var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 0, 0, 0) };
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            cell.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight));
            cell.Controls.Add(new Label { Text = "文字颜色", Dock = DockStyle.Fill, ForeColor = CadDialogTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty }, 0, 0);
            var swatchHost = new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Surface,
                Margin = Padding.Empty };
            _colorButton.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            swatchHost.SizeChanged += (sender, args) =>
                _colorButton.Top = Math.Max(0, (swatchHost.ClientSize.Height - _colorButton.Height) / 2);
            swatchHost.Controls.Add(_colorButton);
            cell.Controls.Add(swatchHost, 0, 1);
            return cell;
        }

        private static void AddInfoLine(TableLayoutPanel table, int row, string label, Label value)
        {
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty }, 0, row);
            value.Dock = DockStyle.Fill;
            value.ForeColor = CadDialogTheme.Text;
            value.TextAlign = ContentAlignment.MiddleLeft;
            value.AutoEllipsis = true;
            value.Margin = Padding.Empty;
            table.Controls.Add(value, 1, row);
        }

        private PublisherForm.ThemedComboBox ConfigureCombo(PublisherForm.ThemedComboBox combo,
            IEnumerable<string> values, ComboBoxStyle style)
        {
            combo.DropDownStyle = style;
            combo.Items.Clear();
            combo.Items.AddRange(values.Where(value => value != null).Cast<object>().ToArray());
            combo.Margin = Padding.Empty;
            combo.Dock = DockStyle.Fill;
            return combo;
        }

        private Control ConfigureInput(TextBox input, string initial)
        {
            input.Text = initial;
            return CadDialogTheme.Input(input);
        }

        private CadRoundedButton CreateTabButton(string text, PublisherForm.UiIcon icon, bool active)
        {
            var button = CadDialogTheme.Button(text, null, active);
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Height = CadDialogTheme.ControlHeight;
            button.MinimumSize = new Size(0, CadDialogTheme.ControlHeight);
            button.MaximumSize = new Size(0, CadDialogTheme.ControlHeight);
            button.Margin = Padding.Empty;
            button.Image = MakeIcon(icon, active ? Color.White : CadDialogTheme.Muted);
            return button;
        }

        private void StyleTabButton(CadRoundedButton button, bool active)
        {
            button.BackColor = active ? CadDialogTheme.Accent : CadDialogTheme.Raised;
            button.ForeColor = active ? Color.White : CadDialogTheme.Muted;
            button.BorderColor = active ? CadDialogTheme.Accent : CadDialogTheme.Border;
            button.PreserveBackColorOnInteraction = active;
            button.Invalidate();
        }

        private CadRoundedButton ActionButton(string text, PublisherForm.UiIcon icon, Action action, bool primary = false)
        {
            var button = CadDialogTheme.Button(text, action, primary);
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.MinimumSize = new Size(0, CadDialogTheme.ControlHeight);
            button.MaximumSize = new Size(0, CadDialogTheme.ControlHeight);
            button.Height = CadDialogTheme.ControlHeight;
            button.Margin = Padding.Empty;
            button.Image = MakeIcon(icon, primary ? Color.White : CadDialogTheme.Muted);
            return button;
        }

        private static Button ChromeButton(string text, Action action, bool close = false)
        {
            var button = new CadChromeButton { Text = text, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, BackColor = Color.FromArgb(27, 30, 40),
                Margin = Padding.Empty, TabStop = false, IsCloseButton = close };
            button.Click += (sender, args) => action();
            return button;
        }

        private Image MakeIcon(PublisherForm.UiIcon icon, Color color)
        {
            var image = PublisherForm.DrawUiIcon(icon, color);
            _icons.Add(image);
            return image;
        }

        private void SetPage(bool registered)
        {
            _pageHost.SuspendLayout();
            try
            {
                _newPage.Visible = !registered;
                _registeredPage.Visible = registered;
                if (registered) _registeredPage.BringToFront();
                else _newPage.BringToFront();
                StyleTabButton(_newTabButton, !registered);
                StyleTabButton(_registeredTabButton, registered);
                ReplaceTabIcon(_newTabButton, PublisherForm.UiIcon.Document, !registered);
                ReplaceTabIcon(_registeredTabButton, PublisherForm.UiIcon.Document, registered);
                _footerHint.Text = registered
                    ? "提示：选择已登记图框并设置比例，插入到当前图纸中。"
                    : "提示：根据设置创建图框块，可选择插入属性文字或纸张边框。";
            }
            finally { _pageHost.ResumeLayout(true); }
        }

        private void ReplaceTabIcon(CadRoundedButton button, PublisherForm.UiIcon icon, bool active)
        {
            var previous = button.Image;
            button.Image = MakeIcon(icon, active ? Color.White : CadDialogTheme.Muted);
            if (previous != null)
            {
                _icons.Remove(previous);
                previous.Dispose();
            }
            button.Invalidate();
        }

        private void UpdateOrientation()
        {
            if (_paper.Items.Count == 0) return;
            _orientation.Text = PaperSizeCatalog.DefaultOrientation(_paper.Text);
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            _previewDimensions.Text = PaperDimensionsText();
            var paper = _paper.Text + (ExtensionValue().Length == 0 ? string.Empty : "+" + ExtensionValue());
            _previewSummary.Text = paper + "  |  " + _orientation.Text + "  |  " + PaperDimensionsText();
            _paperPreview.Invalidate();
        }

        private SizeF GetPaperSize()
        {
            if (string.IsNullOrWhiteSpace(_paper.Text)) return new SizeF(841, 594);
            try
            {
                var size = PaperSizeCatalog.GetSize(_paper.Text, ExtensionValue(), _orientation.Text);
                if (size != null && size.Length >= 2 && size[0] > 0 && size[1] > 0)
                    return new SizeF((float)size[0], (float)size[1]);
            }
            catch { }
            return new SizeF(841, 594);
        }

        private string PaperDimensionsText()
        {
            var size = GetPaperSize();
            return Math.Round(size.Width) + " × " + Math.Round(size.Height) + " mm";
        }

        private string ExtensionValue() => string.Equals(_extension.Text, "无加长", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : _extension.Text;

        private void RefreshExtensionChoices()
        {
            if (_extension == null || _paper == null || string.IsNullOrWhiteSpace(_paper.Text)) return;
            var previous = _extension.Text;
            _extension.Items.Clear();
            _extension.Items.Add("无加长");
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(_paper.Text)
                .Where(value => !string.IsNullOrWhiteSpace(value))) _extension.Items.Add(extension);
            Select(_extension, string.IsNullOrWhiteSpace(previous) ? "无加长" : previous);
            if (_extension.SelectedIndex < 0) _extension.SelectedIndex = 0;
        }

        private void RunCad(Action action)
        {
            if (action == null || IsDisposed) return;
            SaveSettings();
            Hide();
#if ACAD_R19
            CadCommandContext.Execute(() => ExecuteCadAction(action));
#else
            ExecuteCadActionAsync(action);
#endif
        }

#if !ACAD_R19
        private async void ExecuteCadActionAsync(Action action)
        {
            try { await CadCommandContext.ExecuteAsync(() => ExecuteCadAction(action)); }
            catch (Exception exception)
            {
                ShowCadError(exception);
            }
        }
#endif

        private void ExecuteCadAction(Action action)
        {
            try { action(); }
            catch (Exception exception) { ShowCadError(exception); }
            finally
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try { BeginInvoke(new Action(() => { if (!IsDisposed) Show(); })); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private void ShowCadError(Exception exception)
        {
            try
            {
                File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "frame-creation.log"),
                    DateTime.Now.ToString("O") + " " + exception + Environment.NewLine);
            }
            catch { }
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) MessageBox.Show(this, exception.Message, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void InsertBorder()
        {
            var size = PaperSizeCatalog.GetSize(_paper.Text, ExtensionValue(), _orientation.Text);
            if (FrameCreationService.InsertBorder(_document, size[0], size[1])) _refresh?.Invoke();
        }

        private void InsertProperty()
        {
            var selectedColor = _colorButton.Tag as AcColor;
            if (!double.TryParse(_height.Text, out var height) || height <= 0)
                throw new InvalidOperationException("请输入大于 0 的文字高度。");
            if (!double.TryParse(_widthFactor.Text, out var widthFactor) || widthFactor <= 0)
                throw new InvalidOperationException("请输入大于 0 的文字宽度因子。");
            if (FrameCreationService.InsertCenteredProperty(_document, _property.Text, _font.Text, height, widthFactor, selectedColor))
                _refresh?.Invoke();
        }

        private void InsertRegisteredFrame()
        {
            var frame = _frameList.SelectedItem as FrameDefinition;
            if (frame == null) throw new InvalidOperationException("请先选择已登记的图框。");
            var scale = ParseScale(_insertScale.Text);
            if (FrameCreationService.InsertRegisteredFrame(_document, frame, scale)) _refresh?.Invoke();
        }

        private static int ParseScale(string value)
        {
            var scaleText = (value ?? string.Empty).Trim().Replace('：', ':');
            var separator = scaleText.LastIndexOf(':');
            if (separator >= 0) scaleText = scaleText.Substring(separator + 1).Trim();
            if (!int.TryParse(scaleText, out var scale) || scale <= 0)
                throw new InvalidOperationException("请输入有效比例，例如 1:50。");
            return scale;
        }

        private void WriteLayoutRange()
        {
            var frame = _frameList.SelectedItem as FrameDefinition;
            if (frame == null) throw new InvalidOperationException("请先选择已登记的图框。");
            if (!FrameLayoutRangeService.PromptAndSaveRange(_document, frame)) return;
            ReloadRegisteredFrames(frame.RegistrationId);
            _refresh?.Invoke();
            BeginInvoke(new Action(() => MessageBox.Show(this,
                "图框排版范围已重新登记成功。\r\n" + FrameLayoutRangeService.Describe(frame),
                "创建 / 插入图框", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        }

        private void CreateBlock()
        {
            if (!double.TryParse(_height.Text, out var textHeight) || textHeight <= 0)
                throw new InvalidOperationException("请输入大于 0 的文字高度。");
            var paper = _paper.Text;
            var extension = ExtensionValue();
            var size = PaperSizeCatalog.GetSize(paper, extension, _orientation.Text);
            var remark = string.IsNullOrWhiteSpace(_remark.Text) ? "自建图框" : _remark.Text.Trim();
            var blockName = FrameCreationService.CreateFrameBlockFromSelection(_document, string.Empty,
                size[0], size[1], _font.Text, textHeight, remark, out var error, out var detectedPaper,
                out var detectedExtension, out var detectedOrientation, out var createdReferenceId);
            if (string.IsNullOrWhiteSpace(blockName))
            {
                if (!string.IsNullOrWhiteSpace(error)) MessageBox.Show(this, error, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var registered = false;
            if (_register.Checked)
            {
                registered = new FrameRegistrationService().RegisterCreated(_document, createdReferenceId, remark, out var registrationError);
                if (!registered)
                    MessageBox.Show(this, "图框块已创建，但自动登记失败：\r\n" + registrationError,
                        "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else ReloadRegisteredFrames(blockName);
            }
            _refresh?.Invoke();
            if (!_register.Checked || registered)
                MessageBox.Show(this, registered ? "图框块已创建并登记：\r\n" + blockName : "图框块已创建：\r\n" + blockName,
                    "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ChooseColor()
        {
            var dialog = new AcColorDialog();
            dialog.Color = _colorButton.Tag as AcColor
                ?? AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            var color = dialog.Color;
            _colorButton.Tag = color;
            _colorButton.BackColor = DisplayColor(color);
            _colorButton.BorderColor = CadDialogTheme.Border;
            _colorButton.Invalidate();
        }

        private void ReloadRegisteredFrames(string selectedIdOrName)
        {
            var previous = _frameList.SelectedItem as FrameDefinition;
            var preferred = string.IsNullOrWhiteSpace(selectedIdOrName)
                ? previous?.RegistrationId : selectedIdOrName;
            _registeredFrames.Clear();
            _registeredFrames.AddRange(new PublishPlanStore().LoadFrames()
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)));

            _synchronizingFrameSelection = true;
            _frameList.BeginUpdate();
            try
            {
                _frameList.Items.Clear();
                foreach (var frame in _registeredFrames) _frameList.Items.Add(frame);
                _registeredSelection.Items.Clear();
                _registeredSelection.Items.AddRange(_registeredFrames.Cast<object>().ToArray());
                var index = _registeredFrames.FindIndex(frame =>
                    (!string.IsNullOrWhiteSpace(preferred) && string.Equals(frame.RegistrationId, preferred, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(selectedIdOrName) && string.Equals(frame.BlockName, selectedIdOrName, StringComparison.OrdinalIgnoreCase)));
                if (index < 0 && previous != null)
                    index = _registeredFrames.FindIndex(frame => string.Equals(frame.BlockName, previous.BlockName, StringComparison.OrdinalIgnoreCase));
                _frameList.SelectedIndex = index >= 0 ? index : (_registeredFrames.Count > 0 ? 0 : -1);
                _registeredSelection.SelectedIndex = _frameList.SelectedIndex;
            }
            finally { _frameList.EndUpdate(); _synchronizingFrameSelection = false; }
            _frameCount.Text = _registeredFrames.Count + " 项";
            _emptyFrames.Visible = _registeredFrames.Count == 0;
            UpdateSelectedFrame();
        }

        private void UpdateSelectedFrame()
        {
            var frame = _frameList.SelectedItem as FrameDefinition;
            if (!_synchronizingFrameSelection && _registeredSelection.SelectedIndex != _frameList.SelectedIndex)
            {
                _synchronizingFrameSelection = true;
                try { _registeredSelection.SelectedIndex = _frameList.SelectedIndex; }
                finally { _synchronizingFrameSelection = false; }
            }
            _frameNote.Text = frame == null || string.IsNullOrWhiteSpace(frame.Note) ? "—" : frame.Note;
            _framePaper.Text = frame == null ? "—" : frame.PaperDisplay ?? "—";
            _frameOrientation.Text = frame == null ? "—" : frame.PaperOrientation ?? "—";
            _frameRange.Text = frame == null ? "—" : FrameLayoutRangeService.HasValidRange(frame) ? "已登记" : "未登记";
            _toolTip.SetToolTip(_frameRange, frame == null ? string.Empty : FrameLayoutRangeService.Describe(frame));
            var enabled = frame != null;
            _insertRegisteredButton.Enabled = enabled;
            _rangeButton.Enabled = enabled;
            _frameList.Invalidate();
        }

        private void LoadSettings()
        {
            _loading = true;
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(SettingsPath))
                    foreach (var line in File.ReadAllLines(SettingsPath))
                    {
                        var split = line.IndexOf('=');
                        if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1);
                    }
                Select(_paper, values.TryGetValue("Paper", out var savedPaper) ? savedPaper : "A1");
                RefreshExtensionChoices();
                Select(_extension, values.TryGetValue("Extension", out var savedExtension) ? savedExtension : "无加长");
                UpdateOrientation();
                Select(_orientation, values.TryGetValue("Orientation", out var savedOrientation)
                    ? savedOrientation : PaperSizeCatalog.DefaultOrientation(_paper.Text));
                if (values.TryGetValue("Remark", out var remark)) _remark.Text = remark;
                Select(_property, values.TryGetValue("Property", out var property) ? property : "图纸名称");
                Select(_font, values.TryGetValue("Font", out var font) ? font : _font.Text);
                Select(_height, values.TryGetValue("Height", out var height) ? height : "3.5");
                Select(_widthFactor, values.TryGetValue("WidthFactor", out var factor) ? factor : "1");
                _register.Checked = !values.TryGetValue("Register", out var register) || register == "1";
                Select(_insertScale, values.TryGetValue("InsertScale", out var scale) ? scale : "1:100");

                var frameId = values.TryGetValue("InsertFrameId", out var id) ? id : string.Empty;
                var blockName = values.TryGetValue("InsertFrameBlock", out var block) ? block : string.Empty;
                var selected = _registeredFrames.FindIndex(frame =>
                    (!string.IsNullOrWhiteSpace(frameId) && string.Equals(frame.RegistrationId, frameId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(blockName) && string.Equals(frame.BlockName, blockName, StringComparison.OrdinalIgnoreCase)));
                _preferredFrameId = selected >= 0 ? _registeredFrames[selected].RegistrationId : null;

                var color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
                if (values.TryGetValue("ColorIndex", out var colorIndex) && short.TryParse(colorIndex, out var index))
                    color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index);
                if (values.TryGetValue("ColorMethod", out var method) && method == "ByColor"
                    && values.TryGetValue("ColorR", out var redValue) && byte.TryParse(redValue, out var red)
                    && values.TryGetValue("ColorG", out var greenValue) && byte.TryParse(greenValue, out var green)
                    && values.TryGetValue("ColorB", out var blueValue) && byte.TryParse(blueValue, out var blue))
                    color = AcColor.FromRgb(red, green, blue);
                _colorButton.Tag = color;
                _colorButton.BackColor = DisplayColor(color);
            }
            catch { }
            finally { _loading = false; }
            UpdatePreview();
        }

        private string _preferredFrameId;

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var color = _colorButton.Tag as AcColor;
                var selectedFrame = _frameList.SelectedItem as FrameDefinition;
                File.WriteAllLines(SettingsPath, new[]
                {
                    "Paper=" + _paper.Text,
                    "Extension=" + _extension.Text,
                    "Orientation=" + _orientation.Text,
                    "Remark=" + _remark.Text,
                    "Property=" + _property.Text,
                    "Font=" + _font.Text,
                    "Height=" + _height.Text,
                    "WidthFactor=" + _widthFactor.Text,
                    "Register=" + (_register.Checked ? "1" : "0"),
                    "InsertFrameId=" + (selectedFrame?.RegistrationId ?? string.Empty),
                    "InsertFrameBlock=" + (selectedFrame?.BlockName ?? string.Empty),
                    "InsertScale=" + _insertScale.Text,
                    "ColorMethod=" + (color == null ? "ByAci" : color.ColorMethod.ToString()),
                    "ColorIndex=" + (color?.ColorIndex ?? 7),
                    "ColorR=" + (color?.Red ?? 255),
                    "ColorG=" + (color?.Green ?? 255),
                    "ColorB=" + (color?.Blue ?? 255)
                });
            }
            catch { }
        }

        private static void Select(PublisherForm.ThemedComboBox combo, string value)
        {
            var text = value ?? string.Empty;
            for (var index = 0; index < combo.Items.Count; index++)
            {
                if (!string.Equals(Convert.ToString(combo.Items.Values[index]), text, StringComparison.OrdinalIgnoreCase)) continue;
                combo.SelectedIndex = index;
                return;
            }
            if (combo.DropDownStyle == ComboBoxStyle.DropDown) combo.Text = text;
            else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string SettingsPath => UserDataPaths.SettingsFile("frame-creation.settings", "BatchPdfPublisher.frame-creation.settings");

        private static Color DisplayColor(AcColor color)
        {
            if (color == null) return Color.White;
            if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor) return Color.FromArgb(color.Red, color.Green, color.Blue);
            if (color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByAci)
            {
                switch (color.ColorIndex)
                {
                    case 1: return Color.Red;
                    case 2: return Color.Yellow;
                    case 3: return Color.LimeGreen;
                    case 4: return Color.Cyan;
                    case 5: return Color.Blue;
                    case 6: return Color.Magenta;
                    case 8: return Color.DarkGray;
                    case 9: return Color.Gray;
                    default: return Color.White;
                }
            }
            return Color.White;
        }

        private string DrawingName()
        {
            try { return Path.GetFileName(_document.Name); }
            catch { return "未命名图纸"; }
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void WndProc(ref Message message)
        {
            const int wmNcHitTest = 0x0084;
            if (message.Msg == wmNcHitTest && WindowState != FormWindowState.Maximized)
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
            if (disposing)
            {
                _toolTip.Dispose();
                _documentBinding?.Dispose();
                foreach (var icon in _icons) icon.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr word, IntPtr data);

        private sealed class FrameListBox : CadPlainListBox
        {
            private readonly Image _icon = PublisherForm.DrawUiIcon(PublisherForm.UiIcon.Document, CadDialogTheme.Accent);
            private readonly Image _selectedIcon = PublisherForm.DrawUiIcon(PublisherForm.UiIcon.Document, Color.White);

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                using (var background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
                var frame = Items[e.Index] as FrameDefinition;
                if (frame == null) return;
                var selected = (e.State & DrawItemState.Selected) != 0;
                var bounds = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 2,
                    Math.Max(1, e.Bounds.Width - 6), Math.Max(1, e.Bounds.Height - 5));
                var state = e.Graphics.Save();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = CadDialogTheme.Rounded(bounds, 5))
                using (var fill = new SolidBrush(selected ? Color.FromArgb(24, 103, 179) : CadDialogTheme.Surface))
                using (var outline = new Pen(selected ? CadDialogTheme.Accent : CadDialogTheme.Border))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(outline, path);
                    if (selected)
                        using (var accent = new SolidBrush(Color.FromArgb(73, 213, 255)))
                        {
                            e.Graphics.SetClip(path, CombineMode.Intersect);
                            e.Graphics.FillRectangle(accent, bounds.Left, bounds.Top, 4, bounds.Height);
                        }
                }
                e.Graphics.Restore(state);
                var iconBounds = new Rectangle(e.Bounds.Left + 17, e.Bounds.Top + (e.Bounds.Height - 20) / 2, 20, 20);
                e.Graphics.DrawImage(selected ? _selectedIcon : _icon, iconBounds);
                var x = e.Bounds.Left + 52;
                var width = Math.Max(1, e.Bounds.Width - 63);
                TextRenderer.DrawText(e.Graphics, frame.DisplayName ?? frame.BlockName, Font,
                    new Rectangle(x, e.Bounds.Top + 12, width, 24), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                var details = (frame.PaperDisplay ?? "—") + "  |  " + (frame.PaperOrientation ?? "—");
                TextRenderer.DrawText(e.Graphics, details, Font,
                    new Rectangle(x, e.Bounds.Top + 40, width, 20), selected ? CadDialogTheme.Text : CadDialogTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _icon.Dispose(); _selectedIcon.Dispose(); }
                base.Dispose(disposing);
            }
        }

        private sealed class FramePaperPreview : Control
        {
            private readonly Func<SizeF> _paperSize;

            public FramePaperPreview(Func<SizeF> paperSize)
            {
                _paperSize = paperSize;
                BackColor = CadDialogTheme.Raised;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(CadDialogTheme.Raised);
                var size = _paperSize == null ? new SizeF(841, 594) : _paperSize();
                if (size.Width <= 0 || size.Height <= 0) return;
                var available = new RectangleF(12, 12, Math.Max(1, Width - 24), Math.Max(1, Height - 24));
                var ratio = Math.Min(available.Width / size.Width, available.Height / size.Height);
                var paperWidth = size.Width * ratio;
                var paperHeight = size.Height * ratio;
                var x = (Width - paperWidth) / 2F;
                var y = (Height - paperHeight) / 2F;
                var paper = new RectangleF(x, y, paperWidth, paperHeight);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    e.Graphics.FillRectangle(shadow, paper.Left + 5, paper.Top + 6, paper.Width, paper.Height);
                using (var fill = new SolidBrush(Color.FromArgb(250, 250, 249)))
                using (var outline = new Pen(Color.FromArgb(201, 208, 215), 1F))
                {
                    e.Graphics.FillRectangle(fill, paper);
                    e.Graphics.DrawRectangle(outline, paper.X, paper.Y, paper.Width, paper.Height);
                }

                var inset = Math.Max(8F, Math.Min(paper.Width, paper.Height) * .035F);
                var inner = RectangleF.FromLTRB(paper.Left + inset, paper.Top + inset,
                    paper.Right - inset, paper.Bottom - inset);
                using (var line = new Pen(Color.FromArgb(115, 125, 134), .8F))
                {
                    e.Graphics.DrawRectangle(line, inner.X, inner.Y, inner.Width, inner.Height);
                    var blockWidth = inner.Width * .27F;
                    var blockHeight = Math.Max(12F, inner.Height * .13F);
                    var block = new RectangleF(inner.Right - blockWidth, inner.Bottom - blockHeight, blockWidth, blockHeight);
                    e.Graphics.DrawRectangle(line, block.X, block.Y, block.Width, block.Height);
                    e.Graphics.DrawLine(line, block.Left + block.Width * .58F, block.Top, block.Left + block.Width * .58F, block.Bottom);
                    e.Graphics.DrawLine(line, block.Left, block.Top + block.Height * .52F, block.Right, block.Top + block.Height * .52F);
                }
            }
        }
    }
}
