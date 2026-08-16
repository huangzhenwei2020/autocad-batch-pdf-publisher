using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DrawingFont = System.Drawing.Font;
using FormsFlowDirection = System.Windows.Forms.FlowDirection;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowElevationForm : Form
    {
        private sealed class FrameChoice
        {
            public FrameDefinition Frame;
            public string Name;
            public override string ToString() { return Name ?? string.Empty; }
        }
        private readonly Document _document;
        private DoorWindowScheduleReadResult _source;
        private readonly BindingList<DoorWindowScheduleItem> _rows = new BindingList<DoorWindowScheduleItem>();
        private readonly DataGridView _grid = new DataGridView();
        private readonly DoorWindowElevationPreviewControl _preview = new DoorWindowElevationPreviewControl();
        private readonly Label _sourceLabel = new Label();
        private readonly Label _status = new Label();
        private static readonly string[] DoorWindowTypes = { "普通窗", "高窗", "凸窗", "百叶窗", "甲级防火窗", "乙级防火窗", "丙级防火窗", "防火窗（等级待确认）", "普通门", "甲级防火门", "乙级防火门", "丙级防火门", "防火门（等级待确认）", "人防门", "百叶门", "门联窗", "洞口", "待确认" };
        private readonly ComboBox _batchType = Combo(new[] { "不修改" }.Concat(DoorWindowTypes).ToArray());
        private readonly ComboBox _filterType = Combo(new[] { "全部类型" }.Concat(DoorWindowTypes).ToArray());
        private readonly ComboBox _batchDivision = Combo(new[] { "不修改", "未设置", "单扇", "双扇等分", "三扇等分", "上亮", "侧亮", "上亮+侧亮", "门联窗", "自定义" });
        private readonly ComboBox _batchOpening = Combo(new[] { "不修改", "未设置", "固定", "左平开", "右平开", "双扇平开", "左推拉", "右推拉", "双向推拉", "上悬", "下悬", "百叶", "自定义" });
        private readonly ComboBox _drawingScale = ScaleCombo();
        private readonly ComboBox _frameChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Height = 28, Margin = new Padding(4, 3, 4, 0) };
        private readonly ComboBox _templateChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Height = 28, Margin = new Padding(4, 3, 4, 0) };
        private readonly DoorWindowElevationStore _store = new DoorWindowElevationStore();
        private readonly DoorWindowElevationTemplateStore _templateStore = new DoorWindowElevationTemplateStore();
        private bool _propagatingGridEdit;

        public DoorWindowElevationForm(Document document, DoorWindowScheduleReadResult source)
        {
            _document = document; _source = source;
            Text = "批量门窗立面";
            StartPosition = FormStartPosition.CenterParent;
            Width = 1240; Height = 720; MinimumSize = new Size(980, 560);
            Font = new DrawingFont("Microsoft YaHei UI", 9F);
            Build(); LoadSource(source);
            FormClosed += (s, e) => SavePreferences(false);
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 8, 12, 6), BackColor = Color.FromArgb(245, 247, 250) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "门窗表数据", Font = new DrawingFont(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            _sourceLabel.AutoSize = true; _sourceLabel.ForeColor = Color.FromArgb(70, 82, 96);
            var labels = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FormsFlowDirection.TopDown, WrapContents = false }; labels.Controls.Add(title); labels.Controls.Add(_sourceLabel); header.Controls.Add(labels, 0, 0);
            var sourceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FormsFlowDirection.LeftToRight };
            var repick = ButtonFor("重新拾取门窗表"); repick.Click += (s, e) => Repick(); sourceButtons.Controls.Add(repick);
            var locate = ButtonFor("定位来源表"); locate.Click += (s, e) => LocateSource(); sourceButtons.Controls.Add(locate);
            var log = ButtonFor("打开诊断日志"); log.Click += (s, e) => OpenLog(); sourceButtons.Controls.Add(log);
            header.Controls.Add(sourceButtons, 1, 0); root.Controls.Add(header, 0, 0);

            var batch = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(12, 7, 8, 5), BackColor = Color.FromArgb(250, 251, 252) };
            batch.Controls.Add(LabelFor("批量设置")); batch.Controls.Add(_batchType); batch.Controls.Add(_batchDivision); batch.Controls.Add(_batchOpening);
            batch.Controls.Add(LabelFor("按类型选择")); batch.Controls.Add(_filterType);
            var selectType = ButtonFor("勾选该类型"); selectType.Click += (s, e) => SelectByType(); batch.Controls.Add(selectType);
            var applyBatch = ButtonFor("应用到多选/勾选"); applyBatch.Click += (s, e) => ApplyBatch(); batch.Controls.Add(applyBatch);
            var constructionBatch = ButtonFor("批量构造设置"); constructionBatch.Click += (s, e) => ApplyBatchConstruction(); batch.Controls.Add(constructionBatch);
            var auto = ButtonFor("按尺寸自动判断"); auto.Click += (s, e) => ApplyAutomaticSuggestions(); batch.Controls.Add(auto);
            var custom = ButtonFor("编辑当前分格"); custom.Click += (s, e) => EditCurrentDivision(); batch.Controls.Add(custom);
            batch.Controls.Add(LabelFor("参数模板")); LoadTemplateChoices(); batch.Controls.Add(_templateChoice);
            var applyTemplate = ButtonFor("应用到多选/勾选"); applyTemplate.Click += (s, e) => ApplySelectedTemplate(); batch.Controls.Add(applyTemplate);
            var saveTemplate = ButtonFor("当前项存为模板"); saveTemplate.Click += (s, e) => SaveCurrentAsTemplate(); batch.Controls.Add(saveTemplate);
            var deleteTemplate = ButtonFor("删除模板"); deleteTemplate.Click += (s, e) => DeleteSelectedTemplate(); batch.Controls.Add(deleteTemplate);
            batch.Controls.Add(LabelFor("出图比例")); batch.Controls.Add(_drawingScale);
            batch.Controls.Add(LabelFor("排版图框")); LoadFrameChoices(); batch.Controls.Add(_frameChoice);
            var selectAll = ButtonFor("全选"); selectAll.Click += (s, e) => SelectAll(true); batch.Controls.Add(selectAll);
            var selectNone = ButtonFor("全不选"); selectNone.Click += (s, e) => SelectAll(false); batch.Controls.Add(selectNone);
            batch.Controls.Add(new Label { Text = "几何按实际毫米 1:1；安装缝默认 20 mm。天正图名接口未确认时使用兼容图名。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(14, 7, 0, 0) });
            root.Controls.Add(batch, 0, 1);

            ConfigureGrid();
            // SplitContainer is still at its design-time default width while the
            // form tree is being constructed.  Setting minimum panel widths here
            // throws before the window can be shown on some DPI configurations.
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5, BackColor = Color.FromArgb(225, 229, 234) };
            split.Panel1.Controls.Add(_grid);
            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.White };
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewPanel.Controls.Add(new Label { Text = "当前立面预览", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), BackColor = Color.FromArgb(245, 247, 250) }, 0, 0);
            previewPanel.Controls.Add(_preview, 0, 1); split.Panel2.Controls.Add(previewPanel); root.Controls.Add(split, 0, 2);
            Shown += (s, e) => { InitializeSplitLayout(split); SelectFirstRow(); };

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 8, 12, 7), BackColor = Color.FromArgb(245, 247, 250) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.AutoSize = true; _status.Margin = new Padding(0, 7, 0, 0); footer.Controls.Add(_status, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FormsFlowDirection.RightToLeft };
            var save = ButtonFor("保存门窗设置"); save.Click += (s, e) => SavePreferences(true); actions.Controls.Add(save);
            var insert = ButtonFor("插入所选立面"); insert.Click += (s, e) => InsertElevations(); actions.Controls.Add(insert);
            var update = ButtonFor("更新已生成立面"); update.Click += (s, e) => UpdateGeneratedElevation(); actions.Controls.Add(update);
            var insertSchedule = ButtonFor("插入门窗表"); insertSchedule.Click += (s, e) => InsertDoorWindowSchedule(); actions.Controls.Add(insertSchedule);
            var close = ButtonFor("关闭"); close.Click += (s, e) => Close(); actions.Controls.Add(close);
            var none = ButtonFor("取消全选"); none.Click += (s, e) => SelectAll(false); actions.Controls.Add(none);
            var all = ButtonFor("全选可生成项"); all.Click += (s, e) => SelectAll(true); actions.Controls.Add(all);
            footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
        }

        private static void InitializeSplitLayout(SplitContainer split)
        {
            if (split == null || split.ClientSize.Width < 700) return;
            // Set the distance first while both minimum sizes are still zero,
            // then apply constraints after the control has its real pixel width.
            var maximum = Math.Max(1, split.ClientSize.Width - split.SplitterWidth - 240);
            split.SplitterDistance = Math.Min(maximum, Math.Max(480, split.ClientSize.Width - 350));
            split.Panel1MinSize = Math.Min(480, split.SplitterDistance);
            split.Panel2MinSize = Math.Min(240, split.ClientSize.Width - split.SplitterDistance - split.SplitterWidth);
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = true; _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.BackgroundColor = Color.White; _grid.BorderStyle = BorderStyle.FixedSingle; _grid.ColumnHeadersHeight = 34; _grid.RowTemplate.Height = 29;
            _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 232, 240); _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "生成", DataPropertyName = "Selected", Width = 52 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序", DataPropertyName = "Sequence", Width = 44, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "编号", DataPropertyName = "Code", Width = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "洞口尺寸", DataPropertyName = "SizeText", Width = 116, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数量", DataPropertyName = "Quantity", Width = 58, ReadOnly = true });
            _grid.Columns.Add(ComboColumn("门窗类型", "ElevationType", 100, DoorWindowTypes));
            _grid.Columns.Add(ComboColumn("分格模板", "DivisionPreset", 118, new[] { "未设置", "单扇", "双扇等分", "三扇等分", "上亮", "侧亮", "上亮+侧亮", "门联窗", "自定义" }));
            _grid.Columns.Add(ComboColumn("开启方式", "OpeningMode", 110, new[] { "未设置", "固定", "左平开", "右平开", "双扇平开", "左推拉", "右推拉", "双向推拉", "上悬", "下悬", "百叶", "自定义" }));
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "安装缝", DataPropertyName = "HasInstallationGap", Width = 65 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "安装缝(mm)", DataPropertyName = "InstallationGap", Width = 92 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "外框", DataPropertyName = "HasOuterFrame", Width = 58 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "外框宽(mm)", DataPropertyName = "OuterFrameWidth", Width = 92 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "分隔框", DataPropertyName = "HasMullion", Width = 68 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "分隔框宽(mm)", DataPropertyName = "MullionWidth", Width = 100 });
            _grid.Columns.Add(ComboColumn("门套", "DoorFrameType", 72, new[] { "N型", "口型" }));
            _grid.Columns.Add(ComboColumn("材质", "Material", 78, new[] { "无", "玻璃", "实板", "百叶" }));
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "离地高度(mm)", DataPropertyName = "SillHeight", Width = 104 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图集名称", DataPropertyName = "AtlasName", Width = 90 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", DataPropertyName = "Remarks", Width = 140 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "窗框外包", DataPropertyName = "FrameSizeText", Width = 116, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "来源备注", DataPropertyName = "SourceNote", Width = 160, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", DataPropertyName = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 150, ReadOnly = true });
            _grid.DataSource = _rows;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += OnGridCellValueChanged;
            _grid.SelectionChanged += (s, e) => UpdatePreview();
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditCurrentDivision(); };
            _grid.ColumnHeaderMouseClick += (s, e) => { if (e.ColumnIndex == 0) SelectAll(!_rows.Where(IsSelectable).All(x => x.Selected)); };
            _grid.CellFormatting += (s, e) => { if (e.RowIndex < 0) return; var status = _rows[e.RowIndex].Status ?? string.Empty; if (status.Contains("冲突") || status.Contains("缺少") || status.Contains("小于")) e.CellStyle.ForeColor = Color.Firebrick; else if (status.Contains("可生成")) e.CellStyle.ForeColor = Color.FromArgb(20, 112, 65); };
            _grid.CellFormatting += (s, e) => { if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].DataPropertyName != "SillHeight") return; if (!(_rows[e.RowIndex].ElevationType ?? string.Empty).Contains("窗")) { e.Value = "—"; e.FormattingApplied = true; } };
            _grid.CellBeginEdit += (s, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].DataPropertyName == "SillHeight" && !(_rows[e.RowIndex].ElevationType ?? string.Empty).Contains("窗")) e.Cancel = true; };
            _grid.DataError += (s, e) => { e.ThrowException = false; };
        }

        private void LoadSource(DoorWindowScheduleReadResult source)
        {
            _source = source; _rows.RaiseListChangedEvents = false; _rows.Clear();
            var preferences = _store.LoadForActiveProject();
            var savedScale = preferences.Select(x => x.DrawingScale).FirstOrDefault(x => x > 0); if (savedScale > 0) _drawingScale.Text = "1:" + savedScale;
            foreach (var item in source.Items)
            {
                var preference = preferences.FirstOrDefault(x => string.Equals(x.Code, item.Code, StringComparison.OrdinalIgnoreCase) && Math.Abs(x.Width - item.Width) < 0.01 && Math.Abs(x.Height - item.Height) < 0.01)
                    ?? preferences.FirstOrDefault(x => string.Equals(x.Code, item.Code, StringComparison.OrdinalIgnoreCase));
                if (preference != null)
                {
                    item.ElevationType = preference.ElevationType; item.DivisionPreset = preference.DivisionPreset; item.OpeningMode = preference.OpeningMode;
                    if (item.ElevationType == "门") item.ElevationType = "普通门";
                    else if (item.ElevationType == "窗") item.ElevationType = "普通窗";
                    else if (item.ElevationType == "防火门") item.ElevationType = "防火门（等级待确认）";
                    else if (item.ElevationType == "防火窗") item.ElevationType = "防火窗（等级待确认）";
                    if (item.OpeningMode == "推拉") item.OpeningMode = "右推拉";
                    item.HasInstallationGap = preference.HasInstallationGap;
                    item.InstallationGap = preference.InstallationGap > 0 ? preference.InstallationGap : 20d;
                    item.HasOuterFrame = preference.HasOuterFrame; item.OuterFrameWidth = preference.OuterFrameWidth > 0 ? preference.OuterFrameWidth : 50d;
                    item.HasMullion = preference.HasMullion; item.MullionWidth = preference.MullionWidth > 0 ? preference.MullionWidth : 50d; item.DoorFrameType = string.IsNullOrWhiteSpace(preference.DoorFrameType) ? "N型" : preference.DoorFrameType;
                    item.CustomColumnRatios = preference.CustomColumnRatios; item.CustomRowRatios = preference.CustomRowRatios;
                    item.CustomColumnWidths = preference.CustomColumnWidths; item.CustomRowHeights = preference.CustomRowHeights; item.CellOpeningModes = preference.CellOpeningModes;
                    item.CustomCellLayout = preference.CustomCellLayout;
                    item.DoorPlacement = preference.DoorPlacement; item.DoorEdgeDistance = preference.DoorEdgeDistance;
                    item.Material = string.IsNullOrWhiteSpace(preference.Material) ? item.Material : preference.Material;
                    item.AtlasName = string.IsNullOrWhiteSpace(preference.AtlasName) ? item.AtlasName : DoorWindowElevationSuggestionService.NormalizeAtlasName(preference.AtlasName); item.Remarks = preference.Remarks;
                    if (preference.HasSillHeight) item.SillHeight = preference.SillHeight;
                }
                UpdateStatus(item); _rows.Add(item);
            }
            _rows.RaiseListChangedEvents = true; _rows.ResetBindings();
            _sourceLabel.Text = source.SourceDxfName + " · Handle " + source.SourceHandle + " · " + source.Adapter + " · " + CadCompatibilityService.DescribeTianzhengHost();
            UpdateSummary(); _preview.ShowItem(_rows.FirstOrDefault());
            if (IsHandleCreated) BeginInvoke(new Action(SelectFirstRow));
        }

        private void ApplyBatch()
        {
            var targets = OperationTargets();
            foreach (var item in targets)
            {
                if (Convert.ToString(_batchType.SelectedItem) != "不修改") item.ElevationType = Convert.ToString(_batchType.SelectedItem);
                if (Convert.ToString(_batchDivision.SelectedItem) != "不修改") item.DivisionPreset = Convert.ToString(_batchDivision.SelectedItem);
                if (Convert.ToString(_batchOpening.SelectedItem) != "不修改") item.OpeningMode = Convert.ToString(_batchOpening.SelectedItem);
                if ((item.ElevationType ?? string.Empty).Contains("窗") && item.SillHeight <= 0d) item.SillHeight = 900d; NormalizeSillHeight(item);
                UpdateStatus(item);
            }
            _grid.Refresh(); UpdateSummary(); UpdatePreview(); _status.Text = "已批量修改 " + targets.Count + " 项（蓝色多选行优先，否则使用勾选项）。";
        }

        private void SelectByType()
        {
            _grid.EndEdit(); var type = Convert.ToString(_filterType.SelectedItem);
            foreach (var item in _rows) item.Selected = IsSelectable(item) && (type == "全部类型" || string.Equals(item.ElevationType, type, StringComparison.Ordinal));
            _grid.Refresh(); UpdateSummary();
        }

        private void ApplyBatchConstruction()
        {
            _grid.EndEdit(); var targets = OperationTargets();
            if (targets.Count == 0) { MessageBox.Show(this, "请先多选或勾选需要修改的门窗。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var dialog = new DoorWindowBatchConstructionForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (var item in targets)
                {
                    var normalize = false;
                    if (dialog.ApplyGap) { item.HasInstallationGap = dialog.GapEnabled; item.InstallationGap = dialog.Gap; normalize = true; }
                    if (dialog.ApplyOuter) { item.HasOuterFrame = dialog.OuterEnabled; item.OuterFrameWidth = dialog.Outer; }
                    if (dialog.ApplyMullion) { item.HasMullion = dialog.MullionEnabled; item.MullionWidth = dialog.Mullion; }
                    if (dialog.ApplyDoorType) item.DoorFrameType = dialog.DoorType;
                    if (normalize) NormalizeActualSizes(item); UpdateStatus(item);
                }
            }
            _grid.Refresh(); UpdateSummary(); UpdatePreview(); _status.Text = "已批量更新 " + targets.Count + " 项门窗的安装缝、外框或分隔框设置。";
        }

        private void ApplyAutomaticSuggestions()
        {
            var targets = OperationTargets();
            foreach (var item in targets) { DoorWindowElevationSuggestionService.Apply(item); UpdateStatus(item); }
            _grid.Refresh(); UpdateSummary(); UpdatePreview();
        }

        private void LoadTemplateChoices(string selectedId = null)
        {
            _templateChoice.BeginUpdate(); _templateChoice.Items.Clear();
            foreach (var template in _templateStore.Load()) _templateChoice.Items.Add(template);
            _templateChoice.EndUpdate();
            if (_templateChoice.Items.Count == 0) return;
            var selected = _templateChoice.Items.Cast<DoorWindowElevationTemplate>().ToList().FindIndex(x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            _templateChoice.SelectedIndex = selected >= 0 ? selected : 0;
        }

        private void ApplySelectedTemplate()
        {
            _grid.EndEdit();
            var template = _templateChoice.SelectedItem as DoorWindowElevationTemplate;
            if (template == null) { MessageBox.Show(this, "请选择参数模板。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var targets = OperationTargets();
            if (targets.Count == 0) { MessageBox.Show(this, "请先勾选要应用模板的门窗。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            foreach (var item in targets) { template.ApplyTo(item); UpdateStatus(item); }
            _grid.Refresh(); UpdateSummary(); UpdatePreview();
            _status.Text = "已把模板“" + template.Name + "”应用到 " + targets.Count + " 项门窗。";
        }

        private void SaveCurrentAsTemplate()
        {
            _grid.EndEdit();
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index < 0 || _grid.CurrentRow.Index >= _rows.Count) return;
            var item = _rows[_grid.CurrentRow.Index];
            UpdateStatus(item);
            if (item.Status != "参数完整，可生成") { MessageBox.Show(this, "当前门窗参数尚未完整，不能保存为模板。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var defaultName = (item.ElevationType ?? "门窗") + "-" + (item.DivisionPreset ?? "分格") + "-" + (item.OpeningMode ?? "开启");
            var name = Microsoft.VisualBasic.Interaction.InputBox("请输入模板名称：", "当前门窗存为参数模板", defaultName).Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var existing = _templateStore.Load().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && MessageBox.Show(this, "模板“" + name + "”已经存在，是否覆盖？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var template = DoorWindowElevationTemplate.FromItem(name, item);
            if (existing != null) template.Id = existing.Id;
            _templateStore.Upsert(template); LoadTemplateChoices(template.Id);
            _status.Text = "已保存参数模板“" + name + "”。";
        }

        private void DeleteSelectedTemplate()
        {
            var template = _templateChoice.SelectedItem as DoorWindowElevationTemplate;
            if (template == null) return;
            if (MessageBox.Show(this, "确定删除参数模板“" + template.Name + "”？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _templateStore.Delete(template.Id); LoadTemplateChoices();
            _status.Text = "已删除参数模板“" + template.Name + "”。";
        }

        private void EditCurrentDivision()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index < 0 || _grid.CurrentRow.Index >= _rows.Count) return;
            var item = _rows[_grid.CurrentRow.Index];
            using (var dialog = new CustomDoorWindowDivisionForm(item))
                if (dialog.ShowDialog(this) == DialogResult.OK) { UpdateStatus(item); _grid.Refresh(); UpdateSummary(); UpdatePreview(); }
        }

        private void UpdateStatus(DoorWindowScheduleItem item)
        {
            if ((item.Status ?? string.Empty).Contains("同编号")) { item.Selected = false; return; }
            if (item.Width <= 0 || item.Height <= 0) { item.Status = "缺少洞口尺寸"; item.Selected = false; return; }
            var effectiveGap = item.HasInstallationGap ? item.InstallationGap : 0d;
            if (effectiveGap < 0 || item.Width <= effectiveGap * 2 || item.Height <= effectiveGap * 2) { item.Status = "尺寸小于安装缝"; item.Selected = false; return; }
            if (string.IsNullOrWhiteSpace(item.ElevationType) || item.ElevationType == "待确认") item.Status = "待确认门窗类型";
            else if (string.IsNullOrWhiteSpace(item.DivisionPreset) || item.DivisionPreset == "未设置") item.Status = "待设置分格";
            else if (item.DivisionPreset == "自定义")
            {
                if (DoorWindowElevationGeometryBuilder.ParseCellLayout(item.CustomCellLayout).Count > 0)
                {
                    try { DoorWindowElevationGeometryBuilder.Build(item); item.Status = "参数完整，可生成"; } catch { item.Status = "自定义分格参数无效"; }
                    return;
                }
                var columns = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomColumnRatios); var rows = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomRowRatios);
                var openings = (item.CellOpeningModes ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Count == 0 || rows.Count == 0 || openings.Length != columns.Count * rows.Count) item.Status = "待编辑自定义分格";
                else { try { DoorWindowElevationGeometryBuilder.Build(item); item.Status = "参数完整，可生成"; } catch { item.Status = "自定义分格参数无效"; } }
                return;
            }
            else if (string.IsNullOrWhiteSpace(item.OpeningMode) || item.OpeningMode == "未设置") item.Status = "待设置开启";
            else if (item.OpeningMode == "自定义") item.Status = "自定义开启将在下一版开放";
            else item.Status = "参数完整，可生成";
        }

        private void UpdateSummary()
        {
            var conflicts = _rows.Count(x => (x.Status ?? string.Empty).Contains("同编号") || (x.Status ?? string.Empty).Contains("缺少") || (x.Status ?? string.Empty).Contains("小于"));
            var ready = _rows.Count(x => x.Status == "参数完整，可生成");
            _status.Text = "读取 " + _rows.Count + " 种门窗，已勾选 " + _rows.Count(x => x.Selected) + "，参数完整 " + ready + (conflicts > 0 ? "，阻止生成 " + conflicts : string.Empty) + "。可预览、套用模板、插入或原位更新立面。";
        }

        private void SavePreferences(bool notify)
        {
            try { _grid.EndEdit(); _store.SaveForActiveProject(_rows, ParseDrawingScale()); if (notify) MessageBox.Show(this, "门窗类型、分格、开启、安装缝和出图比例已保存到当前项目。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception exception) { if (notify) MessageBox.Show(this, "保存失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

#if ACAD_R19
        private void Repick()
#else
        private async void Repick()
#endif
        {
            Hide();
            try
            {
                _document.Window.Focus(); ObjectId id = ObjectId.Null;
#if ACAD_R19
                id = PromptForTable();
#else
                await CadCommandContext.ExecuteAsync(() => id = PromptForTable());
#endif
                if (id.IsNull) return;
                DoorWindowScheduleReadResult result;
                using (_document.LockDocument()) using (var transaction = _document.Database.TransactionManager.StartTransaction())
                    result = TianzhengDoorWindowService.Read(transaction.GetObject(id, OpenMode.ForRead, false));
                LoadSource(result);
            }
            catch (Exception exception) { MessageBox.Show(this, "读取门窗表失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

        private ObjectId PromptForTable()
        {
            var prompt = new PromptEntityOptions("\n请选择天正门窗表：");
            var result = _document.Editor.GetEntity(prompt);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

        private void LocateSource()
        {
            if (_source == null || _source.SourceId.IsNull || !_source.SourceId.IsValid) return;
            try
            {
                using (_document.LockDocument())
                {
                    _document.Editor.SetImpliedSelection(new[] { _source.SourceId });
                    if (_source.HasExtents)
                    {
                        using (var view = _document.Editor.GetCurrentView())
                        {
                            var transform = Matrix3d.PlaneToWorld(view.ViewDirection);
                            transform = Matrix3d.Displacement(view.Target - Point3d.Origin) * transform;
                            transform = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * transform;
                            transform = transform.Inverse();
                            var min = _source.MinPoint.TransformBy(transform); var max = _source.MaxPoint.TransformBy(transform);
                            var width = Math.Max(Math.Abs(max.X - min.X), 1d); var height = Math.Max(Math.Abs(max.Y - min.Y), 1d);
                            var ratio = view.Height <= 1e-9 ? 1d : view.Width / view.Height;
                            if (width / height > ratio) height = width / ratio; else width = height * ratio;
                            view.CenterPoint = new Point2d((min.X + max.X) / 2d, (min.Y + max.Y) / 2d); view.Width = width * 1.25d; view.Height = height * 1.25d;
                            _document.Editor.SetCurrentView(view);
                        }
                    }
                    _document.Window.Focus();
                }
            }
            catch (Exception exception) { MessageBox.Show(this, "定位来源表失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void OpenLog()
        {
            var path = Path.Combine(UserDataPaths.LogsDirectory, "door-window-elevation.log");
            try { if (!File.Exists(path)) File.WriteAllText(path, "尚无门窗表诊断记录。"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception exception) { MessageBox.Show(this, "无法打开日志：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void UpdatePreview()
        {
            DoorWindowScheduleItem item = null;
            if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _rows.Count) item = _rows[_grid.CurrentRow.Index];
            _preview.ShowItem(item);
        }

        private void SelectFirstRow()
        {
            if (_grid.Rows.Count == 0 || _grid.Columns.Count < 2) { _preview.ShowItem(_rows.FirstOrDefault()); return; }
            _grid.ClearSelection(); _grid.Rows[0].Selected = true; _grid.CurrentCell = _grid.Rows[0].Cells[1]; UpdatePreview();
        }

#if ACAD_R19
        private void InsertElevations()
#else
        private async void InsertElevations()
#endif
        {
            _grid.EndEdit();
            var ready = _rows.Where(x => x.Selected && x.Status == "参数完整，可生成").ToList();
            if (ready.Count == 0) { MessageBox.Show(this, "请先勾选参数完整的门窗。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var scale = ParseDrawingScale(); var frame = SelectedFrame(); SavePreferences(false); Hide();
            var progress = new DoorWindowElevationProgressForm(ready.Count);
            try
            {
                var inserted = 0; progress.Show(); progress.BringToFront(); _document.Window.Focus();
#if ACAD_R19
                using (_document.LockDocument()) inserted = DoorWindowElevationInsertionService.Insert(_document, ready, scale, frame, progress.Report);
#else
                await CadCommandContext.ExecuteAsync(() => inserted = DoorWindowElevationInsertionService.Insert(_document, ready, scale, frame, progress.Report));
#endif
                progress.Close();
                if (inserted > 0) MessageBox.Show(this, "已生成 " + inserted + " 个可独立编辑的门窗立面。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { MessageBox.Show(this, "插入门窗立面失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!progress.IsDisposed) progress.Close(); progress.Dispose(); Show(); Activate(); }
        }

        private int ParseDrawingScale()
        {
            var text = (_drawingScale.Text ?? string.Empty).Trim().Replace('：', ':'); var colon = text.LastIndexOf(':'); if (colon >= 0) text = text.Substring(colon + 1);
            int value; if (!int.TryParse(text, out value) || value <= 0 || value > 10000) throw new InvalidOperationException("出图比例应填写为 1:50 或正整数。"); return value;
        }

#if ACAD_R19
        private void InsertDoorWindowSchedule()
#else
        private async void InsertDoorWindowSchedule()
#endif
        {
            _grid.EndEdit(); SavePreferences(false); Hide();
            try
            {
                _document.Window.Focus(); var inserted = 0;
#if ACAD_R19
                using (_document.LockDocument()) inserted = DoorWindowElevationInsertionService.InsertSchedule(_document, _rows.ToList(), ParseDrawingScale());
#else
                await CadCommandContext.ExecuteAsync(() => inserted = DoorWindowElevationInsertionService.InsertSchedule(_document, _rows.ToList(), ParseDrawingScale()));
#endif
                if (inserted > 0) MessageBox.Show(this, "已插入 " + inserted + " 行门窗表数据。图集名称和备注可在表格中预先修改。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { MessageBox.Show(this, "插入门窗表失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

#if ACAD_R19
        private void UpdateGeneratedElevation()
#else
        private async void UpdateGeneratedElevation()
#endif
        {
            _grid.EndEdit(); SavePreferences(false); Hide();
            try
            {
                _document.Window.Focus();
                DoorWindowElevationMetadata metadata = null; var count = 0;
#if ACAD_R19
                metadata = DoorWindowElevationMetadataService.PromptForGeneratedElevation(_document, out count);
#else
                await CadCommandContext.ExecuteAsync(() => metadata = DoorWindowElevationMetadataService.PromptForGeneratedElevation(_document, out count));
#endif
                if (metadata == null) return;
                Show(); Activate();
                var answer = MessageBox.Show(this,
                    "将原位重新生成门窗“" + (metadata.Code ?? "未编号") + "”，并替换该组 " + count + " 个插件生成对象。\n\n这些对象上的手工修改会被覆盖，是否继续？",
                    "更新已生成立面", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                Hide(); _document.Window.Focus(); var replaced = 0;
#if ACAD_R19
                using (_document.LockDocument()) replaced = DoorWindowElevationInsertionService.Update(_document, metadata, _rows.ToList(), ParseDrawingScale());
#else
                await CadCommandContext.ExecuteAsync(() => replaced = DoorWindowElevationInsertionService.Update(_document, metadata, _rows.ToList(), ParseDrawingScale()));
#endif
                if (replaced > 0) MessageBox.Show(this, "立面已按当前门窗参数和出图比例原位更新。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception) { MessageBox.Show(this, "更新门窗立面失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

        private void LoadFrameChoices()
        {
            _frameChoice.Items.Clear(); _frameChoice.Items.Add(new FrameChoice { Name = "无图框（连续排列）" });
            foreach (var frame in new PublishPlanStore().LoadFrames()) _frameChoice.Items.Add(new FrameChoice { Frame = frame, Name = frame.DisplayName });
            _frameChoice.SelectedIndex = 0;
        }

        private FrameDefinition SelectedFrame()
        { var choice = _frameChoice.SelectedItem as FrameChoice; return choice == null ? null : choice.Frame; }

        private List<DoorWindowScheduleItem> OperationTargets()
        {
            var highlighted = _grid.SelectedRows.Cast<DataGridViewRow>().Where(x => x.Index >= 0 && x.Index < _rows.Count).Select(x => _rows[x.Index]).Distinct().ToList();
            return highlighted.Count > 1 ? highlighted : _rows.Where(x => x.Selected).ToList();
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || _propagatingGridEdit) return;
            var property = _grid.Columns[e.ColumnIndex].DataPropertyName;
            var editableBatchProperty = property == "ElevationType" || property == "DivisionPreset" || property == "OpeningMode" || property == "HasInstallationGap" || property == "InstallationGap" || property == "HasOuterFrame" || property == "OuterFrameWidth" || property == "HasMullion" || property == "MullionWidth" || property == "DoorFrameType" || property == "Material" || property == "SillHeight" || property == "AtlasName" || property == "Remarks";
            if (editableBatchProperty && _grid.SelectedRows.Count > 1)
            {
                _propagatingGridEdit = true;
                try
                {
                    var source = _rows[e.RowIndex];
                    foreach (DataGridViewRow row in _grid.SelectedRows)
                    {
                        if (row.Index < 0 || row.Index >= _rows.Count || row.Index == e.RowIndex) continue;
                        var target = _rows[row.Index];
                        if (property == "ElevationType") target.ElevationType = source.ElevationType;
                        else if (property == "DivisionPreset") target.DivisionPreset = source.DivisionPreset;
                        else if (property == "OpeningMode") target.OpeningMode = source.OpeningMode;
                        else if (property == "HasInstallationGap") target.HasInstallationGap = source.HasInstallationGap;
                        else if (property == "InstallationGap") target.InstallationGap = source.InstallationGap;
                        else if (property == "HasOuterFrame") target.HasOuterFrame = source.HasOuterFrame;
                        else if (property == "OuterFrameWidth") target.OuterFrameWidth = source.OuterFrameWidth;
                        else if (property == "HasMullion") target.HasMullion = source.HasMullion;
                        else if (property == "MullionWidth") target.MullionWidth = source.MullionWidth;
                        else if (property == "DoorFrameType") target.DoorFrameType = source.DoorFrameType;
                        else if (property == "Material") target.Material = source.Material;
                        else if (property == "SillHeight") target.SillHeight = source.SillHeight;
                        else if (property == "AtlasName") target.AtlasName = source.AtlasName;
                        else if (property == "Remarks") target.Remarks = source.Remarks;
                        if (property == "ElevationType" && (target.ElevationType ?? string.Empty).Contains("窗") && target.SillHeight <= 0d) target.SillHeight = 900d;
                        NormalizeSillHeight(target);
                        if (property == "InstallationGap" || property == "HasInstallationGap") NormalizeActualSizes(target);
                        UpdateStatus(target);
                    }
                }
                finally { _propagatingGridEdit = false; }
            }
            if (property == "InstallationGap" || property == "HasInstallationGap") NormalizeActualSizes(_rows[e.RowIndex]);
            if (property == "ElevationType" && (_rows[e.RowIndex].ElevationType ?? string.Empty).Contains("窗") && _rows[e.RowIndex].SillHeight <= 0d) _rows[e.RowIndex].SillHeight = 900d;
            NormalizeSillHeight(_rows[e.RowIndex]);
            UpdateStatus(_rows[e.RowIndex]); _grid.Invalidate(); UpdateSummary(); UpdatePreview();
        }

        private static void NormalizeSillHeight(DoorWindowScheduleItem item)
        {
            if (item == null) return;
            if ((item.ElevationType ?? string.Empty).Contains("窗")) { if (item.SillHeight < 0d) item.SillHeight = 0d; }
            else item.SillHeight = 0d;
        }

        private static void NormalizeActualSizes(DoorWindowScheduleItem item)
        {
            if (item == null || item.DivisionPreset != "自定义") return;
            var layout = DoorWindowElevationGeometryBuilder.ParseCellLayout(item.CustomCellLayout);
            if (layout.Count > 0)
            {
                var oldWidth = layout.Max(x => x.Right); var oldHeight = layout.Max(x => x.Top);
                var gap = item.HasInstallationGap ? item.InstallationGap : 0d; var nextWidth = item.Width - gap * 2d; var nextHeight = item.Height - gap * 2d;
                if (oldWidth > 0 && oldHeight > 0 && nextWidth > 0 && nextHeight > 0)
                {
                    foreach (var cell in layout) { cell.Left *= nextWidth / oldWidth; cell.Right *= nextWidth / oldWidth; cell.Bottom *= nextHeight / oldHeight; cell.Top *= nextHeight / oldHeight; }
                    item.CustomCellLayout = DoorWindowElevationGeometryBuilder.SerializeCellLayout(layout);
                }
                return;
            }
            var columns = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomColumnWidths);
            if (columns.Count == 0) columns = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomColumnRatios);
            var rows = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomRowHeights);
            if (rows.Count == 0) rows = DoorWindowElevationGeometryBuilder.ParseRatios(item.CustomRowRatios);
            var effectiveGap = item.HasInstallationGap ? item.InstallationGap : 0d; var clearWidth = item.Width - effectiveGap * 2d; var clearHeight = item.Height - effectiveGap * 2d;
            if (columns.Count > 0 && clearWidth > 0) item.CustomColumnWidths = string.Join(",", DoorWindowElevationGeometryBuilder.ResolveActualSizes(null, columns, clearWidth, "列宽").Select(x => x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
            if (rows.Count > 0 && clearHeight > 0) item.CustomRowHeights = string.Join(",", DoorWindowElevationGeometryBuilder.ResolveActualSizes(null, rows, clearHeight, "行高").Select(x => x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static bool IsSelectable(DoorWindowScheduleItem item)
        {
            var status = item == null ? string.Empty : item.Status ?? string.Empty;
            return !status.Contains("同编号") && !status.Contains("缺少") && !status.Contains("小于");
        }

        private void SelectAll(bool selected) { foreach (var item in _rows) item.Selected = selected && IsSelectable(item); _grid.Refresh(); UpdateSummary(); }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(2, 7, 8, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Padding = new Padding(8, 0, 8, 0) }; }
        private static ComboBox Combo(IEnumerable<string> values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 118, Height = 28, Margin = new Padding(4, 3, 4, 0) }; box.Items.AddRange(values.Cast<object>().ToArray()); box.SelectedIndex = 0; return box; }
        private static ComboBox ScaleCombo() { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 76, Height = 28, Margin = new Padding(4, 3, 4, 0), Text = "1:50" }; box.Items.AddRange(new object[] { "1:20", "1:25", "1:30", "1:50", "1:100" }); return box; }
        private static DataGridViewComboBoxColumn ComboColumn(string header, string property, int width, IEnumerable<string> values) { var column = new DataGridViewComboBoxColumn { HeaderText = header, DataPropertyName = property, Width = width, FlatStyle = FlatStyle.Flat }; column.Items.AddRange(values.Cast<object>().ToArray()); return column; }
    }
}
