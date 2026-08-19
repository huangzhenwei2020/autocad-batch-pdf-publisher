using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Services;
using CadColor = Autodesk.AutoCAD.Colors.Color;
using CadColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using CadColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;

namespace BatchPdfPublisher.Views
{
    public sealed class DraftingStandardForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly DataGridView _layers = Grid();
        private readonly DataGridView _texts = Grid();
        private readonly ComboBox _dimTextHeight = Combo(new[] { "1.5", "2.5", "3.5", "5", "7", "10" });
        private readonly ComboBox _arrowSize = Combo(new[] { "1.5", "2.5", "3.5", "5", "7" });
        private readonly TextBox _dimensionStylePrefix = new TextBox { Width = 180 };
        private readonly ComboBox _dimensionLineExtension = Combo(new[] { "0", "0.5", "1", "1.25" });
        private readonly ComboBox _baselineSpacing = Combo(new[] { "3", "3.75", "5", "7" });
        private readonly ComboBox _extensionBeyond = Combo(new[] { "0.5", "1", "1.25", "1.5" });
        private readonly ComboBox _extensionOriginOffset = Combo(new[] { "0", "0.5", "0.625", "1" });
        private readonly ComboBox _fixedExtensionLength = Combo(new[] { "3", "4", "5", "7", "10" });
        private readonly ComboBox _dimensionTextGap = Combo(new[] { "0.5", "0.625", "1", "1.25" });
        private readonly ComboBox _dimensionPrecision = Combo(new[] { "0", "1", "2", "3", "4" });
        private readonly ComboBox _dimensionRounding = Combo(new[] { "0", "0.5", "1", "5" });
        private readonly ComboBox _dimensionArrowStyle = Combo(new[] { "实心闭合", "空心闭合", "建筑标记", "建筑斜线", "点" });
        private readonly ComboBox _dimensionTextVertical = Combo(new[] { "尺寸线上方", "尺寸线居中", "尺寸线下方" });
        private readonly ComboBox _dimensionTextHorizontal = Combo(new[] { "尺寸线居中", "靠第一界线", "靠第二界线" });
        private readonly ComboBox _dimensionTextAlign = Combo(new[] { "与尺寸线对齐", "水平" });
        private readonly ComboBox _centerMarkStyle = Combo(new[] { "无", "中心标记", "中心线" });
        private readonly ComboBox _centerMarkSize = Combo(new[] { "1.5", "2.5", "3.5", "5" });
        private readonly ComboBox _arcLengthSymbol = Combo(new[] { "前置", "上方", "无" });
        private readonly ComboBox _jogAngle = Combo(new[] { "30", "45", "60" });
        private readonly CheckBox _createDimension = new CheckBox { Text = "一键应用时创建/更新标注样式", AutoSize = true };
        private readonly CheckBox _useFixedExtensionLength = new CheckBox { Text = "使用固定长度尺寸界线", AutoSize = true };
        private readonly Button _dimensionLineColor = ColorButton();
        private readonly Button _extensionLineColor = ColorButton();
        private readonly Button _dimensionTextColor = ColorButton();
        private readonly TextBox _leaderStyleName = new TextBox { Width = 180 };
        private readonly CheckBox _createLeader = new CheckBox { Text = "一键应用时创建/更新引线样式", AutoSize = true };
        private readonly ComboBox _leaderLineType = Combo(new[] { "直线", "样条曲线" });
        private readonly ComboBox _leaderArrowStyle = Combo(new[] { "实心闭合", "空心闭合", "建筑斜线", "点" });
        private readonly ComboBox _leaderArrowSize = Combo(new[] { "1.5", "2.5", "3.5", "5", "7" });
        private readonly ComboBox _leaderTextHeight = Combo(new[] { "1.5", "2.5", "3.5", "5", "7" });
        private readonly ComboBox _leaderLandingGap = Combo(new[] { "0", "0.5", "0.625", "1" });
        private readonly ComboBox _leaderDoglegLength = Combo(new[] { "0", "2.5", "3.75", "5", "7" });
        private readonly ComboBox _leaderLineWeight = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly CheckBox _leaderLanding = new CheckBox { Text = "启用水平基线", AutoSize = true };
        private readonly CheckBox _leaderDogleg = new CheckBox { Text = "启用折线段", AutoSize = true };
        private readonly CheckBox _leaderFrameText = new CheckBox { Text = "文字加边框", AutoSize = true };
        private readonly Button _leaderLineColor = ColorButton();
        private readonly Button _leaderTextColor = ColorButton();
        private readonly CheckBox _updateExisting = new CheckBox { Text = "同步更新图中已有的同名图层、文字样式和标注样式", AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.FromArgb(65, 75, 90) };
        private DraftingStandardProfile _profile;

