using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class AppUpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
}

public sealed class AppUpdateInfo
{
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string Channel { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public string Notes { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public bool Available { get; init; }
    public bool MetadataComplete { get; init; }
    public string Status { get; init; } = "";
    public string SizeText => Size < 1024 * 1024 ? $"{Size / 1024d:F1} KB" : $"{Size / 1024d / 1024d:F1} MB";
}
