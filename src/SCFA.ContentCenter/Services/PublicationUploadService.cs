using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class PublicationCapability
{
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonIgnore] public bool ApiAvailable { get; set; } = true;
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class CosCredentialStatus
{
    [JsonPropertyName("configured")] public bool Configured { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
}
public sealed class PublicationIntent
{
    [JsonPropertyName("publication_id")] public string PublicationId { get; set; } = "";
    [JsonPropertyName("package_upload_key")] public string PackageUploadKey { get; set; } = "";
    [JsonPropertyName("package_upload_url")] public string PackageUploadUrl { get; set; } = "";
    [JsonPropertyName("thumbnail_upload_key")] public string? ThumbnailUploadKey { get; set; }
    [JsonPropertyName("thumbnail_upload_url")] public string? ThumbnailUploadUrl { get; set; }
}

public sealed class PublicationCommit
{
    [JsonPropertyName("manifest_sha256")] public string ManifestSha256 { get; set; } = "";
}

/// <summary>
/// Uploads checked materials only after an authenticated server issues object-scoped upload URLs.
/// The server owns COS credentials, validates uploaded bytes and atomically commits the manifest.
/// </summary>
public sealed class PublicationUploadService(AuthApiClient auth, CloudCatalogService cloud, ConfigService config)
{
    private static readonly HttpClient UploadHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<PublicationCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        try
        {
            using var publication = CreatePublicationClient();
            return await publication.GetJsonAsync<PublicationCapability>("/v1/admin/publications/capabilities", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return new PublicationCapability { ApiAvailable = false, Message = "管理员发布服务尚未就绪" };
        }
    }

    public async Task<CosCredentialStatus> GetCredentialStatusAsync(CancellationToken ct = default)
    {
        using var publication = CreatePublicationClient();
        return await publication.GetJsonAsync<CosCredentialStatus>("/v1/admin/cos/credentials/status", ct);
    }

    public async Task<CosCredentialStatus> RotateCredentialAsync(string secretId, string secretKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("请填写 COS SecretId 和 SecretKey");
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(TimeSpan.FromSeconds(90));
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            secret_id = secretId.Trim(),
            secret_key = secretKey.Trim()
        }), Encoding.UTF8, "application/json");
        using var publication = CreatePublicationClient();
        return await publication.SendContentAsync<CosCredentialStatus>(HttpMethod.Post,
            "/v1/admin/cos/credentials/rotate", body, requestCts.Token);
    }
    public async Task PublishAsync(PublicationBundle bundle, string kind, UserInfo user, CancellationToken ct = default)
    {
        if (!AccessPolicy.CanPublishContent(user) || string.IsNullOrWhiteSpace(auth.Token))
            throw new UnauthorizedAccessException("请先使用管理员账号登录");
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型", nameof(kind));
        if (!File.Exists(bundle.PackagePath) || !File.Exists(bundle.ManifestPath) || !File.Exists(bundle.OriginalManifestPath))
            throw new FileNotFoundException("待发布材料缺失，请重新生成");
        if (!string.Equals(await ContentHash.FileSha256Async(bundle.PackagePath, ct), bundle.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 文件在打包后已改变，请重新生成");
        if (new FileInfo(bundle.PackagePath).Length != bundle.PackageSize)
            throw new InvalidDataException("ZIP 文件大小在打包后已改变");
        var oldManifest = await File.ReadAllTextAsync(bundle.OriginalManifestPath, ct);
        if (Sha256(oldManifest) != bundle.OriginalManifestSha256)
            throw new InvalidDataException("原始清单在打包后已改变");
        if (Sha256(await cloud.FetchManifestTextAsync(kind, ct)) != bundle.OriginalManifestSha256)
            throw new InvalidDataException("云端清单已由其他管理员修改，请重新生成材料");
        var newManifest = await File.ReadAllTextAsync(bundle.ManifestPath, ct);
        var manifestSha256 = Sha256(newManifest);
        string? thumbnailSha256 = null;
        long? thumbnailSize = null;
        if (bundle.ThumbnailKey is not null)
        {
            if (bundle.ThumbnailPath is null || !File.Exists(bundle.ThumbnailPath))
                throw new FileNotFoundException("缩略图文件缺失，请重新生成");
            thumbnailSha256 = await ContentHash.FileSha256Async(bundle.ThumbnailPath, ct);
            thumbnailSize = new FileInfo(bundle.ThumbnailPath).Length;
        }

        using var publication = CreatePublicationClient();
        var intent = await publication.PostJsonAsync<PublicationIntent>("/v1/admin/publications/intents", new
        {
            kind = kind == "地图" ? "map" : "mod",
            package_key = bundle.PackageKey,
            package_sha256 = bundle.PackageSha256,
            package_size = bundle.PackageSize,
            content_sha256 = bundle.ContentSha256,
            manifest_key = bundle.ManifestKey,
            original_manifest_sha256 = bundle.OriginalManifestSha256,
            next_manifest = newManifest,
            next_manifest_sha256 = manifestSha256,
            thumbnail_key = bundle.ThumbnailKey,
            thumbnail_sha256 = thumbnailSha256,
            thumbnail_size = thumbnailSize
        }, ct);
        if (!Guid.TryParse(intent.PublicationId, out _))
            throw new InvalidDataException("发布服务没有返回有效任务编号");
        ValidateStagingKey(intent.PackageUploadKey, intent.PublicationId, ".zip", config.Current);
        await PutFileAsync(intent.PackageUploadUrl, intent.PackageUploadKey, bundle.PackagePath, ct);
        if (bundle.ThumbnailKey is not null)
        {
            if (string.IsNullOrWhiteSpace(intent.ThumbnailUploadUrl) || string.IsNullOrWhiteSpace(intent.ThumbnailUploadKey))
                throw new InvalidDataException("发布服务没有返回缩略图暂存地址");
            ValidateStagingKey(intent.ThumbnailUploadKey, intent.PublicationId, ".png", config.Current);
            await PutFileAsync(intent.ThumbnailUploadUrl, intent.ThumbnailUploadKey, bundle.ThumbnailPath!, ct);
        }
        using var commitBody = new StringContent(JsonSerializer.Serialize(new
        {
            package_sha256 = bundle.PackageSha256,
            next_manifest_sha256 = manifestSha256
        }), Encoding.UTF8, "application/json");
        var commit = await publication.SendContentAsync<PublicationCommit>(HttpMethod.Post,
            "/v1/admin/publications/" + Uri.EscapeDataString(intent.PublicationId) + "/commit", commitBody, ct);
        if (!string.Equals(commit.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("服务端发布结果与待发布清单不一致，请检查云端状态");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Sha256(await cloud.FetchManifestTextAsync(kind, ct)) == manifestSha256) return;
            }
            catch (HttpRequestException) when (attempt < 2) { }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new InvalidDataException("服务端已提交，但公开清单尚未验证通过；请检查云端状态，勿重复发布");
    }

    private AuthApiClient CreatePublicationClient()
    {
        var account = new Uri(auth.BaseUrl, UriKind.Absolute);
        if (!Uri.TryCreate(config.Current.PublicationApiBaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !endpoint.Host.Equals(account.Host, StringComparison.OrdinalIgnoreCase) ||
            endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidDataException("管理员发布地址必须使用账号服务器的 HTTPS 主机");
        var client = new AuthApiClient(endpoint.GetLeftPart(UriPartial.Authority), auth.PinnedCertSha256);
        client.SetToken(auth.Token);
        return client;
    }
    private async Task PutFileAsync(string rawUrl, string key, string path, CancellationToken ct)
    {
        var url = ValidateUploadUrl(rawUrl, key, config.Current);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        using var content = new StreamContent(file);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        using var response = await UploadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("COS 上传失败，HTTP " + (int)response.StatusCode, null, response.StatusCode);
    }

    public static void ValidateStagingKey(string key, string publicationId, string extension, AppConfig config)
    {
        if (!Guid.TryParse(publicationId, out _) ||
            string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith(config.Root.Trim('/') + "/publication-staging/" + publicationId + "/", StringComparison.Ordinal) ||
            !key.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            key.Contains("//", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal) ||
            key.Contains('\\', StringComparison.Ordinal))
            throw new InvalidDataException("服务端返回的 COS 暂存对象路径无效");
    }
    public static Uri ValidateUploadUrl(string raw, string key, AppConfig config)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || url.Port != 443 ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidDataException("服务端返回的 COS 上传地址无效");
        var expectedHost = $"{config.Bucket}.cos.{config.Region}.myqcloud.com";
        if (!url.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/').Equals(key, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(url.Query))
            throw new InvalidDataException("COS 上传地址与当前桶或对象路径不符");
        return url;
    }

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}