using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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
    internal sealed class TianzhengRoomStatisticsForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly DataGridView _grid = new DataGridView();
        private readonly BindingList<RoomStatisticsRow> _rows = new BindingList<RoomStatisticsRow>();
        private readonly ComboBox _scale = new ComboBox();
        private readonly ComboBox _textHeight = new ComboBox();
        private readonly TextBox _status = new TextBox();
        private readonly ComboBox _filterMode = new ComboBox();
        private readonly TextBox _keywords = new TextBox();
        private readonly ToolTip _tips = new ToolTip();
        private readonly TextBox _floorName = new TextBox { Text = "1层" };
        private readonly NumericUpDown _floorCount = new NumericUpDown { Minimum = 1, Maximum = 999, Value = 1 };
        private readonly HashSet<string> _activeSources = new HashSet<string>(StringComparer.Ordinal);
        private string _activeFloorName = "1层";
        private readonly HashSet<string> _excludedNames = new HashSet<string>(StringComparer.Ordinal);
        private List<RoomStatisticsItem> _allItems = new List<RoomStatisticsItem>();
        private List<RoomStatisticsItem> _items = new List<RoomStatisticsItem>();
        private string _sourceStatus = "尚未统计";
        private string _warnings = "";
        private bool _refreshing;
        private bool _summary;
        private bool _busy;

        public TianzhengRoomStatisticsForm(Document document, bool summary = false)
        {
            _document = document;
            _summary = summary;
            Text = "房间统计";
            StartPosition = FormStartPosition.CenterParent;
            Width = 900; Height = 620; MinimumSize = new Size(660, 500);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            _documentBinding = new ModelessDocumentBinding(this, document);
            Build();
            FormClosed += (s, e) =>
            {
                try { new RoomStatisticsFilterSettings { Mode = _filterMode.SelectedIndex, Keywords = _keywords.Text }.Save(); }
                catch { }
                _tips.Dispose();
            };
            Shown += (s, e) => BeginInvoke(new Action(() => SelectForStatistics(summary)));
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), WrapContents = false, AutoScroll = true, BackColor = Color.FromArgb(247, 249, 252) };
            var rooms = ButtonFor("框选房间"); rooms.Click += (s, e) => SelectForStatistics(false); top.Controls.Add(rooms);
            var tables = ButtonFor("汇总统计表"); tables.Click += (s, e) => SelectForStatistics(true); top.Controls.Add(tables);
            top.Controls.Add(LabelFor("插入比例")); ConfigurePreset(_scale, "1:100", "1:1", "1:20", "1:50", "1:100", "1:150", "1:200"); top.Controls.Add(_scale);
            top.Controls.Add(LabelFor("字高 mm")); ConfigurePreset(_textHeight, "2.5", "1.5", "2.5", "3.5", "5"); top.Controls.Add(_textHeight);
            root.Controls.Add(top, 0, 0);

            var filter = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(8, 3, 8, 3) };
            filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            filter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            _filterMode.DropDownStyle = ComboBoxStyle.DropDownList; _filterMode.Dock = DockStyle.Fill;
            _filterMode.Items.AddRange(new[] { "排除", "仅包含", "全部房间" });
            var settings = RoomStatisticsFilterSettings.Load();
            _filterMode.SelectedIndex = settings.Mode; _keywords.Text = settings.Keywords;
            _keywords.Dock = DockStyle.Fill;
            _tips.SetToolTip(_keywords, "按名称包含匹配，多个关键词用逗号、顿号或分号分隔。");
            filter.Controls.Add(_filterMode, 0, 0);
            filter.Controls.Add(new Label { Text = "名称关键词", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            filter.Controls.Add(_keywords, 2, 0);
            var defaults = ButtonFor("恢复默认"); defaults.Dock = DockStyle.Fill;
            defaults.Click += (s, e) => { _refreshing = true; _filterMode.SelectedIndex = 0; _keywords.Text = RoomStatistics.DefaultKeywords; _excludedNames.Clear(); _refreshing = false; RefreshStatistics(); };
            filter.Controls.Add(defaults, 3, 0); root.Controls.Add(filter, 0, 1);
            _filterMode.SelectedIndexChanged += (s, e) => { _keywords.Enabled = _filterMode.SelectedIndex != 2; RefreshStatistics(); };
            _keywords.TextChanged += (s, e) => RefreshStatistics();
            _keywords.Enabled = _filterMode.SelectedIndex != 2;

            var floors = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 3, 8, 3), WrapContents = false, AutoScroll = true };
            floors.Controls.Add(new Label { Text = "楼层/标准层", Size = new Size(75, 28), TextAlign = ContentAlignment.MiddleLeft }); _floorName.Width = 110; floors.Controls.Add(_floorName);
            floors.Controls.Add(new Label { Text = "层数", Size = new Size(40, 28), TextAlign = ContentAlignment.MiddleLeft }); _floorCount.Width = 55; floors.Controls.Add(_floorCount);
            var applyFloor = ButtonFor("应用本层"); applyFloor.Click += (s,e) => UpdateActiveFloor(); floors.Controls.Add(applyFloor);
            var append = ButtonFor("追加楼层框选"); append.Click += (s,e) => SelectForStatistics(false, true); floors.Controls.Add(append);
            var editFloors = ButtonFor("楼层设置"); editFloors.Click += (s,e) => EditFloors(); floors.Controls.Add(editFloors);
            _tips.SetToolTip(_floorName, "与门窗立面一致：3~10层自动计为8层；标准层可手填代表层数。");
            _floorName.TextChanged += (s,e) =>
            {
                if (_refreshing || _busy) return;
                int count;
                if (FloorStatisticsRules.TryParseFloorCount(_floorName.Text, out count)) { _refreshing = true; _floorCount.Value = count; _refreshing = false; }
            };
            root.Controls.Add(floors, 0, 2);

            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = Color.White; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.EnableHeadersVisualStyles = false;
            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.ColumnHeadersHeight = 36; _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 232, 242);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Included", HeaderText = "统计", DataPropertyName = "Included", Width = 50 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", DataPropertyName = "Sequence", Width = 50 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "房间名称", DataPropertyName = "RoomName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 28, MinimumWidth = 100 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "数量", DataPropertyName = "Count", Width = 65 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FloorQuantitySummary", HeaderText = "分层数量", DataPropertyName = "FloorQuantitySummary", Width = 210 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AreaDetails", HeaderText = "面积×数量（㎡）", DataPropertyName = "AreaDetails", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 52, MinimumWidth = 170 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TotalArea", HeaderText = "合计面积（㎡）", DataPropertyName = "TotalArea", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 20, MinimumWidth = 110 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CountShare", HeaderText = "数量占比", DataPropertyName = "CountShare", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 25, MinimumWidth = 110 });
            foreach (DataGridViewColumn column in _grid.Columns) column.ReadOnly = column.Name != "Included";
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) =>
            {
                if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 0 || e.RowIndex >= _rows.Count) return;
                var row = _rows[e.RowIndex];
                row.Included = Convert.ToBoolean(_grid.Rows[e.RowIndex].Cells[0].Value);
                if (row.Included) _excludedNames.Remove(row.RoomName); else _excludedNames.Add(row.RoomName);
                UpdateSelection();
            };
            _grid.DataSource = _rows; root.Controls.Add(_grid, 0, 3);
            _status.Multiline = true; _status.ReadOnly = true; _status.BorderStyle = BorderStyle.None;
            _status.Dock = DockStyle.Fill; _status.Margin = new Padding(10, 6, 10, 0); _status.ScrollBars = ScrollBars.Vertical;
            _status.Text = "尚未统计"; root.Controls.Add(_status, 0, 4);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(8) };
            var insert = ButtonFor("插入统计表"); insert.Click += (s, e) => InsertTable(); bottom.Controls.Add(insert);
            var close = ButtonFor("关闭"); close.Click += (s, e) => Close(); bottom.Controls.Add(close);
            var all = ButtonFor("全部勾选"); all.Click += (s, e) => { foreach (var row in _rows) _excludedNames.Remove(row.RoomName); RefreshStatistics(); }; bottom.Controls.Add(all);
            var none = ButtonFor("取消勾选"); none.Click += (s, e) => { foreach (var row in _rows) _excludedNames.Add(row.RoomName); RefreshStatistics(); }; bottom.Controls.Add(none);
            root.Controls.Add(bottom, 0, 5); Controls.Add(root);
            RefreshStatistics();
        }

