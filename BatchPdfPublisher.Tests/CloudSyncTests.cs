using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using BatchPdfPublisher.Services;

internal static class CloudSyncTests
{
    private static int _executed;

    private static void Main()
    {
        Run("UploadsNewLocalFile", UploadsNewLocalFile);
        Run("DownloadsToSecondDevice", DownloadsToSecondDevice);
        Run("UsesNewerLocalFile", UsesNewerLocalFile);
        Run("UsesNewerRemoteFile", UsesNewerRemoteFile);
        Run("KeepsConflictWhenTimesMatch", KeepsConflictWhenTimesMatch);
        Run("RepairsMissingRemoteFileFromLocal", RepairsMissingRemoteFileFromLocal);
        Run("RejectsOverlappingRoots", RejectsOverlappingRoots);
        Run("DefersOpenDrawingUntilClosed", DefersOpenDrawingUntilClosed);
        Run("MapsProjectFileOnlyOnce", MapsProjectFileOnlyOnce);
        Run("ExcludesMachineSpecificProjectList", ExcludesMachineSpecificProjectList);
        Run("PackagesSystemFilesAndDefersChangedPackage", PackagesSystemFilesAndDefersChangedPackage);
        Run("FirstConnectionPrefersExistingRemoteSystemPackage", FirstConnectionPrefersExistingRemoteSystemPackage);
        Run("IncludesNormalProjectAttachments", IncludesNormalProjectAttachments);
        Run("IgnoresUnselectedRemoteProject", IgnoresUnselectedRemoteProject);
        Run("BaiduCachesOnlySelectedProjects", BaiduCachesOnlySelectedProjects);
        Run("RepairsMissingRemoteDrawingEvenWhenOpen", RepairsMissingRemoteDrawingEvenWhenOpen);
        Run("RestoresHistoryWithBackup", RestoresHistoryWithBackup);
        Run("ListsWorkFilesAndTheirHistory", ListsWorkFilesAndTheirHistory);
        Run("ResolvesConflictUsingLocalCopy", ResolvesConflictUsingLocalCopy);
        Run("CreatesProviderWithoutChangingLocalMode", CreatesProviderWithoutChangingLocalMode);
        Run("NewInstallationDefaultsToBaidu", NewInstallationDefaultsToBaidu);
        Run("EmptyLocalFolderMigratesToBaidu", EmptyLocalFolderMigratesToBaidu);
        Run("ProtectsProviderCredentialsWithDpapi", ProtectsProviderCredentialsWithDpapi);
        Run("RunsLocalProviderWorkflow", RunsLocalProviderWorkflow);
        Run("ChangingProviderScopeCannotDeleteLocalFiles", ChangingProviderScopeCannotDeleteLocalFiles);
        Run("IdentifiesCommonCloudFolders", IdentifiesCommonCloudFolders);
        Run("MigratesNutstoreInternalRoot", MigratesNutstoreInternalRoot);
        Run("ReportsSynchronizationProgress", ReportsSynchronizationProgress);
        Run("CancelsLargeFileSynchronizationSafely", CancelsLargeFileSynchronizationSafely);
        Run("DoesNotDuplicateUnresolvedConflict", DoesNotDuplicateUnresolvedConflict);
        Run("BuildsBaiduAuthorizationUrl", BuildsBaiduAuthorizationUrl);
        Run("RejectsUnsafeBaiduRemotePath", RejectsUnsafeBaiduRemotePath);
        Run("ExchangesBaiduAuthorizationCode", ExchangesBaiduAuthorizationCode);
        Run("ValidatesBaiduOAuthCallback", ValidatesBaiduOAuthCallback);
        Run("DownloadsBaiduFileThroughMultimediaMetadata", DownloadsBaiduFileThroughMultimediaMetadata);
        Run("DecryptsUnifiedBrokerToken", DecryptsUnifiedBrokerToken);
        Run("RecoversCorruptSettingsFromBackup", RecoversCorruptSettingsFromBackup);
        Console.WriteLine("Executed " + _executed + " cloud sync tests; 0 failed.");
    }

