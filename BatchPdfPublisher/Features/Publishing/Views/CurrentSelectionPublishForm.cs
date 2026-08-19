using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Views
{
    public sealed class CurrentSelectionPublishForm : DpiAwareForm
    {
        private readonly IList<FrameDefinition> _frames;
        private readonly DrawingScanner _scanner = new DrawingScanner();
        private readonly PdfPublisherService _publisher = new PdfPublisherService();
        private readonly PrintRangePreviewService _preview = new PrintRangePreviewService();
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _fileName = new TextBox();
        private readonly TextBox _folder = new TextBox();
        private readonly ComboBox _plotStyle = new ComboBox();
        private readonly ComboBox _marginMode = new ComboBox();
        private readonly CheckBox _overwrite = new CheckBox();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly Button _publishButton;
        private BindingList<SheetItem> _sheets = new BindingList<SheetItem>();
        private bool _publishing;

        public CurrentSelectionPublishForm(IEnumerable<FrameDefinition> frames, string plotStyle, string marginMode)
        {
            _frames = (frames ?? Enumerable.Empty<FrameDefinition>()).ToList();
            Text = "发布当前文件选择";
            Width = 900;
            Height = 620;
            MinimumSize = new System.Drawing.Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            BackColor = System.Drawing.Color.FromArgb(242, 245, 249);

            var document = AcApplication.DocumentManager.MdiActiveDocument;
            var drawingPath = SafeDocumentPath(document);
            var hasSavedDrawing = IsSavedDrawingPath(drawingPath);
            _folder.Text = hasSavedDrawing ? Path.GetDirectoryName(drawingPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var defaultDrawingName = hasSavedDrawing ? Path.GetFileNameWithoutExtension(drawingPath) : Path.GetFileNameWithoutExtension(document?.Name ?? "当前文件");
            _fileName.Text = (string.IsNullOrWhiteSpace(defaultDrawingName) ? "当前文件" : defaultDrawingName) + "_框选图纸.pdf";
            _plotStyle.Text = plotStyle ?? string.Empty;
            _marginMode.Items.AddRange(new object[] { "自动适配", "无白边（满幅）", "保留 3 mm 白边" });
            _marginMode.Text = string.IsNullOrWhiteSpace(marginMode) ? "自动适配" : marginMode;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 4, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            actions.Controls.Add(AccentButton("在 CAD 中框选图框", SelectFrames));
            actions.Controls.Add(Button("上移", () => MoveSheet(-1)));
            actions.Controls.Add(Button("下移", () => MoveSheet(1)));
            actions.Controls.Add(Button("移除", RemoveSelected));
            actions.Controls.Add(Button("预览所选", PreviewSelected));
            actions.Controls.Add(Button("全部预览", PreviewAll));
            root.Controls.Add(actions, 0, 0);

            ConfigureGrid();
            root.Controls.Add(_grid, 0, 1);

            var settings = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 10, 0, 4) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settings.Controls.Add(Label("PDF 文件名"), 0, 0); settings.Controls.Add(_fileName, 1, 0);
            settings.Controls.Add(Label("保存目录"), 2, 0); settings.Controls.Add(_folder, 3, 0);
            _fileName.Dock = DockStyle.Fill; _folder.Dock = DockStyle.Fill;
            settings.Controls.Add(Label("打印样式"), 0, 1); settings.Controls.Add(_plotStyle, 1, 1);
            settings.Controls.Add(Label("白边 / 出血位"), 2, 1); settings.Controls.Add(_marginMode, 3, 1);
            _plotStyle.Dock = DockStyle.Fill; _marginMode.Dock = DockStyle.Fill;
            var folderActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            folderActions.Controls.Add(Button("选择目录", ChooseFolder));
            folderActions.Controls.Add(Button("打开目录", OpenFolder));
            _overwrite.Text = "同名 PDF 直接覆盖"; _overwrite.AutoSize = true; folderActions.Controls.Add(_overwrite);
            settings.Controls.Add(folderActions, 1, 2); settings.SetColumnSpan(folderActions, 3);
            root.Controls.Add(settings, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 6, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _status.Text = "请先点击“在 CAD 中框选图框”。"; _status.AutoEllipsis = true; _status.Dock = DockStyle.Fill;
            _progress.Minimum = 0; _progress.Maximum = 1; _progress.Dock = DockStyle.Fill;
            _publishButton = AccentButton("合并并发布 PDF", PublishSelection);
            footer.Controls.Add(_status, 0, 0); footer.Controls.Add(_progress, 1, 0); footer.Controls.Add(_publishButton, 2, 0);
            root.Controls.Add(footer, 0, 3);
            FormClosed += (s, e) => _preview.Dispose();
        }

        private void ConfigureGrid()
        {
            _grid.Dock = DockStyle.Fill; _grid.AutoGenerateColumns = false; _grid.AllowUserToAddRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
            _grid.BackgroundColor = System.Drawing.Color.White; _grid.BorderStyle = BorderStyle.FixedSingle;
            _grid.Columns.Add(Column("Order", "序", 44));
            _grid.Columns.Add(Column("SheetNumber", "图号", 120));
            _grid.Columns.Add(Column("SheetName", "图名", 220));
            _grid.Columns.Add(Column("FrameDisplay", "图框", 85));
            _grid.Columns.Add(Column("OutputPaperSize", "PDF 尺寸", 130));
            _grid.Columns.Add(Column("PrintScale", "打印比例", 90));
            _grid.Columns.Add(Column("SourceLayout", "空间", 90));
            _grid.DataSource = _sheets;
        }

#if ACAD_R19
        private void SelectFrames()
#else
        private async void SelectFrames()
#endif
        {
            if (_publishing) return;
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) { SetStatus("没有打开的 CAD 文件。"); return; }
            Autodesk.AutoCAD.DatabaseServices.ObjectId[] selectedIds = null;
            Hide();
            try
            {
#if ACAD_R19
                CadCommandContext.Execute(() =>
#else
                await CadCommandContext.ExecuteAsync(() =>
#endif
                {
                    var options = new PromptSelectionOptions
                    {
                        MessageForAdding = "\n请框选要发布的已登记图框，按 Enter 完成：",
                        MessageForRemoval = "\n取消选择图框："
                    };
                    var result = document.Editor.GetSelection(options);
                    if (result.Status == PromptStatus.OK) selectedIds = result.Value.GetObjectIds();
                });
            }
            catch (Exception exception) { SetStatus("框选失败：" + exception.Message); }
            finally { Show(); Activate(); }
            if (selectedIds == null || selectedIds.Length == 0) { SetStatus("没有选择对象。"); return; }

            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in selectedIds)
                try { handles.Add(id.Handle.ToString()); } catch { }
            IList<SheetItem> scanned;
            try { scanned = _scanner.Scan(document, _frames, true, true); }
            catch (Exception exception) { SetStatus("读取框选图框失败：" + exception.Message); return; }
            var picked = scanned.Where(x => handles.Contains(x.BlockHandle)).ToList();
            for (var index = 0; index < picked.Count; index++) picked[index].Order = index + 1;
            _sheets = new BindingList<SheetItem>(picked);
            _grid.DataSource = _sheets;
            SetStatus(picked.Count == 0 ? "所选对象中没有已登记的图框，请先登记图框。" : "已选中 " + picked.Count + " 张图纸，可调整顺序后发布。");
            if (picked.Count > 0) PreviewAll();
        }

#if ACAD_R19
        private void PublishSelection()
#else
        private async void PublishSelection()
#endif
        {
            if (_publishing || _sheets.Count == 0) { if (_sheets.Count == 0) SetStatus("请先框选图框。"); return; }
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) { SetStatus("没有打开的 CAD 文件。"); return; }
            if (CadCompatibilityService.IsTianzhengDrawing(document.Database) && !CadCompatibilityService.IsTianzhengHostLoaded())
            {
                var answer = MessageBox.Show(this,
                    "检测到当前文件是天正图纸，但当前 AutoCAD 进程没有加载天正运行环境。直接发布可能缺少天正专业对象。\r\n\r\n建议改用对应版本天正打开后发布。仍要继续吗？",
                    "天正图纸兼容性提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            var issues = _publisher.ValidateAndNormalizeSheets(_sheets);
            if (issues.Count > 0) { SetStatus("发布前检查失败：" + issues[0].Message); return; }
            var outputPath = BuildOutputPath();
            if (outputPath == null) return;
            _publishing = true; _publishButton.Enabled = false; _progress.Maximum = Math.Max(1, _sheets.Count); _progress.Value = 0;
            Exception publishException = null;
            PdfPublishResult publishResult = null;
            try
            {
                AcApplication.DocumentManager.MdiActiveDocument = document;
#if ACAD_R19
                CadCommandContext.Execute(() =>
#else
                await CadCommandContext.ExecuteAsync(() =>
#endif
                {
                    try
                    {
                        publishResult = _publisher.PublishMerged(document, _sheets.ToList(), outputPath, _plotStyle.Text, _marginMode.Text, _overwrite.Checked, progress =>
                        {
                            _progress.Value = Math.Min(progress.Current, _progress.Maximum);
                            SetStatus("正在发布 " + progress.Current + " / " + progress.Total + " · " + progress.SheetLabel);
                            _progress.Refresh();
                        });
                    }
                    catch (Exception exception) { publishException = exception; }
                });
                if (publishException != null) throw publishException;
                if (publishResult == null || publishResult.Files.Count == 0) throw new InvalidOperationException("AutoCAD 没有返回 PDF 发布结果。");
                SetStatus("发布完成：" + publishResult.SheetCount + " 张已合并到 " + publishResult.Files[0]);
            }
            catch (Exception exception)
            {
                SetStatus("发布失败：" + exception.Message);
                MessageBox.Show(this, _status.Text, "发布当前文件选择", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _publishing = false; _publishButton.Enabled = true; }
        }

        private string BuildOutputPath()
        {
            var folder = (_folder.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder)) { SetStatus("请选择保存目录。"); return null; }
            var name = (_fileName.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var current = AcApplication.DocumentManager.MdiActiveDocument;
                var path = SafeDocumentPath(current);
                var drawingName = IsSavedDrawingPath(path) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(current?.Name ?? "当前文件");
                name = (string.IsNullOrWhiteSpace(drawingName) ? "当前文件" : drawingName) + "_框选图纸.pdf";
            }
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            return Path.Combine(folder, name);
        }

        private void MoveSheet(int direction)
        {
            if (_grid.CurrentRow == null) return;
            var index = _grid.CurrentRow.Index; var target = index + direction;
            if (target < 0 || target >= _sheets.Count) return;
            var item = _sheets[index]; _sheets.RemoveAt(index); _sheets.Insert(target, item); Renumber();
            _grid.ClearSelection(); _grid.Rows[target].Selected = true; _grid.CurrentCell = _grid.Rows[target].Cells[0];
        }

        private void RemoveSelected()
        {
            if (_grid.CurrentRow == null) return;
            _sheets.RemoveAt(_grid.CurrentRow.Index); Renumber();
        }

        private void Renumber() { for (var index = 0; index < _sheets.Count; index++) _sheets[index].Order = index + 1; _grid.Refresh(); }
        private void PreviewSelected() { var sheet = _grid.CurrentRow?.DataBoundItem as SheetItem; if (sheet != null) _preview.Show(AcApplication.DocumentManager.MdiActiveDocument, _sheets, sheet); }
        private void PreviewAll() { if (_sheets.Count > 0) _preview.Show(AcApplication.DocumentManager.MdiActiveDocument, _sheets, _sheets[0]); }
        private void ChooseFolder() { using (var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath; }
        private void OpenFolder() { var folder = _folder.Text; if (string.IsNullOrWhiteSpace(folder)) return; Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        private void SetStatus(string text) { _status.Text = text; _status.Refresh(); }

        private static string SafeDocumentPath(Document document) { try { return document?.Database == null ? string.Empty : (string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Name : document.Database.Filename); } catch { return string.Empty; } }
        private static bool IsSavedDrawingPath(string path) { return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase); }
        private static DataGridViewTextBoxColumn Column(string property, string title, int width) { return new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, Width = width, ReadOnly = true }; }
        private static Label Label(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }; }
        private static Button Button(string text, Action action) { var button = new Button { Text = text, AutoSize = true, MinimumSize = new System.Drawing.Size(0, 28), FlatStyle = FlatStyle.Flat, BackColor = System.Drawing.Color.White }; button.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(190, 201, 216); button.Click += (s, e) => action(); return button; }
        private static Button AccentButton(string text, Action action) { var button = Button(text, action); button.BackColor = System.Drawing.Color.FromArgb(34, 116, 210); button.ForeColor = System.Drawing.Color.White; return button; }
    }
}
