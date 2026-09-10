using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;
using System.Threading;
using System.Security.Cryptography;
using System.Text;

namespace BatchPdfPublisher.Services
{
    // Each ZIP is a complete immutable commit, named by its SHA256. No shared mutable HEAD.
    // A file revision supersedes ONLY the observed parents named for that file.
    // Simultaneous uploads therefore create siblings, never last-writer-wins replacements.
    internal sealed class ImmutableCloudJournal
    {
        public const string RemoteDirectory = "万落安全同步V2";
        [DataContract] public sealed class Manifest { [DataMember] public int Version = 2; [DataMember] public List<Revision> Files = new List<Revision>(); }
        [DataContract] public sealed class Revision
        {
            [DataMember] public string Path; [DataMember] public string Hash; [DataMember] public List<string> Parents = new List<string>();
            [DataMember] public string Payload; [DataMember] public long ModifiedTicks;
        }
        [DataContract] public sealed class Resolution { [DataMember] public string Hash; [DataMember] public List<string> Parents = new List<string>(); }
        private sealed class Node { public string Id; public Revision Value; public string File; }
        private readonly string _root;
        private readonly string _mirror;
        private readonly Dictionary<string, List<Node>> _heads = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal string Archives { get { return Path.Combine(_root, "archives"); } }
        internal string Outbox { get { return Path.Combine(_root, "outbox"); } }

