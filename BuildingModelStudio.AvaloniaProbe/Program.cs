using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using System.Diagnostics;
using System.Numerics;
using BatchPdfPublisher.BuildingModel;

namespace BuildingModelStudio.AvaloniaProbe;

internal static class Program
{
    public static bool Smoke { get; private set; }
    public static bool SmokeFailed { get; set; }
    public static bool CadGenerationCheck { get; private set; }
    public static int GpuBenchCount { get; private set; }
    public static string? SnapshotPath { get; private set; }
    public static bool SnapshotPlan { get; private set; }
    public static bool SnapshotCompact { get; private set; }
    public static Size? SnapshotSize { get; private set; }
    public static bool SnapshotProperties { get; private set; }
    public static bool SnapshotAxes { get; private set; }
    public static bool SnapshotStoreys { get; private set; }
    public static bool SnapshotGizmo { get; private set; }
    public static ModelViewport.DisplayMode? SnapshotDisplayMode { get; private set; }
    public static string? SnapshotRibbon { get; private set; }
    public static Vector3? SnapshotCamera { get; private set; }
    public static bool GizmoCheck { get; private set; }
    public static bool ShortcutCheck { get; private set; }
    public static string? ModelPath { get; private set; }
    public static bool CreateMissingProjectModel { get; private set; }
    public static string? ProjectModelName { get; private set; }
    public static string? ProjectFolder { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--pick-check", StringComparer.OrdinalIgnoreCase))
        {
            RunPickCheck();
            return 0;
        }
        var gpuBenchIndex = Array.IndexOf(args, "--gpu-bench");
        if (gpuBenchIndex >= 0)
        {
            GpuBenchCount = ParseBenchmarkCount(args, gpuBenchIndex, "--gpu-bench");
            AppBuilder.Configure<ProbeApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
            return SmokeFailed ? 1 : 0;
        }
        var benchIndex = Array.IndexOf(args, "--bench");
        if (benchIndex >= 0)
        {
            RunBenchmark(ParseBenchmarkCount(args, benchIndex, "--bench"));
            return 0;
        }
        Smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        CadGenerationCheck = args.Contains("--cad-generation-check", StringComparer.OrdinalIgnoreCase);
        var cameraIndex = Array.IndexOf(args, "--snapshot-camera");
        if (cameraIndex >= 0)
        {
            var parts = cameraIndex + 1 < args.Length ? args[cameraIndex + 1].Split(',') : Array.Empty<string>();
            if (parts.Length != 3) throw new ArgumentException("--snapshot-camera requires yaw,pitch,distance.");
            var values = parts.Select(p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (values.Any(v => !float.IsFinite(v)) || Math.Abs(values[1]) >= 89 || values[2] < 2)
                throw new ArgumentException("Invalid snapshot camera.");
            SnapshotCamera = new Vector3(values[0] * MathF.PI / 180, values[1] * MathF.PI / 180, values[2]);
        }
        var ribbonIndex = Array.IndexOf(args, "--snapshot-ribbon");
        if (ribbonIndex >= 0 && ribbonIndex + 1 < args.Length) SnapshotRibbon = args[ribbonIndex + 1];
        SnapshotGizmo = args.Contains("--snapshot-gizmo", StringComparer.OrdinalIgnoreCase);
        var displayModeIndex = Array.IndexOf(args, "--snapshot-display-mode");
        if (displayModeIndex >= 0)
        {
            if (displayModeIndex + 1 >= args.Length
                || !Enum.TryParse<ModelViewport.DisplayMode>(args[displayModeIndex + 1],
                    true, out var displayMode))
                throw new ArgumentException("--snapshot-display-mode 应为 Wireframe、Solid、Shaded 或 Lit。");
            SnapshotDisplayMode = displayMode;
        }
        GizmoCheck = args.Contains("--gizmo-check", StringComparer.OrdinalIgnoreCase);
        ShortcutCheck = args.Contains("--shortcut-check", StringComparer.OrdinalIgnoreCase);
        var index = Array.IndexOf(args, "--snapshot");
        if (index >= 0 && index + 1 < args.Length) SnapshotPath = Path.GetFullPath(args[index + 1]);
        index = Array.IndexOf(args, "--snapshot-plan");
        if (index >= 0 && index + 1 < args.Length)
        {
            SnapshotPath = Path.GetFullPath(args[index + 1]);
            SnapshotPlan = true;
        }
        index = Array.IndexOf(args, "--snapshot-compact");
        if (index >= 0 && index + 1 < args.Length)
        {
            SnapshotPath = Path.GetFullPath(args[index + 1]);
            SnapshotPlan = true;
            SnapshotCompact = true;
        }
        index = Array.IndexOf(args, "--snapshot-size");
        if (index >= 0)
        {
            var dimensions = index + 1 < args.Length ? args[index + 1].Split('x', 'X') : Array.Empty<string>();
            if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width)
                || !int.TryParse(dimensions[1], out var height) || width < 800 || height < 560)
                throw new ArgumentException("--snapshot-size 需要至少 800x560 的窗口尺寸，例如 820x600。");
            SnapshotSize = new Size(width, height);
        }
        index = Array.IndexOf(args, "--snapshot-properties");
        if (index >= 0 && index + 1 < args.Length)
        {
            SnapshotPath = Path.GetFullPath(args[index + 1]);
            SnapshotPlan = true;
            SnapshotProperties = true;
        }
        index = Array.IndexOf(args, "--snapshot-axes");
        if (index >= 0 && index + 1 < args.Length)
        {
            SnapshotPath = Path.GetFullPath(args[index + 1]);
            SnapshotAxes = true;
        }
        index = Array.IndexOf(args, "--snapshot-storeys");
        if (index >= 0 && index + 1 < args.Length)
        {
            SnapshotPath = Path.GetFullPath(args[index + 1]);
            SnapshotStoreys = true;
        }
        var projectIndex = Array.IndexOf(args, "--project");
        var cadProjectIndex = Array.IndexOf(args, "--cad-project");
        if (cadProjectIndex >= 0 && cadProjectIndex + 1 < args.Length) ProjectFolder = Path.GetFullPath(args[cadProjectIndex + 1]);
        var modelIndex = Array.IndexOf(args, "--model");
        if (projectIndex >= 0)
        {
            if (projectIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[projectIndex + 1]))
                throw new ArgumentException("--project 后必须提供项目文件夹。");
            var folder = Path.GetFullPath(args[projectIndex + 1]);
            ProjectFolder = folder;
            ProjectModelName = modelIndex >= 0 && modelIndex + 1 < args.Length
                ? args[modelIndex + 1] : "建筑模型";
            if (string.IsNullOrWhiteSpace(ProjectModelName)) ProjectModelName = "建筑模型";
            ModelPath = BuildingModelJson.ModelFilePath(folder, ProjectModelName);
            CreateMissingProjectModel = true;
        }
        else if (modelIndex >= 0)
        {
            if (modelIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[modelIndex + 1]))
                throw new ArgumentException("--model 后必须提供 model.json 路径。");
            ModelPath = Path.GetFullPath(args[modelIndex + 1]);
        }
        var exportIndex = Array.IndexOf(args, "--export-glb");
        if (exportIndex >= 0)
        {
            try
            {
                if (exportIndex + 1 >= args.Length) throw new ArgumentException("--export-glb 后需提供文件路径。");
                var floorIndex = Array.IndexOf(args, "--export-storey");
                if (floorIndex >= 0 && floorIndex + 1 >= args.Length)
                    throw new ArgumentException("--export-storey 后需提供楼层 ID。");
                var model = ModelPath == null ? SampleModelFactory.CreateTwoStoreyHouse()
                    : BuildingModelJson.LoadModel(ModelPath);
                var result = BuildingModelGlbExporter.Export(model, args[exportIndex + 1],
                    floorIndex < 0 ? null : args[floorIndex + 1]);
                Console.WriteLine($"GLB_EXPORT_OK storeys={result.StoreyCount} elements={result.ElementCount} triangles={result.TriangleCount} bytes={result.FileBytes}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return SmokeFailed ? 1 : 0;
    }

    private static int ParseBenchmarkCount(string[] args, int index, string option)
    {
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var count)
            || count < 1 || count > 50000)
            throw new ArgumentException($"{option} 后需跟 1 到 50000 的构件数。");
        return count;
    }

    private static void RunPickCheck()
    {
        var scene = ModelViewport.PrepareScene(BuildingVolumeBuilder.Build(SampleModelFactory.CreateTwoStoreyHouse()));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, 760f / 715f, 0.1f, 100f);
        var checkedPoints = 0;
        foreach (var yaw in new[] { -0.8f, 0.4f, 1.5f })
        {
            var transform = ModelViewport.OrbitView(yaw, 0.45f, 19.72f, Vector3.Zero) * projection;
            for (var row = 0; row < 25; row++)
            for (var col = 0; col < 25; col++)
            {
                var x = (col - 12f) / 13f;
                var y = (row - 12f) / 13f;
                var indexed = ModelViewport.PickForBenchmark(scene, transform, x, y);
                var reference = ModelViewport.PickBruteRayForCheck(scene, transform, x, y);
                if (indexed != reference)
                    throw new InvalidOperationException($"拾取不一致：yaw={yaw}, x={x}, y={y}, BVH={indexed}, 全射线扫描={reference}");
                checkedPoints++;
            }
        }
        Console.WriteLine($"PICK_CHECK_OK points={checkedPoints} views=3");
    }

    private static void RunBenchmark(int count)
    {
        var model = CreateBenchmarkModel(count);
        var watch = Stopwatch.StartNew();
        var volume = BuildingVolumeBuilder.Build(model);
        var volumeMs = watch.ElapsedMilliseconds;
        var scene = ModelViewport.PrepareScene(volume);
        var totalMs = watch.ElapsedMilliseconds;
        var transform = ModelViewport.OrbitView(0.4f, 0.45f, 19.72f, Vector3.Zero)
            * Matrix4x4.CreatePerspectiveFieldOfView(0.8f, 760f / 715f, 0.1f, 100f);
        ModelViewport.PickForBenchmark(scene, transform, 0f, 0f);
        var durations = new double[200];
        var hits = 0;
        for (var i = 0; i < durations.Length; i++)
        {
            var x = (i % 20 - 9.5f) / 12f;
            var y = (i / 20 - 4.5f) / 7f;
            var start = Stopwatch.GetTimestamp();
            if (ModelViewport.PickForBenchmark(scene, transform, x, y) != null) hits++;
            durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(durations);
        GC.Collect();
        var managedMb = GC.GetTotalMemory(true) / 1048576d;
        Console.WriteLine($"BENCH elements={count} faces={volume.Faces.Count} triangles={scene.TriangleCount} "
            + $"volumeMs={volumeMs} sceneMs={totalMs - volumeMs} totalMs={totalMs} "
            + $"pickP95Ms={durations[189]:0.###} hits={hits} managedMB={managedMb:0.0}");
    }

    internal static BuildingModelDocument CreateBenchmarkModel(int count)
    {
        var model = new BuildingModelDocument { Name = "视口性能样例" };
        model.Storeys.Add(new StoreyModel { Id = "1F", Name = "一层", Height = 3000 });
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        for (var i = 0; i < count; i++)
        {
            var x = (i % columns) * 1300d;
            var y = (i / columns) * 1300d;
            model.Walls.Add(new WallModel
            {
                Id = "W-" + i, StoreyId = "1F", X1 = x, Y1 = y,
                X2 = x + 1000d, Y2 = y, Thickness = 200d
            });
        }
        return model;
    }
}

internal sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ProbeWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
