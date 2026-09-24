using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class SubmissionRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("content_id")] public string ContentId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("submitter")] public string Submitter { get; set; } = "";
    [JsonPropertyName("reviewer")] public string Reviewer { get; set; } = "";
    [JsonPropertyName("review_message")] public string ReviewMessage { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("content_sha256")] public string ContentSha256 { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("preview_url")] public string PreviewUrl { get; set; } = "";
    [JsonPropertyName("files")] public int Files { get; set; }
    public string SizeText => Size < 1024 * 1024 ? $"{Size / 1024d:F1} KB" : $"{Size / 1024d / 1024d:F1} MB";
    public string TagsText => Tags.Count == 0 ? "无" : string.Join("、", Tags);
}

public sealed class SubmissionDraft
{
    [JsonPropertyName("content_key")] public string ContentKey { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("tags_text")] public string TagsText { get; set; } = "";
    [JsonPropertyName("preview_path")] public string PreviewPath { get; set; } = "";
    [JsonPropertyName("saved_at")] public DateTimeOffset SavedAt { get; set; }
}

public sealed class AdminUserRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("role_key")] public string RoleKey { get; set; } = "";
    [JsonPropertyName("role_label")] public string RoleLabel { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("last_login_at")] public string LastLoginAt { get; set; } = "";
    [JsonPropertyName("active_sessions")] public int ActiveSessions { get; set; }
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = [];
    public string RoleDisplay => RoleKey.Trim().ToLowerInvariant() switch
    {
        "user" => "普通用户",
        "reviewer" => "内容审核员",
        "publisher" => "内容发布员",
        "admin" => "管理员",
        "super_admin" => "超级管理员",
        _ => string.IsNullOrWhiteSpace(RoleLabel) ? "未知角色" : RoleLabel.Trim()
    };
    public string StatusDisplay => Status.Trim().ToLowerInvariant() switch
    {
        "active" => "正常",
        "disabled" => "已停用",
        "suspended" => "已暂停",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status.Trim()
    };
    public string CreatedAtDisplay => FormatLocalTime(CreatedAt);
    public string LastLoginAtDisplay => FormatLocalTime(LastLoginAt);

    private static string FormatLocalTime(string value) => DateTimeOffset.TryParse(value, out var parsed)
        ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : string.IsNullOrWhiteSpace(value) ? "—" : value;
}

public sealed class AuditRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("actor")] public string Actor { get; set; } = "";
    [JsonPropertyName("actor_name")] public string ActorName { get; set; } = "";
    [JsonPropertyName("actor_id")] public string ActorId { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("target_name")] public string TargetName { get; set; } = "";
    [JsonPropertyName("target_id")] public string TargetId { get; set; } = "";
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("remote_ip")] public string RemoteIp { get; set; } = "";
    [JsonIgnore] public string EffectiveActor => !string.IsNullOrWhiteSpace(Actor) ? Actor : !string.IsNullOrWhiteSpace(ActorName) ? ActorName : ActorId;
    [JsonIgnore] public string EffectiveTarget => !string.IsNullOrWhiteSpace(Target) ? Target : !string.IsNullOrWhiteSpace(TargetName) ? TargetName : TargetId;
    [JsonIgnore] public string EffectiveIp => !string.IsNullOrWhiteSpace(Ip) ? Ip : RemoteIp;
    public string EffectiveTime => string.IsNullOrWhiteSpace(Time) ? CreatedAt : Time;
}

public sealed class AuditFetchResult
{
    [JsonPropertyName("records")] public List<AuditRecord> Records { get; set; } = [];
    [JsonPropertyName("integrity_ok")] public bool? IntegrityOk { get; set; }
    [JsonPropertyName("integrity_message")] public string IntegrityMessage { get; set; } = "";
    [JsonPropertyName("total")] public int? Total { get; set; }
}
