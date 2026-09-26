using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ProbeWindow : Window
{
    private sealed class ElementItem
    {
        public string Id = "";
        public string Label = "";
        public override string ToString() => Label;
    }

    private BuildingModelEditSession _session;
    private readonly ModelViewport _viewport;
    private readonly ListBox _elements = new();
    private readonly List<ElementItem> _items = new();
    private readonly StackPanel _properties = new() { Margin = new Thickness(16), Spacing = 12 };
    private readonly TextBlock _status = new();
    private readonly Button _undo = new() { Content = "撤销" };
    private readonly Button _redo = new() { Content = "重做" };
    private readonly Button _publish = new() { Content = "生成 CAD 视图" };
    private string? _selectedId;
    private string? _filePath;
    private string _savedJson;
    private bool _closeConfirmed;
    private int _sceneGeneration;
    private bool HasChanges => BuildingModelJson.ToJson(_session.Model) != _savedJson;

    public ProbeWindow()
    {
        Title = "万落建筑模型 · 跨平台编辑探针";
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 520;
        _filePath = Program.ModelPath;
        _session = new BuildingModelEditSession(_filePath == null
            ? SampleModelFactory.CreateTwoStoreyHouse() : BuildingModelJson.LoadModel(_filePath));
        _savedJson = BuildingModelJson.ToJson(_session.Model);
        _viewport = new ModelViewport(BuildingVolumeBuilder.Build(_session.Model));
        _viewport.ElementPicked += SelectById;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("50,*,34"),
            ColumnDefinitions = new ColumnDefinitions("230,*,290"),
            Background = new SolidColorBrush(Color.Parse("#151B23"))
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Margin = new Thickness(18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbar.Children.Add(new TextBlock
        {
            Text = "建筑建模   平面编辑   立面剖面   图纸发布",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _undo.Click += async (_, _) => { if (_session.Undo()) await RefreshModelAsync("已撤销"); };
        _redo.Click += async (_, _) => { if (_session.Redo()) await RefreshModelAsync("已重做"); };
        toolbar.Children.Add(_undo);
        toolbar.Children.Add(_redo);
        var open = new Button { Content = "打开模型" };
        open.Click += async (_, _) => await OpenModelAsync();
        var save = new Button { Content = "保存" };
        save.Click += async (_, _) => await SaveModelAsync(false);
        var saveAs = new Button { Content = "另存为" };
        saveAs.Click += async (_, _) => await SaveModelAsync(true);
        var resetView = new Button { Content = "视图复位" };
        resetView.Click += (_, _) => _viewport.ResetView();
        toolbar.Children.Add(open);
        toolbar.Children.Add(save);
        toolbar.Children.Add(saveAs);
        _publish.Click += async (_, _) => await PublishViewsAsync();
        toolbar.Children.Add(_publish);
        toolbar.Children.Add(resetView);
        Grid.SetColumnSpan(toolbar, 3);
        root.Children.Add(toolbar);

        var tree = new Grid { RowDefinitions = new RowDefinitions("46,*"), Margin = new Thickness(12) };
        tree.Children.Add(new TextBlock
        {
            Text = "项目构件", FontSize = 20, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _elements.SelectionChanged += (_, _) =>
        {
            _selectedId = (_elements.SelectedItem as ElementItem)?.Id;
            _viewport.SelectElement(_selectedId);
            RefreshProperties();
        };
        Grid.SetRow(_elements, 1);
        tree.Children.Add(_elements);
        Grid.SetRow(tree, 1);
        root.Children.Add(tree);

        Grid.SetRow(_viewport, 1);
        Grid.SetColumn(_viewport, 1);
        root.Children.Add(_viewport);

        var propertyScroll = new ScrollViewer { Content = _properties };
        Grid.SetRow(propertyScroll, 1);
        Grid.SetColumn(propertyScroll, 2);
        root.Children.Add(propertyScroll);

        _status.Foreground = new SolidColorBrush(Color.Parse("#A4B8CF"));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(14, 0);
        _status.Text = "左键选择/旋转 · 中键或右键平移 · 滚轮缩放 · 毫米单位";
        Grid.SetRow(_status, 2);
        Grid.SetColumnSpan(_status, 3);
        root.Children.Add(_status);
        Content = root;

        BuildElementList();
        SelectById("1F-S");
        RefreshHistoryButtons();
        UpdateTitle();
        Closing += OnClosing;
        ConfigureSmokeAndSnapshot();
    }

    private void UpdateTitle()
    {
        Title = $"万落建筑模型 · {(_filePath == null ? "未命名样例" : Path.GetFileName(_filePath))}{(HasChanges ? " *" : "")}";
    }

    private async Task OpenModelAsync()
    {
        if (!await ConfirmSavedAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开建筑模型",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("建筑模型 JSON") { Patterns = new[] { "*.json" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path == null) { _status.Text = "当前只支持本机文件路径。"; return; }
        try
        {
            var loaded = await Task.Run(() =>
            {
                var session = new BuildingModelEditSession(BuildingModelJson.LoadModel(path));
                return (session, volume: BuildingVolumeBuilder.Build(session.Model));
            });
            ++_sceneGeneration;
            _session = loaded.session;
            _filePath = path;
            _savedJson = BuildingModelJson.ToJson(_session.Model);
            _viewport.SetVolume(loaded.volume);
            _viewport.ResetView();
            BuildElementList();
            SelectById(_items.FirstOrDefault()?.Id);
            RefreshHistoryButtons();
            UpdateTitle();
            _status.Text = "已打开 " + path;
        }
        catch (Exception ex) { _status.Text = "打开失败：" + ex.Message; }
    }

    private async Task<bool> SaveModelAsync(bool saveAs)
    {
        var path = saveAs ? null : _filePath;
        if (path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存建筑模型",
                SuggestedFileName = _filePath == null ? "model.json" : Path.GetFileName(_filePath),
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("建筑模型 JSON") { Patterns = new[] { "*.json" } } }
            });
            if (file == null) return false;
            path = file.TryGetLocalPath();
            if (path == null) { _status.Text = "当前只支持保存到本机文件路径。"; return false; }
        }
        try
        {
            var model = _session.Model;
            await Task.Run(() => BuildingModelJson.SaveModel(path, model));
            _filePath = path;
            _savedJson = BuildingModelJson.ToJson(model);
            UpdateTitle();
            _status.Text = "已保存 " + path;
            return true;
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; return false; }
    }

    private async Task PublishViewsAsync()
    {
        if (_filePath == null || !string.Equals(Path.GetFileName(_filePath), "model.json", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "先用「另存为」将模型保存为项目的 建筑模型/<名称>/model.json。";
            return;
        }
        if (HasChanges && !await SaveModelAsync(false)) return;
        var path = _filePath;
        var model = _session.Model;
        _publish.IsEnabled = false;
        _status.Text = "正在生成立面、剖面、平面与图纸视图…";
        try
        {
            var count = await Task.Run(() => BuildingModelViewPublisher.Publish(path, model));
            _status.Text = HasChanges
                ? $"已生成 {count} 张视图，但生成期间模型又有修改，请再次生成。"
                : $"已生成 {count} 张 CAD 视图 → {Path.Combine(Path.GetDirectoryName(path)!, "views")}";
        }
        catch (Exception ex) { _status.Text = "生成视图失败：" + ex.Message; }
        finally { _publish.IsEnabled = true; }
    }

    private async Task<bool> ConfirmSavedAsync()
    {
        if (!HasChanges) return true;
        var dialog = new Window
        {
            Title = "未保存的修改", Width = 390, Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 18 };
        content.Children.Add(new TextBlock { Text = "模型有未保存的修改，是否先保存？" });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var choice in new[] { ("保存", "save"), ("放弃修改", "discard"), ("取消", "cancel") })
        {
            var button = new Button { Content = choice.Item1 };
            button.Click += (_, _) => dialog.Close(choice.Item2);
            actions.Children.Add(button);
        }
        content.Children.Add(actions);
        dialog.Content = content;
        var result = await dialog.ShowDialog<string?>(this);
        if (result == "discard") return true;
        return result == "save" && await SaveModelAsync(false);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !HasChanges) return;
        e.Cancel = true;
        if (await ConfirmSavedAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void BuildElementList()
    {
        var model = _session.Model;
        _elements.ItemsSource = null;
        _items.Clear();
        foreach (var floor in model.Storeys)
        {
            foreach (var wall in model.Walls.Where(x => x.StoreyId == floor.Id))
                _items.Add(new ElementItem { Id = wall.Id, Label = $"{floor.Name} · 墙  {wall.Id}" });
            foreach (var opening in model.Openings.Where(x =>
                model.Walls.Any(w => w.Id == x.HostWallId && w.StoreyId == floor.Id)))
                _items.Add(new ElementItem { Id = opening.Id, Label = $"{floor.Name} · {opening.Kind}  {opening.Id}" });
            foreach (var slab in model.Slabs.Where(x => x.StoreyId == floor.Id))
                _items.Add(new ElementItem { Id = slab.Id, Label = $"{floor.Name} · 楼板  {slab.Id}" });
            foreach (var column in model.Columns.Where(x => x.StoreyId == floor.Id))
                _items.Add(new ElementItem { Id = column.Id, Label = $"{floor.Name} · 柱  {column.Id}" });
            foreach (var stair in model.Stairs.Where(x => x.StoreyId == floor.Id))
                _items.Add(new ElementItem { Id = stair.Id, Label = $"{floor.Name} · 楼梯  {stair.Id}" });
            foreach (var roof in model.Roofs.Where(x => x.StoreyId == floor.Id))
                _items.Add(new ElementItem { Id = roof.Id, Label = $"{floor.Name} · 屋面  {roof.Id}" });
        }
        _elements.ItemsSource = _items;
    }

    private void SelectById(string? id)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        if (!ReferenceEquals(_elements.SelectedItem, item))
        {
            _elements.SelectedItem = item;
            return;
        }
        _selectedId = item?.Id;
        _viewport.SelectElement(_selectedId);
        RefreshProperties();
    }

    private void RefreshProperties()
    {
        _properties.Children.Clear();
        _properties.Children.Add(new TextBlock { Text = "属性", FontSize = 20, FontWeight = FontWeight.Bold });
        if (_selectedId == null)
        {
            _properties.Children.Add(new TextBlock { Text = "点击视口中的构件或从左侧列表选择。", TextWrapping = TextWrapping.Wrap });
            return;
        }
        _properties.Children.Add(new TextBlock { Text = "构件 ID  " + _selectedId, TextWrapping = TextWrapping.Wrap });
        var wall = _session.Model.Walls.FirstOrDefault(x => x.Id == _selectedId);
        if (wall != null)
        {
            var length = Math.Sqrt(Math.Pow(wall.X2 - wall.X1, 2) + Math.Pow(wall.Y2 - wall.Y1, 2));
            _properties.Children.Add(new TextBlock { Text = "墙长（mm）" });
            var field = new TextBox { Text = length.ToString("0.##", CultureInfo.InvariantCulture) };
            _properties.Children.Add(field);
            _properties.Children.Add(new TextBlock { Text = $"厚度 {wall.Thickness:0.##} mm · 楼层 {wall.StoreyId}" });
            var apply = new Button { Content = "应用墙长" };
            apply.Click += async (_, _) => await ApplyNumberAsync(field.Text, value =>
            {
                var success = _session.TrySetWallLength(wall.Id, value, out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
            return;
        }
        var opening = _session.Model.Openings.FirstOrDefault(x => x.Id == _selectedId);
        if (opening != null)
        {
            _properties.Children.Add(new TextBlock { Text = $"{opening.Kind} · 宿主墙 {opening.HostWallId}", TextWrapping = TextWrapping.Wrap });
            _properties.Children.Add(new TextBlock { Text = "沿墙定位（mm）" });
            var field = new TextBox { Text = opening.Offset.ToString("0.##", CultureInfo.InvariantCulture) };
            _properties.Children.Add(field);
            _properties.Children.Add(new TextBlock { Text = $"宽 {opening.Width:0.##} · 高 {opening.Height:0.##} mm" });
            var apply = new Button { Content = "应用窗位" };
            apply.Click += async (_, _) => await ApplyNumberAsync(field.Text, value =>
            {
                var success = _session.TrySetOpeningOffset(opening.Id, value, out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
        }
        else _properties.Children.Add(new TextBlock { Text = "此构件当前只支持选择。" });
    }

    private async Task ApplyNumberAsync(string? text, Func<double, (bool success, string? error)> edit)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            _status.Text = "请输入有效的毫米数值。";
            return;
        }
        var result = edit(value);
        if (!result.success) { _status.Text = result.error; return; }
        await RefreshModelAsync("已更新构件 " + _selectedId);
    }

    private async Task RefreshModelAsync(string message)
    {
        var generation = ++_sceneGeneration;
        var model = _session.Model;
        RefreshProperties();
        RefreshHistoryButtons();
        UpdateTitle();
        _status.Text = message + " · 正在重建三维视图…";
        try
        {
            var volume = await Task.Run(() => BuildingVolumeBuilder.Build(model));
            if (generation != _sceneGeneration) return;
            _viewport.SetVolume(volume);
            _status.Text = $"{message} · 修订 {_session.Revision}{(HasChanges ? " · 未保存" : "")}";
        }
        catch (Exception ex)
        {
            if (generation == _sceneGeneration) _status.Text = "三维重建失败：" + ex.Message;
        }
    }

    private void RefreshHistoryButtons()
    {
        _undo.IsEnabled = _session.CanUndo;
        _redo.IsEnabled = _session.CanRedo;
    }

    private void ConfigureSmokeAndSnapshot()
    {
        if (!Program.Smoke && Program.SnapshotPath == null) return;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += async (_, _) =>
        {
            if (!_viewport.FrameRendered && DateTime.UtcNow - started < TimeSpan.FromSeconds(12)) return;
            timer.Stop();
            if (Program.SnapshotPath != null && _viewport.FrameRendered)
            {
                await Task.Delay(200);
                var visual = ElementComposition.GetElementVisual(this);
                if (visual == null) throw new InvalidOperationException("Composition visual unavailable");
                var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                snapshot.Save(Program.SnapshotPath, PngBitmapEncoderOptions.Default);
                Console.WriteLine("AVALONIA_SNAPSHOT " + Program.SnapshotPath);
            }
            var hit = _viewport.FrameRendered
                ? _viewport.PickAt(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2)) : null;
            var success = _viewport.FrameRendered && hit != null;
            Environment.ExitCode = success ? 0 : 1;
            Console.WriteLine(success ? "AVALONIA_GPU_PICK_OK " + hit : "AVALONIA_GPU_OR_PICK_FAILED");
            Close();
        };
        Opened += (_, _) => timer.Start();
    }
}
