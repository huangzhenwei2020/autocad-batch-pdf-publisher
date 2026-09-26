using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class ProbeWindow : Window
{
    private readonly ModelViewport _viewport;

    public ProbeWindow()
    {
        Title = "万落建筑模型 · 跨平台视口探针";
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 520;
        var model = SampleModelFactory.CreateTwoStoreyHouse();
        var volume = BuildingVolumeBuilder.Build(model);
        _viewport = new ModelViewport(volume);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("46,*,30"),
            ColumnDefinitions = new ColumnDefinitions("230,*,290"),
            Background = new SolidColorBrush(Color.Parse("#151B23"))
        };
        var top = new TextBlock
        {
            Text = "建筑建模     平面编辑     立面剖面     图纸发布",
            FontSize = 16, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0)
        };
        Grid.SetColumnSpan(top, 3);
        root.Children.Add(top);

        var tree = new StackPanel { Margin = new Thickness(14), Spacing = 12 };
        tree.Children.Add(new TextBlock { Text = "项目构件", FontSize = 20, FontWeight = FontWeight.Bold });
        tree.Children.Add(new TextBlock { Text = "▾  双层住宅样例" });
        foreach (var floor in model.Storeys)
        {
            tree.Children.Add(new TextBlock { Text = "  ▾  " + floor.Name });
            tree.Children.Add(new TextBlock { Text = "      墙 · 门窗 · 楼板" });
        }
        Grid.SetRow(tree, 1);
        root.Children.Add(tree);

        Grid.SetRow(_viewport, 1);
        Grid.SetColumn(_viewport, 1);
        root.Children.Add(_viewport);

        var properties = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        properties.Children.Add(new TextBlock { Text = "属性", FontSize = 20, FontWeight = FontWeight.Bold });
        properties.Children.Add(new TextBlock { Text = "只读试验 · 当前建筑模型" });
        properties.Children.Add(new TextBlock { Text = $"楼层 {model.Storeys.Count} · 墙 {model.Walls.Count} · 洞口 {model.Openings.Count}" });
        properties.Children.Add(new TextBlock { Text = $"显示面 {volume.Faces.Count}" });
        properties.Children.Add(new TextBlock { Text = "视口实绘、缩放与跨平台运行仍须验收。", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(properties, 1);
        Grid.SetColumn(properties, 2);
        root.Children.Add(properties);

        var footer = new TextBlock
        {
            Text = "G1 技术探针  ·  模型参数来自现有 Shared/BuildingModel  ·  不保存、不修改 CAD",
            Foreground = new SolidColorBrush(Color.Parse("#A4B8CF")),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0)
        };
        Grid.SetRow(footer, 2);
        Grid.SetColumnSpan(footer, 3);
        root.Children.Add(footer);
        Content = root;

        if (Program.Smoke || Program.SnapshotPath != null)
        {
            var started = DateTime.UtcNow;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timeout.Tick += async (_, _) =>
            {
                if (!_viewport.FrameRendered && DateTime.UtcNow - started < TimeSpan.FromSeconds(12)) return;
                timeout.Stop();
                if (Program.SnapshotPath != null && _viewport.FrameRendered)
                {
                    await Task.Delay(200);
                    var visual = ElementComposition.GetElementVisual(this);
                    if (visual == null) throw new InvalidOperationException("Composition visual unavailable");
                    var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
                    snapshot.Save(Program.SnapshotPath, PngBitmapEncoderOptions.Default);
                    Console.WriteLine("AVALONIA_SNAPSHOT " + Program.SnapshotPath);
                }
                Environment.ExitCode = _viewport.FrameRendered ? 0 : 1;
                Console.WriteLine(_viewport.FrameRendered ? "AVALONIA_GPU_FRAME_OK" : "AVALONIA_GPU_FRAME_TIMEOUT");
                Close();
            };
            Opened += (_, _) => timeout.Start();
        }
    }
}
