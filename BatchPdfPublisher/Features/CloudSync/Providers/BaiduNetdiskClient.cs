using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    public sealed class BaiduNetdiskClient : IDisposable
    {
        public const string AuthorizationEndpoint = "https://openapi.baidu.com/oauth/2.0/authorize";
        public const string TokenEndpoint = "https://openapi.baidu.com/oauth/2.0/token";
        public const string DefaultRedirectUri = "https://openapi.baidu.com/oauth/2.0/login_success";
        public const string FileEndpoint = "https://pan.baidu.com/rest/2.0/xpan/file";
        public const string MultimediaEndpoint = "https://pan.baidu.com/rest/2.0/xpan/multimedia";
        public const string UploadEndpoint = "https://d.pcs.baidu.com/rest/2.0/pcs/superfile2";
        private const int BlockSize = 4 * 1024 * 1024;
        internal static bool IsPlainMd5(string value)
        {
            return value != null && value.Length == 32 && value.All(c =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
        }
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public BaiduNetdiskClient() : this(new HttpClient(), true) { }
        internal BaiduNetdiskClient(HttpClient http, bool ownsHttp = false)
        {
            _http = http ?? throw new ArgumentNullException("http");
            _ownsHttp = ownsHttp;
            _http.Timeout = TimeSpan.FromMinutes(20);
        }

        public static Uri BuildAuthorizationUri(string clientId, string redirectUri, string state)
        {
            Require(clientId, "百度 App Key"); Require(redirectUri, "百度回调地址");
            var url = AuthorizationEndpoint + "?response_type=code&client_id=" + E(clientId)
                + "&redirect_uri=" + E(redirectUri) + "&scope=" + E("basic,netdisk")
                + "&display=popup" + (string.IsNullOrWhiteSpace(state) ? string.Empty : "&state=" + E(state));
            return new Uri(url);
        }

        public static string ExtractAuthorizationCode(string callbackUrl, string expectedState)
        {
            Require(callbackUrl, "授权回调地址");
            if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var uri)) throw new InvalidOperationException("请粘贴浏览器地址栏中的完整回调地址。");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue; var index = part.IndexOf('=');
                var key = Uri.UnescapeDataString(index < 0 ? part : part.Substring(0, index));
                var value = Uri.UnescapeDataString(index < 0 ? string.Empty : part.Substring(index + 1).Replace("+", " "));
                values[key] = value;
            }
            if (values.TryGetValue("error_description", out var description) || values.TryGetValue("error", out description))
                throw new InvalidOperationException("百度授权未完成：" + description);
            if (!values.TryGetValue("state", out var state) || !string.Equals(state, expectedState, StringComparison.Ordinal))
                throw new InvalidOperationException("授权回调校验失败，请重新点击连接，不要使用上一次的回调地址。");
            if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("回调地址中没有授权码。");
            return code;
        }

        public async Task<CloudSyncCredential> ExchangeCodeAsync(string clientId, string clientSecret,
            string redirectUri, string code, CancellationToken cancellationToken)
        {
            Require(code, "授权码");
            return await RequestTokenAsync(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" }, { "code", code.Trim() },
                { "client_id", clientId }, { "client_secret", clientSecret }, { "redirect_uri", redirectUri }
            }, clientSecret, cancellationToken).ConfigureAwait(false);
        }

        public async Task<CloudSyncCredential> RefreshAsync(string clientId, CloudSyncCredential credential,
            CancellationToken cancellationToken)
        {
            if (credential == null) throw new ArgumentNullException("credential");
            Require(credential.RefreshToken, "Refresh Token"); Require(credential.ClientSecret, "百度 Secret Key");
            return await RequestTokenAsync(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" }, { "refresh_token", credential.RefreshToken },
                { "client_id", clientId }, { "client_secret", credential.ClientSecret }
            }, credential.ClientSecret, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IList<BaiduRemoteEntry>> ListRecursiveAsync(string accessToken, string remoteRoot,
            Action<CloudSyncProgress> progress, CancellationToken cancellationToken)
        {
            var root = NormalizeRemotePath(remoteRoot); var result = new List<BaiduRemoteEntry>();
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested(); var directory = pending.Dequeue(); var start = 0;
                while (true)
                {
                    var uri = FileEndpoint + "?method=list&access_token=" + E(accessToken) + "&dir=" + E(directory)
                        + "&start=" + start.ToString(CultureInfo.InvariantCulture) + "&limit=1000&order=name&desc=0&web=0&folder=0";
                    var response = await GetJsonAsync<BaiduListResponse>(uri, cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(response.ErrorCode, response.ErrorMessage);
                    var items = response.Items ?? new List<BaiduListItem>();
                    foreach (var item in items)
                    {
                        var entry = new BaiduRemoteEntry { Path = NormalizeRemotePath(item.Path), IsDirectory = item.IsDirectory != 0,
                            Size = item.Size, Md5 = item.Md5, FileSystemId = item.FileSystemId, ModifiedAtUnix = item.ServerModifiedAt > 0 ? item.ServerModifiedAt : item.LocalModifiedAt };
                        result.Add(entry); if (entry.IsDirectory) pending.Enqueue(entry.Path);
                    }
                    progress?.Invoke(new CloudSyncProgress { Stage = "正在读取百度网盘目录", LogicalPath = directory, Completed = result.Count, Total = 0 });
                    if (items.Count < 1000) break; start += items.Count;
                }
            }
            return result;
        }

        public async Task DownloadAsync(string accessToken, BaiduRemoteEntry entry, string targetPath,
            Action<long, long> progress, CancellationToken cancellationToken)
        {
            if (entry == null || entry.IsDirectory) throw new ArgumentException("下载对象必须是文件。", "entry");
            var metas = await GetJsonAsync<BaiduMetaResponse>(MultimediaEndpoint + "?method=filemetas&openapi=xpansdk&access_token=" + E(accessToken)
                + "&fsids=" + E("[" + entry.FileSystemId.ToString(CultureInfo.InvariantCulture) + "]") + "&dlink=1",
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(metas.ErrorCode, metas.ErrorMessage);
            var meta = metas.Items == null ? null : metas.Items.FirstOrDefault();
            if (meta == null || string.IsNullOrWhiteSpace(meta.DownloadLink))
                throw new IOException("百度网盘文件元信息没有返回下载地址（fs_id=" + entry.FileSystemId.ToString(CultureInfo.InvariantCulture) + "）。");
            var separator = meta.DownloadLink.Contains("?") ? "&" : "?";
            using (var request = new HttpRequestMessage(HttpMethod.Get, meta.DownloadLink + separator + "access_token=" + E(accessToken)))
            using (var response = await SendDownloadAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await EnsureHttpSuccess(response).ConfigureAwait(false);
                entry.DownloadMd5 = null;
                IEnumerable<string> checksumHeaders;
                if (response.Content.Headers.TryGetValues("Content-MD5", out checksumHeaders))
                {
                    var checksum = checksumHeaders.FirstOrDefault();
                    if (IsPlainMd5(checksum)) entry.DownloadMd5 = checksum;
                    else try
                    {
                        var bytes = Convert.FromBase64String(checksum ?? string.Empty);
                        if (bytes.Length == 16) entry.DownloadMd5 = string.Concat(bytes.Select(b => b.ToString("x2")));
                    }
                    catch (FormatException) { }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                        await CopyWithProgress(input, output, entry.Size, progress, cancellationToken).ConfigureAwait(false);
                    ReplaceFile(temporary, targetPath);
                    if (entry.ModifiedAtUnix > 0) try { File.SetLastWriteTimeUtc(targetPath, DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedAtUnix).UtcDateTime); } catch { }
                }
                finally { TryDelete(temporary); }
            }
        }

        private async Task<HttpResponseMessage> SendDownloadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "pan.baidu.com");
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        public async Task UploadAsync(string accessToken, string localPath, string remotePath,
            Action<long, long> progress, CancellationToken cancellationToken)
        {
            var path = NormalizeRemotePath(remotePath); var info = new FileInfo(localPath);
            if (!info.Exists) throw new FileNotFoundException("待上传文件不存在。", localPath);
            await EnsureDirectoryAsync(accessToken, Parent(path), cancellationToken).ConfigureAwait(false);
            var hashes = await ComputeBlocksAsync(localPath, progress, cancellationToken).ConfigureAwait(false);
            var blockList = JsonStringArray(hashes);
            var precreate = await PostFormJsonAsync<BaiduPrecreateResponse>(FileEndpoint + "?method=precreate&access_token=" + E(accessToken),
                new Dictionary<string, string> { { "path", path }, { "size", info.Length.ToString(CultureInfo.InvariantCulture) },
                    { "isdir", "0" }, { "autoinit", "1" }, { "rtype", "3" }, { "block_list", blockList } }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(precreate.ErrorCode, precreate.ErrorMessage);
            if (string.IsNullOrWhiteSpace(precreate.UploadId)) throw new IOException("百度网盘未返回分片上传 ID。");
            var required = precreate.RequiredBlocks == null || precreate.RequiredBlocks.Count == 0
                ? Enumerable.Range(0, hashes.Count).ToList() : precreate.RequiredBlocks;
            using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            {
                foreach (var index in required)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = (int)Math.Min(BlockSize, info.Length - (long)index * BlockSize);
                    var bytes = new byte[length]; stream.Position = (long)index * BlockSize;
                    await ReadExactly(stream, bytes, cancellationToken).ConfigureAwait(false);
                    using (var content = new MultipartFormDataContent())
                    {
                        content.Add(new ByteArrayContent(bytes), "file", "block");
                        var uri = UploadEndpoint + "?method=upload&type=tmpfile&access_token=" + E(accessToken)
                            + "&path=" + E(path) + "&uploadid=" + E(precreate.UploadId) + "&partseq=" + index.ToString(CultureInfo.InvariantCulture);
                        using (var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false))
                            await EnsureHttpSuccess(response).ConfigureAwait(false);
                    }
                    progress?.Invoke(Math.Min(info.Length, ((long)index + 1) * BlockSize), info.Length);
                }
            }
            var created = await PostFormJsonAsync<BaiduApiResponse>(FileEndpoint + "?method=create&access_token=" + E(accessToken),
                new Dictionary<string, string> { { "path", path }, { "size", info.Length.ToString(CultureInfo.InvariantCulture) },
                    { "isdir", "0" }, { "rtype", "3" }, { "uploadid", precreate.UploadId }, { "block_list", blockList } }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(created.ErrorCode, created.ErrorMessage);
        }

        public async Task DeleteAsync(string accessToken, string remotePath, CancellationToken cancellationToken)
        {
            var response = await PostFormJsonAsync<BaiduApiResponse>(FileEndpoint + "?method=filemanager&opera=delete&async=0&access_token=" + E(accessToken),
                new Dictionary<string, string> { { "filelist", JsonStringArray(new[] { NormalizeRemotePath(remotePath) }) } }, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response.ErrorCode, response.ErrorMessage);
        }

        public static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/apps/万落建筑工具";
            var segments = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(x => x == "." || x == "..")) throw new IOException("云端路径不能包含 . 或 ..。");
            return "/" + string.Join("/", segments);
        }

        internal async Task EnsureDirectoryAsync(string token, string path, CancellationToken cancellationToken)
        {
            var segments = NormalizeRemotePath(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var start = segments.Length >= 2 && string.Equals(segments[0], "apps", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            var current = start == 2 ? "/" + segments[0] + "/" + segments[1] : string.Empty;
            for (var index = start; index < segments.Length; index++)
            {
                current += "/" + segments[index];
                var response = await PostFormJsonAsync<BaiduApiResponse>(FileEndpoint + "?method=create&access_token=" + E(token),
                    new Dictionary<string, string> { { "path", current }, { "isdir", "1" }, { "size", "0" }, { "rtype", "0" } }, cancellationToken).ConfigureAwait(false);
                if (response.ErrorCode != 0 && response.ErrorCode != -8 && response.ErrorCode != 31061) EnsureSuccess(response.ErrorCode, response.ErrorMessage);
            }
        }

        private async Task<CloudSyncCredential> RequestTokenAsync(Dictionary<string, string> parameters, string secret, CancellationToken cancellationToken)
        {
            var uri = TokenEndpoint + "?" + string.Join("&", parameters.Select(x => E(x.Key) + "=" + E(x.Value)));
            using (var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false))
            {
                var value = await ReadJsonAsync<BaiduTokenResponse>(response, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || !string.IsNullOrWhiteSpace(value.Error))
                    throw new InvalidOperationException("百度授权失败：" + (value.ErrorDescription ?? value.Error ?? response.ReasonPhrase));
                return new CloudSyncCredential { ClientId = parameters["client_id"], AccessToken = value.AccessToken, RefreshToken = value.RefreshToken, ClientSecret = secret,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, value.ExpiresIn - 120)).ToString("o", CultureInfo.InvariantCulture) };
            }
        }

        private async Task<T> GetJsonAsync<T>(string uri, CancellationToken token)
        {
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                return await ReadJsonAsync<T>(response, token).ConfigureAwait(false);
        }
        private async Task<T> PostFormJsonAsync<T>(string uri, Dictionary<string, string> form, CancellationToken token)
        {
            using (var response = await _http.PostAsync(uri, new FormUrlEncodedContent(form), token).ConfigureAwait(false))
                return await ReadJsonAsync<T>(response, token).ConfigureAwait(false);
        }
        private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            try { using (var stream = new MemoryStream(bytes)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
            catch (Exception ex) { throw new IOException("云盘返回了无法识别的数据（HTTP " + (int)response.StatusCode + "）。", ex); }
        }
        private static async Task EnsureHttpSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new IOException("云盘请求失败（HTTP " + (int)response.StatusCode + "）：" + text);
        }
        private static async Task<List<string>> ComputeBlocksAsync(string path, Action<long, long> progress, CancellationToken token)
        {
            var result = new List<string>(); var info = new FileInfo(path); long completed = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            {
                var buffer = new byte[BlockSize]; int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    token.ThrowIfCancellationRequested(); using (var md5 = MD5.Create()) result.Add(ToHex(md5.ComputeHash(buffer, 0, read)));
                    completed += read; progress?.Invoke(completed, info.Length);
                }
            }
            if (result.Count == 0) using (var md5 = MD5.Create()) result.Add(ToHex(md5.ComputeHash(new byte[0])));
            return result;
        }
        private static async Task CopyWithProgress(Stream input, Stream output, long total, Action<long, long> progress, CancellationToken token)
        {
            var buffer = new byte[1024 * 1024]; long completed = 0; int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            { await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false); completed += read; progress?.Invoke(completed, total); }
        }
        private static async Task ReadExactly(Stream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0; while (offset < buffer.Length) { var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read; }
        }
        private static string JsonStringArray(IEnumerable<string> values) { return "[" + string.Join(",", values.Select(x => "\"" + x.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]"; }
        private static string Parent(string path) { var index = path.LastIndexOf('/'); return index <= 0 ? "/" : path.Substring(0, index); }
        private static string ToHex(byte[] bytes) { var builder = new StringBuilder(bytes.Length * 2); foreach (var b in bytes) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture)); return builder.ToString(); }
        private static string E(string value) { return Uri.EscapeDataString(value ?? string.Empty); }
        private static void Require(string value, string name) { if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(name + "不能为空。"); }
        private static void EnsureSuccess(int code, string message) { if (code != 0) throw new IOException("百度网盘接口错误 " + code + (string.IsNullOrWhiteSpace(message) ? string.Empty : "：" + message)); }
        private static void ReplaceFile(string temporary, string target) { if (File.Exists(target)) File.Replace(temporary, target, target + ".bak", true); else File.Move(temporary, target); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    }

    public sealed class BaiduRemoteEntry { public string Path { get; set; } public bool IsDirectory { get; set; } public long Size { get; set; } public string Md5 { get; set; } public string DownloadMd5 { get; set; } public long FileSystemId { get; set; } public long ModifiedAtUnix { get; set; } }

    [DataContract] internal class BaiduApiResponse { [DataMember(Name = "errno")] public int ErrorCode { get; set; } [DataMember(Name = "errmsg")] public string ErrorMessage { get; set; } }
    [DataContract] internal sealed class BaiduListResponse : BaiduApiResponse { [DataMember(Name = "list")] public List<BaiduListItem> Items { get; set; } }
    [DataContract] internal sealed class BaiduListItem { [DataMember(Name = "path")] public string Path { get; set; } [DataMember(Name = "isdir")] public int IsDirectory { get; set; } [DataMember(Name = "size")] public long Size { get; set; } [DataMember(Name = "md5")] public string Md5 { get; set; } [DataMember(Name = "fs_id")] public long FileSystemId { get; set; } [DataMember(Name = "server_mtime")] public long ServerModifiedAt { get; set; } [DataMember(Name = "local_mtime")] public long LocalModifiedAt { get; set; } }
    [DataContract] internal sealed class BaiduMetaResponse : BaiduApiResponse { [DataMember(Name = "list")] public List<BaiduMetaItem> Items { get; set; } }
    [DataContract] internal sealed class BaiduMetaItem { [DataMember(Name = "dlink")] public string DownloadLink { get; set; } }
    [DataContract] internal sealed class BaiduPrecreateResponse : BaiduApiResponse { [DataMember(Name = "uploadid")] public string UploadId { get; set; } [DataMember(Name = "block_list")] public List<int> RequiredBlocks { get; set; } }
    [DataContract] internal sealed class BaiduTokenResponse { [DataMember(Name = "access_token")] public string AccessToken { get; set; } [DataMember(Name = "refresh_token")] public string RefreshToken { get; set; } [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; } [DataMember(Name = "error")] public string Error { get; set; } [DataMember(Name = "error_description")] public string ErrorDescription { get; set; } }
}
