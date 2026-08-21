using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace BatchPdfPublisher.Views
{
    /// <summary>Small, square-cornered wizard for creating a real-size CAD frame block.</summary>
    public sealed class FrameCreationForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly Action _refresh;
        private readonly ComboBox _paper = new ComboBox();
        private readonly ComboBox _extension = new ComboBox();
        private readonly ComboBox _orientation = new ComboBox();
        private readonly ComboBox _property = new ComboBox();
        private readonly ComboBox _font = new ComboBox();
        private readonly ComboBox _height = new ComboBox();
        private readonly ComboBox _widthFactor = new ComboBox();
        private readonly Button _colorButton = new Button();
        private readonly TextBox _remark = new TextBox();
        private readonly CheckBox _register = new CheckBox();
        private readonly ComboBox _registeredFrame = new ComboBox();
        private readonly ComboBox _insertScale = new ComboBox();
        private Button _insertRegisteredButton;
        private Button _rangeButton;
        private readonly List<FrameDefinition> _registeredFrames;
        private readonly ToolTip _toolTip = new ToolTip();

        public FrameCreationForm(Document document, Action refresh)
        {
            _document = document; _refresh = refresh;
            _registeredFrames = new PublishPlanStore().LoadFrames().Where(x => x != null && !string.IsNullOrWhiteSpace(x.BlockName)).ToList();
            Text = "创建 / 插入图框"; Width = 660; Height = 440; MinimumSize = new Size(620, 410);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9F);
            Build(); LoadSettings(); FormClosed += (s, e) => SaveSettings();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 10 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "纸张规格", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 0);
            AddCombo(_paper, new[] { "A0", "A1", "A2", "A3", "A4" }, "A1", root, 1, 0);
            _paper.SelectedIndexChanged += (s, e) => RefreshExtensionChoices();
            root.Controls.Add(new Label { Text = "加长", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 1);
            AddCombo(_extension, new[] { "无加长", "1/4", "1/2", "3/4", "1", "5/4", "3/2", "7/4", "2", "9/4", "5/2", "3", "7/2" }, "无加长", root, 1, 1);
            RefreshExtensionChoices();
            root.Controls.Add(new Label { Text = "方向", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 2);
            AddCombo(_orientation, new[] { "横向", "纵向" }, "横向", root, 1, 2);
            root.Controls.Add(new Label { Text = "用户备注", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 3);
            _remark.Text = "自建图框"; _remark.Dock = DockStyle.Fill; _remark.Height = 30; root.Controls.Add(_remark, 1, 3);
            root.Controls.Add(new Label { Text = "属性文字", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 4);
            AddCombo(_property, new[] { "工程名称", "子项目名称", "图纸名称", "设计编号", "设计阶段", "图号", "序号", "纸张", "比例", "日期", "版本" }, "图纸名称", root, 1, 4);
            _property.DropDownStyle = ComboBoxStyle.DropDown;
            _register.Text = "创建后登记为图框"; _register.AutoSize = true; _register.Checked = true; _register.Margin = new Padding(0, 7, 0, 3); root.Controls.Add(_register, 1, 6);
            root.Controls.Add(new Label { Text = "字体 / 字高", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 5);
            var fontLine = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            _font.DropDownStyle = ComboBoxStyle.DropDownList; _font.Width = 150; _font.Items.AddRange(FrameCreationService.GetTextStyleNames(_document)); _font.SelectedItem = DraftingStandardService.GetTextStyleName(DraftingStandardProfile.BodyTextKey); if (_font.SelectedIndex < 0) _font.SelectedIndex = 0;
            _height.DropDownStyle = ComboBoxStyle.DropDown; _height.Width = 70; _height.Items.AddRange(new object[] { "1.5", "2.5", "3.5", "5", "7", "10", "14", "20" }); _height.Text = "3.5";
            _widthFactor.DropDownStyle = ComboBoxStyle.DropDownList; _widthFactor.Width = 65; _widthFactor.Items.AddRange(new object[] { "0.5", "0.7", "1" }); _widthFactor.SelectedItem = "1";
            _colorButton.Text = string.Empty; _colorButton.Width = 32; _colorButton.Height = 30; _colorButton.BackColor = Color.White; _colorButton.ForeColor = Color.Black; _colorButton.FlatStyle = FlatStyle.Flat; _colorButton.Click += (s, e) => ChooseColor(); _toolTip.SetToolTip(_colorButton, "选择 AutoCAD 文字颜色");
            fontLine.Controls.Add(_font); fontLine.Controls.Add(new Label { Text = " 高", AutoSize = true, Margin = new Padding(4, 7, 2, 0) }); fontLine.Controls.Add(_height); fontLine.Controls.Add(new Label { Text = " 宽", AutoSize = true, Margin = new Padding(4, 7, 2, 0) }); fontLine.Controls.Add(_widthFactor); fontLine.Controls.Add(_colorButton); root.Controls.Add(fontLine, 1, 5);

            root.Controls.Add(new Label { Text = "插入登记图框", AutoSize = true, Margin = new Padding(0, 7, 0, 3) }, 0, 7);
            var insertLine = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            _registeredFrame.DropDownStyle = ComboBoxStyle.DropDownList; _registeredFrame.Width = 300;
            foreach (var frame in _registeredFrames) _registeredFrame.Items.Add(frame.DisplayName);
            if (_registeredFrame.Items.Count > 0) _registeredFrame.SelectedIndex = 0;
            _insertScale.DropDownStyle = ComboBoxStyle.DropDown; _insertScale.Width = 90;
            _insertScale.Items.AddRange(new object[] { "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:50", "1:100", "1:150", "1:200" }); _insertScale.Text = "1:100";
            insertLine.Controls.Add(_registeredFrame); insertLine.Controls.Add(new Label { Text = " 比例", AutoSize = true, Margin = new Padding(6, 7, 2, 0) }); insertLine.Controls.Add(_insertScale);
            root.Controls.Add(insertLine, 1, 7);
            var help = new Label { Text = _registeredFrames.Count == 0 ? "当前项目还没有登记图框，请先创建并登记。" : "按 CAD 块插入方式使用块基点和所选统一比例，块参照放到图框层。", AutoSize = true, ForeColor = Color.FromArgb(80, 100, 125), Margin = new Padding(0, 8, 0, 8) };
            root.Controls.Add(help, 0, 8); root.SetColumnSpan(help, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            actions.Controls.Add(Button("关闭", (s, e) => Close(), false));
            actions.Controls.Add(Button("创建图框块", (s, e) => RunCad(CreateBlock), true));
            _insertRegisteredButton = Button("插入登记图框", (s, e) => RunCad(InsertRegisteredFrame), true); _insertRegisteredButton.Enabled = _registeredFrames.Count > 0; actions.Controls.Add(_insertRegisteredButton);
            _rangeButton = Button("写入排版范围", (s, e) => RunCad(WriteLayoutRange), true); _rangeButton.Enabled = _registeredFrames.Count > 0; actions.Controls.Add(_rangeButton);
            actions.Controls.Add(Button("插入属性文字", (s, e) => RunCad(InsertProperty), false));
            actions.Controls.Add(Button("插入纸张边框矩形", (s, e) => RunCad(InsertBorder), false));
            root.Controls.Add(actions, 0, 9); root.SetColumnSpan(actions, 2);
        }

        private void InsertBorder()
        {
            SaveSettings();
            var size = PaperSizeCatalog.GetSize(_paper.Text, _extension.Text == "无加长" ? string.Empty : _extension.Text, _orientation.Text);
            if (FrameCreationService.InsertBorder(_document, size[0], size[1])) _refresh?.Invoke();
        }

#if ACAD_R19
        private void RunCad(Action action)
#else
        private async void RunCad(Action action)
#endif
        {
            Hide();
#if ACAD_R19
            CadCommandContext.Execute(() =>
#else
            await CadCommandContext.ExecuteAsync(() =>
#endif
            {
                try { action(); }
                catch (Exception exception) { BeginInvoke(new Action(() => MessageBox.Show(this, exception.Message, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Error))); }
                finally { BeginInvoke(new Action(() => { if (!IsDisposed) Show(); })); }
            });
        }

        private void InsertProperty()
        {
            SaveSettings();
            var selected = _colorButton.Tag as AcColor;
            if (FrameCreationService.InsertCenteredProperty(_document, _property.Text, _font.Text, double.Parse(_height.Text), double.Parse(_widthFactor.Text), selected)) _refresh?.Invoke();
        }

        private void InsertRegisteredFrame()
        {
            SaveSettings();
            if (_registeredFrame.SelectedIndex < 0 || _registeredFrame.SelectedIndex >= _registeredFrames.Count)
                throw new InvalidOperationException("请选择已登记的图框。");
            var scaleText = (_insertScale.Text ?? string.Empty).Trim().Replace('：', ':');
            var separator = scaleText.LastIndexOf(':');
            if (separator >= 0) scaleText = scaleText.Substring(separator + 1).Trim();
            int scale;
            if (!int.TryParse(scaleText, out scale) || scale <= 0) throw new InvalidOperationException("请输入有效比例，例如 1:50。");
            if (FrameCreationService.InsertRegisteredFrame(_document, _registeredFrames[_registeredFrame.SelectedIndex], scale)) _refresh?.Invoke();
        }

        private void WriteLayoutRange()
        {
            SaveSettings();
            if (_registeredFrame.SelectedIndex < 0 || _registeredFrame.SelectedIndex >= _registeredFrames.Count)
                throw new InvalidOperationException("请选择已登记的图框。");
            var frame = _registeredFrames[_registeredFrame.SelectedIndex];
            if (!FrameLayoutRangeService.PromptAndSaveRange(_document, frame)) return;
            _refresh?.Invoke();
            BeginInvoke(new Action(() => MessageBox.Show(this, "图框排版范围已重新登记成功。\r\n" + FrameLayoutRangeService.Describe(frame), "创建 / 插入图框", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        }

        private void CreateBlock()
        {
            SaveSettings();
            var paper = _paper.Text; var extension = _extension.Text == "无加长" ? string.Empty : _extension.Text;
            var size = PaperSizeCatalog.GetSize(paper, extension, _orientation.Text);
            var remark = string.IsNullOrWhiteSpace(_remark.Text) ? "自建图框" : _remark.Text.Trim();
            var blockName = FrameCreationService.CreateFrameBlockFromSelection(_document, "", size[0], size[1], _font.Text, double.Parse(_height.Text), remark, out var error, out var detectedPaper, out var detectedExtension, out var detectedOrientation, out var createdReferenceId);
            if (string.IsNullOrWhiteSpace(blockName)) { if (!string.IsNullOrWhiteSpace(error)) MessageBox.Show(this, error, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!string.IsNullOrWhiteSpace(blockName))
            {
                var registered = false;
                if (_register.Checked)
                {
                    registered = new FrameRegistrationService().RegisterCreated(_document, createdReferenceId, remark, out var registrationError);
                    if (!registered)
                        MessageBox.Show(this, "图框块已创建，但自动登记失败：\r\n" + registrationError, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else ReloadRegisteredFrames(blockName);
                }
                _refresh?.Invoke();
                if (!_register.Checked || registered)
                    MessageBox.Show(this, registered ? "图框块已创建并登记：\r\n" + blockName : "图框块已创建：\r\n" + blockName, "创建图框", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ChooseColor()
        {
            var dialog = new AcColorDialog();
            try
            {
                dialog.Color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var color = dialog.Color;
                    _colorButton.Tag = color;
                    _colorButton.BackColor = DisplayColor(color);
                    _colorButton.ForeColor = _colorButton.BackColor.GetBrightness() < .5f ? Color.White : Color.Black;
                }
            }
            finally { }
        }

        private void ReloadRegisteredFrames(string selectedBlockName)
        {
            _registeredFrames.Clear();
            _registeredFrames.AddRange(new PublishPlanStore().LoadFrames().Where(x => x != null && !string.IsNullOrWhiteSpace(x.BlockName)));
            _registeredFrame.Items.Clear();
            foreach (var frame in _registeredFrames) _registeredFrame.Items.Add(frame.DisplayName);
            var index = _registeredFrames.FindIndex(x => string.Equals(x.BlockName, selectedBlockName, StringComparison.OrdinalIgnoreCase));
            _registeredFrame.SelectedIndex = index >= 0 ? index : (_registeredFrame.Items.Count > 0 ? 0 : -1);
            if (_insertRegisteredButton != null) _insertRegisteredButton.Enabled = _registeredFrames.Count > 0;
            if (_rangeButton != null) _rangeButton.Enabled = _registeredFrames.Count > 0;
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
                    case 1: return Color.Red; case 2: return Color.Yellow; case 3: return Color.LimeGreen; case 4: return Color.Cyan;
                    case 5: return Color.Blue; case 6: return Color.Magenta; case 8: return Color.DarkGray; case 9: return Color.Gray; default: return Color.White;
                }
            }
            return Color.White;
        }
        private void LoadSettings()
        {
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(SettingsPath)) foreach (var line in File.ReadAllLines(SettingsPath)) { var split = line.IndexOf('='); if (split > 0) values[line.Substring(0, split)] = line.Substring(split + 1); }
                Select(_paper, values, "Paper"); Select(_extension, values, "Extension"); Select(_orientation, values, "Orientation");
                if (values.ContainsKey("Remark")) _remark.Text = values["Remark"];
                if (values.ContainsKey("Property")) _property.Text = values["Property"];
                if (values.ContainsKey("Font")) _font.SelectedItem = values["Font"];
                if (values.ContainsKey("Height")) _height.Text = values["Height"];
                if (values.ContainsKey("WidthFactor")) _widthFactor.SelectedItem = values["WidthFactor"];
                if (values.ContainsKey("Register")) _register.Checked = values["Register"] == "1";
                if (values.ContainsKey("InsertScale")) _insertScale.Text = values["InsertScale"];
                if (values.ContainsKey("InsertFrameId") || values.ContainsKey("InsertFrameBlock"))
                {
                    var frameId = values.ContainsKey("InsertFrameId") ? values["InsertFrameId"] : string.Empty;
                    var blockName = values.ContainsKey("InsertFrameBlock") ? values["InsertFrameBlock"] : string.Empty;
                    var selected = _registeredFrames.FindIndex(x =>
                        (!string.IsNullOrWhiteSpace(frameId) && string.Equals(x.RegistrationId, frameId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(blockName) && string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase)));
                    if (selected >= 0) _registeredFrame.SelectedIndex = selected;
                }
                var color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
                if (values.ContainsKey("ColorIndex") && short.TryParse(values["ColorIndex"], out var index)) color = AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, index);
                if (values.ContainsKey("ColorMethod") && values["ColorMethod"] == "ByColor" && byte.TryParse(values["ColorR"], out var red) && byte.TryParse(values["ColorG"], out var green) && byte.TryParse(values["ColorB"], out var blue)) color = AcColor.FromRgb(red, green, blue);
                _colorButton.Tag = color; _colorButton.BackColor = DisplayColor(color); _colorButton.ForeColor = _colorButton.BackColor.GetBrightness() < .5f ? Color.White : Color.Black;
            }
            catch { }
        }
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var color = _colorButton.Tag as AcColor;
                var selectedFrame = _registeredFrame.SelectedIndex >= 0 && _registeredFrame.SelectedIndex < _registeredFrames.Count ? _registeredFrames[_registeredFrame.SelectedIndex] : null;
                File.WriteAllLines(SettingsPath, new[] { "Paper=" + _paper.Text, "Extension=" + _extension.Text, "Orientation=" + _orientation.Text, "Remark=" + _remark.Text, "Property=" + _property.Text, "Font=" + _font.Text, "Height=" + _height.Text, "WidthFactor=" + _widthFactor.Text, "Register=" + (_register.Checked ? "1" : "0"), "InsertFrameId=" + (selectedFrame?.RegistrationId ?? string.Empty), "InsertFrameBlock=" + (selectedFrame?.BlockName ?? string.Empty), "InsertScale=" + _insertScale.Text, "ColorMethod=" + (color == null ? "ByAci" : color.ColorMethod.ToString()), "ColorIndex=" + (color?.ColorIndex ?? 7), "ColorR=" + (color?.Red ?? 255), "ColorG=" + (color?.Green ?? 255), "ColorB=" + (color?.Blue ?? 255) });
            }
            catch { }
        }
        private static void Select(ComboBox box, IDictionary<string, string> values, string key) { if (values.ContainsKey(key)) box.SelectedItem = values[key]; }

        private static void AddCombo(ComboBox box, string[] values, string selected, TableLayoutPanel root, int column, int row)
        { box.DropDownStyle = ComboBoxStyle.DropDownList; box.Dock = DockStyle.Fill; box.Height = 30; box.Items.AddRange(values); box.SelectedItem = selected; root.Controls.Add(box, column, row); }

        private void RefreshExtensionChoices()
        {
            if (_extension == null || _paper == null || string.IsNullOrWhiteSpace(_paper.Text)) return;
            var previous = _extension.Text;
            _extension.Items.Clear();
            _extension.Items.Add("无加长");
            foreach (var extension in PaperSizeCatalog.GetSupportedExtensions(_paper.Text).Where(x => !string.IsNullOrWhiteSpace(x))) _extension.Items.Add(extension);
            _extension.SelectedItem = string.IsNullOrWhiteSpace(previous) ? "无加长" : previous;
            if (_extension.SelectedIndex < 0) _extension.SelectedIndex = 0;
        }
        private static Button Button(string text, EventHandler click, bool accent)
        { var b = new Button { Text = text, AutoSize = true, Height = 30, MinimumSize = new Size(0, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(25, 54, 99), Margin = new Padding(3), Padding = new Padding(7, 2, 7, 2) }; b.FlatAppearance.BorderColor = accent ? Color.FromArgb(104, 145, 185) : Color.FromArgb(190, 201, 216); b.Click += click; return b; }
    }
}
