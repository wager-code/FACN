using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class AuthApiClient : IDisposable
{
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(12);
    public static string ClientVersion => AppVersion.Informational;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private HttpClient _http = null!;
    public string BaseUrl { get; private set; } = "";
    public string PinnedCertSha256 { get; private set; } = "";
    public string Token { get; private set; } = "";

    public AuthApiClient(string baseUrl, string fingerprint) => Reconfigure(baseUrl, fingerprint);

    public void Reconfigure(string baseUrl, string fingerprint)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var normalizedFingerprint = NormalizeFingerprint(fingerprint, allowEmpty: true);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrWhiteSpace(normalizedFingerprint))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => ValidatePinnedCertificate(cert, normalizedFingerprint);
        }
        // 普通 JSON API 使用每次请求的短超时；投稿上传依靠任务取消，不受 12 秒全局超时限制。
        var replacement = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var previous = _http;
        BaseUrl = normalizedBaseUrl;
        PinnedCertSha256 = normalizedFingerprint;
        _http = replacement;
        previous?.Dispose();
    }

    public void SetToken(string token) => Token = token?.Trim() ?? "";

    public Task<HealthResponse> HealthAsync(CancellationToken ct = default) => SendAsync<HealthResponse>(HttpMethod.Get, "/healthz", null, ct);
    public async Task<LoginResponse> LoginAsync(string account, string password, CancellationToken ct = default)
    {
        var result = await SendAsync<LoginResponse>(HttpMethod.Post, "/v1/auth/login", new LoginRequest(account.Trim(), password), ct);
        Token = result.Token;
        return result;
    }
    public Task<RegisterResponse> RegisterAsync(string username, string email, string password, CancellationToken ct = default) =>
        SendAsync<RegisterResponse>(HttpMethod.Post, "/v1/auth/register", new RegisterRequest(username.Trim(), email.Trim(), password), ct);
    public Task<MeResponse> MeAsync(CancellationToken ct = default) => SendAsync<MeResponse>(HttpMethod.Get, "/v1/auth/me", null, ct);
    public Task<T> GetJsonAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Get, path, null, ct);
    public Task<T> PostJsonAsync<T>(string path, object input, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Post, path, input, ct);
    public Task<T> PatchJsonAsync<T>(string path, object input, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Patch, path, input, ct);
    public Task<T> DeleteJsonAsync<T>(string path, CancellationToken ct = default) => SendAsync<T>(HttpMethod.Delete, path, null, ct);
    public Task<T> SendContentAsync<T>(HttpMethod method, string path, HttpContent content, CancellationToken ct = default) => SendRequestAsync<T>(method, path, content, timeout: null, ct);
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await SendAsync<object>(HttpMethod.Post, "/v1/auth/logout", null, ct);
        Token = "";
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? input, CancellationToken ct)
    {
        HttpContent? content = input is null ? null : new StringContent(JsonSerializer.Serialize(input, _json), Encoding.UTF8, "application/json");
        return await SendRequestAsync<T>(method, path, content, ApiRequestTimeout, ct);
    }

    private async Task<T> SendRequestAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan? timeout, CancellationToken ct)
    {
        ValidateApiPath(path);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } requestTimeout) requestCts.CancelAfter(requestTimeout);
        var requestCt = requestCts.Token;
        using var request = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SCFA-Content-Hub/" + ClientVersion);
        request.Headers.TryAddWithoutValidation("X-SCFA-Client-Version", ClientVersion);
        if (!string.IsNullOrWhiteSpace(Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (content is not null) request.Content = content;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCt);
        var data = await ReadLimitedStringAsync(response.Content, 4 * 1024 * 1024, requestCt);
        if (!response.IsSuccessStatusCode)
        {
            ApiErrorResponse? er = null;
            try { er = JsonSerializer.Deserialize<ApiErrorResponse>(data, _json); } catch { }
            var message = !string.IsNullOrWhiteSpace(er?.Message) ? er!.Message : !string.IsNullOrWhiteSpace(er?.Error) ? er!.Error : $"账号 API HTTP {(int)response.StatusCode}";
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        if (typeof(T) == typeof(object)) return (T)(object)new object();
        if (string.IsNullOrWhiteSpace(data))
        {
            if (typeof(T) == typeof(JsonElement))
            {
                using var empty = JsonDocument.Parse("{}");
                return (T)(object)empty.RootElement.Clone();
            }
            return default!;
        }
        return JsonSerializer.Deserialize<T>(data, _json) ?? throw new InvalidDataException("账号 API 返回了无效 JSON");
    }

    private static void ValidateApiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//") || Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new ArgumentException("账号 API 路径必须是以单个 / 开头的相对路径", nameof(path));
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 4 * 1024 * 1024) throw new InvalidDataException("账号 API 响应超过 4MB 安全上限");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > maxBytes) throw new InvalidDataException("账号 API 响应超过 4MB 安全上限");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static string NormalizeBaseUrl(string raw)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri)) throw new ArgumentException("API 地址格式无效");
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            var loopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
            if (!loopback && Environment.GetEnvironmentVariable("SCFA_ALLOW_INSECURE_API") != "1") throw new ArgumentException("非本机账号 API 必须使用 HTTPS");
        }
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static void ValidateConfiguration(string baseUrl, string fingerprint)
    {
        _ = NormalizeBaseUrl(baseUrl);
        _ = NormalizeFingerprint(fingerprint, allowEmpty: true);
    }

    private static string NormalizeFingerprint(string? raw, bool allowEmpty)
    {
        var value = (raw ?? "").Trim().ToLowerInvariant().Replace(":", "").Replace("-", "").Replace(" ", "");
        if (allowEmpty && value.Length == 0) return "";
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("TLS 证书 SHA-256 指纹格式无效");
        return value;
    }

    private static bool ValidatePinnedCertificate(X509Certificate2? cert, string expected)
    {
        if (cert is null) return false;
        var got = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(got), Encoding.ASCII.GetBytes(expected));
    }

    public void Dispose() => _http?.Dispose();
}
