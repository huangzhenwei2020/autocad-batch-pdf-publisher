using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    private readonly BuildingModelEditSession _session;
    private readonly ModelViewport _viewport;
    private readonly ListBox _elements = new();
    private readonly List<ElementItem> _items = new();
    private readonly StackPanel _properties = new() { Margin = new Thickness(16), Spacing = 12 };
    private readonly TextBlock _status = new();
    private readonly Button _undo = new() { Content = "撤销" };
    private readonly Button _redo = new() { Content = "重做" };
    private string? _selectedId;

    public ProbeWindow()
    {
        Title = "万落建筑模型 · 跨平台编辑探针";
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 520;
        _session = new BuildingModelEditSession(SampleModelFactory.CreateTwoStoreyHouse());
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
        _undo.Click += (_, _) => { if (_session.Undo()) RefreshModel("已撤销"); };
        _redo.Click += (_, _) => { if (_session.Redo()) RefreshModel("已重做"); };
        toolbar.Children.Add(_undo);
        toolbar.Children.Add(_redo);
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
        _status.Text = "G1 技术探针 · 样例模型只在内存中编辑，不保存、不修改 CAD";
        Grid.SetRow(_status, 2);
        Grid.SetColumnSpan(_status, 3);
        root.Children.Add(_status);
        Content = root;

        BuildElementList();
        SelectById("1F-S");
        RefreshHistoryButtons();
        ConfigureSmokeAndSnapshot();
    }

    private void BuildElementList()
    {
        var model = _session.Model;
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
            apply.Click += (_, _) => ApplyNumber(field.Text, value =>
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
            apply.Click += (_, _) => ApplyNumber(field.Text, value =>
            {
                var success = _session.TrySetOpeningOffset(opening.Id, value, out var error);
                return (success, error);
            });
            _properties.Children.Add(apply);
        }
        else _properties.Children.Add(new TextBlock { Text = "此构件当前只支持选择。" });
    }

    private void ApplyNumber(string? text, Func<double, (bool success, string? error)> edit)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            _status.Text = "请输入有效的毫米数值。";
            return;
        }
        var result = edit(value);
        if (!result.success) { _status.Text = result.error; return; }
        RefreshModel("已更新构件 " + _selectedId);
    }

    private void RefreshModel(string message)
    {
        _viewport.SetVolume(BuildingVolumeBuilder.Build(_session.Model));
        RefreshProperties();
        RefreshHistoryButtons();
        _status.Text = $"{message} · 修订 {_session.Revision} · 仅内存试验";
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
