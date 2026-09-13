using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CadArchSpec.CadTable;
using Newtonsoft.Json.Linq;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;
using AcColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;

namespace CadArchSpec.Host.Shared.CadTable
{
    internal enum CadTablePreviewAction
    {
        Cancel,
        Repick,
        ExportExcel,
        InsertCad,
        PickCadObjects
    }

    internal sealed class CadTablePreviewForm : Form
    {
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _summary = new Label();
        private readonly JObject _payload;
        private readonly Dictionary<string, Image> _cadObjectPreviews =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly ComboBox _scale = Combo(new[] { "1", "10", "20", "25", "50", "100" });
        private readonly ComboBox _textStyle = Combo(null);
        private readonly ComboBox _insertType = Combo(new[] { "AutoCAD 原生表格", "天正表格（T20）" });
        private readonly CheckBox _originalCadSize = new CheckBox
        {
            Text = "保持原 CAD 尺寸",
            AutoSize = true,
            Margin = new Padding(8, 7, 3, 0)
        };
        private readonly ColorPickerField _borderColor = new ColorPickerField("边框");
        private readonly ColorPickerField _fillColor = new ColorPickerField("底色");
        private readonly ColorPickerField _textColor = new ColorPickerField("文字");
        private readonly ColorPickerField _outerBorderColor = new ColorPickerField("外边框");
        private readonly ColorPickerField _innerBorderColor = new ColorPickerField("内边框");
        private readonly ComboBox _outerBorderWeight = Combo(new[] { "0.05", "0.09", "0.13", "0.18", "0.25", "0.35", "0.50", "0.70", "1.00" });
        private readonly ComboBox _innerBorderWeight = Combo(new[] { "0.05", "0.09", "0.13", "0.18", "0.25", "0.35", "0.50", "0.70", "1.00" });
        private readonly ListBox _templates = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly NumericUpDown _horizontalPadding = new NumericUpDown
        {
            DecimalPlaces = 1,
            Minimum = 0,
            Maximum = 20,
            Increment = .5m,
            Value = 1m,
            Width = 58
        };
        private readonly TextBox _columnTitle = new TextBox { Width = 100, Margin = new Padding(3, 4, 3, 2) };
        private readonly NumericUpDown _textHeight = new NumericUpDown
        {
            DecimalPlaces = 1,
            Minimum = .5m,
            Maximum = 100,
            Increment = .5m,
            Value = 3.5m,
            Width = 64
        };
        private bool _loading;
        private bool _showingAppearance;
        private bool _showingTableBorder;
        private static string LastScale = "1";
        private static string LastTextStyle = string.Empty;
        private static decimal LastTextHeight = 3.5m;
        private static bool LastOriginalCadSize;
        private static string LastInsertType = "AutoCAD 原生表格";
        private bool _redirectingMergeSelection;
        private bool _openingMergedEditor;
        private FormulaBuilderForm _formulaBuilder;
        private Button _insertCadButton;
        private readonly Stack<JObject> _undoStack = new Stack<JObject>();
        private bool _restoringUndo;
        private readonly JObject _savedDefaults;

        public CadTablePreviewAction SelectedAction { get; private set; }
        public JObject Payload { get { return _payload; } }

