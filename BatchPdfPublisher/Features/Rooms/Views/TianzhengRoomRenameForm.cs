using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using AcColorDialog = Autodesk.AutoCAD.Windows.ColorDialog;

namespace BatchPdfPublisher.Views
{
    internal sealed class TianzhengRoomPreviewRow
    {
        public bool Selected { get; set; }
        public int Sequence { get; set; }
        public string OldName { get; set; }
        public string Area { get; set; }
        public string NewName { get; set; }
        public string ColorText { get; set; }
        public ObjectId Id { get; set; }
        public AcColor NewColor { get; set; }
        public bool HasExtents { get; set; }
        public Point3d MinPoint { get; set; }
        public Point3d MaxPoint { get; set; }
    }

    internal sealed class TianzhengRoomRenameForm : DpiAwareForm
    {
        private readonly Document _document;
        private TianzhengRoomInfo _sample;
        private readonly TextBox _sampleName = new TextBox();
        private readonly TextBox _sampleArea = new TextBox();
        private readonly TextBox _newName = new TextBox();
        private readonly CheckBox _changeColor = new CheckBox();
        private readonly Button _colorButton = new Button();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _status = new Label();
        private readonly BindingList<TianzhengRoomPreviewRow> _rows = new BindingList<TianzhengRoomPreviewRow>();
        private AcColor _selectedColor = AcColor.FromColorIndex(ColorMethod.ByAci, 7);
        private bool _refreshing;
        private ObjectId _highlightedId = ObjectId.Null;
        private readonly RoomRenameSettings _settings;

        public TianzhengRoomRenameForm(Document document, TianzhengRoomInfo sample)
        {
            _document = document;
            _sample = sample;
            _settings = RoomRenameSettings.Load();
            _selectedColor = _settings.ToColor();
            Text = "批量修改天正房间名称";
            StartPosition = FormStartPosition.CenterParent;
            Width = 900;
            Height = 590;
            MinimumSize = new Size(720, 460);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            Build();
            ScanCurrentSpace();
            FormClosed += (s, e) => { SaveSettings(); ClearHighlight(); };
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 5), ColumnCount = 1, RowCount = 2, BackColor = System.Drawing.Color.FromArgb(247, 249, 252) };
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var sampleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            sampleRow.Controls.Add(LabelFor("匹配条件"));
            ConfigureReadOnlyBox(_sampleName, "房间名：" + _sample.Name, 170); sampleRow.Controls.Add(_sampleName);
            ConfigureReadOnlyBox(_sampleArea, "面积：" + _sample.AreaText, 130); sampleRow.Controls.Add(_sampleArea);
            var pickSample = ButtonFor("重新拾取"); pickSample.Click += (s, e) => PickSample(); sampleRow.Controls.Add(pickSample);
            var scanAll = ButtonFor("扫描当前空间"); scanAll.Click += (s, e) => ScanCurrentSpace(); sampleRow.Controls.Add(scanAll);
            var select = ButtonFor("重新框选"); select.Click += (s, e) => SelectRooms(); sampleRow.Controls.Add(select);
            var statistics = ButtonFor("房间统计"); statistics.Click += (s, e) => Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(new TianzhengRoomStatisticsForm(_document)); sampleRow.Controls.Add(statistics);
            top.Controls.Add(sampleRow, 0, 0);

            var editRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            editRow.Controls.Add(LabelFor("统一新名称"));
            _newName.Width = 180; _newName.Height = 28; _newName.Text = string.IsNullOrWhiteSpace(_settings.LastName) ? _sample.Name : _settings.LastName;
            _newName.TextChanged += (s, e) => SetAllNames(_newName.Text);
            editRow.Controls.Add(_newName);
            _changeColor.Text = "修改文字颜色"; _changeColor.AutoSize = true; _changeColor.Checked = _settings.ChangeColor; _changeColor.Margin = new Padding(18, 6, 3, 3); editRow.Controls.Add(_changeColor);
            _colorButton.Text = string.Empty; _colorButton.Size = new Size(58, 28); _colorButton.BackColor = DisplayColor(_selectedColor); _colorButton.Click += (s, e) => ChooseColor(); editRow.Controls.Add(_colorButton);
            editRow.Controls.Add(new Label { Text = "列表中的“修改后名称”可单独编辑；双击任意行可定位预览。", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(18, 7, 0, 0) });
            top.Controls.Add(editRow, 0, 1);
            root.Controls.Add(top, 0, 0);

