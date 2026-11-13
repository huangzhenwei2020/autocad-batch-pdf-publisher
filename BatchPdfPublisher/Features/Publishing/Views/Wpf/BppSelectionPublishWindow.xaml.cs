using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;

namespace BatchPdfPublisher.Views
{
    public partial class BppSelectionPublishWindow : BppCadWindow
    {
        private readonly PublisherViewModel _ownerModel;
        private readonly ObservableCollection<SheetItem> _sheets = new ObservableCollection<SheetItem>();
        private readonly PrintRangePreviewService _preview = new PrintRangePreviewService();
        public BppSelectionPublishWindow(Document document, PublisherViewModel viewModel)
        {
            _ownerModel = viewModel;
            BindDocument(document);
            InitializeComponent();
            Sheets.ItemsSource = _sheets;
            PlotStyle.ItemsSource = viewModel.AvailablePlotStyles;
            PlotStyle.Text = viewModel.PlotStyle;
            MarginMode.ItemsSource = new[]
            {
                "自动适配",
                "无白边（满幅）",
                "保留 3 mm 白边"
            };
            MarginMode.SelectedItem = viewModel.MarginMode;
            var drawingPath = document.Database.Filename;
            if (string.IsNullOrWhiteSpace(drawingPath))
                drawingPath = document.Name;
            Folder.Text = File.Exists(drawingPath) ? Path.GetDirectoryName(drawingPath) : UserDataPaths.OutputDirectory;
            FileName.Text = Path.GetFileNameWithoutExtension(drawingPath) + "_框选图纸.pdf";
            Status.Text = "请选择需要发布的已登记图框。";
            Closed += (sender, args) => _preview.Dispose();
        }

        private void Select_Click(object sender, RoutedEventArgs e)
        {
            List<SheetItem> picked = null;
            RunCad(() =>
            {
                var result = SourceDocument.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n请选择要发布的已登记图框：" });
                if (result.Status != PromptStatus.OK)
                    return;
                var handles = new HashSet<string>(result.Value.GetObjectIds().Select(id => id.Handle.ToString()), StringComparer.OrdinalIgnoreCase);
                picked = new DrawingScanner().Scan(SourceDocument, _ownerModel.Frames.ToList(), true, true).Where(sheet => handles.Contains(sheet.BlockHandle)).ToList();
            }, () =>
            {
                if (picked == null)
                {
                    Status.Text = "已取消选择。";
                    return;
                }

                _sheets.Clear();
                foreach (var sheet in picked)
                    _sheets.Add(sheet);
                Renumber();
                Status.Text = "已选中 " + _sheets.Count + " 张图纸";
                if (_sheets.Count > 0)
                    _preview.Show(SourceDocument, _sheets, _sheets[0]);
            });
        }

        private void Renumber()
        {
            for (var index = 0; index < _sheets.Count; index++)
                _sheets[index].Order = index + 1;
            Sheets.Items.Refresh();
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (!IsCadBusy && Sheets.SelectedItem is SheetItem selected)
                _preview.Show(SourceDocument, _sheets, selected);
        }

        private void PreviewAll_Click(object sender, RoutedEventArgs e)
        {
            if (!IsCadBusy && _sheets.Count > 0)
                _preview.Show(SourceDocument, _sheets, _sheets[0]);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Folder.Text))
                    return;
                Directory.CreateDirectory(Folder.Text);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder.Text) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }

        private void MoveSelected(int delta)
        {
            if (IsCadBusy)
                return;
            var selected = new HashSet<SheetItem>(Sheets.SelectedItems.Cast<SheetItem>());
            var start = delta < 0 ? 1 : _sheets.Count - 2;
            for (var index = start; index >= 0 && index < _sheets.Count; index -= delta)
            {
                var target = index + delta;
                if (target < 0 || target >= _sheets.Count || !selected.Contains(_sheets[index]) || selected.Contains(_sheets[target]))
                    continue;
                _sheets.Move(index, target);
            }

            Renumber();
            Sheets.SelectedItems.Clear();
            foreach (var sheet in _sheets.Where(selected.Contains))
                Sheets.SelectedItems.Add(sheet);
        }

        private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (IsCadBusy)
                return;
            foreach (var sheet in Sheets.SelectedItems.Cast<SheetItem>().ToList())
                _sheets.Remove(sheet);
            Renumber();
        }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            var folder = BppDialog.ChooseFolder(this, Folder.Text);
            if (folder != null)
                Folder.Text = folder;
        }

        private void Publish_Click(object sender, RoutedEventArgs e)
        {
            if (IsCadBusy || _sheets.Count == 0)
                return;
            try
            {
                var folder = Folder.Text.Trim();
                if (string.IsNullOrWhiteSpace(folder))
                    throw new InvalidOperationException("请选择输出目录。");
                Directory.CreateDirectory(folder);
                var name = FileName.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("请输入文件名。");
                foreach (var invalid in Path.GetInvalidFileNameChars())
                    name = name.Replace(invalid, '_');
                if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    name += ".pdf";
                var publisher = new PdfPublisherService();
                var rows = _sheets.ToList();
                var issues = publisher.ValidateAndNormalizeSheets(rows);
                if (issues.Count > 0)
                    throw new InvalidOperationException(issues[0].Message);
                var output = Path.Combine(folder, name);
                var style = PlotStyle.Text;
                var margin = MarginMode.SelectedItem as string;
                var overwrite = Overwrite.IsChecked == true;
                PdfPublishResult result = null;
                Progress.Maximum = Math.Max(1, rows.Count);
                Progress.Value = 0;
                RunCad(() => result = publisher.PublishMerged(SourceDocument, rows, output, style, margin, overwrite, progress =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Progress.Value = progress.Current;
                        Status.Text = "正在发布 " + progress.Current + " / " + progress.Total;
                    }));
                }), () => Status.Text = result?.Files.Count > 0 ? "已发布 " + result.SheetCount + " 张图纸到 " + result.Files[0] : "已结束发布，请核对输出结果。");
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Title);
            }
        }
    }
}
