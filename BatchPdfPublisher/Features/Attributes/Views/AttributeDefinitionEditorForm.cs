using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public sealed partial class AttributeDefinitionEditorForm : CadAttributeWindow
    {
        private readonly Document _document;
        private readonly PublisherForm.BufferedDataGridView _grid = new PublisherForm.BufferedDataGridView();
        private readonly Label _title = new Label();
        private readonly TextBox _blockName = new TextBox();
        private readonly TextBox _batchHeight = new TextBox();
        private readonly TextBox _batchWidthFactor = new TextBox();
        private readonly PublisherForm.ThemedComboBox _batchTextStyle = new PublisherForm.ThemedComboBox();
        private readonly PublisherForm.ThemedComboBox _batchAlignment = new PublisherForm.ThemedComboBox();
        private readonly BindingList<AttributeDefinitionEditRow> _rows = new BindingList<AttributeDefinitionEditRow>();
        private readonly AttributeMarkerService _markers = new AttributeMarkerService();
        private AttributeDefinitionEditContext _context;

        public AttributeDefinitionEditorForm(Document document, string blockName = null)
            : base("图块属性定义编辑器", new Size(1360, 760), new Size(1080, 620))
        {
            _document = document;
            Build();
            FormClosed += (s, e) => _markers.Dispose();
            if (!string.IsNullOrWhiteSpace(blockName)) LoadContext(() => AttributeDefinitionEditorService.Read(_document, blockName));
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
            UpdateSelectionCount();
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
            _status.Text = "已把" + (height ? "文字高度" : "宽度因子") + "应用到 " + selectedRows.Count + " 个勾选项，点击“应用并同步实例”写入图块。";
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
            _status.Text = "已把" + (textStyle ? "文字样式" : "对齐方式") + "应用到 " + selectedRows.Count + " 个勾选项，点击“应用并同步实例”写入图块。";
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
            if (e.ColumnIndex >= 0 && (_grid.Columns[e.ColumnIndex].Name == "TextStyle"
                || _grid.Columns[e.ColumnIndex].Name == "Alignment")) return;
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
                _batchTextStyle.Items.Clear();
                foreach (var style in _context.TextStyles) _batchTextStyle.Items.Add(style);
                if (_batchTextStyle.Items.Count > 0) _batchTextStyle.SelectedIndex = 0;
                _title.Text = _rows.Count + " 个属性定义";
                _status.Text = "双击属性行可定位 CAD 实例 · 修改尚未写入";
                UpdateSelectionCount();
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
