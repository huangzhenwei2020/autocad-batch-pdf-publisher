using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class DraftingStandardForm : Form
    {
        private readonly Document _document;
        private readonly DataGridView _layers = Grid();
        private readonly DataGridView _texts = Grid();
        private readonly TextBox _scales = new TextBox();
        private readonly ComboBox _dimTextHeight = Combo(new[] { "1.5", "2.5", "3.5", "5", "7", "10" });
        private readonly ComboBox _arrowSize = Combo(new[] { "1.5", "2.5", "3.5", "5", "7" });
        private readonly CheckBox _updateExisting = new CheckBox { Text = "同步更新图中已有的同名图层、文字样式和标注样式", AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.FromArgb(65, 75, 90) };
        private DraftingStandardProfile _profile;

        public DraftingStandardForm(Document document)
        {
            _document = document; Text = "制图标准设置（BZS）"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 610); Size = new Size(980, 680); Font = new System.Drawing.Font("Microsoft YaHei UI", 9F); BackColor = Color.White; FormBorderStyle = FormBorderStyle.Sizable;
            var intro = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(16, 13, 12, 0), Text = "统一管理万落建筑工具使用的图层、文字样式和标注样式。保存后，图框、目录和楼梯等功能将采用该标准。", ForeColor = Color.FromArgb(45, 55, 70) };
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(15, 5) };
            tabs.TabPages.Add(MakeLayersTab()); tabs.TabPages.Add(MakeTextTab()); tabs.TabPages.Add(MakeDimensionTab());
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(16, 10, 16, 10), BackColor = Color.FromArgb(247, 248, 250) };
            _status.Location = new Point(17, 14); footer.Controls.Add(_status);
            var close = Button("关闭", 88); close.DialogResult = DialogResult.Cancel;
            var defaults = Button("恢复默认", 96); defaults.Click += delegate { LoadProfile(DraftingStandardProfile.CreateDefault()); _status.Text = "已载入默认值，点击保存后才会生效。"; };
            var save = Button("仅保存设置", 110); save.Click += delegate { Save(false); };
            var apply = Button("保存并应用到当前图纸", 170); apply.Click += delegate { Save(true); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 500, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = false }; buttons.Controls.Add(close); buttons.Controls.Add(apply); buttons.Controls.Add(save); buttons.Controls.Add(defaults); footer.Controls.Add(buttons);
            Controls.Add(tabs); Controls.Add(footer); Controls.Add(intro); AcceptButton = apply; CancelButton = close;
            LoadProfile(DraftingStandardService.LoadProfile());
        }

        private TabPage MakeLayersTab()
        {
            var page = Page("图层标准"); _layers.Dock = DockStyle.Fill; _layers.Columns.Add(ReadOnly("用途", 125)); _layers.Columns.Add(TextColumn("图层名称", 280));
            var color = new DataGridViewComboBoxColumn { HeaderText = "ACI 颜色", Width = 105, FlatStyle = FlatStyle.Flat }; for (short i = 1; i <= 9; i++) color.Items.Add(i); _layers.Columns.Add(color);
            var weights = new DataGridViewComboBoxColumn { HeaderText = "线宽 (mm)", Width = 115, FlatStyle = FlatStyle.Flat }; foreach (var x in new[] { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 70 }) weights.Items.Add(x); _layers.Columns.Add(weights);
            var types = new DataGridViewComboBoxColumn { HeaderText = "线型", Width = 130, FlatStyle = FlatStyle.Flat }; types.Items.AddRange("Continuous", "HIDDEN", "CENTER", "DASHED"); _layers.Columns.Add(types); _layers.Columns.Add(ReadOnly("当前图纸", 100));
            page.Controls.Add(_layers); return page;
        }
        private TabPage MakeTextTab()
        {
            var page = Page("文字样式"); _texts.Dock = DockStyle.Fill; _texts.Columns.Add(ReadOnly("用途", 135)); _texts.Columns.Add(TextColumn("样式名称", 260)); _texts.Columns.Add(TextColumn("字体文件", 260));
            var width = new DataGridViewComboBoxColumn { HeaderText = "宽度因子", Width = 120, FlatStyle = FlatStyle.Flat }; width.Items.AddRange("0.5", "0.7", "0.8", "1"); _texts.Columns.Add(width); _texts.Columns.Add(ReadOnly("当前图纸", 110));
            var tip = new Label { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12), Text = "字体文件示例：simsun.ttc（宋体）、simhei.ttf（黑体）、msyh.ttc（微软雅黑）。文字高度由具体图纸内容控制。", ForeColor = Color.FromArgb(75, 85, 100) }; page.Controls.Add(_texts); page.Controls.Add(tip); return page;
        }
        private TabPage MakeDimensionTab()
        {
            var page = Page("标注样式"); var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(18), ColumnCount = 2, RowCount = 5 }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(panel, 0, "常用标注比例", _scales); AddRow(panel, 1, "标注文字高度", _dimTextHeight); AddRow(panel, 2, "箭头大小", _arrowSize); AddRow(panel, 3, "已有资源处理", _updateExisting);
            var note = new Label { AutoSize = true, Text = "比例可输入：1, 20, 50, 100, 200。将创建 WL-标注-1_1、WL-标注-1_20 等样式。\r\n关闭“同步更新”时，只补齐缺少的资源，不改动用户已有设置。", ForeColor = Color.FromArgb(75, 85, 100), Padding = new Padding(0, 8, 0, 0) }; AddRow(panel, 4, "说明", note); page.Controls.Add(panel); return page;
        }

        private void LoadProfile(DraftingStandardProfile profile)
        {
            _profile = profile; _layers.Rows.Clear(); _texts.Rows.Clear();
            var layerNames = ExistingNames(true); foreach (var x in profile.Layers) _layers.Rows.Add(x.Purpose, x.Name, x.ColorIndex, x.LineWeight, x.LineType, layerNames.Contains(x.Name) ? "已存在" : "待创建");
            var textNames = ExistingNames(false); foreach (var x in profile.TextStyles) _texts.Rows.Add(x.Purpose, x.Name, x.FontFile, x.WidthFactor.ToString(CultureInfo.InvariantCulture), textNames.Contains(x.Name) ? "已存在" : "待创建");
            _scales.Text = string.Join(", ", profile.DimensionScales); _dimTextHeight.Text = profile.DimensionTextHeight.ToString(CultureInfo.InvariantCulture); _arrowSize.Text = profile.DimensionArrowSize.ToString(CultureInfo.InvariantCulture); _updateExisting.Checked = profile.UpdateExisting;
            _status.Text = "设置文件：" + DraftingStandardService.SettingsPath;
        }
        private void Save(bool apply)
        {
            try
            {
                _layers.EndEdit(); _texts.EndEdit();
                for (var i = 0; i < _profile.Layers.Count; i++) { var row = _layers.Rows[i]; var x = _profile.Layers[i]; x.Name = Cell(row,1); Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.ValidateSymbolName(x.Name, false); x.ColorIndex = Convert.ToInt16(row.Cells[2].Value); x.LineWeight = Convert.ToInt32(row.Cells[3].Value); x.LineType = Cell(row,4); }
                for (var i = 0; i < _profile.TextStyles.Count; i++) { var row = _texts.Rows[i]; var x = _profile.TextStyles[i]; x.Name = Cell(row,1); Autodesk.AutoCAD.DatabaseServices.SymbolUtilityServices.ValidateSymbolName(x.Name, false); x.FontFile = Cell(row,2); x.WidthFactor = ParsePositive(Cell(row,3), "文字宽度因子"); }
                _profile.DimensionScales = DraftingStandardService.ParseScales(_scales.Text); _profile.DimensionTextHeight = ParsePositive(_dimTextHeight.Text, "标注文字高度"); _profile.DimensionArrowSize = ParsePositive(_arrowSize.Text, "箭头大小"); _profile.UpdateExisting = _updateExisting.Checked; DraftingStandardService.SaveProfile(_profile);
                if (apply && _document != null) using (_document.LockDocument()) using (var tr = _document.Database.TransactionManager.StartTransaction()) { DraftingStandardService.EnsureAll(_document.Database, tr, _profile, _profile.UpdateExisting); tr.Commit(); }
                _status.Text = apply ? "已保存，并已应用到当前图纸。" : "已保存，后续万落工具将使用此标准。"; LoadProfile(DraftingStandardService.LoadProfile());
            }
            catch (Exception ex) { MessageBox.Show(this, "保存制图标准失败：\r\n" + ex.Message, "制图标准", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private HashSet<string> ExistingNames(bool layers)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); if (_document == null) return set;
            try { using (var tr = _document.Database.TransactionManager.StartOpenCloseTransaction()) { var table = tr.GetObject(layers ? _document.Database.LayerTableId : _document.Database.TextStyleTableId, OpenMode.ForRead) as SymbolTable; foreach (ObjectId id in table) { var record = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord; if (record != null) set.Add(record.Name); } } } catch { }
            return set;
        }
        private static string Cell(DataGridViewRow r, int i) { var v = Convert.ToString(r.Cells[i].Value).Trim(); if (v.Length == 0) throw new InvalidOperationException(r.Cells[i].OwningColumn.HeaderText + "不能为空。"); return v; }
        private static double ParsePositive(string value, string name) { double x; if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && !double.TryParse(value, out x) || x <= 0) throw new InvalidOperationException(name + "必须是大于 0 的数值。"); return x; }
        private static DataGridView Grid() { return new DataGridView { AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoGenerateColumns = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, ColumnHeadersHeight = 32, RowTemplate = { Height = 30 } }; }
        private static DataGridViewTextBoxColumn ReadOnly(string name,int width) { return new DataGridViewTextBoxColumn { HeaderText=name,Width=width,ReadOnly=true }; } private static DataGridViewTextBoxColumn TextColumn(string name,int width) { return new DataGridViewTextBoxColumn { HeaderText=name,Width=width }; }
        private static TabPage Page(string text) { return new TabPage(text) { BackColor=Color.White,Padding=new Padding(14) }; } private static ComboBox Combo(string[] values) { var x=new ComboBox { DropDownStyle=ComboBoxStyle.DropDown,Width=160 }; x.Items.AddRange(values); return x; }
        private static Button Button(string text,int width) { return new Button { Text=text,Width=width,Height=32,Margin=new Padding(6,3,0,3),FlatStyle=FlatStyle.Standard }; }
        private static void AddRow(TableLayoutPanel p,int row,string label,Control control) { p.RowStyles.Add(new RowStyle(SizeType.Absolute,row==4?72:40)); p.Controls.Add(new Label { Text=label,AutoSize=true,Anchor=AnchorStyles.Left },0,row); control.Anchor=AnchorStyles.Left|AnchorStyles.Right; p.Controls.Add(control,1,row); }
    }
}
