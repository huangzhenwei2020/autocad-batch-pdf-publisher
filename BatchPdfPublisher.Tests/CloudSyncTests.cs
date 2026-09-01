using System;
using System.Collections.Generic;
using System.IO;
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
        Run("PreservesBothSidesOnConflict", PreservesBothSidesOnConflict);
        Run("FirstConnectionCanPreferRemoteWithBackup", FirstConnectionCanPreferRemoteWithBackup);
        Run("PropagatesRemoteDeletionWithBackup", PropagatesRemoteDeletionWithBackup);
        Run("RejectsOverlappingRoots", RejectsOverlappingRoots);
        Run("DefersOpenDrawingUntilClosed", DefersOpenDrawingUntilClosed);
        Run("MapsProjectFileOnlyOnce", MapsProjectFileOnlyOnce);
        Run("DefersRemoteDeletionUntilDrawingCloses", DefersRemoteDeletionUntilDrawingCloses);
        Run("RestoresHistoryWithBackup", RestoresHistoryWithBackup);
        Run("ResolvesConflictUsingLocalCopy", ResolvesConflictUsingLocalCopy);
        Run("CreatesProviderWithoutChangingLocalMode", CreatesProviderWithoutChangingLocalMode);
        Run("NewInstallationDefaultsToBaidu", NewInstallationDefaultsToBaidu);
        Run("EmptyLocalFolderMigratesToBaidu", EmptyLocalFolderMigratesToBaidu);
        Run("ProtectsProviderCredentialsWithDpapi", ProtectsProviderCredentialsWithDpapi);
        Run("RunsLocalProviderWorkflow", RunsLocalProviderWorkflow);
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
            True(Directory.GetFiles(Path.Combine(root, ".cloud-sync", "center-backups"), "*", SearchOption.AllDirectories).Any(), "restore backup missing");
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
                SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false
            };
            var store = new CloudSyncSettingsStore(); store.SaveSettings(settings);
            Equal(1, CloudSyncWorkflow.Synchronize(settings, store).Uploaded);
            True(File.Exists(Path.Combine(shared, "万落建筑云同步", "通用配置", "workflow.json")), "provider workflow did not upload");
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
                SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false
            };
            var store = new CloudSyncSettingsStore(); store.SaveSettings(settings);
            var engine = new LocalFolderSyncEngine(store);
            var catalog = CloudSyncCatalog.CreateDefault(settings);
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
