using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using BatchPdfPublisher.Services;

internal static class CloudSyncSafetyTests
{
    private sealed class Device
    {
        public string Root, Local, Mirror;
        public CloudSyncCatalog Catalog;
        public ImmutableCloudJournal Journal;
        public Device(string root)
        {
            Root = root; Local = Path.Combine(root, "local"); Mirror = Path.Combine(root, "mirror");
            Directory.CreateDirectory(Local); Directory.CreateDirectory(Mirror);
            Catalog = new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", Local, null) });
            Journal = new ImmutableCloudJournal(Path.Combine(root, "journal"), Mirror);
        }
        public void Read() { Journal.Materialize(Catalog, CancellationToken.None); }
        public void Edit(string name, string value)
        {
            File.WriteAllText(Path.Combine(Local, name), value);
            var path = ImmutableCloudJournal.SafePath(Mirror, "通用配置/" + name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value);
        }
        public string Commit() { return Journal.CreateCommit(Catalog, CancellationToken.None); }
        public void Receive(params string[] commits)
        {
            foreach (var commit in commits) File.Copy(commit, Path.Combine(Journal.Archives, Path.GetFileName(commit)), true);
        }
    }
    private static void InRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "WanluoCloudSafety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Fails<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static void ConcurrentSameFilePreservesBothBranches()
    {
        InRoot(root => {
            var a = new Device(Path.Combine(root, "a")); var b = new Device(Path.Combine(root, "b"));
            a.Read(); a.Edit("x.json", "base"); var initial = a.Commit();
            a.Receive(initial); b.Receive(initial); a.Read(); b.Read();
            a.Edit("x.json", "A"); b.Edit("x.json", "B");
            var ca = a.Commit(); var cb = b.Commit();
            a.Receive(ca, cb); b.Receive(ca, cb); a.Read(); b.Read();
            Check(a.Journal.Blocked.Contains("通用配置/x.json") && b.Journal.Blocked.Contains("通用配置/x.json"), "siblings did not become conflicts");
            Check(a.Commit() == null && b.Commit() == null, "unresolved siblings were overwritten");
            Check(File.ReadAllText(Path.Combine(a.Local, "x.json")) == "A", "local edit lost");
            Check(Directory.GetFiles(a.Mirror, "*.remote-conflict", SearchOption.AllDirectories).Length == 2, "missing cloud conflict copy");
        });
    }
    public static void ConcurrentDifferentFilesMerge()
    {
        InRoot(root => {
            var a = new Device(Path.Combine(root, "a")); var b = new Device(Path.Combine(root, "b"));
            a.Read(); b.Read(); a.Edit("a.json", "A"); b.Edit("b.json", "B");
            var ca = a.Commit(); var cb = b.Commit(); a.Receive(ca, cb); a.Read();
            Check(a.Journal.Blocked.Count == 0, "independent files conflict");
            Check(File.ReadAllText(ImmutableCloudJournal.SafePath(a.Mirror, "通用配置/a.json")) == "A", "missing a");
            Check(File.ReadAllText(ImmutableCloudJournal.SafePath(a.Mirror, "通用配置/b.json")) == "B", "missing b");
        });
    }
    public static void ExplicitResolutionDoesNotSwallowUnseenBranch()
    {
        InRoot(root => {
            var a = new Device(Path.Combine(root, "a")); var b = new Device(Path.Combine(root, "b")); var c = new Device(Path.Combine(root, "c"));
            a.Read(); b.Read(); c.Read(); a.Edit("x", "A"); b.Edit("x", "B"); c.Edit("x", "C");
            var ca = a.Commit(); var cb = b.Commit(); var cc = c.Commit();
            a.Receive(ca, cb); a.Read(); a.Edit("x", "chosen");
            ImmutableCloudJournal.RecordResolution(a.Mirror, "通用配置/x", CloudSyncTransaction.Hash(Path.Combine(a.Local, "x")));
            a.Read(); Check(a.Journal.Blocked.Count == 0, "explicit resolution not accepted");
            var merged = a.Commit(); a.Receive(merged, cc); a.Read();
            Check(a.Journal.Blocked.Contains("通用配置/x"), "unseen sibling silently overwritten");
        });
    }
    public static void MissingParentFailsClosed()
    {
        InRoot(root => {
            var a = new Device(Path.Combine(root, "a")); var b = new Device(Path.Combine(root, "b"));
            a.Read(); a.Edit("x", "one"); var initial = a.Commit(); a.Receive(initial); a.Read();
            a.Edit("x", "two"); var child = a.Commit(); b.Receive(child);
            Fails<IOException>(() => b.Read()); Check(!File.Exists(ImmutableCloudJournal.SafePath(b.Mirror, "通用配置/x")), "incomplete graph was applied");
        });
    }
    public static void CorruptArchiveFailsClosed()
    {
        InRoot(root => {
            var a = new Device(root); a.Read(); a.Edit("x", "one"); var initial = a.Commit(); a.Receive(initial);
            File.AppendAllText(Path.Combine(a.Journal.Archives, Path.GetFileName(initial)), "corruption");
            Fails<System.IO.InvalidDataException>(() => a.Read());
        });
    }
    public static void UnsafeVersionPathsRejected()
    {
        InRoot(root => {
            foreach (var p in new[] { "../x", "a/../x", "C:/x", "a\\x", "/x", "a./x", "a /x", "x:stream", "a//x" })
                Fails<InvalidDataException>(() => ImmutableCloudJournal.SafePath(root, p));
        });
    }
    public static void TransactionRollsBackAllWritesAndState()
    {
        InRoot(root => {
            var file = Path.Combine(root, "x"); File.WriteAllText(file, "before");
            var next = Path.Combine(root, "next"); File.WriteAllText(next, "after");
            var store = new CloudSyncSettingsStore(Path.Combine(root, "settings"), Path.Combine(root, "state"));
            store.SaveState(new CloudSyncState { ProviderScope = "before" });
            using (var tx = new CloudSyncTransaction(Path.Combine(root, "tx")))
            {
                CloudSyncTransaction.BeforeReplace(file, CloudSyncTransaction.Hash(file), CloudSyncTransaction.Hash(next)); File.Copy(next, file, true);
                store.SaveState(new CloudSyncState { ProviderScope = "after" });
                // Simulates a provider failure before the transaction commit point.
            }
            Check(File.ReadAllText(file) == "before" && store.LoadState().ProviderScope == "before", "partial transaction remained");
        });
    }
    public static void TransactionPreservesInterveningUserSave()
    {
        InRoot(root => {
            var file = Path.Combine(root, "x"); File.WriteAllText(file, "before");
            var next = Path.Combine(root, "next"); File.WriteAllText(next, "after");
            using (var tx = new CloudSyncTransaction(Path.Combine(root, "tx")))
            {
                CloudSyncTransaction.BeforeReplace(file, CloudSyncTransaction.Hash(file), CloudSyncTransaction.Hash(next)); File.Copy(next, file, true);
                File.WriteAllText(file, "user-saved-after-download");
            }
            Check(File.ReadAllText(file) == "user-saved-after-download", "rollback lost user save");
            Check(Directory.GetFiles(root, "需要人工核对.txt", SearchOption.AllDirectories).Length == 1, "missing recovery notice");
        });
    }
    public static void RestartReplaysDurableJournal()
    {
        InRoot(root => {
            var tx = Path.Combine(root, "tx", "interrupted"); Directory.CreateDirectory(tx);
            var target = Path.Combine(root, "x"); File.WriteAllText(target, "after");
            var backup = Path.Combine(tx, "0.backup"); File.WriteAllText(backup, "before");
            CloudSyncTransaction.WriteJson(Path.Combine(tx, "journal.json"), new CloudSyncTransaction.Journal {
                Changes = new List<CloudSyncTransaction.Change> { new CloudSyncTransaction.Change {
                    Target = target, Before = CloudSyncTransaction.Hash(backup), After = CloudSyncTransaction.Hash(target), Backup = "0.backup" } } });
            CloudSyncTransaction.Recover(Path.Combine(root, "tx"), p => p == target);
            Check(File.ReadAllText(target) == "before", "crash recovery failed");
            CloudSyncTransaction.Recover(Path.Combine(root, "tx"), p => p == target);
            Check(File.ReadAllText(target) == "before", "recovery not idempotent");
        });
    }
    public static void FirstConnectionNeverUsesTimestampOrPreference()
    {
        InRoot(root => {
            var local = Path.Combine(root, "local"); var mirror = Path.Combine(root, "cloud", "万落建筑云同步", "通用配置");
            Directory.CreateDirectory(local); Directory.CreateDirectory(mirror);
            File.WriteAllText(Path.Combine(local, "x"), "local"); File.WriteAllText(Path.Combine(mirror, "x"), "remote");
            File.SetLastWriteTimeUtc(Path.Combine(mirror, "x"), DateTime.UtcNow.AddYears(1));
            foreach (var pref in new[] { "Local", "Remote", "Conflict" })
            {
                var store = new CloudSyncSettingsStore(Path.Combine(root, pref + ".settings"), Path.Combine(root, pref + ".state"));
                var result = new LocalFolderSyncEngine(store, Path.Combine(root, "history")).Synchronize(new CloudSyncSettings {
                    Enabled = true, SyncFolder = Path.Combine(root, "cloud"), InitialSyncPreference = pref, BackupRoot = Path.Combine(root, "backups") },
                    new CloudSyncCatalog(new[] { new CloudSyncSource("通用配置", local, null) }));
                Check(result.Conflicts == 1 && result.Uploaded == 0 && result.Downloaded == 0, "first connection overwrote content");
            }
        });
    }

