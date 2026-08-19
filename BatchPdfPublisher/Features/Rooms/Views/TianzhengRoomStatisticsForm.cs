using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class TianzhengRoomStatisticsRow
    {
        public int Sequence { get; set; }
        public string RoomName { get; set; }
        public int Count { get; set; }
        public string AreaDetails { get; set; }
        public string TotalArea { get; set; }
    }

    internal sealed class TianzhengRoomStatisticsForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly DataGridView _grid = new DataGridView();
        private readonly BindingList<TianzhengRoomStatisticsRow> _rows = new BindingList<TianzhengRoomStatisticsRow>();
        private readonly ComboBox _scale = new ComboBox();
        private readonly ComboBox _textHeight = new ComboBox();
        private readonly Label _status = new Label();

        public TianzhengRoomStatisticsForm(Document document)
        {
            _document = document;
            Text = "房间面积统计";
            StartPosition = FormStartPosition.CenterParent;
            Width = 820; Height = 530; MinimumSize = new Size(650, 420);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            Build();
            Shown += (s, e) => BeginInvoke(new Action(SelectForStatistics));
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), WrapContents = false, BackColor = Color.FromArgb(247, 249, 252) };
            var refresh = ButtonFor("重新框选统计"); refresh.Click += (s, e) => SelectForStatistics(); top.Controls.Add(refresh);
            top.Controls.Add(LabelFor("插入比例")); ConfigurePreset(_scale, "1:100", "1:1", "1:20", "1:50", "1:100", "1:150", "1:200"); top.Controls.Add(_scale);
            top.Controls.Add(LabelFor("字高")); ConfigurePreset(_textHeight, "2.5", "1.5", "2.5", "3.5", "5"); top.Controls.Add(_textHeight);
            top.Controls.Add(new Label { Text = "同一名称的不同面积会合并显示为“面积×数量”。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(18, 7, 0, 0) });
            root.Controls.Add(top, 0, 0);

            _grid.Dock = DockStyle.Fill; _grid.ReadOnly = true; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = Color.White; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersHeight = 32; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 232, 242); _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", DataPropertyName = "Sequence", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "房间名称", DataPropertyName = "RoomName", Width = 180 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数量", DataPropertyName = "Count", Width = 75 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "面积明细", DataPropertyName = "AreaDetails", Width = 310 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "合计面积", DataPropertyName = "TotalArea", Width = 120 });
            _grid.DataSource = _rows; root.Controls.Add(_grid, 0, 1);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(8) };
            var insert = ButtonFor("插入统计表"); insert.Click += (s, e) => InsertTable(); bottom.Controls.Add(insert);
            var close = ButtonFor("关闭"); close.Click += (s, e) => Close(); bottom.Controls.Add(close);
            _status.AutoSize = true; _status.Margin = new Padding(8, 7, 18, 0); bottom.Controls.Add(_status);
            root.Controls.Add(bottom, 0, 2); Controls.Add(root);
        }

#if ACAD_R19
        private void SelectForStatistics()
#else
        private async void SelectForStatistics()
#endif
        {
            Hide();
            try
            {
                _document.Window.Focus(); ObjectId[] ids = null;
#if ACAD_R19
                ids = PromptForRooms();
#else
                await CadCommandContext.ExecuteAsync(() => ids = PromptForRooms());
#endif
                if (ids != null) LoadStatistics(ids);
            }
            catch (Exception exception) { MessageBox.Show(this, "框选房间统计失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

        private ObjectId[] PromptForRooms()
        {
            var options = new PromptSelectionOptions { MessageForAdding = "\n框选需要统计的天正房间：" };
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, TianzhengRoomService.RoomDxfName) });
            var result = _document.Editor.GetSelection(options, filter);
            return result.Status == PromptStatus.OK ? result.Value.GetObjectIds() : null;
        }

        private void LoadStatistics(IEnumerable<ObjectId> selectedIds)
        {
            var rooms = new List<TianzhengRoomInfo>(); var unreadable = 0;
            using (_document.LockDocument())
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in selectedIds.Distinct())
                {
                    try
                    {
                        var value = transaction.GetObject(id, OpenMode.ForRead, false);
                        if (TianzhengRoomService.IsRoom(value)) rooms.Add(TianzhengRoomService.Read(value));
                    }
                    catch { unreadable++; }
                }
            }
            var result = rooms.GroupBy(x => string.IsNullOrWhiteSpace(x.Name) ? "未命名" : x.Name.Trim(), StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.CurrentCulture)
                .Select((group, index) =>
                {
                    var areaGroups = group.GroupBy(AreaKey).OrderBy(x => AreaSortValue(x.First())).ToList();
                    var details = string.Join("；", areaGroups.Select(x => AreaDisplay(x.First()) + "×" + x.Count()));
                    var numeric = group.Where(x => x.AreaValue.HasValue).ToList();
                    var total = numeric.Count == group.Count() ? numeric.Sum(x => x.AreaValue.Value).ToString("0.##", CultureInfo.InvariantCulture) : "—";
                    return new TianzhengRoomStatisticsRow { Sequence = index + 1, RoomName = group.Key, Count = group.Count(), AreaDetails = details, TotalArea = total };
                }).ToList();
            _rows.Clear(); foreach (var row in result) _rows.Add(row);
            _status.Text = "共 " + rooms.Count + " 个房间，" + result.Count + " 种名称" + (unreadable > 0 ? "，读取失败 " + unreadable + " 个" : string.Empty);
        }

