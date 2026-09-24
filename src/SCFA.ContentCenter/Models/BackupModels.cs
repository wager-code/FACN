using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class ContentBackupEntry
{
    public string Kind { get; init; } = "";
    public string ContentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string OriginalRoot { get; init; } = "";
    public string RevisionRoot { get; init; } = "";
    public string ContentRoot { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
    public string SizeText => FormatBytes(Bytes);
    public string DisplayVersion => string.IsNullOrWhiteSpace(SourceVersion) ? "未知" : SourceVersion;

    private static string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:F1} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / 1024d / 1024d:F1} MB";
        return $"{value / 1024d / 1024d / 1024d:F2} GB";
    }
}

public sealed class ContentBackupMetadata
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("content_id")] public string ContentId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source_version")] public string SourceVersion { get; set; } = "";
    [JsonPropertyName("folder_name")] public string FolderName { get; set; } = "";
    [JsonPropertyName("original_root")] public string OriginalRoot { get; set; } = "";
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("files")] public int Files { get; set; }
}
