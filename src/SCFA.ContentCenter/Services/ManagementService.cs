using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class ManagementService(ConfigService config, AuthApiClient auth, LogService log)
{
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<AdminUserRecord>> FetchUsersAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var root = await auth.GetJsonAsync<JsonElement>(NormalizePath(config.Current.AdminUsersPath, "/v1/admin/users"), ct);
        return ExtractList<AdminUserRecord>(root, "users", "items", "data");
    }

    public async Task UpdateUserAsync(string userId, string roleKey, string status, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var path = NormalizePath(config.Current.AdminUsersPath, "/v1/admin/users").TrimEnd('/') + "/" + Uri.EscapeDataString(userId);
        _ = await auth.PatchJsonAsync<JsonElement>(path, new { role_key = roleKey.Trim(), status = status.Trim() }, ct);
        log.Info($"用户管理更新已发送: {userId} role={roleKey} status={status}");
    }

    public async Task RevokeSessionsAsync(string userId, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var path = NormalizePath(config.Current.AdminUsersPath, "/v1/admin/users").TrimEnd('/') + "/" + Uri.EscapeDataString(userId) + "/sessions/revoke";
        _ = await auth.PostJsonAsync<JsonElement>(path, new { reason = "admin_revoke", client_version = Core.AppVersion.Informational }, ct);
        log.Info("用户会话撤销已发送: " + userId);
    }

    public async Task UnpublishContentAsync(string kind, string contentId, string reason, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind, nameof(kind));
        if (string.IsNullOrWhiteSpace(contentId)) throw new ArgumentException("内容 ID 为空", nameof(contentId));
        reason = string.IsNullOrWhiteSpace(reason) ? "管理员在内容目录中下架" : reason.Trim();
        var path = NormalizePath(config.Current.AdminContentPath, "/v1/admin/content").TrimEnd('/') + "/unpublish";
        _ = await auth.PostJsonAsync<JsonElement>(path, new
        {
            kind = kind == "地图" ? "map" : "mod",
            content_id = contentId.Trim(),
            reason,
            client_version = Core.AppVersion.Informational
        }, ct);
        log.Info($"管理员下架请求已发送: {kind} {contentId} reason={reason}");
    }

    public async Task<IReadOnlyList<AuditRecord>> FetchAuditAsync(CancellationToken ct = default)
        => (await FetchAuditResultAsync(ct)).Records;

    public async Task<AuditFetchResult> FetchAuditResultAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var root = await auth.GetJsonAsync<JsonElement>(NormalizePath(config.Current.AuditApiPath, "/v1/admin/audit"), ct);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out _))
            return root.Deserialize<AuditFetchResult>(_json) ?? new AuditFetchResult();
        return new AuditFetchResult { Records = ExtractList<AuditRecord>(root, "audit", "events", "items", "data").ToList() };
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(auth.Token)) throw new InvalidOperationException("请先使用有权限的服务器账号登录");
    }
    private static string NormalizePath(string value, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!path.StartsWith('/') || path.StartsWith("//") || Uri.TryCreate(path, UriKind.Absolute, out _)) throw new InvalidOperationException("管理 API 路径配置无效");
        return path.TrimEnd('/');
    }
    private IReadOnlyList<T> ExtractList<T>(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.Deserialize<List<T>>(_json) ?? [];
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value.Deserialize<List<T>>(_json) ?? [];
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) return items.Deserialize<List<T>>(_json) ?? [];
            }
        return [];
    }
}
