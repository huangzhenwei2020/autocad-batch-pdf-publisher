using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Runtime.Serialization;

namespace BatchPdfPublisher.Services
{
    // Write-ahead undo log. Payloads are retained, even after recovery, for manual restoration.
    // Cloud publication is append-only: recovery never deletes a published cloud version.
    internal sealed class CloudSyncTransaction : IDisposable
    {
        [ThreadStatic] private static CloudSyncTransaction _current;
        private readonly string _directory;
        private readonly Journal _journal;
        private bool _finished;
        [DataContract] public sealed class Journal { [DataMember] public List<Change> Changes = new List<Change>(); [DataMember] public bool Committed; }
        [DataContract] public sealed class Change { [DataMember] public string Target; [DataMember] public string Before; [DataMember] public string After; [DataMember] public string Backup; }

        internal CloudSyncTransaction(string root)
        {
            if (_current != null) throw new InvalidOperationException("同步事务已启动。");
            _directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _journal = new Journal();
            Save();
            _current = this;
        }

        internal static string Hash(string path) { return File.Exists(path) ? LocalFolderSyncEngine.ComputeHash(path) : null; }
        internal static void BeforeReplace(string target, string expectedBefore, string after)
        {
            if (!string.Equals(Hash(target), expectedBefore, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件在核对后又被修改，已停止覆盖：" + target);
            if (_current == null) return;
            var item = new Change { Target = Path.GetFullPath(target), Before = expectedBefore, After = after };
            if (expectedBefore != null)
            {
                item.Backup = _current._journal.Changes.Count.ToString() + ".backup";
                var backup = Path.Combine(_current._directory, item.Backup);
                File.Copy(target, backup, false);
                using (var durable = new FileStream(backup, FileMode.Open, FileAccess.Write, FileShare.Read)) durable.Flush(true);
                if (!string.Equals(Hash(backup), expectedBefore, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("事务备份校验失败，未替换原文件。");
            }
            _current._journal.Changes.Add(item);
            _current.Save(); // durable journal precedes target mutation
            if (!string.Equals(Hash(target), expectedBefore, StringComparison.OrdinalIgnoreCase))
                throw new IOException("备份期间文件发生变化，已停止覆盖：" + target);
        }

        internal void Commit() { _journal.Committed = true; Save(); _finished = true; }
        private void Save() { WriteJson(Path.Combine(_directory, "journal.json"), _journal); }

        internal static T ReadJson<T>(string path)
        {
            using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        internal static void WriteJson<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        internal static void Recover(string root, Func<string, bool> allowed)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root, "journal.json", SearchOption.AllDirectories))
            {
                var journal = ReadJson<Journal>(file); // unreadable journal fails closed
                if (journal.Committed) continue;
                Restore(Path.GetDirectoryName(file), journal, allowed);
                journal.Committed = true;
                WriteJson(file, journal);
            }
        }

        private static void Restore(string directory, Journal journal, Func<string, bool> allowed)
        {
            foreach (var change in journal.Changes.AsEnumerable().Reverse())
            {
                if (!allowed(change.Target)) throw new IOException("恢复记录的目标不在本机允许范围内：" + change.Target);
                var current = Hash(change.Target);
                if (string.Equals(current, change.Before, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(current, change.After, StringComparison.OrdinalIgnoreCase))
                {
                    // Do not roll back an intervening user save. Keep both versions and an explicit recovery notice.
                    File.AppendAllText(Path.Combine(directory, "需要人工核对.txt"), change.Target + Environment.NewLine);
                    CloudSyncSettingsStore.ReportRecovery("同步恢复时发现后续保存，已保留最新本机文件；请核对备份：" + directory);
                    continue;
                }
                if (change.Before == null) File.Delete(change.Target);
                else
                {
                    var backup = Path.Combine(directory, change.Backup);
                    if (Path.GetFileName(change.Backup) != change.Backup || Hash(backup) != change.Before)
                        throw new IOException("恢复备份校验失败，已保留当前文件。");
                    var tmp = change.Target + ".recovery-" + Guid.NewGuid().ToString("N") + ".tmp";
                    File.Copy(backup, tmp, false);
                    if (File.Exists(change.Target)) File.Replace(tmp, change.Target, null); else File.Move(tmp, change.Target);
                }
            }
        }

        public void Dispose()
        {
            _current = null;
            if (!_finished)
            {
                Restore(_directory, _journal, path => true);
                _journal.Committed = true;
                Save();
            }
        }
    }
}
