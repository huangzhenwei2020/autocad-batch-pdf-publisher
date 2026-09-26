using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class FrameRegistrationEditorForm : DpiAwareForm
    {
        private const string DoNotRead = "（不读取）";
        private readonly IDictionary<string, string> _attributes;
        private readonly FrameDefinition _existing;
        private readonly FrameSizeGuess _guess;
        private readonly string _attributeTagSignature;
        private readonly string _definitionSignature;
        private readonly double _referenceAspectRatio;
        private readonly Func<FrameProjectScanReport> _projectScanner;
        private readonly List<Image> _icons = new List<Image>();
        private readonly TextBox _block = new TextBox();
        private readonly TextBox _note = new TextBox();
        private readonly TextBox[] _defaults = { new TextBox(), new TextBox(), new TextBox(), new TextBox() };
        private readonly PublisherForm.ThemedComboBox _paper = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _extension = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _orientation = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox[] _tags = {
            new PublisherForm.ThemedComboBox(), new PublisherForm.ThemedComboBox(),
            new PublisherForm.ThemedComboBox(), new PublisherForm.ThemedComboBox() };
        private readonly CadToggleSwitch _pickRange = new CadToggleSwitch();
        private readonly Label _rangeDescription = new Label();
        private readonly Label _scanResult = new Label();
        private FrameProjectScanReport _projectScan;
        private bool _loading;

        public FrameDefinition Definition { get; private set; }
        public List<FrameProjectScanIssue> RequestedIssues { get; } = new List<FrameProjectScanIssue>();
        public bool OpenAllRequested { get; private set; }
        public bool PickLayoutRangeRequested => _pickRange.Checked;

        public FrameRegistrationEditorForm(string blockName, FrameSizeGuess guess,
            IDictionary<string, string> attributes, FrameDefinition existing = null,
            string attributeTagSignature = null, string definitionSignature = null,
            double referenceAspectRatio = 0d, Func<FrameProjectScanReport> projectScanner = null)
        {
            _guess = guess;
            _existing = existing;
            _attributes = attributes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _attributeTagSignature = attributeTagSignature;
            _definitionSignature = definitionSignature;
            _referenceAspectRatio = referenceAspectRatio;
            _projectScanner = projectScanner;
            Text = existing == null ? "登记图框" : "修改图框登记";
            Width = 940; Height = 775;
            MinimumSize = new Size(820, 680);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            FormBorderStyle = FormBorderStyle.None;
            SizeGripStyle = SizeGripStyle.Hide;
            Padding = new Padding(1);
            Build();
            LoadValues(blockName);
        }

        private void Build()
        {
            BackColor = CadDialogTheme.Border;
            ForeColor = CadDialogTheme.Text;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            Controls.Add(root);
            root.Controls.Add(TitleBar(), 0, 0);

            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 11,
                Padding = new Padding(20, 14, 20, 14), Margin = Padding.Empty,
                BackColor = CadDialogTheme.Surface };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            for (var i = 0; i < 4; i++) content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            content.Controls.Add(Heading("图框登记信息", PublisherForm.UiIcon.Frame), 0, 0);
            content.Controls.Add(PairRow("图块名称", Input(_block, true), "用户备注", Input(_note)), 0, 1);
            content.Controls.Add(PairRow("纸张规格", Combo(_paper), "加长", Combo(_extension)), 0, 2);
            _extension.DropDownStyle = ComboBoxStyle.DropDown;
            var paperRow = PairRow("纸张方向", Combo(_orientation), "当前检测", new Label {
                Text = _guess.MeasuredSize, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = CadDialogTheme.Muted, AutoEllipsis = true });
            content.Controls.Add(paperRow, 0, 3);
            content.Controls.Add(Heading("属性映射与默认值", PublisherForm.UiIcon.List), 0, 4);
            content.Controls.Add(MapHeader(), 0, 5);
            var names = new[] { "子项目名称", "图号", "图名", "打印比例" };
            for (var i = 0; i < 4; i++) content.Controls.Add(MapRow(names[i], i), 0, i + 6);
            var checks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
                BackColor = CadDialogTheme.Surface, Margin = new Padding(0, 10, 0, 0) };
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            checks.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            checks.Controls.Add(Heading("排版范围", PublisherForm.UiIcon.Select), 0, 0);
            checks.Controls.Add(Heading("工程图框检查", PublisherForm.UiIcon.Search), 1, 0);
            _pickRange.Text = "保存后重新拾取排版范围";
            _pickRange.Dock = DockStyle.Fill;
            checks.Controls.Add(_pickRange, 0, 1);
            _rangeDescription.Dock = DockStyle.Fill;
            _rangeDescription.ForeColor = CadDialogTheme.Muted;
            _rangeDescription.TextAlign = ContentAlignment.MiddleLeft;
            _rangeDescription.AutoEllipsis = true;
            checks.Controls.Add(_rangeDescription, 0, 2);
            checks.Controls.Add(Button("扫描工程 CAD", PublisherForm.UiIcon.Refresh, RunProjectScan), 1, 1);
            _scanResult.Dock = DockStyle.Fill;
            _scanResult.ForeColor = CadDialogTheme.Muted;
            _scanResult.Text = "保存前自动检查同名图块与重复 TAG。";
            _scanResult.TextAlign = ContentAlignment.MiddleLeft;
            _scanResult.AutoEllipsis = true;
            checks.Controls.Add(_scanResult, 1, 2);
            content.Controls.Add(checks, 0, 10);
            root.Controls.Add(new CadScrollHost(content) { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Surface }, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
                Padding = new Padding(16, 9, 16, 9), BackColor = CadDialogTheme.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            footer.Controls.Add(new Label { Text = "保存登记前会检查当前工程的同名图块。", Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            footer.Controls.Add(Button("取消", PublisherForm.UiIcon.Remove, Close), 1, 0);
            footer.Controls.Add(Button("保存登记", PublisherForm.UiIcon.Save, Save, true), 2, 0);
            root.Controls.Add(footer, 0, 2);
        }

        private Control TitleBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4,
                BackColor = Color.FromArgb(27, 30, 40), Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            var mark = CadBrandIcon.CreateTitleMark();
            var caption = new Label { Text = Text, ForeColor = CadDialogTheme.Text,
                TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
            MouseEventHandler drag = (sender, args) => {
                if (args.Button != MouseButtons.Left) return;
                ReleaseCapture(); SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
            };
            bar.MouseDown += drag; mark.MouseDown += drag; caption.MouseDown += drag;
            bar.Controls.Add(mark, 0, 0); bar.Controls.Add(caption, 1, 0);
            bar.Controls.Add(Chrome("−", () => WindowState = FormWindowState.Minimized), 2, 0);
            bar.Controls.Add(Chrome("×", Close), 3, 0);
            return bar;
        }

        private static Button Chrome(string text, Action action)
        {
            var button = new CadChromeButton { Text = text, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Muted, BackColor = Color.FromArgb(27, 30, 40),
                Margin = Padding.Empty, TabStop = false, IsCloseButton = text == "×" };
            button.Click += (sender, args) => action();
            return button;
        }

        private Control Heading(string text, PublisherForm.UiIcon icon)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var picture = new PictureBox { Image = MakeIcon(icon, CadDialogTheme.Accent),
                SizeMode = PictureBoxSizeMode.CenterImage, Dock = DockStyle.Fill };
            row.Controls.Add(picture, 0, 0);
            row.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill,
                ForeColor = CadDialogTheme.Text, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
            return row;
        }

        private Control PairRow(string leftLabel, Control leftValue, string rightLabel, Control rightValue)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = CadDialogTheme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.Controls.Add(Field(leftLabel), 0, 0); row.Controls.Add(leftValue, 1, 0);
            row.Controls.Add(Field(rightLabel), 2, 0); row.Controls.Add(rightValue, 3, 0);
            return row;
        }

        private Control MapHeader()
        {
            var row = MapLayout();
            row.Controls.Add(Field(""), 0, 0);
            row.Controls.Add(Field("CAD 属性标签"), 1, 0);
            row.Controls.Add(Field("检测值 / 手工默认值"), 2, 0);
            return row;
        }

        private Control MapRow(string heading, int index)
        {
            var row = MapLayout();
            row.Controls.Add(Field(heading), 0, 0);
            row.Controls.Add(Combo(_tags[index]), 1, 0);
            row.Controls.Add(Input(_defaults[index]), 2, 0);
            _tags[index].SelectedIndexChanged += (sender, args) => {
                if (!_loading) FillFromTag(index);
            };
            return row;
        }

        private static TableLayoutPanel MapLayout()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = CadDialogTheme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            return row;
        }

        private static Label Field(string text) => new Label { Text = text, Dock = DockStyle.Fill,
            ForeColor = CadDialogTheme.Muted, TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 0, 7, 0), AutoEllipsis = true };

        private static Control Input(TextBox input, bool readOnly = false)
        {
            input.ReadOnly = readOnly;
            var host = CadDialogTheme.Input(input);
            host.Margin = new Padding(3, 0, 8, 8);
            return host;
        }

        private static Control Combo(PublisherForm.ThemedComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Dock = DockStyle.Fill;
            combo.Margin = new Padding(3, 0, 8, 8);
            return combo;
        }

        private CadRoundedButton Button(string text, PublisherForm.UiIcon icon, Action action, bool primary = false)
        {
            var button = CadDialogTheme.Button(text, action, primary);
            button.AutoSize = false; button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 1, 6, 1);
            button.Image = MakeIcon(icon, primary ? Color.White : CadDialogTheme.Muted);
            return button;
        }

        private Image MakeIcon(PublisherForm.UiIcon icon, Color color)
        {
            var image = PublisherForm.DrawUiIcon(icon, color);
            _icons.Add(image);
            return image;
        }

        private void LoadValues(string blockName)
        {
            _loading = true;
            try
            {
                _block.Text = blockName;
                _note.Text = _existing?.Note ?? string.Empty;
                _paper.Items.AddRange(new object[] { "A0", "A1", "A2", "A3", "A4" });
                _orientation.Items.AddRange(new object[] { "横向", "纵向" });
                _paper.SelectedIndexChanged += (sender, args) => UpdatePaperChoices();
                Select(_paper, _existing?.PaperSize ?? _guess.PaperSize);
                UpdatePaperChoices();
                Select(_extension, string.IsNullOrWhiteSpace(_existing?.Extension ?? _guess.Extension)
                    ? "无加长" : _existing?.Extension ?? _guess.Extension);
                Select(_orientation, PaperSizeCatalog.DefaultOrientation(_existing?.PaperSize ?? _guess.PaperSize));
                _orientation.Enabled = false;
                var selectedTags = _existing == null ? Enumerable.Empty<string>() : new[] {
                    _existing.BuildingAttributeTag, _existing.SheetNumberAttributeTag,
                    _existing.SheetNameAttributeTag, _existing.PrintScaleAttributeTag };
                var tags = new[] { DoNotRead }.Concat(_attributes.Keys).Concat(selectedTags)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var combo in _tags) combo.Items.AddRange(tags.Cast<object>().ToArray());
                if (_existing == null)
                {
                    SelectTag(0, tags, "子项目名称", "楼栋", "BUILDING", "栋号", "SUBPROJECT", "SUBPROJECTNAME");
                    SelectTag(1, tags, "图号", "SHEETNO", "SHEET_NO", "DRAWINGNO", "DRAWING_NO");
                    SelectTag(2, tags, "图名", "图纸名称", "SHEETNAME", "SHEET_NAME", "DRAWINGNAME", "DRAWING_NAME");
                    SelectTag(3, tags, "比例", "SCALE", "PRINTSCALE", "PRINT_SCALE");
                    for (var i = 0; i < 4; i++) FillFromTag(i);
                    if (string.IsNullOrWhiteSpace(_defaults[3].Text)) _defaults[3].Text = _guess.PrintScale;
                }
                else
                {
                    Select(_tags[0], EmptyTag(_existing.BuildingAttributeTag));
                    Select(_tags[1], EmptyTag(_existing.SheetNumberAttributeTag));
                    Select(_tags[2], EmptyTag(_existing.SheetNameAttributeTag));
                    Select(_tags[3], EmptyTag(_existing.PrintScaleAttributeTag));
                    _defaults[0].Text = _existing.DefaultBuilding ?? string.Empty;
                    _defaults[1].Text = _existing.DefaultSheetNumber ?? string.Empty;
                    _defaults[2].Text = _existing.DefaultSheetName ?? string.Empty;
                    _defaults[3].Text = string.IsNullOrWhiteSpace(_existing.DefaultPrintScale)
                        ? _guess.PrintScale : _existing.DefaultPrintScale;
                }
                _pickRange.Checked = _existing == null || !FrameLayoutRangeService.HasValidRange(_existing);
                _rangeDescription.Text = "当前登记：" + FrameLayoutRangeService.Describe(_existing);
            }
            finally { _loading = false; }
        }

        private void UpdatePaperChoices()
        {
            var previous = _extension.Text;
            _extension.Items.Clear();
            _extension.Items.Add("无加长");
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(_paper.Text)
                .Where(x => !string.IsNullOrWhiteSpace(x))) _extension.Items.Add(extension);
            Select(_extension, string.IsNullOrWhiteSpace(previous) ? "无加长" : previous);
            Select(_orientation, PaperSizeCatalog.DefaultOrientation(_paper.Text));
        }

        private void SelectTag(int index, IList<string> tags, params string[] aliases)
        {
            foreach (var alias in aliases)
            {
                var found = tags.FirstOrDefault(tag => string.Equals(tag, alias, StringComparison.OrdinalIgnoreCase));
                if (found != null) { Select(_tags[index], found); return; }
            }
            foreach (var alias in aliases)
            {
                var normalized = Normalize(alias);
                var found = tags.FirstOrDefault(tag => string.Equals(Normalize(tag), normalized, StringComparison.OrdinalIgnoreCase));
                if (found != null) { Select(_tags[index], found); return; }
            }
            Select(_tags[index], DoNotRead);
        }

        private void FillFromTag(int index)
        {
            var tag = AttributeTag(index);
            if (!string.IsNullOrWhiteSpace(tag) && _attributes.TryGetValue(tag, out var value)
                && !string.IsNullOrWhiteSpace(value)) _defaults[index].Text = value.Trim();
        }

        private static string Normalize(string value) => new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        private static string EmptyTag(string value) => string.IsNullOrWhiteSpace(value) ? DoNotRead : value;
        private string AttributeTag(int index) => _tags[index].Text == DoNotRead ? string.Empty : _tags[index].Text;
        private static void Select(PublisherForm.ThemedComboBox combo, string value)
        {
            foreach (var item in combo.Items.Values)
                if (string.Equals(Convert.ToString(item), value, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
            if (combo.DropDownStyle == ComboBoxStyle.DropDown) combo.Text = value;
            else combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
        }

        private void RunProjectScan()
        {
            if (_projectScanner == null) return;
            _scanResult.Text = "正在扫描工程 CAD...";
            try
            {
                _projectScan = _projectScanner();
                _scanResult.Text = _projectScan.Summary;
                if (_projectScan.Issues.Count > 0) ShowIssues();
                else if (_projectScan.Failures.Count > 0)
                    MessageBox.Show(this, "部分 CAD 读取失败：\r\n" + string.Join("\r\n", _projectScan.Failures.Take(20)),
                        "工程图框检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception exception)
            {
                _scanResult.Text = "扫描失败：" + exception.Message;
                MessageBox.Show(this, _scanResult.Text, "工程图框检查", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowIssues()
        {
            using (var issues = new FrameRegistrationIssuesForm(_projectScan))
            {
                issues.ShowDialog(this);
                if (issues.SelectedIssue == null) return;
                RequestedIssues.Clear();
                RequestedIssues.Add(issues.SelectedIssue);
                if (issues.OpenAllRequested)
                    RequestedIssues.AddRange(_projectScan.Issues.Where(x => !ReferenceEquals(x, issues.SelectedIssue)));
                OpenAllRequested = issues.OpenAllRequested;
                DialogResult = DialogResult.Cancel;
            }
        }

        private void Save()
        {
            if (_projectScanner != null && _projectScan == null) RunProjectScan();
            if (_projectScan != null && _projectScan.Issues.Count > 0)
            {
                if (RequestedIssues.Count == 0) ShowIssues();
                return;
            }
            if (_projectScan != null && _projectScan.Failures.Count > 0
                && MessageBox.Show(this, "有 " + _projectScan.Failures.Count + " 个工程 CAD 未能完成检查，仍要保存登记吗？",
                    "工程图框检查", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (string.IsNullOrWhiteSpace(_paper.Text))
            {
                MessageBox.Show(this, "请选择纸张规格。", "登记图框", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Definition = new FrameDefinition
            {
                RegistrationId = string.IsNullOrWhiteSpace(_existing?.RegistrationId)
                    ? Guid.NewGuid().ToString("N") : _existing.RegistrationId,
                BlockName = _block.Text,
                TemplateRelativePath = _existing?.TemplateRelativePath,
                AttributeTagSignature = _attributeTagSignature ?? _existing?.AttributeTagSignature,
                DefinitionSignature = _definitionSignature ?? _existing?.DefinitionSignature,
                ReferenceAspectRatio = _referenceAspectRatio > 0d ? _referenceAspectRatio : _existing?.ReferenceAspectRatio ?? 0d,
                PaperSize = _paper.Text,
                Extension = _extension.Text == "无加长" ? string.Empty : _extension.Text,
                PaperOrientation = _orientation.Text,
                Note = _note.Text.Trim(),
                BuildingAttributeTag = AttributeTag(0), SheetNumberAttributeTag = AttributeTag(1),
                SheetNameAttributeTag = AttributeTag(2), PrintScaleAttributeTag = AttributeTag(3),
                DefaultBuilding = _defaults[0].Text.Trim(), DefaultSheetNumber = _defaults[1].Text.Trim(),
                DefaultSheetName = _defaults[2].Text.Trim(), DefaultPrintScale = _defaults[3].Text.Trim(),
                HasLayoutRange = _existing?.HasLayoutRange ?? false,
                LayoutLeftMargin = _existing?.LayoutLeftMargin ?? 0d,
                LayoutRightMargin = _existing?.LayoutRightMargin ?? 0d,
                LayoutTopMargin = _existing?.LayoutTopMargin ?? 0d,
                LayoutBottomMargin = _existing?.LayoutBottomMargin ?? 0d
            };
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var icon in _icons) icon.Dispose();
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr word, IntPtr data);
    }
}
