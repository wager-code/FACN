using System.Net;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class CloudHistoryService(ConfigService config, AuthApiClient auth, CloudCatalogService cloud, LogService log)
{
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<CloudHistoryResult> FetchAsync(string kind, string contentId, CancellationToken ct = default)
    {
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind);
        if (string.IsNullOrWhiteSpace(contentId)) throw new ArgumentException("内容 ID 为空");
        var current = (await cloud.FetchAsync(kind, ct)).FirstOrDefault(x => x.Id.Equals(contentId, StringComparison.OrdinalIgnoreCase));
        var entries = new List<CloudContentEntry>();
        if (current is not null)
        {
            current.HistoryState = "当前正式版本";
            entries.Add(current);
        }

        var path = NormalizePath(config.Current.ContentHistoryApiPath);
        try
        {
            var queryKind = kind == "地图" ? "map" : "mod";
            var root = await auth.GetJsonAsync<JsonElement>($"{path}?kind={queryKind}&id={Uri.EscapeDataString(contentId)}", ct);
            foreach (var item in Extract(root))
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = contentId;
                if (!item.Id.Equals(contentId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.Version) || string.IsNullOrWhiteSpace(item.File)) continue;
                if (item.Size < 0 || item.Size > CloudCatalogService.MaxPackageBytes || !IsOptionalSha256(item.Sha256) || !IsOptionalSha256(item.EffectiveContentHash)) continue;
                if (string.IsNullOrWhiteSpace(item.Name)) item.Name = current?.Name ?? contentId;
                if (string.IsNullOrWhiteSpace(item.FolderName)) item.FolderName = current?.FolderName ?? "";
                item.Aliases ??= current?.Aliases ?? [];
                item.Kind = kind;
                item.HistoryState = "历史版本";
                if (!string.IsNullOrWhiteSpace(item.Thumbnail)) item.ThumbnailUrl = cloud.PublicUrl(item.Thumbnail);
                if (entries.Any(x => x.Version.Equals(item.Version, StringComparison.OrdinalIgnoreCase) && x.Sha256.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))) continue;
                entries.Add(item);
            }
            return new CloudHistoryResult { Entries = Sort(entries) };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new CloudHistoryResult { Entries = Sort(entries), Warning = "服务端尚未提供当前配置的历史版本接口；已仅显示 COS 当前正式版本。" };
        }
        catch (Exception ex)
        {
            log.Error($"读取云端历史失败: {kind} {contentId}", ex);
            return new CloudHistoryResult { Entries = Sort(entries), Warning = "历史版本读取失败：" + ex.Message };
        }
    }

    private List<CloudContentEntry> Extract(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.Deserialize<List<CloudContentEntry>>(_json) ?? [];
        foreach (var name in new[] { "history", "versions", "items", "data" })
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value.Deserialize<List<CloudContentEntry>>(_json) ?? [];
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) return items.Deserialize<List<CloudContentEntry>>(_json) ?? [];
            }
        return [];
    }
    private static List<CloudContentEntry> Sort(List<CloudContentEntry> entries) => entries.OrderByDescending(x => x.HistoryState == "当前正式版本").ThenByDescending(x => x.PublishedAt, StringComparer.OrdinalIgnoreCase).ToList();
    private static string NormalizePath(string value)
    {
        var path = string.IsNullOrWhiteSpace(value) ? "/v1/content/history" : value.Trim();
        if (!path.StartsWith('/') || path.StartsWith("//") || Uri.TryCreate(path, UriKind.Absolute, out _)) throw new InvalidOperationException("内容历史 API 路径配置无效");
        return path.TrimEnd('/');
    }
    private static bool IsOptionalSha256(string? value) => string.IsNullOrWhiteSpace(value) || (value.Trim().Length == 64 && value.Trim().All(Uri.IsHexDigit));
}
