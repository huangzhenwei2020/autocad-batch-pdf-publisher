using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DoorWindowFloorStatisticsForm : DpiAwareForm
    {
        private sealed class FloorSource
        {
            public string Name;
            public int Count;
            public DoorWindowScheduleReadResult Source;
        }

        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly List<FloorSource> _sources = new List<FloorSource>();
        private readonly DataGridView _grid = new DataGridView();
        private readonly Label _hint = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly Button _pick = new Button { Text = "拾取楼层门窗表", AutoSize = true, Height = 29 };
        private readonly Button _edit = new Button { Text = "修改楼层设置", AutoSize = true, Height = 29 };
        private readonly Button _remove = new Button { Text = "删除选中楼层", AutoSize = true, Height = 29 };
        private readonly Button _ok = new Button { Text = "确定并返回", AutoSize = true, Height = 29 };
        private readonly Button _cancel = new Button { Text = "取消", AutoSize = true, Height = 29 };

        public IReadOnlyList<DoorWindowScheduleReadResult> Results { get { return _sources.Select(x => x.Source).ToList(); } }
        public IReadOnlyList<string> FloorNames { get { return _sources.Select(x => x.Name).ToList(); } }
        public IReadOnlyList<int> FloorCounts { get { return _sources.Select(x => x.Count).ToList(); } }

        public DoorWindowFloorStatisticsForm(Document document)
        {
            _document = document; Text = "门窗表·每层单独统计设置"; StartPosition = FormStartPosition.CenterParent;
            Width = 760; Height = 470; MinimumSize = new Size(620, 360); Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            _documentBinding = new ModelessDocumentBinding(this, document);
            Build(); RefreshGrid();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Padding = new Padding(0, 3, 0, 3) };
            top.Controls.Add(new Label { Text = "每个楼层独立拾取门窗表；标准层在“层数”填写重复层数，例如 3~10 层填写 8。", AutoSize = true, Margin = new Padding(0, 7, 20, 0) });
            top.Controls.Add(_pick); _pick.Click += (s, e) => Pick(); top.Controls.Add(_edit); _edit.Click += (s, e) => Edit(); top.Controls.Add(_remove); _remove.Click += (s, e) => Remove();
            root.Controls.Add(top, 0, 0);
            _grid.Dock = DockStyle.Fill; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.AutoGenerateColumns = false; _grid.ReadOnly = true;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "楼层/标准层", Width = 180 }); _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "层数", Width = 80 }); _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "门窗表来源", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            root.Controls.Add(_grid, 0, 1);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 6, 0, 3) }; footer.Controls.Add(_cancel); _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); }; footer.Controls.Add(_ok); _ok.Click += (s, e) => { if (_sources.Count == 0) { MessageBox.Show(this, "请至少拾取一个楼层门窗表。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; } DialogResult = DialogResult.OK; Close(); }; footer.Controls.Add(_hint); root.Controls.Add(footer, 0, 2); Controls.Add(root);
        }

#if ACAD_R19
        private void Pick()
#else
        private async void Pick()
#endif
        {
            Hide();
            try
            {
                _document.Window.Focus(); PromptEntityResult result;
#if ACAD_R19
                result = _document.Editor.GetEntity(new PromptEntityOptions("\n请选择楼层门窗表："));
#else
                result = null;
                await CadCommandContext.ExecuteAsync(() => result = _document.Editor.GetEntity(new PromptEntityOptions("\n请选择楼层门窗表：")));
#endif
                if (result == null || result.Status != PromptStatus.OK) return;
                DoorWindowScheduleReadResult source;
                using (_document.LockDocument()) using (var transaction = _document.Database.TransactionManager.StartTransaction()) source = TianzhengDoorWindowService.Read(transaction.GetObject(result.ObjectId, OpenMode.ForRead, false));
                string name; int count;
                if (!ShowFloorEntry("设置楼层", (_sources.Count + 1) + "层", 1, out name, out count)) return;
                _sources.RemoveAll(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)); _sources.Add(new FloorSource { Name = name, Count = count, Source = source }); RefreshGrid();
            }
            catch (Exception exception) { MessageBox.Show(this, "拾取楼层门窗表失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!IsDisposed) { Show(); Activate(); } }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            BeginInvoke(new Action(() => { if (!IsDisposed) PerformLayout(); }));
        }

        private void Edit()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index < 0 || _grid.CurrentRow.Index >= _sources.Count) return;
            var source = _sources[_grid.CurrentRow.Index]; string name; int count;
            if (!ShowFloorEntry("修改楼层设置", source.Name, source.Count, out name, out count)) return;
            source.Name = name; source.Count = count; RefreshGrid();
        }

        private bool ShowFloorEntry(string title, string defaultName, int defaultCount, out string name, out int count)
        {
            name = null; count = 0;
            using (var dialog = new Form { Text = title, Width = 430, Height = 190, MinimumSize = new Size(360, 160), StartPosition = FormStartPosition.CenterParent, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 3 };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var nameBox = new TextBox { Dock = DockStyle.Fill, Text = defaultName };
            var countBox = new NumericUpDown { Dock = DockStyle.Left, Minimum = 1, Maximum = 999, Value = Math.Max(1, Math.Min(999, defaultCount)), Width = 100 };
                nameBox.TextChanged += (sender, args) => { int parsed; if (TryParseFloorCount(nameBox.Text, out parsed)) countBox.Value = Math.Max(1, Math.Min(999, parsed)); };
                layout.Controls.Add(new Label { Text = "楼层名称", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, 0, 0); layout.Controls.Add(nameBox, 1, 0);
                layout.Controls.Add(new Label { Text = "代表层数", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, 0, 1); layout.Controls.Add(countBox, 1, 1);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true }; var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true };
                buttons.Controls.Add(cancel); buttons.Controls.Add(ok); layout.Controls.Add(buttons, 1, 2); dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(nameBox.Text)) return false;
                name = nameBox.Text.Trim(); int parsedCount; count = TryParseFloorCount(name, out parsedCount) ? parsedCount : (int)countBox.Value; return true;
            }
        }

        private static bool TryParseFloorCount(string text, out int count)
        {
            count = 1;
            var match = Regex.Match(text ?? string.Empty, @"(\d+)\s*[~～至\-—]\s*(\d+)");
            int first, last;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out first) || !int.TryParse(match.Groups[2].Value, out last)) return false;
            count = Math.Abs(last - first) + 1;
            return count > 0 && count <= 999;
        }

        private void Remove() { if (_grid.CurrentRow == null || _grid.CurrentRow.Index < 0 || _grid.CurrentRow.Index >= _sources.Count) return; _sources.RemoveAt(_grid.CurrentRow.Index); RefreshGrid(); }
        private void RefreshGrid() { _grid.Rows.Clear(); foreach (var source in _sources) _grid.Rows.Add(source.Name, source.Count, source.Source.SourceDxfName + " / Handle " + source.Source.SourceHandle); _hint.Text = "已设置 " + _sources.Count + " 个楼层表"; }
    }
}
