using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;

namespace BuildingModelStudio.AvaloniaProbe;

internal static class Program
{
    public static bool Smoke { get; private set; }
    public static string? SnapshotPath { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        Smoke = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        var index = Array.IndexOf(args, "--snapshot");
        if (index >= 0 && index + 1 < args.Length) SnapshotPath = Path.GetFullPath(args[index + 1]);
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
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
