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
        private static int _reloadPending;
        private static int _reloadWorker;
        private static int _syncAfterReload;
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
            }
            QueueReload(false);
            CloudSyncCadNotificationService.Install();
        }

        public static void Reload()
        {
            List<FileSystemWatcher> watchers;
            Timer timer;
            lock (LifecycleSync)
            {
                DetachResources(out watchers, out timer);
            }
            DisposeResources(watchers, timer);
            lock (LifecycleSync)
            {
                if (_installed) ConfigureWatchers();
            }
        }

        public static void QueueReload(bool synchronizeAfterReload)
        {
            if (synchronizeAfterReload) Interlocked.Exchange(ref _syncAfterReload, 1);
            Interlocked.Exchange(ref _reloadPending, 1);
            if (Interlocked.CompareExchange(ref _reloadWorker, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    while (Interlocked.Exchange(ref _reloadPending, 0) != 0) Reload();
                    if (Interlocked.Exchange(ref _syncAfterReload, 0) != 0) RequestSynchronization(false);
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref _syncAfterReload, 0);
                    Trace("后台重载同步监视器失败：" + exception);
                }
                finally
                {
                    Interlocked.Exchange(ref _reloadWorker, 0);
                    if (Volatile.Read(ref _reloadPending) != 0 || Volatile.Read(ref _syncAfterReload) != 0)
                        QueueReload(false);
                }
            });
        }

        public static void Remove()
        {
            List<FileSystemWatcher> watchers;
            Timer timer;
            lock (LifecycleSync)
            {
                _installed = false;
                DetachResources(out watchers, out timer);
            }
            DisposeResources(watchers, timer);
            CloudSyncCadNotificationService.Remove();
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

        public static bool TryBeginManualSynchronization()
        {
            lock (LifecycleSync)
            {
                if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
                Interlocked.Exchange(ref _pending, 0);
                if (_timer != null) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return true;
            }
        }

        public static void EndManualSynchronization()
        {
            Interlocked.Exchange(ref _running, 0);
            if (Interlocked.Exchange(ref _pending, 0) != 0) RequestSynchronization(false);
        }

        private static void ConfigureWatchers()
        {
            var settings = new CloudSyncSettingsStore().LoadSettings();
            if (!settings.Enabled || !settings.AutoSync) return;
            var catalog = CloudSyncCatalog.CreateDefault(settings);
            foreach (var root in catalog.Roots) AddWatcher(root);
            try
            {
                using (var provider = CloudSyncProviderFactory.Create(settings))
                {
                    if (!provider.IsReady) { Trace(provider.Status); return; }
                    var mirror = Path.Combine(settings.SyncFolder ?? string.Empty, ImmutableCloudJournal.RemoteDirectory);
                    if (string.Equals(provider.Id, "LocalFolder", StringComparison.OrdinalIgnoreCase))
                    { Directory.CreateDirectory(mirror); AddWatcher(mirror); }
                }
            }
            catch (Exception exception) { Trace(exception.Message); return; }
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
                FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs args)
                {
                    if (!IsGeneratedSyncArtifact(args.FullPath) &&
                        CloudSyncRetryPolicy.ShouldQueueWatcherEvent(Volatile.Read(ref _running) != 0))
                        RequestSynchronization(false);
                };
                RenamedEventHandler renamed = delegate(object sender, RenamedEventArgs args)
                {
                    if (!IsGeneratedSyncArtifact(args.FullPath) &&
                        CloudSyncRetryPolicy.ShouldQueueWatcherEvent(Volatile.Read(ref _running) != 0))
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
            Trace("同步开始。");
            try
            {
                var store = new CloudSyncSettingsStore();
                var settings = store.LoadSettings();
                if (settings.Enabled)
                    result = CloudSyncWorkflow.Synchronize(settings, store, ReportProgress, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure = exception;
                Trace(exception.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                TraceResult(result, failure);
                var handler = SynchronizationCompleted;
                if (handler != null) try { handler(result, failure); } catch { }
                try { CloudSyncCadNotificationService.Show(result, failure); } catch { }
                if (CloudSyncRetryPolicy.ShouldRetry(failure, result))
                {
                    var failureCount = Interlocked.Increment(ref _consecutiveFailures);
                    var retrySeconds = Math.Min(900, 30 * (int)Math.Pow(2, Math.Min(5, failureCount - 1)));
                    Interlocked.Exchange(ref _pending, 1);
                    lock (LifecycleSync)
                    {
                        if (_timer == null) _timer = new Timer(Execute, null, Timeout.Infinite, Timeout.Infinite);
                        _timer.Change(TimeSpan.FromSeconds(retrySeconds), Timeout.InfiniteTimeSpan);
                    }
                    Trace("同步连续失败 " + failureCount + " 次，将在 " + retrySeconds + " 秒后重试。");
                }
                else
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    if (failure != null) Interlocked.Exchange(ref _pending, 0);
                    else if (Interlocked.Exchange(ref _pending, 0) != 0) RequestSynchronization(false);
                }
            }
        }

        private static void TraceResult(CloudSyncResult result, Exception failure)
        {
            if (failure != null)
            {
                Trace("同步失败：" + failure.Message);
                return;
            }
            if (result == null) { Trace("同步结束：未执行。"); return; }
            Trace("同步完成：" + result.Summary);
            foreach (var operation in result.Operations)
                if (operation != null && operation.Kind != CloudSyncOperationKind.None)
                    Trace(operation.Kind + " " + (operation.LogicalPath ?? string.Empty) +
                        (string.IsNullOrWhiteSpace(operation.Message) ? string.Empty : "；" + operation.Message));
        }

        private static void ReportProgress(CloudSyncProgress progress)
        {
            var handler = SynchronizationProgress;
            if (handler != null) try { handler(progress); } catch { }
        }

        private static void DetachResources(out List<FileSystemWatcher> watchers, out Timer timer)
        {
            watchers = new List<FileSystemWatcher>(Watchers);
            Watchers.Clear();
            timer = _timer;
            _timer = null;
        }

        private static void DisposeResources(IEnumerable<FileSystemWatcher> watchers, Timer timer)
        {
            if (timer != null) try { timer.Dispose(); } catch { }
            foreach (var watcher in watchers ?? new FileSystemWatcher[0])
                try { watcher.Dispose(); } catch { }
        }

        private static bool IsGeneratedSyncArtifact(string path)
        {
            var normalized = (path ?? string.Empty).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.IndexOf(Path.DirectorySeparatorChar + "冲突文件" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf(Path.DirectorySeparatorChar + "历史版本" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf(Path.DirectorySeparatorChar + ".wanluo-sync" + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".rollback", StringComparison.OrdinalIgnoreCase);
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