        internal ImmutableCloudJournal(string root, string mirror) { _root = root; _mirror = mirror; Directory.CreateDirectory(Archives); }
        internal static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
                relative.StartsWith("/") || relative.Split('/').Any(x => x == ".." || x == "." || x.Length == 0 ||
                    x.TrimEnd(' ', '.') != x || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("版本包路径无效。");
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本包路径越界。");
            // Never follow a directory junction supplied in a user data tree.
            for (var parent = Path.GetDirectoryName(path); parent != null && parent.Length >= prefix.Length - 1; parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("同步路径包含目录链接，已停止：" + parent);
            return path;
        }

        internal void Materialize(CloudSyncCatalog catalog, CancellationToken token)
        {
            _heads.Clear(); _observed.Clear(); Blocked.Clear(); _resolved.Clear();
            var nodes = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in Directory.GetFiles(Archives, "*.zip"))
            {
                token.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(archive);
                if (!string.Equals(id, LocalFolderSyncEngine.ComputeHash(archive, token), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("云端不可变版本校验失败：" + id);
                using (var zip = ZipFile.OpenRead(archive))
                {
                    var meta = zip.GetEntry("manifest.json");
                    if (meta == null || meta.Length > 32 * 1024 * 1024) throw new InvalidDataException("版本清单缺失或过大。");
                    Manifest manifest;
                    using (var stream = meta.Open()) manifest = (Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(stream);
                    if (manifest == null || manifest.Version != 2 || manifest.Files == null || manifest.Files.Count > 100000)
                        throw new InvalidDataException("不支持的同步版本包。");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var revision in manifest.Files)
                    {
                        SafePath(_mirror, revision.Path);
                        if (!seen.Add(revision.Path) || revision.Parents == null) throw new InvalidDataException("版本清单含重复文件或无效父版本。");
                        var payload = zip.GetEntry(revision.Payload ?? "");
                        if (payload == null) throw new InvalidDataException("版本包缺少文件。");
                        var extracted = SafePath(Path.Combine(_root, "objects", id), revision.Payload);
                        if (!File.Exists(extracted) || CloudSyncTransaction.Hash(extracted) != revision.Hash)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(extracted));
                            var tmp = extracted + ".tmp";
                            using (var input = payload.Open()) using (var output = File.Create(tmp)) input.CopyTo(output);
                            if (CloudSyncTransaction.Hash(tmp) != revision.Hash) throw new InvalidDataException("版本文件哈希不符。");
                            if (File.Exists(extracted)) File.Replace(tmp, extracted, null); else File.Move(tmp, extracted);
                        }
                        List<Node> list;
                        if (!nodes.TryGetValue(revision.Path, out list)) nodes[revision.Path] = list = new List<Node>();
                        list.Add(new Node { Id = id, Value = revision, File = extracted });
                    }
                }
            }
            foreach (var pair in nodes)
            {
                var byId = pair.Value.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
                ValidateAncestors(byId);
                var parents = new HashSet<string>(pair.Value.SelectMany(n => n.Value.Parents), StringComparer.OrdinalIgnoreCase);
                _heads[pair.Key] = pair.Value.Where(n => !parents.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            }
            // Save the exact observed head set for explicit conflict resolution in Sync Center.
            var headMap = _heads.ToDictionary(x => x.Key, x => x.Value.Select(n => n.Id).ToList(), StringComparer.OrdinalIgnoreCase);
            CloudSyncTransaction.WriteJson(Path.Combine(_mirror, ".wanluo-sync", "heads.json"), headMap);
            foreach (var pair in _heads)
            {
                token.ThrowIfCancellationRequested();
                string local;
                if (!catalog.TryResolve(pair.Key, out local)) continue;
                var heads = pair.Value;
                var target = SafePath(_mirror, pair.Key);
                var intentPath = ResolutionPath(_mirror, pair.Key);
                if (File.Exists(intentPath))
                {
                    var intent = CloudSyncTransaction.ReadJson<Resolution>(intentPath);
                    if (intent.Parents != null && new HashSet<string>(intent.Parents, StringComparer.OrdinalIgnoreCase).SetEquals(heads.Select(n => n.Id)) &&
                        CloudSyncTransaction.Hash(local) == intent.Hash && CloudSyncTransaction.Hash(target) == intent.Hash)
                    { _resolved.Add(pair.Key); _observed[pair.Key] = intent.Hash; continue; }
                }
                if (heads.Select(n => n.Value.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    Blocked.Add(pair.Key);
                    // One pair per cloud sibling; all payloads remain in immutable archives.
                    foreach (var node in heads)
                    {
                        string group;
                        using (var sha = SHA256.Create()) group = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                            string.Join("|", heads.Select(n => n.Id)) + "|" + node.Id))).Replace("-", "");
                        var conflict = SafePath(Path.Combine(_mirror, "冲突文件", "云端并发-" + group), pair.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(conflict));
                        if (!File.Exists(conflict + ".remote-conflict")) File.Copy(node.File, conflict + ".remote-conflict");
                        if (File.Exists(local) && !File.Exists(conflict + ".local-conflict")) File.Copy(local, conflict + ".local-conflict");
                        CloudSyncTransaction.WriteJson(conflict + ".heads.json", heads.Select(n => n.Id).ToList());
                    }
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(heads[0].File, target, true);
                _observed[pair.Key] = heads[0].Value.Hash;
            }
            // Legacy files are read-only seeds; capture them too, without allowing their timestamps to win.
            foreach (var file in catalog.EnumerateFiles())
            {
                var target = SafePath(_mirror, file.LogicalPath);
                if (!_observed.ContainsKey(file.LogicalPath)) _observed[file.LogicalPath] = CloudSyncTransaction.Hash(target);
            }
        }

        private static void ValidateAncestors(Dictionary<string, Node> nodes)
        {
            var counts = nodes.ToDictionary(p => p.Key, p => p.Value.Value.Parents.Distinct().Count(), StringComparer.OrdinalIgnoreCase);
            var children = nodes.ToDictionary(p => p.Key, p => new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.Values)
                foreach (var id in node.Value.Parents.Distinct())
                {
                    if (!nodes.ContainsKey(id)) throw new IOException("云端父版本尚未到齐，暂不应用任何文件。");
                    children[id].Add(node.Id);
                }
            var ready = new Queue<string>(counts.Where(p => p.Value == 0).Select(p => p.Key));
            var visited = 0;
            while (ready.Count > 0)
            {
                var id = ready.Dequeue(); visited++;
                foreach (var child in children[id]) if (--counts[child] == 0) ready.Enqueue(child);
            }
            if (visited != nodes.Count) throw new InvalidDataException("版本清单存在循环。");
        }