        public CadTablePreviewForm(JObject payload, IEnumerable<string> textStyles, string currentTextStyle)
        {
            _payload = payload == null ? new JObject() : (JObject)payload.DeepClone();
            _savedDefaults = CadTableDefaultsStore.Load();
            LastScale = (string)_savedDefaults["scale"] ?? LastScale;
            LastTextStyle = (string)_savedDefaults["textStyle"] ?? LastTextStyle;
            LastTextHeight = (decimal?)_savedDefaults["textHeightMillimeters"] ?? LastTextHeight;
            LastOriginalCadSize = (bool?)_savedDefaults["useOriginalCadSize"] ?? LastOriginalCadSize;
            LastInsertType = (string)_savedDefaults["insertTypeLabel"] ?? LastInsertType;
            var externalUndo = _payload["pendingCadObjectUndo"] as JObject;
            _payload.Remove("pendingCadObjectUndo");
            if (externalUndo != null) _undoStack.Push((JObject)externalUndo.DeepClone());
            Text = "CAD 表格编辑与预览";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1220, 720);
            MinimumSize = new Size(920, 560);
            Font = new Font("Microsoft YaHei UI", 9f);
            KeyPreview = true;
            KeyDown += FormKeyDown;
            foreach (var style in (textStyles ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)))
                _textStyle.Items.Add(style);
            _textStyle.DropDownStyle = ComboBoxStyle.DropDownList;
            var preferredStyle = !string.IsNullOrWhiteSpace(LastTextStyle) && _textStyle.Items.Contains(LastTextStyle)
                ? LastTextStyle : currentTextStyle;
            _textStyle.Text = string.IsNullOrWhiteSpace(preferredStyle) ? "Standard" : preferredStyle;
            _scale.Text = LastScale;
            _insertType.Text = LastInsertType;
            if (_payload["sourceEdit"] is JObject) _insertType.Text = "AutoCAD 原生表格";
            _textHeight.Value = Math.Max(_textHeight.Minimum, Math.Min(_textHeight.Maximum, LastTextHeight));
            _originalCadSize.Checked = LastOriginalCadSize && (bool?)_payload["hasOriginalCadSize"] == true;
            BuildLayout();
            NormalizePayload();
            LoadTable();
            ApplyInsertOptionsFromPayload();
        }

        private void BuildLayout()
        {
            _summary.Dock = DockStyle.Top;
            _summary.Height = 38;
            _summary.Padding = new Padding(10, 9, 10, 5);
            _summary.ForeColor = Color.FromArgb(65, 75, 85);

            var cellTools = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 2, 6, 2),
                ColumnCount = 3,
                RowCount = 8
            };
            ConfigureThreeColumns(cellTools);
            AddGridControl(cellTools, GridButtonFor("添加行", AddRow), 0, 0);
            AddGridControl(cellTools, GridButtonFor("删除行", DeleteRows), 1, 0);
            AddGridControl(cellTools, GridButtonFor("添加列", AddColumn), 2, 0);
            AddGridControl(cellTools, GridButtonFor("删除列", DeleteColumns), 0, 1);
            AddGridControl(cellTools, GridButtonFor("合并单元格", MergeSelection), 1, 1);
            AddGridControl(cellTools, GridButtonFor("取消合并", UnmergeSelection), 2, 1);
            AddGridControl(cellTools, GridButtonFor("关联单元格", LinkSelection), 0, 2);
            AddGridControl(cellTools, GridButtonFor("取消关联", UnlinkSelection), 1, 2);
            AddGridControl(cellTools, GridButtonFor("fx 公式", ShowFormulaBuilder), 2, 2);
            AddGridControl(cellTools, GridButtonFor("左对齐", () => SetAlignment("left")), 0, 3);
            AddGridControl(cellTools, GridButtonFor("居中", () => SetAlignment("center")), 1, 3);
            AddGridControl(cellTools, GridButtonFor("右对齐", () => SetAlignment("right")), 2, 3);
            AddGridControl(cellTools, FieldLabel("左右留白 (mm)"), 0, 4, 2);
            AddGridControl(cellTools, _horizontalPadding, 2, 4);
            AddGridControl(cellTools, _borderColor, 0, 5);
            AddGridControl(cellTools, _fillColor, 1, 5);
            AddGridControl(cellTools, _textColor, 2, 5);
            AddGridControl(cellTools, GridButtonFor("清除底色", ClearSelectedFill), 0, 6);
            AddGridControl(cellTools, GridButtonFor("恢复所选样式", ResetSelectedAppearance), 1, 6, 2);
            AddGridControl(cellTools, GridButtonFor("插入 CAD 对象", PickCadObjects), 0, 7, 2);
            AddGridControl(cellTools, GridButtonFor("移除对象", RemoveCadObject), 2, 7);

            var tableTools = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(6, 2, 6, 2),
                ColumnCount = 3,
                RowCount = 7
            };
            ConfigureThreeColumns(tableTools);
            AddGridControl(tableTools, FieldLabel("当前列标题"), 0, 0);
            AddGridControl(tableTools, _columnTitle, 1, 0);
            AddGridControl(tableTools, GridButtonFor("修改列名", UpdateColumnTitle), 2, 0);
            AddGridControl(tableTools, FieldLabel("插入比例 1:"), 0, 1);
            AddGridControl(tableTools, _scale, 1, 1);
            AddGridControl(tableTools, _originalCadSize, 2, 1);
            AddGridControl(tableTools, FieldLabel("插入类型"), 0, 2);
            _insertType.DropDownStyle = ComboBoxStyle.DropDownList;
            AddGridControl(tableTools, _insertType, 1, 2, 2);
            AddGridControl(tableTools, FieldLabel("CAD 文字样式"), 0, 3);
            AddGridControl(tableTools, _textStyle, 1, 3, 2);
            AddGridControl(tableTools, FieldLabel("纸面字高 (mm)"), 0, 4);
            AddGridControl(tableTools, _textHeight, 1, 4, 2);
            AddGridControl(tableTools, _outerBorderColor, 0, 5, 2);
            AddGridControl(tableTools, _outerBorderWeight, 2, 5);
            AddGridControl(tableTools, _innerBorderColor, 0, 6, 2);
            AddGridControl(tableTools, _innerBorderWeight, 2, 6);
            _borderColor.Click += (sender, args) => PickIndexedColor("borderColorIndex", _borderColor, 7, "边框颜色");
            _fillColor.Click += (sender, args) => PickIndexedColor("fillColorIndex", _fillColor, 7, "底色");
            _textColor.Click += (sender, args) => PickIndexedColor("textColorIndex", _textColor, 7, "文字颜色");
            _outerBorderColor.Click += (sender, args) => PickTableBorderColor(true);
            _innerBorderColor.Click += (sender, args) => PickTableBorderColor(false);
            _outerBorderWeight.SelectedIndexChanged += (sender, args) => ApplyTableBorderWeight(true);
            _innerBorderWeight.SelectedIndexChanged += (sender, args) => ApplyTableBorderWeight(false);
            _horizontalPadding.ValueChanged += (sender, args) =>
            {
                if (!_loading && !_showingAppearance) ApplyHorizontalPadding();
            };
            _originalCadSize.CheckedChanged += (sender, args) => UpdateSizeControls();
            _insertType.SelectedIndexChanged += (sender, args) => UpdateSizeControls();
            foreach (var control in new Control[] { _scale, _originalCadSize, _insertType, _textStyle, _textHeight })
                control.Enter += (sender, args) => BeginInsertSettingChange();
            new ToolTip().SetToolTip(_outerBorderWeight, "外边框线宽（mm）");
            new ToolTip().SetToolTip(_innerBorderWeight, "内边框线宽（mm）");
            UpdateSizeControls();

            var templateActions = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(2, 3, 2, 2),
                ColumnCount = 3,
                RowCount = 1
            };
            ConfigureThreeColumns(templateActions);
            AddGridControl(templateActions, GridButtonFor("打开", LoadSelectedTemplate), 0, 0);
            AddGridControl(templateActions, GridButtonFor("保存当前", SaveAsTemplate), 1, 0);
            AddGridControl(templateActions, GridButtonFor("删除", DeleteSelectedTemplate), 2, 0);
            _templates.DoubleClick += (sender, args) => LoadSelectedTemplate();
            var templatePanel = new Panel { Dock = DockStyle.Fill };
            templatePanel.Controls.Add(_templates);
            templatePanel.Controls.Add(templateActions);

            var groups = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(4, 0, 4, 4)
            };
            groups.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            groups.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
            groups.RowStyles.Add(new RowStyle(SizeType.Percent, 15));
            groups.Controls.Add(Group("所选单元格、行列", cellTools), 0, 0);
            groups.Controls.Add(Group("整表与插入设置", tableTools), 0, 1);
            groups.Controls.Add(Group("常用表格库", templatePanel), 0, 2);

            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.AllowUserToResizeColumns = true;
            _grid.AllowUserToResizeRows = true;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(51, 122, 183);
            _grid.RowHeadersWidth = 55;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.MultiSelect = true;
            _grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            _grid.KeyDown += GridKeyDown;
            _grid.CellBeginEdit += GridCellBeginEdit;
            _grid.CellEndEdit += GridCellEndEdit;
            _grid.SelectionChanged += (sender, args) =>
            {
                ShowCurrentColumnTitle();
                ShowSelectedAppearance();
                if (_formulaBuilder != null && !_formulaBuilder.IsDisposed)
                    _formulaBuilder.UpdateSelection(SelectedRangeAddress());
                _grid.Invalidate();
            };
            _grid.CellPainting += GridCellPainting;
            _grid.Paint += GridPaint;
            _grid.CellMouseDown += GridCellMouseDown;
            _grid.RowHeaderMouseClick += GridRowHeaderMouseClick;
            _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            _grid.CellMouseDoubleClick += GridCellDoubleClick;
            _grid.EditingControlShowing += GridEditingControlShowing;
            _grid.Scroll += (sender, args) => _grid.Invalidate();
            _grid.ColumnWidthChanged += (sender, args) => GridSizeChanged();
            _grid.RowHeightChanged += (sender, args) => GridSizeChanged();

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            actions.Controls.Add(ButtonFor("关闭", () => Finish(CadTablePreviewAction.Cancel)));
            _insertCadButton = ButtonFor(_payload["sourceEdit"] is JObject ? "更新当前表格" : "按设置插入 CAD",
                () => Finish(CadTablePreviewAction.InsertCad));
            actions.Controls.Add(_insertCadButton);
            actions.Controls.Add(ButtonFor("导出 Excel", () => Finish(CadTablePreviewAction.ExportExcel)));
            actions.Controls.Add(ButtonFor("拾取现有表格修改", () => Finish(CadTablePreviewAction.Repick)));
            actions.Controls.Add(ButtonFor("撤销", UndoLastChange));

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                IsSplitterFixed = true,
                SplitterWidth = 5
            };
            // SplitContainer validates minimum sizes against its current width. Give it
            // the form's real working size before applying the sidebar constraints.
            split.Size = new Size(Math.Max(920, ClientSize.Width), Math.Max(420, ClientSize.Height - 90));
            split.Panel1MinSize = 300;
            split.Panel2MinSize = 420;
            split.SplitterDistance = Math.Min(360, split.Width - split.Panel2MinSize - split.SplitterWidth);
            split.Panel1.Controls.Add(groups);
            split.Panel2.Controls.Add(_grid);

            Controls.Add(split);
            Controls.Add(_summary);
            Controls.Add(actions);
            RefreshTemplates();
        }

        private void NormalizePayload()
        {
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            if (columns.Count == 0) columns.Add(CreateColumn(0));
            if (rows.Count == 0) rows.Add(CreateRow(columns));
            if (table["outerBorderColorIndex"] == null) table["outerBorderColorIndex"] =
                _savedDefaults["outerBorderColorIndex"]?.DeepClone() ?? new JValue(7);
            if (table["innerBorderColorIndex"] == null) table["innerBorderColorIndex"] =
                _savedDefaults["innerBorderColorIndex"]?.DeepClone() ?? new JValue(7);
            if (table["outerBorderWeightMillimeters"] == null) table["outerBorderWeightMillimeters"] =
                (double?)_savedDefaults["outerBorderWeightMillimeters"] ?? .25d;
            if (table["innerBorderWeightMillimeters"] == null) table["innerBorderWeightMillimeters"] =
                (double?)_savedDefaults["innerBorderWeightMillimeters"] ?? .13d;
            foreach (var row in rows.OfType<JObject>())
            {
                var cells = EnsureArray(row, "cells");
                while (cells.Count < columns.Count) cells.Add(CreateCell(cells.Count));
                while (cells.Count > columns.Count) cells.RemoveAt(cells.Count - 1);
                foreach (var cell in cells.OfType<JObject>())
                {
                    if (cell["alignment"] == null) cell["alignment"] = "center";
                    if (cell["horizontalPaddingMillimeters"] == null) cell["horizontalPaddingMillimeters"] =
                        (double?)_savedDefaults["horizontalPaddingMillimeters"] ?? 1d;
                    if (cell["borderColorIndex"] == null && _savedDefaults["cellBorderColorIndex"] != null)
                        cell["borderColorIndex"] = _savedDefaults["cellBorderColorIndex"].DeepClone();
                    if (cell["fillColorIndex"] == null && _savedDefaults["cellFillColorIndex"] != null)
                        cell["fillColorIndex"] = _savedDefaults["cellFillColorIndex"].DeepClone();
                    if (cell["textColorIndex"] == null && _savedDefaults["cellTextColorIndex"] != null)
                        cell["textColorIndex"] = _savedDefaults["cellTextColorIndex"].DeepClone();
                }
            }
        }

        private void LoadTable()
        {
            RecalculateFormulas();
            _loading = true;
            try
            {
                var table = EnsureTable();
                var columns = EnsureArray(table, "columns").OfType<JObject>().ToList();
                var rows = EnsureArray(table, "rows").OfType<JObject>().ToList();
                _grid.Columns.Clear();
                _grid.Rows.Clear();
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var source = columns[columnIndex];
                    var title = (string)source["title"];
                    var width = Math.Max(55, Math.Min(360, (int)Math.Round(((double?)source["widthMillimeters"] ?? 36d) * 3d)));
                    _grid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        Name = "column" + columnIndex,
                        HeaderText = string.IsNullOrWhiteSpace(title) ? ColumnName(columnIndex) : title,
                        Width = width,
                        SortMode = DataGridViewColumnSortMode.NotSortable
                    });
                }
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var cells = EnsureArray(rows[rowIndex], "cells").OfType<JObject>().ToList();
                    var values = Enumerable.Range(0, columns.Count)
                        .Select(index => index < cells.Count ? (object)CellDisplayValue(cells[index]) : string.Empty)
                        .ToArray();
                    var gridRowIndex = _grid.Rows.Add(values);
                    _grid.Rows[gridRowIndex].HeaderCell.Value = (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
                    _grid.Rows[gridRowIndex].Height = Math.Max(22, Math.Min(240,
                        (int)Math.Round(((double?)rows[rowIndex]["heightMillimeters"] ?? 8d) * 3d)));
                    for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, columns.Count); columnIndex++)
                        ApplyCellAppearance(gridRowIndex, columnIndex, cells[columnIndex]);
                }
                var warnings = _payload["warnings"] as JArray;
                var warningCount = warnings == null ? 0 : warnings.Count;
                var objectCellCount = (int?)_payload["autoCadObjectCellCount"] ?? 0;
                _summary.Text = string.Format("{0} 行 × {1} 列。公式示例：=A1、=SUM(B2:B8)；关联组内任意一格修改都会同步。{2}{3}",
                    rows.Count, columns.Count,
                    objectCellCount == 0 ? string.Empty : "已自动识别 " + objectCellCount + " 个含 CAD 对象的单元格。 ",
                    warningCount == 0 ? string.Empty : "待复核提示 " + warningCount + " 项。 ");
                ShowCurrentColumnTitle();
                ShowTableBorderAppearance();
                ApplyGridColors();
            }
            finally { _loading = false; }
        }

        private void ApplyCellAppearance(int rowIndex, int columnIndex, JObject cell)
        {
            var rowSpan = (int?)cell["rowSpan"] ?? 1;
            var columnSpan = (int?)cell["columnSpan"] ?? 1;
            var hasCadObject = !string.IsNullOrWhiteSpace((string)cell["cadObjectAssetPath"]);
            var gridCell = _grid.Rows[rowIndex].Cells[columnIndex];
            gridCell.ReadOnly = rowSpan == 0 || columnSpan == 0 || hasCadObject;
            gridCell.Style.BackColor = Color.White;
            gridCell.ToolTipText = string.Empty;
            if (hasCadObject)
            {
                gridCell.ToolTipText = "该单元格包含 CAD 对象；可替换或移除对象";
            }
            else if (gridCell.ReadOnly)
            {
                gridCell.ToolTipText = "该位置属于合并单元格，点击可编辑主单元格";
            }
            else if (rowSpan > 1 || columnSpan > 1)
            {
                gridCell.ToolTipText = string.Format("合并区域：{0} 行 × {1} 列；双击可编辑完整内容", rowSpan, columnSpan);
            }
            if (!string.IsNullOrWhiteSpace((string)cell["linkGroupId"]))
                gridCell.ToolTipText = (gridCell.ToolTipText + " 关联单元格：修改组内任意一格会同步").Trim();
            gridCell.Style.Alignment = GridAlignment((string)cell["alignment"]);
            var padding = Math.Max(0, Math.Min(30, (int)Math.Round(((double?)cell["horizontalPaddingMillimeters"] ?? 1d) * 3d)));
            gridCell.Style.Padding = new Padding(padding, 0, padding, 0);
            gridCell.Style.BackColor = AciDisplayColor((short?)cell["fillColorIndex"], Color.White);
            gridCell.Style.ForeColor = AciDisplayColor((short?)cell["textColorIndex"], Color.Black);
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge))
            {
                var cell = Cell(e.RowIndex, e.ColumnIndex);
                var preview = CadObjectPreview(cell);
                var index = (short?)cell["borderColorIndex"];
                e.Paint(e.ClipBounds, e.PaintParts);
                if (preview != null)
                {
                    using (var brush = new SolidBrush(e.State.HasFlag(DataGridViewElementStates.Selected)
                        ? e.CellStyle.SelectionBackColor : e.CellStyle.BackColor))
                        e.Graphics.FillRectangle(brush, e.CellBounds);
                    var value = (string)cell["displayValue"] ?? string.Empty;
                    var imageBounds = Rectangle.Inflate(e.CellBounds, -4, -4);
                    if (value.Length > 0) imageBounds.Height = Math.Max(1, imageBounds.Height - 18);
                    DrawImageContained(e.Graphics, preview, imageBounds);
                    DrawCadObjectText(e.Graphics, value, e.CellBounds,
                        AciDisplayColor((short?)cell["textColorIndex"], Color.Black),
                        (string)cell["alignment"], (double?)cell["horizontalPaddingMillimeters"] ?? 1d);
                }
                DrawCellBorders(e.Graphics, e.CellBounds, e.RowIndex, e.RowIndex,
                    e.ColumnIndex, e.ColumnIndex, index);
                e.Handled = true;
                return;
            }

            // The grid still owns hit testing and editing. Its merged cells are
            // painted as one region below, so suppress every internal border.
            using (var brush = new SolidBrush(_grid.BackgroundColor))
                e.Graphics.FillRectangle(brush, e.CellBounds);
            e.Handled = true;
        }

        private void GridPaint(object sender, PaintEventArgs e)
        {
            foreach (var merge in MergeRanges().ToList())
            {
                var rectangle = MergeDisplayRectangle(merge);
                if (rectangle.Width <= 0 || rectangle.Height <= 0 || !rectangle.IntersectsWith(_grid.ClientRectangle)) continue;
                var selected = MergeIsSelected(merge);
                var anchorStyle = _grid.Rows[merge[0]].Cells[merge[2]].InheritedStyle;
                var background = selected ? anchorStyle.SelectionBackColor : anchorStyle.BackColor;
                var foreground = selected ? anchorStyle.SelectionForeColor : anchorStyle.ForeColor;
                var anchor = Cell(merge[0], merge[2]);
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, rectangle);
                DrawCellBorders(e.Graphics, rectangle, merge[0], merge[1], merge[2], merge[3],
                    (short?)anchor["borderColorIndex"]);

                var preview = CadObjectPreview(anchor);
                if (preview != null)
                {
                    var value = (string)anchor["displayValue"] ?? string.Empty;
                    var imageBounds = Rectangle.Inflate(rectangle, -4, -4);
                    if (value.Length > 0) imageBounds.Height = Math.Max(1, imageBounds.Height - 18);
                    DrawImageContained(e.Graphics, preview, imageBounds);
                    DrawCadObjectText(e.Graphics, value, rectangle,
                        AciDisplayColor((short?)anchor["textColorIndex"], Color.Black),
                        (string)anchor["alignment"], (double?)anchor["horizontalPaddingMillimeters"] ?? 1d);
                    continue;
                }
                var text = Convert.ToString(_grid.Rows[merge[0]].Cells[merge[2]].Value) ?? string.Empty;
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                var alignment = (string)anchor["alignment"];
                if (string.Equals(alignment, "left", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Left;
                else if (string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Right;
                else flags |= TextFormatFlags.HorizontalCenter;
                var horizontalPadding = Math.Max(2, (int)Math.Round(((double?)anchor["horizontalPaddingMillimeters"] ?? 1d) * 3d));
                var textBounds = Rectangle.Inflate(rectangle, -horizontalPadding, -2);
                TextRenderer.DrawText(e.Graphics, text, _grid.Font, textBounds, foreground, flags);
            }
        }

        private void GridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _redirectingMergeSelection) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge) ||
                e.RowIndex == merge[0] && e.ColumnIndex == merge[2]) return;
            _redirectingMergeSelection = true;
            BeginInvoke(new Action(() =>
            {
                try { SelectCell(merge[0], merge[2]); }
                finally { _redirectingMergeSelection = false; }
            }));
        }

        private void GridCellDoubleClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int[] merge;
            if (!TryFindMerge(e.RowIndex, e.ColumnIndex, out merge))
                merge = new[] { e.RowIndex, e.RowIndex, e.ColumnIndex, e.ColumnIndex };
            BeginInvoke(new Action(() =>
            {
                SelectCell(merge[0], merge[2]);
                ShowCellEditor(merge);
            }));
        }

        private void GridRowHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if ((ModifierKeys & Keys.Control) == 0) _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0,
                Math.Min(_grid.Columns.Count - 1, _grid.CurrentCell == null ? 0 : _grid.CurrentCell.ColumnIndex))];
            foreach (DataGridViewCell cell in _grid.Rows[e.RowIndex].Cells) cell.Selected = true;
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            if ((ModifierKeys & Keys.Control) == 0) _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[Math.Max(0,
                Math.Min(_grid.Rows.Count - 1, _grid.CurrentCell == null ? 0 : _grid.CurrentCell.RowIndex))].Cells[e.ColumnIndex];
            foreach (DataGridViewRow row in _grid.Rows) row.Cells[e.ColumnIndex].Selected = true;
        }

        private void GridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (_grid.CurrentCell == null) return;
            int[] merge;
            if (!TryFindMerge(_grid.CurrentCell.RowIndex, _grid.CurrentCell.ColumnIndex, out merge)) return;
            BeginInvoke(new Action(() =>
            {
                var rectangle = Rectangle.Inflate(MergeDisplayRectangle(merge), -2, -2);
                if (rectangle.Width > 0 && rectangle.Height > 0) e.Control.Bounds = rectangle;
            }));
        }

        private Rectangle MergeDisplayRectangle(int[] merge)
        {
            var first = _grid.GetCellDisplayRectangle(merge[2], merge[0], true);
            var last = _grid.GetCellDisplayRectangle(merge[3], merge[1], true);
            return Rectangle.FromLTRB(first.Left, first.Top, last.Right, last.Bottom);
        }

        private bool MergeIsSelected(int[] merge)
        {
            for (var row = merge[0]; row <= merge[1]; row++)
                for (var column = merge[2]; column <= merge[3]; column++)
                    if (_grid.Rows[row].Cells[column].Selected) return true;
            return false;
        }

        private bool TryFindMerge(int row, int column, out int[] result)
        {
            result = MergeRanges().FirstOrDefault(range =>
                row >= range[0] && row <= range[1] && column >= range[2] && column <= range[3]);
            return result != null;
        }

        private void DrawCellBorders(Graphics graphics, Rectangle bounds, int topRow, int bottomRow,
            int leftColumn, int rightColumn, short? overrideColor)
        {
            var table = EnsureTable();
            var lastRow = Math.Max(0, EnsureArray(table, "rows").Count - 1);
            var lastColumn = Math.Max(0, EnsureArray(table, "columns").Count - 1);
            var outerColor = (short?)table["outerBorderColorIndex"];
            var innerColor = (short?)table["innerBorderColorIndex"];
            var outerWidth = Math.Max(1f, Math.Min(5f, (float)(((double?)table["outerBorderWeightMillimeters"] ?? .25d) * 5d)));
            var innerWidth = Math.Max(1f, Math.Min(5f, (float)(((double?)table["innerBorderWeightMillimeters"] ?? .13d) * 5d)));
            Action<int, int, int, int, bool> draw = (x1, y1, x2, y2, outer) =>
            {
                using (var pen = new Pen(AciDisplayColor(overrideColor ?? (outer ? outerColor : innerColor), _grid.GridColor),
                    overrideColor.HasValue ? Math.Max(1f, innerWidth) : outer ? outerWidth : innerWidth))
                    graphics.DrawLine(pen, x1, y1, x2, y2);
            };
            draw(bounds.Left, bounds.Top, bounds.Right - 1, bounds.Top, topRow == 0);
            draw(bounds.Left, bounds.Bottom - 1, bounds.Right - 1, bounds.Bottom - 1, bottomRow == lastRow);
            draw(bounds.Left, bounds.Top, bounds.Left, bounds.Bottom - 1, leftColumn == 0);
            draw(bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom - 1, rightColumn == lastColumn);
        }

        private void CommitEdits()
        {
            if (_loading) return;
            _grid.EndEdit();
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            for (var columnIndex = 0; columnIndex < Math.Min(columns.Count, _grid.Columns.Count); columnIndex++)
            {
                var column = columns[columnIndex] as JObject;
                if (column == null) continue;
                column["title"] = _grid.Columns[columnIndex].HeaderText;
                column["widthMillimeters"] = Math.Max(8d, _grid.Columns[columnIndex].Width / 3d);
            }
            for (var rowIndex = 0; rowIndex < Math.Min(rows.Count, _grid.Rows.Count); rowIndex++)
            {
                var row = rows[rowIndex] as JObject;
                var cells = row == null ? null : row["cells"] as JArray;
                if (cells == null) continue;
                row["heightMillimeters"] = Math.Max(4d, _grid.Rows[rowIndex].Height / 3d);
                for (var columnIndex = 0; columnIndex < Math.Min(cells.Count, _grid.Columns.Count); columnIndex++)
                {
                    var cell = cells[columnIndex] as JObject;
                    if (cell == null || (int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                    if (!string.IsNullOrWhiteSpace((string)cell["cadObjectAssetPath"])) continue;
                    var gridValue = Convert.ToString(_grid.Rows[rowIndex].Cells[columnIndex].Value) ?? string.Empty;
                    if (gridValue.TrimStart().StartsWith("=", StringComparison.Ordinal))
                        cell["formula"] = gridValue.Trim();
                    else if (string.IsNullOrWhiteSpace((string)cell["formula"]) ||
                        !string.Equals(gridValue, (string)cell["displayValue"] ?? string.Empty, StringComparison.Ordinal))
                    {
                        cell["formula"] = string.Empty;
                        cell["displayValue"] = gridValue;
                    }
                }
            }
            double scale;
            if (!double.TryParse(_scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0d)
                throw new InvalidOperationException("插入比例必须是大于 0 的数字。");
            _payload["cadInsertOptions"] = new JObject
            {
                ["scale"] = scale,
                ["useOriginalCadSize"] = _originalCadSize.Checked,
                ["insertType"] = _insertType.SelectedIndex == 1 ? "tianzheng" : "autocad",
                ["textStyle"] = _textStyle.Text.Trim(),
                ["textHeightMillimeters"] = (double)_textHeight.Value
            };
            RecalculateFormulas();
            _payload["rowCount"] = rows.Count;
            _payload["columnCount"] = columns.Count;
        }

        private void AddRow()
        {
            CommitEdits();
            RecordUndo();
            var table = EnsureTable();
            var merges = CaptureMergeSnapshots();
            UnmergeAll();
            var rows = EnsureArray(table, "rows");
            var index = _grid.CurrentCell == null ? rows.Count : _grid.CurrentCell.RowIndex + 1;
            rows.Insert(index, CreateRow(EnsureArray(table, "columns")));
            RestoreMergeSnapshots(merges.Select(merge => TransformMergeForRowInsert(merge, index)));
            LoadTable();
            SelectCell(index, 0);
        }

        private void DeleteRows()
        {
            var indices = _grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => cell.RowIndex).Distinct().OrderByDescending(value => value).ToList();
            if (indices.Count == 0 && _grid.CurrentCell != null) indices.Add(_grid.CurrentCell.RowIndex);
            if (indices.Count == 0) return;
            CommitEdits();
            var rows = EnsureArray(EnsureTable(), "rows");
            if (rows.Count <= indices.Count) { MessageBox.Show(this, "表格至少保留一行。", Text); return; }
            RecordUndo();
            var merges = CaptureMergeSnapshots();
            UnmergeAll();
            foreach (var index in indices.Where(index => index >= 0 && index < rows.Count)) rows.RemoveAt(index);
            RestoreMergeSnapshots(merges.Select(merge => TransformMergeForRowDelete(merge, indices)).Where(merge => merge != null));
            LoadTable();
        }

        private void AddColumn()
        {
            CommitEdits();
            RecordUndo();
            var table = EnsureTable();
            var merges = CaptureMergeSnapshots();
            UnmergeAll();
            var columns = EnsureArray(table, "columns");
            var index = _grid.CurrentCell == null ? columns.Count : _grid.CurrentCell.ColumnIndex + 1;
            columns.Insert(index, CreateColumn(index));
            foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) EnsureArray(row, "cells").Insert(index, CreateCell(index));
            ReindexColumns();
            RestoreMergeSnapshots(merges.Select(merge => TransformMergeForColumnInsert(merge, index)));
            LoadTable();
            SelectCell(0, index);
        }

        private void DeleteColumns()
        {
            var indices = _grid.SelectedCells.Cast<DataGridViewCell>().Select(cell => cell.ColumnIndex).Distinct().OrderByDescending(value => value).ToList();
            if (indices.Count == 0 && _grid.CurrentCell != null) indices.Add(_grid.CurrentCell.ColumnIndex);
            if (indices.Count == 0) return;
            CommitEdits();
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            if (columns.Count <= indices.Count) { MessageBox.Show(this, "表格至少保留一列。", Text); return; }
            RecordUndo();
            var merges = CaptureMergeSnapshots();
            UnmergeAll();
            foreach (var index in indices.Where(index => index >= 0 && index < columns.Count))
            {
                columns.RemoveAt(index);
                foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) EnsureArray(row, "cells").RemoveAt(index);
            }
            ReindexColumns();
            RestoreMergeSnapshots(merges.Select(merge => TransformMergeForColumnDelete(merge, indices)).Where(merge => merge != null));
            LoadTable();
        }

        private void MergeSelection()
        {
            if (_grid.SelectedCells.Count < 2) return;
            CommitEdits();
            var selected = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
            var top = selected.Min(cell => cell.RowIndex);
            var bottom = selected.Max(cell => cell.RowIndex);
            var left = selected.Min(cell => cell.ColumnIndex);
            var right = selected.Max(cell => cell.ColumnIndex);
            if (selected.Count != (bottom - top + 1) * (right - left + 1))
            {
                MessageBox.Show(this, "请选择一个连续的矩形区域。", Text);
                return;
            }
            RecordUndo();
            UnmergeIntersecting(top, bottom, left, right);
            var anchor = Cell(top, left);
            anchor["rowSpan"] = bottom - top + 1;
            anchor["columnSpan"] = right - left + 1;
            for (var row = top; row <= bottom; row++)
                for (var column = left; column <= right; column++)
                    if (row != top || column != left)
                    {
                        Cell(row, column)["rowSpan"] = 0;
                        Cell(row, column)["columnSpan"] = 0;
                    }
            LoadTable();
            SelectCell(top, left);
        }

        private void LinkSelection()
        {
            if (_grid.CurrentCell == null || _grid.SelectedCells.Count < 2)
            {
                MessageBox.Show(this, "请先选中两个或更多单元格；建立关联时采用当前单元格的内容。", Text);
                return;
            }
            CommitEdits();
            RecordUndo();
            var sourceRow = _grid.CurrentCell.RowIndex;
            var sourceColumn = _grid.CurrentCell.ColumnIndex;
            var source = Cell(sourceRow, sourceColumn);
            var groupId = (string)source["linkGroupId"];
            if (string.IsNullOrWhiteSpace(groupId)) groupId = "link-" + Guid.NewGuid().ToString("N");
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["linkGroupId"] = groupId;
                cell["formula"] = (string)source["formula"] ?? string.Empty;
                cell["displayValue"] = (string)source["displayValue"] ?? string.Empty;
            }
            LoadTable();
            SelectCell(sourceRow, sourceColumn);
        }

        private void UnlinkSelection()
        {
            if (_grid.SelectedCells.Count == 0) return;
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
                Cell(selected.RowIndex, selected.ColumnIndex)["linkGroupId"] = string.Empty;
            LoadTable();
        }

        private void ShowFormulaBuilder()
        {
            if (_grid.CurrentCell == null)
            {
                MessageBox.Show(this, "请先选择要写入公式的目标单元格。", Text);
                return;
            }
            if (_formulaBuilder != null && !_formulaBuilder.IsDisposed)
            {
                _formulaBuilder.Activate();
                return;
            }
            CommitEdits();
            var targetRow = _grid.CurrentCell.RowIndex;
            var targetColumn = _grid.CurrentCell.ColumnIndex;
            var target = Cell(targetRow, targetColumn);
            var existing = (string)target["formula"] ?? string.Empty;
            _formulaBuilder = new FormulaBuilderForm(targetRow, targetColumn, existing,
                () => SelectedRangeAddress(), formula => ApplyFormula(targetRow, targetColumn, formula));
            _formulaBuilder.FormClosed += (sender, args) => _formulaBuilder = null;
            _formulaBuilder.Show(this);
            _formulaBuilder.UpdateSelection(SelectedRangeAddress());
        }

        private void ApplyFormula(int row, int column, string formula)
        {
            if (row < 0 || column < 0 || row >= _grid.Rows.Count || column >= _grid.Columns.Count) return;
            var cell = Cell(row, column);
            RecordUndo();
            var value = (formula ?? string.Empty).Trim();
            if (!value.StartsWith("=", StringComparison.Ordinal)) value = "=" + value;
            cell["formula"] = value;
            PropagateLinkedCell(cell);
            RecalculateFormulas();
            RefreshCalculatedValues();
            SelectCell(row, column);
        }

        private string SelectedRangeAddress()
        {
            if (_grid.SelectedCells.Count == 0) return string.Empty;
            var selected = _grid.SelectedCells.Cast<DataGridViewCell>().ToList();
            var top = selected.Min(cell => cell.RowIndex);
            var bottom = selected.Max(cell => cell.RowIndex);
            var left = selected.Min(cell => cell.ColumnIndex);
            var right = selected.Max(cell => cell.ColumnIndex);
            var first = SpreadsheetFormulaEngine.CellAddress(top, left);
            var last = SpreadsheetFormulaEngine.CellAddress(bottom, right);
            return first == last ? first : first + ":" + last;
        }

        private void UnmergeSelection()
        {
            if (_grid.CurrentCell == null) return;
            CommitEdits();
            var row = _grid.CurrentCell.RowIndex;
            var column = _grid.CurrentCell.ColumnIndex;
            foreach (var merge in MergeRanges())
            {
                if (row >= merge[0] && row <= merge[1] && column >= merge[2] && column <= merge[3])
                {
                    RecordUndo();
                    Unmerge(merge);
                    LoadTable();
                    SelectCell(merge[0], merge[2]);
                    return;
                }
            }
        }

        private void SetAlignment(string alignment)
        {
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["alignment"] = alignment;
                selected.Style.Alignment = GridAlignment(alignment);
            }
        }

        private void PickCadObjects()
        {
            if (_grid.CurrentCell == null)
            {
                MessageBox.Show(this, "请先选择要放置 CAD 对象的单元格。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int[] merge;
            var row = _grid.CurrentCell.RowIndex;
            var column = _grid.CurrentCell.ColumnIndex;
            if (TryFindMerge(row, column, out merge)) { row = merge[0]; column = merge[2]; }
            CommitEdits();
            var undo = (JObject)_payload.DeepClone();
            undo.Remove("pendingCadObjectUndo");
            _payload["pendingCadObjectUndo"] = undo;
            _payload["pendingCadObjectRow"] = row;
            _payload["pendingCadObjectColumn"] = column;
            Finish(CadTablePreviewAction.PickCadObjects);
        }

        private void RemoveCadObject()
        {
            if (_grid.CurrentCell == null) return;
            int[] merge;
            var row = _grid.CurrentCell.RowIndex;
            var column = _grid.CurrentCell.ColumnIndex;
            if (TryFindMerge(row, column, out merge)) { row = merge[0]; column = merge[2]; }
            var cell = Cell(row, column);
            RecordUndo();
            cell["cadObjectAssetPath"] = string.Empty;
            cell["cadObjectPreviewPath"] = string.Empty;
            cell["cadObjectCount"] = 0;
            LoadTable();
            SelectCell(row, column);
        }

        private void ApplyHorizontalPadding()
        {
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["horizontalPaddingMillimeters"] = (double)_horizontalPadding.Value;
                cell["sourceHorizontalPaddingCadUnits"] = JValue.CreateNull();
                ApplyCellAppearance(selected.RowIndex, selected.ColumnIndex, cell);
            }
            _grid.Invalidate();
        }

        private void ShowCurrentColumnTitle()
        {
            if (_grid.CurrentCell == null) return;
            _columnTitle.Text = _grid.Columns[_grid.CurrentCell.ColumnIndex].HeaderText;
        }

        private void UpdateColumnTitle()
        {
            if (_grid.CurrentCell == null || string.IsNullOrWhiteSpace(_columnTitle.Text)) return;
            CommitEdits();
            RecordUndo();
            _grid.Columns[_grid.CurrentCell.ColumnIndex].HeaderText = _columnTitle.Text.Trim();
            CommitEdits();
        }

        private void GridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                PasteClipboard();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                RecordUndo();
                foreach (DataGridViewCell cell in _grid.SelectedCells)
                    if (string.IsNullOrWhiteSpace((string)Cell(cell.RowIndex, cell.ColumnIndex)["cadObjectAssetPath"]))
                    {
                        cell.Value = string.Empty;
                        var target = Cell(cell.RowIndex, cell.ColumnIndex);
                        target["formula"] = string.Empty;
                        target["displayValue"] = string.Empty;
                        PropagateLinkedCell(target);
                    }
                RecalculateFormulas();
                RefreshCalculatedValues();
                e.Handled = true;
            }
            else if ((e.KeyCode == Keys.F2 || e.KeyCode == Keys.Enter) && _grid.CurrentCell != null)
            {
                var row = _grid.CurrentCell.RowIndex;
                var column = _grid.CurrentCell.ColumnIndex;
                int[] merge;
                if (!TryFindMerge(row, column, out merge)) merge = new[] { row, row, column, column };
                BeginInvoke(new Action(() => ShowCellEditor(merge)));
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void PasteClipboard()
        {
            if (_grid.CurrentCell == null || !Clipboard.ContainsText()) return;
            CommitEdits();
            RecordUndo();
            var lines = Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            var values = lines.Select(line => line.Split('\t')).ToArray();
            var startRow = _grid.CurrentCell.RowIndex;
            var startColumn = _grid.CurrentCell.ColumnIndex;
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            var rows = EnsureArray(table, "rows");
            var neededColumns = startColumn + values.Max(row => row.Length);
            while (columns.Count < neededColumns)
            {
                columns.Add(CreateColumn(columns.Count));
                foreach (var row in rows.OfType<JObject>()) EnsureArray(row, "cells").Add(CreateCell(columns.Count - 1));
            }
            while (rows.Count < startRow + values.Length) rows.Add(CreateRow(columns));
            UnmergeIntersecting(startRow, startRow + values.Length - 1, startColumn, neededColumns - 1);
            for (var rowOffset = 0; rowOffset < values.Length; rowOffset++)
                for (var columnOffset = 0; columnOffset < values[rowOffset].Length; columnOffset++)
                {
                    var target = Cell(startRow + rowOffset, startColumn + columnOffset);
                    var value = values[rowOffset][columnOffset] ?? string.Empty;
                    target["formula"] = value.TrimStart().StartsWith("=", StringComparison.Ordinal) ? value.Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace((string)target["formula"])) target["displayValue"] = value;
                    PropagateLinkedCell(target);
                }
            LoadTable();
            SelectCell(startRow, startColumn);
        }

        private void UnmergeIntersecting(int top, int bottom, int left, int right)
        {
            foreach (var merge in MergeRanges().Where(range => range[0] <= bottom && range[1] >= top && range[2] <= right && range[3] >= left).ToList()) Unmerge(merge);
        }

        private void UnmergeAll()
        {
            foreach (var merge in MergeRanges().ToList()) Unmerge(merge);
        }

        private List<MergeSnapshot> CaptureMergeSnapshots()
        {
            return MergeRanges().Select(range => new MergeSnapshot
            {
                Top = range[0],
                Bottom = range[1],
                Left = range[2],
                Right = range[3],
                Anchor = (JObject)Cell(range[0], range[2]).DeepClone()
            }).ToList();
        }

        private void RestoreMergeSnapshots(IEnumerable<MergeSnapshot> snapshots)
        {
            var rows = EnsureArray(EnsureTable(), "rows");
            var columnCount = EnsureArray(EnsureTable(), "columns").Count;
            foreach (var snapshot in snapshots.Where(value => value != null))
            {
                if (snapshot.Top < 0 || snapshot.Left < 0 || snapshot.Bottom >= rows.Count || snapshot.Right >= columnCount) continue;
                var cells = EnsureArray((JObject)rows[snapshot.Top], "cells");
                var anchor = (JObject)snapshot.Anchor.DeepClone();
                anchor["columnKey"] = "column" + (snapshot.Left + 1);
                anchor["rowSpan"] = snapshot.Bottom - snapshot.Top + 1;
                anchor["columnSpan"] = snapshot.Right - snapshot.Left + 1;
                cells[snapshot.Left] = anchor;
                for (var row = snapshot.Top; row <= snapshot.Bottom; row++)
                    for (var column = snapshot.Left; column <= snapshot.Right; column++)
                        if (row != snapshot.Top || column != snapshot.Left)
                        {
                            Cell(row, column)["rowSpan"] = 0;
                            Cell(row, column)["columnSpan"] = 0;
                        }
            }
        }

        private static MergeSnapshot TransformMergeForRowInsert(MergeSnapshot source, int index)
        {
            var result = source.Clone();
            if (index <= result.Top) { result.Top++; result.Bottom++; }
            else if (index <= result.Bottom) result.Bottom++;
            return result;
        }

        private static MergeSnapshot TransformMergeForColumnInsert(MergeSnapshot source, int index)
        {
            var result = source.Clone();
            if (index <= result.Left) { result.Left++; result.Right++; }
            else if (index <= result.Right) result.Right++;
            return result;
        }

        private static MergeSnapshot TransformMergeForRowDelete(MergeSnapshot source, ICollection<int> deleted)
        {
            var survivors = Enumerable.Range(source.Top, source.Bottom - source.Top + 1)
                .Where(index => !deleted.Contains(index)).ToList();
            if (survivors.Count == 0) return null;
            var result = source.Clone();
            result.Top = survivors.First() - deleted.Count(index => index < survivors.First());
            result.Bottom = survivors.Last() - deleted.Count(index => index < survivors.Last());
            return result;
        }

        private static MergeSnapshot TransformMergeForColumnDelete(MergeSnapshot source, ICollection<int> deleted)
        {
            var survivors = Enumerable.Range(source.Left, source.Right - source.Left + 1)
                .Where(index => !deleted.Contains(index)).ToList();
            if (survivors.Count == 0) return null;
            var result = source.Clone();
            result.Left = survivors.First() - deleted.Count(index => index < survivors.First());
            result.Right = survivors.Last() - deleted.Count(index => index < survivors.Last());
            return result;
        }

        private IEnumerable<int[]> MergeRanges()
        {
            var table = EnsureTable();
            var rows = EnsureArray(table, "rows");
            var columns = EnsureArray(table, "columns");
            for (var row = 0; row < rows.Count; row++)
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = Cell(row, column);
                    var rowSpan = (int?)cell["rowSpan"] ?? 1;
                    var columnSpan = (int?)cell["columnSpan"] ?? 1;
                    if (rowSpan > 1 || columnSpan > 1)
                        yield return new[] { row, Math.Min(rows.Count - 1, row + rowSpan - 1), column, Math.Min(columns.Count - 1, column + columnSpan - 1) };
                }
        }

        private void Unmerge(int[] range)
        {
            for (var row = range[0]; row <= range[1]; row++)
                for (var column = range[2]; column <= range[3]; column++)
                {
                    Cell(row, column)["rowSpan"] = 1;
                    Cell(row, column)["columnSpan"] = 1;
                }
        }

        private void ReindexColumns()
        {
            var table = EnsureTable();
            var columns = EnsureArray(table, "columns");
            for (var index = 0; index < columns.Count; index++)
            {
                var key = "column" + (index + 1);
                ((JObject)columns[index])["key"] = key;
                foreach (var row in EnsureArray(table, "rows").OfType<JObject>()) ((JObject)EnsureArray(row, "cells")[index])["columnKey"] = key;
            }
        }

        private JObject EnsureTable()
        {
            var table = _payload["table"] as JObject;
            if (table != null) return table;
            table = new JObject();
            _payload["table"] = table;
            return table;
        }

        private static JArray EnsureArray(JObject owner, string name)
        {
            var array = owner[name] as JArray;
            if (array != null) return array;
            array = new JArray();
            owner[name] = array;
            return array;
        }

        private JObject Cell(int row, int column)
        {
            return (JObject)((JArray)((JObject)EnsureArray(EnsureTable(), "rows")[row])["cells"])[column];
        }

        private static JObject CreateColumn(int index)
        {
            return new JObject
            {
                ["key"] = "column" + (index + 1),
                ["title"] = ColumnName(index),
                ["unit"] = string.Empty,
                ["widthMillimeters"] = 36d,
                ["decimalPlaces"] = 0,
                ["required"] = false
            };
        }

        private static JObject CreateRow(JArray columns)
        {
            var cells = new JArray();
            for (var index = 0; index < columns.Count; index++) cells.Add(CreateCell(index));
            return new JObject
            {
                ["rowId"] = "row-" + Guid.NewGuid().ToString("N"),
                ["rowType"] = "Data",
                ["keepTogether"] = true,
                ["cells"] = cells
            };
        }

        private static JObject CreateCell(int columnIndex)
        {
            return new JObject
            {
                ["cellId"] = "cell-" + Guid.NewGuid().ToString("N"),
                ["columnKey"] = "column" + (columnIndex + 1),
                ["displayValue"] = string.Empty,
                ["numericValue"] = JValue.CreateNull(),
                ["unit"] = string.Empty,
                ["fieldPath"] = string.Empty,
                ["formula"] = string.Empty,
                ["linkGroupId"] = string.Empty,
                ["state"] = "unknown",
                ["source"] = "手工编辑",
                ["sourceHandles"] = new JArray(),
                ["rowSpan"] = 1,
                ["columnSpan"] = 1,
                ["alignment"] = "center",
                ["horizontalPaddingMillimeters"] = 1d,
                ["borderColorIndex"] = JValue.CreateNull(),
                ["fillColorIndex"] = JValue.CreateNull(),
                ["textColorIndex"] = JValue.CreateNull()
            };
        }

        private static string ColumnName(int index)
        {
            var value = index + 1;
            var name = string.Empty;
            while (value > 0) { value--; name = (char)('A' + value % 26) + name; value /= 26; }
            return name;
        }

        private sealed class MergeSnapshot
        {
            public int Top;
            public int Bottom;
            public int Left;
            public int Right;
            public JObject Anchor;

            public MergeSnapshot Clone()
            {
                return new MergeSnapshot { Top = Top, Bottom = Bottom, Left = Left, Right = Right,
                    Anchor = (JObject)Anchor.DeepClone() };
            }
        }

        private static string CellDisplayValue(JObject cell)
        {
            if (cell == null) return string.Empty;
            var value = (string)cell["displayValue"] ?? string.Empty;
            return string.IsNullOrWhiteSpace((string)cell["cadObjectAssetPath"])
                ? value : (value.Length == 0 ? "[CAD 对象]" : value + "  [CAD 对象]");
        }

        private void SelectCell(int row, int column)
        {
            if (row < 0 || column < 0 || row >= _grid.Rows.Count || column >= _grid.Columns.Count) return;
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[row].Cells[column];
            _grid.CurrentCell.Selected = true;
        }

        private void Finish(CadTablePreviewAction action)
        {
            try
            {
                if (action == CadTablePreviewAction.ExportExcel || action == CadTablePreviewAction.InsertCad) CommitEdits();
                if (action == CadTablePreviewAction.ExportExcel || action == CadTablePreviewAction.InsertCad)
                {
                    LastScale = _scale.Text;
                    LastTextStyle = _textStyle.Text.Trim();
                    LastTextHeight = _textHeight.Value;
                    LastOriginalCadSize = _originalCadSize.Checked;
                    LastInsertType = _insertType.Text;
                    SaveDefaults();
                }
                SelectedAction = action;
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void GridCellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            e.Cancel = true;
        }

        private void SaveDefaults()
        {
            var table = EnsureTable();
            JObject selected = null;
            if (_grid.CurrentCell != null) selected = Cell(_grid.CurrentCell.RowIndex, _grid.CurrentCell.ColumnIndex);
            CadTableDefaultsStore.Save(new JObject
            {
                ["scale"] = _scale.Text,
                ["textStyle"] = _textStyle.Text.Trim(),
                ["textHeightMillimeters"] = (double)_textHeight.Value,
                ["useOriginalCadSize"] = _originalCadSize.Checked,
                ["insertTypeLabel"] = _insertType.Text,
                ["outerBorderColorIndex"] = table["outerBorderColorIndex"]?.DeepClone(),
                ["innerBorderColorIndex"] = table["innerBorderColorIndex"]?.DeepClone(),
                ["outerBorderWeightMillimeters"] = table["outerBorderWeightMillimeters"]?.DeepClone(),
                ["innerBorderWeightMillimeters"] = table["innerBorderWeightMillimeters"]?.DeepClone(),
                ["horizontalPaddingMillimeters"] = selected?["horizontalPaddingMillimeters"]?.DeepClone() ?? new JValue(1d),
                ["cellBorderColorIndex"] = selected?["borderColorIndex"]?.DeepClone(),
                ["cellFillColorIndex"] = selected?["fillColorIndex"]?.DeepClone(),
                ["cellTextColorIndex"] = selected?["textColorIndex"]?.DeepClone()
            });
        }

        private void ShowCellEditor(int[] merge)
        {
            if (merge == null || merge.Length < 4 || IsDisposed || _openingMergedEditor) return;
            _openingMergedEditor = true;
            try
            {
            var row = merge[0];
            var column = merge[2];
            var cell = Cell(row, column);
            var formula = (string)cell["formula"];
            var value = string.IsNullOrWhiteSpace(formula)
                ? (string)cell["displayValue"] ?? string.Empty : formula;
            var first = SpreadsheetFormulaEngine.CellAddress(merge[0], merge[2]);
            var last = SpreadsheetFormulaEngine.CellAddress(merge[1], merge[3]);
            var range = first == last ? first : first + ":" + last;
            using (var dialog = new MergedCellEditorForm(range, value))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                RecordUndo();
                var updated = dialog.CellText ?? string.Empty;
                if (updated.TrimStart().StartsWith("=", StringComparison.Ordinal))
                    cell["formula"] = updated.Trim();
                else
                {
                    cell["formula"] = string.Empty;
                    cell["displayValue"] = updated;
                }
                PropagateLinkedCell(cell);
                RecalculateFormulas();
                LoadTable();
                SelectCell(row, column);
            }
            }
            finally { _openingMergedEditor = false; }
        }

        private void GridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var cell = Cell(e.RowIndex, e.ColumnIndex);
            var value = Convert.ToString(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
            if (value.TrimStart().StartsWith("=", StringComparison.Ordinal)) cell["formula"] = value.Trim();
            else { cell["formula"] = string.Empty; cell["displayValue"] = value; }
            PropagateLinkedCell(cell);
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) { RecalculateFormulas(); RefreshCalculatedValues(); }
            }));
        }

        private void RecalculateFormulas()
        {
            var table = EnsureTable();
            var rows = EnsureArray(table, "rows").OfType<JObject>().ToList();
            var columns = EnsureArray(table, "columns");
            if (rows.Count == 0 || columns.Count == 0) return;
            var contents = new string[rows.Count, columns.Count];
            for (var row = 0; row < rows.Count; row++)
            {
                var cells = EnsureArray(rows[row], "cells");
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = cells[column] as JObject;
                    var formula = (string)cell?["formula"];
                    contents[row, column] = string.IsNullOrWhiteSpace(formula)
                        ? (string)cell?["displayValue"] ?? string.Empty : formula;
                }
            }
            var calculated = SpreadsheetFormulaEngine.Calculate(contents);
            for (var row = 0; row < rows.Count; row++)
                for (var column = 0; column < columns.Count; column++)
                {
                    var cell = Cell(row, column);
                    if (!string.IsNullOrWhiteSpace((string)cell["formula"])) cell["displayValue"] = calculated[row, column];
                    double numeric;
                    cell["numericValue"] = double.TryParse(calculated[row, column], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out numeric) ? (JToken)numeric : JValue.CreateNull();
                }
        }

        private void PropagateLinkedCell(JObject source)
        {
            var groupId = (string)source["linkGroupId"];
            if (string.IsNullOrWhiteSpace(groupId)) return;
            foreach (var row in EnsureArray(EnsureTable(), "rows").OfType<JObject>())
                foreach (var target in EnsureArray(row, "cells").OfType<JObject>())
                {
                    if (ReferenceEquals(target, source) || !string.Equals((string)target["linkGroupId"], groupId,
                        StringComparison.Ordinal)) continue;
                    target["formula"] = (string)source["formula"] ?? string.Empty;
                    target["displayValue"] = (string)source["displayValue"] ?? string.Empty;
                }
        }

        private void RefreshCalculatedValues()
        {
            _loading = true;
            try
            {
                for (var row = 0; row < _grid.Rows.Count; row++)
                    for (var column = 0; column < _grid.Columns.Count; column++)
                        _grid.Rows[row].Cells[column].Value = (string)Cell(row, column)["displayValue"] ?? string.Empty;
                _grid.Invalidate();
            }
            finally { _loading = false; }
        }

        private void PickIndexedColor(string propertyName, ColorPickerField field, short defaultIndex, string caption)
        {
            if (_grid.SelectedCells.Count == 0)
            {
                MessageBox.Show(this, "请先选择要修改的单元格。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var current = _grid.CurrentCell == null ? (short?)null : (short?)Cell(
                _grid.CurrentCell.RowIndex, _grid.CurrentCell.ColumnIndex)[propertyName];
            var dialog = new AcColorDialog
            {
                Color = AcColor.FromColorIndex(AcColorMethod.ByAci, current ?? defaultIndex)
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            if (dialog.Color == null || dialog.Color.ColorMethod != AcColorMethod.ByAci ||
                dialog.Color.ColorIndex < 1 || dialog.Color.ColorIndex > 255)
            {
                MessageBox.Show(this, caption + "请选择 1-255 的 AutoCAD 索引颜色。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell[propertyName] = dialog.Color.ColorIndex;
                ApplyCellAppearance(selected.RowIndex, selected.ColumnIndex, cell);
            }
            field.SetColor(AciDisplayColor(dialog.Color.ColorIndex, Color.Black), "ACI " + dialog.Color.ColorIndex);
            ApplyGridColors();
        }

        private void ShowSelectedAppearance()
        {
            if (_grid.CurrentCell == null) return;
            var cell = Cell(_grid.CurrentCell.RowIndex, _grid.CurrentCell.ColumnIndex);
            SetColorField(_borderColor, (short?)cell["borderColorIndex"]);
            SetColorField(_fillColor, (short?)cell["fillColorIndex"]);
            SetColorField(_textColor, (short?)cell["textColorIndex"]);
            var padding = (decimal?)cell["horizontalPaddingMillimeters"] ?? 1m;
            _showingAppearance = true;
            try
            {
                _horizontalPadding.Value = Math.Max(_horizontalPadding.Minimum, Math.Min(_horizontalPadding.Maximum, padding));
            }
            finally { _showingAppearance = false; }
        }

        private void ClearSelectedFill()
        {
            SetSelectedProperty("fillColorIndex", JValue.CreateNull());
        }

        private void ResetSelectedAppearance()
        {
            if (_grid.SelectedCells.Count == 0) return;
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell["borderColorIndex"] = JValue.CreateNull();
                cell["fillColorIndex"] = JValue.CreateNull();
                cell["textColorIndex"] = JValue.CreateNull();
                cell["horizontalPaddingMillimeters"] = 1d;
                cell["sourceHorizontalPaddingCadUnits"] = JValue.CreateNull();
                ApplyCellAppearance(selected.RowIndex, selected.ColumnIndex, cell);
            }
            ShowSelectedAppearance();
            ApplyGridColors();
        }

        private void SetSelectedProperty(string propertyName, JToken value)
        {
            if (_grid.SelectedCells.Count == 0) return;
            CommitEdits();
            RecordUndo();
            foreach (DataGridViewCell selected in _grid.SelectedCells)
            {
                var cell = Cell(selected.RowIndex, selected.ColumnIndex);
                if ((int?)cell["rowSpan"] == 0 || (int?)cell["columnSpan"] == 0) continue;
                cell[propertyName] = value == null ? JValue.CreateNull() : value.DeepClone();
                ApplyCellAppearance(selected.RowIndex, selected.ColumnIndex, cell);
            }
            ShowSelectedAppearance();
            ApplyGridColors();
        }

        private static void SetColorField(ColorPickerField field, short? index)
        {
            field.SetColor(AciDisplayColor(index, Color.Black), index.HasValue ? "ACI " + index.Value : "默认");
        }

        private void ApplyGridColors()
        {
            _grid.BackgroundColor = Color.White;
            _grid.DefaultCellStyle.BackColor = Color.White;
            _grid.DefaultCellStyle.ForeColor = Color.Black;
            _grid.GridColor = SystemColors.ControlDark;
            for (var row = 0; row < _grid.Rows.Count; row++)
                for (var column = 0; column < _grid.Columns.Count; column++)
                    ApplyCellAppearance(row, column, Cell(row, column));
            _grid.Invalidate();
        }

        private void RecordUndo()
        {
            if (_loading || _restoringUndo) return;
            _undoStack.Push((JObject)_payload.DeepClone());
            while (_undoStack.Count > 60)
            {
                var retained = _undoStack.Take(60).Reverse().ToArray();
                _undoStack.Clear();
                foreach (var state in retained) _undoStack.Push(state);
            }
        }

        private void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.Z) return;
            UndoLastChange();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void BeginInsertSettingChange()
        {
            if (_loading || _restoringUndo) return;
            CommitEdits();
            RecordUndo();
        }

        private void GridSizeChanged()
        {
            if (!_loading && !_restoringUndo)
            {
                RecordUndo();
                CommitEdits();
            }
            _grid.Invalidate();
        }

        private void UndoLastChange()
        {
            if (_undoStack.Count == 0)
            {
                _summary.Text = "没有可撤销的操作。";
                return;
            }
            var row = _grid.CurrentCell == null ? 0 : _grid.CurrentCell.RowIndex;
            var column = _grid.CurrentCell == null ? 0 : _grid.CurrentCell.ColumnIndex;
            _restoringUndo = true;
            try
            {
                var state = _undoStack.Pop();
                _payload.RemoveAll();
                foreach (var property in state.Properties())
                    _payload.Add(property.Name, property.Value.DeepClone());
                NormalizePayload();
                LoadTable();
                ApplyInsertOptionsFromPayload();
                if (_insertCadButton != null) _insertCadButton.Text = _payload["sourceEdit"] is JObject
                    ? "更新当前表格" : "按设置插入 CAD";
                SelectCell(Math.Min(row, _grid.Rows.Count - 1), Math.Min(column, _grid.Columns.Count - 1));
                _summary.Text = "已撤销上一步操作。";
            }
            finally { _restoringUndo = false; }
        }

        private void PickTableBorderColor(bool outer)
        {
            var table = EnsureTable();
            var property = outer ? "outerBorderColorIndex" : "innerBorderColorIndex";
            var current = (short?)table[property] ?? 7;
            var dialog = new AcColorDialog { Color = AcColor.FromColorIndex(AcColorMethod.ByAci, current) };
            if (dialog.ShowDialog() != DialogResult.OK || dialog.Color == null ||
                dialog.Color.ColorMethod != AcColorMethod.ByAci || dialog.Color.ColorIndex < 1 || dialog.Color.ColorIndex > 255) return;
            RecordUndo();
            table[property] = dialog.Color.ColorIndex;
            foreach (var row in EnsureArray(table, "rows").OfType<JObject>())
                foreach (var cell in EnsureArray(row, "cells").OfType<JObject>())
                    cell["borderColorIndex"] = JValue.CreateNull();
            ShowTableBorderAppearance();
            ApplyGridColors();
            _grid.Invalidate();
        }

        private void ApplyTableBorderWeight(bool outer)
        {
            if (_loading || _showingTableBorder) return;
            var combo = outer ? _outerBorderWeight : _innerBorderWeight;
            double weight;
            if (!double.TryParse(combo.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out weight) || weight < 0d) return;
            RecordUndo();
            EnsureTable()[outer ? "outerBorderWeightMillimeters" : "innerBorderWeightMillimeters"] = weight;
            _grid.Invalidate();
        }

        private void ShowTableBorderAppearance()
        {
            var table = EnsureTable();
            _showingTableBorder = true;
            try
            {
                SetColorField(_outerBorderColor, (short?)table["outerBorderColorIndex"]);
                SetColorField(_innerBorderColor, (short?)table["innerBorderColorIndex"]);
                _outerBorderWeight.Text = ((double?)table["outerBorderWeightMillimeters"] ?? .25d)
                    .ToString("0.00", CultureInfo.InvariantCulture);
                _innerBorderWeight.Text = ((double?)table["innerBorderWeightMillimeters"] ?? .13d)
                    .ToString("0.00", CultureInfo.InvariantCulture);
            }
            finally { _showingTableBorder = false; }
        }

        private static Color AciDisplayColor(short? index, Color fallback)
        {
            if (!index.HasValue || index.Value < 1 || index.Value > 255) return fallback;
            if (index.Value == 7) return Color.Black;
            var rgb = Autodesk.AutoCAD.Colors.EntityColor.LookUpRgb((byte)index.Value);
            return Color.FromArgb((int)((rgb >> 16) & 255), (int)((rgb >> 8) & 255), (int)(rgb & 255));
        }

        private Image CadObjectPreview(JObject cell)
        {
            if (cell == null) return null;
            var path = ((string)cell["cadObjectPreviewPath"] ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                var assetPath = ((string)cell["cadObjectAssetPath"] ?? string.Empty).Trim();
                if (assetPath.Length > 0) path = Path.ChangeExtension(assetPath, ".png");
            }
            if (path.Length == 0 || !File.Exists(path)) return null;
            Image cached;
            if (_cadObjectPreviews.TryGetValue(path, out cached)) return cached;
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var source = Image.FromStream(stream))
                    cached = new Bitmap(source);
                _cadObjectPreviews[path] = cached;
                return cached;
            }
            catch { return null; }
        }

        private static void DrawImageContained(Graphics graphics, Image image, Rectangle bounds)
        {
            if (graphics == null || image == null || bounds.Width <= 0 || bounds.Height <= 0) return;
            var scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            var target = new Rectangle(bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
            graphics.DrawImage(image, target);
        }

        private static void DrawCadObjectText(Graphics graphics, string value, Rectangle bounds,
            Color foreground, string alignment, double horizontalPaddingMillimeters)
        {
            if (graphics == null || string.IsNullOrWhiteSpace(value) || bounds.Width <= 4 || bounds.Height <= 4) return;
            var padding = Math.Max(2, Math.Min(30,
                (int)Math.Round(Math.Max(0d, horizontalPaddingMillimeters) * 3d)));
            var textBounds = new Rectangle(bounds.Left + padding, Math.Max(bounds.Top + 2, bounds.Bottom - 20),
                Math.Max(1, bounds.Width - padding * 2), 18);
            using (var background = new SolidBrush(Color.FromArgb(220, Color.White)))
                graphics.FillRectangle(background, textBounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if (string.Equals(alignment, "left", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Left;
            else if (string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)) flags |= TextFormatFlags.Right;
            else flags |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(graphics, value, SystemFonts.MessageBoxFont, textBounds, foreground,
                flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var image in _cadObjectPreviews.Values) image.Dispose();
                _cadObjectPreviews.Clear();
            }
            base.Dispose(disposing);
        }

        private void SaveAsTemplate()
        {
            CommitEdits();
            var table = EnsureTable();
            var initial = ((string)table["title"] ?? string.Empty).Trim();
            using (var dialog = new CadTableNameForm(initial))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var existing = CadTableTemplateStore.Load().FirstOrDefault(item => string.Equals(
                    item.Name, dialog.TemplateName, StringComparison.OrdinalIgnoreCase));
                if (existing != null && MessageBox.Show(this, "常用表格“" + dialog.TemplateName + "”已存在，是否覆盖？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                table["title"] = dialog.TemplateName;
                var templatePayload = (JObject)_payload.DeepClone();
                templatePayload.Remove("sourceEdit");
                templatePayload.Remove("pendingCadObjectUndo");
                CadTableTemplateStore.Save(dialog.TemplateName, templatePayload);
                _summary.Text = "已保存常用表格“" + dialog.TemplateName + "”。下次运行 CE 可直接打开。";
                RefreshTemplates(dialog.TemplateName);
            }
        }

        private void RefreshTemplates(string selectedName = null)
        {
            var previous = selectedName ?? (_templates.SelectedItem as CadTableTemplate)?.Name;
            _templates.BeginUpdate();
            try
            {
                _templates.Items.Clear();
                foreach (var template in CadTableTemplateStore.Load()) _templates.Items.Add(template);
                if (_templates.Items.Count == 0) return;
                var index = 0;
                if (!string.IsNullOrWhiteSpace(previous))
                    for (var candidate = 0; candidate < _templates.Items.Count; candidate++)
                        if (string.Equals(((_templates.Items[candidate] as CadTableTemplate)?.Name), previous,
                            StringComparison.OrdinalIgnoreCase)) { index = candidate; break; }
                _templates.SelectedIndex = index;
            }
            finally { _templates.EndUpdate(); }
        }

        private void LoadSelectedTemplate()
        {
            var selected = _templates.SelectedItem as CadTableTemplate;
            if (selected == null) return;
            CommitEdits();
            RecordUndo();
            _payload.RemoveAll();
            foreach (var property in selected.Payload.Properties())
                _payload.Add(property.Name, property.Value.DeepClone());
            NormalizePayload();
            LoadTable();
            ApplyInsertOptionsFromPayload();
            if (_insertCadButton != null) _insertCadButton.Text = _payload["sourceEdit"] is JObject
                ? "更新当前表格" : "按设置插入 CAD";
            _summary.Text = "已打开常用表格“" + selected.Name + "”。";
        }

        private void DeleteSelectedTemplate()
        {
            var selected = _templates.SelectedItem as CadTableTemplate;
            if (selected == null) return;
            if (MessageBox.Show(this, "确定删除常用表格“" + selected.Name + "”？", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            CadTableTemplateStore.Delete(selected.Id);
            RefreshTemplates();
        }

        private void ApplyInsertOptionsFromPayload()
        {
            var options = _payload["cadInsertOptions"] as JObject;
            if (options == null) return;
            var scale = (double?)options["scale"];
            if (scale.HasValue) _scale.Text = scale.Value.ToString("0.###", CultureInfo.InvariantCulture);
            _originalCadSize.Checked = (bool?)options["useOriginalCadSize"] == true &&
                (bool?)_payload["hasOriginalCadSize"] == true;
            _insertType.Text = string.Equals((string)options["insertType"], "tianzheng",
                StringComparison.OrdinalIgnoreCase) ? "天正表格（T20）" : "AutoCAD 原生表格";
            if (_payload["sourceEdit"] is JObject) _insertType.Text = "AutoCAD 原生表格";
            var style = ((string)options["textStyle"] ?? string.Empty).Trim();
            if (style.Length > 0 && _textStyle.Items.Contains(style)) _textStyle.Text = style;
            var height = (decimal?)options["textHeightMillimeters"];
            if (height.HasValue) _textHeight.Value = Math.Max(_textHeight.Minimum,
                Math.Min(_textHeight.Maximum, height.Value));
            UpdateSizeControls();
        }

        private void UpdateSizeControls()
        {
            var tianzheng = _insertType.SelectedIndex == 1;
            var available = (bool?)_payload["hasOriginalCadSize"] == true && !tianzheng;
            _originalCadSize.Enabled = available;
            if (!available) _originalCadSize.Checked = false;
            _scale.Enabled = !_originalCadSize.Checked;
            _textHeight.Enabled = !_originalCadSize.Checked;
            _originalCadSize.Text = tianzheng ? "天正表格不支持原尺寸" :
                available ? "保持原 CAD 尺寸" : "原 CAD 尺寸不可用";
        }

        private static DataGridViewContentAlignment GridAlignment(string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)) return DataGridViewContentAlignment.MiddleLeft;
            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)) return DataGridViewContentAlignment.MiddleRight;
            return DataGridViewContentAlignment.MiddleCenter;
        }

        private static ComboBox Combo(IEnumerable<string> values)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Height = 28, Margin = new Padding(3, 3, 3, 2) };
            if (values != null) combo.Items.AddRange(values.Cast<object>().ToArray());
            return combo;
        }

        private static Control Separator()
        {
            return new Label { Text = "|", AutoSize = true, ForeColor = Color.LightGray, Margin = new Padding(5, 8, 5, 0) };
        }

        private static void ConfigureThreeColumns(TableLayoutPanel panel)
        {
            panel.ColumnStyles.Clear();
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334f));
            for (var row = 0; row < panel.RowCount; row++)
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / panel.RowCount));
        }

        private static void AddGridControl(TableLayoutPanel panel, Control control, int column, int row, int columnSpan = 1)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(3, 2, 3, 2);
            panel.Controls.Add(control, column, row);
            if (columnSpan > 1) panel.SetColumnSpan(control, columnSpan);
        }

        private static Label FieldLabel(string text)
        {
            return new Label { Text = text, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }

        private static Button GridButtonFor(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = false,
                AutoEllipsis = true,
                Image = CadTableToolIcons.Create(text),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                TextAlign = ContentAlignment.MiddleCenter
            };
            button.Click += (sender, args) => action();
            new ToolTip().SetToolTip(button, text);
            return button;
        }

        private static GroupBox Group(string title, Control content)
        {
            var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(5, 15, 5, 3), Margin = new Padding(2) };
            group.Controls.Add(content);
            return group;
        }

        private static Button ButtonFor(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                Margin = new Padding(3, 2, 0, 2),
                Image = CadTableToolIcons.Create(text),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText
            };
            button.Click += (sender, args) => action();
            return button;
        }
    }

    internal sealed class ColorPickerField : UserControl
    {
        private readonly Label _name = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Panel _swatch = new Panel { Dock = DockStyle.Right, Width = 34, Margin = new Padding(3) };
        private readonly Label _value = new Label { Dock = DockStyle.Right, Width = 50, TextAlign = ContentAlignment.MiddleCenter };

        public ColorPickerField(string name)
        {
            Height = 30;
            _name.Text = name;
            _swatch.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_name);
            Controls.Add(_value);
            Controls.Add(_swatch);
            foreach (Control control in new Control[] { _name, _value, _swatch })
                control.Click += (sender, args) => OnClick(args);
            Cursor = Cursors.Hand;
        }

        public void SetColor(Color color, string value)
        {
            _swatch.BackColor = color;
            _value.Text = value ?? string.Empty;
        }
    }

    internal static class CadTableToolIcons
    {
        public static Image Create(string command)
        {
            var bitmap = new Bitmap(18, 18);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var dark = new Pen(Color.FromArgb(31, 48, 61), 1.6f))
            using (var blue = new Pen(Color.FromArgb(18, 119, 191), 1.8f))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var text = command ?? string.Empty;
                if (text.Contains("撤销"))
                {
                    graphics.DrawArc(blue, 4, 4, 10, 10, 210, 250);
                    graphics.DrawLines(blue, new[] { new Point(5, 3), new Point(2, 7), new Point(7, 8) });
                }
                else if (text.Contains("公式"))
                {
                    using (var font = new Font("Times New Roman", 11f, FontStyle.Italic, GraphicsUnit.Pixel))
                        graphics.DrawString("fx", font, Brushes.DodgerBlue, 1, 2);
                }
                else if (text.Contains("对齐") || text == "居中")
                {
                    var mode = text.Contains("左") ? 3 : text.Contains("右") ? 7 : 5;
                    graphics.DrawLine(dark, mode, 4, mode + 8, 4);
                    graphics.DrawLine(dark, mode, 8, mode + 6, 8);
                    graphics.DrawLine(dark, mode, 12, mode + 8, 12);
                }
                else if (text.Contains("关联"))
                {
                    graphics.DrawArc(blue, 1, 5, 9, 8, 120, 240);
                    graphics.DrawArc(dark, 8, 5, 9, 8, -60, 240);
                    if (text.Contains("取消")) graphics.DrawLine(Pens.Red, 3, 15, 15, 3);
                }
                else if (text.Contains("CAD") || text.Contains("拾取"))
                {
                    graphics.DrawRectangle(dark, 2, 2, 13, 13);
                    graphics.DrawLine(blue, 4, 13, 9, 5);
                    graphics.DrawLine(blue, 9, 5, 14, 12);
                }
                else if (text.Contains("合并"))
                {
                    graphics.DrawRectangle(dark, 2, 3, 14, 12);
                    graphics.DrawLine(dark, 2, 9, 16, 9);
                    if (text.Contains("取消")) graphics.DrawLine(Pens.Red, 4, 14, 14, 4);
                    else graphics.DrawLine(blue, 9, 5, 9, 13);
                }
                else if (text.Contains("行") || text.Contains("列"))
                {
                    graphics.DrawRectangle(dark, 2, 3, 12, 12);
                    graphics.DrawLine(dark, 2, 9, 14, 9);
                    graphics.DrawLine(dark, 8, 3, 8, 15);
                    var add = text.Contains("添加");
                    using (var pen = new Pen(add ? Color.DodgerBlue : Color.Crimson, 1.8f))
                    {
                        graphics.DrawLine(pen, 13, 2, 17, 2);
                        if (add) graphics.DrawLine(pen, 15, 0, 15, 4);
                    }
                }
                else
                {
                    graphics.DrawRectangle(dark, 2, 2, 13, 13);
                    graphics.DrawLine(blue, 4, 6, 13, 6);
                    graphics.DrawLine(blue, 4, 10, 11, 10);
                }
            }
            return bitmap;
        }
    }

    internal sealed class MergedCellEditorForm : Form
    {
        private readonly TextBox _content = new TextBox();
        public string CellText { get { return _content.Text; } }

        public MergedCellEditorForm(string range, string value)
        {
            Text = "编辑合并单元格 " + range;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 260);
            MinimumSize = new Size(440, 210);
            Font = new Font("Microsoft YaHei UI", 9f);

            _content.Multiline = true;
            _content.AcceptsReturn = true;
            _content.AcceptsTab = true;
            _content.ScrollBars = ScrollBars.Vertical;
            _content.Dock = DockStyle.Fill;
            _content.Text = value ?? string.Empty;
            _content.SelectAll();

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8),
                WrapContents = false
            };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84, Height = 30 };
            var apply = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 84, Height = 30 };
            actions.Controls.Add(cancel);
            actions.Controls.Add(apply);
            AcceptButton = apply;
            CancelButton = cancel;
            Controls.Add(_content);
            Controls.Add(actions);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _content.Focus();
        }
    }

    internal sealed class FormulaBuilderForm : Form
    {
        private readonly ComboBox _function = new ComboBox();
        private readonly TextBox _formula = new TextBox();
        private readonly Label _selection = new Label();
        private readonly Func<string> _selectedRange;
        private readonly Action<string> _apply;

        public FormulaBuilderForm(int targetRow, int targetColumn, string existingFormula,
            Func<string> selectedRange, Action<string> apply)
        {
            _selectedRange = selectedRange;
            _apply = apply;
            Text = "fx 插入公式";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            ClientSize = new Size(470, 220);
            Font = new Font("Microsoft YaHei UI", 9f);

            var target = SpreadsheetFormulaEngine.CellAddress(targetRow, targetColumn);
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(LabelFor("目标单元格"), 0, 0);
            layout.Controls.Add(new Label { Text = target, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 1, 0);
            layout.Controls.Add(LabelFor("函数"), 0, 1);
            _function.Dock = DockStyle.Fill;
            _function.DropDownStyle = ComboBoxStyle.DropDownList;
            _function.Items.AddRange(new object[]
            {
                "直接引用", "SUM 求和", "AVERAGE 平均值", "MIN 最小值", "MAX 最大值",
                "COUNT 数量", "ROUND 四舍五入", "IF 条件"
            });
            _function.SelectedIndex = 1;
            layout.Controls.Add(_function, 1, 1);

            layout.Controls.Add(LabelFor("当前选区"), 0, 2);
            var rangePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            _selection.AutoSize = false;
            _selection.Width = 215;
            _selection.Height = 30;
            _selection.TextAlign = ContentAlignment.MiddleLeft;
            rangePanel.Controls.Add(_selection);
            rangePanel.Controls.Add(Button("使用选区", UseSelection));
            layout.Controls.Add(rangePanel, 1, 2);

            layout.Controls.Add(LabelFor("公式"), 0, 3);
            _formula.Dock = DockStyle.Fill;
            _formula.Text = string.IsNullOrWhiteSpace(existingFormula) ? "=" : existingFormula;
            layout.Controls.Add(_formula, 1, 3);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 5, 0, 0)
            };
            actions.Controls.Add(Button("应用", Apply));
            actions.Controls.Add(Button("关闭", Close));
            layout.Controls.Add(actions, 0, 4);
            layout.SetColumnSpan(actions, 2);
            Controls.Add(layout);
        }

        public void UpdateSelection(string range)
        {
            _selection.Text = string.IsNullOrWhiteSpace(range) ? "未选择" : range;
        }

        private void UseSelection()
        {
            var range = _selectedRange() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(range)) return;
            var first = range.Split(':')[0];
            switch (_function.SelectedIndex)
            {
                case 0: _formula.Text = "=" + first; break;
                case 1: _formula.Text = "=SUM(" + range + ")"; break;
                case 2: _formula.Text = "=AVERAGE(" + range + ")"; break;
                case 3: _formula.Text = "=MIN(" + range + ")"; break;
                case 4: _formula.Text = "=MAX(" + range + ")"; break;
                case 5: _formula.Text = "=COUNT(" + range + ")"; break;
                case 6: _formula.Text = "=ROUND(" + first + ",2)"; break;
                case 7: _formula.Text = "=IF(" + first + ">0,1,0)"; break;
            }
            _formula.Focus();
            _formula.SelectionStart = _formula.TextLength;
        }

        private void Apply()
        {
            if (string.IsNullOrWhiteSpace(_formula.Text) || _formula.Text.Trim() == "=")
            {
                MessageBox.Show(this, "请先选择单元格范围并生成公式。", Text);
                return;
            }
            _apply(_formula.Text.Trim());
        }

        private static Label LabelFor(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 29, Margin = new Padding(3, 1, 3, 1) };
            button.Click += (sender, args) => action();
            return button;
        }
    }
}
