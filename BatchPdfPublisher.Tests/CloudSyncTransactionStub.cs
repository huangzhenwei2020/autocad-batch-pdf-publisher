using System.IO;
using System.Security.Cryptography;

namespace BatchPdfPublisher.Services
{
    // The real CloudSyncTransaction is compiled into BatchPdfPublisher.CloudSync.Tests,
    // where the write-ahead-log safety properties are asserted. The project-store and
    // project-sync suites only exercise settings persistence, so they link this stub:
    // it mirrors the real file-hash precondition behaviour without pulling the whole
    // synchronization engine (and its provider dependencies) into those projects.
    internal static class CloudSyncTransaction
    {
        internal static string Hash(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var text = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        internal static void BeforeReplace(string target, string expectedBefore, string after)
        {
            if (!string.Equals(Hash(target), expectedBefore, System.StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件在核对后又被修改，已停止覆盖：" + target);
        }
    }
}