#if ACAD_R19
        private void InsertTable()
#else
        private async void InsertTable()
#endif
        {
            if (_rows.Count == 0) { MessageBox.Show(this, "当前空间没有可插入的房间统计数据。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            double scale; double textHeight;
            if (!TryScale(_scale.Text, out scale) || !double.TryParse(_textHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out textHeight) || textHeight <= 0)
            { MessageBox.Show(this, "请输入有效的插入比例和文字高度。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Hide();
            try
            {
                _document.Window.Focus(); Point3d? point = null;
#if ACAD_R19
                point = PromptInsertionPoint();
#else
                await CadCommandContext.ExecuteAsync(() => point = PromptInsertionPoint());
#endif
                if (!point.HasValue) return;
                using (_document.LockDocument()) InsertCadTable(point.Value, scale, textHeight);
                _document.Editor.Regen();
            }
            catch (Exception exception) { MessageBox.Show(this, "插入房间统计表失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

        private Point3d? PromptInsertionPoint()
        {
            var result = _document.Editor.GetPoint("\n指定房间面积统计表左上角插入点：");
            return result.Status == PromptStatus.OK ? result.Value : (Point3d?)null;
        }

        private void InsertCadTable(Point3d point, double scale, double textHeight)
        {
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                var table = new Table { TableStyle = _document.Database.Tablestyle, Position = point };
                table.SetSize(_rows.Count + 2, 5);
                table.SetRowHeight(7d * scale);
                var widths = new[] { 12d, 35d, 15d, 70d, 25d };
                for (var column = 0; column < widths.Length; column++) table.Columns[column].Width = widths[column] * scale;
                table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
                table.Cells[0, 0].TextString = "房间面积统计表";
                var headers = new[] { "序号", "房间名称", "数量", "面积明细", "合计面积" };
                for (var column = 0; column < 5; column++) table.Cells[1, column].TextString = headers[column];
                for (var index = 0; index < _rows.Count; index++)
                {
                    var row = _rows[index]; var target = index + 2;
                    table.Cells[target, 0].TextString = row.Sequence.ToString(CultureInfo.InvariantCulture);
                    table.Cells[target, 1].TextString = row.RoomName;
                    table.Cells[target, 2].TextString = row.Count.ToString(CultureInfo.InvariantCulture);
                    table.Cells[target, 3].TextString = row.AreaDetails;
                    table.Cells[target, 4].TextString = row.TotalArea;
                }
                for (var row = 0; row < _rows.Count + 2; row++)
                    for (var column = 0; column < 5; column++)
                    { table.Cells[row, column].TextHeight = textHeight * scale; table.Cells[row, column].Alignment = CellAlignment.MiddleCenter; }
                var space = (BlockTableRecord)transaction.GetObject(_document.Database.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(table); transaction.AddNewlyCreatedDBObject(table, true); table.GenerateLayout(); transaction.Commit();
            }
        }

        private static string AreaKey(TianzhengRoomInfo room) => room.AreaValue.HasValue ? room.AreaValue.Value.ToString("0.###", CultureInfo.InvariantCulture) : (room.AreaText ?? string.Empty).Trim();
        private static string AreaDisplay(TianzhengRoomInfo room) => room.AreaValue.HasValue ? room.AreaValue.Value.ToString("0.##", CultureInfo.InvariantCulture) : (string.IsNullOrWhiteSpace(room.AreaText) ? "未知" : room.AreaText.Trim());
        private static double AreaSortValue(TianzhengRoomInfo room) => room.AreaValue ?? double.MaxValue;
        private static bool TryScale(string text, out double scale)
        {
            scale = 0; var value = (text ?? string.Empty).Trim().Replace('：', ':'); var parts = value.Split(':');
            double left, right;
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out right) && left > 0 && right > 0) { scale = right / left; return true; }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && scale > 0;
        }
        private static void ConfigurePreset(ComboBox box, string selected, params string[] values) { box.Width = 80; box.DropDownStyle = ComboBoxStyle.DropDown; box.Items.AddRange(values); box.Text = selected; }
        private static Label LabelFor(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(12, 7, 5, 0) };
        private static Button ButtonFor(string text) => new Button { Text = text, AutoSize = true, Height = 28 };
    }
}
