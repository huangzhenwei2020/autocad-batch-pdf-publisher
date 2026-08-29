using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class DetailLayoutForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly List<DetailLayoutItem> _items = new List<DetailLayoutItem>();
        private readonly List<FrameDefinition> _frames;
        private readonly CheckedListBox _list = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true };
        private readonly DetailLayoutPreviewControl _preview = new DetailLayoutPreviewControl();
        private readonly ComboBox _frame = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        private readonly ComboBox _scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 76 };
        private readonly TextBox _left = Box(), _right = Box(), _top = Box(), _bottom = Box(), _gap = Box(), _pageGap = Box();
        private readonly Label _summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(55, 70, 88), Margin = new Padding(0, 8, 0, 0) };
        private readonly CheckBox _deleteSources = new CheckBox { Text = "排版后删除源大样（不删除小平面）", AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
        private DetailLayoutFrameAnchor _frameAnchor;
        private bool _hasExplicitRange;
        private string _rangeFrameId;
        private string _rangeFrameBlock;
        private int _rangeScale;

        private static string SettingsPath { get { return UserDataPaths.SettingsFile("detail-layout.ini"); } }

        public DetailLayoutForm(Document document)
        {
            _document = document;
            _frames = new PublishPlanStore().LoadFrames().Where(x => x != null && !string.IsNullOrWhiteSpace(x.BlockName)).ToList();
            Text = "大样排版"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1050, 690); MinimumSize = new Size(850, 540);
            Font = new Font("Microsoft YaHei UI", 9f); BackColor = Color.White;
            Build(); LoadSettings(); RefreshLayout(true);
            FormClosed += (s, e) => SaveSettings();
        }

        private void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(10, 8, 8, 4), BackColor = Color.FromArgb(245, 247, 250) };
            toolbar.Controls.Add(LabelFor("登记图框"));
            foreach (var value in _frames) _frame.Items.Add(new FrameChoice { Frame = value });
            if (_frame.Items.Count > 0) _frame.SelectedIndex = 0;
            _frame.SelectedIndexChanged += (s, e) => RefreshLayout(false); toolbar.Controls.Add(_frame);
            toolbar.Controls.Add(LabelFor("比例")); _scale.Items.AddRange(new object[] { "1:20", "1:25", "1:50", "1:100", "1:150", "1:200" }); _scale.Text = "1:50"; toolbar.Controls.Add(_scale);
            toolbar.Controls.Add(LabelFor("大样间距")); toolbar.Controls.Add(_gap); toolbar.Controls.Add(LabelFor("页间距")); toolbar.Controls.Add(_pageGap);
            var apply = ButtonFor("更新预览"); apply.Click += (s, e) => RefreshLayout(false); toolbar.Controls.Add(apply);
            toolbar.Controls.Add(_deleteSources);
            toolbar.Controls.Add(new Label { Text = "带图框插入使用图框登记范围；也可在底部直接框选无图框排版范围", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 7, 0, 0) });
            root.Controls.Add(toolbar, 0, 0);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 230, BackColor = Color.FromArgb(225, 230, 236) };
            var leftPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(8), BackColor = Color.White };
            leftPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var sourceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            var add = ButtonFor("添加大样"); add.Click += (s, e) => AddDetail(); sourceButtons.Controls.Add(add);
            var addPlan = ButtonFor("框选平面"); addPlan.Click += (s, e) => AddSmallPlan(); sourceButtons.Controls.Add(addPlan);
            var rename = ButtonFor("编辑名称"); rename.Click += (s, e) => EditSelectedName(); sourceButtons.Controls.Add(rename);
            var remove = ButtonFor("删除"); remove.Click += (s, e) => RemoveSelected(); sourceButtons.Controls.Add(remove);
            var clear = ButtonFor("清空"); clear.Click += (s, e) => { _items.Clear(); SyncList(-1); RefreshLayout(true); }; sourceButtons.Controls.Add(clear);
            var allNumbers = ButtonFor("全部编号"); allNumbers.Click += (s, e) => SetAllNumbering(true); sourceButtons.Controls.Add(allNumbers);
            var noNumbers = ButtonFor("取消编号"); noNumbers.Click += (s, e) => SetAllNumbering(false); sourceButtons.Controls.Add(noNumbers);
            leftPanel.Controls.Add(sourceButtons, 0, 0); leftPanel.Controls.Add(_list, 0, 1); split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(_preview); root.Controls.Add(split, 0, 1);
            _list.SelectedIndexChanged += (s, e) => _preview.SelectIndex(_list.SelectedIndex);
            _list.ItemCheck += (s, e) => BeginInvoke(new Action(() => { if (e.Index >= 0 && e.Index < _items.Count) _items[e.Index].AddIndexNumber = _list.GetItemChecked(e.Index); RefreshLayout(false); }));
            _preview.SelectionChanged += (s, e) => { if (_list.SelectedIndex != _preview.SelectedIndex) _list.SelectedIndex = _preview.SelectedIndex; };
            _preview.OrderChanged += (s, e) => { _items.Clear(); _items.AddRange(_preview.OrderedItems); SyncList(_preview.SelectedIndex); RefreshSummary(); };

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 7, 12, 7), BackColor = Color.FromArgb(245, 247, 250) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.Controls.Add(_summary, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            var close = ButtonFor("关闭"); close.Click += (s, e) => Close(); actions.Controls.Add(close);
            var insert = ButtonFor("带图框插入"); insert.Click += (s, e) => InsertLayout(); actions.Controls.Add(insert);
            var insertRange = ButtonFor("框选排版范围"); insertRange.Click += (s, e) => InsertWithoutFrame(); actions.Controls.Add(insertRange);
            footer.Controls.Add(actions, 1, 0); root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
        }

        private async void AddDetail()
        {
            Hide();
            DetailLayoutItem item = null;
            Exception failure = null;
            try { await CadCommandContext.ExecuteAsync(() => item = DetailLayoutService.PromptForDetail(_document, _items.Count + 1)); }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) { MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (item == null) return;
            _items.Add(item); SyncList(_items.Count - 1); RefreshLayout(true);
        }

        private async void AddSmallPlan()
        {
            var options = PromptSmallPlanOptions(null);
            if (options == null) return;
            Hide(); DetailLayoutItem item = null; Exception failure = null;
            try
            {
                await CadCommandContext.ExecuteAsync(() => item = CaptureSmallPlan(options));
            }
            catch (Exception exception) { failure = Unwrap(exception); }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null)
            {
                MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            if (item == null) return;
            _items.Add(item); SyncList(_items.Count - 1); RefreshLayout(true);
        }

        private DetailLayoutItem CaptureSmallPlan(SmallPlanOptions options)
        {
            var bridge = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(
                    "WL.Stair.Cad2022.DetailLayoutPlanBridge", false))
                .FirstOrDefault(candidate => candidate != null);
            if (bridge == null)
                throw new InvalidOperationException("楼梯小平面模块尚未加载，请使用最新版启动器重新加载插件。");
            object result;
            try
            {
                result = bridge.GetMethod("Capture").Invoke(null, new object[]
                {
                    _document,
                    new PublishPlanStore().GetActiveProject()?.Name ?? "默认项目",
                    options.Name,
                    options.Scale,
                    options.CaptureMode
                });
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
            if (result == null) return null;
            var type = result.GetType();
            var item = new DetailLayoutItem
            {
                Name = Read<string>(result, type, "Name") ?? options.Name,
                ScaleText = Read<string>(result, type, "ScaleText") ?? ("1:" + options.Scale),
                AddIndexNumber = options.AddIndexNumber,
                IsCachedPlan = true,
                CacheRelativePath = Read<string>(result, type, "CacheRelativePath"),
                CacheLayoutOffsetX = Read<double>(result, type, "CacheLayoutOffsetX"),
                CacheLayoutOffsetY = Read<double>(result, type, "CacheLayoutOffsetY"),
                MinPoint = Point3d.Origin,
                MaxPoint = new Point3d(Read<double>(result, type, "Width"),
                    Read<double>(result, type, "Height"), 0d)
            };
            var lines = type.GetProperty("PreviewLines").GetValue(result, null)
                as System.Collections.IEnumerable;
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    var lineType = line.GetType();
                    item.Preview.Add(new DetailPreviewPrimitive
                    {
                        Kind = DetailPreviewPrimitiveKind.Line,
                        X1 = Read<double>(line, lineType, "X1"),
                        Y1 = Read<double>(line, lineType, "Y1"),
                        X2 = Read<double>(line, lineType, "X2"),
                        Y2 = Read<double>(line, lineType, "Y2")
                    });
                }
            }
            if (string.IsNullOrWhiteSpace(item.CacheRelativePath)
                || item.Width <= 1e-6 || item.Height <= 1e-6)
                throw new InvalidOperationException("小平面缓存没有生成有效的排版范围。");
            return item;
        }

        private void EditSelectedName()
        {
            var index = _list.SelectedIndex;
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            var options = PromptSmallPlanOptions(item);
            if (options == null) return;
            item.Name = options.Name;
            item.ScaleText = "1:" + options.Scale;
            item.AddIndexNumber = options.AddIndexNumber;
            SyncList(index); RefreshLayout(false);
        }

        private SmallPlanOptions PromptSmallPlanOptions(DetailLayoutItem item)
        {
            using (var dialog = new Form
            {
                Text = item == null ? "框选平面" : "编辑名称与编号",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(360, item == null ? 205 : 165),
                Font = Font
            })
            {
                var table = new TableLayoutPanel { Dock = DockStyle.Fill,
                    Padding = new Padding(14), ColumnCount = 2, RowCount = 5 };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var name = new TextBox { Dock = DockStyle.Fill,
                    Text = item?.Name ?? ("小平面" + (_items.Count(x => x.IsCachedPlan) + 1)) };
                var scale = new ComboBox { Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDown };
                scale.Items.AddRange(new object[] { "1:20", "1:25", "1:30", "1:50", "1:100" });
                scale.Text = item?.ScaleText ?? _scale.Text;
                var number = new CheckBox { Text = "在图外增加圆圈编号", AutoSize = true,
                    Checked = item != null && item.AddIndexNumber };
                table.Controls.Add(LabelFor("名称"), 0, 0); table.Controls.Add(name, 1, 0);
                table.Controls.Add(LabelFor("比例"), 0, 1); table.Controls.Add(scale, 1, 1);
                table.Controls.Add(number, 1, 2);
                var captureHint = new Label
                {
                    Text = "在 CAD 中框选任意平面范围；不会修改或删除源图。",
                    AutoSize = true,
                    ForeColor = Color.DimGray
                };
                table.Controls.Add(captureHint, 0, 3);
                table.SetColumnSpan(captureHint, 2);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
                var ok = ButtonFor("确定"); ok.DialogResult = DialogResult.OK;
                var cancel = ButtonFor("取消"); cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
                table.Controls.Add(buttons, 0, 4); table.SetColumnSpan(buttons, 2);
                dialog.Controls.Add(table); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                while (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    if (string.IsNullOrWhiteSpace(name.Text))
                    {
                        MessageBox.Show(dialog, "请输入小平面名称。", Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        dialog.DialogResult = DialogResult.None;
                        continue;
                    }
                    try
                    {
                        var scaleValue = ParseSmallPlanScale(scale.Text);
                        return new SmallPlanOptions
                        {
                            Name = name.Text.Trim(), Scale = scaleValue,
                            AddIndexNumber = number.Checked,
                            CaptureMode = 2
                        };
                    }
                    catch (Exception exception)
                    {
                        MessageBox.Show(dialog, exception.Message, Text,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        dialog.DialogResult = DialogResult.None;
                    }
                }
                return null;
            }
        }

        private static int ParseSmallPlanScale(string value)
        {
            var text = (value ?? string.Empty).Trim();
            var separator = text.LastIndexOf(':');
            if (separator >= 0) text = text.Substring(separator + 1);
            int result;
            if (!int.TryParse(text, out result) || result <= 0)
                throw new InvalidOperationException("小平面比例必须是有效正整数。");
            return result;
        }

        private static T Read<T>(object source, Type type, string propertyName)
        {
            var value = type.GetProperty(propertyName).GetValue(source, null);
            return value == null ? default(T) : (T)Convert.ChangeType(value,
                typeof(T), CultureInfo.InvariantCulture);
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is System.Reflection.TargetInvocationException
                && exception.InnerException != null) exception = exception.InnerException;
            return exception;
        }

        private sealed class SmallPlanOptions
        {
            public string Name;
            public int Scale;
            public bool AddIndexNumber;
            public int CaptureMode;
        }

        private async void InsertLayout()
        {
            FrameDefinition frame;
            int scale;
            DetailLayoutOptions options;
            try
            {
                frame = SelectedFrame(); scale = ParseScale(); options = ReadOptions();
                DetailLayoutService.ComputeLayout(_items, frame, scale, options);
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            SaveSettings(); Hide(); Exception failure = null;
            try { await CadCommandContext.ExecuteAsync(() => DetailLayoutService.Insert(_document, _items, frame, scale, options, _frameAnchor)); }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private async void InsertWithoutFrame()
        {
            int scale;
            DetailLayoutOptions options;
            try
            {
                scale = ParseScale();
                options = ReadOptions();
                if (_items.Count == 0)
                    throw new InvalidOperationException("请先添加大样或框选平面。");
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            SaveSettings(); Hide(); Exception failure = null;
            try
            {
                await CadCommandContext.ExecuteAsync(() =>
                    DetailLayoutService.InsertWithoutFrame(_document, _items,
                        scale, options));
            }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null)
                MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
        }

        private async void InsertFrame()
        {
            FrameDefinition frame; int scale;
            try { frame = SelectedFrame(); scale = ParseScale(); if (frame == null) throw new InvalidOperationException("请选择登记图框。"); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Hide(); DetailLayoutFrameAnchor anchor = null; Exception failure = null;
            try { await CadCommandContext.ExecuteAsync(() => anchor = DetailLayoutService.InsertFrameForRange(_document, frame, scale)); }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) { MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (anchor == null) return;
            _frameAnchor = anchor;
            _hasExplicitRange = false;
            _summary.Text = "图框已插入，请点击“框选排版范围”在图框内指定可用区域。";
        }

        private async void PickLayoutRange()
        {
            FrameDefinition frame; int scale; DetailLayoutOptions current;
            try { frame = SelectedFrame(); scale = ParseScale(); current = ReadOptions(); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Hide(); DetailLayoutOptions selected = null; Exception failure = null;
            try { await CadCommandContext.ExecuteAsync(() => selected = DetailLayoutService.PromptLayoutRange(_document, frame, scale, _frameAnchor, current)); }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) { MessageBox.Show(this, failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (selected == null) return;
            _frameAnchor = null;
            SetOptions(selected); RememberRange(frame, scale); SaveSettings(); RefreshLayout(false);
        }

        private async void RegisterLayoutRange()
        {
            var frame = SelectedFrame(); if (frame == null) { MessageBox.Show(this, "请选择登记图框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var current = ReadOptions();
            Hide(); DetailLayoutOptions selected = null; Exception failure = null;
            try
            {
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
            if (selected != null) { SetOptions(selected); SaveSettings(); RefreshLayout(false); }
        }

        private void RemoveSelected()
        {
            var index = _list.SelectedIndex;
            if (index < 0 || index >= _items.Count) return;
            _items.RemoveAt(index); SyncList(Math.Min(index, _items.Count - 1)); RefreshLayout(true);
        }

        private void SyncList(int selected)
        {
            _list.BeginUpdate(); _list.Items.Clear(); foreach (var item in _items) _list.Items.Add(item, item.AddIndexNumber); _list.EndUpdate();
            if (selected >= 0 && selected < _list.Items.Count) _list.SelectedIndex = selected;
        }

        private void SetAllNumbering(bool enabled)
        {
            for (var i = 0; i < _items.Count; i++) { _items[i].AddIndexNumber = enabled; _list.SetItemChecked(i, enabled); }
            RefreshLayout(false);
        }

        private void RefreshLayout(bool replaceOrder)
        {
            try
            {
                var frame = SelectedFrame(); var scale = ParseScale(); var options = ReadOptions();
                _preview.SetLayout(_items, frame, scale, options, replaceOrder);
                RefreshSummary();
            }
            catch (Exception exception) { _summary.Text = exception.Message; }
        }

        private void RefreshSummary()
        {
            try
            {
                var plan = DetailLayoutService.ComputeLayout(_items, SelectedFrame(), ParseScale(), ReadOptions());
                _summary.Text = _items.Count + " 个大样 · " + plan.PageCount + " 页 " + plan.Frame.PaperDisplay + " · 拖动预览可调整顺序";
            }
            catch (Exception exception) { _summary.Text = exception.Message; }
        }

        private FrameDefinition SelectedFrame() { var choice = _frame.SelectedItem as FrameChoice; return choice == null ? null : choice.Frame; }
        private void ShowMissingRangeReminder()
        {
            var frame = SelectedFrame(); if (frame == null || FrameLayoutRangeService.HasValidRange(frame)) return;
            MessageBox.Show(this, "当前图框尚未登记排版范围。\r\n\r\n带图框插入前请到图框登记中补写排版范围；也可以直接使用窗口底部的“框选排版范围”进行无图框排版。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private int ParseScale()
        {
            var text = (_scale.Text ?? string.Empty).Trim().Replace('：', ':'); var i = text.LastIndexOf(':'); if (i >= 0) text = text.Substring(i + 1);
            int value; if (!int.TryParse(text.Trim(), out value) || value <= 0) throw new InvalidOperationException("请输入有效比例，例如 1:50。"); return value;
        }
        private DetailLayoutOptions ReadOptions()
        {
            var frame = SelectedFrame(); var hasRange = FrameLayoutRangeService.HasValidRange(frame);
            return new DetailLayoutOptions { HasExplicitRange = hasRange, LeftMargin = hasRange ? frame.LayoutLeftMargin : 0d, RightMargin = hasRange ? frame.LayoutRightMargin : 0d, TopMargin = hasRange ? frame.LayoutTopMargin : 0d, BottomMargin = hasRange ? frame.LayoutBottomMargin : 0d, ItemGap = Parse(_gap.Text, 5), PageGap = Parse(_pageGap.Text, 30), DeleteSources = _deleteSources.Checked };
        }
        private void SetOptions(DetailLayoutOptions options)
        {
            _hasExplicitRange = options.HasExplicitRange;
            _left.Text = options.LeftMargin.ToString("0.##", CultureInfo.InvariantCulture); _right.Text = options.RightMargin.ToString("0.##", CultureInfo.InvariantCulture);
            _top.Text = options.TopMargin.ToString("0.##", CultureInfo.InvariantCulture); _bottom.Text = options.BottomMargin.ToString("0.##", CultureInfo.InvariantCulture);
            _gap.Text = options.ItemGap.ToString("0.##", CultureInfo.InvariantCulture); _pageGap.Text = options.PageGap.ToString("0.##", CultureInfo.InvariantCulture);
        }
        private static double Parse(string text, double fallback) { double value; return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 ? value : fallback; }

        private void LoadSettings()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(SettingsPath)) foreach (var line in File.ReadAllLines(SettingsPath)) { var i = line.IndexOf('='); if (i > 0) values[line.Substring(0, i)] = line.Substring(i + 1); } } catch { }
            _left.Text = Read(values, "Left", "40"); _right.Text = Read(values, "Right", "80"); _top.Text = Read(values, "Top", "20"); _bottom.Text = Read(values, "Bottom", "20"); _gap.Text = Read(values, "ItemGap", "5"); _pageGap.Text = Read(values, "PageGap", "30"); _scale.Text = Read(values, "Scale", "1:50"); _hasExplicitRange = Read(values, "HasRange", "0") == "1"; _deleteSources.Checked = Read(values, "DeleteSources", "0") == "1";
            var frameId = Read(values, "FrameId", string.Empty); var block = Read(values, "FrameBlock", string.Empty);
            var index = _frames.FindIndex(x => (!string.IsNullOrWhiteSpace(frameId) && string.Equals(x.RegistrationId, frameId, StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrWhiteSpace(block) && string.Equals(x.BlockName, block, StringComparison.OrdinalIgnoreCase)));
            if (index >= 0) _frame.SelectedIndex = index;
            _rangeFrameId = Read(values, "RangeFrameId", frameId); _rangeFrameBlock = Read(values, "RangeFrameBlock", block);
            int savedScale; _rangeScale = int.TryParse(Read(values, "RangeScale", ParseScaleText(_scale.Text)), out savedScale) ? savedScale : 0;
        }

        private void SaveSettings()
        {
            try
            {
                var frame = SelectedFrame(); Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { "HasRange=" + (_hasExplicitRange ? "1" : "0"), "Left=" + _left.Text, "Right=" + _right.Text, "Top=" + _top.Text, "Bottom=" + _bottom.Text, "ItemGap=" + _gap.Text, "PageGap=" + _pageGap.Text, "DeleteSources=" + (_deleteSources.Checked ? "1" : "0"), "Scale=" + _scale.Text, "FrameId=" + (frame == null ? string.Empty : frame.RegistrationId ?? string.Empty), "FrameBlock=" + (frame == null ? string.Empty : frame.BlockName ?? string.Empty), "RangeFrameId=" + (_rangeFrameId ?? string.Empty), "RangeFrameBlock=" + (_rangeFrameBlock ?? string.Empty), "RangeScale=" + _rangeScale.ToString(CultureInfo.InvariantCulture) });
            }
            catch { }
        }

        private void RememberRange(FrameDefinition frame, int scale)
        {
            _hasExplicitRange = true; _rangeScale = scale;
            _rangeFrameId = frame == null ? null : frame.RegistrationId;
            _rangeFrameBlock = frame == null ? null : frame.BlockName;
        }

        private bool RangeMatchesSelection()
        {
            var frame = SelectedFrame(); if (frame == null || _rangeScale <= 0) return false;
            int scale; try { scale = ParseScale(); } catch { return false; }
            if (scale != _rangeScale) return false;
            return (!string.IsNullOrWhiteSpace(_rangeFrameId) && string.Equals(_rangeFrameId, frame.RegistrationId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(_rangeFrameBlock) && string.Equals(_rangeFrameBlock, frame.BlockName, StringComparison.OrdinalIgnoreCase));
        }

        private static string ParseScaleText(string text)
        {
            text = (text ?? string.Empty).Trim().Replace('：', ':'); var index = text.LastIndexOf(':');
            return index >= 0 ? text.Substring(index + 1).Trim() : text;
        }

        private static string Read(IDictionary<string, string> values, string key, string fallback) { string value; return values.TryGetValue(key, out value) ? value : fallback; }
        private sealed class FrameChoice { public FrameDefinition Frame; public override string ToString() { return Frame == null ? "未登记图框" : Frame.DisplayName; } }
        private static TextBox Box() { return new TextBox { Width = 48 }; }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 7, 2, 0), ForeColor = Color.FromArgb(45, 55, 70) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(4, 2, 0, 0), FlatStyle = FlatStyle.Standard }; }
    }
}