        public DraftingStandardForm(Document document)
        {
            _document = document; Text = "制图标注设置（BZS）"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(940, 650); Size = new Size(1040, 760); Font = new System.Drawing.Font("Microsoft YaHei UI", 9F); BackColor = Color.White; FormBorderStyle = FormBorderStyle.Sizable;
            _dimensionLineColor.Click += delegate { PickDimensionColor(_dimensionLineColor); }; _extensionLineColor.Click += delegate { PickDimensionColor(_extensionLineColor); }; _dimensionTextColor.Click += delegate { PickDimensionColor(_dimensionTextColor); }; _leaderLineColor.Click += delegate { PickDimensionColor(_leaderLineColor); }; _leaderTextColor.Click += delegate { PickDimensionColor(_leaderTextColor); }; foreach (var x in LineWeightChoices()) _leaderLineWeight.Items.Add(x); ReloadArrowChoices();
            var intro = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 13, 12, 0), Text = "统一管理万落建筑工具使用的图层、文字样式和标注样式。保存后，图框、目录和楼梯等功能将采用该标准。", ForeColor = Color.FromArgb(45, 55, 70) };
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(15, 5) };
            tabs.TabPages.Add(MakeLayersTab()); tabs.TabPages.Add(MakeTextTab()); tabs.TabPages.Add(MakeDimensionTab()); tabs.TabPages.Add(MakeLeaderTab());
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(16, 10, 16, 10), BackColor = Color.FromArgb(247, 248, 250) };
            _status.Location = new Point(17, 14); footer.Controls.Add(_status);
            var close = Button("关闭", 88); close.DialogResult = DialogResult.Cancel;
            var defaults = Button("恢复默认", 96); defaults.Click += delegate { LoadProfile(DraftingStandardProfile.CreateDefault()); _status.Text = "已载入默认值，点击保存后才会生效。"; };
            var save = Button("仅保存设置", 110); save.Click += delegate { Save(false); };
            var apply = Button("保存并一键创建勾选项", 180); apply.Click += delegate { Save(true); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 500, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = false }; buttons.Controls.Add(close); buttons.Controls.Add(apply); buttons.Controls.Add(save); buttons.Controls.Add(defaults); footer.Controls.Add(buttons);
            Controls.Add(tabs); Controls.Add(footer); Controls.Add(intro); AcceptButton = apply; CancelButton = close;
            LoadProfile(DraftingStandardService.LoadProfile());
        }

        private TabPage MakeLayersTab()
        {
            var page = Page("图层标准"); _layers.Dock = DockStyle.Fill; _layers.Columns.Add(TextColumn("图层名称", 250));
            _layers.Columns.Add(new DataGridViewButtonColumn { HeaderText = "颜色", Width = 112, FlatStyle = FlatStyle.Flat, UseColumnTextForButtonValue = false });
            var weights = new DataGridViewComboBoxColumn { HeaderText = "线宽", Width = 110, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox }; foreach (var x in LineWeightChoices()) weights.Items.Add(x); _layers.Columns.Add(weights);
            var types = new DataGridViewComboBoxColumn { HeaderText = "线型", Width = 125, FlatStyle = FlatStyle.Flat }; types.Items.AddRange("Continuous", "HIDDEN", "CENTER", "DASHED"); _layers.Columns.Add(types);
            _layers.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "打印", Width = 62 });
            _layers.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "创建", Width = 62 });
            _layers.Columns.Add(ReadOnly("当前图纸", 90));
            _layers.Columns.Add(TextColumn("备注", 210));
            _layers.CellContentClick += LayerCellContentClick;
            _layers.CellPainting += LayerCellPainting;
            _layers.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 7, 0, 0), WrapContents = false };
            var all = Button("全选创建", 90); all.Click += delegate { SetChecked(_layers, 5, true); };
            var none = Button("取消全选", 90); none.Click += delegate { SetChecked(_layers, 5, false); };
            var add = Button("添加图层", 90); add.Click += delegate { AddLayerRow(); };
            var remove = Button("删除所选", 90); remove.Click += delegate { RemoveLayerRow(); };
            var create = Button("创建勾选图层", 120); create.Click += delegate { Save(true, ApplyScope.Layers); };
            var update = Button("更新当前图纸图层", 145); update.Click += delegate { Save(true, ApplyScope.LayersForceUpdate); };
            footer.Controls.Add(add); footer.Controls.Add(remove); footer.Controls.Add(all); footer.Controls.Add(none); footer.Controls.Add(create); footer.Controls.Add(update);
            page.Controls.Add(_layers); page.Controls.Add(footer); return page;
        }
        private TabPage MakeTextTab()
        {
            var page = Page("文字样式"); _texts.Dock = DockStyle.Fill; _texts.Columns.Add(TextColumn("样式名称", 190));
            var fontTypes = new DataGridViewComboBoxColumn { HeaderText = "字体类型", Width = 135, FlatStyle = FlatStyle.Flat }; fontTypes.Items.AddRange("Windows 字体", "CAD 字体（SHX）"); _texts.Columns.Add(fontTypes);
            var availableFonts = GetAvailableFontFiles();
            var fonts = new DataGridViewComboBoxColumn { HeaderText = "字体文件", Width = 175, FlatStyle = FlatStyle.Flat }; foreach (var font in availableFonts) fonts.Items.Add(font); _texts.Columns.Add(fonts);
            var bigFonts = new DataGridViewComboBoxColumn { HeaderText = "大字体（可空）", Width = 135, FlatStyle = FlatStyle.Flat }; bigFonts.Items.Add(""); foreach (var font in availableFonts.Where(x => x.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))) bigFonts.Items.Add(font); _texts.Columns.Add(bigFonts);
            var heights = new DataGridViewComboBoxColumn { HeaderText = "字高（1:1）", Width = 100, FlatStyle = FlatStyle.Flat }; heights.Items.AddRange("0", "1.5", "2.5", "3.5", "5", "7", "10", "14", "20"); _texts.Columns.Add(heights);
            var width = new DataGridViewComboBoxColumn { HeaderText = "宽度因子", Width = 95, FlatStyle = FlatStyle.Flat }; width.Items.AddRange("0.5", "0.7", "0.8", "1"); _texts.Columns.Add(width); _texts.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "创建", Width = 58 }); _texts.Columns.Add(ReadOnly("当前图纸", 82)); _texts.Columns.Add(TextColumn("备注", 170));
            _texts.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == 2) { var font = Cell(_texts.Rows[e.RowIndex], 2); _texts.Rows[e.RowIndex].Cells[1].Value = FontType(font); } };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 72 };
            var add = Button("添加文字样式", 112); add.Location = new Point(0, 7); add.Click += delegate { AddTextStyleRow(); };
            var remove = Button("删除所选", 96); remove.Location = new Point(120, 7); remove.Click += delegate { RemoveTextStyleRow(); };
            var create = Button("创建勾选样式", 120); create.Location = new Point(224, 7); create.Click += delegate { Save(true, ApplyScope.TextStyles); };
            var tip = new Label { Location = new Point(354, 11), Size = new Size(650, 48), Text = "字高 0 表示可变字高。字体列表来自 Windows Fonts 和 AutoCAD 支持路径；备注可自由修改。", ForeColor = Color.FromArgb(75, 85, 100) };
            footer.Controls.Add(add); footer.Controls.Add(remove); footer.Controls.Add(create); footer.Controls.Add(tip); page.Controls.Add(_texts); page.Controls.Add(footer); return page;
        }
        private TabPage MakeDimensionTab()
        {
            var page = Page("标注样式"); var tabs = new TabControl { Dock = DockStyle.Fill };
            var basic = SubPage("基本"); var p1 = SettingPanel(4); AddPair(p1, 0, "样式名称前缀", _dimensionStylePrefix, "创建标注样式", _createDimension); AddPair(p1, 1, "数值精度", _dimensionPrecision, "四舍五入", _dimensionRounding); AddPair(p1, 2, "已有资源处理", _updateExisting, "", new Label { Text = "基础样式始终按 1:1 保存。", AutoSize = true, ForeColor = Color.FromArgb(75, 85, 100) }); basic.Controls.Add(p1);
            var lines = SubPage("尺寸线与界线"); var p2 = SettingPanel(5); AddPair(p2, 0, "尺寸线颜色", _dimensionLineColor, "尺寸界线颜色", _extensionLineColor); AddPair(p2, 1, "尺寸线超出", _dimensionLineExtension, "基线间距", _baselineSpacing); AddPair(p2, 2, "界线超出尺寸线", _extensionBeyond, "起点偏移量", _extensionOriginOffset); AddPair(p2, 3, "固定长度界线", _useFixedExtensionLength, "固定长度", _fixedExtensionLength); lines.Controls.Add(p2);
            var text = SubPage("文字"); var p3 = SettingPanel(5); AddPair(p3, 0, "文字高度（1:1）", _dimTextHeight, "文字颜色", _dimensionTextColor); AddPair(p3, 1, "文字与尺寸线间距", _dimensionTextGap, "文字样式", new Label { Text = "使用“标注”文字样式", AutoSize = true }); AddPair(p3, 2, "垂直位置", _dimensionTextVertical, "水平位置", _dimensionTextHorizontal); AddPair(p3, 3, "文字对齐", _dimensionTextAlign, "", new Label { Text = "与尺寸线对齐：文字沿尺寸线方向", AutoSize = true, ForeColor = Color.FromArgb(75, 85, 100) }); text.Controls.Add(p3);
            var symbols = SubPage("符号和箭头"); var p4 = SettingPanel(5); AddPair(p4, 0, "箭头形式", _dimensionArrowStyle, "箭头大小（1:1）", _arrowSize); AddPair(p4, 1, "圆心标记", _centerMarkStyle, "圆心标记大小", _centerMarkSize); AddPair(p4, 2, "弧长符号", _arcLengthSymbol, "折弯角度", _jogAngle); var refresh = Button("重新读取当前库", 130); refresh.Click += delegate { ReloadArrowChoices(); }; var openLibrary = Button("打开实际库位置", 130); openLibrary.Click += delegate { OpenArrowLibraryFolder(); }; AddPair(p4, 3, "自定义箭头库", refresh, "实际使用文件", openLibrary); AddPair(p4, 4, "当前文件", new Label { Text = DraftingStandardService.ArrowLibraryFileName, AutoSize = true }, "修改后操作", new Label { Text = "保存 DWG，再点“重新读取当前库”", AutoSize = true }); symbols.Controls.Add(p4);
            tabs.TabPages.Add(basic); tabs.TabPages.Add(lines); tabs.TabPages.Add(text); tabs.TabPages.Add(symbols);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50 }; var create = Button("创建/更新标注样式", 160); create.Location = new Point(0, 8); create.Click += delegate { Save(true, ApplyScope.Dimension); }; footer.Controls.Add(create); page.Controls.Add(tabs); page.Controls.Add(footer); return page;
        }

        private TabPage MakeLeaderTab()
        {
            var page = Page("引线样式"); var tabs = new TabControl { Dock = DockStyle.Fill };
            var basic = SubPage("基本"); var p1 = SettingPanel(5); AddPair(p1, 0, "引线样式名称", _leaderStyleName, "创建引线样式", _createLeader); AddPair(p1, 1, "引线类型", _leaderLineType, "引线线宽", _leaderLineWeight); AddPair(p1, 2, "引线颜色", _leaderLineColor, "启用水平基线", _leaderLanding); AddPair(p1, 3, "基线间隙", _leaderLandingGap, "启用折线段", _leaderDogleg); AddPair(p1, 4, "折线段长度", _leaderDoglegLength, "", new Label()); basic.Controls.Add(p1);
            var arrow = SubPage("符号和箭头"); var p2 = SettingPanel(3); AddPair(p2, 0, "箭头形式", _leaderArrowStyle, "箭头大小（1:1）", _leaderArrowSize); var refresh = Button("重新读取当前库", 130); refresh.Click += delegate { ReloadArrowChoices(); }; var openLibrary = Button("打开实际库位置", 130); openLibrary.Click += delegate { OpenArrowLibraryFolder(); }; AddPair(p2, 1, "自定义箭头库", refresh, "实际使用文件", openLibrary); AddPair(p2, 2, "当前文件", new Label { Text = DraftingStandardService.ArrowLibraryFileName, AutoSize = true }, "修改后操作", new Label { Text = "保存 DWG，再重新读取", AutoSize = true }); arrow.Controls.Add(p2);
            var text = SubPage("文字"); var p3 = SettingPanel(4); AddPair(p3, 0, "文字高度（1:1）", _leaderTextHeight, "文字颜色", _leaderTextColor); AddPair(p3, 1, "文字边框", _leaderFrameText, "文字样式", new Label { Text = "使用“标注”文字样式", AutoSize = true }); text.Controls.Add(p3);
            tabs.TabPages.Add(basic); tabs.TabPages.Add(arrow); tabs.TabPages.Add(text);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 50 }; var create = Button("创建/更新引线样式", 160); create.Location = new Point(0, 8); create.Click += delegate { Save(true, ApplyScope.Leader); }; footer.Controls.Add(create); page.Controls.Add(tabs); page.Controls.Add(footer); return page;
        }

        private void LoadProfile(DraftingStandardProfile profile)
        {
            _profile = profile; _layers.Rows.Clear(); _texts.Rows.Clear();
            var layerNames = ExistingNames(true); foreach (var x in profile.Layers) { var row = _layers.Rows[_layers.Rows.Add(x.Name, ColorCaption(x), WeightChoice(x.LineWeight), x.LineType, x.IsPlottable, x.CreateOnApply, layerNames.Contains(x.Name) ? "已存在" : "待创建", x.Purpose)]; row.Cells[1].Tag = x; }
            var fonts = (DataGridViewComboBoxColumn)_texts.Columns[2]; var bigFonts = (DataGridViewComboBoxColumn)_texts.Columns[3]; var heights = (DataGridViewComboBoxColumn)_texts.Columns[4]; foreach (var x in profile.TextStyles) { AddComboValue(fonts, x.FontFile); AddComboValue(bigFonts, x.BigFontFile ?? ""); AddComboValue(heights, x.TextHeight.ToString(CultureInfo.InvariantCulture)); }
            var textNames = ExistingNames(false); foreach (var x in profile.TextStyles) { var row = _texts.Rows[_texts.Rows.Add(x.Name, string.IsNullOrWhiteSpace(x.FontType) ? FontType(x.FontFile) : x.FontType, x.FontFile, x.BigFontFile ?? "", x.TextHeight.ToString(CultureInfo.InvariantCulture), x.WidthFactor.ToString(CultureInfo.InvariantCulture), x.CreateOnApply, textNames.Contains(x.Name) ? "已存在" : "待创建", x.Purpose)]; row.Tag = x.Key; }
            _dimensionStylePrefix.Text = profile.DimensionStylePrefix; _createDimension.Checked = profile.DimensionCreateOnApply; _dimTextHeight.Text = profile.DimensionTextHeight.ToString(CultureInfo.InvariantCulture); _arrowSize.Text = profile.DimensionArrowSize.ToString(CultureInfo.InvariantCulture); _dimensionLineExtension.Text = profile.DimensionLineExtension.ToString(CultureInfo.InvariantCulture); _baselineSpacing.Text = profile.BaselineSpacing.ToString(CultureInfo.InvariantCulture); _extensionBeyond.Text = profile.ExtensionBeyond.ToString(CultureInfo.InvariantCulture); _extensionOriginOffset.Text = profile.ExtensionOriginOffset.ToString(CultureInfo.InvariantCulture); _useFixedExtensionLength.Checked = profile.UseFixedExtensionLength; _fixedExtensionLength.Text = profile.FixedExtensionLength.ToString(CultureInfo.InvariantCulture); _dimensionTextGap.Text = profile.DimensionTextGap.ToString(CultureInfo.InvariantCulture); _dimensionPrecision.Text = profile.DimensionPrecision.ToString(CultureInfo.InvariantCulture); _dimensionRounding.Text = profile.DimensionRounding.ToString(CultureInfo.InvariantCulture); SetColorButton(_dimensionLineColor, profile.DimensionLineColor); SetColorButton(_extensionLineColor, profile.ExtensionLineColor); SetColorButton(_dimensionTextColor, profile.DimensionTextColor); _updateExisting.Checked = profile.UpdateExisting;
            _dimensionArrowStyle.Text = profile.DimensionArrowStyle; _centerMarkStyle.Text = profile.CenterMarkStyle; _centerMarkSize.Text = profile.CenterMarkSize.ToString(CultureInfo.InvariantCulture); _arcLengthSymbol.Text = profile.ArcLengthSymbol; _jogAngle.Text = profile.JogAngle.ToString(CultureInfo.InvariantCulture);
            _dimensionTextVertical.Text = profile.DimensionTextVertical; _dimensionTextHorizontal.Text = profile.DimensionTextHorizontal; _dimensionTextAlign.Text = profile.DimensionTextAlign;
            _leaderStyleName.Text = profile.LeaderStyleName; _createLeader.Checked = profile.LeaderCreateOnApply; _leaderLineType.Text = profile.LeaderLineType; _leaderArrowStyle.Text = profile.LeaderArrowStyle; _leaderArrowSize.Text = profile.LeaderArrowSize.ToString(CultureInfo.InvariantCulture); _leaderTextHeight.Text = profile.LeaderTextHeight.ToString(CultureInfo.InvariantCulture); _leaderLandingGap.Text = profile.LeaderLandingGap.ToString(CultureInfo.InvariantCulture); _leaderDoglegLength.Text = profile.LeaderDoglegLength.ToString(CultureInfo.InvariantCulture); _leaderLineWeight.SelectedItem = WeightChoice(profile.LeaderLineWeight); _leaderLanding.Checked = profile.LeaderEnableLanding; _leaderDogleg.Checked = profile.LeaderEnableDogleg; _leaderFrameText.Checked = profile.LeaderFrameText; SetColorButton(_leaderLineColor, profile.LeaderLineColor); SetColorButton(_leaderTextColor, profile.LeaderTextColor);
            _status.Text = "设置文件：" + DraftingStandardService.SettingsPath;
        }
        private void Save(bool apply, ApplyScope scope = ApplyScope.All)
        {
            try
            {
                _layers.EndEdit(); _texts.EndEdit();
                for (var i = 0; i < _profile.Layers.Count; i++) { var row = _layers.Rows[i]; var x = _profile.Layers[i]; x.Name = Cell(row,0); Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.ValidateSymbolName(x.Name, false); x.LineWeight = ResolveLineWeight(row.Cells[2]); x.LineType = Cell(row,3); x.IsPlottable = Convert.ToBoolean(row.Cells[4].Value); x.CreateOnApply = Convert.ToBoolean(row.Cells[5].Value); x.Purpose = Cell(row,7); }
                var textStyles = new List<DraftingTextStyleSetting>(); for (var i = 0; i < _texts.Rows.Count; i++) { var row = _texts.Rows[i]; var name = Cell(row,0); Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.ValidateSymbolName(name, false); var font = Cell(row,2); var fontType = FontType(font); textStyles.Add(new DraftingTextStyleSetting { Key = Convert.ToString(row.Tag), Purpose = Cell(row,8), Name = name, FontType = fontType, FontFile = font, BigFontFile = fontType == "Windows 字体" ? string.Empty : Convert.ToString(row.Cells[3].Value).Trim(), TextHeight = ParseNonNegative(Cell(row,4), "文字字高"), WidthFactor = ParsePositive(Cell(row,5), "文字宽度因子"), CreateOnApply = Convert.ToBoolean(row.Cells[6].Value) }); } _profile.TextStyles = textStyles;
                _profile.DimensionScales = new List<int> { 1 }; _profile.DimensionStylePrefix = _dimensionStylePrefix.Text.Trim(); if (_profile.DimensionStylePrefix.Length == 0) throw new InvalidOperationException("标注样式名称前缀不能为空。"); _profile.DimensionCreateOnApply = _createDimension.Checked; _profile.DimensionTextHeight = ParsePositive(_dimTextHeight.Text, "标注文字高度"); _profile.DimensionArrowSize = ParsePositive(_arrowSize.Text, "箭头大小"); _profile.DimensionLineExtension = ParseNonNegative(_dimensionLineExtension.Text, "尺寸线超出"); _profile.BaselineSpacing = ParsePositive(_baselineSpacing.Text, "基线间距"); _profile.ExtensionBeyond = ParseNonNegative(_extensionBeyond.Text, "界线超出尺寸线"); _profile.ExtensionOriginOffset = ParseNonNegative(_extensionOriginOffset.Text, "起点偏移量"); _profile.UseFixedExtensionLength = _useFixedExtensionLength.Checked; _profile.FixedExtensionLength = ParsePositive(_fixedExtensionLength.Text, "固定界线长度"); _profile.DimensionTextGap = ParseNonNegative(_dimensionTextGap.Text, "文字间距"); _profile.DimensionPrecision = ParseInteger(_dimensionPrecision.Text, "数值精度", 0, 8); _profile.DimensionRounding = ParseNonNegative(_dimensionRounding.Text, "四舍五入"); _profile.DimensionLineColor = ColorIndex(_dimensionLineColor); _profile.ExtensionLineColor = ColorIndex(_extensionLineColor); _profile.DimensionTextColor = ColorIndex(_dimensionTextColor); _profile.UpdateExisting = _updateExisting.Checked;
                _profile.DimensionArrowStyle = _dimensionArrowStyle.Text; _profile.CenterMarkStyle = _centerMarkStyle.Text; _profile.CenterMarkSize = ParseNonNegative(_centerMarkSize.Text, "圆心标记大小"); _profile.ArcLengthSymbol = _arcLengthSymbol.Text; _profile.JogAngle = ParsePositive(_jogAngle.Text, "折弯角度");
                _profile.DimensionTextVertical = _dimensionTextVertical.Text; _profile.DimensionTextHorizontal = _dimensionTextHorizontal.Text; _profile.DimensionTextAlign = _dimensionTextAlign.Text;
                _profile.LeaderStyleName = _leaderStyleName.Text.Trim(); Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.ValidateSymbolName(_profile.LeaderStyleName, false); _profile.LeaderCreateOnApply = _createLeader.Checked; _profile.LeaderLineType = _leaderLineType.Text; _profile.LeaderArrowStyle = _leaderArrowStyle.Text; _profile.LeaderArrowSize = ParsePositive(_leaderArrowSize.Text, "引线箭头大小"); _profile.LeaderTextHeight = ParsePositive(_leaderTextHeight.Text, "引线文字高度"); _profile.LeaderLandingGap = ParseNonNegative(_leaderLandingGap.Text, "引线基线间隙"); _profile.LeaderDoglegLength = ParseNonNegative(_leaderDoglegLength.Text, "引线折线段长度"); var leaderWeight = _leaderLineWeight.SelectedItem as LineWeightChoice; if (leaderWeight == null) throw new InvalidOperationException("请选择有效的引线线宽。"); _profile.LeaderLineWeight = leaderWeight.Value; _profile.LeaderEnableLanding = _leaderLanding.Checked; _profile.LeaderEnableDogleg = _leaderDogleg.Checked; _profile.LeaderFrameText = _leaderFrameText.Checked; _profile.LeaderLineColor = ColorIndex(_leaderLineColor); _profile.LeaderTextColor = ColorIndex(_leaderTextColor); DraftingStandardService.SaveProfile(_profile);
                if (apply && _document != null) using (_document.LockDocument()) using (var tr = _document.Database.TransactionManager.StartTransaction()) { if (scope == ApplyScope.LayersForceUpdate) DraftingStandardService.ApplyAllConfiguredLayersToCurrentDrawing(_document.Database, tr, _profile); else if (scope == ApplyScope.Layers) DraftingStandardService.ApplyConfiguredLayers(_document.Database, tr, _profile, _profile.UpdateExisting); else if (scope == ApplyScope.TextStyles) DraftingStandardService.ApplyConfiguredTextStyles(_document.Database, tr, _profile, _profile.UpdateExisting); else if (scope == ApplyScope.Dimension) DraftingStandardService.ApplyConfiguredDimensionStyle(_document.Database, tr, _profile, _profile.UpdateExisting); else if (scope == ApplyScope.Leader) DraftingStandardService.ApplyConfiguredLeaderStyle(_document.Database, tr, _profile, _profile.UpdateExisting); else DraftingStandardService.ApplyConfiguredResources(_document.Database, tr, _profile, _profile.UpdateExisting); tr.Commit(); }
                _status.Text = apply ? "已保存，并已在当前图纸创建勾选资源。" : "已保存，后续万落工具将使用此标准。"; LoadProfile(DraftingStandardService.LoadProfile());
            }
            catch (Exception ex) { MessageBox.Show(this, "保存制图标准失败：\r\n" + ex.Message, "制图标准", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private HashSet<string> ExistingNames(bool layers)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (_document == null) return set;
            try { using (var tr = _document.Database.TransactionManager.StartOpenCloseTransaction()) { var table = tr.GetObject(layers ? _document.Database.LayerTableId : _document.Database.TextStyleTableId, OpenMode.ForRead) as SymbolTable; foreach (ObjectId id in table) { var record = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord; if (record != null) set.Add(record.Name); } } } catch { }
            return set;
        }
        private void AddTextStyleRow()
        {
            var index = _texts.Rows.Add("WL-文字-新样式", "Windows 字体", "simsun.ttc", "", "0", "1", true, "待创建", "自定义文字");
            _texts.Rows[index].Tag = "Custom_" + Guid.NewGuid().ToString("N"); _texts.CurrentCell = _texts.Rows[index].Cells[0]; _texts.BeginEdit(true);
        }
        private void AddLayerRow()
        {
            var setting = new DraftingLayerSetting { Key = "CustomLayer_" + Guid.NewGuid().ToString("N"), Purpose = "自定义图层", Name = "WL-自定义-新图层", ColorIndex = 7, TrueColorRgb = -1, LineWeight = 18, LineType = "Continuous", IsPlottable = true, CreateOnApply = true }; _profile.Layers.Add(setting);
            var index = _layers.Rows.Add(setting.Name, ColorCaption(setting), WeightChoice(setting.LineWeight), setting.LineType, true, true, "待创建", setting.Purpose); _layers.Rows[index].Cells[1].Tag = setting; _layers.CurrentCell = _layers.Rows[index].Cells[0]; _layers.BeginEdit(true);
        }
        private void RemoveLayerRow()
        {
            if (_layers.CurrentRow == null) return; var setting = _layers.CurrentRow.Cells[1].Tag as DraftingLayerSetting; if (setting == null) return;
            if (!setting.Key.StartsWith("CustomLayer_", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "插件预设图层不能从标准中删除；如果不想一键创建，可以取消勾选“创建”。", "图层管理"); return; }
            _profile.Layers.Remove(setting); _layers.Rows.Remove(_layers.CurrentRow);
        }
        private void RemoveTextStyleRow()
        {
            if (_texts.CurrentRow == null) return;
            var key = Convert.ToString(_texts.CurrentRow.Tag);
            if (key == DraftingStandardProfile.BodyTextKey || key == DraftingStandardProfile.TitleTextKey || key == DraftingStandardProfile.AnnotationTextKey) { MessageBox.Show(this, "正文、标题和标注文字样式是插件必需资源，不能删除。", "文字样式"); return; }
            _texts.Rows.Remove(_texts.CurrentRow);
        }

        private static string FontType(string fontFile)
        {
            return string.Equals(Path.GetExtension(fontFile ?? string.Empty), ".shx", StringComparison.OrdinalIgnoreCase)
                ? "CAD 字体（SHX）"
                : "Windows 字体";
        }

        private static List<string> GetAvailableFontFiles()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "宋体", "黑体", "微软雅黑", "仿宋", "楷体", "Arial",
                "simsun.ttc", "simhei.ttf", "msyh.ttc", "arial.ttf",
                "txt.shx", "simplex.shx", "tssdeng.shx", "hztxt.shx", "hzfs.shx", "gbcbig.shx"
            };
            try
            {
                using (var installed = new InstalledFontCollection())
                    foreach (var family in installed.Families) result.Add(family.Name);
            }
            catch { }
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts)); } catch { }
            try
            {
                var supportPaths = Convert.ToString(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("ACADPREFIX"));
                foreach (var folder in (supportPaths ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) folders.Add(folder.Trim());
            }
            catch { }
            foreach (var folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        var extension = Path.GetExtension(file);
                        if (string.Equals(extension, ".shx", StringComparison.OrdinalIgnoreCase)) result.Add(Path.GetFileName(file));
                    }
                }
                catch { }
            }
            return result.OrderBy(x => FontType(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private void LayerCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            var setting = _layers.Rows[e.RowIndex].Cells[1].Tag as DraftingLayerSetting;
            if (setting == null) return;
            var dialog = new CadColorDialog();
            {
                dialog.Color = setting.TrueColorRgb >= 0
                    ? CadColor.FromRgb((byte)((setting.TrueColorRgb >> 16) & 255), (byte)((setting.TrueColorRgb >> 8) & 255), (byte)(setting.TrueColorRgb & 255))
                    : CadColor.FromColorIndex(CadColorMethod.ByAci, setting.ColorIndex);
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var selected = dialog.Color;
                if (selected.ColorMethod == CadColorMethod.ByLayer || selected.ColorMethod == CadColorMethod.ByBlock)
                {
                    MessageBox.Show(this, "图层自身的颜色不能设为 ByLayer 或 ByBlock，请选择索引颜色或真彩色。", "图层颜色", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (selected.ColorMethod == CadColorMethod.ByAci)
                {
                    setting.ColorIndex = selected.ColorIndex;
                    setting.TrueColorRgb = -1;
                }
                else
                {
                    setting.TrueColorRgb = (selected.Red << 16) | (selected.Green << 8) | selected.Blue;
                }
                _layers.Rows[e.RowIndex].Cells[1].Value = ColorCaption(setting);
                _layers.InvalidateCell(1, e.RowIndex);
            }
        }
        private void LayerCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1 || (e.PaintParts & DataGridViewPaintParts.ContentForeground) == 0) return;
            e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
            var setting = _layers.Rows[e.RowIndex].Cells[1].Tag as DraftingLayerSetting;
            if (setting != null)
            {
                var swatch = new Rectangle(e.CellBounds.Left + 7, e.CellBounds.Top + 7, 18, Math.Max(10, e.CellBounds.Height - 14));
                using (var brush = new SolidBrush(DisplayColor(setting))) e.Graphics.FillRectangle(brush, swatch);
                e.Graphics.DrawRectangle(Pens.DimGray, swatch);
                TextRenderer.DrawText(e.Graphics, ColorCaption(setting), _layers.Font, new Rectangle(e.CellBounds.Left + 31, e.CellBounds.Top, e.CellBounds.Width - 34, e.CellBounds.Height), _layers.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            e.Handled = true;
        }
        private static System.Drawing.Color DisplayColor(DraftingLayerSetting x)
        {
            if (x.TrueColorRgb >= 0) return System.Drawing.Color.FromArgb((x.TrueColorRgb >> 16) & 255, (x.TrueColorRgb >> 8) & 255, x.TrueColorRgb & 255);
            var rgb = Autodesk.AutoCAD.Colors.EntityColor.LookUpRgb((byte)Math.Max(0, Math.Min(255, (int)x.ColorIndex)));
            return System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
        private void PickDimensionColor(Button button)
        {
            var dialog = new CadColorDialog { Color = CadColor.FromColorIndex(CadColorMethod.ByAci, ColorIndex(button)) };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            var selected = dialog.Color;
            if (selected.ColorMethod != CadColorMethod.ByAci) { MessageBox.Show(this, "标注样式颜色请选择 AutoCAD 索引颜色。", "标注颜色", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SetColorButton(button, selected.ColorIndex);
        }
        private static void SetColorButton(Button button, short index)
        {
            button.Tag = index; button.Text = index == 0 ? "0 / ByBlock" : "ACI " + index;
            var rgb = Autodesk.AutoCAD.Colors.EntityColor.LookUpRgb((byte)Math.Max(0, Math.Min(255, (int)index)));
            button.BackColor = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); button.ForeColor = index == 7 || index == 2 ? Color.Black : Color.White;
        }
        private static short ColorIndex(Button button) { return button.Tag is short ? (short)button.Tag : (short)0; }
        private void ReloadArrowChoices()
        {
            var dimension = _dimensionArrowStyle.Text; var leader = _leaderArrowStyle.Text; var choices = DraftingStandardService.GetArrowStyleChoices();
            FillArrowChoices(_dimensionArrowStyle, choices, dimension); FillArrowChoices(_leaderArrowStyle, choices, leader);
            _status.Text = System.IO.File.Exists(DraftingStandardService.ArrowLibraryPath) ? "已读取箭头图块库：" + DraftingStandardService.ArrowLibraryPath : "未找到箭头图块库，当前只显示内置箭头。";
        }
        private void OpenArrowLibraryFolder()
        {
            var path = DraftingStandardService.ArrowLibraryPath;
            if (!System.IO.File.Exists(path)) { MessageBox.Show(this, "当前实际图块库不存在：\r\n" + path, "箭头图块库", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            _status.Text = "当前 CAD 实际读取：" + path;
        }
        private static void FillArrowChoices(ComboBox combo, IEnumerable<string> values, string selected) { combo.BeginUpdate(); combo.Items.Clear(); foreach (var value in values) combo.Items.Add(value); if (!string.IsNullOrWhiteSpace(selected) && !combo.Items.Contains(selected)) combo.Items.Add(selected); combo.Text = string.IsNullOrWhiteSpace(selected) ? "实心闭合" : selected; combo.EndUpdate(); }
        private static void AddComboValue(DataGridViewComboBoxColumn column, string value) { if (!column.Items.Contains(value)) column.Items.Add(value); }
        private static void SetChecked(DataGridView grid, int column, bool value) { foreach (DataGridViewRow row in grid.Rows) row.Cells[column].Value = value; }
        private static string ColorCaption(DraftingLayerSetting x) { return x.TrueColorRgb >= 0 ? "RGB " + ((x.TrueColorRgb >> 16) & 255) + "," + ((x.TrueColorRgb >> 8) & 255) + "," + (x.TrueColorRgb & 255) : "ACI " + x.ColorIndex; }
        private static LineWeightChoice WeightChoice(int value) { return LineWeightChoices().FirstOrDefault(x => x.Value == value) ?? new LineWeightChoice(value, value >= 0 ? (value / 100d).ToString("0.00", CultureInfo.InvariantCulture) + " mm" : "默认"); }
        private static int ResolveLineWeight(DataGridViewCell cell)
        {
            var choice = cell.Value as LineWeightChoice; if (choice != null) return choice.Value; var text = Convert.ToString(cell.FormattedValue).Trim();
            var match = LineWeightChoices().FirstOrDefault(x => string.Equals(x.ToString(), text, StringComparison.OrdinalIgnoreCase)); if (match != null) return match.Value;
            if (string.Equals(text, "默认", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase)) return (int)LineWeight.ByLineWeightDefault;
            double mm; if (double.TryParse(text.Replace("mm", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mm) || double.TryParse(text.Replace("mm", "").Trim(), out mm)) return (int)Math.Round(mm * 100d);
            throw new InvalidOperationException("请选择有效的 CAD 线宽。");
        }
        private static IEnumerable<LineWeightChoice> LineWeightChoices()
        {
            yield return new LineWeightChoice((int)LineWeight.ByLineWeightDefault, "默认");
            foreach (var value in new[] { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 })
                yield return new LineWeightChoice(value, (value / 100d).ToString("0.00", CultureInfo.InvariantCulture) + " mm");
        }
        private sealed class LineWeightChoice
        {
            public readonly int Value; private readonly string _text;
            public LineWeightChoice(int value, string text) { Value = value; _text = text; }
            public override string ToString() { return _text; }
            public override bool Equals(object obj) { var other = obj as LineWeightChoice; return other != null && other.Value == Value; }
            public override int GetHashCode() { return Value; }
        }
        private static string Cell(DataGridViewRow r, int i) { var v = Convert.ToString(r.Cells[i].Value).Trim(); if (v.Length == 0) throw new InvalidOperationException(r.Cells[i].OwningColumn.HeaderText + "不能为空。"); return v; }
        private static double ParsePositive(string value, string name) { double x; if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && !double.TryParse(value, out x) || x <= 0) throw new InvalidOperationException(name + "必须是大于 0 的数值。"); return x; }
        private static double ParseNonNegative(string value, string name) { double x; if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && !double.TryParse(value, out x) || x < 0) throw new InvalidOperationException(name + "必须是大于或等于 0 的数值。"); return x; }
        private static int ParseInteger(string value, string name, int min, int max) { int x; if (!int.TryParse(value, out x) || x < min || x > max) throw new InvalidOperationException(name + "必须是 " + min + " 到 " + max + " 之间的整数。"); return x; }
        private static DataGridView Grid() { return new DataGridView { AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoGenerateColumns = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, ColumnHeadersHeight = 32, RowTemplate = { Height = 30 } }; }
        private static DataGridViewTextBoxColumn ReadOnly(string name,int width) { return new DataGridViewTextBoxColumn { HeaderText=name,Width=width,ReadOnly=true }; } private static DataGridViewTextBoxColumn TextColumn(string name,int width) { return new DataGridViewTextBoxColumn { HeaderText=name,Width=width }; }
        private static TabPage Page(string text) { return new TabPage(text) { BackColor=Color.White,Padding=new Padding(14) }; } private static ComboBox Combo(string[] values) { var x=new ComboBox { DropDownStyle=ComboBoxStyle.DropDown,Width=160 }; x.Items.AddRange(values); return x; }
        private static TabPage SubPage(string text) { return new TabPage(text) { BackColor = Color.White, Padding = new Padding(8) }; }
        private static TableLayoutPanel SettingPanel(int rows) { var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = Math.Max(120, rows * 48 + 28), Padding = new Padding(18), ColumnCount = 4, RowCount = rows }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); return panel; }
        private static Button ColorButton() { return new Button { Width = 160, Height = 28, FlatStyle = FlatStyle.Standard, UseVisualStyleBackColor = false }; }
        private static Button Button(string text,int width) { return new Button { Text=text,Width=width,Height=32,Margin=new Padding(6,3,0,3),FlatStyle=FlatStyle.Standard }; }
        private static void AddRow(TableLayoutPanel p,int row,string label,Control control) { p.RowStyles.Add(new RowStyle(SizeType.Absolute,row==4?72:40)); p.Controls.Add(new Label { Text=label,AutoSize=true,Anchor=AnchorStyles.Left },0,row); control.Anchor=AnchorStyles.Left|AnchorStyles.Right; p.Controls.Add(control,1,row); }
        private static void AddPair(TableLayoutPanel panel, int row, string leftLabel, Control left, string rightLabel, Control right) { panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); panel.Controls.Add(new Label { Text = leftLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); left.Anchor = AnchorStyles.Left | AnchorStyles.Right; panel.Controls.Add(left, 1, row); panel.Controls.Add(new Label { Text = rightLabel, AutoSize = true, Anchor = AnchorStyles.Left }, 2, row); right.Anchor = AnchorStyles.Left | AnchorStyles.Right; panel.Controls.Add(right, 3, row); }
        private enum ApplyScope { All, Layers, LayersForceUpdate, TextStyles, Dimension, Leader }
    }
}
