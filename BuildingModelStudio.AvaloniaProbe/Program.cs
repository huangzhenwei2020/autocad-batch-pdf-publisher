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
    public static int GpuBenchCount { get; private set; }
    public static string? SnapshotPath { get; private set; }
    public static bool SnapshotPlan { get; private set; }
    public static bool SnapshotGizmo { get; private set; }
    public static bool GizmoCheck { get; private set; }
    public static bool ShortcutCheck { get; private set; }
    public static string? ModelPath { get; private set; }
    public static bool CreateMissingProjectModel { get; private set; }
    public static string? ProjectModelName { get; private set; }

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
        SnapshotGizmo = args.Contains("--snapshot-gizmo", StringComparer.OrdinalIgnoreCase);
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
        var projectIndex = Array.IndexOf(args, "--project");
        var modelIndex = Array.IndexOf(args, "--model");
        if (projectIndex >= 0)
        {
            if (projectIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[projectIndex + 1]))
                throw new ArgumentException("--project 后必须提供项目文件夹。");
            var folder = Path.GetFullPath(args[projectIndex + 1]);
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
        var view = Matrix4x4.CreateLookAt(new Vector3(11, 10, 13), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.8f, 760f / 715f, 0.1f, 100f);
        var checkedPoints = 0;
        foreach (var yaw in new[] { -0.8f, 0.4f, 1.5f })
        {
            var transform = Matrix4x4.CreateFromYawPitchRoll(yaw, -0.2f, 0) * view * projection;
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
        var transform = Matrix4x4.CreateFromYawPitchRoll(0.4f, -0.2f, 0)
            * Matrix4x4.CreateLookAt(new Vector3(11, 10, 13), Vector3.Zero, Vector3.UnitY)
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
