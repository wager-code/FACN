using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SCFA.PublicationGateway;

public sealed class GatewayOptions
{
    public string Bucket { get; init; } = Environment.GetEnvironmentVariable("SCFA_COS_BUCKET") ?? "";
    public string Region { get; init; } = Environment.GetEnvironmentVariable("SCFA_COS_REGION") ?? "";
    public string Root { get; init; } = Environment.GetEnvironmentVariable("SCFA_COS_ROOT") ?? "scfa";
    public string DataDirectory { get; init; } = Environment.GetEnvironmentVariable("SCFA_PUBLICATION_DATA") ?? "/var/lib/scfa-publication";
    public string AccountBaseUrl { get; init; } = Environment.GetEnvironmentVariable("SCFA_ACCOUNT_LOCAL_URL") ?? "http://127.0.0.1:18080";
    public byte[] MasterKey { get; init; } = Convert.FromBase64String(Environment.GetEnvironmentVariable("SCFA_PUBLICATION_MASTER_KEY_B64") ?? "");

    public void Validate()
    {
        if (!Regex.IsMatch(Bucket, @"^[a-z0-9][a-z0-9-]{2,62}-[0-9]{5,}$") ||
            !Regex.IsMatch(Region, @"^[a-z]{2}-[a-z0-9-]+$") ||
            !Regex.IsMatch(Root, @"^[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*$"))
            throw new InvalidOperationException("COS 桶、地域或根路径配置无效");
        if (!Uri.TryCreate(AccountBaseUrl, UriKind.Absolute, out var account) ||
            account.Scheme != Uri.UriSchemeHttp || account.Host != "127.0.0.1")
            throw new InvalidOperationException("账号验证服务只能指向本机 127.0.0.1");
        if (MasterKey.Length != 32) throw new InvalidOperationException("需要 32 字节的服务端加密主密钥");
        Directory.CreateDirectory(DataDirectory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(DataDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed record CosCredentials(string SecretId, string SecretKey, DateTimeOffset UpdatedAt);

public sealed class CredentialStore(GatewayOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string PathName => Path.Combine(options.DataDirectory, "cos-credentials.json");

    public async Task<CosCredentials?> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(PathName)) return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(PathName, ct))
                ?? throw new InvalidDataException("COS 凭据存储文件无效");
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(options.MasterKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes("scfa-cos-credentials-v1"));
            try
            {
                return JsonSerializer.Deserialize<CosCredentials>(plaintext)
                    ?? throw new InvalidDataException("COS 凭据内容无效");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(CosCredentials credentials, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.SecretId) || string.IsNullOrWhiteSpace(credentials.SecretKey))
            throw new InvalidDataException("COS SecretId/SecretKey 不能为空");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(options.MasterKey, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("scfa-cos-credentials-v1"));
        CryptographicOperations.ZeroMemory(plaintext);
        var envelope = new Envelope(Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
        await _gate.WaitAsync(ct);
        try
        {
            var temp = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(envelope), ct);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, PathName, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
    }

    private sealed record Envelope(string Nonce, string Tag, string Ciphertext);
}

public sealed record PublicationIntentRequest(
    string Kind, string PackageKey, string PackageSha256, long PackageSize, string ContentSha256,
    string ManifestKey, string OriginalManifestSha256, string NextManifest, string NextManifestSha256,
    string? ThumbnailKey, string? ThumbnailSha256, long? ThumbnailSize);
public sealed record PublicationIntentResponse(
    string PublicationId, string PackageUploadKey, string PackageUploadUrl,
    string? ThumbnailUploadKey, string? ThumbnailUploadUrl);
public sealed record PublicationCommitRequest(string PackageSha256, string NextManifestSha256);
public sealed record PublicationCommitResponse(string ManifestSha256);
public sealed record UnpublishRequest(string Kind, string ContentId, string Reason, string? ClientVersion);
public sealed record UnpublishResponse(string ManifestSha256, string ContentId);
public sealed record ArchiveVersion(JsonObject Entry, string State);
public sealed record ArchivedContent(string Id, string Name, bool Downlisted, List<ArchiveVersion> Versions);
public sealed record PublicationArchiveResponse(List<ArchivedContent> Items, bool Truncated);
public sealed record RestoreRequest(string Kind, string ContentId, string PackageKey, string PackageSha256);
public sealed record RestoreResponse(string ManifestSha256, string ContentId);
public sealed record CredentialRotationRequest(string SecretId, string SecretKey);
public sealed record PublicationTicket(
    string Id, string UserId, DateTimeOffset ExpiresAt, PublicationIntentRequest Request,
    string PackageStagingKey, string? ThumbnailStagingKey, bool Completed = false);