        internal string CreateCommit(CloudSyncCatalog catalog, CancellationToken token)
        {
            var manifest = new Manifest();
            var sources = new List<string>();
            foreach (var file in catalog.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();
                if (Blocked.Contains(file.LogicalPath)) continue;
                var path = SafePath(_mirror, file.LogicalPath);
                var hash = CloudSyncTransaction.Hash(path);
                if (hash == null) continue;
                string old; _observed.TryGetValue(file.LogicalPath, out old);
                // Migrate observed legacy contents as well. Conflict files are never selected here.
                if (hash == old && _heads.ContainsKey(file.LogicalPath) && !_resolved.Contains(file.LogicalPath)) continue;
                List<Node> heads; _heads.TryGetValue(file.LogicalPath, out heads);
                manifest.Files.Add(new Revision { Path = file.LogicalPath, Hash = hash, Payload = "files/" + sources.Count,
                    ModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks,
                    Parents = heads == null ? new List<string>() : heads.Select(n => n.Id).ToList() });
                sources.Add(path);
            }
            if (sources.Count == 0) return null;
            Directory.CreateDirectory(Outbox);
            var temp = Path.Combine(Outbox, Guid.NewGuid().ToString("N") + ".tmp");
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var output = zip.CreateEntry("manifest.json").Open()) new DataContractJsonSerializer(typeof(Manifest)).WriteObject(output, manifest);
                for (var i = 0; i < sources.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(sources[i], manifest.Files[i].Payload, CompressionLevel.NoCompression);
                    if (CloudSyncTransaction.Hash(sources[i]) != manifest.Files[i].Hash) throw new IOException("打包期间文件变化，未提交。");
                }
            }
            var final = Path.Combine(Outbox, LocalFolderSyncEngine.ComputeHash(temp, token) + ".zip");
            if (File.Exists(final)) File.Delete(temp); else File.Move(temp, final);
            return final;
        }

        internal static void RecordConflictHeads(string mirror, string logicalPath, string conflictBase)
        {
            var mapPath = Path.Combine(mirror, ".wanluo-sync", "heads.json");
            if (!File.Exists(mapPath)) return;
            var map = CloudSyncTransaction.ReadJson<Dictionary<string, List<string>>>(mapPath);
            var pair = map.FirstOrDefault(x => string.Equals(x.Key, logicalPath, StringComparison.OrdinalIgnoreCase));
            CloudSyncTransaction.WriteJson(conflictBase + ".heads.json", pair.Value ?? new List<string>());
        }

        internal static string HeadStamp(string mirror, string logicalPath)
        {
            var path = Path.Combine(mirror, ".wanluo-sync", "heads.json");
            if (!File.Exists(path)) return null;
            var map = CloudSyncTransaction.ReadJson<Dictionary<string, List<string>>>(path);
            var pair = map.FirstOrDefault(x => string.Equals(x.Key, logicalPath, StringComparison.OrdinalIgnoreCase));
            return pair.Value == null || pair.Value.Count == 0 ? null : string.Join("|", pair.Value.OrderBy(x => x, StringComparer.Ordinal));
        }

        internal static void RecordResolution(string mirror, string logicalPath, string hash, string conflictCopy = null)
        {
            var mapPath = Path.Combine(mirror, ".wanluo-sync", "heads.json");
            if (!File.Exists(mapPath)) return;
            var map = CloudSyncTransaction.ReadJson<Dictionary<string, List<string>>>(mapPath);
            var pair = map.FirstOrDefault(x => string.Equals(x.Key, logicalPath, StringComparison.OrdinalIgnoreCase));
            if (conflictCopy != null)
            {
                var suffix = conflictCopy.EndsWith(".local-conflict", StringComparison.OrdinalIgnoreCase) ? ".local-conflict" : ".remote-conflict";
                var snapshot = conflictCopy.Substring(0, conflictCopy.Length - suffix.Length) + ".heads.json";
                var seen = File.Exists(snapshot) ? CloudSyncTransaction.ReadJson<List<string>>(snapshot) : new List<string>();
                if (!new HashSet<string>(seen).SetEquals(pair.Value ?? new List<string>()))
                    throw new IOException("云端冲突分支已经更新，请刷新同步中心后重新选择；本次选择未提交。");
            }
            if (pair.Value != null) CloudSyncTransaction.WriteJson(ResolutionPath(mirror, logicalPath), new Resolution { Hash = hash, Parents = pair.Value });
        }
        private static string ResolutionPath(string mirror, string path) { return SafePath(Path.Combine(mirror, ".wanluo-sync", "resolutions"), path + ".json"); }
    }
}
