using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    /// <summary>Desktop side of the Wanluo stateless OAuth broker protocol.</summary>
    public sealed class BaiduBrokerAuthClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public BaiduBrokerAuthClient() : this(new HttpClient(), true) { }
        internal BaiduBrokerAuthClient(HttpClient http, bool ownsHttp = false)
        {
            _http = http ?? throw new ArgumentNullException("http"); _ownsHttp = ownsHttp;
            _http.Timeout = TimeSpan.FromMinutes(3);
        }

        public async Task<CloudSyncCredential> AuthorizeAsync(string brokerBaseUrl, CancellationToken cancellationToken)
        {
            var broker = ValidateBrokerUrl(brokerBaseUrl);
            using (var session = BrokerSession.Create())
            {
                var request = new BrokerAuthorizeRequest { Port = session.Port, Nonce = session.Nonce, PublicKey = session.PublicKey };
                var response = await PostJsonAsync<BrokerAuthorizeRequest, BrokerAuthorizeResponse>(new Uri(broker, "/v1/baidu/authorize"), request, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.AuthorizeUrl)) throw new IOException("授权服务没有返回百度登录地址。");
                Process.Start(new ProcessStartInfo(response.AuthorizeUrl) { UseShellExecute = true });
                var callback = await session.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(callback.Nonce, session.Nonce)) throw new CryptographicException("本机 OAuth 回调校验失败。");
                return DecryptCredential(callback.Payload, session.PrivateKey);
            }
        }

        public async Task<CloudSyncCredential> RefreshAsync(string brokerBaseUrl, string refreshToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidOperationException("Refresh Token 不能为空。");
            var broker = ValidateBrokerUrl(brokerBaseUrl);
            using (var session = BrokerSession.Create())
            {
                var request = new BrokerRefreshRequest { Port = session.Port, Nonce = session.Nonce, PublicKey = session.PublicKey, RefreshToken = refreshToken };
                var response = await PostJsonAsync<BrokerRefreshRequest, BrokerRefreshResponse>(new Uri(broker, "/v1/baidu/refresh"), request, cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(response.Nonce, session.Nonce)) throw new CryptographicException("刷新令牌响应校验失败。");
                return DecryptCredential(response.Payload, session.PrivateKey);
            }
        }

        internal static CloudSyncCredential DecryptCredential(string encodedEnvelope, RSA privateKey)
        {
            if (string.IsNullOrWhiteSpace(encodedEnvelope)) throw new CryptographicException("授权响应没有加密内容。");
            BrokerTokenEnvelope envelope;
            using (var stream = new MemoryStream(Base64UrlDecode(encodedEnvelope)))
                envelope = (BrokerTokenEnvelope)new DataContractJsonSerializer(typeof(BrokerTokenEnvelope)).ReadObject(stream);
            if (envelope == null || envelope.Version != 1 || envelope.Algorithm != "RSA-OAEP-256+A256CBC-HS256")
                throw new CryptographicException("授权响应使用了不支持的加密协议。");
            var keyMaterial = privateKey.Decrypt(Base64UrlDecode(envelope.WrappedKey), RSAEncryptionPadding.OaepSHA256);
            try
            {
                if (keyMaterial.Length != 64) throw new CryptographicException("授权响应密钥长度无效。");
                var iv = Base64UrlDecode(envelope.Iv); var ciphertext = Base64UrlDecode(envelope.Ciphertext); var expectedMac = Base64UrlDecode(envelope.Mac);
                if (iv.Length != 16 || expectedMac.Length != 32) throw new CryptographicException("授权响应加密参数无效。");
                var authenticated = new byte[iv.Length + ciphertext.Length]; Buffer.BlockCopy(iv, 0, authenticated, 0, iv.Length); Buffer.BlockCopy(ciphertext, 0, authenticated, iv.Length, ciphertext.Length);
                byte[] actualMac;
                using (var hmac = new HMACSHA256(Slice(keyMaterial, 32, 32))) actualMac = hmac.ComputeHash(authenticated);
                if (!FixedEquals(actualMac, expectedMac)) throw new CryptographicException("授权响应完整性校验失败。");
                byte[] plaintext;
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Slice(keyMaterial, 0, 32); aes.IV = iv;
                    using (var decryptor = aes.CreateDecryptor()) plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                }
                try
                {
                    BrokerTokenPayload payload;
                    using (var stream = new MemoryStream(plaintext)) payload = (BrokerTokenPayload)new DataContractJsonSerializer(typeof(BrokerTokenPayload)).ReadObject(stream);
                    if (payload == null || string.IsNullOrWhiteSpace(payload.AccessToken) || string.IsNullOrWhiteSpace(payload.RefreshToken))
                        throw new CryptographicException("授权响应缺少令牌。");
                    return new CloudSyncCredential { AuthMode = "Broker", AccessToken = payload.AccessToken, RefreshToken = payload.RefreshToken,
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, payload.ExpiresIn - 120)).ToString("o", CultureInfo.InvariantCulture) };
                }
                finally { Array.Clear(plaintext, 0, plaintext.Length); }
            }
            finally { Array.Clear(keyMaterial, 0, keyMaterial.Length); }
        }

        private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(Uri uri, TRequest value, CancellationToken cancellationToken)
        {
            byte[] body;
            using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(TRequest)).WriteObject(stream, value); body = stream.ToArray(); }
            using (var content = new ByteArrayContent(body))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using (var response = await _http.PostAsync(uri, content, cancellationToken).ConfigureAwait(false))
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new IOException("万落授权服务请求失败（HTTP " + (int)response.StatusCode + "）。");
                    using (var stream = new MemoryStream(bytes)) return (TResponse)new DataContractJsonSerializer(typeof(TResponse)).ReadObject(stream);
                }
            }
        }

        private static Uri ValidateBrokerUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("万落授权服务地址必须是无参数的 HTTPS 地址。");
            return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
        }

        internal static string Base64UrlEncode(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        internal static byte[] Base64UrlDecode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Base64URL 内容为空。");
            var normalized = value.Replace('-', '+').Replace('_', '/'); normalized += new string('=', (4 - normalized.Length % 4) % 4);
            return Convert.FromBase64String(normalized);
        }
        private static byte[] Slice(byte[] value, int offset, int count) { var result = new byte[count]; Buffer.BlockCopy(value, offset, result, 0, count); return result; }
        private static bool FixedEquals(string left, string right) { return FixedEquals(Encoding.UTF8.GetBytes(left ?? string.Empty), Encoding.UTF8.GetBytes(right ?? string.Empty)); }
        private static bool FixedEquals(byte[] left, byte[] right) { if (left.Length == 0 || right.Length == 0) return left.Length == right.Length; var difference = left.Length ^ right.Length; var count = Math.Max(left.Length, right.Length); for (var i = 0; i < count; i++) difference |= left[i % left.Length] ^ right[i % right.Length]; return difference == 0; }
        public void Dispose() { if (_ownsHttp) _http.Dispose(); }

        private sealed class BrokerSession : IDisposable
        {
            private readonly TcpListener _listener;
            public RSA PrivateKey { get; private set; }
            public int Port { get; private set; }
            public string Nonce { get; private set; }
            public BrokerPublicKey PublicKey { get; private set; }

            public static BrokerSession Create()
            {
                var rsa = new RSACng(2048); var parameters = rsa.ExportParameters(false);
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
                return new BrokerSession(listener) { PrivateKey = rsa, Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                    Nonce = Base64UrlEncode(Random(24)), PublicKey = new BrokerPublicKey { KeyType = "RSA", Algorithm = "RSA-OAEP-256", Use = "enc",
                        Modulus = Base64UrlEncode(parameters.Modulus), Exponent = Base64UrlEncode(parameters.Exponent) } };
            }

            private BrokerSession(TcpListener listener) { _listener = listener; }

            public async Task<BrokerLocalCallback> WaitAsync(CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(delegate { try { _listener.Stop(); } catch { } }))
                using (var client = await AcceptAsync(cancellationToken).ConfigureAwait(false))
                using (var stream = client.GetStream())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestLine = await ReadLineAsync(stream, 16384, cancellationToken).ConfigureAwait(false);
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2 || parts[0] != "GET") throw new IOException("本机 OAuth 回调格式无效。");
                    var uri = new Uri("http://127.0.0.1" + parts[1]); var values = ParseQuery(uri.Query);
                    if (!values.TryGetValue("nonce", out var nonce) || !values.TryGetValue("payload", out var payload)) throw new IOException("本机 OAuth 回调缺少参数。");
                    var html = "<!doctype html><meta charset=\"utf-8\"><title>万落建筑工具</title><h2>授权数据已返回 AutoCAD</h2><p>请返回 AutoCAD，最终结果以插件显示的“已登录”为准；随后可以关闭此页面。</p>";
                    var bytes = Encoding.UTF8.GetBytes(html);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                    return new BrokerLocalCallback { Nonce = nonce, Payload = payload };
                }
            }

            public void Dispose() { try { _listener.Stop(); } catch { } PrivateKey?.Dispose(); }
            private async Task<TcpClient> AcceptAsync(CancellationToken token)
            {
                try { return await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                catch (SocketException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            }
            private static byte[] Random(int count) { var value = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value; }
            private static async Task<string> ReadLineAsync(Stream stream, int maximum, CancellationToken token)
            {
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < maximum)
                {
                    var read = await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false); if (read == 0) break;
                    if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r'); bytes.Add(one[0]);
                }
                throw new IOException("本机 OAuth 回调请求过长。");
            }
            private static Dictionary<string, string> ParseQuery(string query)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in query.TrimStart('?').Split('&')) { var index = part.IndexOf('='); if (index > 0) result[Uri.UnescapeDataString(part.Substring(0, index))] = Uri.UnescapeDataString(part.Substring(index + 1).Replace("+", " ")); }
                return result;
            }
        }
    }

    [DataContract] internal sealed class BrokerAuthorizeRequest { [DataMember(Name = "port")] public int Port { get; set; } [DataMember(Name = "nonce")] public string Nonce { get; set; } [DataMember(Name = "public_key")] public BrokerPublicKey PublicKey { get; set; } }
    [DataContract] internal sealed class BrokerRefreshRequest : BrokerAuthorizeRequestBase { [DataMember(Name = "refresh_token")] public string RefreshToken { get; set; } }
    [DataContract] internal class BrokerAuthorizeRequestBase { [DataMember(Name = "port")] public int Port { get; set; } [DataMember(Name = "nonce")] public string Nonce { get; set; } [DataMember(Name = "public_key")] public BrokerPublicKey PublicKey { get; set; } }
    [DataContract] internal sealed class BrokerPublicKey { [DataMember(Name = "kty")] public string KeyType { get; set; } [DataMember(Name = "alg")] public string Algorithm { get; set; } [DataMember(Name = "use")] public string Use { get; set; } [DataMember(Name = "n")] public string Modulus { get; set; } [DataMember(Name = "e")] public string Exponent { get; set; } }
    [DataContract] internal sealed class BrokerAuthorizeResponse { [DataMember(Name = "authorize_url")] public string AuthorizeUrl { get; set; } [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; } }
    [DataContract] internal sealed class BrokerRefreshResponse { [DataMember(Name = "nonce")] public string Nonce { get; set; } [DataMember(Name = "payload")] public string Payload { get; set; } }
    [DataContract] internal sealed class BrokerTokenEnvelope { [DataMember(Name = "v")] public int Version { get; set; } [DataMember(Name = "alg")] public string Algorithm { get; set; } [DataMember(Name = "key")] public string WrappedKey { get; set; } [DataMember(Name = "iv")] public string Iv { get; set; } [DataMember(Name = "ciphertext")] public string Ciphertext { get; set; } [DataMember(Name = "mac")] public string Mac { get; set; } }
    [DataContract] internal sealed class BrokerTokenPayload { [DataMember(Name = "access_token")] public string AccessToken { get; set; } [DataMember(Name = "refresh_token")] public string RefreshToken { get; set; } [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; } }
    internal sealed class BrokerLocalCallback { public string Nonce { get; set; } public string Payload { get; set; } }
}
