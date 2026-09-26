using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

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
