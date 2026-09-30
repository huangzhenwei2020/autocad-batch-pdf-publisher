using System.Text.Json;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class BlenderIntegration
{
    public bool OpenAfterExport { get; set; } = true;
    public string? ExecutablePath { get; set; }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "WanLuo", "BuildingModel", "blender-settings.json");

    public static BlenderIntegration Load()
    {
        try { return JsonSerializer.Deserialize<BlenderIntegration>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, SettingsPath, true);
    }

    public string? FindExecutable()
    {
        if (ExecutablePath != null && File.Exists(ExecutablePath)) return ExecutablePath;
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Blender Foundation"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var app = "/Applications/Blender.app/Contents/MacOS/Blender";
            if (File.Exists(app)) return app;
        }
        var candidates = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.GetDirectories(root).Select(d => Path.Combine(d, "blender.exe"))
                        .Where(File.Exists));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        // Multiple installed versions: prefer the newest executable version.
        var found = candidates.OrderByDescending(p => Version.TryParse(
            System.Diagnostics.FileVersionInfo.GetVersionInfo(p).FileVersion, out var version)
            ? version : new Version()).FirstOrDefault();
        if (found != null) return found;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "blender.exe" : "blender");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
