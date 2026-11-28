using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BatchPdfPublisher.Views
{
    internal sealed partial class ExternalCadLibraryWindow
    {
        private const int PageSize = 18;
        private readonly Document _document;
        private readonly Window _window;
        private readonly TextBox _search = new TextBox { MinWidth = 200, ToolTip = "搜索当前文件夹中的 DWG" };
        private readonly TreeView _folders = new TreeView();
        private readonly WrapPanel _cards = new WrapPanel();
        private readonly ScrollViewer _cardScroll;
        private readonly ExternalCadView _large = new ExternalCadView { Height = 200, ShowOrigin = true };
        private readonly ExternalCadView _mark = new ExternalCadView { Height = 55 };
        private readonly TextBlock _assetName = new TextBlock { FontSize = 18 };
        private readonly CheckBox _title = new CheckBox { Content = "天正图名" };
        private readonly CheckBox _number = new CheckBox { Content = "图名编号" };
        private readonly TextBox _titleValue = new TextBox();
        private readonly TextBox _numberValue = new TextBox { Text = "1" };
        private readonly TextBox _scale = new TextBox { Text = "1:25" };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _count = new TextBlock();
        private readonly TextBlock _pageLabel = new TextBlock();
        private readonly List<ExternalCadAsset> _marked = new List<ExternalCadAsset>();
        private readonly LinkedList<ExternalCadAsset> _previewCache = new LinkedList<ExternalCadAsset>();
        private readonly DispatcherTimer _thumbnailTimer;
        private List<ExternalCadAsset> _assets = new List<ExternalCadAsset>();
        private readonly Dictionary<ExternalCadAsset, Border> _borders = new Dictionary<ExternalCadAsset, Border>();
        private readonly List<PendingThumbnail> _pendingThumbnails = new List<PendingThumbnail>();
        private ExternalCadAsset _selected;
        private Button _insert, _batch, _clear, _previous, _next;
        private int _page, _rootVersion, _folderVersion;
        private bool _closed;

        internal static void Open(Document document)
        {
            var view = new ExternalCadLibraryWindow(document);
            BppDialog.Show(view._window, null);
        }

        private ExternalCadLibraryWindow(Document document)
        {
            _document = document;
            _window = BppDialog.Create("万落建筑工具 · 外部 CAD 图库", 1320, 820);
            _window.MinWidth = 820;
            _window.MinHeight = 560;
            _thumbnailTimer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _thumbnailTimer.Tick += (sender, args) => LoadNextVisibleThumbnail();
            _window.Activated += (sender, args) => QueueVisibleThumbnails();
            _window.Deactivated += (sender, args) => _thumbnailTimer.Stop();
            _window.Closed += (sender, args) => { _closed = true; _thumbnailTimer.Stop(); };
            var shell = new DockPanel { Margin = new Thickness(10) };
            _window.Content = shell;
            var toolbar = AddDock(shell, Dock.Top);
            toolbar.Margin = new Thickness(0, 4, 0, 6);
            toolbar.Children.Add(Button("收录图块", () => Capture(true), Dock.Left, true));
            toolbar.Children.Add(Button("框选收录", () => Capture(false), Dock.Left));
            toolbar.Children.Add(Button("刷新", Refresh, Dock.Left));
            toolbar.Children.Add(Docked(_search, Dock.Right));
            toolbar.Children.Add(Docked(Label("搜索当前文件夹"), Dock.Right));
            toolbar.Children.Add(new Border());
            var footer = AddDock(shell, Dock.Bottom);
            _batch = Button("批量插入", Batch, Dock.Right, true);
            _insert = Button("插入所选单项", InsertSingle, Dock.Right);
            _clear = Button("清空勾选", () => { _marked.Clear(); DisplayPage(); }, Dock.Left);
            footer.Children.Add(_batch);
            footer.Children.Add(_insert);
            footer.Children.Add(Docked(_count, Dock.Left));
            footer.Children.Add(_clear);
            footer.Children.Add(_status);

            var columns = Columns(234, 286);
            shell.Children.Add(columns);
            var left = new DockPanel();
            left.Children.Add(Docked(Label("图库文件夹"), Dock.Top));
            var foldersToolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            foldersToolbar.Children.Add(Button("添加图库", ChooseLibrary));
            _newFolder = Button("新建文件夹", () => PromptForFolderName(SelectedFolder, false));
            _newFolder.IsEnabled = false;
            foldersToolbar.Children.Add(_newFolder);
            left.Children.Add(Docked(foldersToolbar, Dock.Top));
            _folders.Background = Brush("#14232F");
            _folders.Foreground = Brush("#E8F0F7");
            _folders.BorderBrush = Brush("#2D4558");
            ScrollViewer.SetHorizontalScrollBarVisibility(_folders, ScrollBarVisibility.Disabled);
            left.Children.Add(_folders);
            columns.Children.Add(left);

            var gallery = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
            Grid.SetColumn(gallery, 1);
            columns.Children.Add(gallery);
            var pages = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            pages.Children.Add(_previous = Button("上一页", () => { _page--; DisplayPage(); }));
            pages.Children.Add(_pageLabel);
            pages.Children.Add(_next = Button("下一页", () => { _page++; DisplayPage(); }));
            gallery.Children.Add(Docked(pages, Dock.Bottom));
            _cardScroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _cardScroll.SizeChanged += (sender, args) => FitCards(_cardScroll.ActualWidth);
            _cardScroll.ScrollChanged += (sender, args) => QueueVisibleThumbnails();
            gallery.Children.Add(_cardScroll);

            var settings = new StackPanel();
            settings.Children.Add(_assetName);
            settings.Children.Add(_large);
            settings.Children.Add(Label("随图插入"));
            settings.Children.Add(_title);
            settings.Children.Add(_number);
            settings.Children.Add(Field("图名", _titleValue));
            settings.Children.Add(Field("编号", _numberValue));
            settings.Children.Add(Field("比例", _scale));
            settings.Children.Add(_mark);
            var settingsScroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(settingsScroll, 2);
            columns.Children.Add(settingsScroll);

            _search.TextChanged += (sender, args) => { _page = 0; DisplayPage(); };
            _folders.SelectedItemChanged += (sender, args) =>
            {
                var folder = (args.NewValue as TreeViewItem)?.Tag as Folder;
                _newFolder.IsEnabled = folder != null;
                if (folder != null) _ = LoadFolderAsync(folder);
            };
            HookFolderInteraction();
            foreach (var box in new[] { _title, _number }) { box.Checked += (s, e) => UpdateLabel(); box.Unchecked += (s, e) => UpdateLabel(); }
            foreach (var field in new[] { _titleValue, _numberValue, _scale }) field.TextChanged += (s, e) => UpdateLabel();
            try { _libraryRoots.AddRange(ExternalCadFolders.Load()); }
            catch (Exception ex) { _folderSettingsError = ex.Message; }
            _window.Loaded += (sender, args) => { if (_rootVersion == 0) ResetFolders(); };
            UpdateCommands();
            UpdateLabel();
        }

        private void Refresh()
        {
            if (_folders.SelectedItem is TreeViewItem item && item.Tag is Folder folder)
            {
                _ = LoadFolderAsync(folder);
                _ = LoadChildrenAsync(item, true);
            }
            else ResetFolders();
        }

        private void ResetFolders(string selectedRoot = null)
        {
            _rootVersion++;
            _folderVersion++;
            _assets.Clear();
            _folders.Items.Clear();
            _page = 0;
            Select(null);
            DisplayPage();
            if (_libraryRoots.Count == 0)
            {
                _status.Text = _folderSettingsError ?? "点击「添加图库」选择已有文件夹。";
                return;
            }
            foreach (var path in _libraryRoots) _folders.Items.Add(CreateFolder(path, ""));
            var root = FindRoot(selectedRoot) ?? (TreeViewItem)_folders.Items[0];
            root.IsSelected = true;
            root.IsExpanded = true;
        }

        private TreeViewItem CreateFolder(string root, string category)
        {
            var folder = new Folder(root, category);
            var item = new TreeViewItem { Tag = folder, Foreground = Brush("#E8F0F7") };
            var header = new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                Padding = new Thickness(5, 2, 5, 2), ToolTip = folder.FullPath };
            var style = new Style(typeof(TextBlock));
            var selected = new DataTrigger { Binding = new Binding(nameof(TreeViewItem.IsSelected)) { Source = item }, Value = true };
            selected.Setters.Add(new Setter(TextBlock.BackgroundProperty, Brush("#195A92")));
            style.Triggers.Add(selected);
            header.Style = style;
            item.Header = header;
            AttachFolderMenu(item, header);
            item.Items.Add(new TreeViewItem { Header = "正在读取…", IsEnabled = false });
            item.Expanded += (sender, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, item)) _ = LoadChildrenAsync(item);
            };
            return item;
        }

        private async Task LoadChildrenAsync(TreeViewItem parent, bool force = false)
        {
            var folder = (Folder)parent.Tag;
            if (folder.Loaded && !force) return;
            var version = _rootVersion;
            var request = ++folder.Request;
            try
            {
                var children = await Task.Run(() => ExternalCadLibrary.ReadSubfolders(folder.Root, folder.Path));
                if (_closed || version != _rootVersion || request != folder.Request) return;
                var existing = parent.Items.OfType<TreeViewItem>().Where(value => value.Tag is Folder)
                    .ToDictionary(value => ((Folder)value.Tag).Path, StringComparer.OrdinalIgnoreCase);
                var ordered = children.Select(path => existing.TryGetValue(path, out var item) ? item : CreateFolder(folder.Root, path)).ToList();
                if (!parent.Items.Cast<object>().SequenceEqual(ordered))
                {
                    var retained = new HashSet<TreeViewItem>(ordered);
                    foreach (var obsolete in parent.Items.OfType<TreeViewItem>().Where(item => !retained.Contains(item)).ToList())
                        parent.Items.Remove(obsolete);
                    for (var index = 0; index < ordered.Count; index++)
                    {
                        var item = ordered[index];
                        if (index < parent.Items.Count && ReferenceEquals(parent.Items[index], item)) continue;
                        parent.Items.Remove(item);
                        parent.Items.Insert(index, item);
                    }
                }
                folder.Loaded = true;
            }
            catch (Exception ex)
            {
                if (_closed || version != _rootVersion || request != folder.Request) return;
                if (!folder.Loaded) parent.Items.Clear();
                _status.Text = "无法读取文件夹：" + ex.Message;
            }
        }

        private async Task LoadFolderAsync(Folder folder)
        {
            var version = ++_folderVersion;
            _page = 0;
            _assets = new List<ExternalCadAsset>();
            Select(null);
            DisplayPage("正在读取当前文件夹…");
            try
            {
                var assets = await Task.Run(() => ExternalCadLibrary.ReadFolder(folder.Root, folder.Path));
                if (_closed || version != _folderVersion) return;
                var previews = _previewCache.ToDictionary(cached => cached.Path, StringComparer.OrdinalIgnoreCase);
                _assets = assets.Select(asset => previews.TryGetValue(asset.Path, out var cached)
                    && cached.Size == asset.Size && cached.LastWriteUtc == asset.LastWriteUtc ? cached : asset).ToList();
                var paths = _assets.ToDictionary(asset => asset.Path, StringComparer.OrdinalIgnoreCase);
                for (var index = _marked.Count - 1; index >= 0; index--)
                {
                    if (!Same(Path.GetDirectoryName(_marked[index].Path), folder.FullPath)) continue;
                    if (paths.TryGetValue(_marked[index].Path, out var replacement)) _marked[index] = replacement;
                    else _marked.RemoveAt(index);
                }
                DisplayPage();
                _status.Text = _assets.Count == 0 ? "可在左侧展开并选择子文件夹。" : "双击卡片插入单项，或勾选后批量插入。";
            }
            catch (Exception ex)
            {
                if (_closed || version != _folderVersion) return;
                DisplayPage("无法读取当前文件夹");
                _status.Text = ex.Message;
            }
        }

        private void DisplayPage(string emptyMessage = null)
        {
            var term = _search.Text.Trim();
            var visible = _assets.Where(asset => asset.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            var pages = Math.Max(1, (visible.Count + PageSize - 1) / PageSize);
            _page = Math.Max(0, Math.Min(_page, pages - 1));
            _cards.Children.Clear();
            _borders.Clear();
            _pendingThumbnails.Clear();
            foreach (var asset in visible.Skip(_page * PageSize).Take(PageSize)) _cards.Children.Add(Card(asset));
            if (visible.Count == 0) _cards.Children.Add(Label(emptyMessage ?? (term.Length == 0 ? "当前文件夹暂无 DWG" : "当前文件夹没有匹配的 DWG")));
            FitCards(_cardScroll.ActualWidth);
            _pageLabel.Text = (_page + 1) + " / " + pages;
            if (_previous != null) _previous.IsEnabled = _page > 0;
            if (_next != null) _next.IsEnabled = _page + 1 < pages;
            _cardScroll.ScrollToTop();
            UpdateCommands();
            QueueVisibleThumbnails();
        }

        private void FitCards(double width)
        {
            if (width <= 0 || double.IsNaN(width)) return;
            var available = Math.Max(30, width - SystemParameters.VerticalScrollBarWidth);
            var columns = Math.Max(1, Math.Min(3, (int)(available / 155)));
            foreach (var card in _borders.Values)
            {
                card.Width = Math.Floor(available / columns) - 8;
                var preview = ((StackPanel)card.Child).Children.OfType<ExternalCadView>().Single();
                preview.Height = Math.Max(110, Math.Min(190, card.Width * .66));
            }
        }

        private Border Card(ExternalCadAsset asset)
        {
            var box = new CheckBox { IsChecked = _marked.Contains(asset), Margin = new Thickness(5), ToolTip = "勾选以批量插入" };
            box.Checked += (sender, args) => { if (!_marked.Contains(asset)) _marked.Add(asset); UpdateCommands(); };
            box.Unchecked += (sender, args) => { _marked.Remove(asset); UpdateCommands(); };
            var stack = new StackPanel();
            stack.Children.Add(box);
            var preview = new ExternalCadView { Height = 122, IsHitTestVisible = false };
            if (asset.Preview != null || asset.Error != null) PaintPreview(asset, preview, 400);
            else _pendingThumbnails.Add(new PendingThumbnail(asset, preview, box));
            box.IsEnabled = asset.Error == null;
            stack.Children.Add(preview);
            stack.Children.Add(new TextBlock { Text = asset.Name, Margin = new Thickness(8, 6, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = asset.IsCollected ? "图库收录" : "外部 DWG",
                Foreground = Brush("#91A5B6"), FontSize = 11, Margin = new Thickness(8, 0, 8, 6) });
            var card = new Border { Width = 168, Margin = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Background = Brush("#14232F"), BorderBrush = Brush(_selected == asset ? "#1397FF" : "#2D4558"),
                Child = stack, ToolTip = asset.Path + "\n拖到左侧文件夹可移动此 DWG" };
            _borders[asset] = card;
            card.MouseLeftButtonDown += (sender, args) =>
            {
                Select(asset);
                if (args.ClickCount == 1)
                {
                    _dragAsset = asset;
                    _assetDragOrigin = args.GetPosition(_window);
                }
                if (args.ClickCount == 2) Run(InsertSingle);
            };
            return card;
        }

        private void Select(ExternalCadAsset asset)
        {
            _selected = asset;
            _assetName.Text = asset?.Name ?? "选择图库对象";
            _titleValue.Text = asset?.Name ?? "";
            PaintPreview(asset, _large);
            foreach (var pair in _borders) pair.Value.BorderBrush = Brush(pair.Key == asset ? "#1397FF" : "#2D4558");
            QueueVisibleThumbnails();
            UpdateCommands();
        }

        private void QueueVisibleThumbnails()
        {
            if (!_closed && !_movingAsset && _window.IsActive && (_pendingThumbnails.Count != 0 || NeedsPreview(_selected))) _thumbnailTimer.Start();
        }

        private static bool NeedsPreview(ExternalCadAsset asset) => asset != null && asset.Preview == null && asset.Error == null;

        private void LoadNextVisibleThumbnail()
        {
            if (_closed || _movingAsset || !_window.IsActive || _cardScroll.ViewportHeight <= 0 || _cardScroll.ViewportWidth <= 0)
            {
                _thumbnailTimer.Stop();
                return;
            }
            var viewport = new Rect(0, 0, _cardScroll.ViewportWidth, _cardScroll.ViewportHeight);
            var selected = NeedsPreview(_selected) ? _selected : null;
            var next = selected != null ? _pendingThumbnails.FirstOrDefault(thumbnail => thumbnail.Asset == selected)
                : _pendingThumbnails.FirstOrDefault(thumbnail => thumbnail.View.IsLoaded
                && thumbnail.View.ActualHeight > 0
                && viewport.IntersectsWith(thumbnail.View.TransformToAncestor(_cardScroll).TransformBounds(new Rect(thumbnail.View.RenderSize))));
            var asset = selected ?? next?.Asset;
            if (asset == null) { _thumbnailTimer.Stop(); return; }
            LoadPreview(asset);
            if (next != null)
            {
                _pendingThumbnails.Remove(next);
                PaintPreview(asset, next.View, 400);
                next.Box.IsEnabled = asset.Error == null;
                if (asset.Error != null) next.Box.IsChecked = false;
            }
            if (_selected == asset) PaintPreview(asset, _large);
            UpdateCommands();
            if (_pendingThumbnails.Count == 0) _thumbnailTimer.Stop();
        }

        private void LoadPreview(ExternalCadAsset asset)
        {
            if (asset.Preview != null || asset.Error != null) return;
            try
            {
                ExternalCadLibrary.LoadPreview(asset);
                var stale = _previewCache.FirstOrDefault(value => Same(value.Path, asset.Path));
                if (stale != null) _previewCache.Remove(stale);
                _previewCache.AddFirst(asset);
                if (_previewCache.Count > 96) _previewCache.RemoveLast();
            }
            catch (Exception ex) { asset.Error = ex.Message; }
        }

        private static void PaintPreview(ExternalCadAsset asset, ExternalCadView view, int limit = int.MaxValue)
        {
            view.Parts = asset?.Preview == null ? null : ExternalCadPreview.Asset(asset, asset.Base, 0, 0, 1, limit: limit).ToList();
            view.Error = asset?.Error != null ? "预览失败" : NeedsPreview(asset) ? "正在读取预览…" : view.Parts?.Count == 0 ? "CAD 中可查看此对象" : null;
            view.InvalidateVisual();
        }

        private void UpdateCommands()
        {
            _count.Text = "已勾选 " + _marked.Count + " 项";
            _batch.IsEnabled = _clear.IsEnabled = _marked.Count > 0;
            _insert.IsEnabled = _selected != null && _selected.Error == null;
        }

        private void UpdateLabel()
        {
            _titleValue.IsEnabled = _title.IsChecked == true;
            _numberValue.IsEnabled = _number.IsChecked == true;
            _scale.IsEnabled = _title.IsChecked == true || _number.IsChecked == true;
            int scale;
            _mark.Parts = TryScale(_scale.Text, out scale) ? ExternalCadSheet.Label(
                _titleValue.IsEnabled ? _titleValue.Text : null, _numberValue.IsEnabled ? _numberValue.Text : null, scale, 0, 0).ToList() : null;
            _mark.InvalidateVisual();
        }

        private static void OpenFolder(string path)
        {
            if (!Directory.Exists(path)) throw new InvalidOperationException("请先选择可用的图库文件夹。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void Capture(bool block)
        {
            if (SelectedFolder == null) ChooseLibrary();
            var destination = SelectedFolder?.Tag as Folder;
            if (destination == null) return;
            ExternalCadFolders.RequireDirectory(destination.Root, destination.FullPath);
            ObjectIdCollection ids;
            using (_document.Editor.StartUserInteraction(_window))
            {
                if (block)
                {
                    var options = new PromptEntityOptions("\n请选择要收录的图块：");
                    options.SetRejectMessage("\n请选择图块参照。");
                    options.AddAllowedClass(typeof(BlockReference), false);
                    var result = _document.Editor.GetEntity(options);
                    if (result.Status != PromptStatus.OK) return;
                    ids = new ObjectIdCollection(new[] { result.ObjectId });
                }
                else
                {
                    var result = _document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n框选或点选要收录的对象，回车确认：" });
                    if (result.Status != PromptStatus.OK) return;
                    ids = new ObjectIdCollection(result.Value.GetObjectIds());
                }
            }
            var asset = new ExternalCadAsset { Preview = new List<DetailPreviewPrimitive>() };
            var name = "";
            using (var tr = _document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                asset.Bounds = ExternalCadLibrary.Measure(ids, tr);
                asset.Base = asset.Bounds.MinPoint;
                foreach (ObjectId id in ids) DetailLayoutService.CapturePreview(tr.GetObject(id, OpenMode.ForRead) as Entity, asset.Preview, 0, true);
                if (block)
                {
                    var reference = (BlockReference)tr.GetObject(ids[0], OpenMode.ForRead);
                    asset.Base = reference.Position;
                    var definition = (BlockTableRecord)tr.GetObject(reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord, OpenMode.ForRead);
                    name = definition.Name.StartsWith("*", StringComparison.Ordinal) ? "" : definition.Name;
                }
            }
            var savedCategory = destination.Path;
            var rootNode = FindRoot(destination.Root);
            var categories = new[] { "" }.Concat(KnownFolders(rootNode.Items).Select(value => value.Path)).ToList();
            var dialog = new ExternalCadCaptureWindow(destination.Root, asset, name, destination.Path,
                categories, ids.Count,
                dialogWindow =>
                {
                    using (_document.Editor.StartUserInteraction(dialogWindow))
                    {
                        var result = _document.Editor.GetPoint("\n拾取插入基点：");
                        return result.Status == PromptStatus.OK ? (Point3d?)result.Value.TransformBy(_document.Editor.CurrentUserCoordinateSystem) : null;
                    }
                }, (title, folder) =>
                {
                    var path = ExternalCadLibrary.Save(_document, ids, asset.Base, destination.Root, folder, title);
                    savedCategory = ExternalCadFolders.Relative(destination.Root, Path.GetDirectoryName(path));
                });
            if (dialog.Show(_window))
            {
                _ = RevealFolderAsync(destination.Root, savedCategory);
                _status.Text = "已收录到外部图库。";
            }
        }

        private async Task RevealFolderAsync(string root, string category)
        {
            var version = _folderVersion;
            var item = FindRoot(root);
            var path = "";
            foreach (var segment in category.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (item == null) return;
                item.IsExpanded = true;
                path = Path.Combine(path, segment);
                var child = FindChild(item, path);
                if (child == null)
                {
                    await LoadChildrenAsync(item, true);
                    if (_closed || version != _folderVersion) return;
                    child = FindChild(item, path);
                }
                item = child;
            }
            if (item == null) return;
            if (item.IsSelected) Refresh();
            else { item.IsSelected = true; item.BringIntoView(); }
        }

        private static TreeViewItem FindChild(TreeViewItem parent, string path) =>
            parent.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is Folder value && Same(value.Path, path));

        private static IEnumerable<Folder> KnownFolders(ItemCollection items)
        {
            foreach (var item in items.OfType<TreeViewItem>())
            {
                if (!(item.Tag is Folder folder)) continue;
                yield return folder;
                foreach (var child in KnownFolders(item.Items)) yield return child;
            }
        }

        private void InsertSingle()
        {
            if (_selected == null) return;
            if (_selected.Error != null) throw new InvalidOperationException(_selected.Error);
            int scale;
            if (!TryScale(_scale.Text, out scale)) throw new InvalidOperationException("请输入有效比例，例如 1:25。");
            var sheet = new ExternalCadSheet { Scale = scale, Title = _title.IsChecked == true,
                Number = _number.IsChecked == true, SingleTitle = _titleValue.Text.Trim(), SingleNumber = _numberValue.Text.Trim() };
            if ((sheet.Title && sheet.SingleTitle.Length == 0) || (sheet.Number && sheet.SingleNumber.Length == 0))
                throw new InvalidOperationException("请填写已勾选的图名或编号。");
            if (sheet.SingleTitle.Length > 80 || sheet.SingleNumber.Length > 12)
                throw new InvalidOperationException("图名最多 80 字，编号最多 12 字。");
            var asset = _selected;
            Place(sheet, asset, turns => ExternalCadPreview.Single(sheet, asset, turns));
        }

        private void Batch()
        {
            if (_marked.Count == 0) return;
            var dialog = new ExternalCadBatchWindow(_document, _marked, _title.IsChecked == true, _number.IsChecked == true);
            if (!dialog.Show(_window)) return;
            var sheet = dialog.Plan;
            Place(sheet, null, ignored => Enumerable.Range(0, sheet.PageCount).SelectMany(page =>
                ExternalCadPreview.Page(sheet, page, dialog.FramePreview, Math.Max(8, 1400 / sheet.PageCount)).Select(part =>
                    ExternalCadSheet.Shape(part.Kind, part.X1 + page * (sheet.PageWidth + sheet.PageGap), part.Y1,
                        part.X2 + page * (sheet.PageWidth + sheet.PageGap), part.Y2, part.Text))));
        }

        private void Place(ExternalCadSheet sheet, ExternalCadAsset single, Func<int, IEnumerable<DetailPreviewPrimitive>> geometry)
        {
            var placed = false;
            using (_document.Editor.StartUserInteraction(_window))
            using (var prepared = NeedsPreview(single) ? ExternalCadLibrary.OpenDrawing(single.Path) : null)
            {
                if (prepared != null) ExternalCadLibrary.LoadPreview(single, prepared);
                var jig = new ExternalCadInsertionJig(_document, single != null, sheet.Frame != null, geometry);
                if (jig.Pick(out var point, out var turns))
                {
                    ExternalCadSheet.Insert(_document, sheet, single, point, turns, prepared);
                    _document.Editor.WriteMessage(single == null ? "\n已整批插入 " + sheet.Slots.Count + " 个对象。" : "\n已插入 " + single.Name + "。");
                    placed = true;
                }
                else _status.Text = "已取消插入。";
            }
            if (placed) _window.Close();
            else if (single != null) PaintPreview(single, _large);
        }

        private void Run(Action action)
        {
            if (_movingAsset) return;
            try { action(); }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
        private Button Button(string text, Action action, Dock dock = Dock.Left, bool primary = false) =>
            MakeButton(text, () => Run(action), dock, primary);
        internal static Button MakeButton(string text, Action action, Dock dock = Dock.Left, bool primary = false)
        {
            var button = new Button { Content = text };
            if (primary) button.Background = Brush("#087BC8");
            DockPanel.SetDock(button, dock);
            button.Click += (sender, args) => action();
            return button;
        }
        internal static T Docked<T>(T element, Dock dock) where T : UIElement { DockPanel.SetDock(element, dock); return element; }
        private static DockPanel AddDock(DockPanel parent, Dock edge)
        { var panel = Docked(new DockPanel(), edge); parent.Children.Add(panel); return panel; }
        internal static Grid Columns(double left, double right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right) });
            return grid;
        }
        internal static TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(5, 3, 8, 3) };
        internal static DockPanel Field(string name, Control field)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var label = Label(name); label.Width = 80;
            row.Children.Add(Docked(label, Dock.Left));
            row.Children.Add(field);
            return row;
        }
        internal static SolidColorBrush Brush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        internal static bool TryScale(string text, out int scale)
        {
            var value = (text ?? "").Trim().Replace('：', ':');
            if (value.StartsWith("1:", StringComparison.Ordinal)) value = value.Substring(2).Trim();
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out scale) && scale >= 1 && scale <= 5000;
        }
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private sealed class Folder
        {
            internal Folder(string root, string path) { Root = root; Path = path; }
            internal readonly string Root, Path;
            internal bool IsRoot => Path.Length == 0;
            internal string FullPath => IsRoot ? Root : System.IO.Path.Combine(Root, Path);
            internal string Name
            {
                get
                {
                    var name = System.IO.Path.GetFileName(FullPath.TrimEnd('\\', '/'));
                    return name.Length == 0 ? Root : name;
                }
            }
            internal bool Loaded;
            internal int Request;
        }

        private sealed class PendingThumbnail
        {
            internal PendingThumbnail(ExternalCadAsset asset, ExternalCadView view, CheckBox box)
            { Asset = asset; View = view; Box = box; }
            internal readonly ExternalCadAsset Asset;
            internal readonly ExternalCadView View;
            internal readonly CheckBox Box;
        }
    }
}