    private static CloudSyncSettings Activate(string device, string shared)
    {
        UserDataPaths.TestRootDirectory = device;
        Directory.CreateDirectory(UserDataPaths.SettingsDirectory);
        var settings = new CloudSyncSettings { Enabled = true, Provider = "LocalFolder", SyncFolder = shared,
            SyncGeneralSettings = true, SyncProjectConfigurations = false, SyncTemplatesAndSchemes = false,
            BackupRoot = Path.Combine(device, "backups") };
        new CloudSyncSettingsStore().SaveSettings(settings);
        return settings;
    }
    public static void TwoDeviceWorkflowConvergesWithoutRepeatingTransfers()
    {
        InRoot(root => {
            var shared = Path.Combine(root, "cloud"); var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            var settings = Activate(a, shared);
            File.WriteAllText(UserDataPaths.SettingsFile("a.json"), "base-a"); File.WriteAllText(UserDataPaths.SettingsFile("b.json"), "base-b");
            CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            settings = Activate(b, shared); Check(CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore()).Downloaded == 2, "second device did not receive files");
            File.WriteAllText(UserDataPaths.SettingsFile("b.json"), "device-b");
            settings = Activate(a, shared); File.WriteAllText(UserDataPaths.SettingsFile("a.json"), "device-a");
            CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            settings = Activate(b, shared); var merged = CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            Check(merged.Conflicts == 0 && merged.Uploaded == 1 && merged.Downloaded == 1, "per-file merge failed");
            settings = Activate(a, shared); CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            Check(File.ReadAllText(UserDataPaths.SettingsFile("b.json")) == "device-b", "device B edit missing");
            var count = Directory.GetFiles(Path.Combine(shared, ImmutableCloudJournal.RemoteDirectory), "*.zip").Length;
            for (var i = 0; i < 3; i++)
            {
                var unchanged = CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
                Check(unchanged.Uploaded == 0 && unchanged.Downloaded == 0, "sync did not converge");
            }
            Check(Directory.GetFiles(Path.Combine(shared, ImmutableCloudJournal.RemoteDirectory), "*.zip").Length == count, "unchanged files published again");
        });
    }
    public static void WorkflowCancellationRestoresAppliedFilesThenRetries()
    {
        InRoot(root => {
            var shared = Path.Combine(root, "cloud"); var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            var settings = Activate(a, shared); File.WriteAllText(UserDataPaths.SettingsFile("x.json"), "base");
            CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            settings = Activate(b, shared); CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            settings = Activate(a, shared); File.WriteAllText(UserDataPaths.SettingsFile("x.json"), "remote-new");
            CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            settings = Activate(b, shared);
            using (var cancel = new CancellationTokenSource())
                Fails<OperationCanceledException>(() => CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore(),
                    p => { if (p.Stage == "文件核对完成，等待发布") cancel.Cancel(); }, cancel.Token));
            Check(File.ReadAllText(UserDataPaths.SettingsFile("x.json")) == "base", "failed workflow left applied content");
            var retried = CloudSyncWorkflow.Synchronize(settings, new CloudSyncSettingsStore());
            Check(retried.Downloaded == 1 && File.ReadAllText(UserDataPaths.SettingsFile("x.json")) == "remote-new", "retry failed after rollback");
        });
    }

    private sealed class ArchiveServer : HttpMessageHandler
    {
        public byte[] Bytes; public string Name; public int Downloads;
        public bool Corrupt;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string json;
            if (request.RequestUri.Host == "download.test")
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? Encoding.UTF8.GetBytes("bad") : Bytes) });
            }
            if (request.RequestUri.Query.Contains("method=filemetas")) json = "{\"errno\":0,\"list\":[{\"dlink\":\"https://download.test/file\"}]}";
            else if (request.RequestUri.Query.Contains("method=list"))
            {
                string md5; using (var hash = MD5.Create()) md5 = BitConverter.ToString(hash.ComputeHash(Bytes)).Replace("-", "").ToLowerInvariant();
                json = "{\"errno\":0,\"list\":[{\"path\":\"/apps/test/" + ImmutableCloudJournal.RemoteDirectory + "/" + Name +
                    "\",\"isdir\":0,\"size\":" + Bytes.Length + ",\"fs_id\":1,\"md5\":\"" + md5 + "\"}]}";
            }
            else json = "{\"errno\":0}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
    public static void BaiduProviderCountsRealDownloadsAndReusesArchive()
    {
        InRoot(root => {
            var source = new Device(Path.Combine(root, "source")); source.Read(); source.Edit("x.json", "remote"); var archive = source.Commit();
            var settings = Activate(Path.Combine(root, "receiver"), Path.Combine(root, "unused"));
            settings.Provider = "BaiduNetdisk"; settings.ProviderClientId = "test"; settings.ProviderRedirectUri = "https://callback.test";
            settings.ProviderBrokerUrl = ""; settings.ProviderRemoteFolder = "/apps/test";
            var credentials = new CloudSyncCredentialStore(Path.Combine(root, "credentials"));
            credentials.Save("BaiduNetdisk", new CloudSyncCredential { ClientId = "test", ClientSecret = "test-secret", RefreshToken = "test-refresh",
                AccessToken = "test-token", ExpiresAtUtc = DateTime.UtcNow.AddDays(1).ToString("O"), AccountIdentity = "test-account" });
            using (var server = new ArchiveServer { Bytes = File.ReadAllBytes(archive), Name = Path.GetFileName(archive) })
            using (var http = new HttpClient(server))
            {
                var client = new BaiduNetdiskClient(http);
                using (var provider = new BaiduNetdiskProvider(settings, credentials, client))
                {
                    server.Corrupt = true;
                    Fails<InvalidDataException>(() => provider.Prepare(null, CancellationToken.None));
                    Check(!File.Exists(UserDataPaths.SettingsFile("x.json")), "corrupt download reached user file");
                    server.Corrupt = false;
                    provider.Prepare(null, CancellationToken.None);
                    var result = new LocalFolderSyncEngine(new CloudSyncSettingsStore()).Synchronize(settings, CloudSyncCatalog.CreateDefault(settings), provider.WorkingFolder);
                    provider.Complete(result, null, CancellationToken.None);
                    Check(result.NetworkDownloaded == 1 && result.NetworkDownloadedBytes == server.Bytes.Length, "network count does not include archive download");
                    Check(File.ReadAllText(UserDataPaths.SettingsFile("x.json")) == "remote", "archive not applied");
                    provider.Prepare(null, CancellationToken.None);
                    result = new LocalFolderSyncEngine(new CloudSyncSettingsStore()).Synchronize(settings, CloudSyncCatalog.CreateDefault(settings), provider.WorkingFolder);
                    provider.Complete(result, null, CancellationToken.None);
                    Check(server.Downloads == 2 && result.NetworkDownloaded == 0 && result.NetworkUploaded == 0, "unchanged archive repeatedly transferred");
                }
            }
        });
    }
}
