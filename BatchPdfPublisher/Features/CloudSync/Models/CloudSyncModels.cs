using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    public sealed class CloudSyncSettings
    {
        public CloudSyncSettings()
        {
            Provider = "BaiduNetdisk";
            DeviceName = Environment.MachineName;
            SyncGeneralSettings = true;
            SyncProjectConfigurations = true;
            SyncTemplatesAndSchemes = true;
            SyncProjectFiles = false;
            AutoSync = true;
            InitialSyncPreference = "Remote";
            HistoryRetentionDays = 30;
            KeepVersionsPerFile = 20;
            SystemPackageIntervalMinutes = 30;
            ProjectMappings = new List<CloudSyncProjectMapping>();
            ProjectWorkspaceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑项目");
            BackupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "万落建筑备份");
        }

        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string Provider { get; set; }
        [DataMember] public string ProviderClientId { get; set; }
        [DataMember] public string ProviderRedirectUri { get; set; }
        [DataMember] public string ProviderBrokerUrl { get; set; }
        [DataMember] public string ProviderRemoteFolder { get; set; }
        [DataMember] public string SyncFolder { get; set; }
        [DataMember] public string DeviceName { get; set; }
        [DataMember] public bool SyncGeneralSettings { get; set; }
        [DataMember] public bool SyncProjectConfigurations { get; set; }
        [DataMember] public bool SyncTemplatesAndSchemes { get; set; }
        [DataMember] public bool SyncProjectFiles { get; set; }
        [DataMember] public bool AutoSync { get; set; }
        [DataMember] public string InitialSyncPreference { get; set; }
        [DataMember] public int HistoryRetentionDays { get; set; }
        [DataMember] public int KeepVersionsPerFile { get; set; }
        [DataMember] public int SystemPackageIntervalMinutes { get; set; }
        [DataMember] public List<CloudSyncProjectMapping> ProjectMappings { get; set; }
        [DataMember] public string ProjectWorkspaceRoot { get; set; }
        [DataMember] public string BackupRoot { get; set; }
    }

    [DataContract]
    public sealed class CloudSyncProjectMapping
    {
        [DataMember] public string ProjectName { get; set; }
        [DataMember] public string CloudId { get; set; }
        [DataMember] public string LocalFolder { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool SelectionConfirmed { get; set; }
    }

    [DataContract]
    public sealed class CloudSyncCredential
    {
        [DataMember] public string ClientId { get; set; }
        [DataMember] public string ClientSecret { get; set; }
        [DataMember] public string AuthMode { get; set; }
        [DataMember] public string AccessToken { get; set; }
        [DataMember] public string RefreshToken { get; set; }
        [DataMember] public string ExpiresAtUtc { get; set; }
        [DataMember] public string AccountDisplayName { get; set; }
        [DataMember] public string AccountIdentity { get; set; }
    }

    [DataContract]
    public sealed class CloudSyncFileState
    {
        [DataMember] public string LogicalPath { get; set; }
        [DataMember] public string BaseHash { get; set; }
        [DataMember] public string LocalHash { get; set; }
        [DataMember] public string RemoteHash { get; set; }
        [DataMember] public string LastSynchronizedAtUtc { get; set; }
    }

    [DataContract]
    public sealed class CloudSyncState
    {
        public CloudSyncState()
        {
            SchemaVersion = 2;
            Files = new List<CloudSyncFileState>();
        }

        [DataMember] public int SchemaVersion { get; set; }
        [DataMember] public string ProviderId { get; set; }
        [DataMember] public string ProviderScope { get; set; }
        [DataMember] public List<CloudSyncFileState> Files { get; set; }
    }

    public enum CloudSyncOperationKind
    {
        None,
        Upload,
        Download,
        DeleteLocal,
        DeleteRemote,
        Conflict,
        Pending,
        Error
    }

    public sealed class CloudSyncOperation
    {
        public string LogicalPath { get; set; }
        public CloudSyncOperationKind Kind { get; set; }
        public string Message { get; set; }
    }

    public sealed class CloudSyncResult
    {
        public CloudSyncResult()
        {
            Operations = new List<CloudSyncOperation>();
        }

        public IList<CloudSyncOperation> Operations { get; private set; }
        public int Uploaded { get; set; }
        public int Downloaded { get; set; }
        public int Conflicts { get; set; }
        public int Pending { get; set; }
        public int Deleted { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int LocalFileCount { get; set; }
        public int RemoteFileCount { get; set; }
        public long UploadedBytes { get; set; }
        public long DownloadedBytes { get; set; }

        public string Summary
        {
            get
            {
                return string.Format("上传 {0}，下载 {1}，删除 {2}，冲突 {3}，待应用 {4}，警告 {5}，错误 {6}",
                    Uploaded, Downloaded, Deleted, Conflicts, Pending, Warnings, Errors);
            }
        }
    }

    public sealed class CloudSyncProgress
    {
        public string Stage { get; set; }
        public string LogicalPath { get; set; }
        public int Completed { get; set; }
        public int Total { get; set; }
        public long BytesCompleted { get; set; }
        public long BytesTotal { get; set; }
        public double BytesPerSecond { get; set; }
        public string Direction { get; set; }

        public string SpeedText
        {
            get { return BytesPerSecond <= 0d ? string.Empty : FormatBytes((long)BytesPerSecond) + "/s"; }
        }

        public string BytesText
        {
            get { return BytesTotal <= 0 ? string.Empty : FormatBytes(BytesCompleted) + " / " + FormatBytes(BytesTotal); }
        }

        public int Percentage
        {
            get { return BytesTotal > 0 ? Math.Max(0, Math.Min(100, (int)Math.Round(BytesCompleted * 100d / BytesTotal))) : Total <= 0 ? 0 : Math.Max(0, Math.Min(100, (int)Math.Round(Completed * 100d / Total))); }
        }

        private static string FormatBytes(long value) { var units = new[] { "B", "KB", "MB", "GB" }; var number = Math.Max(0, value); var index = 0; double display = number; while (display >= 1024d && index < units.Length - 1) { display /= 1024d; index++; } return display.ToString(index == 0 ? "0" : "0.0") + " " + units[index]; }
    }
}
