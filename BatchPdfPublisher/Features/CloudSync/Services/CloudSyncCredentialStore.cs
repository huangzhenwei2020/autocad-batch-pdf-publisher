using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>Stores provider tokens with Windows DPAPI, outside every synchronized directory.</summary>
    public sealed class CloudSyncCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WanluoArchitectureTools.CloudSync.v1");
        private readonly string _root;

        public CloudSyncCredentialStore()
            : this(Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "credentials")) { }

        internal CloudSyncCredentialStore(string root) { _root = Path.GetFullPath(root); }

        public void Save(string providerId, CloudSyncCredential credential)
        {
            if (credential == null) throw new ArgumentNullException("credential");
            var path = CredentialPath(providerId); Directory.CreateDirectory(_root);
            byte[] plain;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(CloudSyncCredential)).WriteObject(stream, credential);
                plain = stream.ToArray();
            }
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            Array.Clear(plain, 0, plain.Length);
        }

        public CloudSyncCredential Load(string providerId)
        {
            var path = CredentialPath(providerId);
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using (var stream = new MemoryStream(plain))
                    return (CloudSyncCredential)new DataContractJsonSerializer(typeof(CloudSyncCredential)).ReadObject(stream);
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public void Delete(string providerId)
        {
            var path = CredentialPath(providerId);
            if (File.Exists(path)) File.Delete(path);
        }

        private string CredentialPath(string providerId)
        {
            var safe = string.IsNullOrWhiteSpace(providerId) ? "provider" : providerId.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) safe = safe.Replace(invalid, '_');
            var path = Path.GetFullPath(Path.Combine(_root, safe + ".credential"));
            var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("凭据路径无效。");
            return path;
        }
    }
}
