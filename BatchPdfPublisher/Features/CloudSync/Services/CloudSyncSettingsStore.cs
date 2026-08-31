using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.Services
{
    public sealed class CloudSyncSettingsStore
    {
        private static readonly object FileSync = new object();
        private readonly string _settingsPath;
        private readonly string _statePath;

        public CloudSyncSettingsStore()
            : this(UserDataPaths.SettingsFile("cloud-sync.settings.json"),
                Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "state.json"))
        {
        }

        internal CloudSyncSettingsStore(string settingsPath, string statePath)
        {
            _settingsPath = settingsPath;
            _statePath = statePath;
        }

        public CloudSyncSettings LoadSettings()
        {
            var settings = Load(_settingsPath, new CloudSyncSettings());
            if (string.IsNullOrWhiteSpace(settings.Provider)) settings.Provider = "LocalFolder";
            settings.SyncFolder = CloudSyncFolderDetector.ResolveUsableFolder(settings.SyncFolder);
            if (settings.ProjectMappings == null) settings.ProjectMappings = new System.Collections.Generic.List<CloudSyncProjectMapping>();
            return settings;
        }

        public void SaveSettings(CloudSyncSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (string.IsNullOrWhiteSpace(settings.DeviceName)) settings.DeviceName = Environment.MachineName;
            if (settings.HistoryRetentionDays < 1) settings.HistoryRetentionDays = 30;
            if (settings.KeepVersionsPerFile < 1) settings.KeepVersionsPerFile = 20;
            if (settings.ProjectMappings == null) settings.ProjectMappings = new System.Collections.Generic.List<CloudSyncProjectMapping>();
            Save(_settingsPath, settings);
        }

        public CloudSyncState LoadState()
        {
            var state = Load(_statePath, new CloudSyncState());
            if (state.Files == null) state.Files = new System.Collections.Generic.List<CloudSyncFileState>();
            return state;
        }

        public void SaveState(CloudSyncState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            Save(_statePath, state);
        }

        private static T Load<T>(string path, T fallback)
        {
            lock (FileSync)
            {
                try
                {
                    if (!File.Exists(path)) return fallback;
                    using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                }
                catch
                {
                    return fallback;
                }
            }
        }

        private static void Save<T>(string path, T value)
        {
            lock (FileSync)
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory)) throw new IOException("同步配置目录无效。");
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                    else File.Move(temporary, path);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }
    }
}
