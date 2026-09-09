using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    internal sealed class PaddleOcrComponentProgress
    {
        public string Stage { get; set; }
        public long Completed { get; set; }
        public long Total { get; set; }
        public int Percentage { get { return Total <= 0 ? 0 : Math.Max(0, Math.Min(100, (int)(Completed * 100L / Total))); } }
    }

    internal sealed class PaddleOcrComponentStatus
    {
        public bool IsInstalled { get; set; }
        public bool CanUninstall { get; set; }
        public string InstallDirectory { get; set; }
        public string ExecutablePath { get; set; }
        public string Version { get; set; }
        public string Message { get; set; }
    }

    [DataContract]
    internal sealed class PaddleOcrComponentManifest
    {
        [DataMember] public string Component { get; set; }
        [DataMember] public int ProtocolVersion { get; set; }
        [DataMember] public string PaddleOCR { get; set; }
        [DataMember] public string PaddlePaddle { get; set; }
        [DataMember] public string Model { get; set; }
        [DataMember] public List<PaddleOcrComponentFile> Files { get; set; }
    }

    [DataContract]
    internal sealed class PaddleOcrComponentFile
    {
        [DataMember] public string Path { get; set; }
        [DataMember] public long Size { get; set; }
        [DataMember] public string Sha256 { get; set; }
    }

    [DataContract]
    internal sealed class GitHubPaddleRelease
    {
        [DataMember(Name = "draft")] public bool IsDraft { get; set; }
        [DataMember(Name = "prerelease")] public bool IsPrerelease { get; set; }
        [DataMember(Name = "assets")] public List<GitHubPaddleAsset> Assets { get; set; }
    }

    [DataContract]
    internal sealed class GitHubPaddleAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
        [DataMember(Name = "digest")] public string Digest { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    internal sealed class LineVisionPaddleOcrComponentService
    {
        internal const string ComponentFolderName = "LineVisionPaddleOcrWorker";
        internal const string ExecutableName = "LineVisionPaddleOcrWorker.exe";
        internal const string ManifestName = "component-manifest.json";
        private const string ReleasesApi = "https://api.github.com/repos/huangzhenwei2020/autocad-batch-pdf-publisher/releases?per_page=20";
        private const string ReleasesPage = "https://github.com/huangzhenwei2020/autocad-batch-pdf-publisher/releases/latest";
        private static readonly object SettingsSync = new object();

        private static string LocationSettingsPath { get { return Path.Combine(UserDataPaths.RootDirectory, "运行文件", "linevision-paddle-component.path"); } }
        public static string DefaultInstallDirectory { get { return Path.Combine(UserDataPaths.RootDirectory, "运行文件", ComponentFolderName); } }

        public static string GetInstallDirectory()
        {
            lock (SettingsSync)
            {
                try
                {
                    if (File.Exists(LocationSettingsPath))
                    {
                        var configured = File.ReadAllText(LocationSettingsPath).Trim();
                        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
                    }
                }
                catch { }
                return Path.GetFullPath(DefaultInstallDirectory);
            }
        }

        public static void SetInstallParent(string parentDirectory)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory)) throw new ArgumentException("请选择增强组件的安装位置。", "parentDirectory");
            var parent = Path.GetFullPath(parentDirectory);
            Directory.CreateDirectory(parent);
            var target = string.Equals(new DirectoryInfo(parent).Name, ComponentFolderName, StringComparison.OrdinalIgnoreCase)
                ? parent : Path.Combine(parent, ComponentFolderName);
            lock (SettingsSync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocationSettingsPath));
                File.WriteAllText(LocationSettingsPath, target, Encoding.UTF8);
            }
        }

        public static string ResolveWorkerPath()
        {
            var configured = Environment.GetEnvironmentVariable("WANLUO_LINEVISION_PADDLE_WORKER");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var managedStatus = InspectDirectory(GetInstallDirectory(), true);
            if (managedStatus.IsInstalled) return managedStatus.ExecutablePath;
            return null;
        }

        public static PaddleOcrComponentStatus GetStatus()
        {
            var directory = GetInstallDirectory();
            var managed = InspectDirectory(directory, true);
            if (managed.IsInstalled || managed.CanUninstall) return managed;
            var packaged = Path.Combine(UserDataPaths.PluginDirectory, "OcrEngine", ComponentFolderName);
            var packagedStatus = InspectDirectory(packaged, false);
            return packagedStatus.IsInstalled ? packagedStatus : managed;
        }

        private static PaddleOcrComponentStatus InspectDirectory(string directory, bool canUninstall)
        {
            var executable = Path.Combine(directory, ExecutableName);
            var manifestPath = Path.Combine(directory, ManifestName);
            if (!File.Exists(executable) || !File.Exists(manifestPath))
                return new PaddleOcrComponentStatus { InstallDirectory = directory, ExecutablePath = executable, Message = "尚未安装 PaddleOCR 增强组件。" };
            try
            {
                var manifest = ReadManifest(manifestPath);
                ValidateManifestIdentity(manifest);
                ValidateInstalledFiles(directory, manifest);
                return new PaddleOcrComponentStatus
                {
                    IsInstalled = true,
                    CanUninstall = canUninstall,
                    InstallDirectory = directory,
                    ExecutablePath = executable,
                    Version = FormatVersion(manifest),
                    Message = canUninstall ? "PaddleOCR 增强组件已安装。" : "发布包已包含 PaddleOCR 增强组件。"
                };
            }
            catch (Exception exception)
            {
                return new PaddleOcrComponentStatus { CanUninstall = canUninstall, InstallDirectory = directory, ExecutablePath = executable, Message = "组件不完整：" + exception.Message };
            }
        }

        public static async Task<string> DownloadLatestPackageAsync(IProgress<PaddleOcrComponentProgress> progress, CancellationToken cancellationToken)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                var assemblyVersion = typeof(LineVisionPaddleOcrComponentService).Assembly.GetName().Version;
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WanLuoArchitectureTools", assemblyVersion == null ? "1.0" : assemblyVersion.ToString()));
                progress?.Report(new PaddleOcrComponentProgress { Stage = "正在查询可用增强组件" });
                List<GitHubPaddleRelease> releases;
                using (var response = await http.GetAsync(ReleasesApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new IOException("查询组件失败（HTTP " + (int)response.StatusCode + "）。");
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        releases = (List<GitHubPaddleRelease>)new DataContractJsonSerializer(typeof(List<GitHubPaddleRelease>)).ReadObject(stream);
                }
                var asset = (releases ?? new List<GitHubPaddleRelease>()).Where(item => item != null && !item.IsDraft && !item.IsPrerelease)
                    .SelectMany(item => item.Assets ?? new List<GitHubPaddleAsset>())
                    .FirstOrDefault(item => item != null && !string.IsNullOrWhiteSpace(item.Name)
                        && item.Name.StartsWith("WanLuo-PaddleOCR-", StringComparison.OrdinalIgnoreCase)
                        && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (asset == null) throw new InvalidOperationException("GitHub 尚未发布 PaddleOCR 用户组件，请选择离线安装包。 ");
                if (string.IsNullOrWhiteSpace(asset.Digest) || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("线上组件没有 SHA256 摘要，已拒绝下载。请联系发布者重新上传合规组件。 ");
                if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("线上组件下载地址无效。 ");
                if (!string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("线上组件不是由项目 GitHub Release 提供，已拒绝下载。 ");
                if (asset.Size <= 0 || asset.Size > 2L * 1024L * 1024L * 1024L)
                    throw new InvalidDataException("线上组件大小异常，已拒绝下载。 ");

                var target = Path.Combine(UserDataPaths.TemporaryDirectory, "WanLuo-PaddleOCR-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    using (var response = await http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw new IOException("下载组件失败（HTTP " + (int)response.StatusCode + "）。");
                        var total = response.Content.Headers.ContentLength ?? asset.Size;
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                        {
                            var buffer = new byte[1024 * 1024]; long completed = 0; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                                completed += read;
                                progress?.Report(new PaddleOcrComponentProgress { Stage = "正在下载 PaddleOCR 增强组件", Completed = completed, Total = total });
                            }
                        }
                    }
                    var expected = asset.Digest.Substring("sha256:".Length).Trim();
                    if (!string.Equals(Sha256(target), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("线上组件 SHA256 校验失败，文件未安装。 ");
                    return target;
                }
                catch
                {
                    TryDeleteFile(target);
                    throw;
                }
            }
        }

        public static void InstallPackage(string packagePath, IProgress<PaddleOcrComponentProgress> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) throw new FileNotFoundException("PaddleOCR 安装包不存在。", packagePath);
            using (new ComponentOperationLock())
            {
                var target = GetInstallDirectory();
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(parent)) throw new IOException("增强组件安装位置无效。 ");
                Directory.CreateDirectory(parent);
                var staging = Path.Combine(parent, "." + ComponentFolderName + ".stage-" + Guid.NewGuid().ToString("N"));
                var backup = Path.Combine(parent, "." + ComponentFolderName + ".previous-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ExtractPackage(packagePath, staging, progress, cancellationToken);
                    var componentRoot = FindComponentRoot(staging);
                    ValidateComponent(componentRoot, progress, cancellationToken);
                    if (!PathsEqual(componentRoot, staging))
                    {
                        var normalized = staging + ".component";
                        Directory.Move(componentRoot, normalized);
                        Directory.Delete(staging, true);
                        staging = normalized;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(target)) Directory.Move(target, backup);
                    Directory.Move(staging, target);
                    TryDeleteDirectory(backup);
                }
                catch
                {
                    TryDeleteDirectory(staging);
                    if (!Directory.Exists(target) && Directory.Exists(backup)) Directory.Move(backup, target);
                    throw;
                }
            }
        }

        public static void Uninstall()
        {
            using (new ComponentOperationLock())
            {
                var target = GetInstallDirectory();
                if (!Directory.Exists(target)) return;
                var manifest = Path.Combine(target, ManifestName);
                var executable = Path.Combine(target, ExecutableName);
                if (!File.Exists(manifest) || !File.Exists(executable))
                    throw new IOException("所选目录不是可识别的 PaddleOCR 组件目录，未执行删除。 ");
                Directory.Delete(target, true);
            }
        }

        public static void OpenInstallDirectory()
        {
            var directory = GetInstallDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
        }

        public static void OpenDownloadPage()
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }

        internal static void ValidateComponent(string componentRoot, IProgress<PaddleOcrComponentProgress> progress, CancellationToken cancellationToken)
        {
            var manifest = ReadManifest(Path.Combine(componentRoot, ManifestName));
            ValidateManifestIdentity(manifest);
            var root = Path.GetFullPath(componentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var files = manifest.Files ?? new List<PaddleOcrComponentFile>();
            if (files.Count == 0) throw new InvalidDataException("组件清单没有文件记录。 ");
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = files[index];
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) throw new InvalidDataException("组件清单包含无效文件。 ");
                var candidate = Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("组件清单路径越界：" + item.Path);
                if (!File.Exists(candidate) || new FileInfo(candidate).Length != item.Size || !string.Equals(Sha256(candidate), item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("组件文件校验失败：" + item.Path);
                progress?.Report(new PaddleOcrComponentProgress { Stage = "正在校验组件文件", Completed = index + 1, Total = files.Count });
            }
            if (!File.Exists(Path.Combine(componentRoot, ExecutableName))) throw new InvalidDataException("组件缺少入口程序。 ");
        }

        private static void ExtractPackage(string packagePath, string staging, IProgress<PaddleOcrComponentProgress> progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(staging);
            var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
                for (var index = 0; index < entries.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = entries[index];
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含越界路径，已拒绝安装。 ");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, false);
                    progress?.Report(new PaddleOcrComponentProgress { Stage = "正在解压组件", Completed = index + 1, Total = entries.Count });
                }
            }
        }

        private static string FindComponentRoot(string staging)
        {
            var manifests = Directory.GetFiles(staging, ManifestName, SearchOption.AllDirectories);
            if (manifests.Length != 1) throw new InvalidDataException("安装包必须包含且只能包含一份组件清单。 ");
            return Path.GetDirectoryName(manifests[0]);
        }

        private static PaddleOcrComponentManifest ReadManifest(string path)
        {
            if (!File.Exists(path)) throw new InvalidDataException("缺少组件清单。 ");
            using (var stream = File.OpenRead(path))
                return (PaddleOcrComponentManifest)new DataContractJsonSerializer(typeof(PaddleOcrComponentManifest)).ReadObject(stream);
        }

        private static void ValidateManifestIdentity(PaddleOcrComponentManifest manifest)
        {
            if (manifest == null || !string.Equals(manifest.Component, ComponentFolderName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("组件类型不正确。 ");
            if (manifest.ProtocolVersion != LineVisionOcrProtocol.CurrentVersion)
                throw new InvalidDataException("组件协议版本不兼容。 ");
        }

        private static void ValidateInstalledFiles(string componentRoot, PaddleOcrComponentManifest manifest)
        {
            var root = Path.GetFullPath(componentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var files = manifest.Files ?? new List<PaddleOcrComponentFile>();
            if (files.Count == 0) throw new InvalidDataException("组件清单没有文件记录。 ");
            var executableListed = false;
            foreach (var item in files)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path)) throw new InvalidDataException("组件清单包含无效文件。 ");
                var candidate = Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("组件清单路径越界：" + item.Path);
                if (!File.Exists(candidate) || new FileInfo(candidate).Length != item.Size) throw new InvalidDataException("组件文件缺失或大小不符：" + item.Path);
                if (string.Equals(item.Path.Replace('/', Path.DirectorySeparatorChar), ExecutableName, StringComparison.OrdinalIgnoreCase)) executableListed = true;
            }
            if (!executableListed) throw new InvalidDataException("组件清单没有登记入口程序。 ");
        }

        private static string FormatVersion(PaddleOcrComponentManifest manifest)
        {
            var version = string.IsNullOrWhiteSpace(manifest.PaddleOCR) ? "未知版本" : "PaddleOCR " + manifest.PaddleOCR;
            return string.IsNullOrWhiteSpace(manifest.Model) ? version : version + " / " + manifest.Model;
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

        private sealed class ComponentOperationLock : IDisposable
        {
            private readonly Mutex _mutex = new Mutex(false, "WanluoArchitectureTools.PaddleOcrComponent");
            private bool _acquired;

            public ComponentOperationLock()
            {
                try { _acquired = _mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { _acquired = true; }
                if (!_acquired) { _mutex.Dispose(); throw new IOException("另一份 AutoCAD 正在安装或卸载 PaddleOCR 组件，请稍后重试。 "); }
            }

            public void Dispose()
            {
                if (_acquired) try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
        }
    }
}
