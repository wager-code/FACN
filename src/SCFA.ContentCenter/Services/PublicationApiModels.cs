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

public sealed class UnpublishResult
{
    [JsonPropertyName("manifest_sha256")] public string ManifestSha256 { get; set; } = "";
    [JsonPropertyName("content_id")] public string ContentId { get; set; } = "";
}

public sealed class RestoreResult
{
    [JsonPropertyName("manifest_sha256")] public string ManifestSha256 { get; set; } = "";
    [JsonPropertyName("content_id")] public string ContentId { get; set; } = "";
}

public sealed class PublicationArchiveDto
{
    [JsonPropertyName("items")] public List<ArchivedContentDto> Items { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}
public sealed class ArchivedContentDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downlisted")] public bool Downlisted { get; set; }
    [JsonPropertyName("versions")] public List<ArchivedVersionDto> Versions { get; set; } = [];
}
public sealed class ArchivedVersionDto
{
    [JsonPropertyName("entry")] public JsonElement Entry { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
}

/// <summary>
/// Uploads checked materials only after an authenticated server issues object-scoped upload URLs.
/// The server owns COS credentials, validates uploaded bytes and atomically commits the manifest.
/// </summary>
