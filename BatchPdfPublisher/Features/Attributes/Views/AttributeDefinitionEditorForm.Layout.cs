using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed partial class AttributeDefinitionEditorForm
    {
        private readonly Label _selectionCount = new Label();
        private readonly Label _drawingName = new Label();
        private readonly Label _status = new Label();

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
                Margin = Padding.Empty, Padding = Padding.Empty, BackColor = CadDialogTheme.Canvas };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);
            root.Controls.Add(TitleBar(), 0, 0);
            root.Controls.Add(BuildHeader(), 0, 1);
            root.Controls.Add(BuildBatchCard(), 0, 2);
            root.Controls.Add(BuildGridCard(), 0, 3);
            root.Controls.Add(BuildFooter(), 0, 4);
            _rows.ListChanged += (sender, args) => UpdateSelectionCount();
            UpdateSelectionCount();
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty, Padding = new Padding(14, 18, 14, 18) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
            header.Controls.Add(CadAttributeUi.Label("属性定义", true), 0, 0);
            _drawingName.Text = "当前图纸：" + System.IO.Path.GetFileName(_document.Name);
            _drawingName.Dock = DockStyle.Fill;
            _drawingName.TextAlign = ContentAlignment.MiddleLeft;
            _drawingName.ForeColor = CadDialogTheme.Muted;
            _drawingName.AutoEllipsis = true;
            header.Controls.Add(_drawingName, 1, 0);
            var pick = Command("拾取图块", PickBlock, PublisherForm.UiIcon.Select, true);
            pick.Margin = new Padding(0, 0, 8, 0);
            header.Controls.Add(pick, 2, 0);
            header.Controls.Add(CadAttributeUi.Label("图块名称"), 3, 0);
            header.Controls.Add(CadAttributeUi.Input(_blockName), 4, 0);
            _title.Text = "尚未选择图块";
            _title.Dock = DockStyle.Fill;
            _title.AutoEllipsis = true;
            _title.TextAlign = ContentAlignment.MiddleRight;
            _title.ForeColor = CadDialogTheme.Muted;
            header.Controls.Add(_title, 6, 0);
            return header;
        }

        private Control BuildBatchCard()
        {
            var card = CadDialogTheme.Card();
            card.Margin = new Padding(12, 8, 12, 8);
            card.Padding = new Padding(14, 8, 14, 8);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 3) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            top.Controls.Add(CadAttributeUi.Label("批量设置  (仅应用到勾选行)", true), 0, 0);
            top.Controls.Add(Command("全部勾选", () => SetAllSelected(true), PublisherForm.UiIcon.List), 2, 0);
            top.Controls.Add(Command("全部取消", () => SetAllSelected(false)), 4, 0);
            content.Controls.Add(top, 0, 0);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
                BackColor = CadDialogTheme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
            fields.Layout += (sender, args) => fields.Invalidate(true);
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (var i = 0; i < 7; i++)
                fields.ColumnStyles.Add(new ColumnStyle(i % 2 == 0 ? SizeType.Percent : SizeType.Absolute,
                    i % 2 == 0 ? 25 : 12));
            fields.Controls.Add(BatchField("文字高度", CadAttributeUi.Input(_batchHeight), () => ApplyBatchNumber(true)), 0, 0);
            fields.Controls.Add(BatchField("宽度因子", CadAttributeUi.Input(_batchWidthFactor), () => ApplyBatchNumber(false)), 2, 0);
            CadAttributeUi.Combo(_batchTextStyle);
            fields.Controls.Add(BatchField("文字样式", _batchTextStyle, () => ApplyBatchChoice(true)), 4, 0);
            _batchAlignment.Items.AddRange(new object[] { "左下", "中下", "右下", "左中", "居中", "右中", "左上", "中上", "右上" });
            _batchAlignment.SelectedIndex = 0;
            CadAttributeUi.Combo(_batchAlignment);
            fields.Controls.Add(BatchField("对齐方式", _batchAlignment, () => ApplyBatchChoice(false)), 6, 0);
            content.Controls.Add(fields, 0, 1);
            card.Controls.Add(content);
            return card;
        }

        private Control BatchField(string label, Control editor, Action apply)
        {
            var group = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 2,
                Margin = Padding.Empty, BackColor = CadDialogTheme.Surface };
            group.Layout += (sender, args) => group.Invalidate(true);
            group.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            group.RowStyles.Add(new RowStyle(SizeType.Absolute, CadDialogTheme.ControlHeight));
            group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            group.Controls.Add(CadAttributeUi.Label(label), 0, 0);
            editor.Margin = new Padding(0, 0, 8, 0);
            editor.Dock = DockStyle.Fill;
            group.Controls.Add(editor, 0, 1);
            group.Controls.Add(Command("应用", apply), 1, 1);
            return group;
        }

        private Control BuildGridCard()
        {
            var card = CadDialogTheme.Card();
            card.Margin = new Padding(12, 0, 12, 8);
            card.Padding = new Padding(8);
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2,
                ColumnCount = 1, BackColor = CadDialogTheme.Surface, Margin = Padding.Empty };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
                Padding = new Padding(14, 0, 14, 0), BackColor = CadDialogTheme.Surface };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.Controls.Add(CadAttributeUi.Label("属性定义", true), 0, 0);
            _selectionCount.Dock = DockStyle.Fill;
            _selectionCount.TextAlign = ContentAlignment.MiddleLeft;
            _selectionCount.ForeColor = CadDialogTheme.Muted;
            heading.Controls.Add(_selectionCount, 1, 0);
            content.Controls.Add(heading, 0, 0);
            ConfigureGrid();
            content.Controls.Add(new CadAttributeGridHost(_grid) { Dock = DockStyle.Fill }, 0, 1);
            card.Controls.Add(content);
            return card;
        }

        private void ConfigureGrid()
        {
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            CadAttributeUi.StyleGrid(_grid);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "修改", DataPropertyName = "IsSelected", Width = 62, Frozen = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性 TAG", DataPropertyName = "Tag", Width = 125 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "提示文字", DataPropertyName = "Prompt", Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "默认内容", DataPropertyName = "DefaultValue", Width = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文字高度", DataPropertyName = "Height", Width = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "宽度因子", DataPropertyName = "WidthFactor", Width = 95 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TextStyle", HeaderText = "文字样式", DataPropertyName = "TextStyle", Width = 138, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alignment", HeaderText = "对齐方式", DataPropertyName = "Alignment", Width = 105, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "隐藏", DataPropertyName = "Invisible", Width = 65 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "常量", DataPropertyName = "Constant", Width = 65, ReadOnly = true });
            _grid.DataSource = _rows;
            _grid.CellDoubleClick += GridCellDoubleClick;
            _grid.CellClick += GridChoiceClick;
            _grid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (sender, args) =>
            {
                if (args.RowIndex >= 0 && args.ColumnIndex == 0) UpdateSelectionCount();
            };
            _grid.CellPainting += (sender, args) =>
            {
                if (args.RowIndex < 0 || args.ColumnIndex < 0) return;
                var name = _grid.Columns[args.ColumnIndex].Name;
                if (name != "TextStyle" && name != "Alignment") return;
                args.Paint(args.CellBounds, DataGridViewPaintParts.All);
                TextRenderer.DrawText(args.Graphics, "⌄", Font,
                    new Rectangle(args.CellBounds.Right - 23, args.CellBounds.Top + 5, 16, args.CellBounds.Height - 8),
                    CadDialogTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                args.Handled = true;
            };
        }

        private void GridChoiceClick(object sender, DataGridViewCellEventArgs args)
        {
            if (args.RowIndex < 0 || _context == null) return;
            var name = _grid.Columns[args.ColumnIndex].Name;
            if (name != "TextStyle" && name != "Alignment") return;
            CommitGrid();
            var row = _grid.Rows[args.RowIndex].DataBoundItem as AttributeDefinitionEditRow;
            if (row == null) return;
            var values = name == "TextStyle" ? _context.TextStyles.ToArray() :
                new[] { "左下", "中下", "右下", "左中", "居中", "右中", "左上", "中上", "右上" };
            var entries = values.Select(value => Tuple.Create(value, (Action)(() =>
            {
                if (name == "TextStyle") row.TextStyle = value; else row.Alignment = value;
                _grid.InvalidateRow(args.RowIndex);
            }))).ToArray();
            var cell = _grid.GetCellDisplayRectangle(args.ColumnIndex, args.RowIndex, true);
            CadAttributeUi.Menu(_grid, entries, Math.Max(150, cell.Width), new Point(cell.Left, cell.Bottom));
        }

        private void UpdateSelectionCount()
        {
            _selectionCount.Text = "已勾选 " + _rows.Count(row => row.IsSelected) + " / " + _rows.Count;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4,
                Padding = new Padding(14, 11, 14, 11), BackColor = CadDialogTheme.Surface };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
            _status.Text = "双击属性行可定位 CAD 实例 · 修改尚未写入";
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = CadDialogTheme.Muted;
            _status.AutoEllipsis = true;
            footer.Controls.Add(_status, 0, 0);
            footer.Controls.Add(Command("关闭", Close), 1, 0);
            footer.Controls.Add(Command("应用并同步实例", Apply, PublisherForm.UiIcon.Save, true), 3, 0);
            return footer;
        }
    }
}