    private static void RecoversCorruptSettingsFromBackup()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var settingsPath = Path.Combine(root, "settings.json");
            var statePath = Path.Combine(root, "state.json");
            var store = new CloudSyncSettingsStore(settingsPath, statePath);
            store.SaveSettings(new CloudSyncSettings { DeviceName = "backup-version" });
            store.SaveSettings(new CloudSyncSettings { DeviceName = "current-version" });
            File.WriteAllText(settingsPath, "{broken");
            var recovered = store.LoadSettings();
            Equal("backup-version", recovered.DeviceName);
            True(Directory.GetFiles(root, "settings.json.corrupt-*", SearchOption.TopDirectoryOnly).Length == 1,
                "corrupt settings were not quarantined");
            True(!string.IsNullOrWhiteSpace(CloudSyncSettingsStore.LastRecoveryNotice), "recovery notice missing");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

    private static void UsesNewerLocalFile()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.WriteAllText(remoteFile, "remote-change"); File.SetLastWriteTimeUtc(remoteFile, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllText(localFile, "local-change"); File.SetLastWriteTimeUtc(localFile, DateTime.UtcNow);
            var result = engine.Synchronize(settings, catalog);
            Equal("local-change", File.ReadAllText(localFile));
            Equal("local-change", File.ReadAllText(remoteFile)); Equal(1, result.Uploaded);
        });
    }

    private static void UsesNewerRemoteFile()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.WriteAllText(localFile, "local-old"); File.SetLastWriteTimeUtc(localFile, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllText(remoteFile, "cloud-current"); File.SetLastWriteTimeUtc(remoteFile, DateTime.UtcNow);
            var result = engine.Synchronize(settings, catalog);
            Equal("cloud-current", File.ReadAllText(localFile)); Equal(1, result.Downloaded);
        });
    }

    private static void KeepsConflictWhenTimesMatch()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json"); var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            var same = DateTime.UtcNow.AddMinutes(1); File.WriteAllText(localFile, "local"); File.SetLastWriteTimeUtc(localFile, same); File.WriteAllText(remoteFile, "remote"); File.SetLastWriteTimeUtc(remoteFile, same);
            var result = engine.Synchronize(settings, catalog); Equal(1, result.Conflicts); Equal("local", File.ReadAllText(localFile)); Equal("remote", File.ReadAllText(remoteFile));
        });
    }

    private static void RepairsMissingRemoteFileFromLocal()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.Delete(remoteFile);
            var result = engine.Synchronize(settings, catalog);
            Equal(1, result.Uploaded);
            Equal("base", File.ReadAllText(localFile));
            Equal("base", File.ReadAllText(remoteFile));
            True(result.Operations.Any(item => item.Kind == CloudSyncOperationKind.Upload && item.Message.Contains("补传")), "missing remote file was not reported as repair upload");
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

    private static void IgnoresUnselectedRemoteProject()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var local = Path.Combine(root, "selected-project");
            var shared = Path.Combine(root, "shared");
            Directory.CreateDirectory(local);
            var mirror = Path.Combine(shared, "万落建筑云同步", "项目文件");
            Directory.CreateDirectory(Path.Combine(mirror, "selected"));
            Directory.CreateDirectory(Path.Combine(mirror, "unselected"));
            File.WriteAllText(Path.Combine(mirror, "selected", "selected.dwg"), "selected");
            var untouched = Path.Combine(mirror, "unselected", "unselected.dwg");
            File.WriteAllText(untouched, "unselected");
            var settings = new CloudSyncSettings { Enabled = true, SyncFolder = shared, InitialSyncPreference = "Remote" };
            var store = new CloudSyncSettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "state.json"));
            var engine = new LocalFolderSyncEngine(store, Path.Combine(root, "history"));
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("项目文件/selected", local, null) });
            var result = engine.Synchronize(settings, catalog, shared);
            Equal(1, result.Downloaded);
            Equal(0, result.Errors);
            True(File.Exists(Path.Combine(local, "selected.dwg")), "selected project was not downloaded");
            True(File.Exists(untouched), "unselected remote project was changed");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ExcludesMachineSpecificProjectList()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.ProjectsDirectory);
            File.WriteAllText(Path.Combine(UserDataPaths.ProjectsDirectory, "项目列表.json"), "[{\"ProjectFolder\":\"D:\\\\old-pc\"}]");
            File.WriteAllText(Path.Combine(UserDataPaths.ProjectsDirectory, "当前项目.txt"), "旧电脑项目");
            var projection = Path.Combine(UserDataPaths.ProjectsDirectory, "同步项目", "sample", "项目.json");
            Directory.CreateDirectory(Path.GetDirectoryName(projection)); File.WriteAllText(projection, "{}");
            var settings = new CloudSyncSettings { SyncGeneralSettings = false, SyncProjectConfigurations = true, SyncTemplatesAndSchemes = false };
            True(CloudSystemPackageService.Prepare(settings, true, null, CancellationToken.None), "system package was not created");
            var files = CloudSyncCatalog.CreateDefault(settings).EnumerateFiles().ToList();
            Equal(1, files.Count);
            Equal(CloudSystemPackageService.LogicalPath, files[0].LogicalPath);
            using (var archive = ZipFile.OpenRead(files[0].LocalPath))
            {
                True(archive.GetEntry("项目配置/同步项目/sample/项目.json") != null, "portable project projection missing from package");
                True(archive.GetEntry("项目配置/项目列表.json") == null, "machine-specific project list entered package");
                True(archive.GetEntry("项目配置/当前项目.txt") == null, "machine-specific active project entered package");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void PackagesSystemFilesAndDefersChangedPackage()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
            var source = Path.Combine(UserDataPaths.SettingsDirectory, "drawing.settings.json");
            File.WriteAllText(source, "first");
            var settings = new CloudSyncSettings { SyncGeneralSettings = true, SyncProjectConfigurations = false,
                SyncTemplatesAndSchemes = false, SystemPackageIntervalMinutes = 30 };
            True(CloudSystemPackageService.Prepare(settings, false, null, CancellationToken.None), "first package should be created immediately");
            var firstHash = LocalFolderSyncEngine.ComputeHash(CloudSystemPackageService.PackagePath);
            File.WriteAllText(source, "second");
            True(!CloudSystemPackageService.Prepare(settings, false, null, CancellationToken.None), "changed package should wait for configured interval");
            Equal(firstHash, LocalFolderSyncEngine.ComputeHash(CloudSystemPackageService.PackagePath));
            True(CloudSystemPackageService.Prepare(settings, true, null, CancellationToken.None), "manual synchronization should force package creation");
            True(!string.Equals(firstHash, LocalFolderSyncEngine.ComputeHash(CloudSystemPackageService.PackagePath), StringComparison.Ordinal), "forced package was not refreshed");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void FirstConnectionPrefersExistingRemoteSystemPackage()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
            File.WriteAllText(Path.Combine(UserDataPaths.SettingsDirectory, "defaults.json"), "fresh-install-default");
            var settings = new CloudSyncSettings { SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false };
            True(CloudSystemPackageService.Prepare(settings, false, true, null, CancellationToken.None), "remote package should remain in synchronization scope");
            True(!File.Exists(CloudSystemPackageService.PackagePath), "fresh installation created a local package before downloading remote settings");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void IncludesNormalProjectAttachments()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            foreach (var name in new[] { "plan.dwg", "site.jpg", "notes.txt", "plot.ctb", "reference.pdf" }) File.WriteAllText(Path.Combine(project, name), name);
            File.WriteAllText(Path.Combine(project, "ignored.bak"), "temporary");
            var settings = new CloudSyncSettings
            {
                SyncGeneralSettings = false, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false, SyncProjectFiles = true,
                ProjectMappings = new List<CloudSyncProjectMapping> { new CloudSyncProjectMapping { CloudId = "sample", LocalFolder = project, Enabled = true } }
            };
            var names = CloudSyncCatalog.CreateDefault(settings).EnumerateFiles().Select(file => Path.GetFileName(file.LocalPath)).ToList();
            True(new[] { "plan.dwg", "site.jpg", "notes.txt", "plot.ctb", "reference.pdf" }.All(names.Contains), "normal project attachments were excluded");
            True(!names.Contains("ignored.bak"), "temporary backup file was included");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BaiduCachesOnlySelectedProjects()
    {
        var settings = new CloudSyncSettings
        {
            SyncProjectFiles = true,
            ProjectMappings = new List<CloudSyncProjectMapping>
            {
                new CloudSyncProjectMapping { CloudId = "selected", Enabled = true },
                new CloudSyncProjectMapping { CloudId = "not-selected", Enabled = false }
            }
        };
        using (var provider = new BaiduNetdiskProvider(settings))
        {
            True(!provider.ShouldTransferRelativePath("万落建筑云同步/项目配置/project.json"), "legacy loose system file should be ignored");
            True(provider.ShouldTransferRelativePath("万落建筑云同步/系统文件包/万落建筑系统文件.zip"), "system package should download");
            True(provider.ShouldTransferRelativePath("万落建筑云同步/项目文件/selected/plan.dwg"), "selected project should download");
            True(!provider.ShouldTransferRelativePath("万落建筑云同步/项目文件/not-selected/plan.dwg"), "unselected project entered provider cache");
            True(!provider.ShouldTransferRelativePath("万落建筑云同步/项目文件/unknown/plan.dwg"), "unknown project entered provider cache");
        }
    }

    private static void RepairsMissingRemoteDrawingEvenWhenOpen()
    {
        WithWorkspace((root, local, shared, engine, settings, ignored) =>
        {
            UserDataPaths.TestRootDirectory = root;
            var drawing = Path.Combine(local, "delete-me.dwg");
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("项目文件/test", local, null) });
            File.WriteAllText(drawing, "base"); engine.Synchronize(settings, catalog);
            File.Delete(Path.Combine(shared, "万落建筑云同步", "项目文件", "test", "delete-me.dwg"));
            CloudSyncPendingFileService.RegisterOpenPathProbe(path => true);
            var repaired = engine.Synchronize(settings, catalog);
            Equal(1, repaired.Uploaded); True(File.Exists(drawing), "open drawing was deleted");
            True(File.Exists(Path.Combine(shared, "万落建筑云同步", "项目文件", "test", "delete-me.dwg")), "missing cloud drawing was not repaired");
            CloudSyncPendingFileService.ClearOpenPathProbe();
        });
    }

    private static void RestoresHistoryWithBackup()
    {
        WithDefaultWorkspace((root, settings, engine, catalog, localFile, remoteFile) =>
        {
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.WriteAllText(remoteFile, "remote-current"); engine.Synchronize(settings, catalog);
            Equal("remote-current", File.ReadAllText(localFile));
            var center = new CloudSyncCenterService();
            var history = center.Load().History.First(item => item.LogicalPath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
            center.RestoreHistory(history);
            Equal("base", File.ReadAllText(localFile));
            True(Directory.GetFiles(Path.Combine(root, "backups", "手动操作前备份"), "*", SearchOption.AllDirectories).Any(), "restore backup missing");
        });
    }

    private static void ResolvesConflictUsingLocalCopy()
    {
        WithDefaultWorkspace((root, settings, engine, catalog, localFile, remoteFile) =>
        {
            File.WriteAllText(localFile, "base"); engine.Synchronize(settings, catalog);
            File.WriteAllText(localFile, "local-choice"); File.WriteAllText(remoteFile, "remote-choice");
            Equal(1, engine.Synchronize(settings, catalog).Conflicts);
            var center = new CloudSyncCenterService();
            var conflict = center.Load().Conflicts.Single();
            center.ResolveConflict(conflict, true);
            Equal("local-choice", File.ReadAllText(localFile));
            Equal("local-choice", File.ReadAllText(remoteFile));
            Equal(0, center.Load().Conflicts.Count);
        });
    }

    private static void CreatesProviderWithoutChangingLocalMode()
    {
        var root = NewRoot();
        try
        {
            using (var local = CloudSyncProviderFactory.Create(new CloudSyncSettings { Provider = "LocalFolder", SyncFolder = root }))
            {
                True(local.IsReady, "local folder provider should remain ready");
                local.Prepare(null, CancellationToken.None); Equal(Path.GetFullPath(root), local.WorkingFolder);
            }
            using (var provider115 = CloudSyncProviderFactory.Create(new CloudSyncSettings { Provider = "115OpenApi" }))
            {
                True(!provider115.IsReady, "115 provider must not enable before official app approval");
                Throws<InvalidOperationException>(() => provider115.Prepare(null, CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ProtectsProviderCredentialsWithDpapi()
    {
        var root = NewRoot();
        try
        {
            var store = new CloudSyncCredentialStore(root);
            store.Save("115OpenApi", new CloudSyncCredential { AccessToken = "secret-access", RefreshToken = "secret-refresh" });
            var raw = File.ReadAllBytes(Directory.GetFiles(root, "*.credential").Single());
            True(!System.Text.Encoding.UTF8.GetString(raw).Contains("secret-access"), "credential was stored as plaintext");
            var loaded = store.Load("115OpenApi");
            Equal("secret-access", loaded.AccessToken); Equal("secret-refresh", loaded.RefreshToken);
            store.Delete("115OpenApi"); True(!Directory.GetFiles(root, "*.credential").Any(), "credential delete failed");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void NewInstallationDefaultsToBaidu()
    {
        Equal("BaiduNetdisk", new CloudSyncSettings().Provider);
    }

    private static void EmptyLocalFolderMigratesToBaidu()
    {
        var root = NewRoot();
        try
        {
            var store = new CloudSyncSettingsStore(Path.Combine(root, "settings.json"), Path.Combine(root, "state.json"));
            store.SaveSettings(new CloudSyncSettings { Provider = "LocalFolder", SyncFolder = string.Empty });
            Equal("BaiduNetdisk", store.LoadSettings().Provider);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void RunsLocalProviderWorkflow()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
            File.WriteAllText(Path.Combine(UserDataPaths.SettingsDirectory, "workflow.json"), "value");
            var shared = Path.Combine(root, "shared");
            var settings = new CloudSyncSettings
            {
                Enabled = true, Provider = "LocalFolder", SyncFolder = shared,
                SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false,
                BackupRoot = Path.Combine(root, "backups")
            };
            var store = new CloudSyncSettingsStore(); store.SaveSettings(settings);
            Equal(1, CloudSyncWorkflow.Synchronize(settings, store).Uploaded);
            True(File.Exists(Path.Combine(shared, "万落建筑云同步", "系统文件包", CloudSystemPackageService.PackageFileName)), "provider workflow did not upload system package");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void IdentifiesCommonCloudFolders()
    {
        Equal("OneDrive", CloudSyncFolderDetector.IdentifyProvider(@"C:\Users\test\OneDrive\万落同步"));
        Equal("Dropbox", CloudSyncFolderDetector.IdentifyProvider(@"D:\Dropbox\万落同步"));
        Equal("坚果云", CloudSyncFolderDetector.IdentifyProvider(@"D:\坚果云\万落同步"));
        Equal("Syncthing", CloudSyncFolderDetector.IdentifyProvider(@"E:\Syncthing\WanLuo"));
        Equal("通用同步文件夹", CloudSyncFolderDetector.IdentifyProvider(@"F:\Shared\WanLuo"));
    }

    private static void ListsWorkFilesAndTheirHistory()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            var project = Path.Combine(root, "workspace", "示例项目"); Directory.CreateDirectory(project);
            var drawing = Path.Combine(project, "一层平面.dwg"); File.WriteAllText(drawing, "current");
            var settings = new CloudSyncSettings
            {
                Provider = "LocalFolder", SyncFolder = Path.Combine(root, "cloud"), SyncProjectFiles = true,
                SyncGeneralSettings = false, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false,
                BackupRoot = Path.Combine(root, "backups"),
                ProjectMappings = new List<CloudSyncProjectMapping>
                {
                    new CloudSyncProjectMapping { ProjectName = "示例项目", CloudId = "sample", LocalFolder = project, Enabled = true }
                }
            };
            new CloudSyncSettingsStore().SaveSettings(settings);
            CloudSyncRemoteInventoryStore.Save(new[] { "万落建筑云同步/项目文件/sample/一层平面.dwg" });
            File.WriteAllText(drawing, "old");
            CloudBackupService.BackupFile(drawing, "项目文件/sample/一层平面.dwg", "历史版本", settings);
            File.WriteAllText(drawing, "current");
            var center = new CloudSyncCenterService();
            var item = center.Load().WorkFiles.Single(candidate => candidate.LogicalPath == "项目文件/sample/一层平面.dwg");
            True(item.LocalExists && item.CloudExists, "work file local/cloud status was not loaded");
            var history = center.HistoryFor(item.LogicalPath);
            Equal(1, history.Count);
            center.RestoreHistory(history[0]);
            Equal("old", File.ReadAllText(drawing));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ChangingProviderScopeCannotDeleteLocalFiles()
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
            var firstCloud = Path.Combine(root, "cloud-a"); var secondCloud = Path.Combine(root, "cloud-b");
            Directory.CreateDirectory(firstCloud); Directory.CreateDirectory(secondCloud);
            var local = Path.Combine(UserDataPaths.SettingsDirectory, "scope-test.json"); File.WriteAllText(local, "keep-me");
            var settings = new CloudSyncSettings { Enabled = true, Provider = "LocalFolder", SyncFolder = firstCloud, SyncGeneralSettings = true,
                SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false, InitialSyncPreference = "Remote", DeviceName = "TEST-PC",
                BackupRoot = Path.Combine(root, "backups") };
            var store = new CloudSyncSettingsStore(); store.SaveSettings(settings);
            CloudSyncWorkflow.Synchronize(settings, store);
            settings.SyncFolder = secondCloud; store.SaveSettings(settings);
            CloudSyncWorkflow.Synchronize(settings, store);
            Equal("keep-me", File.ReadAllText(local));
            True(File.Exists(Path.Combine(secondCloud, "万落建筑云同步", "系统文件包", CloudSystemPackageService.PackageFileName)), "local system package was not safely initialized in the new cloud scope");
            True(Directory.GetDirectories(Path.Combine(root, "backups", "首次连接备份")).Length >= 1, "first connection snapshot missing");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void MigratesNutstoreInternalRoot()
    {
        var root = NewRoot();
        try
        {
            var internalRoot = Path.Combine(root, "Nutstore");
            Directory.CreateDirectory(Path.Combine(internalRoot, "dlcache1"));
            var cloudFolder = Path.Combine(internalRoot, "1", "我的坚果云");
            Directory.CreateDirectory(cloudFolder);
            Equal(Path.GetFullPath(cloudFolder), CloudSyncFolderDetector.ResolveUsableFolder(internalRoot));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ReportsSynchronizationProgress()
    {
        WithDefaultWorkspace((root, settings, engine, catalog, localFile, remoteFile) =>
        {
            File.WriteAllText(localFile, "progress");
            var progress = new List<CloudSyncProgress>();
            engine.Synchronize(settings, catalog, settings.SyncFolder, item => progress.Add(item));
            True(progress.Any(item => item.Stage == "正在扫描本机文件"), "local scan progress was not reported");
            True(progress.Any(item => item.Stage == "正在核对文件" && item.Total > 0), "file progress was not reported");
            True(progress.Any(item => item.Direction == "上传" && item.BytesTotal > 0 && item.BytesCompleted == item.BytesTotal), "byte transfer progress was not reported");
            Equal("同步完成", progress.Last().Stage);
        });
    }

    private static void CancelsLargeFileSynchronizationSafely()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "large-project.dwg");
            using (var stream = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.SetLength(32L * 1024 * 1024);
            using (var cancellation = new CancellationTokenSource())
            {
                Throws<OperationCanceledException>(() => engine.Synchronize(settings, catalog, settings.SyncFolder, item =>
                {
                    if (item.Stage == "正在核对文件") cancellation.Cancel();
                }, cancellation.Token));
            }
            var mirror = Path.Combine(shared, "万落建筑云同步");
            var formal = Path.Combine(mirror, "通用配置", "large-project.dwg");
            True(!File.Exists(formal), "cancelled sync published an incomplete formal file");
            var temporary = Directory.Exists(mirror)
                ? Directory.GetFiles(mirror, "*.tmp", SearchOption.AllDirectories)
                : new string[0];
            Equal(0, temporary.Length);
        });
    }

    private static void DoesNotDuplicateUnresolvedConflict()
    {
        WithWorkspace((root, local, shared, engine, settings, catalog) =>
        {
            var localFile = Path.Combine(local, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            File.WriteAllText(localFile, "base");
            engine.Synchronize(settings, catalog);
            File.WriteAllText(localFile, "local-change");
            File.WriteAllText(remoteFile, "remote-change");
            Equal(1, engine.Synchronize(settings, catalog).Conflicts);
            var conflictRoot = Path.Combine(shared, "万落建筑云同步", "冲突文件");
            var firstCount = Directory.GetFiles(conflictRoot, "*", SearchOption.AllDirectories).Length;
            Equal(1, engine.Synchronize(settings, catalog).Conflicts);
            Equal(firstCount, Directory.GetFiles(conflictRoot, "*", SearchOption.AllDirectories).Length);
        });
    }

    private static void BuildsBaiduAuthorizationUrl()
    {
        Equal("https://openapi.baidu.com/oauth/2.0/login_success", BaiduNetdiskClient.DefaultRedirectUri);
        var uri = BaiduNetdiskClient.BuildAuthorizationUri("app key", "https://example.test/oauth/callback", "state-value").AbsoluteUri;
        True(uri.StartsWith(BaiduNetdiskClient.AuthorizationEndpoint, StringComparison.Ordinal), "wrong authorization endpoint");
        True(uri.Contains("client_id=app%20key"), "client id was not encoded");
        True(uri.Contains("scope=basic%2Cnetdisk"), "netdisk scope missing");
        True(uri.Contains("state=state-value"), "oauth state missing");
    }

    private static void RejectsUnsafeBaiduRemotePath()
    {
        Equal("/apps/万落建筑工具/方案库", BaiduNetdiskClient.NormalizeRemotePath(@"\apps\万落建筑工具\方案库"));
        Throws<IOException>(() => BaiduNetdiskClient.NormalizeRemotePath("/apps/万落建筑工具/../其他目录"));
    }

    private static void ExchangesBaiduAuthorizationCode()
    {
        HttpRequestMessage captured = null;
        using (var http = new HttpClient(new StubHttpHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
            };
        })))
        using (var client = new BaiduNetdiskClient(http))
        {
            var credential = client.ExchangeCodeAsync("app-key", "secret-key", "https://example.test/callback", "auth-code", CancellationToken.None).GetAwaiter().GetResult();
            Equal("app-key", credential.ClientId); Equal("access", credential.AccessToken); Equal("refresh", credential.RefreshToken); Equal("secret-key", credential.ClientSecret);
            Equal(HttpMethod.Get, captured.Method);
            True(captured.RequestUri.Query.Contains("grant_type=authorization_code"), "authorization grant missing");
            True(captured.RequestUri.Query.Contains("client_secret=secret-key"), "secret missing from token exchange");
        }
    }

    private static void ValidatesBaiduOAuthCallback()
    {
        Equal("the-code", BaiduNetdiskClient.ExtractAuthorizationCode("https://example.test/callback?code=the-code&state=expected", "expected"));
        Throws<InvalidOperationException>(() => BaiduNetdiskClient.ExtractAuthorizationCode("https://example.test/callback?code=old&state=wrong", "expected"));
    }

    private static void DownloadsBaiduFileThroughMultimediaMetadata()
    {
        var requests = new List<HttpRequestMessage>();
        using (var http = new HttpClient(new StubHttpHandler(request =>
        {
            requests.Add(request);
            if (requests.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"errno\":0,\"list\":[{\"dlink\":\"https://d.pcs.baidu.test/file/demo\"}]}", Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("downloaded")) };
        })))
        using (var client = new BaiduNetdiskClient(http))
        {
            var root = NewRoot();
            try
            {
                var target = Path.Combine(root, "download.bin");
                client.DownloadAsync("access-token", new BaiduRemoteEntry { FileSystemId = 123456789L, Size = 10 }, target, null, CancellationToken.None).GetAwaiter().GetResult();
                Equal("downloaded", File.ReadAllText(target));
                Equal(2, requests.Count);
                Equal(BaiduNetdiskClient.MultimediaEndpoint, requests[0].RequestUri.GetLeftPart(UriPartial.Path));
                True(requests[0].RequestUri.Query.Contains("method=filemetas"), "filemetas method missing");
                True(requests[0].RequestUri.Query.Contains("dlink=1"), "dlink flag missing");
                True(requests[0].RequestUri.Query.Contains("fsids=%5B123456789%5D"), "fs_id missing");
                Equal("pan.baidu.com", requests[1].Headers.UserAgent.ToString());
                True(requests[1].RequestUri.Query.Contains("access_token=access-token"), "download token missing");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void DecryptsUnifiedBrokerToken()
    {
        using (var rsa = new RSACng(2048))
        {
            var keyMaterial = new byte[64]; for (var index = 0; index < keyMaterial.Length; index++) keyMaterial[index] = (byte)(index + 1);
            var iv = new byte[16]; for (var index = 0; index < iv.Length; index++) iv[index] = (byte)(100 + index);
            byte[] plaintext;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(BrokerTokenPayload)).WriteObject(stream, new BrokerTokenPayload { AccessToken = "broker-access", RefreshToken = "broker-refresh", ExpiresIn = 3600 });
                plaintext = stream.ToArray();
            }
            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = keyMaterial.Take(32).ToArray(); aes.IV = iv;
                using (var encryptor = aes.CreateEncryptor()) ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
            }
            var authenticated = iv.Concat(ciphertext).ToArray(); byte[] mac;
            using (var hmac = new HMACSHA256(keyMaterial.Skip(32).ToArray())) mac = hmac.ComputeHash(authenticated);
            var envelope = new BrokerTokenEnvelope
            {
                Version = 1, Algorithm = "RSA-OAEP-256+A256CBC-HS256",
                WrappedKey = BaiduBrokerAuthClient.Base64UrlEncode(rsa.Encrypt(keyMaterial, RSAEncryptionPadding.OaepSHA256)),
                Iv = BaiduBrokerAuthClient.Base64UrlEncode(iv), Ciphertext = BaiduBrokerAuthClient.Base64UrlEncode(ciphertext), Mac = BaiduBrokerAuthClient.Base64UrlEncode(mac)
            };
            string encoded;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(BrokerTokenEnvelope)).WriteObject(stream, envelope);
                encoded = BaiduBrokerAuthClient.Base64UrlEncode(stream.ToArray());
            }
            var credential = BaiduBrokerAuthClient.DecryptCredential(encoded, rsa);
            Equal("broker-access", credential.AccessToken); Equal("broker-refresh", credential.RefreshToken);
            envelope.Mac = BaiduBrokerAuthClient.Base64UrlEncode(new byte[32]);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(BrokerTokenEnvelope)).WriteObject(stream, envelope);
                encoded = BaiduBrokerAuthClient.Base64UrlEncode(stream.ToArray());
            }
            Throws<CryptographicException>(() => BaiduBrokerAuthClient.DecryptCredential(encoded, rsa));
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_handler(request)); }
    }

    private static void WithDefaultWorkspace(Action<string, CloudSyncSettings, LocalFolderSyncEngine, CloudSyncCatalog, string, string> action)
    {
        var root = NewRoot();
        try
        {
            UserDataPaths.TestRootDirectory = root;
            Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
            var shared = Path.Combine(root, "shared"); Directory.CreateDirectory(shared);
            var settings = new CloudSyncSettings
            {
                Enabled = true, Provider = "LocalFolder", SyncFolder = shared, DeviceName = "TEST-PC",
                SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false,
                BackupRoot = Path.Combine(root, "backups")
            };
            var store = new CloudSyncSettingsStore(); store.SaveSettings(settings);
            var engine = new LocalFolderSyncEngine(store);
            var catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", UserDataPaths.SettingsDirectory, null) });
            var localFile = Path.Combine(UserDataPaths.SettingsDirectory, "settings.json");
            var remoteFile = Path.Combine(shared, "万落建筑云同步", "通用配置", "settings.json");
            action(root, settings, engine, catalog, localFile, remoteFile);
        }
        finally { Directory.Delete(root, true); }
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
            var settings = new CloudSyncSettings { Enabled = true, Provider = "LocalFolder", SyncFolder = shared, DeviceName = "TEST-PC" };
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
        try { test(); _executed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + name + " · " + exception.GetType().FullName + " · " + exception.Message);
            throw;
        }
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
