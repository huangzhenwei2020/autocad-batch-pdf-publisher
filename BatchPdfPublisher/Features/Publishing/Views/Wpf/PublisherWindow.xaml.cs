using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using BatchPdfPublisher.ViewModels;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Views
{
    public partial class PublisherWindow : Window
    {
        private readonly PublisherViewModel _viewModel;
        private readonly ObservableCollection<SheetItem> _rows = new ObservableCollection<SheetItem>();
        private readonly Dictionary<SheetItem, KeyValuePair<string, string>> _previewPaths = new Dictionary<SheetItem, KeyValuePair<string, string>>();
        private bool _refreshing;
        private bool _refreshPending;
        private bool _closed;
        private bool _selectionFromGrid;
        private bool _quickUpdating;
        private bool _generatingPreview;
        private int _previewVersion;
        private Point _dragStart;
        private SheetItem _dragSheet;
        private string _viewedFile;
        private bool _changingNavigation;
        private bool _fitPreview = true;
        private double _previewScale = 1;
        private string _displayedPreviewKey;
        public PublisherWindow() : this(new PublisherViewModel())
        {
        }

        internal PublisherWindow(PublisherViewModel viewModel)
        {
            _viewModel = viewModel;
            InitializeComponent();
            BppDialog.ConfigureWindow(this);
            DataContext = _viewModel;
            VersionLabel.Text = WanluoArchitectureTools.ProductVersion.Display;
            Title += " " + WanluoArchitectureTools.ProductVersion.Display;
            SortBox.ItemsSource = new[]
            {
                "自定义顺序",
                "图号升序",
                "图号降序",
                "图名升序",
                "图名降序",
                "按 CAD 文件",
                "布局在前，模型在后",
                "序号／页码升序",
                "序号／页码降序"
            };
            OrientationColumn.ItemsSource = new[]
            {
                "横向",
                "纵向"
            };
            QuickOrientation.ItemsSource = new[]
            {
                "横向",
                "纵向"
            };
            QuickScale.ItemsSource = new[]
            {
                "1:1",
                "1:2",
                "1:5",
                "1:10",
                "1:20",
                "1:25",
                "1:50",
                "1:75",
                "1:100",
                "1:150",
                "1:200",
                "1:500"
            };
            EnableListReordering(CadFilesList, "WanLuo.CadFileOrder", (source, target, after) => _viewModel.MoveCadFile(((CadFileItem)source).Path, ((CadFileItem)target).Path, after));
            EnableListReordering(BuildingsList, "WanLuo.BuildingOrder", (source, target, after) => _viewModel.MoveBuilding((string)source, (string)target, after));
            _viewModel.PropertyChanged += ViewModelChanged;
            Loaded += (sender, args) =>
            {
                SheetsGrid.ItemsSource = _rows;
                MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
                MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
                Width = Math.Min(Width, SystemParameters.WorkArea.Width);
                Height = Math.Min(Height, SystemParameters.WorkArea.Height);
                LoadLayout();
                RefreshRows();
            };
            Closing += WindowClosing;
            Closed += (sender, args) =>
            {
                _closed = true;
                _previewVersion++;
                ClearPreviewCache();
                _viewModel.PropertyChanged -= ViewModelChanged;
                SaveLayout();
                _viewModel.SaveProjectCommand.Execute(null);
                _viewModel.Dispose();
            };
        }

        internal PublisherViewModel ViewModel => _viewModel;
        private bool Busy => _viewModel.IsPublishing || _viewModel.IsScanning || _generatingPreview;

        private void UpdateBusyUi()
        {
            ProjectActions.IsEnabled = !Busy;
            WorkArea.IsEnabled = !Busy;
            PublishButton.IsEnabled = !Busy;
            OutputSettingsButton.IsEnabled = !Busy;
            FooterOptions.IsEnabled = !Busy;
            Progress.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closed)
                return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ViewModelChanged(sender, e)));
                return;
            }

            if (e.PropertyName == nameof(PublisherViewModel.SelectedSheet))
            {
                if (_viewModel.SelectedSheet != null)
                    SelectNavigationFile(_viewModel.SelectedSheet.SourceFile);
                if (_viewedFile != null && _viewModel.SelectedSheet != null && !SameFile(_viewedFile, _viewModel.SelectedSheet.SourceFile))
                {
                    _viewedFile = null;
                    QueueRefresh();
                }

                if (!_selectionFromGrid && !_refreshing && _rows.Contains(_viewModel.SelectedSheet))
                {
                    _refreshing = true;
                    try
                    {
                        SheetsGrid.SelectedItems.Clear();
                        SheetsGrid.SelectedItem = _viewModel.SelectedSheet;
                        SheetsGrid.ScrollIntoView(_viewModel.SelectedSheet);
                    }
                    finally
                    {
                        _refreshing = false;
                    }
                }

                _previewVersion++;
                PreviewImage.Source = null;
                RefreshReview();
            }

            if (e.PropertyName == nameof(PublisherViewModel.SelectedProject))
            {
                _viewedFile = null;
                ClearPreviewCache();
            }

            if (e.PropertyName == nameof(PublisherViewModel.PlotStyle) || e.PropertyName == nameof(PublisherViewModel.MarginMode))
                RefreshReview();
            if (e.PropertyName == nameof(PublisherViewModel.Sheets) || e.PropertyName == nameof(PublisherViewModel.SelectedBuilding) || e.PropertyName == nameof(PublisherViewModel.SelectedProject))
                QueueRefresh();
            if (e.PropertyName == nameof(PublisherViewModel.IsPublishing) || e.PropertyName == nameof(PublisherViewModel.PublishProgressValue) || e.PropertyName == nameof(PublisherViewModel.PublishProgressMaximum) || e.PropertyName == nameof(PublisherViewModel.IsScanning) || e.PropertyName == nameof(PublisherViewModel.ScanProgressValue) || e.PropertyName == nameof(PublisherViewModel.ScanProgressMaximum))
            {
                UpdateBusyUi();
                Progress.Maximum = Math.Max(1, _viewModel.IsScanning ? _viewModel.ScanProgressMaximum : _viewModel.PublishProgressMaximum);
                Progress.Value = _viewModel.IsScanning ? _viewModel.ScanProgressValue : _viewModel.PublishProgressValue;
            }

            if (e.PropertyName == nameof(PublisherViewModel.OutputDirectory) || e.PropertyName == nameof(PublisherViewModel.OutputNextToCadFile))
                RefreshOutput();
        }

        private void QueueRefresh()
        {
            if (_refreshPending || _closed)
                return;
            _refreshPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _refreshPending = false;
                if (!_closed)
                    RefreshRows();
            }));
        }

        private void RefreshRows()
        {
            if (_refreshing || _closed)
                return;
            var selected = new HashSet<SheetItem>(SelectedSheets());
            var query = (SearchBox.Text ?? string.Empty).Trim();
            var visible = _viewModel.SheetView.Cast<SheetItem>().Where(sheet => _viewedFile == null || SameFile(sheet.SourceFile, _viewedFile)).Where(sheet => query.Length == 0 || new[] { sheet.Building, sheet.PageNumber, sheet.SheetNumber, sheet.SheetName, sheet.FrameDisplay, sheet.SourceFileName, sheet.SourceLayout }.Any(value => value != null && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
            _refreshing = true;
            try
            {
                BuildingColumn.ItemsSource = _viewModel.Buildings.Concat(new[] { "未分组" }).Distinct().ToList();
                PlotStyleColumn.ItemsSource = new[]
                {
                    "使用输出设置"
                }.Concat(_viewModel.AvailablePlotStyles).Concat(_viewModel.Sheets.Select(sheet => sheet.PlotStyle).Where(style => !string.IsNullOrWhiteSpace(style))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!_rows.SequenceEqual(visible))
                {
                    _rows.Clear();
                    foreach (var sheet in visible)
                        _rows.Add(sheet);
                }
                else if (!SheetsGrid.IsKeyboardFocusWithin)
                    SheetsGrid.Items.Refresh();
                SheetsGrid.SelectedItems.Clear();
                foreach (var sheet in visible.Where(selected.Contains))
                    SheetsGrid.SelectedItems.Add(sheet);
                if (SheetsGrid.SelectedItems.Count == 0 && visible.Contains(_viewModel.SelectedSheet))
                    SheetsGrid.SelectedItem = _viewModel.SelectedSheet;
                SortBox.SelectedIndex = (int)_viewModel.CurrentSheetSort;
                UpdateNavigationGroups();
                if (!visible.Contains(_viewModel.SelectedSheet))
                    _viewModel.SelectedSheet = visible.FirstOrDefault();
                if (SheetsGrid.SelectedItems.Count == 0 && visible.Contains(_viewModel.SelectedSheet))
                    SheetsGrid.SelectedItem = _viewModel.SelectedSheet;
            }
            finally
            {
                _refreshing = false;
            }

            RefreshCount();
            RefreshReview();
            RefreshOutput();
        }

        private List<SheetItem> SelectedSheets() => _rows.Where(sheet => SheetsGrid.SelectedItems.Contains(sheet)).ToList();
        private void RefreshCount()
        {
            CountLabel.Text = "已选 " + SheetsGrid.SelectedItems.Count + " 张";
            ViewTitle.Text = (_viewModel.SelectedBuilding ?? (_viewedFile == null ? "图纸列表" : Path.GetFileNameWithoutExtension(_viewedFile))) + " · " + _rows.Count + " 张";
        }

        private void Sheets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing)
                return;
            _selectionFromGrid = true;
            try
            {
                var added = e.AddedItems.OfType<SheetItem>().LastOrDefault();
                var current = SheetsGrid.CurrentItem as SheetItem;
                _viewModel.SelectedSheet = added ?? (current != null && SheetsGrid.SelectedItems.Contains(current) ? current : SheetsGrid.SelectedItem as SheetItem);
            }
            finally
            {
                _selectionFromGrid = false;
            }

            RefreshCount();
        }

        private void RefreshReview()
        {
            if (_closed)
                return;
            var sheet = _viewModel.SelectedSheet;
            PreviewTitle.Text = sheet == null ? "请选择图纸" : sheet.SheetNumber + "  " + sheet.SheetName;
            PreviewFacts.Text = sheet == null ? string.Empty : sheet.FrameDisplay + " · " + sheet.OutputPaperSize + " · " + sheet.PaperOrientation;
            PreviewPosition.Text = sheet == null ? "0 / 0" : (_rows.IndexOf(sheet) + 1) + " / " + _rows.Count;
            PreviewNotice.Text = sheet == null ? string.Empty : "点击“更新预览”查看实际打印效果";
            _quickUpdating = true;
            try
            {
                QuickOrientation.SelectedItem = sheet?.PaperOrientation;
                QuickScale.Text = sheet?.PrintScale ?? string.Empty;
            }
            finally
            {
                _quickUpdating = false;
            }

            var key = sheet == null ? null : PreviewCacheKey(sheet);
            if (PreviewImage.Source != null && _displayedPreviewKey == key)
                PreviewNotice.Text = "打印预览已更新";
            else
            {
                PreviewImage.Source = null;
                if (sheet != null && _previewPaths.TryGetValue(sheet, out var preview) && preview.Key == key)
                    _ = ShowPreviewAsync(sheet, preview.Value);
            }
        }

        private bool CommitGrid()
        {
            return SheetsGrid.CommitEdit(DataGridEditingUnit.Cell, true) && SheetsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private static string SourceKey(string path)
        {
            try
            {
                return Path.GetFullPath(path ?? string.Empty);
            }
            catch (ArgumentException)
            {
                return path ?? string.Empty;
            }
            catch (NotSupportedException)
            {
                return path ?? string.Empty;
            }
        }

        private static bool SameFile(string left, string right) => string.Equals(SourceKey(left), SourceKey(right), StringComparison.OrdinalIgnoreCase);
        private void UpdateNavigationGroups()
        {
            var files = _viewModel.Sheets.GroupBy(sheet => SourceKey(sheet.SourceFile), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.GroupBy(sheet => sheet.Building ?? "未分组").Select(building => new CadFileGroupItem(group.Key, building.Key, building.Count())).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var file in _viewModel.CadFiles)
            {
                var groups = files.TryGetValue(SourceKey(file.Path), out var found) ? found : new List<CadFileGroupItem>();
                if (file.Groups.Select(group => group.DisplayName).SequenceEqual(groups.Select(group => group.DisplayName)))
                    continue;
                file.Groups.Clear();
                foreach (var group in groups)
                    file.Groups.Add(group);
            }
        }

        private void CadFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || _changingNavigation || NavigationTabs.SelectedIndex != 0)
                return;
            _viewedFile = (CadFilesList.SelectedItem as CadFileItem)?.Path;
            _viewModel.SelectedBuilding = null;
            QueueRefresh();
        }

        private void FileGroup_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || !CommitGrid() || !(((FrameworkElement)sender).Tag is CadFileGroupItem group))
                return;
            SelectNavigationFile(group.SourceFile);
            _viewedFile = group.SourceFile;
            _viewModel.SelectedBuilding = group.Building;
            QueueRefresh();
        }

        private void SelectNavigationFile(string path)
        {
            if (NavigationTabs.SelectedIndex != 0)
                return;
            _changingNavigation = true;
            try
            {
                CadFilesList.SelectedItem = _viewModel.CadFiles.FirstOrDefault(file => SameFile(file.Path, path));
            }
            finally
            {
                _changingNavigation = false;
            }
        }

        private void Building_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || _changingNavigation || NavigationTabs.SelectedIndex != 1)
                return;
            _viewedFile = null;
            QueueRefresh();
        }

        private void NavigationTab_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, NavigationTabs) || CadFilesList == null || BuildingsList == null || _refreshing)
                return;
            _changingNavigation = true;
            try
            {
                _viewedFile = NavigationTabs.SelectedIndex == 0 ? (CadFilesList.SelectedItem as CadFileItem)?.Path : null;
                _viewModel.SelectedBuilding = null;
                BuildingsList.SelectedItem = null;
            }
            finally
            {
                _changingNavigation = false;
            }

            QueueRefresh();
        }

        private void AllGroups_Click(object sender, RoutedEventArgs e)
        {
            _viewedFile = null;
            _viewModel.SelectedBuilding = null;
            QueueRefresh();
        }

        private void OpenMenu_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            button.ContextMenu.DataContext = _viewModel;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }

        private void Columns_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitGrid())
                return;
            var menu = new ContextMenu
            {
                PlacementTarget = (Button)sender
            };
            foreach (var column in SheetsGrid.Columns.OrderBy(column => column.DisplayIndex))
            {
                var item = new MenuItem
                {
                    Header = column.Header,
                    IsCheckable = true,
                    IsChecked = column.Visibility == Visibility.Visible,
                    StaysOpenOnClick = true
                };
                item.Click += (source, args) =>
                {
                    column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    SaveLayout();
                };
                menu.Items.Add(item);
            }

            ((Button)sender).ContextMenu = menu;
            menu.IsOpen = true;
        }

        private void OutputSettings_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || !CommitGrid())
                return;
            BppDialog.Show(new BppOutputSettingsWindow(_viewModel), this);
            RefreshOutput();
        }

        private void UpdatePreviewScale()
        {
            if (PreviewImage?.Source == null || PreviewViewport == null)
                return;
            var width = PreviewImage.Source.Width;
            var height = PreviewImage.Source.Height;
            if (width <= 0 || height <= 0)
                return;
            if (_fitPreview)
                _previewScale = Math.Max(0.01, Math.Min(Math.Max(1, PreviewViewport.ActualWidth - 18) / width, Math.Max(1, PreviewViewport.ActualHeight - 18) / height));
            PreviewImage.Width = width * _previewScale;
            PreviewImage.Height = height * _previewScale;
        }

        private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fitPreview)
                UpdatePreviewScale();
        }

        private void FitPreview_Click(object sender, RoutedEventArgs e)
        {
            _fitPreview = true;
            UpdatePreviewScale();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomPreview(1.25);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomPreview(0.8);
        private void ZoomPreview(double factor)
        {
            _fitPreview = false;
            _previewScale = Math.Max(0.01, Math.Min(8, _previewScale * factor));
            UpdatePreviewScale();
        }

        private void PreviousPreview_Click(object sender, RoutedEventArgs e) => SelectPreview(-1);
        private void NextPreview_Click(object sender, RoutedEventArgs e) => SelectPreview(1);
        private async void SelectPreview(int offset)
        {
            if (Busy || !CommitGrid())
                return;
            var index = _rows.IndexOf(_viewModel.SelectedSheet) + offset;
            if (index < 0 || index >= _rows.Count)
                return;
            _viewModel.SelectedSheet = _rows[index];
            var sheet = _viewModel.SelectedSheet;
            if (!_previewPaths.TryGetValue(sheet, out var preview) || preview.Key != PreviewCacheKey(sheet))
                await GeneratePreview();
        }

        private void Sheets_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || _refreshing)
                return;
            var project = _viewModel.SelectedProject;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closed || !ReferenceEquals(project, _viewModel.SelectedProject) || !CommitGrid())
                    return;
                _viewModel.ApplySheetEdits();
                RefreshRows();
            }));
        }

        private void MoveSelection(Action<List<SheetItem>> move)
        {
            if (Busy || !CommitGrid())
                return;
            var selected = SelectedSheets();
            if (selected.Count == 0)
                return;
            _refreshing = true;
            try
            {
                move(selected);
            }
            finally
            {
                _refreshing = false;
            }

            RefreshRows();
            if (SheetsGrid.SelectedItem != null)
                SheetsGrid.ScrollIntoView(SheetsGrid.SelectedItem);
        }

        private void Up_Click(object sender, RoutedEventArgs e) => MoveSelection(selected => _viewModel.MoveSheetsByOffset(selected, -1, _rows.ToList()));
        private void Down_Click(object sender, RoutedEventArgs e) => MoveSelection(selected => _viewModel.MoveSheetsByOffset(selected, 1, _rows.ToList()));
        private void Top_Click(object sender, RoutedEventArgs e) => MoveSelection(selected => _viewModel.MoveSheetsToEdge(selected, false));
        private void Bottom_Click(object sender, RoutedEventArgs e) => MoveSelection(selected => _viewModel.MoveSheetsToEdge(selected, true));
        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (SheetsGrid != null)
                QueueRefresh();
        }

        private void Sort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing || SortBox.SelectedIndex < 0 || Busy || !CommitGrid())
                return;
            _viewModel.SortVisibleSheets((SheetSortMode)SortBox.SelectedIndex);
            RefreshRows();
        }

        private void Sheets_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var modes = new Dictionary<string, SheetSortMode>
            {
                {
                    "PageNumber",
                    SheetSortMode.PageNumberAscending
                },
                {
                    "SheetNumber",
                    SheetSortMode.SheetNumberAscending
                },
                {
                    "SheetName",
                    SheetSortMode.SheetNameAscending
                },
                {
                    "SourceFileName",
                    SheetSortMode.SourceFileAscending
                }
            };
            if (modes.TryGetValue(e.Column.SortMemberPath, out var mode))
            {
                _viewModel.SortVisibleSheets(mode);
                RefreshRows();
            }
        }

        private void Sheets_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragSheet = null;
            if (Busy || Keyboard.Modifiers != ModifierKeys.None || FindAncestor<TextBox>(e.OriginalSource as DependencyObject) != null || FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) != null)
                return;
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
                return;
            _dragSheet = row.Item as SheetItem;
            _dragStart = e.GetPosition(SheetsGrid);
            if (row.IsSelected && SheetsGrid.SelectedItems.Count > 1)
            {
                e.Handled = true;
                SheetsGrid.Focus();
            }
        }

        private void Sheets_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragSheet == null || e.LeftButton != MouseButtonState.Pressed || Busy)
                return;
            var point = e.GetPosition(SheetsGrid);
            if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            var selected = SelectedSheets().ToArray();
            if (!selected.Contains(_dragSheet))
                return;
            _dragSheet = null;
            DragDrop.DoDragDrop(SheetsGrid, new DataObject(typeof(SheetItem[]), selected), DragDropEffects.Move);
        }

        private void Sheets_DragOver(object sender, DragEventArgs e)
        {
            var sources = e.Data.GetData(typeof(SheetItem[])) as SheetItem[];
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            e.Effects = !Busy && sources?.Length > 0 && (row == null || ((SheetItem)row.Item).Building == sources[0].Building) ? DragDropEffects.Move : DragDropEffects.None;
            var viewer = FindDescendant<ScrollViewer>(SheetsGrid);
            var point = e.GetPosition(SheetsGrid);
            if (point.Y < 40)
                viewer?.LineUp();
            else if (point.Y > SheetsGrid.ActualHeight - 25)
                viewer?.LineDown();
            e.Handled = true;
        }

        private void Sheets_Drop(object sender, DragEventArgs e)
        {
            var sources = e.Data.GetData(typeof(SheetItem[])) as SheetItem[];
            if (sources == null || Busy)
                return;
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            var target = row?.Item as SheetItem;
            var after = row == null || e.GetPosition(row).Y > row.ActualHeight / 2;
            MoveSelection(selected => _viewModel.MoveSheets(sources, target, after));
            e.Handled = true;
        }

        private void EnableListReordering(ListBox list, string format, Action<object, object, bool> move)
        {
            Point start = new Point();
            object candidate = null;
            list.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                candidate = null;
                if (Busy || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
                    return;
                var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                if (row != null)
                {
                    candidate = row.DataContext;
                    start = e.GetPosition(list);
                }
            };
            list.PreviewMouseMove += (sender, e) =>
            {
                if (candidate == null || e.LeftButton != MouseButtonState.Pressed || Busy)
                    return;
                var point = e.GetPosition(list);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                var data = new DataObject(format, candidate);
                candidate = null;
                DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
            };
            list.DragOver += (sender, e) =>
            {
                e.Effects = !Busy && e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            list.Drop += (sender, e) =>
            {
                if (Busy || !e.Data.GetDataPresent(format) || list.Items.Count == 0)
                    return;
                var row = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                var target = row?.DataContext ?? list.Items[list.Items.Count - 1];
                move(e.Data.GetData(format), target, row == null || e.GetPosition(row).Y >= row.ActualHeight / 2);
                RefreshRows();
                e.Handled = true;
            };
        }

        private static T FindAncestor<T>(DependencyObject item)
            where T : DependencyObject
        {
            while (item != null)
            {
                if (item is T match)
                    return match;
                item = item is ContentElement content ? ContentOperations.GetParent(content) : VisualTreeHelper.GetParent(item);
            }

            return null;
        }

        private static T FindDescendant<T>(DependencyObject item)
            where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
            {
                var child = VisualTreeHelper.GetChild(item, index);
                if (child is T match)
                    return match;
                var nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private async void Preview_Click(object sender, RoutedEventArgs e) => await GeneratePreview();
        private string PreviewCacheKey(SheetItem sheet)
        {
            return string.Join("|", new[] { sheet.SourceFile, sheet.SourceLayout, sheet.BlockHandle, sheet.MinX.ToString("R"), sheet.MinY.ToString("R"), sheet.MaxX.ToString("R"), sheet.MaxY.ToString("R"), sheet.FrameDisplay, sheet.PaperOrientation, sheet.PrintScale, sheet.PlotStyle, _viewModel.PlotStyle, _viewModel.MarginMode });
        }

        private void CachePreview(SheetItem sheet, string path)
        {
            if (_previewPaths.TryGetValue(sheet, out var previous) && previous.Value != path)
                DeletePreview(previous.Value);
            _previewPaths[sheet] = new KeyValuePair<string, string>(PreviewCacheKey(sheet), path);
        }

        private static void DeletePreview(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void ClearPreviewCache()
        {
            foreach (var preview in _previewPaths.Values)
                DeletePreview(preview.Value);
            _previewPaths.Clear();
            _displayedPreviewKey = null;
            PreviewImage.Source = null;
        }

        private static BitmapImage RenderPreview(string path)
        {
            using (var bitmap = PdfPreviewRenderer.RenderFirstPage(path, 1800, 1800))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        private async Task<bool> ShowPreviewAsync(SheetItem sheet, string path)
        {
            var version = ++_previewVersion;
            var key = PreviewCacheKey(sheet);
            try
            {
                var pixels = await Task.Run(() => RenderPreview(path));
                if (_closed || version != _previewVersion || !ReferenceEquals(sheet, _viewModel.SelectedSheet) || key != PreviewCacheKey(sheet))
                    return false;
                PreviewImage.Source = pixels;
                _displayedPreviewKey = key;
                _fitPreview = true;
                UpdatePreviewScale();
                PreviewNotice.Text = "打印预览已更新";
                return true;
            }
            catch (Exception exception)
            {
                if (!_closed && version == _previewVersion)
                    PreviewNotice.Text = "预览显示失败，可点击“更新预览”重试。" + exception.Message;
                PdfPublisherService.WritePublishDiagnostic("WPF 打印预览显示失败" + Environment.NewLine + exception + Environment.NewLine);
                return false;
            }
        }

        private async Task GeneratePreview()
        {
            var sheet = _viewModel.SelectedSheet;
            if (sheet == null || Busy || _generatingPreview || !CommitGrid())
                return;
            _generatingPreview = true;
            UpdateBusyUi();
            PreviewNotice.Text = "正在生成打印预览…";
            try
            {
                var path = await _viewModel.CreatePrintPreviewAsync(sheet);
                CachePreview(sheet, path);
                await ShowPreviewAsync(sheet, path);
            }
            catch (Exception exception)
            {
                if (!_closed)
                    PreviewNotice.Text = exception.Message;
            }
            finally
            {
                _generatingPreview = false;
                if (!_closed)
                    UpdateBusyUi();
            }
        }

        private async void AllPreviews_Click(object sender, RoutedEventArgs e)
        {
            if (Busy || _generatingPreview || !CommitGrid())
                return;
            var sheets = _rows.ToList();
            if (sheets.Count == 0)
            {
                PreviewNotice.Text = "当前列表没有可预览的图纸。";
                return;
            }

            _generatingPreview = true;
            var failures = 0;
            var completedSheets = new List<SheetItem>();
            _previewVersion++;
            PreviewImage.Source = null;
            _displayedPreviewKey = null;
            Progress.Maximum = sheets.Count;
            Progress.Value = 0;
            UpdateBusyUi();
            PreviewNotice.Text = "正在生成打印预览 0 / " + sheets.Count;
            try
            {
                await _viewModel.CreatePrintPreviewsAsync(sheets, (current, total, sheet, path, error) =>
                {
                    if (_closed)
                    {
                        DeletePreview(path);
                        return;
                    }

                    Progress.Value = current;
                    if (error == null)
                    {
                        CachePreview(sheet, path);
                        completedSheets.Add(sheet);
                        if (ReferenceEquals(sheet, _viewModel.SelectedSheet))
                            _ = ShowPreviewAsync(sheet, path);
                    }
                    else
                    {
                        failures++;
                        if (_previewPaths.TryGetValue(sheet, out var previous))
                        {
                            DeletePreview(previous.Value);
                            _previewPaths.Remove(sheet);
                        }

                        PdfPublisherService.WritePublishDiagnostic("批量生成打印预览失败：" + sheet.SheetNumber + " " + sheet.SheetName + Environment.NewLine + error + Environment.NewLine);
                    }

                    PreviewNotice.Text = "已准备预览 " + current + " / " + total + (error == null ? string.Empty : "  " + error.Message);
                });
                if (!_closed)
                {
                    var selected = completedSheets.Contains(_viewModel.SelectedSheet) ? _viewModel.SelectedSheet : completedSheets.FirstOrDefault();
                    if (selected != null)
                    {
                        _viewModel.SelectedSheet = selected;
                        if (await ShowPreviewAsync(selected, _previewPaths[selected].Value))
                            PreviewNotice.Text = "预览已完成：成功 " + completedSheets.Count + " 张" + (failures == 0 ? string.Empty : "，失败 " + failures + " 张，可选中后更新预览重试。详情见发布日志。");
                    }
                    else
                        PreviewNotice.Text = "这次未生成可用预览，请选中图纸后更新预览重试。详情见发布日志。";
                }
            }
            catch (Exception exception)
            {
                if (!_closed)
                    ShowError(exception);
            }
            finally
            {
                _generatingPreview = false;
                if (!_closed)
                    UpdateBusyUi();
            }
        }

        private void PreviewImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || PreviewImage.Source == null)
                return;
            var window = BppDialog.Create("打印预览", 1000, 760);
            window.Content = new ScrollViewer
            {
                Content = new Image
                {
                    Source = PreviewImage.Source,
                    Stretch = Stretch.Uniform
                },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            BppDialog.Show(window, this);
        }

        private void Quick_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_quickUpdating || QuickScale == null)
                return;
            if (ReferenceEquals(sender, QuickScale) && e.AddedItems.Count > 0)
            {
                QuickScale.Text = e.AddedItems[0] as string ?? QuickScale.Text;
            }

            ApplyQuickSettings();
        }

        private void QuickScale_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyQuickSettings();
        private void ApplyQuickSettings()
        {
            var sheet = _viewModel.SelectedSheet;
            if (_quickUpdating || sheet == null || Busy)
                return;
            sheet.PaperOrientation = QuickOrientation.SelectedItem as string ?? sheet.PaperOrientation;
            if (!string.IsNullOrWhiteSpace(QuickScale.Text))
                sheet.PrintScale = QuickScale.Text.Trim();
            _viewModel.ApplySheetEdits();
            PreviewImage.Source = null;
            RefreshRows();
        }

        public void ScanDrawing()
        {
            if (!Busy)
            {
                _viewModel.ScanCommand.Execute(null);
                RefreshRows();
            }
        }

        public void PublishPdf()
        {
            if (Busy || !CommitGrid())
                return;
            _viewModel.SaveProjectCommand.Execute(null);
            _viewModel.PublishCommand.Execute(null);
        }

        public void OpenFrameManagerForCommand() => FrameManager_Click(this, new RoutedEventArgs());
        public void OpenFrameCreationForCommand() => BppDialog.ShowFrameCreation(_viewModel, this);
        public void OpenCatalogInsertForCommand() => BppDialog.ShowCatalog(_viewModel, this, RefreshRows);
        private void Publish_Click(object sender, RoutedEventArgs e) => PublishPdf();
        private void Scan_Click(object sender, RoutedEventArgs e) => ScanDrawing();
        private void ScanChecked_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy)
            {
                _viewModel.ScanCadFiles(_viewModel.CadFiles.Where(file => file.IsSelected).Select(file => file.Path).ToList());
                RefreshRows();
            }
        }

        private void AddCad_Click(object sender, RoutedEventArgs e)
        {
            if (Busy)
                return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CAD 图纸|*.dwg",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true)
                _viewModel.AddCadFiles(dialog.FileNames);
        }

        private void RemoveCad_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy && CadFilesList.SelectedItem is CadFileItem file)
            {
                _viewModel.RemoveCadFile(file.Path);
                RefreshRows();
            }
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy)
                MessageBox.Show(this, _viewModel.SaveAllOpenCadFiles(), "全部保存");
        }

        private void CadFiles_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null || FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
                return;
            if (!Busy && CadFilesList.SelectedItem is CadFileItem file)
                _viewModel.OpenCadFile(file.Path);
        }

        private void CadFileChecked_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is CadFileItem file)
                _viewModel.SetCadFileSelected(file.Path, file.IsSelected);
        }

        private void ProjectManager_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy)
            {
                BppDialog.ShowProjects(_viewModel, this);
                RefreshRows();
            }
        }

        private void FrameManager_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy)
            {
                BppDialog.ShowFrames(_viewModel, this);
                RefreshRows();
            }
        }

        private void Catalog_Click(object sender, RoutedEventArgs e) => OpenCatalogInsertForCommand();
        private void SelectionPublish_Click(object sender, RoutedEventArgs e)
        {
            if (!Busy)
                BppDialog.ShowSelectionPublisher(_viewModel, this);
        }

        private void SaveCad_Click(object sender, RoutedEventArgs e)
        {
            if (Busy)
                return;
            if (!_viewModel.SaveCurrentCadToProjectFolder(out var destination, out var error))
                MessageBox.Show(this, error, "存入工程");
        }

        private void RefreshOutput()
        {
            var selected = new HashSet<string>(_viewModel.PublishBuildings.Where(item => item.IsSelected).Select(item => item.Name));
            var count = _viewModel.Sheets.Count(sheet => selected.Contains(sheet.Building));
            PublishSummary.Text = "发布 " + count + " 张 · " + selected.Count + " 个子项目";
            var directories = _viewModel.CadFiles.Where(file => file.IsSelected).Select(file => Path.GetDirectoryName(file.Path)).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var output = _viewModel.OutputDirectory ?? string.Empty;
            if (_viewModel.OutputNextToCadFile)
                output = directories.FirstOrDefault() + (directories.Count > 1 ? " 等 " + directories.Count + " 个目录" : string.Empty);
            ActualOutputLabel.Text = "输出目录 " + output;
            ActualOutputLabel.ToolTip = _viewModel.OutputNextToCadFile ? string.Join(Environment.NewLine, directories) : output;
        }

        private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message, "批量 PDF 发布", MessageBoxButton.OK, MessageBoxImage.Warning);
        private void WindowClosing(object sender, CancelEventArgs e)
        {
            if (Busy || _generatingPreview)
            {
                e.Cancel = true;
                MessageBox.Show(this, "请等待当前任务完成后关闭窗口。", "任务正在处理");
            }
        }

        private static string LayoutPath => UserDataPaths.SettingsFile("批量发布-WPF联动布局.txt");

        private void LoadLayout()
        {
            try
            {
                var currentLayout = File.Exists(LayoutPath);
                var path = currentLayout ? LayoutPath : UserDataPaths.SettingsFile("批量发布-WPF布局.txt");
                if (!File.Exists(path))
                    return;
                var values = File.ReadAllLines(path);
                if (currentLayout && values.Length > 0 && double.TryParse(values[0], out var left))
                    LeftColumn.Width = new GridLength(Math.Max(220, Math.Min(400, left)));
                if (currentLayout && values.Length > 1 && double.TryParse(values[1], out var right))
                    RightColumn.Width = new GridLength(Math.Max(0.6, Math.Min(2.5, right)), GridUnitType.Star);
                if (values.Length > 2)
                {
                    var names = values[2].Split('|');
                    for (var index = 0; index < names.Length; index++)
                    {
                        var column = SheetsGrid.Columns.FirstOrDefault(item => item.SortMemberPath == names[index]);
                        if (column != null)
                            column.DisplayIndex = Math.Min(index, SheetsGrid.Columns.Count - 1);
                    }
                }

                if (currentLayout && values.Length > 3)
                {
                    var hidden = new HashSet<string>(values[3].Split('|'));
                    foreach (var column in SheetsGrid.Columns)
                        column.Visibility = hidden.Contains(column.Header.ToString()) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (IOException)
            {
            }
        }

        private void SaveLayout()
        {
            try
            {
                File.WriteAllLines(LayoutPath, new[] { LeftColumn.ActualWidth.ToString(), (RightColumn.ActualWidth / Math.Max(1, WorkArea.ActualWidth - LeftColumn.ActualWidth - 16) * 3.55).ToString(), string.Join("|", SheetsGrid.Columns.OrderBy(column => column.DisplayIndex).Select(column => column.SortMemberPath)), string.Join("|", SheetsGrid.Columns.Where(column => column.Visibility != Visibility.Visible).Select(column => column.Header.ToString())) });
            }
            catch (IOException)
            {
            }
        }
    }
}
