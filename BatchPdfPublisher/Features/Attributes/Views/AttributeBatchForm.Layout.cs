using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed partial class AttributeBatchForm
    {
        private Label _selectedCount;
        private RowStyle _advancedRow;
        private RowStyle _advancedContentRow;
        private TableLayoutPanel _ruleSidebar;
        private void Build()
        {
            BackColor = CadDialogTheme.Canvas;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty, Padding = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);
            root.Controls.Add(TitleBar(), 0, 0);
            root.Controls.Add(BuildHeader(), 0, 1);
            root.Controls.Add(BuildMainArea(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);
            ConfigureRules();
            ConfigureGrid();
            RefreshPresets();
            UpdateStatusText();
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(12, 14, 12, 14), Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
            header.Controls.Add(CadAttributeUi.Label("批改属性", true), 0, 0);
            var drawing = Path.GetFileName(_document.Name);
            header.Controls.Add(CadAttributeUi.Label("当前图纸：" + drawing), 1, 0);
            var select = Command("框选图块", SelectBlocks, PublisherForm.UiIcon.Select, true);
            select.Margin = new Padding(0, 0, 10, 0);
            header.Controls.Add(select, 2, 0);
            _selectedCount = CadAttributeUi.Label("已选 0 个属性");
            _selectedCount.ForeColor = CadDialogTheme.Text;
            header.Controls.Add(_selectedCount, 3, 0);
            header.Controls.Add(CadAttributeUi.Label("属性标记"), 4, 0);
            CadAttributeUi.Combo(_tag);
            _tag.SelectedIndexChanged += (sender, args) => RefreshGrid();
            header.Controls.Add(_tag, 5, 0);
            return header;
        }

        private Control BuildPresetBar()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 6,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty,
                Padding = new Padding(0, 10, 0, 10) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.Controls.Add(CadAttributeUi.Label("命名方案"), 0, 0);
            CadAttributeUi.Combo(_presets);
            _presets.Margin = new Padding(0, 0, 8, 0);
            bar.Controls.Add(_presets, 1, 0);
            bar.Controls.Add(PresetButton("载入", LoadPreset, PublisherForm.UiIcon.Document), 2, 0);
            bar.Controls.Add(PresetButton("保存", SavePreset, PublisherForm.UiIcon.Save), 3, 0);
            bar.Controls.Add(PresetButton("删除", DeletePreset, PublisherForm.UiIcon.Trash), 4, 0);
            return bar;
        }

        private Control PresetButton(string text, Action action, PublisherForm.UiIcon icon)
        {
            var button = Command(text, action, icon);
            button.Margin = new Padding(0, 0, 8, 0);
            return button;
        }

        private Control BuildMainArea()
        {
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Margin = new Padding(12, 8, 12, 8), BackColor = CadDialogTheme.Canvas };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(BuildPresetBar(), 0, 0);
            left.Controls.Add(BuildPreviewCard(), 0, 1);
            main.Controls.Add(left, 0, 0);
            main.Controls.Add(BuildRuleCard(), 2, 0);
            return main;
        }

        private Control BuildPreviewCard()
        {
            var card = CadDialogTheme.Card();
            card.Padding = new Padding(8);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(CadAttributeUi.Label("修改预览", true), 0, 0);
            var gridHost = new CadAttributeGridHost(_grid) { Dock = DockStyle.Fill };
            content.Controls.Add(gridHost, 0, 1);
            card.Controls.Add(content);
            return card;
        }

        private Control BuildRuleCard()
        {
            _ruleSidebar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false,
                MinimumSize = new Size(0, 512), ColumnCount = 1, RowCount = 4,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
            _ruleSidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 358));
            _ruleSidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            _advancedRow = new RowStyle(SizeType.Absolute, 146);
            _ruleSidebar.RowStyles.Add(_advancedRow);
            _ruleSidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var ruleCard = CadDialogTheme.Card();
            ruleCard.Padding = new Padding(7);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            AddRuleRow(fields, CadAttributeUi.Label("编号规则", true), 36);
            AddRuleRow(fields, InlineRuleWithAction("固定内容", CadAttributeUi.Input(_seed),
                RuleButton("取首项", UseFirstValue, PublisherForm.UiIcon.Document)), 44);
            AddRuleRow(fields, InlineRule("编号方式", CadAttributeUi.Combo(_incrementMode)), 44);
            AddRuleRow(fields, InlineRule("递增首项", CadAttributeUi.Combo(_incrementStart, true)), 44);
            AddRuleRow(fields, InlineRule("递增位置", CadAttributeUi.Combo(_incrementPosition)), 44);
            AddRuleRow(fields, InlineRulePair("方向", CadAttributeUi.Combo(_direction),
                "步长", CadAttributeUi.Input(_stepText)), 44);
            AddRuleRow(fields, InlineRule("排序", CadAttributeUi.Combo(_sort)), 44);
            AddRuleRow(fields, InlineRulePair("前缀", CadAttributeUi.Input(_prefix),
                "后缀", CadAttributeUi.Input(_suffix)), 44);
            ruleCard.Controls.Add(fields);
            _ruleSidebar.Controls.Add(ruleCard, 0, 0);

            var advancedCard = CadDialogTheme.Card();
            advancedCard.Padding = new Padding(7);
            var advancedContent = new TableLayoutPanel { Dock = DockStyle.Fill,
                ColumnCount = 1, RowCount = 2, BackColor = CadDialogTheme.Surface,
                Margin = Padding.Empty };
            advancedContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            _advancedContentRow = new RowStyle(SizeType.Absolute, 88);
            advancedContent.RowStyles.Add(_advancedContentRow);
            var advancedToggle = Command("高级设置 ▲", null, PublisherForm.UiIcon.Gear);
            advancedToggle.Click += (sender, args) => ToggleAdvanced(advancedToggle);
            var toggleHost = new Panel { Dock = DockStyle.Fill, BackColor = CadDialogTheme.Surface,
                Padding = new Padding(0, 4, 0, 4), Margin = Padding.Empty };
            toggleHost.Controls.Add(advancedToggle);
            advancedContent.Controls.Add(toggleHost, 0, 0);
            _advancedPanel.AutoSize = false;
            _advancedPanel.RowCount = 2;
            _advancedPanel.ColumnCount = 1;
            _advancedPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            _advancedPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            _advancedPanel.Dock = DockStyle.Fill;
            _advancedPanel.BackColor = CadDialogTheme.Surface;
            _advancedPanel.Padding = Padding.Empty;
            _advancedPanel.Margin = Padding.Empty;
            _advancedPanel.Controls.Add(InlineRule("作用范围", CadAttributeUi.Combo(_scope)), 0, 0);
            _advancedPanel.Controls.Add(InlineRule("行列容差", CadAttributeUi.Input(_tolerance)), 0, 1);
            _advancedPanel.Visible = true;
            advancedContent.Controls.Add(_advancedPanel, 0, 1);
            advancedCard.Controls.Add(advancedContent);
            _ruleSidebar.Controls.Add(advancedCard, 0, 2);
            return new CadScrollHost(_ruleSidebar) { Dock = DockStyle.Fill,
                BackColor = CadDialogTheme.Canvas, Margin = Padding.Empty };
        }

        private CadRoundedButton RuleButton(string text, Action action, PublisherForm.UiIcon icon)
        {
            var button = Command(text, action, icon);
            button.Dock = DockStyle.Fill;
            button.Margin = Padding.Empty;
            return button;
        }

        private static Control InlineRule(string label, Control editor, int labelWidth = 96)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Padding = new Padding(0, 4, 0, 4), Margin = Padding.Empty,
                BackColor = CadDialogTheme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(CadAttributeUi.Label(label), 0, 0);
            editor.Dock = DockStyle.Fill;
            editor.Margin = Padding.Empty;
            row.Controls.Add(editor, 1, 0);
            return row;
        }

        private static Control InlineRuleWithAction(string label, Control editor, Control action)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
                Padding = new Padding(0, 4, 0, 4), Margin = Padding.Empty,
                BackColor = CadDialogTheme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            row.Controls.Add(CadAttributeUi.Label(label), 0, 0);
            row.Controls.Add(editor, 1, 0);
            row.Controls.Add(action, 3, 0);
            return row;
        }

        private static Control InlineRulePair(string firstLabel, Control first,
            string secondLabel, Control second)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1,
                Padding = new Padding(0, 4, 0, 4), Margin = Padding.Empty,
                BackColor = CadDialogTheme.Surface };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.Controls.Add(CadAttributeUi.Label(firstLabel), 0, 0);
            row.Controls.Add(first, 1, 0);
            row.Controls.Add(CadAttributeUi.Label(secondLabel), 3, 0);
            row.Controls.Add(second, 4, 0);
            return row;
        }

        private static void AddRuleRow(TableLayoutPanel table, Control control, int height)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            control.Margin = Padding.Empty;
            table.Controls.Add(control, 0, row);
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Padding = new Padding(10, 11, 6, 11), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var width in new[] { 94, 112, 94, 76, 94, 136, 76, 140, 76 })
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width + 6));
            _status.AutoSize = false;
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = CadDialogTheme.Muted;
            _status.AutoEllipsis = true;
            _status.Margin = Padding.Empty;
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(FooterButton("上一项", () => LocateAdjacent(-1), PublisherForm.UiIcon.Up), 1, 0);
            footer.Controls.Add(FooterButton("定位当前", LocateCurrent, PublisherForm.UiIcon.Select), 2, 0);
            footer.Controls.Add(FooterButton("下一项", () => LocateAdjacent(1), PublisherForm.UiIcon.Down), 3, 0);
            footer.Controls.Add(FooterButton("全选", () => SetPreviewSelection(true), PublisherForm.UiIcon.List), 4, 0);
            footer.Controls.Add(FooterButton("取消全选", () => SetPreviewSelection(false)), 5, 0);
            footer.Controls.Add(FooterButton("恢复自动编号", ResetManualValues), 6, 0);
            var more = FooterButton("更多", null, PublisherForm.UiIcon.List);
            more.Click += (sender, args) => ShowMoreMenu(more);
            footer.Controls.Add(more, 7, 0);
            footer.Controls.Add(FooterButton("写入属性", Apply, PublisherForm.UiIcon.Publish, true), 8, 0);
            footer.Controls.Add(FooterButton("关闭", Close), 9, 0);
            return footer;
        }

        private CadRoundedButton FooterButton(string text, Action action,
            PublisherForm.UiIcon? icon = null, bool primary = false)
        {
            var button = Command(text, action, icon, primary);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(6, 0, 0, 0);
            return button;
        }

        private void ConfigureRules()
        {
            _seed.TextChanged += (sender, args) => RefreshGrid();
            _prefix.TextChanged += (sender, args) => RefreshGrid();
            _suffix.TextChanged += (sender, args) => RefreshGrid();
            _incrementMode.Items.AddRange(new object[]
            {
                "不递增", "数字：1, 2, 3…（支持01/001）", "字母大写：A, B, C…", "字母小写：a, b, c…",
                "罗马大写：I, II, III…", "罗马小写：i, ii, iii…", "中文数字：一, 二, 三…",
                "中文大写：壹, 贰, 叁…", "带圈数字：①, ②, ③…", "括号数字：⑴, ⑵, ⑶…",
                "黑圈数字：❶, ❷, ❸…", "双圈数字：⓵, ⓶, ⓷…", "实心圈数字：➊, ➋, ➌…",
                "半角括号：(1), (2), (3)…", "全角括号：（1）,（2）,（3）…", "方括号：[1], [2], [3]…",
                "中文括号：（一）,（二）,（三）…", "带圈大写：Ⓐ, Ⓑ, Ⓒ…", "带圈小写：ⓐ, ⓑ, ⓒ…",
                "括号字母：⒜, ⒝, ⒞…", "半角字母：(A), (B), (C)…", "天干：甲, 乙, 丙…",
                "地支：子, 丑, 寅…", "中文序数：第一, 第二, 第三…"
            });
            var savedStyle = _settings.NumberingStyle.HasValue && _settings.NumberingStyle.Value >= 0
                && _settings.NumberingStyle.Value <= (int)AttributeNumberingStyle.ChineseOrdinal
                ? _settings.NumberingStyle.Value
                : (_settings.Letters ? (int)AttributeNumberingStyle.LatinUpper : (int)AttributeNumberingStyle.Arabic);
            _incrementMode.SelectedIndex = !_settings.Increment ? 0 : savedStyle + 1;
            _increment.Checked = _incrementMode.SelectedIndex > 0;
            _letters.Checked = savedStyle == (int)AttributeNumberingStyle.LatinUpper
                || savedStyle == (int)AttributeNumberingStyle.LatinLower;
            _reverse.Checked = _settings.Reverse;
            _incrementMode.SelectedIndexChanged += (sender, args) => SyncIncrementMode();
            UpdateIncrementStartItems(_settings.StartItem);
            _incrementStart.Enabled = _incrementMode.SelectedIndex > 0;
            _incrementStart.TextChanged += (sender, args) =>
            {
                if (_loadingOptions) return;
                _settings.StartItem = _incrementStart.Text;
                SaveSettings();
                RefreshGrid();
            };
            _incrementPosition.Items.AddRange(new object[] { "后缀递增", "前缀递增", "前后缀递增" });
            _incrementPosition.SelectedIndex = _settings.PrefixIncrement && _settings.SuffixIncrement ? 2
                : _settings.PrefixIncrement ? 1 : 0;
            _prefixIncrement.Checked = _incrementPosition.SelectedIndex != 0;
            _suffixIncrement.Checked = _incrementPosition.SelectedIndex != 1;
            _incrementPosition.Enabled = _incrementMode.SelectedIndex > 0;
            _incrementPosition.SelectedIndexChanged += (sender, args) => SyncIncrementPosition();
            _direction.Items.AddRange(new object[] { "正向", "反向" });
            _direction.SelectedIndex = _settings.Reverse ? 1 : 0;
            _direction.SelectedIndexChanged += (sender, args) => SyncDirection();
            _step.Minimum = 1;
            _step.Maximum = 9999;
            _step.Value = Math.Max(1, Math.Min(9999, _settings.Step));
            _stepText.Text = ((int)_step.Value).ToString();
            _step.ValueChanged += (sender, args) =>
            {
                var value = ((int)_step.Value).ToString();
                if (_stepText.Text != value) _stepText.Text = value;
                if (_loadingOptions) return;
                _settings.Step = (int)_step.Value;
                SaveSettings();
                RefreshGrid();
            };
            _stepText.TextChanged += (sender, args) =>
            {
                if (int.TryParse(_stepText.Text, out var value) && value >= 1 && value <= 9999)
                    _step.Value = value;
            };
            _sort.Items.AddRange(new object[] { "先左右后上下", "先上下后左右" });
            _sort.SelectedIndex = Math.Max(0, Math.Min(1, _settings.Sort));
            _sort.SelectedIndexChanged += (sender, args) =>
            {
                if (_loadingOptions) return;
                _settings.Sort = _sort.SelectedIndex;
                SaveSettings();
                ReSort();
            };
            _scope.Items.AddRange(new object[] { "所有框选属性图块", "仅登记图框" });
            _scope.SelectedIndex = Math.Max(0, Math.Min(1, _settings.Scope));
            _scope.SelectedIndexChanged += (sender, args) =>
            {
                _settings.Scope = _scope.SelectedIndex;
                SaveSettings();
            };
            _tolerance.Text = _settings.Tolerance ?? string.Empty;
            _tolerance.TextChanged += (sender, args) =>
            {
                if (_loadingOptions) return;
                _settings.Tolerance = _tolerance.Text;
                SaveSettings();
                ReSort();
            };
        }

        private void ConfigureGrid()
        {
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AutoGenerateColumns = false;
            CadAttributeUi.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "写入", DataPropertyName = "Selected", Width = 58, Frozen = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", DataPropertyName = "Sequence", Width = 66, ReadOnly = true, Frozen = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "图块名称", DataPropertyName = "BlockName", Width = 150, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性标记", DataPropertyName = "Tag", Width = 126, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "修改前", DataPropertyName = "OldValue", Width = 165, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "NewValue", HeaderText = "修改后（可编辑）", DataPropertyName = "NewValue", Width = 190 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "检查结果", DataPropertyName = "State", Width = 98, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "X", HeaderText = "插入点 X", DataPropertyName = "X", Width = 108, ReadOnly = true, Visible = false });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Y", HeaderText = "插入点 Y", DataPropertyName = "Y", Width = 108, ReadOnly = true, Visible = false });
            _grid.DataSource = _previewRows;
            _grid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (sender, args) =>
            {
                if (!_updatingPreview && args.RowIndex >= 0 && args.ColumnIndex == 0)
                {
                    RememberPreviewSelection(args.RowIndex);
                    UpdatePreviewStates();
                }
            };
            _grid.CellEndEdit += GridCellEndEdit;
            _grid.CellDoubleClick += GridCellDoubleClick;
            _grid.CellFormatting += GridCellFormatting;
        }

        private void ShowMoreMenu(Control anchor)
        {
            CadAttributeUi.Menu(anchor, new[]
            {
                Tuple.Create("导出预览 CSV", (Action)ExportPreview),
                Tuple.Create("定位下一异常", (Action)LocateNextWarning),
                Tuple.Create("显示全部序号", (Action)ShowAllOrderMarkers),
                Tuple.Create("清除 CAD 标记", (Action)(() => _markers.Clear())),
                Tuple.Create("打开失败日志", (Action)OpenFailureLog)
            });
        }
    }
}
