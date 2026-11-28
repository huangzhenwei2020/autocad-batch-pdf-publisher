using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    internal static class ExternalCadFolders
    {
        internal static string SettingsPath => Path.Combine(UserDataPaths.RootDirectory, "external-cad-library-folders.txt");

        internal static List<string> Load()
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : ExternalCadLibrary.LocationFile;
            var roots = new List<string>();
            if (!File.Exists(path)) return roots;
            foreach (var line in File.ReadLines(path))
            {
                if (!Path.IsPathRooted(line.Trim())) continue;
                var root = Normalize(line.Trim());
                if (!roots.Any(other => Contains(other, root) || Contains(root, other))) roots.Add(root);
            }
            return roots;
        }

        internal static void Save(IEnumerable<string> previous, IEnumerable<string> roots)
        {
            var path = SettingsPath;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                if (!Load().SequenceEqual(previous, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("图库列表已在另一窗口变更，请重新打开外部图库。");
                try
                {
                    File.WriteAllLines(temporary, roots);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        internal static string Normalize(string path)
        {
            var full = Path.GetFullPath(path);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var volume = Path.GetPathRoot(full);
            return trimmed.Length < volume.Length ? volume : trimmed;
        }

        internal static bool Contains(string folder, string path) =>
            string.Equals(folder, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Prefix(folder), StringComparison.OrdinalIgnoreCase);

        internal static string Relative(string folder, string path) =>
            string.Equals(folder, path, StringComparison.OrdinalIgnoreCase) ? "" : path.Substring(Prefix(folder).Length);

        private static string Prefix(string path) => path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        internal static bool CanMove(string source, string destination) =>
            !Contains(source, destination)
            && string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetDirectoryName(source), destination, StringComparison.OrdinalIgnoreCase);

        internal static string ChildPath(string folder, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.EndsWith(".", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("请输入有效的文件夹名称。");
            return Path.Combine(folder, name);
        }

        internal static void RequireDirectory(string root, string path)
        {
            if (!Contains(root, path)) throw new InvalidOperationException("目标必须位于已添加的图库中。");
            var current = path;
            while (true)
            {
                if (!Directory.Exists(current))
                    throw new InvalidOperationException("文件夹不可用，请检查位置或权限。");
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("请操作磁盘上的实际文件夹。");
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
                current = Path.GetDirectoryName(current);
            }
        }

        internal static void RequireUnusedPath(string path)
        {
            if (Directory.Exists(path) || File.Exists(path))
                throw new InvalidOperationException("目标位置已有同名文件或文件夹。");
        }

        internal static void Move(string source, string target)
        {
            if (source == target) return;
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                RequireUnusedPath(target);
                Directory.Move(source, target);
                return;
            }

            // Directory.Move rejects case-only renames on .NET Framework.
            var temporary = Path.Combine(Path.GetDirectoryName(source), "." + Guid.NewGuid().ToString("N"));
            Directory.Move(source, temporary);
            try { Directory.Move(temporary, target); }
            catch
            {
                Directory.Move(temporary, source);
                throw;
            }
        }

        internal static void MoveDrawing(string source, string target)
        {
            RequireUnusedPath(target);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("请移动实际的 DWG 文件。");
            if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Move(source, target);
                return;
            }

            // Publish a complete copy before touching the original on another volume.
            var temporary = Path.Combine(Path.GetDirectoryName(target), "." + Guid.NewGuid().ToString("N") + ".dwg");
            try
            {
                using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    var written = File.GetLastWriteTimeUtc(source);
                    using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                        output.Flush(true);
                    }
                    File.SetLastWriteTimeUtc(temporary, written);
                    File.Move(temporary, target);
                    try { File.Delete(source); }
                    catch
                    {
                        File.Delete(target);
                        throw;
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