            ConfigureGrid();
            root.Controls.Add(_grid, 0, 1);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(8) };
            var apply = ButtonFor("写入房间"); apply.Click += (s, e) => ApplyChanges(); bottom.Controls.Add(apply);
            var close = ButtonFor("关闭"); close.Click += (s, e) => Close(); bottom.Controls.Add(close);
            var clear = ButtonFor("取消全选"); clear.Click += (s, e) => SetSelection(false); bottom.Controls.Add(clear);
            var all = ButtonFor("全选"); all.Click += (s, e) => SetSelection(true); bottom.Controls.Add(all);
            var locate = ButtonFor("定位当前"); locate.Click += (s, e) => LocateCurrent(); bottom.Controls.Add(locate);
            _status.AutoSize = true; _status.Margin = new Padding(8, 7, 18, 0); bottom.Controls.Add(_status);
            root.Controls.Add(bottom, 0, 2);
            Controls.Add(root);
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.RowHeadersVisible = false; _grid.BackgroundColor = System.Drawing.Color.White;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.ColumnHeadersHeight = 32; _grid.EnableHeadersVisualStyles = false; _grid.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(225, 232, 242);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(247, 249, 252);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "修改", DataPropertyName = "Selected", Width = 55 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", DataPropertyName = "Sequence", Width = 58, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原房间名称", DataPropertyName = "OldName", Width = 180, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "使用面积", DataPropertyName = "Area", Width = 130, ReadOnly = true });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "修改后名称（可编辑）", DataPropertyName = "NewName", Width = 220 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RoomColor", HeaderText = "文字颜色", DataPropertyName = "ColorText", Width = 110, ReadOnly = true });
            _grid.DataSource = _rows;
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Locate(_rows[e.RowIndex]); };
            _grid.CellClick += (s, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "RoomColor") ChooseColorForRows(_rows[e.RowIndex]); };
            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "RoomColor") return;
                var row = _rows[e.RowIndex]; var color = row.NewColor ?? _selectedColor;
                e.CellStyle.BackColor = DisplayColor(color); e.CellStyle.ForeColor = ContrastColor(e.CellStyle.BackColor);
            };
        }

        private void ScanCurrentSpace()
        {
            var ids = new List<ObjectId>();
            using (_document.LockDocument())
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(_document.Database.CurrentSpaceId, OpenMode.ForRead);
                ids.AddRange(space.Cast<ObjectId>());
            }
            LoadMatches(ids);
        }

#if ACAD_R19
        private void PickSample()
#else
        private async void PickSample()
