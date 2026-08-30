using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    public static class CloudSyncCoordinator
    {
        private static readonly object LifecycleSync = new object();
        private static readonly List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();
        private static Timer _timer;
        private static int _running;
        private static int _pending;
        private static bool _installed;

        public static event Action<CloudSyncResult, Exception> SynchronizationCompleted;

        public static void Install()
        {
            lock (LifecycleSync)
            {
                if (_installed) return;
                _installed = true;
                ConfigureWatchers();
            }
        }

        public static void Reload()
        {
            lock (LifecycleSync)
            {
                DisposeWatchers();
                if (_timer != null) { _timer.Dispose(); _timer = null; }
                if (_installed) ConfigureWatchers();
            }
        }

        public static void Remove()
        {
            lock (LifecycleSync)
            {
                _installed = false;
                DisposeWatchers();
                if (_timer != null) { _timer.Dispose(); _timer = null; }
            }
        }

        public static void RequestSynchronization(bool immediate)
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            if (!settings.Enabled) return;
            Interlocked.Exchange(ref _pending, 1);
            lock (LifecycleSync)
            {
                if (_timer == null) _timer = new Timer(Execute, null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(immediate ? 10 : 2500, Timeout.Infinite);
            }
        }

        private static void ConfigureWatchers()
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            if (!settings.Enabled || !settings.AutoSync || string.IsNullOrWhiteSpace(settings.SyncFolder)) return;
            var catalog = CloudSyncCatalog.CreateDefault(settings);
            foreach (var root in catalog.Roots) AddWatcher(root);
            var mirror = Path.Combine(settings.SyncFolder, "万落建筑云同步");
            try { Directory.CreateDirectory(mirror); AddWatcher(mirror); } catch { }
            _timer = new Timer(Execute, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private static void AddWatcher(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler changed = delegate { RequestSynchronization(false); };
                RenamedEventHandler renamed = delegate { RequestSynchronization(false); };
                watcher.Changed += changed;
                watcher.Created += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.EnableRaisingEvents = true;
                Watchers.Add(watcher);
            }
            catch (Exception exception) { Trace("监视目录失败：" + root + "；" + exception.Message); }
        }

        private static void Execute(object ignored)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) { Interlocked.Exchange(ref _pending, 1); return; }
            Interlocked.Exchange(ref _pending, 0);
            CloudSyncResult result = null;
            Exception failure = null;
            try
            {
                var store = new CloudSyncSettingsStore();
                var settings = store.LoadSettings();
                if (settings.Enabled)
                    result = new LocalFolderSyncEngine(store).Synchronize(settings, CloudSyncCatalog.CreateDefault(settings));
            }
            catch (Exception exception)
            {
                failure = exception;
                Trace(exception.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                var handler = SynchronizationCompleted;
                if (handler != null) try { handler(result, failure); } catch { }
                if (Interlocked.Exchange(ref _pending, 0) != 0) RequestSynchronization(false);
            }
        }

        private static void DisposeWatchers()
        {
            foreach (var watcher in Watchers) try { watcher.Dispose(); } catch { }
            Watchers.Clear();
        }

        private static void Trace(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "cloud-sync.log"),
                    DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
