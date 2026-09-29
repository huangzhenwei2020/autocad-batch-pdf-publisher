using System.Collections.Concurrent;
using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.Interactivity;
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
    private readonly ViewportTransformOverlay _gizmo;
    private readonly PlanEditorCanvas _planCanvas = new();
    private readonly TabControl _workspaces = new();
    private readonly ComboBox _storeyChooser = new() { Width = 130 };
    private readonly TextBox _newWallHeight = new() { Width = 110, Text = "0", PlaceholderText = "墙高 mm" };
    private readonly ListBox _elements = new();
    private readonly TreeView _browserTree = new();
    private readonly Dictionary<string, TreeViewItem> _browserNodes = new();
    private readonly List<ElementItem> _items = new();
    private readonly Dictionary<PlanTool, Button> _planToolButtons = new();
    private readonly Dictionary<ViewTransformTool, Button> _viewToolButtons = new();
    private readonly StackPanel _properties = new() { Margin = new Thickness(16), Spacing = 12 };
    private readonly TextBlock _status = new();
    private readonly TextBox _commandInput = new()
    { PlaceholderText = "输入命令：WA 画墙 / M 移动 / CO 复制", MinWidth = 180 };
    private readonly Button _polarButton = new() { Content = "极轴 45°：关" };
    private readonly Button _orthoButton = new() { Content = "正交 F8：关" };
    private readonly Button _polarStatusButton = new() { Content = "极轴 F10" };
    private readonly Button _orthoStatusButton = new() { Content = "正交 F8" };
    private readonly Button _undo = new() { Content = "撤销 Ctrl+Z" };
    private readonly Button _redo = new() { Content = "重做 Ctrl+Y" };
    private readonly Button _publish = new() { Content = "生成 CAD 视图" };
    private readonly Button _sendToCad = new() { Content = "推到 CAD" };
    private CancellationTokenSource? _publishCancellation;
    private string? _selectedId;
    private string? _filePath;
    private string _savedJson;
    private bool _closeConfirmed;
    private bool _refreshingStoreys;
    private bool _movingInViewport;
    private bool _copyingInViewport;
    private string _elementFilter = "";
    private PointModel? _viewportMoveBase;
    private int _sceneGeneration;
    private bool HasChanges => BuildingModelJson.ToJson(_session.Model) != _savedJson;
    private static readonly string ReleaseRevision = ReadReleaseRevision();
    private static readonly ConcurrentDictionary<string, Bitmap> IconCache = new();

    private static string ReadReleaseRevision()
    {
        try
        {
            var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "build-info.json"));
            if (!File.Exists(file)) return "dev";
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var sha = json.RootElement.GetProperty("GitCommit").GetString();
            return string.IsNullOrWhiteSpace(sha) ? "dev" : sha.Substring(0, Math.Min(7, sha.Length));
        }
        catch { return "dev"; }
    }

    private static bool TryNumber(string? text, out double value)
        => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);

    public ProbeWindow()
    {
        Title = "万落建筑模型";
        Width = 1500;
        Height = 900;
        MinWidth = 960;
        MinHeight = 620;
        if (Program.SnapshotCompact) { Width = 1000; Height = 650; }
        _session = new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
        _savedJson = BuildingModelJson.ToJson(_session.Model);
        var initialVolume = BuildingVolumeBuilder.Build(_session.Model);
        _viewport = new ModelViewport(initialVolume);
        _gizmo = new ViewportTransformOverlay(_viewport);
        _gizmo.PreviewChanged += value => _status.Text = value + " · 松开鼠标提交，Esc 取消";
        _gizmo.TransformFinished += async (id, dx, dy, angle, copy) =>
        {
            id = StandardStoreyLayout.SourceElementId(_session.Model, id);
            var entered = Program.GizmoCheck ? new MovementResult(dx, dy, angle)
                : await new MovementInputWindow(dx, dy, angle).ShowDialog<MovementResult?>(this);
            if (entered == null) return;
            if (!_session.TryTransformWall(id, entered.X, entered.Y, entered.Angle,
                copy, out var affectedId, out var error))
            { _status.Text = error; return; }
            _selectedId = affectedId;
            await RefreshModelAsync(copy ? "已复制墙及门窗" : "已变换墙");
        };
        _viewport.ElementPicked += SelectById;
        _planCanvas.ElementPicked += SelectById;
        _planCanvas.MoveStageChanged += text => _status.Text = text;
        _planCanvas.MoveRequested += async (id, from, to, copy) =>
            await FinishMoveAsync(id, from, to, copy);
        _planCanvas.SnapChanged += kind =>
        {
            if (kind != PlanEditing.SnapNone) _status.Text = "捕捉：" + kind;
        };
        _planCanvas.AxisConstraintChanged += mode => _status.Text = mode switch
        {
            PlanAxisConstraint.X => _planCanvas.OrthogonalEnabled
                ? "F8 正交：当前锁定水平方向。" : "按住 Shift：当前锁定水平方向；松开恢复自由。",
            PlanAxisConstraint.Y => _planCanvas.OrthogonalEnabled
                ? "F8 正交：当前锁定垂直方向。" : "按住 Shift：当前锁定垂直方向；松开恢复自由。",
            _ => "自由画墙。"
        };
        _planCanvas.WallRequested += (start, end) =>
        {
            if (!TryNumber(_newWallHeight.Text, out var wallHeight) || wallHeight < 0)
            { _status.Text = "新墙高度请输入非负毫米数；0 表示随楼层。"; return false; }
            if (!_session.TryAddWall(new WallModel
            {
                StoreyId = (_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F",
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Thickness = 240, Height = wallHeight
            }, out var id, out var error)) { _status.Text = error; return false; }
            _selectedId = id;
            _ = RefreshModelAsync("已新增墙");
            return true;
        };
        _planCanvas.WallGripReleased += async (id, index, position) =>
        {
            var wall = _session.Model.Walls.FirstOrDefault(w => w.Id == id);
            if (wall == null) return;
            var ox = index == 0 ? wall.X1 : wall.X2;
            var oy = index == 0 ? wall.Y1 : wall.Y2;
            var entered = await new MovementInputWindow(position.X - ox, position.Y - oy, 0)
                .ShowDialog<MovementResult?>(this);
            if (entered == null) return;
            if (!_session.TryMoveWallGripOnly(id, index, ox + entered.X, oy + entered.Y, out var error))
            { _status.Text = error; return; }
            _selectedId = id;
            await RefreshModelAsync("已调整墙交接端点");
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
            RowDefinitions = new RowDefinitions("38,116,*,88"),
            ColumnDefinitions = new ColumnDefinitions("270,*,36"),
            Background = new SolidColorBrush(Color.Parse("#101923"))
        };
        var ribbonTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3,
            Margin = new Thickness(0) };
        var ribbonContent = new ContentControl();
        var quickAccess = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3,
            Margin = new Thickness(10, 0) };
        Button Quick(string icon, string tip, Action callback)
        {
            var button = new Button { Content = QuickIcon(icon), Width = 32,
                Height = 32, Padding = new Thickness(4), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0) };
            button.Click += (_, _) => callback();
            ToolTip.SetTip(button, tip);
            return button;
        }
        quickAccess.Children.Add(Quick("folder-open", "打开模型  Ctrl+O", () => _ = OpenModelAsync()));
        quickAccess.Children.Add(Quick("save", "保存  Ctrl+S", () => _ = SaveModelAsync(false)));
        quickAccess.Children.Add(Quick("undo-2", "撤销  Ctrl+Z", () => _ = UndoModelAsync()));
        quickAccess.Children.Add(Quick("redo-2", "重做  Ctrl+Y", () => _ = RedoModelAsync()));
        var commandSearch = new TextBox { PlaceholderText = "搜索命令...",
            Margin = new Thickness(4, 3, 8, 3), MinHeight = 30 };
        commandSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ExecuteCommand(commandSearch.Text);
            commandSearch.Clear();
            e.Handled = true;
        };
        var headerLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,180") };
        headerLayout.Children.Add(quickAccess);
        Grid.SetColumn(ribbonTabs, 1);
        headerLayout.Children.Add(ribbonTabs);
        Grid.SetColumn(commandSearch, 2);
        headerLayout.Children.Add(commandSearch);
        var ribbonHeader = new Border { Background = new SolidColorBrush(Color.Parse("#19232F")),
            Child = headerLayout };
        Grid.SetColumnSpan(ribbonHeader, 3);
        root.Children.Add(ribbonHeader);
        var ribbonScroll = new ScrollViewer { Content = ribbonContent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var ribbonNavigation = new Grid { ColumnDefinitions = new ColumnDefinitions("0,*,32") };
        var ribbonLeft = new Button { Content = "‹", Padding = new Thickness(0), IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#20364A")) };
        var ribbonRight = new Button { Content = "›", Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.Parse("#20364A")) };
        void UpdateRibbonArrows()
        {
            var remaining = ribbonScroll.Extent.Width - ribbonScroll.Viewport.Width;
            var showLeft = ribbonScroll.Offset.X > 1;
            var showRight = remaining > ribbonScroll.Offset.X + 1;
            ribbonLeft.IsVisible = showLeft;
            ribbonRight.IsVisible = showRight;
            ribbonNavigation.ColumnDefinitions[0].Width = new GridLength(showLeft ? 32 : 0);
            ribbonNavigation.ColumnDefinitions[2].Width = new GridLength(showRight ? 32 : 0);
        }
        ribbonLeft.Click += (_, _) =>
        {
            ribbonScroll.Offset = new Vector(Math.Max(0, ribbonScroll.Offset.X - 300), 0);
            UpdateRibbonArrows();
        };
        ribbonRight.Click += (_, _) =>
        {
            ribbonScroll.Offset = new Vector(ribbonScroll.Offset.X + 300, 0);
            UpdateRibbonArrows();
        };
        ribbonScroll.SizeChanged += (_, _) => Dispatcher.UIThread.Post(UpdateRibbonArrows,
            DispatcherPriority.Loaded);
        ribbonScroll.ScrollChanged += (_, _) => UpdateRibbonArrows();
        ribbonNavigation.Children.Add(ribbonLeft);
        Grid.SetColumn(ribbonScroll, 1);
        ribbonNavigation.Children.Add(ribbonScroll);
        Grid.SetColumn(ribbonRight, 2);
        ribbonNavigation.Children.Add(ribbonRight);
        var ribbonSurface = new Border { Background = new SolidColorBrush(Color.Parse("#1C2835")),
            BorderBrush = new SolidColorBrush(Color.Parse("#304357")), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = ribbonNavigation };
        Grid.SetRow(ribbonSurface, 1);
        Grid.SetColumnSpan(ribbonSurface, 3);
        root.Children.Add(ribbonSurface);
        _undo.Click += async (_, _) => await UndoModelAsync();
        _redo.Click += async (_, _) => await RedoModelAsync();
        var deleteSelected = new Button { Content = "删除选中" };
        deleteSelected.Click += async (_, _) => await DeleteSelectedAsync();
        var open = new Button { Content = "打开模型" };
        open.Click += async (_, _) => await OpenModelAsync();
        var save = new Button { Content = "保存" };
        save.Click += async (_, _) => await SaveModelAsync(false);
        var saveAs = new Button { Content = "另存为" };
        saveAs.Click += async (_, _) => await SaveModelAsync(true);
        var axisSettings = new Button { Content = "轴号设置" };
        axisSettings.Click += async (_, _) => await OpenAxisSettingsAsync();
        var resetView = new Button { Content = "视图复位" };
        resetView.Click += (_, _) => ResetActiveView();
        var globalStoreys = new Button { Content = "楼层设置" };
        globalStoreys.Click += async (_, _) => await OpenStoreySettingsAsync();
        _publish.Click += async (_, _) => await PublishViewsAsync(false);
        _sendToCad.Click += async (_, _) => await PublishViewsAsync(true);
        var cancelPublish = new Button { Content = "取消生成" };
        cancelPublish.Click += (_, _) => _publishCancellation?.Cancel();

        var tree = new Grid { RowDefinitions = new RowDefinitions("42,36,*"),
            Margin = new Thickness(8, 8, 4, 8) };
        var leftHeader = new Grid();
        leftHeader.Children.Add(new TextBlock { Text = "项目浏览器", FontSize = 14,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0) });
        tree.Children.Add(leftHeader);
        var browserSearch = new TextBox { PlaceholderText = "搜索构件...", Margin = new Thickness(8, 0, 8, 5) };
        Grid.SetRow(browserSearch, 1);
        tree.Children.Add(browserSearch);
        browserSearch.TextChanged += (_, _) => FilterElementList(browserSearch.Text);
        _elements.SelectionChanged += (_, _) =>
        {
            _selectedId = (_elements.SelectedItem as ElementItem)?.Id;
            if (_selectedId != null && _browserNodes.TryGetValue(_selectedId, out var browserNode))
                browserNode.IsSelected = true;
            _viewport.SelectElement(_selectedId);
            _planCanvas.SetSelection(_selectedId);
            UpdateGizmoSelection();
            RefreshProperties();
        };
        Grid.SetRow(_browserTree, 2);
        _browserTree.Margin = new Thickness(8, 5, 8, 8);
        tree.Children.Add(_browserTree);
        var leftToggle = new Button { Width = 30, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 7, 0) };
        ToolTip.SetTip(leftToggle, "收起项目浏览器");
        var leftHost = new Grid();
        leftHost.Children.Add(tree);
        leftHost.Children.Add(leftToggle);
        leftToggle.Click += (_, _) =>
        {
            tree.IsVisible = !tree.IsVisible;
            root.ColumnDefinitions[0].Width = new GridLength(tree.IsVisible ? 270 : 38);
            leftToggle.Content = CommandIcon(tree.IsVisible ? "chevron-left" : "chevron-right", 15);
            ToolTip.SetTip(leftToggle, tree.IsVisible ? "收起项目浏览器" : "展开项目浏览器");
        };
        Grid.SetRow(leftHost, 2);
        root.Children.Add(leftHost);

        var viewportHost = new Grid();
        viewportHost.Children.Add(_viewport);
        var viewportInput = new Border { Background = Brushes.Transparent };
        viewportInput.PointerPressed += (_, e) =>
        {
            var buttons = e.GetCurrentPoint(viewportInput).Properties;
            var selecting = buttons.IsLeftButtonPressed;
            if (_movingInViewport && selecting)
            {
                var wall = _session.Model.Walls.FirstOrDefault(w => w.Id == _selectedId);
                if (wall == null) { CancelMove(); return; }
                var elevation = _session.Model.BaseElevationOf(wall)
                    + _session.Model.HeightOf(wall) / 2d;
                if (!_viewport.TryScreenToPlan(e.GetPosition(_viewport), elevation, out var point))
                { _status.Text = "当前视角无法确定移动点，请调整视角后重试。"; return; }
                if (_viewportMoveBase == null)
                {
                    _viewportMoveBase = point;
                    _status.Text = (_copyingInViewport ? "复制 CO" : "移动 M")
                        + "：指定目标点；也可在弹窗中输入精确位移。Esc 取消。";
                }
                else
                {
                    var from = _viewportMoveBase;
                    var id = _selectedId!;
                    var copy = _copyingInViewport;
                    CancelMove();
                    _ = FinishMoveAsync(id, from, point, copy);
                }
                e.Handled = true;
                return;
            }
            var orbiting = buttons.IsMiddleButtonPressed
                && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var panning = buttons.IsRightButtonPressed
                || (buttons.IsMiddleButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (!selecting && !panning && !orbiting) return;
            if (selecting && _gizmo.TryBegin(e.GetPosition(_viewport),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                e.Pointer.Capture(viewportInput);
                e.Handled = true;
                return;
            }
            _viewport.BeginInteraction(e.GetPosition(_viewport), selecting, panning,
                orbiting, orbiting);
            e.Pointer.Capture(viewportInput);
            e.Handled = true;
        };
        viewportInput.PointerMoved += (_, e) =>
        {
            if (_gizmo.IsDragging)
            {
                _gizmo.Move(e.GetPosition(_viewport));
                return;
            }
            var buttons = e.GetCurrentPoint(viewportInput).Properties;
            _viewport.MoveInteraction(e.GetPosition(_viewport), buttons.IsLeftButtonPressed,
                buttons.IsMiddleButtonPressed || buttons.IsRightButtonPressed);
            _gizmo.InvalidateVisual();
        };
        viewportInput.PointerReleased += (_, e) =>
        {
            if (_gizmo.IsDragging) _gizmo.Finish();
            _viewport.EndInteraction(e.GetPosition(_viewport));
            if (e.Pointer.Captured == viewportInput) e.Pointer.Capture(null);
            e.Handled = true;
        };
        viewportInput.PointerWheelChanged += (_, e) =>
        {
            _viewport.Zoom(e.Delta.Y);
            _gizmo.InvalidateVisual();
            e.Handled = true;
        };
        viewportHost.Children.Add(viewportInput);
        viewportHost.Children.Add(_gizmo);
        var viewTools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10)
        };
        foreach (var (label, tool) in new[]
        {
            ("选择 Q", ViewTransformTool.Select),
            ("移动 W", ViewTransformTool.Move),
            ("旋转 E", ViewTransformTool.Rotate)
        })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => SetViewTool(tool);
            _viewToolButtons.Add(tool, button);
            viewTools.Children.Add(button);
        }
        var preciseMove3D = new Button { Content = "基点移动 M" };
        preciseMove3D.Click += (_, _) => BeginMove();
        viewTools.Children.Add(preciseMove3D);
        // View tools live in the ribbon; the viewport remains unobstructed.

        var planLayout = new Grid { RowDefinitions = new RowDefinitions("42,*") };
        var planTools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3
        };
        var openingTools = new StackPanel { Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center };
        _storeyChooser.SelectionChanged += (_, _) =>
        {
            if (!_refreshingStoreys && _storeyChooser.SelectedItem is StoreyItem floor)
                _planCanvas.SetStorey(floor.Id);
        };
        var planViewStrip = new StackPanel { Orientation = Orientation.Horizontal,
            Spacing = 8, Margin = new Thickness(8, 4) };
        planViewStrip.Children.Add(new TextBlock { Text = "楼层", VerticalAlignment = VerticalAlignment.Center });
        planViewStrip.Children.Add(_storeyChooser);
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
                SetPlanTool(tool);
            };
            _planToolButtons[tool] = button;
            if (tool == PlanTool.Wall) planTools.Children.Add(button);
            else if (tool is PlanTool.Door or PlanTool.Window)
            {
                button.Classes.Add("ribbon-small");
                openingTools.Children.Add(button);
            }
        }
        planTools.Children.Add(openingTools);
        _polarButton.Click += (_, _) => SetPolar(!_planCanvas.PolarEnabled,
            _planCanvas.PolarStepDegrees);
        _orthoButton.Click += (_, _) => SetOrthogonal(!_planCanvas.OrthogonalEnabled);
        var parameterTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        parameterTools.Children.Add(new TextBlock { Text = "精确墙长", VerticalAlignment = VerticalAlignment.Center });
        parameterTools.Children.Add(wallLength);
        parameterTools.Children.Add(new TextBlock { Text = "新墙高（0=随楼层）",
            VerticalAlignment = VerticalAlignment.Center });
        parameterTools.Children.Add(_newWallHeight);
        var delete = new Button { Content = "删除选中" };
        var preciseMovePlan = new Button { Content = "基点移动 M" };
        preciseMovePlan.Click += (_, _) => BeginMove();
        delete.Click += async (_, _) => await DeleteSelectedAsync();
        var fitPlan = new Button { Content = "缩放适应" };
        fitPlan.Click += (_, _) => _planCanvas.Fit();
        planViewStrip.Children.Add(fitPlan);
        planLayout.Children.Add(planViewStrip);
        Grid.SetRow(_planCanvas, 1);
        planLayout.Children.Add(_planCanvas);

        var modelTab = new TabItem { Header = "三维视图", Content = viewportHost,
            FontSize = 13, MinWidth = 88, Height = 32 };
        var planTab = new TabItem { Header = "平面视图", Content = planLayout,
            FontSize = 13, MinWidth = 88, Height = 32 };
        var drawingTab = new TabItem { Header = "图纸视图", IsEnabled = false,
            FontSize = 13, MinWidth = 88, Height = 32 };
        _workspaces.Margin = new Thickness(8, 0, 8, 0);
        _workspaces.Items.Add(modelTab);
        _workspaces.Items.Add(planTab);
        _workspaces.Items.Add(drawingTab);
        void UpdateWorkspaceTabs()
        {
            foreach (var tab in new[] { modelTab, planTab, drawingTab })
            {
                var selected = ReferenceEquals(tab, _workspaces.SelectedItem);
                tab.Background = new SolidColorBrush(Color.Parse(selected ? "#1B5D9E" : "#1B2937"));
                tab.Foreground = new SolidColorBrush(Color.Parse(selected ? "#FFFFFF" : "#B2C4D6"));
                tab.BorderBrush = new SolidColorBrush(Color.Parse("#30485E"));
                tab.BorderThickness = new Thickness(1);
            }
        }
        _workspaces.SelectionChanged += (_, _) => UpdateWorkspaceTabs();
        _workspaces.SelectedIndex = Program.Smoke || Program.SnapshotPath != null ? 0 : 1;
        UpdateWorkspaceTabs();
        Grid.SetRow(_workspaces, 2);
        Grid.SetColumn(_workspaces, 1);
        root.Children.Add(_workspaces);

        var propertyScroll = new ScrollViewer { Content = _properties };
        var rightHeader = new Grid { Height = 42 };
        rightHeader.Children.Add(new TextBlock { Text = "属性", FontSize = 14,
            FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0) });
        var rightPanel = new Grid { RowDefinitions = new RowDefinitions("42,*"), IsVisible = false,
            Margin = new Thickness(4, 8, 8, 8) };
        rightPanel.Children.Add(rightHeader);
        Grid.SetRow(propertyScroll, 1);
        rightPanel.Children.Add(propertyScroll);
        var rightToggle = new Button { Width = 30, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 7, 0) };
        ToolTip.SetTip(rightToggle, "展开属性栏");
        var propertyRailLabel = new TextBlock { Text = "属\n性", FontSize = 12,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 52, 0, 0) };
        var rightHost = new Grid();
        rightHost.Children.Add(rightPanel);
        rightHost.Children.Add(propertyRailLabel);
        rightHost.Children.Add(rightToggle);
        rightToggle.Click += (_, _) =>
        {
            rightPanel.IsVisible = !rightPanel.IsVisible;
            propertyRailLabel.IsVisible = !rightPanel.IsVisible;
            root.ColumnDefinitions[2].Width = new GridLength(rightPanel.IsVisible ? 310 : 36);
            rightToggle.Content = CommandIcon(rightPanel.IsVisible ? "chevron-right" : "chevron-left", 15);
            ToolTip.SetTip(rightToggle, rightPanel.IsVisible ? "收起属性栏" : "展开属性栏");
        };
        if (Program.SnapshotProperties)
        {
            rightPanel.IsVisible = true;
            propertyRailLabel.IsVisible = false;
            root.ColumnDefinitions[2].Width = new GridLength(310);
        }
        Grid.SetRow(rightHost, 2);
        Grid.SetColumn(rightHost, 2);
        root.Children.Add(rightHost);

        var pages = new Dictionary<string, Control>();
        var tabButtons = new Dictionary<string, Button>();
        static Button Action(string caption, Action callback)
        {
            var button = new Button { Content = caption };
            button.Click += (_, _) => callback();
            return button;
        }
        static Button Planned(string caption)
        {
            var button = new Button { Content = caption, IsHitTestVisible = false, Opacity = 0.43 };
            ToolTip.SetTip(button, caption + "：尚未实现");
            return button;
        }
        var structuralTools = new StackPanel { Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center };
        foreach (var name in new[] { "楼板", "柱", "楼梯" })
        {
            var button = Planned(name);
            button.Classes.Add("ribbon-list");
            button.Opacity = 0.62;
            structuralTools.Children.Add(button);
        }
        static Border Group(string title, params Control[] controls)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal,
                Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            foreach (var control in controls) actions.Children.Add(control);
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,18"),
                Margin = new Thickness(6, 4, 6, 1) };
            layout.Children.Add(actions);
            var caption = new TextBlock { Text = title, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#9BB3CA")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(caption, 1);
            layout.Children.Add(caption);
            return new Border { Child = layout, BorderBrush = new SolidColorBrush(Color.Parse("#405267")),
                BorderThickness = new Thickness(0, 0, 1, 0) };
        }
        static StackPanel Page(params Control[] groups)
        {
            var page = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch };
            foreach (var group in groups) page.Children.Add(group);
            return page;
        }
        pages["建模"] = Page(
            Group("建筑构件", planTools),
            Group("结构构件", structuralTools),
            Group("基准设置", axisSettings, globalStoreys, Planned("标高")),
            Group("修改", Action("移动", () => BeginMove()),
                Action("复制", () => BeginMove(true)),
                Planned("旋转"), Planned("镜像"), Planned("偏移"), Planned("阵列"),
                Action("删除", () => _ = DeleteSelectedAsync())),
            Group("编辑", Planned("相交"), Planned("修剪"), Planned("延伸")));
        pages["构件"] = Page(Group("建筑构件",
            Action("画墙", () => { _workspaces.SelectedIndex = 1; SetPlanTool(PlanTool.Wall); }),
            Action("放门", () => { _workspaces.SelectedIndex = 1; SetPlanTool(PlanTool.Door); }),
            Action("放窗", () => { _workspaces.SelectedIndex = 1; SetPlanTool(PlanTool.Window); })),
            Group("绘制参数", parameterTools));
        pages["编辑"] = Page(
            Group("历史", _undo, _redo),
            Group("修改", _planToolButtons[PlanTool.Select], preciseMovePlan,
                Action("复制 CO", () => BeginMove(true)), deleteSelected),
            Group("绘制约束", _orthoButton, _polarButton));
        pages["标注"] = Page(Group("轴线与楼层",
            Action("轴号设置", () => _ = OpenAxisSettingsAsync()),
            Action("楼层设置", () => _ = OpenStoreySettingsAsync())));
        pages["视图"] = Page(
            Group("三维操作", viewTools),
            Group("视图", resetView,
                Action("平面视图", () => _workspaces.SelectedIndex = 1),
                Action("三维视图", () => _workspaces.SelectedIndex = 0)));
        pages["出图"] = Page(Group("CAD 输出", _publish, _sendToCad, cancelPublish));
        pages["管理"] = Page(Group("模型文件", open, save, saveAs),
            Group("项目设置", Action("楼层设置", () => _ = OpenStoreySettingsAsync())));
        void StyleRibbonControl(Control control, bool inRibbon = true)
        {
            switch (control)
            {
                case Button button:
                    var list = inRibbon && button.Classes.Contains("ribbon-list");
                    var small = inRibbon && button.Classes.Contains("ribbon-small");
                    button.MinHeight = inRibbon ? list ? 24 : small ? 36 : 74 : 32;
                    if (inRibbon) button.MinWidth = list ? 168 : small ? 98 : 72;
                    button.FontSize = 12;
                    button.Padding = inRibbon ? list ? new Thickness(6, 0)
                        : small ? new Thickness(6, 2) : new Thickness(5, 5)
                        : new Thickness(10, 5);
                    button.Background = new SolidColorBrush(Color.Parse("#1C2835"));
                    button.Foreground = new SolidColorBrush(Color.Parse("#D9E9F5"));
                    button.BorderBrush = new SolidColorBrush(Color.Parse("#38556E"));
                    button.BorderThickness = inRibbon ? new Thickness(0) : new Thickness(1);
                    button.CornerRadius = new CornerRadius(4);
                    if (inRibbon && button.Content is "画墙")
                    {
                        button.MinWidth = 78;
                        button.BorderBrush = new SolidColorBrush(Color.Parse("#4B81AE"));
                        button.BorderThickness = new Thickness(1);
                    }
                    if (inRibbon && button.Content is string label)
                        SetCommandVisual(button, label, small || list);
                    break;
                case ComboBox combo:
                    combo.MinHeight = 32;
                    combo.Background = new SolidColorBrush(Color.Parse("#20364A"));
                    combo.BorderBrush = new SolidColorBrush(Color.Parse("#38556E"));
                    break;
                case TextBox input:
                    input.MinHeight = 32;
                    input.Background = new SolidColorBrush(Color.Parse("#20364A"));
                    input.BorderBrush = new SolidColorBrush(Color.Parse("#38556E"));
                    break;
            }
            if (control is Panel panel)
                foreach (var child in panel.Children) StyleRibbonControl(child, inRibbon);
            else if (control is Border border && border.Child != null)
                StyleRibbonControl(border.Child, inRibbon);
        }
        foreach (var page in pages.Values) StyleRibbonControl(page);
        foreach (var control in new Control[] { browserSearch, _storeyChooser, fitPlan,
            leftToggle, rightToggle, _commandInput })
            StyleRibbonControl(control, false);
        SetCommandVisual(fitPlan, "缩放适应", true);
        leftToggle.Content = CommandIcon("chevron-left", 15);
        rightToggle.Content = CommandIcon(rightPanel.IsVisible ? "chevron-right" : "chevron-left", 15);
        ribbonLeft.Content = CommandIcon("chevron-left", 15);
        ribbonRight.Content = CommandIcon("chevron-right", 15);
        void SelectRibbon(string name)
        {
            ribbonContent.Content = pages[name];
            ribbonScroll.Offset = new Vector(0, 0);
            Dispatcher.UIThread.Post(UpdateRibbonArrows, DispatcherPriority.Loaded);
            foreach (var pair in tabButtons)
            {
                pair.Value.Background = new SolidColorBrush(Color.Parse(pair.Key == name ? "#155995" : "#19232F"));
                pair.Value.Foreground = new SolidColorBrush(Color.Parse(pair.Key == name ? "#FFFFFF" : "#C1D1DF"));
            }
        }
        foreach (var name in new[] { "建模", "构件", "编辑", "标注", "视图", "出图", "管理" })
        {
            var button = new Button { Content = name, MinWidth = 86, Height = 34,
                FontSize = 16, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(12, 4),
                BorderThickness = new Thickness(0) };
            button.Click += (_, _) => SelectRibbon(name);
            tabButtons[name] = button;
            ribbonTabs.Children.Add(button);
        }
        SelectRibbon("建模");

        _status.Foreground = new SolidColorBrush(Color.Parse("#A4B8CF"));
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(14, 0);
        _status.Text = "左键选择 · 中键旋转 · Shift+中键或右键平移 · 滚轮缩放 · 毫米单位";
        var console = new Grid
        {
            RowDefinitions = new RowDefinitions("24,32,28"),
            ColumnDefinitions = new ColumnDefinitions("65,*"),
            Margin = new Thickness(12, 2)
        };
        Grid.SetColumnSpan(_status, 2);
        console.Children.Add(_status);
        var prompt = new TextBlock { Text = "命令:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0) };
        Grid.SetRow(prompt, 1);
        console.Children.Add(prompt);
        _commandInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                var command = _commandInput.Text;
                _commandInput.Clear();
                ExecuteCommand(command);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _commandInput.Clear();
                CancelActiveCommand();
                e.Handled = true;
            }
        };
        Grid.SetRow(_commandInput, 1);
        Grid.SetColumn(_commandInput, 1);
        console.Children.Add(_commandInput);
        var statusStrip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 1, 0, 0) };
        statusStrip.Children.Add(new TextBlock { Text = "毫米单位  ·  Esc 取消当前命令",
            FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8FA9BD")),
            VerticalAlignment = VerticalAlignment.Center });
        void StyleStatusButton(Button button, int column, string tip, Action callback)
        {
            button.MinWidth = 78;
            button.Height = 24;
            button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.FontSize = 11;
            button.Background = new SolidColorBrush(Color.Parse("#1C3042"));
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(3);
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => callback();
            Grid.SetColumn(button, column);
            statusStrip.Children.Add(button);
        }
        StyleStatusButton(_orthoStatusButton, 1, "正交约束 (F8)",
            () => SetOrthogonal(!_planCanvas.OrthogonalEnabled));
        StyleStatusButton(_polarStatusButton, 2, "极轴追踪 (F10)",
            () => SetPolar(!_planCanvas.PolarEnabled, _planCanvas.PolarStepDegrees));
        Grid.SetRow(statusStrip, 2);
        Grid.SetColumnSpan(statusStrip, 2);
        console.Children.Add(statusStrip);
        Grid.SetRow(console, 3);
        Grid.SetColumnSpan(console, 3);
        root.Children.Add(console);
        Content = root;
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);

        BuildElementList();
        RefreshStoreys();
        SelectById("1F-S");
        SetViewTool(Program.SnapshotGizmo ? ViewTransformTool.Move : ViewTransformTool.Select);
        SetPlanTool(PlanTool.Select);
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

    private static (string? icon, string? shortcut) RibbonButtonVisual(string label) => label switch
    {
        "选择" or "选择 Q" => ("mouse-pointer-2", "Q"),
        "画墙" => ("wall-plan", "WA"),
        "放门" => ("door-open", "DR"),
        "放窗" => ("app-window", "WN"),
        "楼板" => ("layers", null),
        "柱" => ("box", null),
        "楼梯" => ("layers", null),
        "标高" => ("layers", null),
        "移动" or "基点移动 M" => ("move", "M"),
        "移动 W" => ("move", "W"),
        "旋转 E" => ("rotate-ccw", "E"),
        "旋转" => ("rotate-ccw", null),
        "复制" or "复制 CO" => ("copy", "CO"),
        "镜像" => ("copy", null),
        "偏移" => ("move", null),
        "阵列" => ("grid-3x3", null),
        "相交" => ("grid-3x3", null),
        "修剪" => ("trash", null),
        "延伸" => ("move", null),
        "删除" => ("trash", "Del"),
        "删除选中" => ("trash", "Del"),
        "楼层设置" => ("layers", "LS"),
        "轴号设置" => ("grid-3x3", "AX"),
        "打开模型" => ("folder-open", "Ctrl+O"),
        "保存" => ("save", "Ctrl+S"),
        "另存为" => ("file-plus", "Ctrl+Shift+S"),
        "生成 CAD 视图" => ("file-axis-3d", "PV"),
        "推到 CAD" => ("send", "SC"),
        "取消生成" => ("undo-2", null),
        "视图复位" => ("maximize", "Home"),
        "缩放适应" => ("maximize", "ZF"),
        "平面视图" => ("panel-top", "PL"),
        "三维视图" => ("box", "3D"),
        _ when label.StartsWith("撤销", StringComparison.Ordinal) => ("undo-2", "Ctrl+Z"),
        _ when label.StartsWith("重做", StringComparison.Ordinal) => ("redo-2", "Ctrl+Y"),
        _ when label.StartsWith("正交", StringComparison.Ordinal) => ("grid-3x3", "F8"),
        _ when label.StartsWith("极轴", StringComparison.Ordinal) => ("grid-3x3", "F10"),
        _ => (null, null)
    };

    private static Control CommandIcon(string name, double size)
    {
        if (name is "door-open" or "app-window")
        {
            var path = name == "door-open"
                ? "M4 21 L4 3 L19 3 L19 21 M8 21 L8 7 L16 5 L16 21 M11 14 L12 14"
                : "M3 4 L21 4 L21 20 L3 20 Z M3 10 L21 10 M12 10 L12 20";
            return VectorIcon(path, size);
        }
        var bitmap = IconCache.GetOrAdd(name, key =>
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://万落建筑模型/Resources/Icons/" + key + ".png"));
            return new Bitmap(stream);
        });
        return new Image { Source = bitmap, Width = size, Height = size,
            Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }

    private static Control VectorIcon(string path, double size) =>
        new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(path), Width = size, Height = size,
            Stroke = new SolidColorBrush(Color.Parse("#9CCFFF")), StrokeThickness = 2,
            Stretch = Stretch.Uniform, IsHitTestVisible = false
        };

    private static Control QuickIcon(string name)
    {
        var data = name switch
        {
            "folder-open" => "M2 20 L5 10 L22 10 L19 20 Z M2 20 L2 5 L9 5 L11 7 L19 7 L19 10",
            "save" => "M3 3 L17 3 L21 7 L21 21 L3 21 Z M7 3 L7 9 L16 9 L16 3 M7 21 L7 13 L17 13 L17 21",
            "undo-2" => "M9 4 L4 9 L9 14 M4 9 L15 9 C19 9 21 12 21 16 C21 19 18 21 14 21",
            _ => "M15 4 L20 9 L15 14 M20 9 L9 9 C5 9 3 12 3 16 C3 19 6 21 10 21"
        };
        return VectorIcon(data, 17);
    }

    private static Control BrowserHeader(string title, string icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(CommandIcon(icon, 14));
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static void SetCommandVisual(Button button, string label, bool compact = false)
    {
        var (icon, shortcut) = RibbonButtonVisual(label);
        if (icon == null) return;
        var title = shortcut != null && label.EndsWith(shortcut, StringComparison.OrdinalIgnoreCase)
            ? label[..^shortcut.Length].Trim() : label;
        if (button.Classes.Contains("ribbon-list"))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,14"), Width = 154 };
            row.Children.Add(CommandIcon(icon, 17));
            var text = new TextBlock { Text = title, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var arrow = CommandIcon("chevron-right", 12);
            Grid.SetColumn(arrow, 2);
            row.Children.Add(arrow);
            button.Content = row;
        }
        else if (compact)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal,
                Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(CommandIcon(icon, 15));
            row.Children.Add(new TextBlock { Text = title + (shortcut == null ? "" : "  " + shortcut),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
        }
        else
        {
            var column = new StackPanel { Spacing = 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            column.Children.Add(CommandIcon(icon, 27));
            column.Children.Add(new TextBlock { Text = title, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center });
            if (shortcut != null)
                column.Children.Add(new TextBlock { Text = shortcut, FontSize = 9,
                    Foreground = new SolidColorBrush(Color.Parse("#7FA8CE")),
                    HorizontalAlignment = HorizontalAlignment.Center });
            button.Content = column;
        }
        ToolTip.SetTip(button, !button.IsHitTestVisible ? label + "：尚未实现"
            : shortcut == null ? label : label + "  (" + shortcut + ")");
    }

    private void UpdateTitle()
    {
        Title = $"万落建筑模型 [{ReleaseRevision}] · {(_filePath == null ? "未命名样例" : Path.GetFileName(_filePath))}{(HasChanges ? " *" : "")}";
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
        FilterElementList(_elementFilter);
    }

    private void FilterElementList(string? query)
    {
        _elementFilter = query?.Trim() ?? "";
        _elements.ItemsSource = _items;
        RebuildBrowserTree();
    }

    private void RebuildBrowserTree()
    {
        _browserTree.Items.Clear();
        _browserNodes.Clear();
        var model = _session.Model;
        var project = new TreeViewItem { Header = BrowserHeader(model.Name, "folder-open"), IsExpanded = true };
        var floors = new TreeViewItem { Header = BrowserHeader("楼层", "layers"), IsExpanded = true };
        project.Items.Add(floors);
        var wallStoreys = model.Walls.ToDictionary(w => w.Id, w => w.StoreyId);
        var matches = _elementFilter.Length == 0 ? null : _items
            .Where(item => item.Label.Contains(_elementFilter,
                StringComparison.CurrentCultureIgnoreCase))
            .Select(item => item.Id).ToHashSet();
        bool Matches(string id) => matches == null || matches.Contains(id);
        void AddCategory(TreeViewItem floorNode, string name,
            IEnumerable<(string id, string label)> elements)
        {
            var entries = elements.Where(x => Matches(x.id)).ToList();
            if (entries.Count == 0) return;
            var icon = name switch { "墙" => "wall-plan", "门窗" => "door-open",
                "楼板" => "layers", "柱" => "box", _ => "layers" };
            var category = new TreeViewItem { Header = BrowserHeader($"{name} ({entries.Count})", icon),
                IsExpanded = _elementFilter.Length > 0 || entries.Count < 12 };
            foreach (var (id, label) in entries)
            {
                var leaf = new TreeViewItem { Header = BrowserHeader(label, icon) };
                ToolTip.SetTip(leaf, id);
                leaf.PointerPressed += (_, _) => SelectById(id);
                category.Items.Add(leaf);
                _browserNodes[id] = leaf;
            }
            floorNode.Items.Add(category);
        }
        foreach (var floor in model.Storeys)
        {
            var floorNode = new TreeViewItem
            {
                Header = BrowserHeader($"{floor.Name}  (标高 {floor.Elevation:0} mm)"
                    + (string.IsNullOrWhiteSpace(floor.TemplateStoreyId) ? "" : " · 标准层"), "layers"),
                IsExpanded = _elementFilter.Length > 0 || floor == model.Storeys.FirstOrDefault()
            };
            if (!string.IsNullOrWhiteSpace(floor.TemplateStoreyId))
                floorNode.Items.Add(new TreeViewItem
                { Header = BrowserHeader("共用 " + floor.TemplateStoreyId + " 平面构件", "copy") });
            AddCategory(floorNode, "墙", model.Walls.Where(x => x.StoreyId == floor.Id)
                .Select(x => (x.Id, "墙  " + x.Id)));
            AddCategory(floorNode, "门窗", model.Openings.Where(x =>
                    wallStoreys.TryGetValue(x.HostWallId, out var storey) && storey == floor.Id)
                .Select(x => (x.Id, x.Kind + "  " + x.Id)));
            AddCategory(floorNode, "楼板", model.Slabs.Where(x => x.StoreyId == floor.Id)
                .Select(x => (x.Id, "楼板  " + x.Id)));
            AddCategory(floorNode, "柱", model.Columns.Where(x => x.StoreyId == floor.Id)
                .Select(x => (x.Id, "柱  " + x.Id)));
            AddCategory(floorNode, "楼梯", model.Stairs.Where(x => x.StoreyId == floor.Id)
                .Select(x => (x.Id, "楼梯  " + x.Id)));
            AddCategory(floorNode, "屋面", model.Roofs.Where(x => x.StoreyId == floor.Id)
                .Select(x => (x.Id, "屋面  " + x.Id)));
            if (floorNode.Items.Count > 0) floors.Items.Add(floorNode);
        }
        _browserTree.Items.Add(project);
        if (_selectedId != null && _browserNodes.TryGetValue(_selectedId, out var selected))
            selected.IsSelected = true;
    }

    private async Task OpenAxisSettingsAsync()
    {
        var dialog = new AxisSettingsWindow(_session.Model);
        if (!await dialog.ShowDialog<bool>(this)) return;
        if (_session.TryReplaceAxes(dialog.ResultAxes, out var error))
            await RefreshModelAsync("轴号已更新");
        else _status.Text = error;
    }

    private void RefreshStoreys()
    {
        var previous = (_storeyChooser.SelectedItem as StoreyItem)?.Id;
        _refreshingStoreys = true;
        try
        {
        _storeyChooser.ItemsSource = _session.Model.Storeys.Select(s => new StoreyItem
        { Id = s.Id, Name = s.Name + "  (" + s.Elevation.ToString("0") + " mm)"
            + (string.IsNullOrWhiteSpace(s.TemplateStoreyId) ? "" : " · 标准层 " + s.TemplateStoreyId) }).ToList();
        _storeyChooser.SelectedItem = (_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?
            .FirstOrDefault(s => s.Id == previous) ?? (_storeyChooser.ItemsSource as IEnumerable<StoreyItem>)?.FirstOrDefault();
        }
        finally { _refreshingStoreys = false; }
        _planCanvas.SetModel(_session.Model, (_storeyChooser.SelectedItem as StoreyItem)?.Id ?? "1F");
    }

    private void SelectById(string? id)
    {
        var visualId = id;
        id = StandardStoreyLayout.SourceElementId(_session.Model, id);
        var item = _items.FirstOrDefault(x => x.Id == id);
        if (!ReferenceEquals(_elements.SelectedItem, item))
        {
            _elements.SelectedItem = item;
            if (visualId != id) _viewport.SelectElement(visualId);
            return;
        }
        _selectedId = item?.Id;
        _viewport.SelectElement(visualId ?? _selectedId);
        _planCanvas.SetSelection(_selectedId);
        UpdateGizmoSelection();
        RefreshProperties();
    }

    private void SetViewTool(ViewTransformTool tool)
    {
        _gizmo.SetTool(tool);
        foreach (var entry in _viewToolButtons)
            entry.Value.Background = new SolidColorBrush(Color.Parse(entry.Key == tool ? "#2169BF" : "#263342"));
        _status.Text = tool == ViewTransformTool.Select ? "选择 Q：点选构件；左键空白处旋转视角。"
            : tool == ViewTransformTool.Move ? "移动 W：拖红色 X 轴、绿色 Y 轴或中心方块；Shift 拖动复制。"
            : "旋转 E：拖动选中墙的橙色圆环；Shift 拖动复制。";
    }

    private void ResetActiveView()
    {
        if (_workspaces.SelectedIndex == 1) _planCanvas.Fit();
        else { _viewport.ResetView(); _gizmo.InvalidateVisual(); }
    }

    private void SetPlanTool(PlanTool tool)
    {
        _planCanvas.Tool = tool;
        _planCanvas.CancelDraft();
        foreach (var entry in _planToolButtons)
            entry.Value.Background = new SolidColorBrush(Color.Parse(
                entry.Key == tool ? "#1768A7" : "#20364A"));
        _status.Text = tool == PlanTool.Wall
            ? "画墙 WA：点起点，指向方向后可直接输入长度，空格/回车确认，Esc 退出。"
            : tool == PlanTool.Select ? "选择 Q：点击墙或门窗选择构件。"
            : "请点选宿主墙上的位置。";
        _planCanvas.Focus();
    }

    private void SetPolar(bool enabled, double stepDegrees)
    {
        if (enabled && _planCanvas.OrthogonalEnabled) SetOrthogonal(false);
        _planCanvas.SetPolar(enabled, stepDegrees);
        SetCommandVisual(_polarButton, $"极轴 {stepDegrees:0.#}°：{(enabled ? "开" : "关")}");
        _polarStatusButton.Background = new SolidColorBrush(Color.Parse(enabled ? "#155995" : "#1C3042"));
        _status.Text = enabled ? $"极轴已开启：按 {stepDegrees:0.#}° 增量追踪；Shift 临时正交。"
            : "极轴已关闭；Shift 可临时正交。";
    }

    private void SetOrthogonal(bool enabled)
    {
        if (enabled && _planCanvas.PolarEnabled) SetPolar(false, _planCanvas.PolarStepDegrees);
        _planCanvas.SetOrthogonal(enabled);
        SetCommandVisual(_orthoButton, $"正交 F8：{(enabled ? "开" : "关")}");
        _orthoStatusButton.Background = new SolidColorBrush(Color.Parse(enabled ? "#155995" : "#1C3042"));
        _status.Text = enabled ? "正交已开启：画墙和指定目标点时锁定水平或垂直。F8 关闭。"
            : "正交已关闭；按住 Shift 可临时锁定水平或垂直。";
    }

    private void CancelActiveCommand()
    {
        _commandInput.Clear();
        _gizmo.Cancel();
        CancelMove();
        if (_workspaces.SelectedIndex == 1) SetPlanTool(PlanTool.Select);
        else _planCanvas.CancelDraft();
        _status.Text = "命令已取消，返回选择。";
    }

    private void ExecuteCommand(string? input)
    {
        var command = input?.Trim() ?? "";
        if (_workspaces.SelectedIndex == 1 && _planCanvas.HasWallStart
            && ModelCommandCatalog.TryPolarLength(command, out var polarLength, out var angleDegrees))
        {
            if (!_planCanvas.TryDrawWallPolar(polarLength, angleDegrees, out var error)) _status.Text = error;
            else { _status.Text = $"已按 {angleDegrees:0.##}° 画 {polarLength:0.##} mm 墙；Esc 退出。"; _planCanvas.Focus(); }
            return;
        }
        if (_workspaces.SelectedIndex == 1 && _planCanvas.HasWallStart
            && ModelCommandCatalog.TryLength(command, out var length))
        {
            if (!_planCanvas.TryDrawWallLength(length, out var error)) _status.Text = error;
            else { _status.Text = $"已画 {length:0.##} mm 墙；继续指定下一段，Esc 退出。"; _planCanvas.Focus(); }
            return;
        }
        if (ModelCommandCatalog.TryDisplacement(input, out var dx, out var dy))
        {
            var active = _movingInViewport || _planCanvas.IsMoving;
            if (!active || _selectedId == null)
            { _status.Text = "请先选择墙并输入 M 或 CO。"; return; }
            var copy = _movingInViewport ? _copyingInViewport : _planCanvas.IsCopyingMove;
            var id = _selectedId;
            CancelMove();
            _planCanvas.CancelDraft();
            _ = ApplyExactDisplacementAsync(id, dx, dy, copy);
            return;
        }
        if (command.StartsWith("POLAR", StringComparison.OrdinalIgnoreCase)
            && command.Length > 5 && double.TryParse(command[5..].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var polarStep)
            && double.IsFinite(polarStep) && polarStep >= 1 && polarStep <= 90)
        { SetPolar(true, polarStep); return; }
        switch (command.ToUpperInvariant())
        {
            case "DR": _workspaces.SelectedIndex = 1; SetPlanTool(PlanTool.Door); return;
            case "WN": _workspaces.SelectedIndex = 1; SetPlanTool(PlanTool.Window); return;
            case "LS": _ = OpenStoreySettingsAsync(); return;
            case "AX": _ = OpenAxisSettingsAsync(); return;
            case "PL": _workspaces.SelectedIndex = 1; return;
            case "3D": _workspaces.SelectedIndex = 0; return;
            case "ZF": ResetActiveView(); return;
            case "PV": _ = PublishViewsAsync(false); return;
            case "SC": _ = PublishViewsAsync(true); return;
        }
        switch (ModelCommandCatalog.Resolve(input))
        {
            case ModelCommandKind.Wall:
                _workspaces.SelectedIndex = 1;
                SetPlanTool(PlanTool.Wall); break;
            case ModelCommandKind.Move: BeginMove(false); break;
            case ModelCommandKind.Copy: BeginMove(true); break;
            case ModelCommandKind.Polar:
                SetPolar(!_planCanvas.PolarEnabled, _planCanvas.PolarStepDegrees); break;
            case ModelCommandKind.Ortho:
                SetOrthogonal(!_planCanvas.OrthogonalEnabled); break;
            case ModelCommandKind.Select:
                if (_workspaces.SelectedIndex == 1) SetPlanTool(PlanTool.Select);
                else SetViewTool(ViewTransformTool.Select);
                break;
            case ModelCommandKind.GizmoMove:
                if (_workspaces.SelectedIndex == 0) SetViewTool(ViewTransformTool.Move);
                break;
            case ModelCommandKind.Rotate:
                if (_workspaces.SelectedIndex == 0) SetViewTool(ViewTransformTool.Rotate);
                break;
            case ModelCommandKind.Mirror:
                _status.Text = "MI 镜像尚未实现；当前模型不会被修改。"; break;
            case ModelCommandKind.Fillet:
                _status.Text = "F 圆角尚未实现；当前模型不会被修改。"; break;
            default:
                _status.Text = string.IsNullOrWhiteSpace(input) ? "请输入命令。"
                    : "未知命令：" + input.Trim(); break;
        }
    }

    private void BeginMove(bool copy = false)
    {
        if (_session.Model.Walls.All(w => w.Id != _selectedId))
        { _status.Text = "请先选择要操作的墙。"; return; }
        _gizmo.Cancel();
        _commandInput.PlaceholderText = "可输入 @ΔX,ΔY 精确提交，例如 @300,0";
        if (_workspaces.SelectedIndex == 1)
        {
            if (!_planCanvas.BeginMove(copy)) _status.Text = "请在当前楼层选择一面墙。";
            else _planCanvas.Focus();
            return;
        }
        if (_workspaces.SelectedIndex != 0) return;
        _movingInViewport = true;
        _copyingInViewport = copy;
        _viewportMoveBase = null;
        _commandInput.PlaceholderText = "可输入 @ΔX,ΔY 精确提交，例如 @300,0";
        _status.Text = (copy ? "复制 CO" : "移动 M")
            + "：在三维视图指定基点，再指定目标点；Esc 取消。";
    }

    private void CancelMove()
    {
        _movingInViewport = false;
        _copyingInViewport = false;
        _viewportMoveBase = null;
        _commandInput.PlaceholderText = "输入命令：M 移动 / CO 复制";
    }

    private async Task FinishMoveAsync(string id, PointModel from, PointModel to, bool copy)
    {
        var entered = await new MovementInputWindow(to.X - from.X, to.Y - from.Y, 0, copy)
            .ShowDialog<MovementResult?>(this);
        if (entered == null)
        {
            _commandInput.PlaceholderText = "输入命令：M 移动 / CO 复制";
            _status.Text = copy ? "已取消复制。" : "已取消移动。";
            return;
        }
        await ApplyExactDisplacementAsync(id, entered.X, entered.Y, copy);
    }

    private async Task ApplyExactDisplacementAsync(string id, double dx, double dy, bool copy)
    {
        if (!_session.TryTransformWall(id, dx, dy, 0,
            copy, out var affectedId, out var error))
        { _status.Text = error; return; }
        _selectedId = affectedId;
        _commandInput.PlaceholderText = "输入命令：M 移动 / CO 复制";
        await RefreshModelAsync(copy ? "已复制墙及门窗" : "已移动墙（长度保持不变）");
    }

    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.O)
            { e.Handled = true; _ = OpenModelAsync(); return; }
            if (e.Key == Key.S)
            {
                e.Handled = true;
                _ = SaveModelAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return;
            }
        }
        var editingOtherField = ShortcutEditingText(e.Source) && !IsCommandInput(e.Source);
        if (editingOtherField) return;
        if (e.Key == Key.Home)
        { e.Handled = true; ResetActiveView(); return; }
        if (e.Key == Key.Escape)
        { e.Handled = true; CancelActiveCommand(); return; }
        if (e.Key == Key.F8)
        { e.Handled = true; SetOrthogonal(!_planCanvas.OrthogonalEnabled); return; }
        if (e.Key == Key.F10)
        { e.Handled = true; SetPolar(!_planCanvas.PolarEnabled, _planCanvas.PolarStepDegrees); return; }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && !ShortcutEditingText(e.Source))
        { e.Handled = true; _ = DeleteSelectedAsync(); return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { e.Handled = true; _ = RedoModelAsync(); }
            else if (e.Key == Key.Z)
            { e.Handled = true; _ = UndoModelAsync(); }
            else if (e.Key == Key.Y)
            { e.Handled = true; _ = RedoModelAsync(); }
            return;
        }
        if (IsCommandInput(e.Source) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            if (!string.IsNullOrWhiteSpace(_commandInput.Text))
            { var command = _commandInput.Text; _commandInput.Clear(); ExecuteCommand(command); }
            e.Handled = true;
            return;
        }
        var character = CommandCharacter(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (character == null) return;
        _commandInput.Text += character;
        _commandInput.CaretIndex = _commandInput.Text?.Length ?? 0;
        _commandInput.Focus();
        e.Handled = true;
    }

    private bool IsCommandInput(object? source)
    {
        var control = source as Control;
        while (control != null)
        {
            if (ReferenceEquals(control, _commandInput)) return true;
            control = control.Parent as Control;
        }
        return false;
    }

    private static string? CommandCharacter(Key key, bool shift)
    {
        var name = key.ToString();
        if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') return name;
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
            return shift && name[1] == '2' ? "@" : name[1].ToString();
        if (name.StartsWith("NumPad", StringComparison.Ordinal)
            && name.Length == 7 && char.IsDigit(name[^1])) return name[^1].ToString();
        return name switch
        {
            "OemComma" => shift ? "<" : ",", "OemPeriod" or "Decimal" => ".",
            "OemMinus" or "Subtract" => "-", "OemPlus" or "Add" => "+",
            _ => null
        };
    }

    private static bool ShortcutEditingText(object? source)
    {
        var control = source as Control;
        while (control != null)
        {
            if (control is TextBox || control is ComboBox) return true;
            control = control.Parent as Control;
        }
        return false;
    }

    private async Task UndoModelAsync()
    {
        if (_gizmo.IsDragging)
        {
            _gizmo.Cancel();
            _status.Text = "已取消当前拖动，模型未修改。";
            return;
        }
        _gizmo.Cancel();
        _planCanvas.CancelDraft();
        if (_session.Undo()) await RefreshModelAsync("已撤销（Ctrl+Z）");
    }

    private async Task RedoModelAsync()
    {
        _gizmo.Cancel();
        _planCanvas.CancelDraft();
        if (_session.Redo()) await RefreshModelAsync("已重做（Ctrl+Y / Ctrl+Shift+Z）");
    }

    private async Task DeleteSelectedAsync()
    {
        if (!_session.TryDeleteElement(_selectedId ?? "", out var error))
        { _status.Text = error; return; }
        _gizmo.Cancel();
        _planCanvas.CancelDraft();
        _selectedId = null;
        await RefreshModelAsync("已删除构件（Ctrl+Z 可撤销）");
    }

    private void UpdateGizmoSelection()
    {
        var wall = _session.Model.Walls.FirstOrDefault(x => x.Id == _selectedId);
        var storey = wall == null ? null : _session.Model.FindStorey(wall.StoreyId);
        _gizmo.SetSelection(wall, storey == null ? 0 : storey.Elevation
            + (wall!.Height > 0 ? wall.Height : storey.Height) / 2);
    }

    private void RefreshProperties()
    {
        _properties.Children.Clear();
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
            var bodyOffset = WallReferenceGeometry.BodyOffset(wall);
            var leftField = AddNumberField("轴线左侧墙厚（mm）", wall.Thickness / 2d + bodyOffset);
            var rightField = AddNumberField("轴线右侧墙厚（mm）", wall.Thickness / 2d - bodyOffset);
            var heightField = AddNumberField("墙高（mm）", wall.Height);
            _properties.Children.Add(new TextBlock { Text = "左右按墙起点 → 终点判断；两侧之和为总墙厚。",
                TextWrapping = TextWrapping.Wrap });
            var apply = InspectorButton("应用墙体参数");
            apply.Click += async (_, _) => await ApplyGeometryAsync(
                new[] { lengthField, leftField, rightField, heightField }, values =>
            {
                var success = _session.TrySetWallGeometryBySides(wall.Id,
                    values[0], values[1], values[2], values[3], out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
            _properties.Children.Add(new TextBlock { Text = "整墙定位（门窗随墙移动）", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 0) });
            var deltaXField = AddNumberField("水平位移 X（mm）", 0);
            var deltaYField = AddNumberField("竖直位移 Y（mm）", 0);
            var angleField = AddNumberField("逆时针旋转（度，绕墙中点）", 0);
            var move = InspectorButton("移动 / 旋转墙");
            move.Click += async (_, _) => await ApplyGeometryAsync(
                new[] { deltaXField, deltaYField, angleField }, values =>
            {
                var success = _session.TryTransformWall(wall.Id, values[0], values[1], values[2],
                    false, out _, out var error);
                return (success, error);
            });
            _properties.Children.Add(move);
            var copy = InspectorButton("复制墙和门窗");
            copy.Click += async (_, _) => await ApplyGeometryAsync(
                new[] { deltaXField, deltaYField, angleField }, values =>
            {
                var success = _session.TryTransformWall(wall.Id, values[0], values[1], values[2],
                    true, out var newId, out var error);
                if (success) _selectedId = newId;
                return (success, error);
            });
            _properties.Children.Add(copy);
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
            var apply = InspectorButton("应用门窗参数");
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
        var field = new TextBox { Text = value.ToString("0.##", CultureInfo.InvariantCulture),
            MinHeight = 34, Background = new SolidColorBrush(Color.Parse("#20364A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38556E")) };
        _properties.Children.Add(field);
        return field;
    }

    private static Button InspectorButton(string label) => new()
    {
        Content = label, MinHeight = 34, Padding = new Thickness(10, 5),
        Background = new SolidColorBrush(Color.Parse("#20364A")),
        Foreground = new SolidColorBrush(Color.Parse("#D9E9F5")),
        BorderBrush = new SolidColorBrush(Color.Parse("#38556E")),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4)
    };

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
            _gizmo.InvalidateVisual();
            _status.Text = $"{message} · 修订 {_session.Revision}{(HasChanges ? " · 未保存" : "")}";
        }
        catch (Exception ex)
        {
            if (generation == _sceneGeneration) _status.Text = "三维重建失败：" + ex.Message;
        }
    }

    private async Task OpenStoreySettingsAsync()
    {
        var dialog = new StoreySettingsWindow(_session.Model);
        if (!await dialog.ShowDialog<bool>(this)) return;
        if (_session.TryReplaceStoreys(dialog.ResultStoreys, out var error))
            await RefreshModelAsync("楼层已更新");
        else _status.Text = error;
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
                Window? settingsDialog = null;
                if (Program.SnapshotAxes)
                    settingsDialog = new AxisSettingsWindow(_session.Model);
                else if (Program.SnapshotStoreys)
                    settingsDialog = new StoreySettingsWindow(_session.Model);
                if (settingsDialog != null) { settingsDialog.Show(this); await Task.Delay(350); }
                var visual = ElementComposition.GetElementVisual((Control?)settingsDialog ?? this);
                if (visual == null) throw new InvalidOperationException("Composition visual unavailable");
                var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                snapshot.Save(Program.SnapshotPath, PngBitmapEncoderOptions.Default);
                settingsDialog?.Close();
                Console.WriteLine("AVALONIA_SNAPSHOT " + Program.SnapshotPath);
            }
            var hit = _viewport.FrameRendered
                ? _viewport.PickAt(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2)) : null;
            var success = emptyProject ? _filePath == Program.ModelPath && File.Exists(_filePath)
                : _viewport.FrameRendered && hit != null;
            if (Program.GizmoCheck) success &= RunGizmoSmokeCheck() && RunCameraSmokeCheck();
            if (Program.ShortcutCheck) success &= RunShortcutSmokeCheck();
            Program.SmokeFailed = !success;
            Console.WriteLine(success ? (emptyProject ? "AVALONIA_EMPTY_PROJECT_OK " + _filePath
                : "AVALONIA_GPU_PICK_OK " + hit) : "AVALONIA_GPU_OR_PICK_FAILED");
            if (Program.GizmoCheck || Program.ShortcutCheck) _closeConfirmed = true;
            Close();
        };
        if (IsVisible) timer.Start();
        else Opened += (_, _) => timer.Start();
    }

    private bool RunGizmoSmokeCheck()
    {
        var wall = _session.Model.Walls.FirstOrDefault(x => x.Id == "1F-S");
        var storey = wall == null ? null : _session.Model.FindStorey(wall.StoreyId);
        if (wall == null || storey == null) return false;
        _gizmo.SetSelection(wall, storey.Elevation + storey.Height / 2);
        _gizmo.SetTool(ViewTransformTool.Move);
        var center = _viewport.ProjectModelPoint((wall.X1 + wall.X2) / 2,
            (wall.Y1 + wall.Y2) / 2, storey.Elevation + storey.Height / 2);
        if (center == null || !_gizmo.TryBegin(center.Value, false)) return false;
        var originalX = wall.X1;
        var originalY = wall.Y1;
        _gizmo.Move(new Point(center.Value.X + 30, center.Value.Y - 15));
        _gizmo.Finish();
        var moved = _session.Model.Walls.First(x => x.Id == wall.Id);
        var success = _session.Revision > 0 && (Math.Abs(moved.X1 - originalX) > 1
            || Math.Abs(moved.Y1 - originalY) > 1);
        if (success)
        {
            _gizmo.SetTool(ViewTransformTool.Rotate);
            center = _viewport.ProjectModelPoint((moved.X1 + moved.X2) / 2,
                (moved.Y1 + moved.Y2) / 2, storey.Elevation + storey.Height / 2);
            var beforeRevision = _session.Revision;
            success = center != null && _gizmo.TryBegin(new Point(center.Value.X + 49, center.Value.Y), false);
            if (success)
            {
                _gizmo.Move(new Point(center!.Value.X, center.Value.Y - 49));
                _gizmo.Finish();
                success = _session.Revision == beforeRevision + 1;
            }
        }
        if (success)
        {
            var rotated = _session.Model.Walls.First(x => x.Id == wall.Id);
            _gizmo.SetTool(ViewTransformTool.Move);
            center = _viewport.ProjectModelPoint((rotated.X1 + rotated.X2) / 2,
                (rotated.Y1 + rotated.Y2) / 2, storey.Elevation + storey.Height / 2);
            var beforeWalls = _session.Model.Walls.Count;
            success = center != null && _gizmo.TryBegin(center.Value, true);
            if (success)
            {
                _gizmo.Move(new Point(center!.Value.X + 30, center.Value.Y - 15));
                _gizmo.Finish();
                success = _session.Model.Walls.Count == beforeWalls + 1;
            }
        }
        Console.WriteLine(success ? "AVALONIA_GIZMO_MOVE_ROTATE_COPY_OK" : "AVALONIA_GIZMO_CHECK_FAILED");
        return success;
    }

    private bool RunCameraSmokeCheck()
    {
        var original = _viewport.CameraAngles;
        _viewport.BeginInteraction(new Point(100, 100), true, false, false);
        _viewport.MoveInteraction(new Point(100, 130), true, false);
        _viewport.EndInteraction(new Point(100, 130));
        var stationary = _viewport.CameraAngles;
        _viewport.BeginInteraction(new Point(100, 100), true, false, true);
        _viewport.MoveInteraction(new Point(100, 130), true, false);
        _viewport.EndInteraction(new Point(100, 130));
        var orbit = _viewport.CameraAngles;
        var passed = original == stationary && orbit.Pitch < stationary.Pitch;
        var fixedPoint = _session.Model.Walls.FirstOrDefault(w => w.Id == "1F-S");
        if (fixedPoint != null)
        {
            var before = _viewport.ProjectModelPoint(fixedPoint.X1, fixedPoint.Y1,
                _session.Model.BaseElevationOf(fixedPoint));
            var reduced = BuildingModelJson.FromJson(BuildingModelJson.ToJson(_session.Model));
            reduced.Walls.RemoveAll(w => w.Id != "1F-S");
            reduced.Openings.Clear();
            _viewport.SetScene(ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(reduced)));
            var after = _viewport.ProjectModelPoint(fixedPoint.X1, fixedPoint.Y1,
                _session.Model.BaseElevationOf(fixedPoint));
            passed &= before != null && after != null
                && Math.Abs(before.Value.X - after.Value.X) < 0.1
                && Math.Abs(before.Value.Y - after.Value.Y) < 0.1;
            _viewport.SetScene(ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(_session.Model)));
        }
        Console.WriteLine(passed ? "AVALONIA_CAMERA_DRAG_OK" : "AVALONIA_CAMERA_DRAG_FAILED");
        _viewport.ResetView();
        return passed;
    }

    private bool RunShortcutSmokeCheck()
    {
        var original = _session.Model.Walls.First(x => x.Id == "1F-S").X2;
        if (!_session.TrySetWallLength("1F-S", original + 500, out _)) return false;
        _viewport.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control
        });
        var undone = _session.Model.Walls.First(x => x.Id == "1F-S").X2 == original;
        _viewport.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = Key.Y, KeyModifiers = KeyModifiers.Control
        });
        var redone = _session.Model.Walls.First(x => x.Id == "1F-S").X2 == original + 500;
        _viewport.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control
        });
        _viewport.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = Key.Z,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift
        });
        var shiftRedo = _session.Model.Walls.First(x => x.Id == "1F-S").X2 == original + 500;
        var revision = _session.Revision;
        var textBox = _properties.Children.OfType<TextBox>().FirstOrDefault();
        textBox?.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control
        });
        var textKeptModel = textBox != null && _session.Revision == revision;
        var shiftHorizontal = PlanEditorCanvas.ShiftDirection(new PointModel(0, 0),
            new PointModel(100, 20)) == PlanAxisConstraint.X;
        var shiftVertical = PlanEditorCanvas.ShiftDirection(new PointModel(0, 0),
            new PointModel(20, 100)) == PlanAxisConstraint.Y;
        var commands = ModelCommandCatalog.Resolve("m") == ModelCommandKind.Move
            && ModelCommandCatalog.Resolve("WA") == ModelCommandKind.Wall
            && ModelCommandCatalog.Resolve("ORTHO") == ModelCommandKind.Ortho
            && ModelCommandCatalog.Resolve(" CO ") == ModelCommandKind.Copy
            && ModelCommandCatalog.Resolve("mi") == ModelCommandKind.Mirror
            && ModelCommandCatalog.Resolve("f") == ModelCommandKind.Fillet
            && ModelCommandCatalog.TryPolarLength("@3000<45", out var polarDistance,
                out var polarAngle) && polarDistance == 3000 && polarAngle == 45
            && ModelCommandCatalog.TryDisplacement("@300,-25", out var commandX,
                out var commandY) && commandX == 300 && commandY == -25;
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.M });
        var typedM = _commandInput.Text == "M";
        _commandInput.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Space });
        var moving = typedM && _movingInViewport;
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        var moveCancelled = !_movingInViewport;
        ExecuteCommand("CO");
        var copying = _movingInViewport && _copyingInViewport;
        CancelMove();
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F8 });
        var orthoOn = _planCanvas.OrthogonalEnabled && !_planCanvas.PolarEnabled;
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F10 });
        var polarOn = !_planCanvas.OrthogonalEnabled && _planCanvas.PolarEnabled;
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.F10 });
        _viewport.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.W });
        _commandInput.Text += "A";
        _commandInput.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Space });
        var wallStarted = _workspaces.SelectedIndex == 1 && _planCanvas.Tool == PlanTool.Wall;
        _planCanvas.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        var wallCancelled = _planCanvas.Tool == PlanTool.Select;
        ExecuteCommand("DR");
        var doorCommand = _workspaces.SelectedIndex == 1 && _planCanvas.Tool == PlanTool.Door;
        ExecuteCommand("WN");
        var windowCommand = _workspaces.SelectedIndex == 1 && _planCanvas.Tool == PlanTool.Window;
        ExecuteCommand("3D");
        var modelCommand = _workspaces.SelectedIndex == 0;
        ExecuteCommand("PL");
        var planCommand = _workspaces.SelectedIndex == 1;
        var togglesKeepVisual = _orthoButton.Content is StackPanel
            && _polarButton.Content is StackPanel;
        var polarMath = PlanEditorCanvas.PolarPoint(new PointModel(0, 0),
            new PointModel(100, 20), 45);
        var polarSnaps = Math.Abs(polarMath.Y) < 0.001;
        var planMove = _planCanvas.BeginMove();
        _planCanvas.CancelDraft();
        var success = undone && redone && shiftRedo && textKeptModel
            && shiftHorizontal && shiftVertical && commands
            && moving && moveCancelled && copying && planMove && orthoOn
            && polarOn && wallStarted && wallCancelled && polarSnaps
            && doorCommand && windowCommand && modelCommand && planCommand
            && togglesKeepVisual;
        Console.WriteLine(success ? "AVALONIA_CTRL_Z_Y_SHIFT_Z_OK" : "AVALONIA_SHORTCUT_CHECK_FAILED");
        return success;
    }
}
