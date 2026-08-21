using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    /// <summary>
    /// 门窗立面排版预览对话框：选择排版图框、调整排版范围（页边距/页间距），
    /// 在预览中拖拽门窗调整顺序，确认后按当前顺序与范围插入 CAD。
    /// </summary>
    internal sealed class DoorWindowLayoutPreviewForm : DpiAwareForm
    {
        private readonly IList<DoorWindowScheduleItem> _source;
        private readonly Document _document;
        private readonly int _scale;
        private readonly FrameDefinition _initialFrame;
        private readonly List<FrameDefinition> _frames;
        private readonly DoorWindowLayoutPreviewControl _preview = new DoorWindowLayoutPreviewControl();
        private readonly ComboBox _frameChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Height = 28 };
        private readonly TextBox _leftMargin = new TextBox { Width = 52 }, _rightMargin = new TextBox { Width = 52 }, _topMargin = new TextBox { Width = 52 }, _bottomMargin = new TextBox { Width = 52 }, _pageGap = new TextBox { Width = 52 }, _itemGap = new TextBox { Width = 52 }, _boundaryGap = new TextBox { Width = 52 };
        private readonly CheckBox _includeSchedule = new CheckBox { Text = "同时插入门窗表", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _includeNotes = new CheckBox { Text = "门窗设计说明", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _autoFrame = new CheckBox { Text = "自动最小图框", AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _useTianzhengTitle = new CheckBox { Text = "天正图名标注", AutoSize = true, Margin = new Padding(8, 7, 0, 0), Checked = true };
        private readonly Label _summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(60, 75, 92) };

        public DoorWindowElevationInsertionService.DoorWindowLayoutOptions Options { get; private set; }
        public IList<DoorWindowScheduleItem> OrderedItems => _preview.OrderedItems;
        public FrameDefinition SelectedLayoutFrame => SelectedFrame();

        public DoorWindowLayoutPreviewForm(Document document, IList<DoorWindowScheduleItem> items, int drawingScale, FrameDefinition selectedFrame, bool includeSchedule, bool includeNotes, bool useTianzhengTitle)
        {
            _document = document;
            _source = (items ?? new List<DoorWindowScheduleItem>()).Where(x => x != null).ToList();
            _scale = Math.Max(1, drawingScale);
            _frames = new PublishPlanStore().LoadFrames().Where(x => !string.IsNullOrWhiteSpace(x.BlockName)).ToList();
            var saved = LoadSavedMargins();
            _initialFrame = selectedFrame
                ?? _frames.FirstOrDefault(x => !string.IsNullOrWhiteSpace(saved.LayoutFrameRegistrationId) && string.Equals(x.RegistrationId, saved.LayoutFrameRegistrationId, StringComparison.OrdinalIgnoreCase))
                ?? _frames.FirstOrDefault(x => !string.IsNullOrWhiteSpace(saved.LayoutFrameBlockName) && string.Equals(x.BlockName, saved.LayoutFrameBlockName, StringComparison.OrdinalIgnoreCase));
            Text = "门窗立面排版预览"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false;
            ClientSize = new Size(980, 640); MinimumSize = new Size(820, 520); Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.White;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.White };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(12, 10, 8, 6), BackColor = Color.FromArgb(245, 247, 250) };
            toolbar.Controls.Add(LabelFor("排版图框"));
            foreach (var frame in _frames) _frameChoice.Items.Add(new FrameChoice { Frame = frame });
            if (_frameChoice.Items.Count == 0) _frameChoice.Items.Add(new FrameChoice());
            var index = _frames.FindIndex(x => _initialFrame != null
                && ((!string.IsNullOrWhiteSpace(_initialFrame.RegistrationId) && string.Equals(x.RegistrationId, _initialFrame.RegistrationId, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(x.BlockName, _initialFrame.BlockName, StringComparison.OrdinalIgnoreCase)));
            _frameChoice.SelectedIndex = index >= 0 ? index : 0;
            _frameChoice.SelectedIndexChanged += (s, e) => { SaveMargins(); RefreshLayout(); };
            toolbar.Controls.Add(_frameChoice);
            toolbar.Controls.Add(LabelFor("页间距")); toolbar.Controls.Add(_pageGap);
            toolbar.Controls.Add(LabelFor("门窗间距")); toolbar.Controls.Add(_itemGap);
            toolbar.Controls.Add(LabelFor("边界间距")); toolbar.Controls.Add(_boundaryGap);
            _includeSchedule.Checked = includeSchedule;
            _includeSchedule.CheckedChanged += (s, e) => { _includeNotes.Enabled = _includeSchedule.Checked; RefreshLayout(); };
            toolbar.Controls.Add(_includeSchedule);
            _includeNotes.Checked = includeNotes;
            _includeNotes.CheckedChanged += (s, e) => RefreshLayout();
            _includeNotes.Enabled = _includeSchedule.Checked;
            toolbar.Controls.Add(_includeNotes);
            _autoFrame.CheckedChanged += (s, e) => { if (_autoFrame.Checked) ApplyAutoFrame(); else { Options = ReadOptions(); RefreshSummary(); } };
            toolbar.Controls.Add(_autoFrame);
            _useTianzhengTitle.Checked = useTianzhengTitle;
            _useTianzhengTitle.CheckedChanged += (s, e) => { Options = ReadOptions(); RefreshSummary(); };
            toolbar.Controls.Add(_useTianzhengTitle);
            var lockButton = ButtonFor("锁定到本页"); lockButton.Click += (s, e) => { _preview.LockSelectedToPage(); RefreshSummary(); }; toolbar.Controls.Add(lockButton);
            var unlockButton = ButtonFor("解锁"); unlockButton.Click += (s, e) => { _preview.UnlockSelected(); RefreshSummary(); }; toolbar.Controls.Add(unlockButton);
            var registerRange = ButtonFor("登记排版范围"); registerRange.Click += (s, e) => RegisterLayoutRange(); toolbar.Controls.Add(registerRange);
            var hint = new Label { Text = "排版范围读取自图框登记；门窗表和设计说明排在门窗之后。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 6, 0, 0) };
            toolbar.Controls.Add(hint);
            root.Controls.Add(toolbar, 0, 0);

            root.Controls.Add(_preview, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), ColumnCount = 2, BackColor = Color.FromArgb(245, 247, 250) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _summary.Margin = new Padding(0, 8, 0, 0); footer.Controls.Add(_summary, 0, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            var cancel = ButtonFor("取消"); cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); }; buttons.Controls.Add(cancel);
            var ok = ButtonFor("确认排版并插入"); ok.Click += (s, e) => Confirm(); buttons.Controls.Add(ok);
            footer.Controls.Add(buttons, 1, 0); root.Controls.Add(footer, 0, 2);
            Controls.Add(root);

            SetMargins(saved);
            FormClosing += (s, e) => SaveMargins();
            Shown += (s, e) => { RefreshLayout(); ShowMissingRangeReminder(); };
        }

        /// <summary>排版参数持久化路径：记录上次使用的边距/页间距，下次打开自动恢复。</summary>
        internal static string LayoutSettingsPath { get { return BatchPdfPublisher.Services.UserDataPaths.SettingsFile("door-window-layout.ini"); } }

        internal static DoorWindowElevationInsertionService.DoorWindowLayoutOptions LoadSavedMargins()
        {
            var options = new DoorWindowElevationInsertionService.DoorWindowLayoutOptions();
            try
            {
                if (System.IO.File.Exists(LayoutSettingsPath))
                {
                    var data = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in System.IO.File.ReadAllLines(LayoutSettingsPath))
                    {
                        var i = line.IndexOf('=');
                        if (i > 0) data[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                    options.LeftMargin = ReadDouble(data, "Left", options.LeftMargin);
                    options.RightMargin = ReadDouble(data, "Right", options.RightMargin);
                    options.TopMargin = ReadDouble(data, "Top", options.TopMargin);
                    options.BottomMargin = ReadDouble(data, "Bottom", options.BottomMargin);
                    options.PageGap = ReadDouble(data, "PageGap", options.PageGap);
                    options.ItemGap = ReadDouble(data, "ItemGap", options.ItemGap);
                    options.BoundaryGap = ReadDouble(data, "BoundaryGap", options.BoundaryGap);
                    string frameRegistrationId;
                    if (data.TryGetValue("FrameRegistrationId", out frameRegistrationId)) options.LayoutFrameRegistrationId = frameRegistrationId;
                    string frameBlockName;
                    if (data.TryGetValue("FrameBlockName", out frameBlockName)) options.LayoutFrameBlockName = frameBlockName;
                    string scheduleValue;
                    if (data.TryGetValue("IncludeSchedule", out scheduleValue)) options.IncludeSchedule = string.Equals(scheduleValue, "1", StringComparison.Ordinal) || string.Equals(scheduleValue, "true", StringComparison.OrdinalIgnoreCase);
                    string notesValue;
                    if (data.TryGetValue("IncludeScheduleNotes", out notesValue)) options.IncludeScheduleNotes = string.Equals(notesValue, "1", StringComparison.Ordinal) || string.Equals(notesValue, "true", StringComparison.OrdinalIgnoreCase);
                    string titleValue;
                    if (data.TryGetValue("UseTianzhengTitle", out titleValue)) options.UseTianzhengTitle = !string.Equals(titleValue, "0", StringComparison.Ordinal) && !string.Equals(titleValue, "false", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return options;
        }

        private static double ReadDouble(System.Collections.Generic.IDictionary<string, string> data, string key, double fallback)
        {
            string value;
            double result;
            return data.TryGetValue(key, out value) && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) && result >= 0d ? result : fallback;
        }

        private void SaveMargins()
        {
            try
            {
                var options = ReadOptions();
                var frame = SelectedFrame();
                var lines = new[]
                {
                    "Left=" + options.LeftMargin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Right=" + options.RightMargin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Top=" + options.TopMargin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Bottom=" + options.BottomMargin.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "PageGap=" + options.PageGap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "ItemGap=" + options.ItemGap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "BoundaryGap=" + options.BoundaryGap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "FrameRegistrationId=" + (frame == null ? string.Empty : frame.RegistrationId ?? string.Empty),
                    "FrameBlockName=" + (frame == null ? string.Empty : frame.BlockName ?? string.Empty),
                    "IncludeSchedule=" + (options.IncludeSchedule ? "1" : "0"),
                    "IncludeScheduleNotes=" + (options.IncludeScheduleNotes ? "1" : "0"),
                    "UseTianzhengTitle=" + (options.UseTianzhengTitle ? "1" : "0")
                };
                System.IO.File.WriteAllLines(LayoutSettingsPath, lines);
            }
            catch { }
        }

        private sealed class FrameChoice { public FrameDefinition Frame; public override string ToString() { return Frame == null ? "未选择图框" : Frame.DisplayName; } }

        private void SetMargins(DoorWindowElevationInsertionService.DoorWindowLayoutOptions options)
        {
            _leftMargin.Text = options.LeftMargin.ToString("0.##"); _rightMargin.Text = options.RightMargin.ToString("0.##");
            _topMargin.Text = options.TopMargin.ToString("0.##"); _bottomMargin.Text = options.BottomMargin.ToString("0.##"); _pageGap.Text = options.PageGap.ToString("0.##");
            _itemGap.Text = options.ItemGap.ToString("0.##");
            _boundaryGap.Text = options.BoundaryGap.ToString("0.##");
        }

        private DoorWindowElevationInsertionService.DoorWindowLayoutOptions ReadOptions()
        {
            var frame = SelectedFrame(); var hasRange = FrameLayoutRangeService.HasValidRange(frame);
            return new DoorWindowElevationInsertionService.DoorWindowLayoutOptions
            {
                LeftMargin = hasRange ? frame.LayoutLeftMargin : 0d, RightMargin = hasRange ? frame.LayoutRightMargin : 0d,
                TopMargin = hasRange ? frame.LayoutTopMargin : 0d, BottomMargin = hasRange ? frame.LayoutBottomMargin : 0d, PageGap = Parse(_pageGap.Text, 30d),
                ItemGap = Parse(_itemGap.Text, 5d),
                BoundaryGap = Parse(_boundaryGap.Text, 10d),
                LayoutFrameRegistrationId = SelectedFrame() == null ? null : SelectedFrame().RegistrationId,
                LayoutFrameBlockName = SelectedFrame() == null ? null : SelectedFrame().BlockName,
                IncludeSchedule = _includeSchedule.Checked,
                IncludeScheduleNotes = _includeSchedule.Checked && _includeNotes.Checked,
                UseTianzhengTitle = _useTianzhengTitle.Checked
            };
        }

        private async void RegisterLayoutRange()
        {
            var frame = SelectedFrame();
            if (frame == null) { MessageBox.Show(this, "请选择排版图框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Hide(); DetailLayoutOptions selected = null; Exception failure = null;
            try
            {
                var current = new DetailLayoutOptions { ItemGap = Parse(_itemGap.Text, 5d), PageGap = Parse(_pageGap.Text, 30d) };
                await CadCommandContext.ExecuteAsync(() =>
                {
                    var anchor = DetailLayoutService.InsertFrameForRange(_document, frame, 1);
                    if (anchor != null) selected = DetailLayoutService.PromptLayoutRange(_document, frame, 1, anchor, current);
                    if (selected != null) FrameLayoutRangeService.SaveRange(frame, selected.LeftMargin, selected.RightMargin, selected.TopMargin, selected.BottomMargin);
                });
            }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) { MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (selected != null) RefreshLayout();
        }
        private static double Parse(string text, double fallback) { double value; return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0 ? value : fallback; }

        private void RefreshLayout()
        {
            var frame = SelectedFrame();
            Options = ReadOptions();
            try
            {
                // 首次打开时预览控件尚未加载数据（OrderedItems 为空），必须用初始数据源 _source；
                // 之后重建时基于拖拽后的顺序（锁定状态保存在 item 上，随重建保留）。
                var source = _preview.OrderedItems.Count > 0 ? _preview.OrderedItems.ToList() : _source;
                _preview.SetLayout(source, frame, _scale, Options);
                RefreshSummary(frame);
            }
            catch (Exception exception) { _summary.Text = exception.Message; }
        }

        /// <summary>只刷新页数与摘要（不重建布局，保留拖拽顺序与锁定状态）。</summary>
        private void RefreshSummary()
        {
            try { RefreshSummary(SelectedFrame()); }
            catch (Exception exception) { _summary.Text = exception.Message; }
        }

        private void RefreshSummary(FrameDefinition frame)
        {
            _summary.Text = _preview.OrderedItems.Count + " 个门窗";
            var lockedCount = _preview.OrderedItems.Count(x => x.LockedPage > 0);
            if (lockedCount > 0) _summary.Text += " · 锁定 " + lockedCount + " 个";
            if (frame != null)
            {
                var plan = DoorWindowElevationInsertionService.ComputeLayout(_preview.OrderedItems, _scale, frame, Options);
                _summary.Text += " · " + plan.PageCount + " 页 " + frame.PaperDisplay + " · 页间距 " + Options.PageGap.ToString("0.##") + " mm";
            }
        }

        private FrameDefinition SelectedFrame()
        { var choice = _frameChoice.SelectedItem as FrameChoice; return choice == null ? null : choice.Frame; }

        private void ShowMissingRangeReminder()
        {
            var frame = SelectedFrame(); if (frame == null || FrameLayoutRangeService.HasValidRange(frame)) return;
            MessageBox.Show(this, "当前图框尚未登记排版范围。\r\n\r\n请点击“登记排版范围”，程序将插入一个 1:1 图框供您框选，并把范围写入图框登记。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>自动选择最小图框：比较页数与纸张面积，选出最省纸方案并切换到该图框。</summary>
        private void ApplyAutoFrame()
        {
            try
            {
                var options = ReadOptions();
                var best = DoorWindowElevationInsertionService.PickSmallestFrame(_preview.OrderedItems.ToList(), _scale, _frames, options);
                if (best == null)
                {
                    _autoFrame.Checked = false;
                    _summary.Text = "没有可放下的登记图框（请检查图框登记与排版参数）。";
                    return;
                }
                var index = _frames.FindIndex(x => string.Equals(x.RegistrationId, best.RegistrationId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && _frameChoice.SelectedIndex != index) _frameChoice.SelectedIndex = index;
                else RefreshSummary();
            }
            catch (Exception exception) { _summary.Text = "自动选择图框失败：" + exception.Message; }
        }

        private void Confirm()
        {
            var frame = SelectedFrame();
            if (frame == null) { MessageBox.Show(this, "请选择排版图框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Options = ReadOptions();
            try { DoorWindowElevationInsertionService.ComputeLayout(_preview.OrderedItems, _scale, frame, Options); }
            catch (Exception exception) { MessageBox.Show(this, "排版校验失败：" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SaveMargins();
            DialogResult = DialogResult.OK; Close();
        }

        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 7, 2, 0), ForeColor = Color.FromArgb(45, 55, 70) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(4, 2, 0, 0), FlatStyle = FlatStyle.Standard }; }
    }
}
