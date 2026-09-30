using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using COSXML;
using COSXML.Auth;
using COSXML.Model.Tag;

namespace SCFA.PublicationGateway;

public sealed class CosTransport(GatewayOptions options)
{
    // Per-operation linked deadlines cover response headers, error bodies, and streamed content.
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly HttpClient _http = SharedHttp;
    private readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(45);
    private readonly TimeSpan _smallObjectTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _largeTransferTimeout = TimeSpan.FromHours(12);

    internal CosTransport(GatewayOptions options, HttpClient http, TimeSpan metadataTimeout,
        TimeSpan smallObjectTimeout, TimeSpan largeTransferTimeout) : this(options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _metadataTimeout = metadataTimeout;
        _smallObjectTimeout = smallObjectTimeout;
        _largeTransferTimeout = largeTransferTimeout;
    }

    public string SignedUrl(CosCredentials credentials, string method, string key, long seconds)
    {
        var config = new CosXmlConfig.Builder().IsHttps(true).SetRegion(options.Region).Build();
        var provider = new DefaultQCloudCredentialProvider(credentials.SecretId, credentials.SecretKey, seconds);
        var server = new CosXmlServer(config, provider);
        var request = new PreSignatureStruct
        {
            appid = options.Bucket[(options.Bucket.LastIndexOf('-') + 1)..],
            region = options.Region,
            bucket = options.Bucket,
            key = key,
            httpMethod = method,
            isHttps = true,
            signHost = true,
            signDurationSecond = seconds
        };
        var raw = server.GenerateSignURL(request);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals($"{options.Bucket}.cos.{options.Region}.myqcloud.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("COS SDK 生成的签名地址与目标桶不符");
        return raw;
    }

    public async Task<bool> ExistsAsync(CosCredentials credentials, string key, CancellationToken ct)
    {
        return await WithDeadlineAsync("HEAD", _metadataTimeout, ct, async operationCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, SignedUrl(credentials, "HEAD", key, 300));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationCt);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            await EnsureCosSuccessAsync(response, "HEAD", operationCt);
            return true;
        });
    }
    public async Task<byte[]> GetBytesAsync(CosCredentials credentials, string key, int limit, CancellationToken ct)
    {
        return await WithDeadlineAsync("GET", _smallObjectTimeout, ct, async operationCt =>
        {
            using var response = await _http.GetAsync(SignedUrl(credentials, "GET", key, 300), HttpCompletionOption.ResponseHeadersRead, operationCt);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("COS 对象不存在");
            await EnsureCosSuccessAsync(response, "GET", operationCt);
            if (response.Content.Headers.ContentLength is long length && length > limit)
                throw new InvalidDataException("COS 对象超过读取上限");
            await using var input = await response.Content.ReadAsStreamAsync(operationCt);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer, operationCt);
                if (count == 0) break;
                if (output.Length + count > limit) throw new InvalidDataException("COS 对象超过读取上限");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        });
    }

    public async Task DownloadToFileAsync(CosCredentials credentials, string key, string path, long limit, CancellationToken ct)
    {
        await WithDeadlineAsync("GET", _largeTransferTimeout, ct, async operationCt =>
        {
            using var response = await _http.GetAsync(SignedUrl(credentials, "GET", key, 300), HttpCompletionOption.ResponseHeadersRead, operationCt);
            await EnsureCosSuccessAsync(response, "GET", operationCt);
            if (response.Content.Headers.ContentLength is long length && length > limit)
                throw new InvalidDataException("COS 包超过安全上限");
            await using var input = await response.Content.ReadAsStreamAsync(operationCt);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, operationCt);
                if (count == 0) break;
                total += count;
                if (total > limit) throw new InvalidDataException("COS 包超过安全上限");
                await output.WriteAsync(buffer.AsMemory(0, count), operationCt);
            }
        });
    }

    public async Task VerifyObjectHashAsync(CosCredentials credentials, string key, long expectedSize, string expectedSha256, CancellationToken ct)
    {
        await WithDeadlineAsync("GET", _largeTransferTimeout, ct, async operationCt =>
        {
            if (expectedSize is <= 0 or > 4L * 1024 * 1024 * 1024 ||
                !Regex.IsMatch(expectedSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("待恢复 ZIP 的大小或哈希无效");
            using var response = await _http.GetAsync(SignedUrl(credentials, "GET", key, 3600), HttpCompletionOption.ResponseHeadersRead, operationCt);
            await EnsureCosSuccessAsync(response, "GET", operationCt);
            if (response.Content.Headers.ContentLength is long declared && declared != expectedSize)
                throw new InvalidDataException("待恢复 ZIP 的云端大小与档案不符");
            await using var input = await response.Content.ReadAsStreamAsync(operationCt);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, operationCt);
                if (count == 0) break;
                total += count;
                if (total > expectedSize) throw new InvalidDataException("待恢复 ZIP 超过档案大小");
                digest.AppendData(buffer, 0, count);
            }
            if (total != expectedSize ||
                !Convert.ToHexString(digest.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("待恢复 ZIP 内容与档案校验不符");
        });
    }

    public async Task PutFileAsync(CosCredentials credentials, string key, string path, CancellationToken ct)
    {
        await WithDeadlineAsync("PUT", _largeTransferTimeout, ct, async operationCt =>
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            using var content = new StreamContent(file);
            using var request = new HttpRequestMessage(HttpMethod.Put, SignedUrl(credentials, "PUT", key, 3600)) { Content = content };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationCt);
            await EnsureCosSuccessAsync(response, "PUT", operationCt);
        });
    }

    public async Task PutBytesAsync(CosCredentials credentials, string key, byte[] data, CancellationToken ct)
    {
        await WithDeadlineAsync("PUT", _smallObjectTimeout, ct, async operationCt =>
        {
            using var content = new ByteArrayContent(data);
            using var request = new HttpRequestMessage(HttpMethod.Put, SignedUrl(credentials, "PUT", key, 300)) { Content = content };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationCt);
            await EnsureCosSuccessAsync(response, "PUT", operationCt);
        });
    }

    public async Task DeleteAsync(CosCredentials credentials, string key, CancellationToken ct)
    {
        await WithDeadlineAsync("DELETE", _metadataTimeout, ct, async operationCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, SignedUrl(credentials, "DELETE", key, 300));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationCt);
            if (response.StatusCode != HttpStatusCode.NotFound) await EnsureCosSuccessAsync(response, "DELETE", operationCt);
        });
    }

    public async Task VerifyCredentialAsync(CosCredentials candidate, CancellationToken ct)
    {
        var key = options.Root + "/publication-staging/credential-check/" + Guid.NewGuid().ToString("N") + ".txt";
        var payload = RandomNumberGenerator.GetBytes(32);
        await PutBytesAsync(candidate, key, payload, ct);
        try
        {
            var got = await GetBytesAsync(candidate, key, 1024, ct);
            if (!CryptographicOperations.FixedTimeEquals(got, payload))
                throw new InvalidDataException("COS 新密钥写入与读取校验失败");
        }
        finally { await DeleteAsync(candidate, key, ct); }
    }

    public static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string Sha256(string data) => Sha256(Encoding.UTF8.GetBytes(data));

    private Task WithDeadlineAsync(string operation, TimeSpan timeout, CancellationToken ct,
        Func<CancellationToken, Task> work) =>
        WithDeadlineAsync(operation, timeout, ct, async token =>
        {
            await work(token);
            return true;
        });

    private static async Task<T> WithDeadlineAsync<T>(string operation, TimeSpan timeout, CancellationToken ct,
        Func<CancellationToken, Task<T>> work)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { return await work(deadline.Token); }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CosTimeoutException(operation, timeout, ex);
        }
    }
    private static async Task EnsureCosSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await CosRequestException.FromResponseAsync(response, operation, ct);
    }
}

public sealed class CosTimeoutException(string operation, TimeSpan timeout, Exception innerException)
    : TimeoutException($"COS {operation} 请求超时（{timeout.TotalSeconds:0.#} 秒）", innerException)
{
    public string Operation { get; } = operation;
    public TimeSpan Timeout { get; } = timeout;
}
public sealed class CosRequestException(string operation, HttpStatusCode statusCode, string? cosCode)
    : HttpRequestException($"COS {operation} HTTP {(int)statusCode}" + (cosCode is null ? "" : $" ({cosCode})"), null, statusCode)
{
    public string Operation { get; } = operation;
    public string? CosCode { get; } = cosCode;

    public static async Task<CosRequestException> FromResponseAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        // COS error XML can include object paths and request details. Only a short, validated Code reaches logs or clients.
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[4096];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        var body = Encoding.UTF8.GetString(bytes.AsSpan(0, count));
        var match = Regex.Match(body, @"<Code>\s*([A-Za-z][A-Za-z0-9]{0,63})\s*</Code>", RegexOptions.CultureInvariant);
        return new CosRequestException(operation, response.StatusCode, match.Success ? match.Groups[1].Value : null);
    }
}