#if ACAD_R19
        private void SelectForStatistics(bool summary, bool append = false)
#else
        private async void SelectForStatistics(bool summary, bool append = false)
#endif
        {
            if (_busy) return;
            _busy = true; Hide();
            try
            {
                _document.Window.Focus();
                Action select = () =>
                {
                    EnsureDocument();
                    var options = new PromptSelectionOptions { MessageForAdding = summary ? "\n框选需要汇总的房间统计表：" : "\n框选需要统计的天正房间：" };
                    var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, summary ? "ACAD_TABLE" : TianzhengRoomService.RoomDxfName) });
                    var result = _document.Editor.GetSelection(options, filter);
                    if (result.Status == PromptStatus.OK) LoadStatistics(result.Value.GetObjectIds(), summary, append);
                };
#if ACAD_R19
                select();
#else
                await CadCommandContext.ExecuteAsync(select);
#endif
            }
            catch (Exception exception)
            {
                // An invalid new selection must not leave the previous result ready for insertion.
                _allItems.Clear(); _items.Clear(); _rows.Clear(); _sourceStatus = "统计未完成";
                _warnings = exception.Message; _status.Text = "统计未完成：" + exception.Message;
            }
            finally { _busy = false; if (!IsDisposed) { Show(); Activate(); } }
        }

        private void LoadStatistics(IEnumerable<ObjectId> selectedIds, bool summary, bool append)
        {
            var items = new List<RoomStatisticsItem>();
            var warnings = new List<string>(); var sources = 0;
            using (_document.LockDocument())
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in selectedIds.Distinct())
                {
                    try
                    {
                        var value = transaction.GetObject(id, OpenMode.ForRead, false);
                        if (summary)
                        {
                            var table = value as Table;
                            List<RoomStatisticsItem> tableItems; string warning;
                            if (table == null) continue;
                            if (!RoomStatisticsTableService.TryRead(table, transaction, out tableItems, out warning, true))
                            { warnings.Add("跳过表 " + id.Handle + "：" + warning); continue; }
                            foreach (var item in tableItems.Where(x => string.IsNullOrWhiteSpace(x.FloorName)))
                                item.FloorSource = _document.Database.FingerprintGuid + ":table:" + id.Handle;
                            items.AddRange(tableItems); sources++;
                            if (warning != null) warnings.Add("表 " + id.Handle + "：" + warning);
                        }
                        else if (TianzhengRoomService.IsRoom(value))
                        {
                            var room = TianzhengRoomService.Read(value);
                            items.Add(RoomStatistics.Room(_document.Database.FingerprintGuid + ":room:" + id.Handle, room.Name, room.AreaValue, room.AreaText));
                            sources++;
                        }
                    }
                    catch (Exception ex) { warnings.Add("读取失败 " + id.Handle + "：" + ex.Message); }
                }
            }
            int duplicates;
            if (!summary)
            {
                items = items.Select(x => RoomStatistics.WithFloor(x, _floorName.Text, (int)_floorCount.Value)).ToList();
                var name = _floorName.Text.Trim();
                if (append && _allItems.Any(x => string.Equals(RoomStatistics.Floor(x), name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    if (MessageBox.Show("楼层“" + name + "”已存在，是否替换该层的统计来源？", "房间统计", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                }
                _activeSources.Clear(); foreach (var item in items) _activeSources.Add(item.Source);
                _activeFloorName = name;
                if (append) items.AddRange(_allItems.Where(x => !string.Equals(RoomStatistics.Floor(x), name, StringComparison.CurrentCultureIgnoreCase)));
            }
            else _activeSources.Clear();
            var combined = RoomStatistics.Combine(items, out duplicates, summary);
            _summary = summary; _allItems = combined; _excludedNames.Clear();
            _sourceStatus = summary ? "汇总 " + sources + " 张表" : "按层统计 " + combined.Select(RoomStatistics.Floor).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() + " 组楼层";
            if (duplicates > 0) _sourceStatus += "；排除重复来源 " + duplicates + " 个";
            _warnings = string.Join(Environment.NewLine, warnings);
            RefreshStatistics();
            if (warnings.Count > 0)
                MessageBox.Show("部分对象未读取或缺少来源信息，请核对窗口下方的提示后再插入。", "房间统计", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void EditFloors()
        {
            if (_allItems.Count == 0) { _status.Text = "请先框选房间或统计表。"; return; }
            using (var dialog = new RoomFloorSettingsForm(_allItems))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _allItems = dialog.Result; _activeSources.Clear(); RefreshStatistics();
            }
        }

        private void UpdateActiveFloor()
        {
            if (_activeSources.Count == 0) { _status.Text = "当前没有可编辑的单层来源。"; return; }
            try
            {
                if (_floorName.Text.Trim() != _activeFloorName && _allItems.Any(x => RoomStatistics.Floor(x) != _activeFloorName && string.Equals(RoomStatistics.Floor(x), _floorName.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
                    throw new InvalidOperationException("该楼层名称已有其他来源，请核对楼层设置，或重新框选并确认替换。");
                var replacement = _allItems.Select(x => _activeSources.Contains(x.Source) && RoomStatistics.Floor(x) == _activeFloorName
                    ? RoomStatistics.WithFloor(x, _floorName.Text, (int)_floorCount.Value) : x).ToList();
                int duplicates; _allItems = RoomStatistics.Combine(replacement, out duplicates, _summary);
                _activeFloorName = _floorName.Text.Trim();
                _refreshing = true; _floorCount.Value = _allItems.Where(x => RoomStatistics.Floor(x) == _activeFloorName).Select(RoomStatistics.Layers).First(); _refreshing = false;
                RefreshStatistics();
            }
            catch (Exception ex) { _status.Text = "楼层设置未应用：" + ex.Message; }
        }

        private void RefreshStatistics()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                _grid.Columns["AreaDetails"].Visible = !_summary;
                _grid.Columns["TotalArea"].Visible = !_summary;
                _grid.Columns["CountShare"].Visible = _summary;
                _grid.Columns["FloorQuantitySummary"].Visible = _allItems.Any(x => !string.IsNullOrWhiteSpace(x.FloorName));
                var filtered = RoomStatistics.Filter(_allItems, _filterMode.SelectedIndex, _keywords.Text);
                var rows = RoomStatistics.Rows(filtered, _summary);
                _rows.Clear();
                foreach (var row in rows) { row.Included = !_excludedNames.Contains(row.RoomName); _rows.Add(row); }
                UpdateSelection();
            }
            finally { _refreshing = false; }
        }

        private void UpdateSelection()
        {
            var included = new HashSet<string>(_rows.Where(x => x.Included).Select(x => x.RoomName), StringComparer.Ordinal);
            _items = RoomStatistics.Filter(_allItems, _filterMode.SelectedIndex, _keywords.Text).Where(x => included.Contains(string.IsNullOrWhiteSpace(x.Name) ? "未命名" : x.Name.Trim())).ToList();
            var count = _items.Sum(RoomStatistics.Quantity);
            foreach (var row in _rows) row.CountShare = row.Included ? RoomStatistics.Share(row.Count, count) : "—";
            _grid.Refresh();
            _status.Text = _sourceStatus + "；统计 " + count + " 个房间、" + included.Count + " 种名称；排除 " + (_allItems.Sum(RoomStatistics.Quantity) - count) + " 个" +
                (_summary ? "" : "；合计 " + RoomStatistics.Total(_items) + " ㎡") +
                (_warnings.Length == 0 ? "" : Environment.NewLine + _warnings);
        }

#if ACAD_R19
        private void InsertTable()
#else
        private async void InsertTable()
#endif
        {
            if (_busy) return;
            if (_items.Count == 0) { _status.Text = "请先框选房间或统计表。"; return; }
            double scale, textHeight;
            if (!TryScale(_scale.Text, out scale) || !double.TryParse(_textHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out textHeight) || !FinitePositive(textHeight))
            { MessageBox.Show(this, "请输入有效的插入比例和文字高度。", "房间统计", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            _busy = true; Hide();
            try
            {
                _document.Window.Focus();
                Action insert = () =>
                {
                    EnsureDocument();
                    var point = _document.Editor.GetPoint("\n指定房间统计表左上角插入点：");
                    if (point.Status != PromptStatus.OK) return;
                    using (_document.LockDocument())
                    using (var transaction = _document.Database.TransactionManager.StartTransaction())
                    {
                        RoomStatisticsTableService.Insert(_document.Database, transaction,
                            point.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem), _items, _summary, scale, textHeight);
                        transaction.Commit();
                    }
                    _document.Editor.Regen();
                };
#if ACAD_R19
                insert();
#else
                await CadCommandContext.ExecuteAsync(insert);
#endif
            }
            catch (Exception exception) { _status.Text = "插入失败：" + exception.Message; }
            finally { _busy = false; if (!IsDisposed) { Show(); Activate(); } }
        }

        private void EnsureDocument()
        {
            if (!ReferenceEquals(Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument, _document))
                throw new InvalidOperationException("请切回统计窗口对应的图纸。");
        }

        private static bool TryScale(string text, out double scale)
        {
            scale = 0; var value = (text ?? string.Empty).Trim().Replace('：', ':'); var parts = value.Split(':');
            double left, right;
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out right) && FinitePositive(left) && FinitePositive(right))
            { scale = right / left; return FinitePositive(scale); }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) && FinitePositive(scale);
        }
        private static bool FinitePositive(double value) { return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value); }
        private static void ConfigurePreset(ComboBox box, string selected, params string[] values) { box.Width = 80; box.DropDownStyle = ComboBoxStyle.DropDown; box.Items.AddRange(values); box.SelectedIndex = box.Items.IndexOf(selected); }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(12, 7, 5, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 28 }; }
    }
}
