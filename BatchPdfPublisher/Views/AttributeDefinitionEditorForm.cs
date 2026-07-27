using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed class AttributeDefinitionEditorForm : Form
    {
        private readonly Document _document;
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _title = new Label();
        private readonly TextBox _blockName = new TextBox();
        private readonly TextBox _batchHeight = new TextBox();
        private readonly TextBox _batchWidthFactor = new TextBox();
        private readonly ComboBox _batchTextStyle = new ComboBox();
        private readonly ComboBox _batchAlignment = new ComboBox();
        private readonly BindingList<AttributeDefinitionEditRow> _rows = new BindingList<AttributeDefinitionEditRow>();
        private readonly AttributeMarkerService _markers = new AttributeMarkerService();
        private AttributeDefinitionEditContext _context;

        public AttributeDefinitionEditorForm(Document document, string blockName = null)
        {
            _document = document;
            Text = "图块属性定义编辑器"; Width = 1240; Height = 680; MinimumSize = new System.Drawing.Size(900, 520); StartPosition = FormStartPosition.CenterParent;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi; SizeGripStyle = SizeGripStyle.Show;
            Build();
            FormClosed += (s, e) => _markers.Dispose();
            if (!string.IsNullOrWhiteSpace(blockName)) LoadContext(() => AttributeDefinitionEditorService.Read(_document, blockName));
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var pick = new Button { Text = "拾取图块", AutoSize = true }; pick.Click += (s, e) => PickBlock(); top.Controls.Add(pick);
            top.Controls.Add(new Label { Text = "图块名称", AutoSize = true, Margin = new Padding(14, 7, 3, 3) });
            _blockName.Width = 220; _blockName.Margin = new Padding(3, 3, 3, 3); top.Controls.Add(_blockName);
            _title.AutoSize = true; _title.Margin = new Padding(12, 7, 3, 3); _title.Text = "请拾取一个带属性定义的图块（双击属性行可定位实例）"; top.Controls.Add(_title); root.Controls.Add(top, 0, 0);
            var batch = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1, Margin = new Padding(0, 4, 0, 6) };
            var selectionLine = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            selectionLine.Controls.Add(ActionButton("全部勾选", (s, e) => SetAllSelected(true)));
            selectionLine.Controls.Add(ActionButton("全部取消", (s, e) => SetAllSelected(false)));
            selectionLine.Controls.Add(new Label { Text = "批量设置只写入勾选的属性行", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(12, 7, 3, 0) });
            batch.Controls.Add(selectionLine, 0, 0);
            var formatLine = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            formatLine.Controls.Add(new Label { Text = "字高", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
            _batchHeight.Width = 70; formatLine.Controls.Add(_batchHeight);
            formatLine.Controls.Add(ActionButton("应用", (s, e) => ApplyBatchNumber(true)));
            formatLine.Controls.Add(new Label { Text = "宽度因子", AutoSize = true, Margin = new Padding(14, 7, 3, 0) });
            _batchWidthFactor.Width = 70; formatLine.Controls.Add(_batchWidthFactor);
            formatLine.Controls.Add(ActionButton("应用", (s, e) => ApplyBatchNumber(false)));
            formatLine.Controls.Add(new Label { Text = "文字样式", AutoSize = true, Margin = new Padding(14, 7, 3, 0) });
            _batchTextStyle.DropDownStyle = ComboBoxStyle.DropDownList; _batchTextStyle.Width = 130; formatLine.Controls.Add(_batchTextStyle);
            formatLine.Controls.Add(ActionButton("应用", (s, e) => ApplyBatchChoice(true)));
            formatLine.Controls.Add(new Label { Text = "对齐方式", AutoSize = true, Margin = new Padding(14, 7, 3, 0) });
            _batchAlignment.DropDownStyle = ComboBoxStyle.DropDownList; _batchAlignment.Width = 90;
            _batchAlignment.Items.AddRange(new object[] { "左下", "中下", "右下", "左中", "居中", "右中", "左上", "中上", "右上" });
            _batchAlignment.SelectedIndex = 0; formatLine.Controls.Add(_batchAlignment);
            formatLine.Controls.Add(ActionButton("应用", (s, e) => ApplyBatchChoice(false)));
            batch.Controls.Add(formatLine, 0, 1);
            root.Controls.Add(batch, 0, 1);
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false; _grid.DataSource = _rows;
            _grid.CellDoubleClick += GridCellDoubleClick;
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "修改", DataPropertyName = "IsSelected", Width = 52 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "属性 TAG", DataPropertyName = "Tag", Width = 115 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "提示文字", DataPropertyName = "Prompt", Width = 145 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "默认内容", DataPropertyName = "DefaultValue", Width = 160 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文字高度", DataPropertyName = "Height", Width = 85 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "宽度因子", DataPropertyName = "WidthFactor", Width = 85 });
            _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "TextStyle", HeaderText = "文字样式", DataPropertyName = "TextStyle", Width = 130, FlatStyle = FlatStyle.Flat });
            var alignment = new DataGridViewComboBoxColumn { HeaderText = "对齐方式", DataPropertyName = "Alignment", Width = 95, FlatStyle = FlatStyle.Flat };
            alignment.Items.AddRange("左下", "中下", "右下", "左中", "居中", "右中", "左上", "中上", "右上"); _grid.Columns.Add(alignment);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "隐藏", DataPropertyName = "Invisible", Width = 55 });
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "常量", DataPropertyName = "Constant", Width = 55, ReadOnly = true });
            root.Controls.Add(_grid, 0, 2);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft };
            var apply = new Button { Text = "应用并同步实例", AutoSize = true }; apply.Click += (s, e) => Apply(); bottom.Controls.Add(apply);
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += (s, e) => Close(); bottom.Controls.Add(close); root.Controls.Add(bottom, 0, 3);
        }

        private static Button ActionButton(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(3) };
            button.Click += action;
            return button;
        }

        private void CommitGrid()
        {
            _grid.EndEdit();
            try { (BindingContext[_rows] as CurrencyManager)?.EndCurrentEdit(); } catch { }
        }

        private void SetAllSelected(bool selected)
        {
            CommitGrid();
            foreach (var row in _rows) row.IsSelected = selected;
            _grid.Refresh();
        }

        private void ApplyBatchNumber(bool height)
        {
            CommitGrid();
            var text = height ? _batchHeight.Text : _batchWidthFactor.Text;
            double value;
            if (!double.TryParse(text, out value) || value <= 0)
            {
                MessageBox.Show(this, height ? "请输入大于 0 的文字高度。" : "请输入大于 0 的宽度因子。", "批量修改");
                return;
            }
            if (!height && value > 100)
            {
                MessageBox.Show(this, "宽度因子应在 0.01～100 之间。", "批量修改");
                return;
            }
            var selectedRows = _rows.Where(x => x.IsSelected).ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show(this, "请先勾选要批量修改的属性。", "批量修改");
                return;
            }
            foreach (var row in selectedRows)
                if (height) row.Height = value; else row.WidthFactor = value;
            _grid.Refresh();
            _title.Text = "已把" + (height ? "文字高度" : "宽度因子") + "应用到 " + selectedRows.Count + " 个勾选项，点击“应用并同步实例”写入图块。";
        }

        private void ApplyBatchChoice(bool textStyle)
        {
            CommitGrid();
            var value = textStyle ? Convert.ToString(_batchTextStyle.SelectedItem) : Convert.ToString(_batchAlignment.SelectedItem);
            if (string.IsNullOrWhiteSpace(value))
            {
                MessageBox.Show(this, textStyle ? "请选择文字样式。" : "请选择对齐方式。", "批量修改");
                return;
            }
            var selectedRows = _rows.Where(x => x.IsSelected).ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show(this, "请先勾选要批量修改的属性。", "批量修改");
                return;
            }
            foreach (var row in selectedRows)
                if (textStyle) row.TextStyle = value; else row.Alignment = value;
            _grid.Refresh();
            _title.Text = "已把" + (textStyle ? "文字样式" : "对齐方式") + "应用到 " + selectedRows.Count + " 个勾选项，点击“应用并同步实例”写入图块。";
        }

        private void PickBlock()
        {
            _markers.Clear();
            Hide();
            try
            {
                var options = new PromptEntityOptions("\n请选择要修改属性定义的图块："); options.SetRejectMessage("\n请选择图块参照。"); options.AddAllowedClass(typeof(BlockReference), true);
                var result = _document.Editor.GetEntity(options);
                if (result.Status == PromptStatus.OK) LoadContext(() => AttributeDefinitionEditorService.Read(_document, result.ObjectId));
            }
            finally { Show(); Activate(); }
        }

        private void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _context == null) return;
            var row = _grid.Rows[e.RowIndex].DataBoundItem as AttributeDefinitionEditRow;
            if (row == null) return;
            try
            {
                var target = AttributeDefinitionEditorService.FindFirstInstance(_document, _context, row);
                if (target == null)
                {
                    MessageBox.Show(this, "当前 DWG 中没有找到该属性定义对应的图块实例。", "定位属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _document.Editor.SetImpliedSelection(new[] { target.AttributeId });
                _markers.ShowCurrent(_document, target);
                using (var view = _document.Editor.GetCurrentView())
                {
                    var width = Math.Max(target.MaxPoint.X - target.MinPoint.X, 1d);
                    var height = Math.Max(target.MaxPoint.Y - target.MinPoint.Y, 1d);
                    var viewRatio = view.Height <= 1e-9 ? 1d : view.Width / view.Height;
                    if (width / height > viewRatio) height = width / viewRatio; else width = height * viewRatio;
                    view.CenterPoint = new Point2d((target.MinPoint.X + target.MaxPoint.X) * 0.5d, (target.MinPoint.Y + target.MaxPoint.Y) * 0.5d);
                    view.Width = width * 1.12d;
                    view.Height = height * 1.12d;
                    _document.Editor.SetCurrentView(view);
                }
                _document.Window.Focus();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "无法定位该属性：" + exception.Message, "定位属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadContext(Func<AttributeDefinitionEditContext> loader)
        {
            try
            {
                _context = loader(); _rows.RaiseListChangedEvents = false; _rows.Clear(); foreach (var row in _context.Rows) _rows.Add(row); _rows.RaiseListChangedEvents = true; _rows.ResetBindings();
                _blockName.Text = _context.BlockName;
                var styleColumn = _grid.Columns["TextStyle"] as DataGridViewComboBoxColumn; styleColumn.Items.Clear();
                _batchTextStyle.Items.Clear();
                foreach (var style in _context.TextStyles) { styleColumn.Items.Add(style); _batchTextStyle.Items.Add(style); }
                if (_batchTextStyle.Items.Count > 0) _batchTextStyle.SelectedIndex = 0;
                _title.Text = "当前图块：" + _context.BlockName + " · 属性定义 " + _rows.Count + " 个 · 双击属性行可定位实例";
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "图块属性定义", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void Apply()
        {
            if (_context == null) return; CommitGrid();
            try
            {
                var requestedName = _blockName.Text.Trim();
                var count = AttributeDefinitionEditorService.Apply(_document, _context, _rows, requestedName);
                MessageBox.Show(this, "已更新图块“" + requestedName + "”的名称和属性定义，并同步 " + count + " 个属性实例。属性值保持不变。", "图块属性定义", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadContext(() => AttributeDefinitionEditorService.Read(_document, requestedName));
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "无法应用修改", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
