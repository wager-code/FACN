using System.Net;
using System.Security.Cryptography;
using System.Text;
using COSXML;
using COSXML.Auth;
using COSXML.Model.Tag;

namespace SCFA.PublicationGateway;

public sealed class CosTransport(GatewayOptions options)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

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
        using var request = new HttpRequestMessage(HttpMethod.Head, SignedUrl(credentials, "HEAD", key, 300));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
    public async Task<byte[]> GetBytesAsync(CosCredentials credentials, string key, int limit, CancellationToken ct)
    {
        using var response = await Http.GetAsync(SignedUrl(credentials, "GET", key, 300), HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("COS 对象不存在");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("COS 对象超过读取上限");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("COS 对象超过读取上限");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task DownloadToFileAsync(CosCredentials credentials, string key, string path, long limit, CancellationToken ct)
    {
        using var response = await Http.GetAsync(SignedUrl(credentials, "GET", key, 300), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("COS 包超过安全上限");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            total += count;
            if (total > limit) throw new InvalidDataException("COS 包超过安全上限");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    public async Task PutFileAsync(CosCredentials credentials, string key, string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        using var content = new StreamContent(file);
        using var request = new HttpRequestMessage(HttpMethod.Put, SignedUrl(credentials, "PUT", key, 3600)) { Content = content };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PutBytesAsync(CosCredentials credentials, string key, byte[] data, CancellationToken ct)
    {
        using var content = new ByteArrayContent(data);
        using var request = new HttpRequestMessage(HttpMethod.Put, SignedUrl(credentials, "PUT", key, 300)) { Content = content };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(CosCredentials credentials, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, SignedUrl(credentials, "DELETE", key, 300));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
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
}