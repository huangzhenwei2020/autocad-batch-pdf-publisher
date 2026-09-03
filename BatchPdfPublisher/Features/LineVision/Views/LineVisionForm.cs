using Autodesk.AutoCAD.ApplicationServices;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using Microsoft.VisualBasic;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace BatchPdfPublisher.Views
{
    internal sealed class LineVisionForm : DpiAwareForm
    {
        private readonly Document _document;
        private readonly ModelessDocumentBinding _documentBinding;
        private readonly TextBox _path = new TextBox { Width = 330, ReadOnly = true };
        private readonly ComboBox _profile = DropDown(150);
        private readonly ComboBox _view = DropDown(118);
        private readonly TextBox _threshold = Box("0", 55), _minimum = Box("18", 55), _closeGap = Box("2", 55), _collinear = Box("3", 55), _mergeGap = Box("5", 55), _scale = Box("1", 82);
        private readonly CheckBox _diagonals = new CheckBox { Text = "识别45°斜线", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _recognizeText = new CheckBox { Text = "识别文字", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _maskText = new CheckBox { Text = "线稿中遮罩文字", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly CheckBox _insertText = new CheckBox { Text = "生成CAD文字", Checked = true, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
        private readonly ComboBox _ocrLanguage = DropDown(118);
        private readonly TextBox _ocrConfidence = Box("0.70", 55), _maskExpansion = Box("2", 45);
        private readonly Label _range = new Label { Text = "范围：整张图片", AutoSize = true, ForeColor = Color.FromArgb(60, 75, 92), Margin = new Padding(8, 8, 0, 0) };
        private readonly Label _status = new Label { Text = "请选择图片。", AutoSize = true, ForeColor = Color.FromArgb(50, 65, 80), Margin = new Padding(0, 8, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        private readonly LineVisionPreviewControl _preview = new LineVisionPreviewControl();
        private readonly ListView _objects = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true, HideSelection = false };
        private readonly DataGridView _texts = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White };
        private Rectangle? _region;
        private LineVisionResult _result;
        private bool _syncingObjects;
        private CancellationTokenSource _cancellation;
        private readonly Button _analyze = ButtonFor("分析图像");
        private readonly Button _cancelAnalysis = ButtonFor("取消分析");

        private static string SettingsPath { get { return UserDataPaths.SettingsFile("line-vision.ini"); } }

        public LineVisionForm(Document document)
        {
            _document = document ?? throw new ArgumentNullException("document");
            _documentBinding = new ModelessDocumentBinding(this, document);
            Text = "图像转 CAD"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1220, 760); MinimumSize = new Size(940, 610);
            Font = new Font("Microsoft YaHei UI", 9f); BackColor = Color.White;
            Build(); LoadSettings();
            FormClosed += (s, e) => { if (_cancellation != null) _cancellation.Cancel(); SaveSettings(); if (_result != null) _result.Dispose(); };
        }

        private void Build()
        {
            _profile.Items.AddRange(new object[] { "建筑平面图", "普通线稿", "手绘草图" }); _profile.SelectedIndex = 0;
            _profile.SelectedIndexChanged += (s, e) => ApplyProfileDefaults();
            _view.Items.AddRange(new object[] { "彩色识别结果", "原始图像", "黑白预处理" }); _view.SelectedIndex = 0;
            _ocrLanguage.Items.AddRange(new object[] { "简体中文", "英文" }); _ocrLanguage.SelectedIndex = 0;
            _view.SelectedIndexChanged += (s, e) =>
            {
                _preview.PreviewMode = _view.SelectedIndex == 1 ? LineVisionPreviewMode.Original : _view.SelectedIndex == 2 ? LineVisionPreviewMode.Binary : LineVisionPreviewMode.Result;
                _preview.Fit(); _preview.Invalidate();
            };
            _preview.RegionSelected += (s, e) => { _region = e.Region; _range.Text = string.Format(CultureInfo.InvariantCulture, "范围：X {0}，Y {1}，{2} × {3} px", e.Region.X, e.Region.Y, e.Region.Width, e.Region.Height); };
            _preview.CalibrationSelected += Calibrate;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 8, 10, 6), BackColor = Color.FromArgb(245, 247, 250) };
            var fileRow = Row(); fileRow.Controls.Add(LabelFor("图片")); fileRow.Controls.Add(_path);
            var choose = ButtonFor("选择图片"); choose.Click += (s, e) => ChooseImage(); fileRow.Controls.Add(choose);
            fileRow.Controls.Add(LabelFor("模式")); fileRow.Controls.Add(_profile);
            var whole = ButtonFor("整图"); whole.Click += (s, e) => UseWholeImage(); fileRow.Controls.Add(whole);
            var crop = ButtonFor("框选范围"); crop.Click += (s, e) => BeginCrop(); fileRow.Controls.Add(crop); fileRow.Controls.Add(_range); top.Controls.Add(fileRow);

            var parameterRow = Row();
            Add(parameterRow, "阈值(0自动)", _threshold); Add(parameterRow, "最短线", _minimum); Add(parameterRow, "补断线", _closeGap); Add(parameterRow, "共线容差", _collinear); Add(parameterRow, "合并间隙", _mergeGap);
            parameterRow.Controls.Add(_diagonals); parameterRow.Controls.Add(LabelFor("预览")); parameterRow.Controls.Add(_view);
            _analyze.BackColor = Color.FromArgb(32, 113, 196); _analyze.ForeColor = Color.White; _analyze.Click += async (s, e) => await AnalyzeAsync(); parameterRow.Controls.Add(_analyze);
            _cancelAnalysis.Enabled = false; _cancelAnalysis.Click += (s, e) => { if (_cancellation != null) _cancellation.Cancel(); }; parameterRow.Controls.Add(_cancelAnalysis); top.Controls.Add(parameterRow);

            var scaleRow = Row(); Add(scaleRow, "CAD单位/像素", _scale);
            var calibrate = ButtonFor("两点标定"); calibrate.Click += (s, e) => _preview.BeginCalibration(); scaleRow.Controls.Add(calibrate);
            var fit = ButtonFor("适合窗口"); fit.Click += (s, e) => _preview.Fit(); scaleRow.Controls.Add(fit);
            var all = ButtonFor("全选"); all.Click += (s, e) => SetAll(true); scaleRow.Controls.Add(all);
            var none = ButtonFor("全不选"); none.Click += (s, e) => SetAll(false); scaleRow.Controls.Add(none);
            scaleRow.Controls.Add(new Label { Text = "绿色水平线 · 蓝色垂直线 · 黄色斜线；滚轮缩放，右键或中键平移", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 8, 0, 0) }); top.Controls.Add(scaleRow);
            var ocrRow = Row(); ocrRow.Controls.Add(_recognizeText); Add(ocrRow, "语言", _ocrLanguage); Add(ocrRow, "最低置信度", _ocrConfidence); ocrRow.Controls.Add(_maskText); Add(ocrRow, "遮罩扩边(px)", _maskExpansion); ocrRow.Controls.Add(_insertText);
            ocrRow.Controls.Add(new Label { Text = "文字可在右侧“文字”页校正后再插入", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(12, 8, 0, 0) }); top.Controls.Add(ocrRow);
            root.Controls.Add(top, 0, 0);

            _objects.Columns.Add("生成", 48); _objects.Columns.Add("类型", 72); _objects.Columns.Add("长度(px)", 82); _objects.Columns.Add("置信度", 70); _objects.Columns.Add("坐标", 250);
            _objects.ItemChecked += (s, e) =>
            {
                if (_syncingObjects || _result == null || e.Item.Index < 0 || e.Item.Index >= _result.Segments.Count) return;
                _result.Segments[e.Item.Index].IsEnabled = e.Item.Checked; _preview.Invalidate(); UpdateStatus();
            };
            _objects.SelectedIndexChanged += (s, e) => { _preview.SelectedSegmentIndex = _objects.SelectedIndices.Count == 0 ? -1 : _objects.SelectedIndices[0]; _preview.Invalidate(); };
            BuildTextGrid();
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var linePage = new TabPage("线段"); linePage.Controls.Add(_objects); tabs.TabPages.Add(linePage);
            var textPage = new TabPage("文字"); textPage.Controls.Add(_texts); tabs.TabPages.Add(textPage);
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterDistance = 850, BackColor = Color.FromArgb(220, 226, 232) };
            split.Panel1.Controls.Add(_preview); split.Panel2.Controls.Add(tabs); root.Controls.Add(split, 0, 1);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12, 7, 12, 7), BackColor = Color.FromArgb(245, 247, 250) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(_status, 0, 0); footer.Controls.Add(_progress, 1, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            var closeButton = ButtonFor("关闭"); closeButton.Click += (s, e) => Close(); actions.Controls.Add(closeButton);
            var insert = ButtonFor("插入 CAD"); insert.BackColor = Color.FromArgb(32, 113, 196); insert.ForeColor = Color.White; insert.Click += async (s, e) => await InsertAsync(); actions.Controls.Add(insert);
            footer.Controls.Add(actions, 2, 0); root.Controls.Add(footer, 0, 2); Controls.Add(root);
        }

        private void BuildTextGrid()
        {
            _texts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "生成", Width = 48 });
            _texts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "识别文字", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _texts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Confidence", HeaderText = "置信度", Width = 68, ReadOnly = true });
            _texts.Columns.Add(new DataGridViewTextBoxColumn { Name = "Position", HeaderText = "位置", Width = 95, ReadOnly = true });
            _texts.CurrentCellDirtyStateChanged += (s, e) => { if (_texts.IsCurrentCellDirty) _texts.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _texts.DataError += (s, e) => { e.ThrowException = false; };
            _texts.CellValueChanged += TextCellValueChanged;
            _texts.SelectionChanged += (s, e) => { _preview.SelectedTextIndex = _texts.CurrentRow == null ? -1 : _texts.CurrentRow.Index; _preview.Invalidate(); };
        }

        private void ChooseImage()
        {
            if (_cancellation != null) _cancellation.Cancel();
            using (var dialog = new OpenFileDialog { Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*", Title = "选择要转换为 CAD 的图片" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { _path.Text = dialog.FileName; _region = null; _range.Text = "范围：整张图片"; _preview.LoadInput(dialog.FileName); _view.SelectedIndex = 1; _status.Text = "图片已载入，请选择整图或框选范围后分析。"; }
                catch (Exception exception) { MessageBox.Show(this, "无法打开图片：\r\n" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void UseWholeImage()
        {
            _region = null; _range.Text = "范围：整张图片";
            if (File.Exists(_path.Text)) { try { _preview.LoadInput(_path.Text); _view.SelectedIndex = 1; } catch { } }
        }

        private void BeginCrop()
        {
            if (!File.Exists(_path.Text)) { MessageBox.Show(this, "请先选择图片。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { _preview.LoadInput(_path.Text); _view.SelectedIndex = 1; _preview.BeginRegionSelection(); _status.Text = "请在图片上按住左键框选识别范围。"; }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private async Task AnalyzeAsync()
        {
            if (!File.Exists(_path.Text)) { MessageBox.Show(this, "请先选择图片。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            LineVisionSettings settings;
            try { settings = ReadSettings(); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_cancellation != null) _cancellation.Cancel();
            _cancellation = new CancellationTokenSource();
            var cancellation = _cancellation;
            var path = _path.Text; var region = _region;
            _analyze.Enabled = false; _cancelAnalysis.Enabled = true; _progress.Value = 0; _status.Text = "准备分析……";
            try
            {
                var progress = new Progress<Tuple<int, string>>(value => { _progress.Value = Math.Max(0, Math.Min(100, value.Item1)); _status.Text = value.Item2; });
                var recognized = new LineVisionOcrPageResult();
                string ocrWarning = null;
                if (_recognizeText.Checked)
                {
                    try
                    {
                        ((IProgress<Tuple<int, string>>)progress).Report(Tuple.Create(4, "正在识别文字……"));
                        var engine = new LineVisionOcrWorkerClient();
                        recognized = await engine.RecognizeAsync(path, new LineVisionOcrOptions
                        {
                            Language = _ocrLanguage.SelectedIndex == 1 ? "en-US" : "zh-Hans-CN",
                            MinimumConfidence = ParseDouble(_ocrConfidence, "最低置信度", 0d, 1d),
                            SourceRegion = region,
                            MaskExpansionPixels = ParseInt(_maskExpansion, "遮罩扩边", 0, 50)
                        }, cancellation.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { ocrWarning = exception.Message; }
                }
                var textRegions = recognized.TextRegions;
                var mask = _recognizeText.Checked && _maskText.Checked && string.IsNullOrEmpty(ocrWarning);
                var expansion = ParseInt(_maskExpansion, "遮罩扩边", 0, 50);
                var result = await Task.Run(() => LineVisionProcessor.Analyze(path, region, settings, textRegions, mask, expansion, cancellation.Token, (percent, message) => ((IProgress<Tuple<int, string>>)progress).Report(Tuple.Create(percent, message))));
                result.OcrWarning = ocrWarning;
                if (cancellation.IsCancellationRequested || !string.Equals(path, _path.Text, StringComparison.OrdinalIgnoreCase)) { result.Dispose(); return; }
                if (_result != null) _result.Dispose(); _result = result;
                _preview.SetResult(result); _view.SelectedIndex = 0; SyncObjects(); SyncTexts(); UpdateStatus();
            }
            catch (OperationCanceledException) { _status.Text = "分析已取消。"; }
            catch (Exception exception) { _status.Text = "分析失败"; MessageBox.Show(this, "分析图像失败：\r\n" + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                cancellation.Dispose();
                if (!IsDisposed) { _analyze.Enabled = true; _cancelAnalysis.Enabled = false; _progress.Value = 0; }
            }
        }

        private async Task InsertAsync()
        {
            if (_result == null) { MessageBox.Show(this, "请先分析图像。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            double scale;
            if (!double.TryParse(_scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0d) { MessageBox.Show(this, "CAD单位/像素必须是大于0的数字。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Hide(); Exception failure = null; var inserted = new LineVisionInsertResult();
            try { await CadCommandContext.ExecuteAsync(() => inserted = LineVisionCadWriter.PromptAndInsert(_document, _result, scale, _insertText.Checked)); }
            catch (Exception exception) { failure = exception; }
            finally { if (!IsDisposed) { Show(); Activate(); } }
            if (failure != null) MessageBox.Show(this, "插入失败：\r\n" + failure.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (inserted.TotalCount > 0) MessageBox.Show(this, "已插入 " + inserted.LineCount + " 根可编辑直线、" + inserted.TextCount + " 个可编辑文字。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Calibrate(object sender, LineVisionCalibrationEventArgs e)
        {
            var input = Interaction.InputBox("两点图像距离为 " + e.PixelDistance.ToString("0.##", CultureInfo.InvariantCulture) + " px。\r\n请输入这两点在 CAD 中的实际距离：", "两点比例标定", "1000");
            double actual;
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out actual) || actual <= 0d) return;
            _scale.Text = (actual / e.PixelDistance).ToString("0.########", CultureInfo.InvariantCulture);
            _status.Text = "比例已标定：1 px = " + _scale.Text + " CAD单位。";
        }

        private LineVisionSettings ReadSettings()
        {
            return new LineVisionSettings
            {
                Threshold = ParseInt(_threshold, "二值化阈值", 0, 254), MinimumLineLengthPixels = ParseInt(_minimum, "最短线", 3, 10000),
                CloseGapPixels = ParseInt(_closeGap, "补断线", 0, 100), CollinearTolerancePixels = ParseInt(_collinear, "共线容差", 0, 100),
                MergeGapPixels = ParseInt(_mergeGap, "合并间隙", 0, 500), DetectDiagonals = _diagonals.Checked
            };
        }

        private void ApplyProfileDefaults()
        {
            if (_profile.SelectedIndex == 1) { _minimum.Text = "12"; _closeGap.Text = "1"; _collinear.Text = "2"; _mergeGap.Text = "3"; }
            else if (_profile.SelectedIndex == 2) { _minimum.Text = "10"; _closeGap.Text = "4"; _collinear.Text = "5"; _mergeGap.Text = "8"; }
            else { _minimum.Text = "18"; _closeGap.Text = "2"; _collinear.Text = "3"; _mergeGap.Text = "5"; }
        }

        private void SyncObjects()
        {
            _syncingObjects = true; _objects.BeginUpdate(); _objects.Items.Clear();
            if (_result != null) foreach (var line in _result.Segments)
            {
                var item = new ListViewItem(string.Empty) { Checked = line.IsEnabled };
                item.SubItems.Add(DirectionText(line.Direction)); item.SubItems.Add(line.Length.ToString("0.0", CultureInfo.InvariantCulture)); item.SubItems.Add(line.Confidence.ToString("P0", CultureInfo.CurrentCulture));
                item.SubItems.Add(string.Format(CultureInfo.InvariantCulture, "({0:0},{1:0}) → ({2:0},{3:0})", line.X1, line.Y1, line.X2, line.Y2)); _objects.Items.Add(item);
            }
            _objects.EndUpdate(); _syncingObjects = false;
        }

        private void SyncTexts()
        {
            _syncingObjects = true; _texts.Rows.Clear();
            if (_result != null) foreach (var text in _result.TextRegions)
            {
                var bounds = text.Bounds;
                _texts.Rows.Add(text.IsEnabled, text.Text, text.Confidence.ToString("P0", CultureInfo.CurrentCulture), string.Format(CultureInfo.InvariantCulture, "{0:0},{1:0}", bounds.Left, bounds.Top));
            }
            _syncingObjects = false;
        }

        private void TextCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_syncingObjects || _result == null || e.RowIndex < 0 || e.RowIndex >= _result.TextRegions.Count) return;
            var region = _result.TextRegions[e.RowIndex];
            if (e.ColumnIndex == _texts.Columns["Enabled"].Index) region.IsEnabled = Convert.ToBoolean(_texts.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? false);
            else if (e.ColumnIndex == _texts.Columns["Text"].Index) region.Text = Convert.ToString(_texts.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? string.Empty;
            _preview.Invalidate(); UpdateStatus();
        }

        private void SetAll(bool enabled)
        {
            if (_result == null) return; _syncingObjects = true;
            foreach (var line in _result.Segments) line.IsEnabled = enabled;
            foreach (ListViewItem item in _objects.Items) item.Checked = enabled;
            _syncingObjects = false; _preview.Invalidate(); UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_result == null) return;
            var enabled = _result.Segments.Count(x => x.IsEnabled);
            var textEnabled = _result.TextRegions.Count(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Text));
            var circles = _result.Circles.Count(x => x.IsEnabled);
            _status.Text = "线段 " + _result.Segments.Count + " 根（选中 " + enabled + "），圆形 " + _result.Circles.Count + " 个（选中 " + circles + "），文字 " + _result.TextRegions.Count + " 个（选中 " + textEnabled + "）。";
            if (!string.IsNullOrWhiteSpace(_result.OcrWarning)) _status.Text += " OCR 未完成，已按纯线稿处理：" + _result.OcrWarning;
        }

        private void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var parts = line.Split(new[] { '=' }, 2); if (parts.Length != 2) continue;
                    if (parts[0] == "threshold") _threshold.Text = parts[1]; else if (parts[0] == "minimum") _minimum.Text = parts[1]; else if (parts[0] == "closeGap") _closeGap.Text = parts[1];
                    else if (parts[0] == "collinear") _collinear.Text = parts[1]; else if (parts[0] == "mergeGap") _mergeGap.Text = parts[1]; else if (parts[0] == "scale") _scale.Text = parts[1]; else if (parts[0] == "diagonals") _diagonals.Checked = parts[1] == "1";
                    else if (parts[0] == "recognizeText") _recognizeText.Checked = parts[1] == "1"; else if (parts[0] == "maskText") _maskText.Checked = parts[1] == "1"; else if (parts[0] == "insertText") _insertText.Checked = parts[1] == "1";
                    else if (parts[0] == "ocrLanguage") _ocrLanguage.SelectedIndex = parts[1] == "en-US" ? 1 : 0; else if (parts[0] == "ocrConfidence") _ocrConfidence.Text = parts[1]; else if (parts[0] == "maskExpansion") _maskExpansion.Text = parts[1];
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { "threshold=" + _threshold.Text, "minimum=" + _minimum.Text, "closeGap=" + _closeGap.Text, "collinear=" + _collinear.Text, "mergeGap=" + _mergeGap.Text, "scale=" + _scale.Text, "diagonals=" + (_diagonals.Checked ? "1" : "0"), "recognizeText=" + (_recognizeText.Checked ? "1" : "0"), "maskText=" + (_maskText.Checked ? "1" : "0"), "insertText=" + (_insertText.Checked ? "1" : "0"), "ocrLanguage=" + (_ocrLanguage.SelectedIndex == 1 ? "en-US" : "zh-Hans-CN"), "ocrConfidence=" + _ocrConfidence.Text, "maskExpansion=" + _maskExpansion.Text });
            }
            catch { }
        }

        private static string DirectionText(LineVisionDirection direction) { return direction == LineVisionDirection.Horizontal ? "水平" : direction == LineVisionDirection.Vertical ? "垂直" : direction == LineVisionDirection.Diagonal ? "斜线" : "待确认"; }
        private static int ParseInt(TextBox box, string name, int minimum, int maximum) { int value; if (!int.TryParse(box.Text, out value) || value < minimum || value > maximum) throw new InvalidOperationException(name + "必须为 " + minimum + "–" + maximum + " 的整数。"); return value; }
        private static double ParseDouble(TextBox box, string name, double minimum, double maximum) { double value; if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum) throw new InvalidOperationException(name + "必须为 " + minimum.ToString(CultureInfo.InvariantCulture) + "–" + maximum.ToString(CultureInfo.InvariantCulture) + " 的数字。"); return value; }
        private static FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) }; }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(6, 8, 3, 0) }; }
        private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true, Height = 29, Margin = new Padding(5, 2, 0, 2), FlatStyle = FlatStyle.Flat }; }
        private static TextBox Box(string text, int width) { return new TextBox { Text = text, Width = width, Margin = new Padding(0, 4, 4, 0) }; }
        private static ComboBox DropDown(int width) { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 4, 5, 0) }; }
        private static void Add(FlowLayoutPanel row, string label, Control control) { row.Controls.Add(LabelFor(label)); row.Controls.Add(control); }
    }
}
