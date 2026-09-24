using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class SyncRunRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("started_at")] public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("ended_at")] public DateTimeOffset EndedAt { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("installed")] public int Installed { get; set; }
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("messages")] public List<string> Messages { get; set; } = [];

    [JsonIgnore]
    public string Summary => Status == "完成"
        ? $"完成 · 安装/更新 {Installed} · 跳过 {Skipped} · 失败 {Failed}"
        : Status;
}
