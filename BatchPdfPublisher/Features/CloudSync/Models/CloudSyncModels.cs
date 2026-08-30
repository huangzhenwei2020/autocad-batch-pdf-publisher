using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    public sealed class CloudSyncSettings
    {
        public CloudSyncSettings()
        {
            Provider = "LocalFolder";
            DeviceName = Environment.MachineName;
            SyncGeneralSettings = true;
            SyncProjectConfigurations = true;
            SyncTemplatesAndSchemes = true;
            SyncProjectFiles = false;
            AutoSync = true;
            HistoryRetentionDays = 30;
            KeepVersionsPerFile = 20;
        }

        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string Provider { get; set; }
        [DataMember] public string SyncFolder { get; set; }
        [DataMember] public string DeviceName { get; set; }
        [DataMember] public bool SyncGeneralSettings { get; set; }
        [DataMember] public bool SyncProjectConfigurations { get; set; }
        [DataMember] public bool SyncTemplatesAndSchemes { get; set; }
        [DataMember] public bool SyncProjectFiles { get; set; }
        [DataMember] public bool AutoSync { get; set; }
        [DataMember] public int HistoryRetentionDays { get; set; }
        [DataMember] public int KeepVersionsPerFile { get; set; }
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
            SchemaVersion = 1;
            Files = new List<CloudSyncFileState>();
        }

        [DataMember] public int SchemaVersion { get; set; }
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
        public int Deleted { get; set; }
        public int Errors { get; set; }

        public string Summary
        {
            get
            {
                return string.Format("上传 {0}，下载 {1}，删除 {2}，冲突 {3}，错误 {4}",
                    Uploaded, Downloaded, Deleted, Conflicts, Errors);
            }
        }
    }
}
