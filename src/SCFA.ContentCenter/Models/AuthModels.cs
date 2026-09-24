using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class ApiErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class HealthResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("service")] public string Service { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("submission_ready")] public bool SubmissionReady { get; set; }
    [JsonPropertyName("updater_ready")] public bool UpdaterReady { get; set; }
    [JsonPropertyName("updater_version")] public string UpdaterVersion { get; set; } = "";
    [JsonPropertyName("updater_url")] public string UpdaterUrl { get; set; } = "";
    [JsonPropertyName("updater_sha256")] public string UpdaterSha256 { get; set; } = "";
    [JsonPropertyName("updater_size")] public long UpdaterSize { get; set; }
    [JsonPropertyName("updater_notes")] public string UpdaterNotes { get; set; } = "";
    [JsonPropertyName("updater_published_at")] public string UpdaterPublishedAt { get; set; } = "";
    [JsonPropertyName("direct_ready")] public bool DirectReady { get; set; }
    [JsonPropertyName("direct_url")] public string DirectUrl { get; set; } = "";
    [JsonPropertyName("direct_cert_sha256")] public string DirectCertSha256 { get; set; } = "";
}

public sealed class UserInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("role_key")] public string RoleKey { get; set; } = "";
    [JsonPropertyName("role_label")] public string RoleLabel { get; set; } = "";
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("last_login_at")] public string LastLoginAt { get; set; } = "";
    [JsonPropertyName("active_sessions")] public int ActiveSessions { get; set; }
}

public sealed record LoginRequest(
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("password")] string Password);

public sealed class LoginResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
    [JsonPropertyName("user")] public UserInfo User { get; set; } = new();
}

public sealed record RegisterRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password);

public sealed class RegisterResponse { [JsonPropertyName("user")] public UserInfo User { get; set; } = new(); }
public sealed class MeResponse { [JsonPropertyName("user")] public UserInfo User { get; set; } = new(); }
