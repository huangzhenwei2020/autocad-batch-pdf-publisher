using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BatchPdfPublisher.Services
{
    /// <summary>Stores a stable DWG copy created from an open AutoCAD database after a successful save.</summary>
    internal static class CloudSyncSavedDrawingSnapshotStore
    {
        private static string Root { get { return Path.Combine(UserDataPaths.RootDirectory, ".cloud-sync", "saved-drawing-snapshots"); } }

        internal static string TemporaryPath(string sourcePath)
        {
            Directory.CreateDirectory(Root);
            return Path.Combine(Root, Key(sourcePath) + "." + Guid.NewGuid().ToString("N") + ".tmp.dwg");
        }

        internal static void Commit(string sourcePath, string temporaryPath)
        {
            var source = Normalize(sourcePath);
            if (!File.Exists(temporaryPath)) throw new FileNotFoundException("保存后的 DWG 同步副本不存在。", temporaryPath);
            var snapshot = SnapshotPath(source);
            var stamp = snapshot + ".stamp";
            var hash = LocalFolderSyncEngine.ComputeHash(temporaryPath);
            var sourceTicks = File.GetLastWriteTimeUtc(source).Ticks;
            Directory.CreateDirectory(Root);
            if (File.Exists(snapshot)) File.Replace(temporaryPath, snapshot, null, true); else File.Move(temporaryPath, snapshot);
            var stampTemporary = stamp + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(stampTemporary, new[] { source, sourceTicks.ToString(CultureInfo.InvariantCulture), hash });
            if (File.Exists(stamp)) File.Replace(stampTemporary, stamp, null, true); else File.Move(stampTemporary, stamp);
        }

        internal static bool TryGet(string sourcePath, out string snapshotPath, out string snapshotHash)
        {
            snapshotPath = null; snapshotHash = null;
            try
            {
                var source = Normalize(sourcePath);
                var snapshot = SnapshotPath(source);
                var stamp = snapshot + ".stamp";
                if (!File.Exists(source) || !File.Exists(snapshot) || !File.Exists(stamp)) return false;
                var lines = File.ReadAllLines(stamp);
                long ticks;
                if (lines.Length != 3 || !string.Equals(lines[0], source, StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                    ticks != File.GetLastWriteTimeUtc(source).Ticks || string.IsNullOrWhiteSpace(lines[2])) return false;
                var actual = LocalFolderSyncEngine.ComputeHash(snapshot);
                if (!string.Equals(actual, lines[2], StringComparison.OrdinalIgnoreCase)) return false;
                snapshotPath = snapshot; snapshotHash = actual;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static string SnapshotPath(string sourcePath) { return Path.Combine(Root, Key(sourcePath) + ".dwg"); }
        private static string Normalize(string path) { return Path.GetFullPath(path ?? string.Empty).Trim(); }
        private static string Key(string sourcePath)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(sourcePath).ToUpperInvariant())).Take(16).Select(x => x.ToString("x2")));
        }
    }
}
