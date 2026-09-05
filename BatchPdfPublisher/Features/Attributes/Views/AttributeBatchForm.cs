using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows.Forms;
using System.ComponentModel;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class AttributeBatchForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly ComboBox _sort = new ComboBox();
        private readonly ComboBox _scope = new ComboBox();
        private readonly ComboBox _tag = new ComboBox();
        private readonly TextBox _seed = new TextBox();
        private readonly CheckBox _increment = new CheckBox();
        private readonly CheckBox _letters = new CheckBox();
        private readonly TextBox _prefix = new TextBox();
        private readonly TextBox _suffix = new TextBox();
        private readonly TextBox _tolerance = new TextBox();
        private readonly NumericUpDown _step = new NumericUpDown();
        private readonly CheckBox _reverse = new CheckBox();
        private readonly CheckBox _prefixIncrement = new CheckBox();
        private readonly CheckBox _suffixIncrement = new CheckBox();
        private readonly ComboBox _presets = new ComboBox();
        private readonly ComboBox _incrementMode = new ComboBox();
        private readonly ComboBox _incrementStart = new ComboBox();
        private readonly ComboBox _incrementPosition = new ComboBox();
        private readonly ComboBox _direction = new ComboBox();
        private readonly FlowLayoutPanel _advancedPanel = new FlowLayoutPanel();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _status = new Label();
        private readonly BindingList<AttributePreviewRow> _previewRows = new BindingList<AttributePreviewRow>();
        private List<AttributeTarget> _targets = new List<AttributeTarget>();
        private readonly HashSet<string> _registeredBlockNames;
        private readonly AttributeBatchSettings _settings;
        private readonly AttributeMarkerService _markers = new AttributeMarkerService();
        private readonly ToolTip _toolTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 450, ReshowDelay = 100 };
        private readonly HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId> _excludedAttributeIds = new HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
        private readonly Dictionary<Autodesk.AutoCAD.DatabaseServices.ObjectId, string> _manualValues = new Dictionary<Autodesk.AutoCAD.DatabaseServices.ObjectId, string>();
        private List<AttributeBatchPreset> _presetItems;
        private bool _updatingPreview;
        private bool _loadingOptions;

        public AttributeBatchForm(Document document)
        {
            _document = document;
            _settings = AttributeBatchSettings.Load();
            _presetItems = AttributePresetStore.Load();
            _registeredBlockNames = new HashSet<string>(new PublishPlanStore().LoadFrames().Select(x => x.BlockName).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            Text = "批量修改图块属性  " + WanluoArchitectureTools.ProductVersion.Display; Width = 1040; Height = 620; StartPosition = FormStartPosition.CenterParent;
            Build();
            FormClosed += (s, e) => _markers.Dispose();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, RowCount = 3, ColumnCount = 1, Padding = new Padding(8, 6, 8, 4), BackColor = System.Drawing.Color.FromArgb(247, 249, 252) };
            var schemeRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            schemeRow.Controls.Add(LabelFor("命名方案")); _presets.Width = 150; _presets.DropDownStyle = ComboBoxStyle.DropDownList; schemeRow.Controls.Add(_presets);
            var loadPreset = new Button { Text = "载入", AutoSize = true }; loadPreset.Click += (s, e) => LoadPreset(); Tip(loadPreset, "把选中的命名方案载入到起始值、递增方式、排序和前后缀设置中。"); schemeRow.Controls.Add(loadPreset);
            var savePreset = new Button { Text = "保存", AutoSize = true }; savePreset.Click += (s, e) => SavePreset(); Tip(savePreset, "把当前编号规则保存为可重复使用的命名方案。"); schemeRow.Controls.Add(savePreset);
            var deletePreset = new Button { Text = "删除", AutoSize = true }; deletePreset.Click += (s, e) => DeletePreset(); Tip(deletePreset, "删除当前选中的命名方案，不影响图块属性。"); schemeRow.Controls.Add(deletePreset);
            var select = new Button { Text = "框选图块", AutoSize = true, Margin = new Padding(18, 0, 3, 0) }; select.Click += (s, e) => SelectBlocks(); Tip(select, "返回 CAD 框选图块，读取所有不同图块定义中的属性标记和值。"); schemeRow.Controls.Add(select);
            schemeRow.Controls.Add(LabelFor("属性标记")); _tag.Width = 135; _tag.DropDownStyle = ComboBoxStyle.DropDownList; _tag.SelectedIndexChanged += (s, e) => RefreshGrid(); schemeRow.Controls.Add(_tag);
            top.Controls.Add(schemeRow, 0, 0);

            var commonRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 5, 0, 0) };
            commonRow.Controls.Add(LabelFor("固定内容")); _seed.Width = 135; _seed.TextChanged += (s, e) => RefreshGrid(); commonRow.Controls.Add(_seed);
            var useFirst = new Button { Text = "取首项", AutoSize = true }; useFirst.Click += (s, e) => UseFirstValue(); Tip(useFirst, "把排序后第一项的现有属性值作为新的起始值。"); commonRow.Controls.Add(useFirst);
            commonRow.Controls.Add(LabelFor("编号方式")); _incrementMode.Width = 205; _incrementMode.DropDownStyle = ComboBoxStyle.DropDownList; _incrementMode.MaxDropDownItems = 24;
            _incrementMode.Items.AddRange(new object[]
            {
                "不递增",
                "数字：1, 2, 3…（支持01/001）",
                "字母大写：A, B, C…",
                "字母小写：a, b, c…",
                "罗马大写：I, II, III…",
                "罗马小写：i, ii, iii…",
                "中文数字：一, 二, 三…",
                "中文大写：壹, 贰, 叁…",
                "带圈数字：①, ②, ③…",
                "括号数字：⑴, ⑵, ⑶…",
                "黑圈数字：❶, ❷, ❸…",
                "双圈数字：⓵, ⓶, ⓷…",
                "实心圈数字：➊, ➋, ➌…",
                "半角括号：(1), (2), (3)…",
                "全角括号：（1）,（2）,（3）…",
                "方括号：[1], [2], [3]…",
                "中文括号：（一）,（二）,（三）…",
                "带圈大写：Ⓐ, Ⓑ, Ⓒ…",
                "带圈小写：ⓐ, ⓑ, ⓒ…",
                "括号字母：⒜, ⒝, ⒞…",
                "半角字母：(A), (B), (C)…",
                "天干：甲, 乙, 丙…",
                "地支：子, 丑, 寅…",
                "中文序数：第一, 第二, 第三…"
            });
            var savedStyle = _settings.NumberingStyle.HasValue && _settings.NumberingStyle.Value >= 0 && _settings.NumberingStyle.Value <= (int)AttributeNumberingStyle.ChineseOrdinal
                ? _settings.NumberingStyle.Value
                : (_settings.Letters ? (int)AttributeNumberingStyle.LatinUpper : (int)AttributeNumberingStyle.Arabic);
            _incrementMode.SelectedIndex = !_settings.Increment ? 0 : savedStyle + 1; _incrementMode.SelectedIndexChanged += (s, e) => SyncIncrementMode(); commonRow.Controls.Add(_incrementMode);
            commonRow.Controls.Add(LabelFor("递增首项")); _incrementStart.Width = 88; _incrementStart.DropDownStyle = ComboBoxStyle.DropDown; _incrementStart.MaxDropDownItems = 20;
            UpdateIncrementStartItems(_settings.StartItem); _incrementStart.Enabled = _incrementMode.SelectedIndex > 0;
            _incrementStart.TextChanged += (s, e) => { if (_loadingOptions) return; _settings.StartItem = _incrementStart.Text; SaveSettings(); RefreshGrid(); };
            Tip(_incrementStart, "可从列表选择难输入的编号，也可直接输入任意首项，例如 5、01、⑦、丙或 C。"); commonRow.Controls.Add(_incrementStart);
            commonRow.Controls.Add(LabelFor("递增位置")); _incrementPosition.Width = 105; _incrementPosition.DropDownStyle = ComboBoxStyle.DropDownList; _incrementPosition.Items.AddRange(new object[] { "后缀递增", "前缀递增", "前后缀递增" });
            var incrementPosition = _settings.PrefixIncrement && _settings.SuffixIncrement ? 2 : _settings.PrefixIncrement ? 1 : 0;
            _prefixIncrement.Checked = incrementPosition != 0; _suffixIncrement.Checked = incrementPosition != 1;
            _incrementPosition.SelectedIndex = incrementPosition; _incrementPosition.Enabled = _incrementMode.SelectedIndex > 0; _incrementPosition.SelectedIndexChanged += (s, e) => SyncIncrementPosition(); commonRow.Controls.Add(_incrementPosition);
            commonRow.Controls.Add(LabelFor("方向")); _direction.Width = 90; _direction.DropDownStyle = ComboBoxStyle.DropDownList; _direction.Items.AddRange(new object[] { "正向", "反向" }); _direction.SelectedIndex = _settings.Reverse ? 1 : 0; _direction.SelectedIndexChanged += (s, e) => SyncDirection(); commonRow.Controls.Add(_direction);
            commonRow.Controls.Add(LabelFor("步长")); _step.Minimum = 1; _step.Maximum = 9999; _step.Width = 62; _step.Value = Math.Max(1, Math.Min(9999, _settings.Step)); _step.ValueChanged += (s, e) => { if (_loadingOptions) return; _settings.Step = (int)_step.Value; SaveSettings(); RefreshGrid(); }; commonRow.Controls.Add(_step);
            commonRow.Controls.Add(LabelFor("排序")); _sort.Width = 130; _sort.DropDownStyle = ComboBoxStyle.DropDownList; _sort.Items.AddRange(new object[] { "先左右后上下", "先上下后左右" }); _sort.SelectedIndex = Math.Max(0, Math.Min(1, _settings.Sort)); _sort.SelectedIndexChanged += (s, e) => { if (_loadingOptions) return; _settings.Sort = _sort.SelectedIndex; SaveSettings(); ReSort(); }; commonRow.Controls.Add(_sort);
            commonRow.Controls.Add(LabelFor("前缀")); _prefix.Width = 85; _prefix.TextChanged += (s, e) => RefreshGrid(); commonRow.Controls.Add(_prefix);
            commonRow.Controls.Add(LabelFor("后缀")); _suffix.Width = 85; _suffix.TextChanged += (s, e) => RefreshGrid(); commonRow.Controls.Add(_suffix);
            var advancedToggle = new Button { Text = "高级设置 ▼", AutoSize = true }; advancedToggle.Click += (s, e) => ToggleAdvanced(advancedToggle); Tip(advancedToggle, "展开作用范围、行列分组容差和坐标信息。"); commonRow.Controls.Add(advancedToggle);
            top.Controls.Add(commonRow, 0, 1);

            _advancedPanel.Dock = DockStyle.Top; _advancedPanel.AutoSize = true; _advancedPanel.Visible = false; _advancedPanel.Padding = new Padding(4, 5, 0, 2); _advancedPanel.BackColor = System.Drawing.Color.FromArgb(235, 240, 247);
            _advancedPanel.Controls.Add(LabelFor("作用范围")); _scope.Width = 150; _scope.DropDownStyle = ComboBoxStyle.DropDownList; _scope.Items.AddRange(new object[] { "所有框选属性图块", "仅登记图框" }); _scope.SelectedIndex = Math.Max(0, Math.Min(1, _settings.Scope)); _scope.SelectedIndexChanged += (s, e) => { _settings.Scope = _scope.SelectedIndex; SaveSettings(); }; _advancedPanel.Controls.Add(_scope);
            _advancedPanel.Controls.Add(LabelFor("行列容差")); _tolerance.Width = 75; _tolerance.Text = _settings.Tolerance ?? string.Empty; _tolerance.TextChanged += (s, e) => { if (_loadingOptions) return; _settings.Tolerance = _tolerance.Text; SaveSettings(); ReSort(); }; _advancedPanel.Controls.Add(_tolerance);
            top.Controls.Add(_advancedPanel, 0, 2);
            root.Controls.Add(top, 0, 0);
            _grid.Dock = DockStyle.Fill; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AutoGenerateColumns = false; _grid.ReadOnly = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.BackgroundColor = System.Drawing.Color.White; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None; _grid.ColumnHeadersVisible = true; _grid.ColumnHeadersHeight = 34; _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(225, 232, 242); _grid.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(31, 48, 74); _grid.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold); _grid.AlternatingRowsDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(247, 249, 252); _grid.RowHeadersVisible = false;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "写入", DataPropertyName = "Selected", Width = 52, ReadOnly = false, Frozen = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "排序序号", DataPropertyName = "Sequence", Width = 70, ReadOnly = true, Frozen = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图块名称", DataPropertyName = "BlockName", Width = 160, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性标记（TAG）", DataPropertyName = "Tag", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "修改前属性值", DataPropertyName = "OldValue", Width = 165, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NewValue", HeaderText = "修改后属性值（可编辑）", DataPropertyName = "NewValue", Width = 180, ReadOnly = false });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "检查结果", DataPropertyName = "State", Width = 90, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "X", HeaderText = "图块插入点 X", DataPropertyName = "X", Width = 110, ReadOnly = true, Visible = false });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Y", HeaderText = "图块插入点 Y", DataPropertyName = "Y", Width = 110, ReadOnly = true, Visible = false });
            _grid.DataSource = _previewRows;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => { if (!_updatingPreview && e.RowIndex >= 0 && e.ColumnIndex == 0) { RememberPreviewSelection(e.RowIndex); UpdatePreviewStates(); } };
            _grid.CellEndEdit += GridCellEndEdit;
            _grid.CellDoubleClick += GridCellDoubleClick;
            _grid.CellFormatting += GridCellFormatting;
            root.Controls.Add(_grid, 0, 1);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var apply = new Button { Text = "写入属性", AutoSize = true }; apply.Click += (s, e) => Apply(); Tip(apply, "把预览表中已勾选的修改后值写入 CAD；写入后可用 AutoCAD 撤销。"); bottom.Controls.Add(apply);
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (s, e) => Close(); Tip(close, "关闭批量属性面板并清除临时 CAD 标记。"); bottom.Controls.Add(close);
            var clearAll = new Button { Text = "取消全选", AutoSize = true }; clearAll.Click += (s, e) => SetPreviewSelection(false); Tip(clearAll, "取消所有预览行的写入勾选，保留预览内容。"); bottom.Controls.Add(clearAll);
            var selectAll = new Button { Text = "全选", AutoSize = true }; selectAll.Click += (s, e) => SetPreviewSelection(true); Tip(selectAll, "勾选当前属性标记下的全部预览行参与写入。"); bottom.Controls.Add(selectAll);
            var resetValues = new Button { Text = "恢复自动编号", AutoSize = true }; resetValues.Click += (s, e) => ResetManualValues(); Tip(resetValues, "清除表格中的手工改值，按当前起始值和递增规则重新计算。"); bottom.Controls.Add(resetValues);
            var next = new Button { Text = "下一项", AutoSize = true }; next.Click += (s, e) => LocateAdjacent(1); Tip(next, "定位排序后的下一图块，并在属性文字附近显示红色标记。"); bottom.Controls.Add(next);
            var locate = new Button { Text = "定位当前", AutoSize = true }; locate.Click += (s, e) => LocateCurrent(); Tip(locate, "缩放到预览表当前行的图块，并标出对应属性文字。"); bottom.Controls.Add(locate);
            var previous = new Button { Text = "上一项", AutoSize = true }; previous.Click += (s, e) => LocateAdjacent(-1); Tip(previous, "定位排序后的上一图块，并在属性文字附近显示红色标记。"); bottom.Controls.Add(previous);
            var more = new Button { Text = "更多 ▾", AutoSize = true }; var menu = BuildMoreMenu(); more.Click += (s, e) => menu.Show(more, 0, more.Height); Tip(more, "打开 CSV 导出、异常定位、全部序号标记和失败日志等辅助功能。"); bottom.Controls.Add(more);
            _status.AutoSize = true; _status.Margin = new Padding(8, 7, 15, 0); bottom.Controls.Add(_status);
            root.Controls.Add(bottom, 0, 2);
            RefreshPresets();
        }

        private void SelectBlocks()
        {
            Hide();
            try
            {
                _targets = AttributeBatchService.SelectTargets(_document, CurrentOrder());
                _excludedAttributeIds.Clear();
                _manualValues.Clear();
                if (_scope.SelectedIndex == 1)
                {
                    _targets = _targets.Where(x => _registeredBlockNames.Contains(x.BlockName)).ToList();
                }
                _targets = AttributeBatchService.Sort(_targets, CurrentOrder(), CurrentTolerance());
                _tag.Items.Clear(); foreach (var tag in _targets.Select(x => x.Tag).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) _tag.Items.Add(tag);
                if (_tag.Items.Count > 0) _tag.SelectedIndex = 0;
                else MessageBox.Show(this, _scope.SelectedIndex == 1 ? "选中对象中没有与当前工程登记规则匹配的属性图框。请确认图框块已在当前工程登记。" : "选中对象中没有可修改的块属性。", "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { Show(); Activate(); }
        }

        private AttributeSortOrder CurrentOrder() => _sort.SelectedIndex == 1 ? AttributeSortOrder.TopThenLeft : AttributeSortOrder.LeftThenTop;
        private void ReSort() { _targets = AttributeBatchService.Sort(_targets, CurrentOrder(), CurrentTolerance()); RefreshGrid(); }
        private double? CurrentTolerance()
        {
            if (string.IsNullOrWhiteSpace(_tolerance.Text)) return null;
            if (double.TryParse(_tolerance.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= 0d) return value;
            return null;
        }
        private void UseFirstValue()
        {
            var tag = _tag.SelectedItem as string;
            var first = _targets.FirstOrDefault(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (first != null) _seed.Text = first.OldValue ?? string.Empty;
        }
        private void RefreshGrid()
        {
            if (_loadingOptions) return;
            var tag = _tag.SelectedItem as string;
            var targets = _targets.Where(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase)).ToList();
            _grid.SuspendLayout();
            _previewRows.RaiseListChangedEvents = false; _previewRows.Clear();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var style = CurrentNumberingStyle();
                var automaticValue = AttributeBatchService.BuildComposedValue(_seed.Text, _prefix.Text, _suffix.Text, _incrementStart.Text, i,
                    _increment.Checked, _prefixIncrement.Checked, _suffixIncrement.Checked, style, (int)_step.Value, _reverse.Checked);
                target.NewValue = _manualValues.TryGetValue(target.AttributeId, out var manualValue) ? manualValue : automaticValue;
                _previewRows.Add(new AttributePreviewRow { Selected = !_excludedAttributeIds.Contains(target.AttributeId), Target = target, Sequence = i + 1, BlockName = target.BlockName, Tag = target.Tag, OldValue = target.OldValue, NewValue = target.NewValue, X = target.Center.X.ToString("0.###"), Y = target.Center.Y.ToString("0.###") });
            }
            _previewRows.RaiseListChangedEvents = true; _previewRows.ResetBindings();
            _grid.ResumeLayout(false);
            UpdatePreviewStates();
            _tolerance.BackColor = !string.IsNullOrWhiteSpace(_tolerance.Text) && !CurrentTolerance().HasValue ? System.Drawing.Color.MistyRose : System.Drawing.Color.White;
            _incrementStart.BackColor = _increment.Checked && !AttributeBatchService.IsNumberingStartItem(_incrementStart.Text, CurrentNumberingStyle())
                ? System.Drawing.Color.MistyRose : System.Drawing.Color.White;
        }
        private void Apply()
        {
            _grid.EndEdit();
            SyncPreviewValues();
            RemoveInvalidTargets(true);
            var selectedRows = _previewRows.Where(x => x.Selected && x.Target != null).ToList();
            var rows = selectedRows.Select(x => x.Target).ToList();
            if (rows.Count == 0) return;
            var emptyCount = rows.Count(x => string.IsNullOrWhiteSpace(x.NewValue));
            var duplicateCount = HasActiveIncrement()
                ? rows.Where(x => !string.IsNullOrWhiteSpace(x.NewValue)).GroupBy(x => x.NewValue, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Sum(x => x.Count())
                : 0;
            if (emptyCount > 0 && MessageBox.Show(this, "勾选项中有 " + emptyCount + " 个新值为空，写入后会清空对应属性。是否继续？", "批量属性检查", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (duplicateCount > 0 && MessageBox.Show(this, "递增结果中有 " + duplicateCount + " 个属性值重复。请确认步长、起始值或手工修改是否正确。仍要继续吗？", "批量属性检查", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (MessageBox.Show(this, "确认写入预览中的属性值吗？本次操作可用 AutoCAD 撤销。", "批量属性", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var result = AttributeBatchService.Apply(_document, rows);
            foreach (var row in rows.Where(x => x != null && result.ChangedAttributeIds.Contains(x.AttributeId)))
            {
                row.OldValue = row.NewValue ?? string.Empty;
                _manualValues.Remove(row.AttributeId);
            }
            var message = "已修改 " + result.Changed + " 个属性，跳过 " + result.Skipped + " 个未变化属性。" + (result.Failed > 0 ? "\r\n失败 " + result.Failed + " 个，详情已写入临时日志。" : string.Empty);
            MessageBox.Show(this, message, "批量属性", MessageBoxButtons.OK, result.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            RefreshGrid();
        }

        private void SaveSettings() { _settings.Save(); }
        private void Tip(Control control, string text) { _toolTip.SetToolTip(control, text); }
        private static Label LabelFor(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(5, 7, 0, 0) };
        private void SyncIncrementMode()
        {
            _increment.Checked = _incrementMode.SelectedIndex > 0;
            _letters.Checked = CurrentNumberingStyle() == AttributeNumberingStyle.LatinUpper || CurrentNumberingStyle() == AttributeNumberingStyle.LatinLower;
            _incrementPosition.Enabled = _increment.Checked;
            _incrementStart.Enabled = _increment.Checked;
            UpdateIncrementStartItems(_incrementStart.Text);
            if (_loadingOptions) return;
            _settings.Increment = _increment.Checked; _settings.Letters = _letters.Checked; _settings.NumberingStyle = (int)CurrentNumberingStyle(); SaveSettings(); RefreshGrid();
        }
        private void SyncIncrementPosition()
        {
            var prefix = _incrementPosition.SelectedIndex == 1 || _incrementPosition.SelectedIndex == 2;
            var suffix = _incrementPosition.SelectedIndex == 0 || _incrementPosition.SelectedIndex == 2;
            _prefixIncrement.Checked = prefix;
            _suffixIncrement.Checked = suffix;
            if (_loadingOptions) return;
            _settings.PrefixIncrement = prefix;
            _settings.SuffixIncrement = suffix;
            SaveSettings();
            RefreshGrid();
        }
        private AttributeNumberingStyle CurrentNumberingStyle()
        {
            var value = Math.Max(0, _incrementMode.SelectedIndex - 1);
            return (AttributeNumberingStyle)Math.Min((int)AttributeNumberingStyle.ChineseOrdinal, value);
        }
        private void UpdateIncrementStartItems(string selected)
        {
            selected = selected ?? string.Empty;
            var style = CurrentNumberingStyle();
            var choices = AttributeBatchService.GetNumberingStartItems(style);
            if (!AttributeBatchService.IsNumberingStartItem(selected, style))
                selected = choices.FirstOrDefault() ?? string.Empty;
            _incrementStart.BeginUpdate();
            try
            {
                _incrementStart.Items.Clear();
                foreach (var item in choices)
                    _incrementStart.Items.Add(item);
                _incrementStart.Text = selected;
            }
            finally { _incrementStart.EndUpdate(); }
        }
        private void SyncDirection()
        {
            _reverse.Checked = _direction.SelectedIndex == 1;
            if (_loadingOptions) return;
            _settings.Reverse = _reverse.Checked; SaveSettings(); RefreshGrid();
        }
        private void ToggleAdvanced(Button button)
        {
            _advancedPanel.Visible = !_advancedPanel.Visible; button.Text = _advancedPanel.Visible ? "高级设置 ▲" : "高级设置 ▼";
            _grid.Columns["X"].Visible = _advancedPanel.Visible; _grid.Columns["Y"].Visible = _advancedPanel.Visible;
        }
        private ContextMenuStrip BuildMoreMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("导出预览 CSV", null, (s, e) => ExportPreview());
            menu.Items.Add("定位下一异常", null, (s, e) => LocateNextWarning());
            menu.Items.Add("显示全部序号", null, (s, e) => ShowAllOrderMarkers());
            menu.Items.Add("清除 CAD 标记", null, (s, e) => _markers.Clear());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开失败日志", null, (s, e) => OpenFailureLog());
            return menu;
        }
        private void LocateCurrent()
        {
            if (_grid.CurrentRow == null) return;
            GridCellDoubleClick(this, new DataGridViewCellEventArgs(_grid.CurrentCell?.ColumnIndex ?? 0, _grid.CurrentRow.Index));
        }
        private void ShowAllOrderMarkers()
        {
            try { _markers.ShowOrder(_document, _previewRows.Where(x => x.Selected && x.Target != null).Select(x => x.Target)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void RefreshPresets()
        {
            var selected = _settings.LastPreset;
            _presets.Items.Clear(); foreach (var preset in _presetItems.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) _presets.Items.Add(preset.Name);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var index = _presets.Items.Cast<string>().ToList().FindIndex(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _presets.SelectedIndex = index;
            }
            if (_presets.SelectedIndex < 0 && _presets.Items.Count > 0) _presets.SelectedIndex = 0;
        }
        private void LoadPreset()
        {
            var name = _presets.SelectedItem as string;
            var preset = _presetItems.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;
            _loadingOptions = true;
            try
            {
                _seed.Text = preset.Seed ?? string.Empty; _prefix.Text = preset.Prefix ?? string.Empty; _suffix.Text = preset.Suffix ?? string.Empty;
                _increment.Checked = preset.Increment; _letters.Checked = preset.Letters; _reverse.Checked = preset.Reverse;
                var presetStyle = preset.NumberingStyle.HasValue && preset.NumberingStyle.Value >= 0 && preset.NumberingStyle.Value <= (int)AttributeNumberingStyle.ChineseOrdinal
                    ? preset.NumberingStyle.Value
                    : (preset.Letters ? (int)AttributeNumberingStyle.LatinUpper : (int)AttributeNumberingStyle.Arabic);
                _incrementMode.SelectedIndex = !preset.Increment ? 0 : presetStyle + 1; _direction.SelectedIndex = preset.Reverse ? 1 : 0;
                UpdateIncrementStartItems(preset.StartItem);
                var presetPosition = preset.PrefixIncrement && preset.SuffixIncrement ? 2 : preset.PrefixIncrement ? 1 : 0;
                _prefixIncrement.Checked = presetPosition != 0; _suffixIncrement.Checked = presetPosition != 1;
                _incrementPosition.SelectedIndex = presetPosition; _incrementPosition.Enabled = preset.Increment;
                _step.Value = Math.Max(_step.Minimum, Math.Min(_step.Maximum, preset.Step));
                _sort.SelectedIndex = Math.Max(0, Math.Min(1, preset.Sort)); _tolerance.Text = preset.Tolerance ?? string.Empty;
                _settings.Increment = preset.Increment; _settings.Letters = preset.Letters; _settings.NumberingStyle = presetStyle; _settings.StartItem = preset.StartItem; _settings.Reverse = preset.Reverse;
                _settings.PrefixIncrement = _prefixIncrement.Checked; _settings.SuffixIncrement = _suffixIncrement.Checked;
                _settings.Step = (int)_step.Value; _settings.Sort = _sort.SelectedIndex; _settings.Tolerance = _tolerance.Text;
                _settings.LastPreset = preset.Name; SaveSettings(); _manualValues.Clear();
            }
            finally { _loadingOptions = false; ReSort(); }
        }
        private void SavePreset()
        {
            var name = Microsoft.VisualBasic.Interaction.InputBox("请输入方案名称：", "保存命名方案", _presets.SelectedItem as string ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            var preset = _presetItems.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null) { preset = new AttributeBatchPreset(); _presetItems.Add(preset); }
            preset.Name = name; preset.Seed = _seed.Text; preset.Prefix = _prefix.Text; preset.Suffix = _suffix.Text; preset.StartItem = _incrementStart.Text; preset.Increment = _increment.Checked; preset.PrefixIncrement = _prefixIncrement.Checked; preset.SuffixIncrement = _suffixIncrement.Checked; preset.Letters = _letters.Checked; preset.NumberingStyle = (int)CurrentNumberingStyle(); preset.Reverse = _reverse.Checked; preset.Step = (int)_step.Value; preset.Sort = _sort.SelectedIndex; preset.Tolerance = _tolerance.Text;
            AttributePresetStore.Save(_presetItems); _settings.LastPreset = name; SaveSettings(); RefreshPresets();
        }
        private void DeletePreset()
        {
            var name = _presets.SelectedItem as string; if (string.IsNullOrWhiteSpace(name)) return;
            if (MessageBox.Show(this, "确认删除命名方案“" + name + "”吗？", "批量属性", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _presetItems.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); AttributePresetStore.Save(_presetItems);
            if (string.Equals(_settings.LastPreset, name, StringComparison.OrdinalIgnoreCase)) { _settings.LastPreset = string.Empty; SaveSettings(); }
            RefreshPresets();
        }
        private void SetPreviewSelection(bool selected)
        {
            foreach (var row in _previewRows.Where(x => x.Target != null))
            {
                row.Selected = selected;
                if (selected) _excludedAttributeIds.Remove(row.Target.AttributeId);
                else _excludedAttributeIds.Add(row.Target.AttributeId);
            }
            UpdatePreviewStates();
        }
        private void RememberPreviewSelection(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _previewRows.Count) return;
            var row = _previewRows[rowIndex];
            if (row.Target == null) return;
            if (row.Selected) _excludedAttributeIds.Remove(row.Target.AttributeId);
            else _excludedAttributeIds.Add(row.Target.AttributeId);
        }
        private void ResetManualValues()
        {
            foreach (var row in _previewRows.Where(x => x.Target != null)) _manualValues.Remove(row.Target.AttributeId);
            RefreshGrid();
        }
        private void GridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "NewValue") return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as AttributePreviewRow;
            if (row?.Target == null) return;
            row.NewValue = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
            row.Target.NewValue = row.NewValue;
            _manualValues[row.Target.AttributeId] = row.NewValue;
            UpdatePreviewStates();
        }
        private void SyncPreviewValues()
        {
            foreach (var row in _previewRows.Where(x => x.Target != null)) row.Target.NewValue = row.NewValue ?? string.Empty;
        }
        private void UpdatePreviewStates()
        {
            if (_updatingPreview) return;
            _updatingPreview = true;
            try
            {
            var duplicates = HasActiveIncrement()
                ? new HashSet<string>(_previewRows.Where(x => x.Selected && !string.IsNullOrWhiteSpace(x.NewValue)).GroupBy(x => x.NewValue, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _previewRows)
            {
                if (!row.Selected) row.State = "未选";
                else if (string.IsNullOrWhiteSpace(row.NewValue)) row.State = "空值";
                else if (string.Equals(row.OldValue ?? string.Empty, row.NewValue ?? string.Empty, StringComparison.Ordinal)) row.State = "未变化";
                else if (duplicates.Contains(row.NewValue)) row.State = "重复";
                else row.State = "将修改";
            }
            _previewRows.ResetBindings(); _grid.Invalidate();
            UpdateStatusText();
            }
            finally { _updatingPreview = false; }
        }
        private void UpdateStatusText()
        {
            var tag = _tag.SelectedItem as string;
            var toleranceText = string.IsNullOrWhiteSpace(_tolerance.Text) ? "自动容差" : (CurrentTolerance().HasValue ? "容差 " + _tolerance.Text : "容差输入无效，已自动判断");
            var changed = _previewRows.Count(x => x.Selected && x.State == "将修改");
            var unchanged = _previewRows.Count(x => x.Selected && x.State == "未变化");
            var warnings = _previewRows.Count(x => x.Selected && (x.State == "空值" || x.State == "重复"));
            _status.Text = string.IsNullOrWhiteSpace(tag) ? "请先框选图块" : "已选 " + _previewRows.Count(x => x.Selected) + "/" + _previewRows.Count + " · 修改 " + changed + " · 未变 " + unchanged + " · 异常 " + warnings + " · " + toleranceText;
        }
        private bool HasActiveIncrement() => _increment.Checked;
        private void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as AttributePreviewRow; if (row == null) return;
            var color = row.State == "空值" ? System.Drawing.Color.MistyRose : row.State == "重复" ? System.Drawing.Color.LemonChiffon : row.State == "将修改" ? System.Drawing.Color.Honeydew : row.State == "未选" ? System.Drawing.Color.White : System.Drawing.Color.WhiteSmoke;
            e.CellStyle.BackColor = color;
        }
        private void LocateNextWarning()
        {
            var warningRows = _previewRows.Select((row, index) => new { row, index }).Where(x => x.row.Selected && (x.row.State == "空值" || x.row.State == "重复")).ToList();
            if (warningRows.Count == 0) { MessageBox.Show(this, "当前预览没有空值或重复值。", "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var current = _grid.CurrentRow?.Index ?? -1; var next = warningRows.FirstOrDefault(x => x.index > current) ?? warningRows[0];
            _grid.ClearSelection(); _grid.Rows[next.index].Selected = true; _grid.CurrentCell = _grid.Rows[next.index].Cells[Math.Min(1, _grid.Columns.Count - 1)];
            GridCellDoubleClick(this, new DataGridViewCellEventArgs(_grid.CurrentCell.ColumnIndex, next.index));
        }
        private void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as AttributePreviewRow;
            if (row?.Target == null) return;
            if (!AttributeBatchService.IsUsable(row.Target.BlockId) || !AttributeBatchService.IsUsable(row.Target.AttributeId))
            {
                RemoveInvalidTargets(true);
                return;
            }
            try
            {
                var highlightId = row.Target.AttributeId.IsValid ? row.Target.AttributeId : row.Target.BlockId;
                _document.Editor.SetImpliedSelection(new[] { highlightId });
                _markers.ShowCurrent(_document, row.Target);
                using (var view = _document.Editor.GetCurrentView())
                {
                    var width = Math.Max(row.Target.MaxPoint.X - row.Target.MinPoint.X, 1d);
                    var height = Math.Max(row.Target.MaxPoint.Y - row.Target.MinPoint.Y, 1d);
                    var centerX = (row.Target.MinPoint.X + row.Target.MaxPoint.X) * 0.5d;
                    var centerY = (row.Target.MinPoint.Y + row.Target.MaxPoint.Y) * 0.5d;
                    var viewRatio = view.Height <= 1e-9 ? 1d : view.Width / view.Height;
                    var objectRatio = width / height;
                    if (objectRatio > viewRatio) height = width / viewRatio; else width = height * viewRatio;
                    view.CenterPoint = new Autodesk.AutoCAD.Geometry.Point2d(centerX, centerY);
                    view.Width = width * 1.12d;
                    view.Height = height * 1.12d;
                    _document.Editor.SetCurrentView(view);
                }
                _document.Window.Focus();
            }
            catch (Exception ex) { MessageBox.Show(this, "无法定位该图块：" + ex.Message, "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void OpenFailureLog()
        {
            var path = System.IO.Path.Combine(UserDataPaths.LogsDirectory, "BatchPdfPublisher.attribute.log");
            if (!System.IO.File.Exists(path)) { MessageBox.Show(this, "当前没有属性修改失败日志。", "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开日志：" + ex.Message, "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private void ExportPreview()
        {
            _grid.EndEdit(); SyncPreviewValues();
            if (_previewRows.Count == 0) { MessageBox.Show(this, "当前没有可导出的预览数据。", "批量属性", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var choice = MessageBox.Show(this, "选择“是”只导出已勾选行；选择“否”导出当前预览中的全部行。", "导出预览 CSV", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            var rows = choice == DialogResult.Yes ? _previewRows.Where(x => x.Selected).ToList() : _previewRows.ToList();
            var lines = new List<string> { "写入,排序序号,图块名称,属性标记,修改前属性值,修改后属性值,检查结果,图块插入点X,图块插入点Y" };
            lines.AddRange(rows.Select(x => CsvExportService.Cell(x.Selected ? "是" : "否") + "," + x.Sequence + "," + CsvExportService.Cell(x.BlockName) + "," + CsvExportService.Cell(x.Tag) + "," + CsvExportService.Cell(x.OldValue) + "," + CsvExportService.Cell(x.NewValue) + "," + CsvExportService.Cell(x.State) + "," + CsvExportService.Cell(x.X) + "," + CsvExportService.Cell(x.Y)));
            if (CsvExportService.Save(this, "属性修改预览_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv", lines, out var path)) CsvExportService.Reveal(path);
        }
        private void LocateAdjacent(int direction)
        {
            if (_previewRows.Count == 0) return;
            var index = _grid.CurrentRow?.Index ?? (direction > 0 ? -1 : 0);
            index = (index + direction + _previewRows.Count) % _previewRows.Count;
            _grid.ClearSelection();
            _grid.Rows[index].Selected = true;
            _grid.CurrentCell = _grid.Rows[index].Cells[Math.Min(1, _grid.Columns.Count - 1)];
            GridCellDoubleClick(this, new DataGridViewCellEventArgs(_grid.CurrentCell.ColumnIndex, index));
        }
        private void LocateTarget(AttributeTarget target)
        {
            if (target == null) return;
            var rowIndex = _previewRows.ToList().FindIndex(x => x.Target != null && x.Target.AttributeId == target.AttributeId);
            if (rowIndex >= 0)
            {
                _grid.ClearSelection(); _grid.Rows[rowIndex].Selected = true; _grid.CurrentCell = _grid.Rows[rowIndex].Cells[Math.Min(1, _grid.Columns.Count - 1)];
                GridCellDoubleClick(this, new DataGridViewCellEventArgs(_grid.CurrentCell.ColumnIndex, rowIndex));
                return;
            }
            try
            {
                _document.Editor.SetImpliedSelection(new[] { target.AttributeId.IsValid ? target.AttributeId : target.BlockId });
                _markers.ShowCurrent(_document, target);
                _document.Window.Focus();
            }
            catch { }
        }

        private void RemoveInvalidTargets(bool notify)
        {
            var invalid = _targets.Where(x => x == null || !AttributeBatchService.IsUsable(x.BlockId) || !AttributeBatchService.IsUsable(x.AttributeId)).ToList();
            if (invalid.Count == 0) return;
            foreach (var target in invalid)
            {
                _targets.Remove(target);
                if (target != null)
                {
                    _manualValues.Remove(target.AttributeId);
                    _excludedAttributeIds.Remove(target.AttributeId);
                }
            }
            RefreshGrid();
            if (notify) MessageBox.Show(this, "有 " + invalid.Count + " 个图块或属性在面板打开期间被删除、重定义或同步，已从当前预览安全移除。请重新框选以读取最新对象。", "批量属性已更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private sealed class AttributePreviewRow
        {
            public bool Selected { get; set; }
            public AttributeTarget Target { get; set; }
            public int Sequence { get; set; }
            public string BlockName { get; set; }
            public string Tag { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
            public string X { get; set; }
            public string Y { get; set; }
            public string State { get; set; }
        }
    }
}
