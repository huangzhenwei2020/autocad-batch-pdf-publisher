using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    /// <summary>AutoCAD-independent synchronization coordinator used by the tray agent.</summary>
    public static class CloudSyncCoordinator
    {
        private static readonly object LifecycleSync = new object();
        private static readonly List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();
        private static Timer _timer;
        private static int _running;
        private static int _pending;
        private static int _consecutiveFailures;
        private static bool _installed;

        public static event Action<CloudSyncResult, Exception> SynchronizationCompleted;
        public static event Action<CloudSyncProgress> SynchronizationProgress;

        public static void Install()
        {
            lock (LifecycleSync)
            {
                if (_installed) return;
                _installed = true;
                ConfigureWatchers();
            }
        }

        public static void Remove()
        {
            List<FileSystemWatcher> watchers;
            Timer timer;
            lock (LifecycleSync)
            {
                _installed = false;
                watchers = new List<FileSystemWatcher>(Watchers);
                Watchers.Clear();
                timer = _timer;
                _timer = null;
            }
            if (timer != null) try { timer.Dispose(); } catch { }
            foreach (var watcher in watchers) try { watcher.Dispose(); } catch { }
        }

        public static void RequestSynchronization(bool immediate)
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            if (!settings.Enabled) return;
            Interlocked.Exchange(ref _pending, 1);
            lock (LifecycleSync)
            {
                if (_timer == null) _timer = new Timer(Execute, null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(immediate ? 10 : 2500, settings.AutoSync ? 300000 : Timeout.Infinite);
            }
        }

        public static void QueueReload(bool synchronizeAfterReload)
        {
            Remove();
            Install();
            if (synchronizeAfterReload) RequestSynchronization(false);
        }

        private static void ConfigureWatchers()
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            if (!settings.Enabled || !settings.AutoSync) return;
            foreach (var root in CloudSyncCatalog.CreateDefault(settings).Roots) AddWatcher(root);
            _timer = new Timer(Execute, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
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
                FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs args)
                {
                    if (!IsGeneratedSyncArtifact(args.FullPath) && CloudSyncRetryPolicy.ShouldQueueWatcherEvent(Volatile.Read(ref _running) != 0))
                        RequestSynchronization(false);
                };
                RenamedEventHandler renamed = delegate(object sender, RenamedEventArgs args)
                {
                    if (!IsGeneratedSyncArtifact(args.FullPath) && CloudSyncRetryPolicy.ShouldQueueWatcherEvent(Volatile.Read(ref _running) != 0))
                        RequestSynchronization(false);
                };
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
            Trace("后台同步开始。");
            try
            {
                var store = new CloudSyncSettingsStore();
                var settings = store.LoadSettings();
                if (settings.Enabled) result = CloudSyncWorkflow.Synchronize(settings, store, ReportProgress, CancellationToken.None);
            }
            catch (Exception exception) { failure = exception; Trace(exception.ToString()); }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                Trace(failure != null ? "后台同步失败：" + failure.GetBaseException().Message :
                    result == null ? "后台同步结束：未执行。" : "后台同步完成：" + result.Summary);
                var completed = SynchronizationCompleted;
                if (completed != null) try { completed(result, failure); } catch { }
                if (CloudSyncRetryPolicy.ShouldRetry(failure, result))
                {
                    var failures = Interlocked.Increment(ref _consecutiveFailures);
                    var delay = Math.Min(900, 30 * (int)Math.Pow(2, Math.Min(5, failures - 1)));
                    lock (LifecycleSync)
                    {
                        if (_timer == null) _timer = new Timer(Execute, null, Timeout.Infinite, Timeout.Infinite);
                        _timer.Change(TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    if (failure == null && Interlocked.Exchange(ref _pending, 0) != 0) RequestSynchronization(false);
                }
            }
        }

        private static void ReportProgress(CloudSyncProgress progress)
        {
            var handler = SynchronizationProgress;
            if (handler != null) try { handler(progress); } catch { }
        }

        private static bool IsGeneratedSyncArtifact(string path)
        {
            var normalized = (path ?? string.Empty).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.IndexOf(Path.DirectorySeparatorChar + "冲突文件" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf(Path.DirectorySeparatorChar + "历史版本" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf(Path.DirectorySeparatorChar + ".wanluo-sync" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith(".rollback", StringComparison.OrdinalIgnoreCase);
        }

        private static void Trace(string message)
        {
            try { File.AppendAllText(Path.Combine(UserDataPaths.LogsDirectory, "cloud-sync-agent.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine); }
            catch { }
        }
    }
}
