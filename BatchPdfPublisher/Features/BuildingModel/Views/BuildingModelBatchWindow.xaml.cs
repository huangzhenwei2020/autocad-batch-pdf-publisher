using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    public enum BuildingFrameMode { Project, BuiltIn, None }

    public sealed class BuildingModelBatchRow : INotifyPropertyChanged
    {
        private bool _isChecked;
        private string _paperText;
        public StudioViewEntry Entry { get; set; }
        public string Name => string.IsNullOrWhiteSpace(Entry.Title) ? Entry.Id : Entry.Title;
        public string KindText => Entry.Kind == ViewKind.Sheet ? "图纸" : Entry.Kind == ViewKind.Plan ? "平面"
            : Entry.Kind == ViewKind.Section ? "剖面" : Entry.Kind == ViewKind.Schedule ? "门窗表"
            : Entry.Kind == ViewKind.OpeningElevation ? "门窗立面" : Entry.Kind == ViewKind.Axonometric ? "轴测" : "立面";
        public string StatusText => Entry.Pending ? "待落图" : Entry.Placed ? "已落图" : "可落图";
        public Brush StatusBrush => Entry.Pending ? Brushes.DeepSkyBlue : Entry.Placed
            ? Brushes.LightGreen : Brushes.LightSlateGray;
        public string PaperText
        {
            get => _paperText;
            set { if (_paperText == value) return; _paperText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PaperText))); }
        }
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class BuildingModelBatchWindow : Window
    {
        private readonly List<BuildingModelBatchRow> _rows;
        private readonly List<FrameDefinition> _frames;
        private ICollectionView _filteredRows;
        private string _filter = "全部";
        private bool _ready;
        private FrameDefinition _selectedFrame;
        private readonly Func<List<StudioViewEntry>> _reloadEntries;

        public IReadOnlyList<StudioViewEntry> SelectedEntries { get; private set; }
        public BuildingFrameMode FrameMode => ProjectFrameRadio.IsChecked == true ? BuildingFrameMode.Project
            : NoFrameRadio.IsChecked == true ? BuildingFrameMode.None : BuildingFrameMode.BuiltIn;
        public FrameDefinition SelectedFrame => FrameMode == BuildingFrameMode.Project ? _selectedFrame : null;

        public BuildingModelBatchWindow(string projectName, IEnumerable<StudioViewEntry> entries,
            IEnumerable<FrameDefinition> frames, Func<List<StudioViewEntry>> reloadEntries = null)
        {
            _reloadEntries=reloadEntries;
            InitializeComponent();
            ProjectText.Text = "当前项目：" + (string.IsNullOrWhiteSpace(projectName) ? "未选择" : projectName);
            _frames = (frames ?? Enumerable.Empty<FrameDefinition>())
                .Where(frame => frame != null && !string.IsNullOrWhiteSpace(frame.BlockName)).ToList();
            _rows = (entries ?? Enumerable.Empty<StudioViewEntry>())
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.FilePath))
                .Select(entry => new BuildingModelBatchRow { Entry = entry }).ToList();
            var firstSheet = _rows.Select(row => row.Entry).FirstOrDefault(entry => entry.Kind == ViewKind.Sheet);
            _selectedFrame = firstSheet == null ? _frames.FirstOrDefault()
                : _frames.FirstOrDefault(frame => FrameMatches(firstSheet, frame)) ?? _frames.FirstOrDefault();
            foreach (var row in _rows) row.PropertyChanged += Row_PropertyChanged;
            ViewsList.ItemsSource = _rows;
            _filteredRows = CollectionViewSource.GetDefaultView(ViewsList.ItemsSource);
            _filteredRows.Filter = FilterRow;
            if (_selectedFrame == null)
            {
                ProjectFrameRadio.IsEnabled = false;
                BuiltInFrameRadio.IsChecked = true;
            }
            _ready = true;
            UpdateFramePanel();
            UpdateFilterButtons();
            UpdateCount();
            Loaded += (sender, args) =>
            {
                var area = SystemParameters.WorkArea;
                Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 32));
                Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 32));
                ResponsiveLayout();
                UpdateWorkspaceHeight();
            };
            SizeChanged += (sender, args) => { ResponsiveLayout(); UpdateWorkspaceHeight(); };
            WorkspaceScroller.ScrollChanged += (sender, args) => UpdateWorkspaceHeight();
            SourceInitialized += (sender, args) => SetDarkTitleBar();
        }

        private bool FilterRow(object item)
        {
            var row = item as BuildingModelBatchRow;
            if (row == null) return false;
            var search = (SearchBox.Text ?? string.Empty).Trim();
            if (search.Length > 0 && row.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0
                && (row.Entry.Id ?? string.Empty).IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0)
                return false;
            return _filter == "全部" || row.KindText == _filter;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
        {
            if (SearchHint != null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            if (_ready) _filteredRows.Refresh();
        }

        private void FilterButton_Click(object sender, RoutedEventArgs args)
        {
            var button = sender as Button;
            if (button == null) return;
            _filter = button.Tag as string ?? "全部";
            _filteredRows.Refresh();
            UpdateFilterButtons();
        }

        private void UpdateFilterButtons()
        {
            foreach (var button in FilterPanel.Children.OfType<Button>())
            {
                var active = string.Equals(button.Tag as string, _filter, StringComparison.Ordinal);
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#087CF4" : "#21384F"));
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#087CF4" : "#41617D"));
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs args)
        {
            foreach (var row in _filteredRows.Cast<BuildingModelBatchRow>()) row.IsChecked = true;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs args)
        {
            foreach (var row in _rows) row.IsChecked = false;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs args)
        {
            if(_reloadEntries==null)return;
            try {
                var entries=_reloadEntries();
                var selected=new HashSet<string>(_rows.Where(r=>r.IsChecked).Select(r=>r.Entry.Id));
                foreach(var row in _rows)row.PropertyChanged-=Row_PropertyChanged;
                _rows.Clear();
                foreach(var entry in entries) {
                    var row=new BuildingModelBatchRow { Entry=entry,IsChecked=selected.Contains(entry.Id) };
                    row.PropertyChanged+=Row_PropertyChanged;_rows.Add(row);
                }
                _filteredRows.Refresh();UpdateFramePanel();UpdateCount();
            } catch(Exception ex) { SelectedCountText.Text="刷新失败："+ex.Message; }
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BuildingModelBatchRow.IsChecked)) UpdateCount();
        }

        private void UpdateCount()
        {
            var count = _rows.Count(row => row.IsChecked);
            SelectedCountText.Text = "已勾选 " + count + " 项 · 仅勾选项会落图";
            StartButton.Content = "开始落图（" + count + " 项）";
            StartButton.IsEnabled = count > 0;
            UpdateFrameWarning();
        }

        private void FrameMode_Changed(object sender, RoutedEventArgs args)
        {
            if (_ready) UpdateFramePanel();
        }

        private void FrameSelectButton_Click(object sender, RoutedEventArgs args)
        {
            if (_frames.Count == 0) return;
            var choices = _frames.Select((frame, index) => new BuildingModelChoice
            {
                Value = index,
                Title = frame.DisplayName,
                Subtitle = (frame.PaperOrientation ?? "方向未登记")
                    + (string.IsNullOrWhiteSpace(frame.Note) ? string.Empty : " · " + frame.Note),
                Badge = frame == _selectedFrame ? "当前" : string.Empty
            });
            var picker = new BuildingModelChoiceWindow("选择项目图框", "只列出当前项目已经登记的图框。",
                choices, false, "使用此图框") { Owner = this };
            if (picker.ShowDialog() != true || !picker.SelectedValue.HasValue) return;
            _selectedFrame = _frames[picker.SelectedValue.Value];
            UpdateFramePanel();
        }

        private void UpdateFramePanel()
        {
            var project = FrameMode == BuildingFrameMode.Project;
            FrameSelectButton.IsEnabled = project && _selectedFrame != null;
            FrameNameText.Text = project ? (_selectedFrame?.DisplayName ?? "请先登记项目图框")
                : FrameMode == BuildingFrameMode.BuiltIn ? "使用视图自带图框" : "仅落视图内容";
            FramePaperText.Text = project ? (_selectedFrame?.PaperDisplay ?? "—")
                + " " + (_selectedFrame?.PaperOrientation ?? "") : FrameMode == BuildingFrameMode.BuiltIn ? "图纸原规格" : "无图框";
            FramePreview.Visibility = ActualWidth < 900 || FrameMode == BuildingFrameMode.None
                ? Visibility.Collapsed : Visibility.Visible;
            foreach (var row in _rows)
            {
                if (row.Entry.Kind == ViewKind.Sheet)
                    row.PaperText = (row.Entry.PaperName ?? "—") + (row.Entry.PaperWidth >= row.Entry.PaperHeight ? " 横向" : " 纵向");
                else if (project && _selectedFrame != null)
                    row.PaperText = _selectedFrame.PaperDisplay + " " + (_selectedFrame.PaperOrientation ?? "");
                else row.PaperText = FrameMode == BuildingFrameMode.None ? "无图框" : "A3 横向";
            }
            UpdateFrameWarning();
        }

        private void UpdateFrameWarning()
        {
            if (!_ready) return;
            var mismatch = _rows.FirstOrDefault(row => row.IsChecked && !FrameMatches(row.Entry));
            FrameHintText.Text = mismatch == null ? "规格不匹配时提示，不自动套错图框"
                : "⚠ “" + mismatch.Name + "”与所选图框规格不匹配，请换图框或取消勾选。";
            FrameHintText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                mismatch == null ? "#94B6D1" : "#FFBD74"));
        }

        private bool FrameMatches(StudioViewEntry entry)
        {
            return FrameMode != BuildingFrameMode.Project || _selectedFrame == null
                || FrameMatches(entry, _selectedFrame);
        }

        private static bool FrameMatches(StudioViewEntry entry, FrameDefinition frame)
        {
            if (entry.Kind != ViewKind.Sheet) return true;
            if (!string.IsNullOrWhiteSpace(entry.PaperName) &&
                !string.Equals(entry.PaperName.Trim(), (frame.PaperSize ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (entry.PaperWidth <= 0d || entry.PaperHeight <= 0d) return true;
            var orientation = string.IsNullOrWhiteSpace(frame.PaperOrientation)
                ? PaperSizeCatalog.DefaultOrientation(frame.PaperSize) : frame.PaperOrientation;
            var size = PaperSizeCatalog.GetSize(frame.PaperSize, frame.Extension, orientation);
            return Math.Abs(entry.PaperWidth - size[0]) < 2d && Math.Abs(entry.PaperHeight - size[1]) < 2d;
        }

        private void StartButton_Click(object sender, RoutedEventArgs args)
        {
            var selected = _rows.Where(row => row.IsChecked).ToList();
            if (selected.Count == 0) return;
            var mismatch = selected.FirstOrDefault(row => !FrameMatches(row.Entry));
            if (mismatch != null)
            {
                MessageBox.Show(this, "“" + mismatch.Name + "”的纸张规格与所选项目图框不一致。请更换图框，或取消勾选后重试。",
                    "图框规格不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SelectedEntries = selected.Select(row => row.Entry).ToList();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs args) { DialogResult = false; }

        private void ResponsiveLayout()
        {
            if (!_ready) return;
            var compact = ActualWidth > 0d && ActualWidth < 900d;
            FrameColumn.Width = new GridLength(compact ? 220 : 330);
            WorkspaceGap.Width = new GridLength(compact ? 8 : 12);
            FilterPanel.Visibility = ActualWidth < 1180d ? Visibility.Collapsed : Visibility.Visible;
            FramePreview.Visibility = compact || FrameMode == BuildingFrameMode.None
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateWorkspaceHeight()
        {
            var viewport = WorkspaceScroller.ViewportHeight;
            if (viewport <= 0d) return;
            var height = Math.Max(420d, viewport - 16d);
            if (double.IsNaN(WorkspaceGrid.Height) || Math.Abs(WorkspaceGrid.Height - height) > 0.5d)
                WorkspaceGrid.Height = height;
        }

        private void SetDarkTitleBar()
        {
            var enabled = 1;
            var handle = new WindowInteropHelper(this).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute,
            ref int value, int valueSize);
    }
}
