using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class CloudHistoryService(PublicationUploadService publication, CloudCatalogService cloud, LogService log)
{
    public async Task<CloudHistoryResult> FetchArchiveAsync(string kind, UserInfo user, CancellationToken ct = default)
    {
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind);
        try
        {
            var response = await publication.GetArchiveAsync(kind, user, ct);
            var entries = new List<CloudContentEntry>();
            foreach (var group in response.Items)
            foreach (var version in group.Versions)
            {
                if (version.Entry.ValueKind != JsonValueKind.Object) continue;
                var item = version.Entry.Deserialize<CloudContentEntry>();
                if (item is null || !string.Equals(item.Id, group.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(item.Version) || string.IsNullOrWhiteSpace(item.File) ||
                    item.Size < 0 || item.Size > CloudCatalogService.MaxPackageBytes ||
                    !IsOptionalSha256(item.Sha256) || !IsOptionalSha256(item.EffectiveContentHash)) continue;
                item.Kind = kind;
                item.Aliases ??= [];
                item.Tags ??= [];
                if (string.IsNullOrWhiteSpace(item.Name)) item.Name = group.Name;
                if (string.IsNullOrWhiteSpace(item.FolderName)) item.FolderName = item.Id;
                item.HistoryState = version.State switch
                {
                    "current" => "当前正式版",
                    "downlisted" => "已下架",
                    _ => "历史版本"
                };
                if (!string.IsNullOrWhiteSpace(item.Thumbnail)) item.ThumbnailUrl = cloud.PublicUrl(item.Thumbnail);
                entries.Add(item);
            }
            return new CloudHistoryResult
            {
                Entries = entries,
                Warning = response.Truncated ? "历史快照超过查询上限，仅显示最近记录。" : ""
            };
        }
        catch (Exception ex)
        {
            log.Error("读取云端版本档案失败: " + kind, ex);
            throw;
        }
    }

    private static bool IsOptionalSha256(string? value) =>
        string.IsNullOrWhiteSpace(value) || (value.Trim().Length == 64 && value.Trim().All(Uri.IsHexDigit));
}
