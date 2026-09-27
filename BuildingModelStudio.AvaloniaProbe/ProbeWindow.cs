using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    private sealed class StoreyItem
    {
        public string Id = "";
        public string Name = "";
        public override string ToString() => Name;
    }

    private BuildingModelEditSession _session;
    private readonly ModelViewport _viewport;
    private readonly PlanEditorCanvas _planCanvas = new();
    private readonly TabControl _workspaces = new();
    private readonly ComboBox _storeyChooser = new() { Width = 130 };
    private readonly ListBox _elements = new();
    private readonly List<ElementItem> _items = new();
    private readonly StackPanel _properties = new() { Margin = new Thickness(16), Spacing = 12 };
    private readonly TextBlock _status = new();
    private readonly Button _undo = new() { Content = "撤销" };
    private readonly Button _redo = new() { Content = "重做" };
    private readonly Button _publish = new() { Content = "生成 CAD 视图" };
    private readonly Button _sendToCad = new() { Content = "推到 CAD" };
    private CancellationTokenSource? _publishCancellation;
    private string? _selectedId;
    private string? _filePath;
    private string _savedJson;
    private bool _closeConfirmed;
    private bool _refreshingStoreys;
    private int _sceneGeneration;
    private bool HasChanges => BuildingModelJson.ToJson(_session.Model) != _savedJson;

    public ProbeWindow()
    {
        Title = "万落建筑模型";
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 520;
        _session = new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
        _savedJson = BuildingModelJson.ToJson(_session.Model);
        _viewport = new ModelViewport(BuildingVolumeBuilder.Build(_session.Model));
        _viewport.ElementPicked += SelectById;
        _planCanvas.ElementPicked += SelectById;
        _planCanvas.SnapChanged += kind =>
        {
            if (kind != PlanEditing.SnapNone) _status.Text = "捕捉：" + kind;
        };
        _planCanvas.WallRequested += (start, end) =>
        {
            if (!_session.TryAddWall(new WallModel
            {
                StoreyId = (_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F",
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Thickness = 240
            }, out var id, out var error)) { _status.Text = error; return false; }
            _selectedId = id;
            _ = RefreshModelAsync("已新增墙");
            return true;
        };
        _planCanvas.WallGripReleased += async (id, index, position) =>
        {
            var wall = _session.Model.Walls.FirstOrDefault(w => w.Id == id);
            if (wall == null) return;
            var x1 = index == 0 ? position.X : wall.X1;
            var y1 = index == 0 ? position.Y : wall.Y1;
            var x2 = index == 1 ? position.X : wall.X2;
            var y2 = index == 1 ? position.Y : wall.Y2;
            if (!_session.TrySetWallEndpoints(id, x1, y1, x2, y2, out var error))
            { _status.Text = error; return; }
            _selectedId = id;
            await RefreshModelAsync("已调整墙端点");
        };
        _planCanvas.OpeningRequested += async (kind, wallId, offset) =>
        {
            var opening = PlanEditing.CreateOpening(kind, wallId, offset);
            if (!_session.TryAddOpening(opening, out var id, out var error))
            { _status.Text = error; return; }
            _selectedId = id;
            await RefreshModelAsync("已新增" + kind);
        };

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
        _publish.Click += async (_, _) => await PublishViewsAsync(false);
        _sendToCad.Click += async (_, _) => await PublishViewsAsync(true);
        toolbar.Children.Add(_publish);
        toolbar.Children.Add(_sendToCad);
        var cancelPublish = new Button { Content = "取消生成" };
        cancelPublish.Click += (_, _) => _publishCancellation?.Cancel();
        toolbar.Children.Add(cancelPublish);
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
            _planCanvas.SetSelection(_selectedId);
            RefreshProperties();
        };
        Grid.SetRow(_elements, 1);
        tree.Children.Add(_elements);
        Grid.SetRow(tree, 1);
        root.Children.Add(tree);

        var viewportHost = new Grid();
        viewportHost.Children.Add(_viewport);
        var viewportInput = new Border { Background = Brushes.Transparent };
        viewportInput.PointerPressed += (_, e) =>
        {
            var buttons = e.GetCurrentPoint(viewportInput).Properties;
            var selecting = buttons.IsLeftButtonPressed;
            var panning = buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed;
            if (!selecting && !panning) return;
            _viewport.BeginInteraction(e.GetPosition(_viewport), selecting, panning);
            e.Pointer.Capture(viewportInput);
            e.Handled = true;
        };
        viewportInput.PointerMoved += (_, e) =>
        {
            var buttons = e.GetCurrentPoint(viewportInput).Properties;
            _viewport.MoveInteraction(e.GetPosition(_viewport), buttons.IsLeftButtonPressed,
                buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed);
        };
        viewportInput.PointerReleased += (_, e) =>
        {
            _viewport.EndInteraction(e.GetPosition(_viewport));
            if (e.Pointer.Captured == viewportInput) e.Pointer.Capture(null);
            e.Handled = true;
        };
        viewportInput.PointerWheelChanged += (_, e) =>
        {
            _viewport.Zoom(e.Delta.Y);
            e.Handled = true;
        };
        viewportHost.Children.Add(viewportInput);

        var planLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var planTools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8)
        };
        planTools.Children.Add(new TextBlock { Text = "楼层", VerticalAlignment = VerticalAlignment.Center });
        _storeyChooser.SelectionChanged += (_, _) =>
        {
            if (!_refreshingStoreys && _storeyChooser.SelectedItem is StoreyItem floor)
                _planCanvas.SetStorey(floor.Id);
        };
        planTools.Children.Add(_storeyChooser);
        var wallLength = new TextBox { Width = 100, PlaceholderText = "墙长 mm" };
        wallLength.KeyDown += (sender, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (!double.TryParse(wallLength.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var length))
                _status.Text = "请输入有效的墙长（mm）。";
            else if (!_planCanvas.TryDrawWallLength(length, out var error)) _status.Text = error;
            else { wallLength.Text = ""; _planCanvas.Focus(); }
            e.Handled = true;
        };
        foreach (var (label, tool) in new[]
        {
            ("选择", PlanTool.Select), ("画墙", PlanTool.Wall),
            ("放门", PlanTool.Door), ("放窗", PlanTool.Window)
        })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) =>
            {
                _planCanvas.Tool = tool;
                _planCanvas.CancelDraft();
                _status.Text = tool == PlanTool.Wall ? "画墙：连续点墙轴线端点，Esc 结束。"
                    : tool == PlanTool.Select ? "点击墙或门窗选择构件。"
                    : "请点选宿主墙上的位置放" + label.Substring(1) + "。";
                _planCanvas.Focus();
            };
            planTools.Children.Add(button);
        }
        planTools.Children.Add(wallLength);
        var delete = new Button { Content = "删除选中" };
        delete.Click += async (_, _) =>
        {
            if (!_session.TryDeleteElement(_selectedId ?? "", out var error))
            { _status.Text = error; return; }
            _selectedId = null;
            await RefreshModelAsync("已删除构件");
        };
        planTools.Children.Add(delete);
        var fitPlan = new Button { Content = "缩放适应" };
        fitPlan.Click += (_, _) => _planCanvas.Fit();
        planTools.Children.Add(fitPlan);
        planLayout.Children.Add(planTools);
        Grid.SetRow(_planCanvas, 1);
        planLayout.Children.Add(_planCanvas);

        _workspaces.Items.Add(new TabItem { Header = "建筑建模", Content = viewportHost });
        _workspaces.Items.Add(new TabItem { Header = "平面编辑", Content = planLayout });
        _workspaces.Items.Add(new TabItem { Header = "立面编辑", IsEnabled = false });
        _workspaces.Items.Add(new TabItem { Header = "剖面编辑", IsEnabled = false });
        _workspaces.Items.Add(new TabItem { Header = "图纸发布", IsEnabled = false });
        Grid.SetRow(_workspaces, 1);
        Grid.SetColumn(_workspaces, 1);
        root.Children.Add(_workspaces);

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
        RefreshStoreys();
        SelectById("1F-S");
        RefreshHistoryButtons();
        UpdateTitle();
        Closing += OnClosing;
        Closed += (_, _) => _publishCancellation?.Cancel();
        if (Program.GpuBenchCount > 0) Opened += async (_, _) => await RunGpuBenchmarkAsync(Program.GpuBenchCount);
        else if (Program.ModelPath == null) ConfigureSmokeAndSnapshot();
        else Opened += async (_, _) =>
        {
            if (await OpenStartupModelAsync(Program.ModelPath)) ConfigureSmokeAndSnapshot();
            else if (Program.Smoke) { Program.SmokeFailed = true; Close(); }
        };
    }

    private async Task<bool> OpenStartupModelAsync(string path)
    {
        if (Program.CreateMissingProjectModel && !File.Exists(path))
        {
            try
            {
                var name = Program.ProjectModelName ?? "建筑模型";
                await Task.Run(() =>
                {
                    // Do not replace a model another process created after the existence check.
                    if (!File.Exists(path))
                        BuildingModelJson.SaveModel(path, SampleModelFactory.CreateEmptyModel(name));
                });
            }
            catch (Exception ex)
            {
                _status.Text = "新建项目模型失败：" + ex.Message;
                return false;
            }
        }
        return await LoadModelAsync(path);
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
        await LoadModelAsync(path);
    }

    private async Task<bool> LoadModelAsync(string path)
    {
        var generation = ++_sceneGeneration;
        _status.Text = "正在后台打开模型…";
        try
        {
            var loaded = await Task.Run(() =>
            {
                var session = new BuildingModelEditSession(BuildingModelJson.LoadModel(path));
                return (session, scene: ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(session.Model)));
            });
            if (generation != _sceneGeneration) return false;
            _session = loaded.session;
            _filePath = path;
            _savedJson = BuildingModelJson.ToJson(_session.Model);
            _viewport.SetScene(loaded.scene);
            _viewport.ResetView();
            BuildElementList();
            RefreshStoreys();
            _planCanvas.Fit();
            SelectById(_items.FirstOrDefault()?.Id);
            RefreshHistoryButtons();
            UpdateTitle();
            _status.Text = "已打开 " + path;
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "打开失败：" + ex.Message;
            if (Program.Smoke) Console.Error.WriteLine(_status.Text);
            return false;
        }
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
            var snapshotJson = BuildingModelJson.ToJson(_session.Model);
            await Task.Run(() => BuildingModelJson.SaveModel(path, BuildingModelJson.FromJson(snapshotJson)));
            _filePath = path;
            _savedJson = snapshotJson;
            UpdateTitle();
            var current = BuildingModelJson.ToJson(_session.Model) == snapshotJson;
            _status.Text = current ? "已保存 " + path : "保存期间模型又有修改；当前版本仍需保存。";
            return current;
        }
        catch (Exception ex) { _status.Text = "保存失败：" + ex.Message; return false; }
    }

    private async Task PublishViewsAsync(bool markForCad)
    {
        if (_publishCancellation != null) return;
        if (_filePath == null || !string.Equals(Path.GetFileName(_filePath), "model.json", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "先用「另存为」将模型保存为项目的 建筑模型/<名称>/model.json。";
            return;
        }
        if (HasChanges && !await SaveModelAsync(false)) return;
        var path = _filePath;
        var session = _session;
        var revision = session.Revision;
        var snapshotJson = BuildingModelJson.ToJson(session.Model);
        var modelFolder = Path.GetDirectoryName(path)!;
        var cancellation = new CancellationTokenSource();
        _publishCancellation = cancellation;
        string? staging = null;
        _publish.IsEnabled = false;
        _sendToCad.IsEnabled = false;
        _status.Text = "正在生成立面、剖面、平面与图纸视图…";
        try
        {
            var result = await Task.Run(() =>
            {
                var token = cancellation.Token;
                var model = BuildingModelJson.FromJson(snapshotJson);
                var libraryPath = Path.Combine(modelFolder, "openings.json");
                var library = File.Exists(libraryPath)
                    ? BuildingModelJson.LoadOpeningLibrary(libraryPath) : null;
                var views = BuildingModelViewPublisher.Generate(model, library, token.ThrowIfCancellationRequested);
                var stage = Path.Combine(modelFolder, ".views-staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(stage);
                    foreach (var view in views)
                    {
                        token.ThrowIfCancellationRequested();
                        BuildingModelJson.SaveView(Path.Combine(stage, view.Id + ".json"), view);
                    }
                    token.ThrowIfCancellationRequested();
                    return (folder: stage, views);
                }
                catch
                {
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                    throw;
                }
            }, cancellation.Token);
            staging = result.folder;
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_session, session)
                || session.Revision != revision || _filePath != path
                || BuildingModelJson.ToJson(session.Model) != snapshotJson)
            {
                _status.Text = "生成期间模型或项目已变化，旧结果已丢弃。";
                return;
            }
            var viewsFolder = Path.Combine(modelFolder, StudioLaunch.ViewsFolderName);
            var chosen = markForCad
                ? result.views.Where(view => view.Kind == ViewKind.Sheet).ToArray() : Array.Empty<ViewDocument>();
            if (markForCad && chosen.Length == 0) chosen = result.views.ToArray();
            if (markForCad)
            {
                if (!StudioLaunch.WritePendingFile(Path.Combine(staging, StudioLaunch.PendingFileName),
                    chosen.Select(view => new StudioPendingEntry
                    { Id = view.Id, FilePath = Path.Combine(viewsFolder, view.Id + ".json") })))
                    throw new IOException("写入 CAD 待落图清单失败。");
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var oldBackup = StudioLaunch.CommitStagedViews(modelFolder, staging);
            staging = null;
            if (markForCad)
                _status.Text = $"已生成 {result.views.Count} 张视图，待落图 {chosen.Length} 张；回到 CAD 执行 LTTZ。";
            else _status.Text = $"已生成 {result.views.Count} 张 CAD 视图 → {viewsFolder}";
            if (oldBackup != null) _status.Text += " 旧视图备份保留在：" + oldBackup;
        }
        catch (OperationCanceledException) { _status.Text = "视图生成已取消。"; }
        catch (Exception ex) { _status.Text = "生成视图失败：" + ex.Message; }
        finally
        {
            if (staging != null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); }
                catch (Exception ex) { _status.Text += " 临时文件清理失败：" + ex.Message; }
            }
            if (ReferenceEquals(_publishCancellation, cancellation)) _publishCancellation = null;
            cancellation.Dispose();
            _publish.IsEnabled = true;
            _sendToCad.IsEnabled = true;
        }
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
        var wallsByStorey = model.Walls.GroupBy(x => x.StoreyId).ToDictionary(x => x.Key, x => x.ToList());
        var wallStoreys = model.Walls.ToDictionary(x => x.Id, x => x.StoreyId);
        var openingsByStorey = model.Openings.Where(x => wallStoreys.ContainsKey(x.HostWallId))
            .GroupBy(x => wallStoreys[x.HostWallId]).ToDictionary(x => x.Key, x => x.ToList());
        var slabsByStorey = model.Slabs.GroupBy(x => x.StoreyId).ToDictionary(x => x.Key, x => x.ToList());
        var columnsByStorey = model.Columns.GroupBy(x => x.StoreyId).ToDictionary(x => x.Key, x => x.ToList());
        var stairsByStorey = model.Stairs.GroupBy(x => x.StoreyId).ToDictionary(x => x.Key, x => x.ToList());
        var roofsByStorey = model.Roofs.GroupBy(x => x.StoreyId).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var floor in model.Storeys)
        {
            if (!wallsByStorey.TryGetValue(floor.Id, out var walls)) walls = new();
            foreach (var wall in walls)
                _items.Add(new ElementItem { Id = wall.Id, Label = $"{floor.Name} · 墙  {wall.Id}" });
            if (!openingsByStorey.TryGetValue(floor.Id, out var openings)) openings = new();
            foreach (var opening in openings)
                _items.Add(new ElementItem { Id = opening.Id, Label = $"{floor.Name} · {opening.Kind}  {opening.Id}" });
            if (!slabsByStorey.TryGetValue(floor.Id, out var slabs)) slabs = new();
            foreach (var slab in slabs)
                _items.Add(new ElementItem { Id = slab.Id, Label = $"{floor.Name} · 楼板  {slab.Id}" });
            if (!columnsByStorey.TryGetValue(floor.Id, out var columns)) columns = new();
            foreach (var column in columns)
                _items.Add(new ElementItem { Id = column.Id, Label = $"{floor.Name} · 柱  {column.Id}" });
            if (!stairsByStorey.TryGetValue(floor.Id, out var stairs)) stairs = new();
            foreach (var stair in stairs)
                _items.Add(new ElementItem { Id = stair.Id, Label = $"{floor.Name} · 楼梯  {stair.Id}" });
            if (!roofsByStorey.TryGetValue(floor.Id, out var roofs)) roofs = new();
            foreach (var roof in roofs)
                _items.Add(new ElementItem { Id = roof.Id, Label = $"{floor.Name} · 屋面  {roof.Id}" });
        }
        _elements.ItemsSource = _items;
    }

    private void RefreshStoreys()
    {
        var previous = (_storeyChooser.SelectedItem as StoreyItem)?.Id;
        _refreshingStoreys = true;
        try
        {
        _storeyChooser.ItemsSource = _session.Model.Storeys.Select(s => new StoreyItem
        { Id = s.Id, Name = s.Name + "  (" + s.Elevation.ToString("0") + " mm)" }).ToList();
        _storeyChooser.SelectedItem = (_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?
            .FirstOrDefault(s => s.Id == previous) ?? (_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?.FirstOrDefault();
        }
        finally { _refreshingStoreys = false; }
        _planCanvas.SetModel(_session.Model, (_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
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
        _planCanvas.SetSelection(_selectedId);
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
            _properties.Children.Add(new TextBlock { Text = $"楼层 {wall.StoreyId} · 墙高 0 表示随楼层" });
            var lengthField = AddNumberField("墙长（mm）", length);
            var thicknessField = AddNumberField("墙厚（mm）", wall.Thickness);
            var heightField = AddNumberField("墙高（mm）", wall.Height);
            var apply = new Button { Content = "应用墙体参数" };
            apply.Click += async (_, _) => await ApplyGeometryAsync(
                new[] { lengthField, thicknessField, heightField }, values =>
            {
                var success = _session.TrySetWallGeometry(wall.Id, values[0], values[1], values[2], out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
            return;
        }
        var opening = _session.Model.Openings.FirstOrDefault(x => x.Id == _selectedId);
        if (opening != null)
        {
            _properties.Children.Add(new TextBlock { Text = $"{opening.Kind} · 宿主墙 {opening.HostWallId}", TextWrapping = TextWrapping.Wrap });
            var offsetField = AddNumberField("沿墙中心定位（mm）", opening.Offset);
            var widthField = AddNumberField("洞口宽（mm）", opening.Width);
            var heightField = AddNumberField("洞口高（mm）", opening.Height);
            var sillField = AddNumberField("窗台高（mm）", opening.Sill);
            var apply = new Button { Content = "应用门窗参数" };
            apply.Click += async (_, _) => await ApplyGeometryAsync(
                new[] { offsetField, widthField, heightField, sillField }, values =>
            {
                var success = _session.TrySetOpeningGeometry(opening.Id, values[0], values[1], values[2], values[3], out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
        }
        else _properties.Children.Add(new TextBlock { Text = "此构件当前只支持选择。" });
    }

    private TextBox AddNumberField(string label, double value)
    {
        _properties.Children.Add(new TextBlock { Text = label });
        var field = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture) };
        _properties.Children.Add(field);
        return field;
    }

    private async Task ApplyGeometryAsync(IReadOnlyList<TextBox> fields,
        Func<double[], (bool success, string? error)> edit)
    {
        var values = new double[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var text = fields[i].Text;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out values[i])
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                _status.Text = $"第 {i + 1} 个参数不是有效的毫米数值。";
                return;
            }
        }
        var result = edit(values);
        if (!result.success) { _status.Text = result.error; return; }
        await RefreshModelAsync("已更新构件 " + _selectedId);
    }

    private async Task RefreshModelAsync(string message)
    {
        var generation = ++_sceneGeneration;
        var model = _session.Model;
        var selected = _selectedId;
        BuildElementList();
        RefreshStoreys();
        SelectById(selected);
        RefreshProperties();
        RefreshHistoryButtons();
        UpdateTitle();
        _status.Text = message + " · 正在重建三维视图…";
        try
        {
            var scene = await Task.Run(() => ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(model)));
            if (generation != _sceneGeneration) return;
            _viewport.SetScene(scene);
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

    private async Task RunGpuBenchmarkAsync(int count)
    {
        try
        {
            _status.Text = $"正在准备 {count} 道墙的 GPU 测试场景…";
            var prepared = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var model = Program.CreateBenchmarkModel(count);
                var scene = ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(model));
                return (model, scene, preparationMs: watch.ElapsedMilliseconds);
            });
            _session = new BuildingModelEditSession(prepared.model);
            _savedJson = BuildingModelJson.ToJson(prepared.model);
            var listWatch = Stopwatch.StartNew();
            BuildElementList();
            var listMs = listWatch.ElapsedMilliseconds;
            var previousFrame = _viewport.RenderedFrameCount;
            var uploadStart = Stopwatch.GetTimestamp();
            _viewport.SetScene(prepared.scene);
            await WaitForFrameAsync(prepared.scene.VertexCount, previousFrame);
            var firstFrameMs = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            var uploadGpuMs = _viewport.LastSynchronizedFrameMs;
            var responseTimes = new double[60];
            var gpuTimes = new double[60];
            for (var i = 0; i < responseTimes.Length; i++)
            {
                previousFrame = _viewport.RenderedFrameCount;
                var requestStart = Stopwatch.GetTimestamp();
                _viewport.RotateForBenchmark(0.035f);
                await WaitForFrameAsync(prepared.scene.VertexCount, previousFrame);
                responseTimes[i] = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                gpuTimes[i] = _viewport.LastSynchronizedFrameMs;
            }
            Array.Sort(responseTimes);
            Array.Sort(gpuTimes);
            var managedMb = GC.GetTotalMemory(true) / 1048576d;
            Console.WriteLine($"GPU_BENCH renderer={_viewport.GpuRenderer} elements={count} vertices={prepared.scene.VertexCount} "
                + $"prepareMs={prepared.preparationMs} listMs={listMs} firstFrameMs={firstFrameMs:0.###} "
                + $"uploadAndDrawMs={uploadGpuMs:0.###} rotationResponseP95Ms={responseTimes[56]:0.###} "
                + $"rotationGpuP95Ms={gpuTimes[56]:0.###} managedMB={managedMb:0.0}");
        }
        catch (Exception ex)
        {
            Program.SmokeFailed = true;
            Console.Error.WriteLine("GPU_BENCH_FAILED " + ex);
        }
        finally { Close(); }
    }

    private async Task WaitForFrameAsync(int expectedVertices, long afterFrame)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_viewport.RenderedFrameCount > afterFrame
                && _viewport.LastRenderedVertexCount == expectedVertices)
            {
                if (!_viewport.FrameRendered) throw new InvalidOperationException("OpenGL 返回错误或没有绘制模型。");
                return;
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("等待 GPU 视口绘制超时。");
    }

    private void ConfigureSmokeAndSnapshot()
    {
        if (!Program.Smoke && Program.SnapshotPath == null) return;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += async (_, _) =>
        {
            var emptyProject = Program.CreateMissingProjectModel && _session.Model.Walls.Count == 0
                && _session.Model.Columns.Count == 0 && _session.Model.Slabs.Count == 0;
            if (!_viewport.FrameRendered && !emptyProject && DateTime.UtcNow - started < TimeSpan.FromSeconds(12)) return;
            timer.Stop();
            if (Program.SnapshotPath != null && (_viewport.FrameRendered || emptyProject))
            {
                if (Program.SnapshotPlan) _workspaces.SelectedIndex = 1;
                await Task.Delay(200);
                var visual = ElementComposition.GetElementVisual(this);
                if (visual == null) throw new InvalidOperationException("Composition visual unavailable");
                var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                snapshot.Save(Program.SnapshotPath, PngBitmapEncoderOptions.Default);
                Console.WriteLine("AVALONIA_SNAPSHOT " + Program.SnapshotPath);
            }
            var hit = _viewport.FrameRendered
                ? _viewport.PickAt(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2)) : null;
            var success = emptyProject ? _filePath == Program.ModelPath && File.Exists(_filePath)
                : _viewport.FrameRendered && hit != null;
            Program.SmokeFailed = !success;
            Console.WriteLine(success ? (emptyProject ? "AVALONIA_EMPTY_PROJECT_OK " + _filePath
                : "AVALONIA_GPU_PICK_OK " + hit) : "AVALONIA_GPU_OR_PICK_FAILED");
            Close();
        };
        if (IsVisible) timer.Start();
        else Opened += (_, _) => timer.Start();
    }
}