#endif
        {
            Hide();
            try
            {
                _document.Window.Focus();
#if ACAD_R19
                var id = PromptForSample();
#else
                var id = ObjectId.Null;
                await CadCommandContext.ExecuteAsync(() => id = PromptForSample());
#endif
                if (id.IsNull) return;
                TianzhengRoomInfo sample = null;
                using (_document.LockDocument())
                using (var transaction = _document.Database.TransactionManager.StartTransaction())
                {
                    var entity = transaction.GetObject(id, OpenMode.ForRead, false);
                    if (TianzhengRoomService.IsRoom(entity)) sample = TianzhengRoomService.Read(entity);
                }
                if (sample == null) { MessageBox.Show(this, "所选对象不是原生天正房间，请重新拾取。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                _sample = sample;
                _sampleName.Text = "房间名：" + _sample.Name;
                _sampleArea.Text = "面积：" + _sample.AreaText;
                ScanCurrentSpace();
            }
            catch (Exception exception) { MessageBox.Show(this, "重新拾取匹配房间失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { Show(); Activate(); }
        }

        private ObjectId PromptForSample()
        {
            var options = new PromptEntityOptions("\n请选择新的天正房间作为匹配样板：");
            var result = _document.Editor.GetEntity(options);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

#if ACAD_R19
        private void SelectRooms()
#else
        private async void SelectRooms()
#endif
        {
            Hide();
            try
            {
                _document.Window.Focus();
#if ACAD_R19
                var ids = PromptForRooms();
                if (ids != null) LoadMatches(ids);
#else
                ObjectId[] ids = null;
                await CadCommandContext.ExecuteAsync(() => ids = PromptForRooms());
                if (ids != null) LoadMatches(ids);
#endif
            }
            finally { Show(); Activate(); }
        }

        private ObjectId[] PromptForRooms()
        {
            var options = new PromptSelectionOptions { MessageForAdding = "\n框选要检查的天正房间：" };
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, TianzhengRoomService.RoomDxfName) });
            var result = _document.Editor.GetSelection(options, filter);
            return result.Status == PromptStatus.OK ? result.Value.GetObjectIds() : null;
        }

        private void LoadMatches(IEnumerable<ObjectId> ids)
        {
            _refreshing = true; _rows.Clear(); var unreadable = 0; var sequence = 1;
            using (_document.LockDocument())
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in ids.Distinct())
                {
                    try
                    {
                        var entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || !TianzhengRoomService.IsRoom(entity)) continue;
                        var info = TianzhengRoomService.Read(entity);
                        if (!TianzhengRoomService.Matches(info, _sample)) continue;
                        var row = new TianzhengRoomPreviewRow { Selected = true, Sequence = sequence++, OldName = info.Name, Area = info.AreaText, NewName = _newName.Text.Trim(), Id = id, NewColor = entity.Color, ColorText = ColorName(entity.Color) };
                        try { var extents = entity.GeometricExtents; row.MinPoint = extents.MinPoint; row.MaxPoint = extents.MaxPoint; row.HasExtents = true; } catch { }
                        _rows.Add(row);
                    }
                    catch { unreadable++; }
                }
            }
            _refreshing = false;
            _status.Text = "匹配 " + _rows.Count + " 个房间" + (unreadable > 0 ? "，读取失败 " + unreadable + " 个" : string.Empty);
            _grid.Refresh();
        }

        private void SetAllNames(string value)
        {
            if (_refreshing) return;
            foreach (var row in _rows) row.NewName = (value ?? string.Empty).Trim();
            _grid.Refresh();
        }

        private void ChooseColor()
        {
            var dialog = new AcColorDialog { Color = _selectedColor };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            _selectedColor = dialog.Color; _changeColor.Checked = true; _colorButton.BackColor = DisplayColor(_selectedColor);
            foreach (var row in _rows.Where(x => x.Selected)) { row.NewColor = _selectedColor; row.ColorText = ColorName(_selectedColor); }
            _grid.Refresh();
            SaveSettings();
        }

        private void ChooseColorForRows(TianzhengRoomPreviewRow clickedRow)
        {
            var initial = clickedRow.NewColor ?? _selectedColor;
            var dialog = new AcColorDialog { Color = initial };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            _selectedColor = dialog.Color; _changeColor.Checked = true; _colorButton.BackColor = DisplayColor(_selectedColor);
            var targets = _grid.SelectedRows.Count > 1
                ? _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.DataBoundItem as TianzhengRoomPreviewRow).Where(x => x != null).ToList()
                : new List<TianzhengRoomPreviewRow> { clickedRow };
            foreach (var row in targets) { row.NewColor = _selectedColor; row.ColorText = ColorName(_selectedColor); }
            _grid.Refresh();
            SaveSettings();
        }

        private void ApplyChanges()
        {
            _grid.EndEdit(); var selected = _rows.Where(x => x.Selected).ToList();
            if (selected.Count == 0) { MessageBox.Show(this, "请至少勾选一个房间。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (selected.Any(x => string.IsNullOrWhiteSpace(x.NewName))) { MessageBox.Show(this, "修改后名称不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, "即将修改 " + selected.Count + " 个房间，是否继续？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var changed = 0; var skipped = 0; var failed = 0; string firstFailure = null;
            using (_document.LockDocument())
            using (var transaction = _document.Database.TransactionManager.StartTransaction())
            {
                foreach (var row in selected)
                {
                    try
                    {
                        var entity = transaction.GetObject(row.Id, OpenMode.ForWrite, false) as Entity;
                        var current = TianzhengRoomService.Read(entity);
                        if (!TianzhengRoomService.Matches(current, _sample)) { skipped++; continue; }
                        TianzhengRoomService.Rename(entity, row.NewName.Trim());
                        if (_changeColor.Checked && row.NewColor != null) entity.Color = row.NewColor;
                        changed++;
                    }
                    catch (Exception exception) { failed++; if (firstFailure == null) firstFailure = exception.Message; }
                }
                transaction.Commit();
            }
            _document.Editor.Regen();
            SaveSettings();
            _status.Text = "完成：成功 " + changed + "，条件变化跳过 " + skipped + "，失败 " + failed;
            MessageBox.Show(this, _status.Text + (firstFailure == null ? string.Empty : "\r\n\r\n首个失败原因：" + firstFailure), Text, MessageBoxButtons.OK, failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void LocateCurrent() { if (_grid.CurrentRow != null) Locate(_rows[_grid.CurrentRow.Index]); }
        private void Locate(TianzhengRoomPreviewRow row)
        {
            try
            {
                ClearHighlight();
                using (_document.LockDocument())
                {
                _document.Editor.SetImpliedSelection(new[] { row.Id });
                using (var transaction = _document.Database.TransactionManager.StartTransaction())
                {
                    var entity = transaction.GetObject(row.Id, OpenMode.ForRead, false) as Entity;
                    if (entity != null) { entity.Highlight(); _highlightedId = row.Id; }
                }
                if (row.HasExtents)
                {
                    using (var view = _document.Editor.GetCurrentView())
                    {
                        var worldToDisplay = Matrix3d.PlaneToWorld(view.ViewDirection);
                        worldToDisplay = Matrix3d.Displacement(view.Target - Point3d.Origin) * worldToDisplay;
                        worldToDisplay = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * worldToDisplay;
                        worldToDisplay = worldToDisplay.Inverse();
                        var corners = new[]
                        {
                            row.MinPoint,
                            new Point3d(row.MaxPoint.X, row.MinPoint.Y, row.MinPoint.Z),
                            new Point3d(row.MinPoint.X, row.MaxPoint.Y, row.MaxPoint.Z),
                            row.MaxPoint
                        }.Select(x => x.TransformBy(worldToDisplay)).ToArray();
                        var minX = corners.Min(x => x.X); var maxX = corners.Max(x => x.X);
                        var minY = corners.Min(x => x.Y); var maxY = corners.Max(x => x.Y);
                        var width = Math.Max(maxX - minX, 1d); var height = Math.Max(maxY - minY, 1d);
                        var ratio = view.Height <= 1e-9 ? 1d : view.Width / view.Height;
                        if (width / height > ratio) height = width / ratio; else width = height * ratio;
                        view.CenterPoint = new Point2d((minX + maxX) / 2d, (minY + maxY) / 2d);
                        view.Width = width * 1.25d; view.Height = height * 1.25d; _document.Editor.SetCurrentView(view);
                    }
                }
                _document.Window.Focus();
                }
            }
            catch (Exception exception) { MessageBox.Show(this, "无法定位该房间：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void ClearHighlight()
        {
            if (_highlightedId.IsNull || !_highlightedId.IsValid) return;
            try
            {
                using (_document.LockDocument())
                {
                using (var transaction = _document.Database.TransactionManager.StartTransaction())
                {
                    var entity = transaction.GetObject(_highlightedId, OpenMode.ForRead, false) as Entity;
                    if (entity != null) entity.Unhighlight();
                }
                }
            }
            catch { }
            _highlightedId = ObjectId.Null;
        }

        private void SaveSettings()
        {
            try
            {
                _settings.LastName = (_newName.Text ?? string.Empty).Trim();
                _settings.ChangeColor = _changeColor.Checked;
                _settings.SetColor(_selectedColor);
                _settings.Save();
            }
            catch { }
        }

        private void SetSelection(bool selected) { foreach (var row in _rows) row.Selected = selected; _grid.Refresh(); }
        private static Label LabelFor(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 7, 0) };
        private static Button ButtonFor(string text) => new Button { Text = text, AutoSize = true, Height = 28 };
        private static void ConfigureReadOnlyBox(TextBox box, string text, int width) { box.Text = text; box.ReadOnly = true; box.Width = width; box.BackColor = SystemColors.Control; box.BorderStyle = BorderStyle.FixedSingle; }
        private static string ColorName(AcColor color) => color == null ? "" : color.ColorMethod == ColorMethod.ByAci ? "ACI " + color.ColorIndex : "RGB " + color.Red + "," + color.Green + "," + color.Blue;
        private static System.Drawing.Color DisplayColor(AcColor color)
        {
            if (color == null) return System.Drawing.Color.White;
            if (color.ColorMethod == ColorMethod.ByColor) return System.Drawing.Color.FromArgb(color.Red, color.Green, color.Blue);
            var rgb = EntityColor.LookUpRgb((byte)Math.Max(0, Math.Min(255, (int)color.ColorIndex)));
            return System.Drawing.Color.FromArgb((int)((rgb >> 16) & 255), (int)((rgb >> 8) & 255), (int)(rgb & 255));
        }
        private static System.Drawing.Color ContrastColor(System.Drawing.Color value) => value.GetBrightness() < 0.45f ? System.Drawing.Color.White : System.Drawing.Color.Black;

        private sealed class RoomRenameSettings
        {
            public string LastName = string.Empty;
            public bool ChangeColor;
            public int ColorMethod = (int)Autodesk.AutoCAD.Colors.ColorMethod.ByAci;
            public int ColorIndex = 7;
            public byte Red;
            public byte Green;
            public byte Blue;
            private static string PathName => UserDataPaths.SettingsFile("tianzheng-room-rename.ini");

            public static RoomRenameSettings Load()
            {
                var result = new RoomRenameSettings();
                try
                {
                    foreach (var line in File.ReadAllLines(PathName, Encoding.UTF8))
                    {
                        var separator = line.IndexOf('='); if (separator <= 0) continue;
                        var key = line.Substring(0, separator); var value = line.Substring(separator + 1);
                        int number;
                        if (key == "Name") { try { result.LastName = Encoding.UTF8.GetString(Convert.FromBase64String(value)); } catch { } }
                        else if (key == "ChangeColor") result.ChangeColor = value == "1";
                        else if (key == "Method" && int.TryParse(value, out number)) result.ColorMethod = number;
                        else if (key == "Index" && int.TryParse(value, out number)) result.ColorIndex = number;
                        else if (key == "Red" && int.TryParse(value, out number)) result.Red = (byte)Math.Max(0, Math.Min(255, number));
                        else if (key == "Green" && int.TryParse(value, out number)) result.Green = (byte)Math.Max(0, Math.Min(255, number));
                        else if (key == "Blue" && int.TryParse(value, out number)) result.Blue = (byte)Math.Max(0, Math.Min(255, number));
                    }
                }
                catch { }
                return result;
            }

            public void Save()
            {
                var lines = new[] { "Name=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(LastName ?? string.Empty)), "ChangeColor=" + (ChangeColor ? "1" : "0"), "Method=" + ColorMethod.ToString(CultureInfo.InvariantCulture), "Index=" + ColorIndex.ToString(CultureInfo.InvariantCulture), "Red=" + Red, "Green=" + Green, "Blue=" + Blue };
                File.WriteAllLines(PathName, lines, Encoding.UTF8);
            }

            public AcColor ToColor()
            {
                try
                {
                    var method = (Autodesk.AutoCAD.Colors.ColorMethod)ColorMethod;
                    return method == Autodesk.AutoCAD.Colors.ColorMethod.ByColor ? AcColor.FromRgb(Red, Green, Blue) : AcColor.FromColorIndex(method, (short)ColorIndex);
                }
                catch { return AcColor.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7); }
            }

            public void SetColor(AcColor color)
            {
                if (color == null) return;
                ColorMethod = (int)color.ColorMethod; ColorIndex = color.ColorIndex; Red = color.Red; Green = color.Green; Blue = color.Blue;
            }
        }
    }
}
