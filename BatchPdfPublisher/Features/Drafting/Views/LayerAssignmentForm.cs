using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// 归层对话框：把所选对象归到用户指定的图层。
    /// 图层清单来自标准（BZS）与当前图纸的并集，方便把项目图层直接归到标准图层上。
    /// </summary>
    public sealed class LayerAssignmentForm : DpiAwareForm
    {
        private readonly Database _database;
        private readonly int _selectionCount;
        private readonly IList<ObjectId> _selectionIds;
        private readonly TextBox _filter = new TextBox { Width = 380 };
        private readonly ListBox _layers = new ListBox();
        private readonly CheckBox _systemOnly = new CheckBox { Text = "只显示系统图层", AutoSize = true };
        private readonly CheckBox _setByLayer = new CheckBox { Text = "颜色、线型、线宽设为随层", AutoSize = true };
        private readonly CheckBox _blockAttributes = new CheckBox { Text = "块属性一起归层", AutoSize = true };
        private readonly CheckBox _mergeLayers = new CheckBox { Text = "把所选对象所在的旧图层整体合并到目标图层", AutoSize = true };
        private readonly CheckBox _remember = new CheckBox { Text = "记住本次选择", AutoSize = true };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.FromArgb(65, 75, 90) };
        private readonly Label _mergeHint = new Label { AutoSize = false, ForeColor = Color.FromArgb(150, 90, 30) };
        private readonly List<string> _allLayers = new List<string>();
        private HashSet<string> _systemLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>用户确认后的目标图层名；未确认为 null。</summary>
        public string TargetLayerName { get; private set; }
        public bool SetByLayer { get { return _setByLayer.Checked; } }
        public bool IncludeBlockAttributes { get { return _blockAttributes.Checked; } }
        public bool RememberChoice { get { return _remember.Checked; } }
        /// <summary>是否把所选对象所在的旧图层整体并入目标图层。</summary>
        public bool MergeSourceLayers { get { return _mergeLayers.Checked; } }

        public LayerAssignmentForm(Database database, IList<ObjectId> selectionIds)
        {
            _database = database;
            _selectionIds = selectionIds ?? new List<ObjectId>();
            _selectionCount = _selectionIds.Count;

            Text = "归层（所选对象移动到图层）";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false;
            ClientSize = new Size(560, 660);
            MinimumSize = new Size(500, 600);
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;

            var header = new Label
            {
                Dock = DockStyle.Top, Height = 46, Padding = new Padding(16, 12, 12, 0),
                Text = "把所选 " + _selectionCount + " 个对象归到下面的图层。输入关键字可筛选。",
                ForeColor = Color.FromArgb(45, 55, 70)
            };

            var filterPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(16, 6, 16, 4) };
            filterPanel.Controls.Add(new Label { Text = "筛选图层", Location = new Point(16, 10), AutoSize = true });
            _filter.Location = new Point(90, 7); _filter.Width = 430;
            _filter.TextChanged += delegate { RefreshList(); };
            filterPanel.Controls.Add(_filter);
            _systemOnly.Location = new Point(90, 36);
            // 默认只看标准里定义的图层，避免图纸里成百上千个杂层淹没清单。
            _systemOnly.Checked = true;
            _systemOnly.CheckedChanged += delegate { RefreshList(); };
            filterPanel.Controls.Add(_systemOnly);

            var optionPanel = new Panel { Dock = DockStyle.Bottom, Height = 158, Padding = new Padding(16, 6, 16, 4) };
            _setByLayer.Location = new Point(16, 6);
            _blockAttributes.Location = new Point(16, 30);
            _mergeLayers.Location = new Point(16, 54);
            _mergeHint.Location = new Point(36, 78); _mergeHint.Size = new Size(490, 36);
            _remember.Location = new Point(16, 118);
            _status.Location = new Point(16, 138);
            optionPanel.Controls.Add(_setByLayer); optionPanel.Controls.Add(_blockAttributes);
            optionPanel.Controls.Add(_mergeLayers); optionPanel.Controls.Add(_mergeHint);
            optionPanel.Controls.Add(_remember); optionPanel.Controls.Add(_status);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 8, 16, 8), BackColor = Color.FromArgb(247, 248, 250) };
            var ok = new Button { Text = "归层", Width = 110, Height = 32, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
            ok.Click += delegate { Confirm(); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 240, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            footer.Controls.Add(buttons);

            _layers.Dock = DockStyle.Fill;
            _layers.IntegralHeight = false;
            _layers.DoubleClick += delegate { if (_layers.SelectedItem != null) { Confirm(); DialogResult = DialogResult.OK; } };

            Controls.Add(_layers);
            Controls.Add(optionPanel);
            Controls.Add(footer);
            Controls.Add(filterPanel);
            Controls.Add(header);
            AcceptButton = ok; CancelButton = cancel;

            LoadLayers();
            UpdateMergeHint();
            _mergeLayers.CheckedChanged += delegate { UpdateMergeHint(); };
            ApplyRemembered();
        }

        private void UpdateMergeHint()
        {
            if (!_mergeLayers.Checked)
            {
                _mergeHint.Text = "仅移动所选对象本身，其他图层上的对象不受影响。";
                _mergeHint.ForeColor = Color.FromArgb(75, 85, 100);
                _setByLayer.Enabled = true;
                return;
            }
            var sources = LayerAssignmentService.GetLayersOfObjects(_database == null ? null : Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument, _selectionIds);
            _mergeHint.Text = sources.Count == 0
                ? "所选对象所在图层无法识别，将按“仅移动所选对象”处理。"
                : "将把 " + sources.Count + " 个旧图层（" + string.Join("、", sources.ToArray()) + "）上的所有对象并入目标图层，"
                  + "清空的旧图层会被清理。此选项会删除原图层，颜色将自动设为随层。";
            _mergeHint.ForeColor = Color.FromArgb(150, 90, 30);
            // 旧图层会被删除，此时颜色必须随层，否则对象拿着已删除图层的颜色定义。
            _setByLayer.Checked = true;
            _setByLayer.Enabled = false;
        }

        private void LoadLayers()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var transaction = _database.TransactionManager.StartOpenCloseTransaction())
                {
                    var table = (LayerTable)transaction.GetObject(_database.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        // 锁定/关闭的图层仍可显示，但归层前会拦下来，避免用户事后才发现。
                        var record = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                        if (record != null) names.Add(record.Name);
                    }
                }
            }
            catch { }
            // 标准里的图层即使当前图纸还没有也列出来：归层时按标准创建它。
            _systemLayers = LayerAssignmentService.GetSystemLayerNames();
            foreach (var name in _systemLayers) names.Add(name);
            _allLayers.Clear();
            _allLayers.AddRange(names);
            RefreshList();
        }

        private void RefreshList()
        {
            var keyword = (_filter.Text ?? string.Empty).Trim();
            var selected = _layers.SelectedItem as string;
            _layers.BeginUpdate();
            _layers.Items.Clear();
            foreach (var name in _allLayers)
            {
                if (_systemOnly.Checked && !_systemLayers.Contains(name)) continue;
                if (keyword.Length > 0 && name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _layers.Items.Add(name);
            }
            _layers.EndUpdate();
            if (selected != null && _layers.Items.Contains(selected)) _layers.SelectedItem = selected;
            else if (_layers.Items.Count > 0) _layers.SelectedIndex = 0;
            _status.Text = "共 " + _layers.Items.Count + " 个图层可选"
                + (_systemOnly.Checked ? "（系统图层 " + _systemLayers.Count + " 个）" : "（含图纸中的全部图层）") + "。";
        }

        private void Confirm()
        {
            TargetLayerName = _layers.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(TargetLayerName))
            {
                MessageBox.Show(this, "请先选择一个目标图层。", "归层", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_remember.Checked) LayerAssignmentMemory.Save(TargetLayerName, _setByLayer.Checked, _blockAttributes.Checked, _systemOnly.Checked);
        }

        private void ApplyRemembered()
        {
            var memory = LayerAssignmentMemory.Load();
            _setByLayer.Checked = memory.SetByLayer;
            _blockAttributes.Checked = memory.IncludeBlockAttributes;
            _systemOnly.Checked = memory.SystemLayersOnly;
            _remember.Checked = memory.HasValue;
            RefreshList();
            if (!string.IsNullOrWhiteSpace(memory.LayerName) && _layers.Items.Contains(memory.LayerName))
                _layers.SelectedItem = memory.LayerName;
        }
    }

    /// <summary>记住上次归层使用的目标图层与选项，减少重复操作。</summary>
    public sealed class LayerAssignmentMemory
    {
        public string LayerName;
        public bool SetByLayer = true;
        public bool IncludeBlockAttributes = true;
        public bool SystemLayersOnly = true;
        public bool HasValue;

        private static string SettingsPath { get { return UserDataPaths.SettingsFile("layer-assignment.ini"); } }

        public static LayerAssignmentMemory Load()
        {
            var memory = new LayerAssignmentMemory();
            try
            {
                if (!File.Exists(SettingsPath)) return memory;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var split = line.IndexOf('=');
                    if (split <= 0) continue;
                    var key = line.Substring(0, split).Trim();
                    var value = line.Substring(split + 1).Trim();
                    if (string.Equals(key, "Layer", StringComparison.OrdinalIgnoreCase)) { memory.LayerName = value; memory.HasValue = !string.IsNullOrWhiteSpace(value); }
                    else if (string.Equals(key, "SetByLayer", StringComparison.OrdinalIgnoreCase)) memory.SetByLayer = value == "1";
                    else if (string.Equals(key, "BlockAttributes", StringComparison.OrdinalIgnoreCase)) memory.IncludeBlockAttributes = value == "1";
                    else if (string.Equals(key, "SystemLayersOnly", StringComparison.OrdinalIgnoreCase)) memory.SystemLayersOnly = value == "1";
                }
            }
            catch { }
            return memory;
        }

        public static void Save(string layerName, bool setByLayer, bool includeBlockAttributes, bool systemLayersOnly)
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[]
                {
                    "Layer=" + layerName,
                    "SetByLayer=" + (setByLayer ? "1" : "0"),
                    "BlockAttributes=" + (includeBlockAttributes ? "1" : "0"),
                    "SystemLayersOnly=" + (systemLayersOnly ? "1" : "0")
                });
            }
            catch { }
        }
    }
}
