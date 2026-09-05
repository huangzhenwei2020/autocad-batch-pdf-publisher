using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.Services
{
    [DataContract]
    internal sealed class CloudSyncRemoteInventory
    {
        public CloudSyncRemoteInventory() { Files = new List<string>(); }
        [DataMember] public string UpdatedAtUtc { get; set; }
        [DataMember] public List<string> Files { get; set; }
    }

    public static class CloudSyncRemoteInventoryStore
    {
        private static readonly object FileSync = new object();
        private static string InventoryPath { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "remote-inventory.json"); } }

        public static void Save(IEnumerable<string> relativePaths)
        {
            var inventory = new CloudSyncRemoteInventory
            {
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
                Files = (relativePaths ?? Enumerable.Empty<string>()).Select(CloudSyncSource.NormalizeLogicalPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
            };
            lock (FileSync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(InventoryPath));
                var temporary = InventoryPath + ".tmp";
                using (var stream = File.Create(temporary)) new DataContractJsonSerializer(typeof(CloudSyncRemoteInventory)).WriteObject(stream, inventory);
                if (File.Exists(InventoryPath)) File.Replace(temporary, InventoryPath, InventoryPath + ".bak", true);
                else File.Move(temporary, InventoryPath);
            }
        }

        public static bool HasProjectFiles(string cloudId)
        {
            if (string.IsNullOrWhiteSpace(cloudId)) return false;
            var prefix = "万落建筑云同步/项目文件/" + cloudId.Trim('/') + "/";
            return LoadFiles().Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasSnapshot()
        {
            lock (FileSync)
            {
                if (!File.Exists(InventoryPath)) return false;
                try
                {
                    using (var stream = File.OpenRead(InventoryPath))
                        return new DataContractJsonSerializer(typeof(CloudSyncRemoteInventory)).ReadObject(stream) is CloudSyncRemoteInventory;
                }
                catch { return false; }
            }
        }

        public static IList<string> ProjectFilePaths()
        {
            const string prefix = "万落建筑云同步/项目文件/";
            return LoadFiles().Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(path => path.Substring("万落建筑云同步/".Length))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IList<string> LoadFiles()
        {
            lock (FileSync)
            {
                try
                {
                    if (!File.Exists(InventoryPath)) return new string[0];
                    using (var stream = File.OpenRead(InventoryPath))
                    {
                        var value = (CloudSyncRemoteInventory)new DataContractJsonSerializer(typeof(CloudSyncRemoteInventory)).ReadObject(stream);
                        return value == null || value.Files == null ? (IList<string>)new string[0] : value.Files;
                    }
                }
                catch { return new string[0]; }
            }
        }
    }
}
