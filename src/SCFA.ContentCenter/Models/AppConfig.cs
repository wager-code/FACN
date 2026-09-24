using System.Text.Json;
using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class LoginAccountRecord
{
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("password_encrypted")] public string PasswordEncrypted { get; set; } = "";
    [JsonPropertyName("last_used_at")] public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LocalUserProfile
{
    [JsonPropertyName("user_key")] public string UserKey { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("qq")] public string QQ { get; set; } = "";
    [JsonPropertyName("phone")] public string Phone { get; set; } = "";
    [JsonPropertyName("avatar_path")] public string AvatarPath { get; set; } = "";
}

public sealed class AppConfig
{
    [JsonPropertyName("secret_id")] public string SecretId { get; set; } = "";
    [JsonPropertyName("secret_key_encrypted")] public string SecretKeyEncrypted { get; set; } = "";
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "scfa-map-center-1317535019";
    [JsonPropertyName("region")] public string Region { get; set; } = "ap-shanghai";
    [JsonPropertyName("root")] public string Root { get; set; } = "scfa";
    [JsonPropertyName("ui_theme")] public string UiTheme { get; set; } = "navy";
    [JsonPropertyName("ui_scale_percent")] public int UiScalePercent { get; set; }
    [JsonPropertyName("ui_font_size")] public string UiFontSize { get; set; } = "medium";
    [JsonPropertyName("auto_layout")] public bool AutoLayout { get; set; } = true;
    [JsonPropertyName("high_dpi")] public bool HighDpi { get; set; } = true;
    [JsonPropertyName("game_root")] public string GameRoot { get; set; } = "";
    [JsonPropertyName("maps_dir")] public string MapsDir { get; set; } = "";
    [JsonPropertyName("mods_dir")] public string ModsDir { get; set; } = "";
    [JsonPropertyName("download_dir")] public string DownloadDir { get; set; } = "";
    [JsonPropertyName("release_dir")] public string ReleaseDir { get; set; } = "";
    [JsonPropertyName("api_base_url")] public string ApiBaseUrl { get; set; } = "https://124.223.170.117:18443";
    [JsonPropertyName("api_direct_url")] public string ApiDirectUrl { get; set; } = "https://124.223.170.117:18443";
    [JsonPropertyName("api_direct_cert_sha256")] public string ApiDirectCertSha256 { get; set; } = "bcb1a9ebe36a0ce6b25a3408d074301de7561417cde8507db65ede34b7b89aa9";
    [JsonPropertyName("last_login_account")] public string LastLoginAccount { get; set; } = "";
    [JsonPropertyName("remember_login_account")] public bool RememberLoginAccount { get; set; } = true;
    [JsonPropertyName("remember_login_password")] public bool RememberLoginPassword { get; set; }
    [JsonPropertyName("login_password_encrypted")] public string LoginPasswordEncrypted { get; set; } = "";
    [JsonPropertyName("auto_login")] public bool AutoLogin { get; set; }
    [JsonPropertyName("login_accounts")] public List<LoginAccountRecord> LoginAccounts { get; set; } = [];
    [JsonPropertyName("local_user_profiles")] public List<LocalUserProfile> LocalUserProfiles { get; set; } = [];
    [JsonPropertyName("update_channel")] public string UpdateChannel { get; set; } = "stable";
    [JsonPropertyName("update_manifest_url")] public string UpdateManifestUrl { get; set; } = "";
    [JsonPropertyName("auto_check_updates")] public bool AutoCheckUpdates { get; set; } = true;
    [JsonPropertyName("submission_api_path")] public string SubmissionApiPath { get; set; } = "/v1/submissions";
    [JsonPropertyName("submission_admin_path")] public string SubmissionAdminPath { get; set; } = "/v1/admin/submissions";
    [JsonPropertyName("admin_users_path")] public string AdminUsersPath { get; set; } = "/v1/admin/users";
    [JsonPropertyName("admin_content_path")] public string AdminContentPath { get; set; } = "/v1/admin/content";
    [JsonPropertyName("audit_api_path")] public string AuditApiPath { get; set; } = "/v1/admin/audit";
    [JsonPropertyName("content_history_api_path")] public string ContentHistoryApiPath { get; set; } = "/v1/content/history";
    [JsonPropertyName("favorite_content_keys")] public List<string> FavoriteContentKeys { get; set; } = [];
    [JsonPropertyName("recent_content_keys")] public List<string> RecentContentKeys { get; set; } = [];
    [JsonPropertyName("csharp_offline_allowed")] public bool OfflineAllowed { get; set; } = true;

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static AppConfig Defaults() => new();
}
