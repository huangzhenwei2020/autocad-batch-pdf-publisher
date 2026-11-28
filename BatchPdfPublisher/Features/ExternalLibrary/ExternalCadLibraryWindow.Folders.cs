using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Views
{
    internal sealed partial class ExternalCadLibraryWindow
    {
        private readonly List<string> _libraryRoots = new List<string>();
        private string _folderSettingsError;
        private Button _newFolder;
        private TreeViewItem _dragFolder;
        private TextBlock _dropHeader;
        private Point _dragOrigin;
        private ExternalCadAsset _dragAsset;
        private Point _assetDragOrigin;
        private bool _movingAsset;

        private TreeViewItem SelectedFolder => _folders.SelectedItem as TreeViewItem;

        private TreeViewItem FindRoot(string path) =>
            _folders.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is Folder folder && Same(folder.Root, path));

        private void ChooseLibrary()
        {
            var path = BppDialog.ChooseFolder(_window, (SelectedFolder?.Tag as Folder)?.FullPath);
            if (path != null) AddLibrary(path);
        }

        private void AddLibrary(string path)
        {
            path = ExternalCadFolders.Normalize(path);
            var existing = FindRoot(path);
            if (existing != null)
            {
                SelectFolderNode(existing);
                return;
            }
            ExternalCadFolders.RequireDirectory(path, path);
            RequireSeparateLibrary(path, _libraryRoots);
            SaveRoots(_libraryRoots.Concat(new[] { path }));
            var item = CreateFolder(path, "");
            _folders.Items.Add(item);
            SelectFolderNode(item);
        }

        private static void RequireSeparateLibrary(string path, IEnumerable<string> otherRoots)
        {
            if (otherRoots.Any(root => ExternalCadFolders.Contains(root, path) || ExternalCadFolders.Contains(path, root)))
                throw new InvalidOperationException("此目录与已添加的图库重叠，可在左侧展开已有图库查找。");
        }

        private void SaveRoots(IEnumerable<string> roots)
        {
            var updated = roots.ToList();
            ExternalCadFolders.Save(_libraryRoots, updated);
            _libraryRoots.Clear();
            _libraryRoots.AddRange(updated);
        }

        private static void SelectFolderNode(TreeViewItem item)
        {
            item.IsSelected = true;
            item.IsExpanded = true;
            item.BringIntoView();
        }

        private void AttachFolderMenu(TreeViewItem item, TextBlock header)
        {
            var folder = (Folder)item.Tag;
            var menu = new ContextMenu();
            void Add(string title, Action action)
            {
                var entry = new MenuItem { Header = title };
                entry.Click += (sender, args) => Run(action);
                menu.Items.Add(entry);
            }
            Add("新建子文件夹", () => PromptForFolderName(item, false));
            if (Path.GetDirectoryName(folder.FullPath) != null)
                Add("重命名（F2）", () => PromptForFolderName(item, true));
            Add("在资源管理器中打开", () => OpenFolder(folder.FullPath));
            if (folder.IsRoot) Add("从列表移除图库", () => RemoveLibrary(folder));
            header.ContextMenu = menu;
        }

        private void RemoveLibrary(Folder folder)
        {
            SaveRoots(_libraryRoots.Where(root => !Same(root, folder.Root)));
            _marked.RemoveAll(asset => ExternalCadFolders.Contains(folder.Root, asset.Path));
            foreach (var asset in _previewCache.Where(asset => ExternalCadFolders.Contains(folder.Root, asset.Path)).ToList())
                _previewCache.Remove(asset);
            ResetFolders((SelectedFolder?.Tag as Folder)?.Root);
        }

        private void PromptForFolderName(TreeViewItem item, bool rename)
        {
            if (!(item?.Tag is Folder folder)) return;
            var caption = rename ? "重命名文件夹" : "新建子文件夹";
            var dialog = BppDialog.Create(caption, 440, 195);
            dialog.ResizeMode = ResizeMode.NoResize;
            var panel = new DockPanel { Margin = new Thickness(12) };
            dialog.Content = panel;
            var name = new TextBox { Text = rename ? folder.Name : "", MaxLength = 180 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(MakeButton("取消", () => dialog.DialogResult = false));
            var confirm = MakeButton(rename ? "重命名" : "创建", () =>
            {
                try
                {
                    if (rename) RenameFolder(item, name.Text);
                    else CreateSubfolder(folder, name.Text);
                    dialog.DialogResult = true;
                }
                catch (Exception ex) { error.Text = ex.Message; }
            }, primary: true);
            confirm.IsDefault = true;
            actions.Children.Add(confirm);
            panel.Children.Add(Docked(actions, Dock.Bottom));
            panel.Children.Add(Docked(error, Dock.Bottom));
            panel.Children.Add(Docked(new TextBlock { Text = folder.FullPath, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder.FullPath }, Dock.Top));
            panel.Children.Add(Docked(name, Dock.Top));
            dialog.Loaded += (sender, args) => { name.Focus(); name.SelectAll(); };
            BppDialog.Show(dialog, _window);
        }

        private void CreateSubfolder(Folder parent, string name)
        {
            ExternalCadFolders.RequireDirectory(parent.Root, parent.FullPath);
            var path = ExternalCadFolders.ChildPath(parent.FullPath, name);
            ExternalCadFolders.RequireUnusedPath(path);
            Directory.CreateDirectory(path);
            _ = RevealFolderAsync(parent.Root, ExternalCadFolders.Relative(parent.Root, path));
        }

        private void RenameFolder(TreeViewItem item, string name)
        {
            var folder = (Folder)item.Tag;
            var parent = Path.GetDirectoryName(folder.FullPath);
            if (parent == null) throw new InvalidOperationException("不能重命名磁盘或共享根目录。");
            var target = ExternalCadFolders.ChildPath(parent, name);
            if (target == folder.FullPath) return;
            ExternalCadFolders.RequireDirectory(folder.Root, folder.FullPath);
            if (folder.IsRoot)
            {
                RequireSeparateLibrary(target, _libraryRoots.Where(root => !Same(root, folder.Root)));
                ExternalCadFolders.Move(folder.FullPath, target);
                try { SaveRoots(_libraryRoots.Select(root => Same(root, folder.Root) ? target : root)); }
                catch
                {
                    ExternalCadFolders.Move(target, folder.FullPath);
                    throw;
                }
                RebaseAssets(folder.FullPath, target, target);
                ResetFolders(target);
                return;
            }
            RelocateFolder(item, folder.Root, target);
        }

        private void RelocateFolder(TreeViewItem item, string root, string target)
        {
            var folder = (Folder)item.Tag;
            ExternalCadFolders.Move(folder.FullPath, target);
            RebaseAssets(folder.FullPath, target, root);
            _rootVersion++;
            _folderVersion++;
            ((TreeViewItem)ItemsControl.ItemsControlFromItemContainer(item)).Items.Remove(item);
            _ = RevealFolderAsync(root, ExternalCadFolders.Relative(root, target));
        }

        private void RebaseAssets(string original, string destination, string root)
        {
            foreach (var asset in _assets.Concat(_marked).Concat(_previewCache).Distinct())
            {
                if (!ExternalCadFolders.Contains(original, asset.Path)) continue;
                asset.Path = Path.Combine(destination, ExternalCadFolders.Relative(original, asset.Path));
                asset.Category = ExternalCadFolders.Relative(root, Path.GetDirectoryName(asset.Path));
            }
        }

        private void DropFolder(TreeViewItem source, TreeViewItem target, bool after)
        {
            var from = (Folder)source.Tag;
            var to = (Folder)target.Tag;
            if (source == target) return;
            if (from.IsRoot)
            {
                if (!to.IsRoot) return;
                var order = _libraryRoots.Where(root => !Same(root, from.Root)).ToList();
                var index = order.FindIndex(root => Same(root, to.Root)) + (after ? 1 : 0);
                order.Insert(index, from.Root);
                if (_libraryRoots.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) return;
                SaveRoots(order);
                var selection = SelectedFolder;
                _folders.Items.Remove(source);
                _folders.Items.Insert(index, source);
                if (selection != null) selection.IsSelected = true;
                return;
            }
            if (!ExternalCadFolders.CanMove(from.FullPath, to.FullPath))
                throw new InvalidOperationException("请选择同一磁盘上的其他文件夹，且目标不能位于被移动文件夹内。");
            ExternalCadFolders.RequireDirectory(from.Root, from.FullPath);
            ExternalCadFolders.RequireDirectory(to.Root, to.FullPath);
            RelocateFolder(source, to.Root, ExternalCadFolders.ChildPath(to.FullPath, from.Name));
        }

        private bool CanDropAsset(ExternalCadAsset asset, Folder target) =>
            !_movingAsset && target != null && _assets.Contains(asset)
            && !Same(Path.GetDirectoryName(asset.Path), target.FullPath);

        private async Task DropAssetAsync(ExternalCadAsset asset, Folder target)
        {
            if (!CanDropAsset(asset, target)) return;
            var sourceFolder = (SelectedFolder?.Tag as Folder)?.FullPath;
            var sourceLibrary = (SelectedFolder?.Tag as Folder)?.Root;
            var sourcePath = asset.Path;
            if (sourceFolder == null || !Same(sourceFolder, Path.GetDirectoryName(sourcePath))) return;
            var targetPath = Path.Combine(target.FullPath, Path.GetFileName(sourcePath));
            _movingAsset = true;
            _thumbnailTimer.Stop();
            _status.Text = "正在移动「" + asset.Name + "」…";
            try
            {
                await Task.Run(() =>
                {
                    ExternalCadFolders.RequireDirectory(sourceLibrary, sourceFolder);
                    ExternalCadFolders.RequireDirectory(target.Root, target.FullPath);
                    ExternalCadLibrary.RequireSameDrawing(asset);
                    ExternalCadFolders.MoveDrawing(sourcePath, targetPath);
                });
                if (_closed) return;
                foreach (var value in _assets.Concat(_marked).Concat(_previewCache).Append(asset).Distinct())
                {
                    if (!Same(value.Path, sourcePath)) continue;
                    value.Path = targetPath;
                    value.Category = target.Path;
                }
                var current = SelectedFolder?.Tag as Folder;
                if (current != null && (Same(current.FullPath, sourceFolder) || Same(current.FullPath, target.FullPath)))
                    await LoadFolderAsync(current);
                if (!_closed) _status.Text = "已将「" + asset.Name + "」移到「" + target.Name + "」。";
            }
            catch (Exception ex)
            {
                if (!_closed) _status.Text = "移动失败：" + ex.Message;
            }
            finally
            {
                _movingAsset = false;
                QueueVisibleThumbnails();
            }
        }

        private void HookFolderInteraction()
        {
            _folders.AllowDrop = true;
            _window.PreviewMouseLeftButtonUp += (sender, args) => _dragAsset = null;
            _window.PreviewMouseMove += (sender, args) =>
            {
                if (_movingAsset || _dragAsset == null || args.LeftButton != MouseButtonState.Pressed) return;
                var delta = args.GetPosition(_window) - _assetDragOrigin;
                if (!PassedDragThreshold(delta)) return;
                var asset = _dragAsset;
                _dragAsset = null;
                try { DragDrop.DoDragDrop(_cards, new DataObject(typeof(ExternalCadAsset), asset), DragDropEffects.Move); }
                finally { HighlightDrop(null); }
            };
            _folders.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.F2 || SelectedFolder == null) return;
                args.Handled = true;
                Run(() => PromptForFolderName(SelectedFolder, true));
            };
            _folders.PreviewMouseLeftButtonDown += (sender, args) =>
            {
                _dragFolder = HitFolder(args.OriginalSource);
                _dragOrigin = args.GetPosition(_folders);
            };
            _folders.PreviewMouseLeftButtonUp += (sender, args) => _dragFolder = null;
            _folders.MouseMove += (sender, args) =>
            {
                if (_movingAsset || _dragFolder == null || args.LeftButton != MouseButtonState.Pressed) return;
                var delta = args.GetPosition(_folders) - _dragOrigin;
                if (!PassedDragThreshold(delta)) return;
                var item = _dragFolder;
                _dragFolder = null;
                try { DragDrop.DoDragDrop(item, new DataObject(typeof(TreeViewItem), item), DragDropEffects.Move); }
                finally { HighlightDrop(null); }
            };
            _folders.DragOver += (sender, args) =>
            {
                var source = args.Data.GetData(typeof(TreeViewItem)) as TreeViewItem;
                var asset = args.Data.GetData(typeof(ExternalCadAsset)) as ExternalCadAsset;
                var target = HitFolder(args.OriginalSource);
                var from = source?.Tag as Folder;
                var to = target?.Tag as Folder;
                var allowed = asset != null ? CanDropAsset(asset, to) : !_movingAsset && source != target && from != null && to != null
                    && (from.IsRoot ? to.IsRoot : ExternalCadFolders.CanMove(from.FullPath, to.FullPath));
                HighlightDrop(allowed ? target.Header as TextBlock : null);
                args.Effects = allowed ? DragDropEffects.Move : DragDropEffects.None;
                args.Handled = true;
            };
            _folders.DragLeave += (sender, args) => HighlightDrop(null);
            _folders.Drop += (sender, args) =>
            {
                HighlightDrop(null);
                var source = args.Data.GetData(typeof(TreeViewItem)) as TreeViewItem;
                var asset = args.Data.GetData(typeof(ExternalCadAsset)) as ExternalCadAsset;
                var target = HitFolder(args.OriginalSource);
                if (asset != null && target?.Tag is Folder destination)
                    _ = DropAssetAsync(asset, destination);
                else if (source?.Tag is Folder && target?.Header is TextBlock targetHeader)
                    Run(() => DropFolder(source, target, args.GetPosition(targetHeader).Y >= targetHeader.ActualHeight / 2));
                args.Handled = true;
            };
        }

        private static bool PassedDragThreshold(Vector delta) =>
            Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;

        private static TreeViewItem HitFolder(object origin)
        {
            for (var node = origin as Visual; node != null; node = VisualTreeHelper.GetParent(node) as Visual)
                if (node is TreeViewItem item) return item.Tag is Folder ? item : null;
            return null;
        }

        private void HighlightDrop(TextBlock header)
        {
            if (header == _dropHeader) return;
            _dropHeader?.ClearValue(TextBlock.BackgroundProperty);
            _dropHeader = header;
            if (header != null) header.Background = Brush("#276D81");
        }
    }
}
