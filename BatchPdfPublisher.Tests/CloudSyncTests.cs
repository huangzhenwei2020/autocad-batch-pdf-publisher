using System;
using System.IO;
using System.Linq;
using BatchPdfPublisher.Services;

internal static class CloudSyncTests
{
    private static int _executed;

    private static void Main()
    {
        Run("UploadsNewLocalFile", UploadsNewLocalFile);
        Run("DownloadsToSecondDevice", DownloadsToSecondDevice);
        Run("PreservesBothSidesOnConflict", PreservesBothSidesOnConflict);
        Run("FirstConnectionCanPreferRemoteWithBackup", FirstConnectionCanPreferRemoteWithBackup);
        Run("PropagatesRemoteDeletionWithBackup", PropagatesRemoteDeletionWithBackup);
        Run("RejectsOverlappingRoots", RejectsOverlappingRoots);
        Run("DefersOpenDrawingUntilClosed", DefersOpenDrawingUntilClosed);
        Run("MapsProjectFileOnlyOnce", MapsProjectFileOnlyOnce);
        Run("DefersRemoteDeletionUntilDrawingCloses", DefersRemoteDeletionUntilDrawingCloses);
        Console.WriteLine("Executed " + _executed + " cloud sync tests; 0 failed.");
    }

    private static void UploadsNewLocalFile()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            File.WriteAllText(Path.Combine(local, "settings.json"), "one");
            var result = engine.Synchronize(settings, catalog);
            Equal(1, result.Uploaded);
            Equal("one", File.ReadAllText(Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json")));
        });
    }

    private static void DownloadsToSecondDevice()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            File.WriteAllText(Path.Combine(local, "settings.json"), "one");
            engine.Synchronize(settings, catalog);
            var second = Path.Combine(root, "second"); Directory.CreateDirectory(second);
            var secondStore = new CloudSyncSettingsStore(Path.Combine(root, "second-settings.json"), Path.Combine(root, "second-state.json"));
            var secondEngine = new LocalFolderSyncEngine(secondStore, Path.Combine(root, "second-history"));
            var secondCatalog = new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", second, null) });
            var result = secondEngine.Synchronize(settings, secondCatalog);
            Equal(1, result.Downloaded);
            Equal("one", File.ReadAllText(Path.Combine(second, "settings.json")));
        });
    }

    private static void PreservesBothSidesOnConflict()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.WriteAllText(localFile, "local-change");
            File.WriteAllText(remoteFile, "remote-change");
            var result = engine.Synchronize(settings, catalog);
            Equal(1, result.Conflicts);
            Equal("local-change", File.ReadAllText(localFile));
            Equal("remote-change", File.ReadAllText(remoteFile));
            var conflictFiles = Directory.GetFiles(Path.Combine(shared, "万落建筑云同步", "冲突文件"), "*", SearchOption.AllDirectories);
            Equal(2, conflictFiles.Length);
        });
    }

    private static void FirstConnectionCanPreferRemoteWithBackup()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(remoteFile));
            File.WriteAllText(localFile, "generated-default");
            File.WriteAllText(remoteFile, "cloud-current");
            settings.InitialSyncPreference = "Remote";
            var result = engine.Synchronize(settings, catalog);
            Equal(1, result.Downloaded);
            Equal("cloud-current", File.ReadAllText(localFile));
            True(Directory.GetFiles(Path.Combine(root, "history"), "*", SearchOption.AllDirectories).Any(), "local backup missing");
        });
    }

    private static void PropagatesRemoteDeletionWithBackup()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.Delete(remoteFile);
            var result = engine.Synchronize(settings, catalog);
            Equal(1, result.Deleted);
            True(!File.Exists(localFile), "local file should be deleted");
            True(Directory.GetFiles(Path.Combine(root, "history"), "*", SearchOption.AllDirectories).Any(), "history backup missing");
        });
    }

    private static void RejectsOverlappingRoots()
    {
        var root = NewRoot();
        try
        {
            var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
            var settings = new CloudSyncSettings { Enabled = true, SyncFolder = local };
            var store = new CloudSyncSettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "state.json"));
            var engine = new LocalFolderSyncEngine(store, Path.Combine(root, "history"));
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", local, null) });
            Throws<InvalidOperationException>(() => engine.Synchronize(settings, catalog));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DefersOpenDrawingUntilClosed()
    {
        WithWorkspace((root, local, shared, engine, settings, ignored) =>
        {
            UserDataPaths.TestRootDirectory = root;
            var drawing = Path.Combine(local, "plan.dwg");
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("项目文件/test", local, null) });
            File.WriteAllText(drawing, "base");
            engine.Synchronize(settings, catalog);
            var remote = Path.Combine(shared, "万落建筑云同步", "项目文件", "test", "plan.dwg");
            File.WriteAllText(remote, "remote-change");
            CloudSyncPendingFileService.RegisterOpenPathProbe(path => true);
            var deferred = engine.Synchronize(settings, catalog);
            Equal(1, deferred.Pending);
            Equal("base", File.ReadAllText(drawing));
            CloudSyncPendingFileService.RegisterOpenPathProbe(path => false);
            engine.Synchronize(settings, catalog);
            Equal("remote-change", File.ReadAllText(drawing));
            CloudSyncPendingFileService.ClearOpenPathProbe();
        });
    }

    private static void MapsProjectFileOnlyOnce()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var project = Path.Combine(UserDataPaths.ProjectsDirectory, "示例项目");
            Directory.CreateDirectory(project);
            var drawing = Path.Combine(project, "plan.dwg");
            var data = Path.Combine(project, "project.json");
            File.WriteAllText(drawing, "dwg"); File.WriteAllText(data, "json");
            var settings = new CloudSyncSettings
            {
                SyncGeneralSettings = false,
                SyncProjectConfigurations = true,
                SyncTemplatesAndSchemes = false,
                SyncProjectFiles = true,
                ProjectMappings = new System.Collections.Generic.List<CloudSyncProjectMapping>
                {
                    new CloudSyncProjectMapping { ProjectName = "示例项目", CloudId = "sample", LocalFolder = project, Enabled = true }
                }
            };
            var files = CloudSyncCatalog.CreateDefault(settings).EnumerateFiles().ToList();
            Equal(1, files.Count(item => string.Equals(item.LocalPath, drawing, StringComparison.OrdinalIgnoreCase)));
            Equal(1, files.Count(item => string.Equals(item.LocalPath, data, StringComparison.OrdinalIgnoreCase)));
            True(files.Any(item => item.LogicalPath == "项目文件/sample/plan.dwg"), "drawing should use project mapping");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DefersRemoteDeletionUntilDrawingCloses()
    {
        WithWorkspace((root, local, shared, engine, settings, ignored) =>
        {
            UserDataPaths.TestRootDirectory = root;
            var drawing = Path.Combine(local, "delete-me.dwg");
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("项目文件/test", local, null) });
            File.WriteAllText(drawing, "base"); engine.Synchronize(settings, catalog);
            File.Delete(Path.Combine(shared, "万落建筑云同步", "项目文件", "test", "delete-me.dwg"));
            CloudSyncPendingFileService.RegisterOpenPathProbe(path => true);
            var deferred = engine.Synchronize(settings, catalog);
            Equal(1, deferred.Pending); True(File.Exists(drawing), "open drawing was deleted");
            CloudSyncPendingFileService.RegisterOpenPathProbe(path => false);
            engine.Synchronize(settings, catalog);
            True(!File.Exists(drawing), "pending remote deletion was not applied after close");
            CloudSyncPendingFileService.ClearOpenPathProbe();
        });
    }

    private static void WithWorkspace(Action<string, string, string, LocalFolderSyncEngine, CloudSyncSettings, CloudSyncCatalog> action)
    {
        var root = NewRoot();
        try
        {
            var local = Path.Combine(root, "local"); var shared = Path.Combine(root, "shared");
            Directory.CreateDirectory(local); Directory.CreateDirectory(shared);
            var store = new CloudSyncSettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "state.json"));
            var engine = new LocalFolderSyncEngine(store, Path.Combine(root, "history"));
            var settings = new CloudSyncSettings { Enabled = true, SyncFolder = shared, DeviceName = "TEST-PC" };
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", local, null) });
            action(root, local, shared, engine, settings, catalog);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "WanluoCloudSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static void Run(string name, Action test)
    {
        test(); _executed++; Console.WriteLine("PASS " + name);
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual);
    }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
