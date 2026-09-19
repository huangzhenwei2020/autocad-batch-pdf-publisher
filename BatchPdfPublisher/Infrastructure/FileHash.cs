using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace BatchPdfPublisher.Services
{
    /// <summary>
    /// 文件内容哈希（SHA256 大写十六进制，与云同步一直以来的口径一致）。
    ///
    /// 原来是 LocalFolderSyncEngine 的私有实现，云同步事务与同步引擎都用它；
    /// 因为启动器也要编译云同步事务与项目配置存储（源码链接），抽到这里可以
    /// **只保留一份**实现，避免两边算法漂移。
    /// </summary>
    public static class FileHash
    {
        public static string ComputeSha256Upper(string path)
        {
            return ComputeSha256Upper(path, CancellationToken.None);
        }

        public static string ComputeSha256Upper(string path, CancellationToken cancellationToken)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    algorithm.TransformBlock(buffer, 0, read, null, 0);
                }
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(algorithm.Hash).Replace("-", string.Empty);
            }
        }
    }
}
