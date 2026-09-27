using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Core;

namespace SCFA.PublicationGateway;

public sealed record PublicationTarget(string Id, string Folder, string GameVersion);

public static partial class ManifestValidator
{
    private const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;

    public static PublicationTarget Validate(PublicationIntentRequest request, string oldText, GatewayOptions options)
    {
        if (request.Kind is not ("map" or "mod") ||
            request.PackageSize is <= 0 or > MaxPackageBytes ||
            Encoding.UTF8.GetByteCount(request.NextManifest ?? "") > 16 * 1024 * 1024 ||
            !IsHash(request.PackageSha256) || !IsHash(request.ContentSha256) ||
            !IsHash(request.OriginalManifestSha256) || !IsHash(request.NextManifestSha256) ||
            CosTransport.Sha256(oldText) != request.OriginalManifestSha256 ||
            CosTransport.Sha256(request.NextManifest ?? "") != request.NextManifestSha256)
            throw new InvalidDataException("发布清单或哈希参数无效");
        var listKey = request.Kind == "map" ? "maps" : "mods";
        var manifestKey = options.Root + "/manifest/" + (request.Kind == "map" ? "latest.json" : "mods.json");
        if (request.ManifestKey != manifestKey) throw new InvalidDataException("发布清单对象路径无效");
        var oldRoot = JsonNode.Parse(oldText) as JsonObject ?? throw new InvalidDataException("线上清单格式无效");
        var nextRoot = JsonNode.Parse(request.NextManifest ?? "") as JsonObject ?? throw new InvalidDataException("新清单格式无效");
        var oldList = oldRoot[listKey] as JsonArray ?? throw new InvalidDataException("线上清单缺少内容列表");
        var nextList = nextRoot[listKey] as JsonArray ?? throw new InvalidDataException("新清单缺少内容列表");
        if (nextList.Count is < 1 or > 10000 || nextList.Count > oldList.Count + 1 || nextList.Count < oldList.Count)
            throw new InvalidDataException("新清单条目数异常");
        var oldMeta = (JsonObject)oldRoot.DeepClone();
        var newMeta = (JsonObject)nextRoot.DeepClone();
        oldMeta.Remove(listKey); newMeta.Remove(listKey);
        oldMeta.Remove("updated_at"); newMeta.Remove("updated_at");
        if (!JsonNode.DeepEquals(oldMeta, newMeta))
            throw new InvalidDataException("发布不能修改其他清单字段");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonObject? target = null;
        var targetIndex = -1;
        for (var index = 0; index < nextList.Count; index++)
        {
            var item = nextList[index] as JsonObject ?? throw new InvalidDataException("清单条目不是对象");
            var id = Required(item, "id");
            var folder = String(item, "folder_name");
            // Published manifests can contain legacy entries without folder_name. Keep those
            // entries byte-for-byte unchanged and use their ID only for collision checking.
            if (folder.Length == 0 && index < oldList.Count &&
                oldList[index] is JsonObject legacy &&
                String(legacy, "id").Equals(id, StringComparison.OrdinalIgnoreCase) &&
                String(legacy, "folder_name").Length == 0 &&
                JsonNode.DeepEquals(legacy, item))
                folder = id;
            if (!IdentityPattern().IsMatch(id) || !ValidFolder(folder) ||
                !ids.Add(id) || !folders.Add(folder))
                throw new InvalidDataException("清单含无效或重复 ID/目录");
            if (String(item, "file") == request.PackageKey)
            {
                if (target is not null) throw new InvalidDataException("发布包被多个条目引用");
                target = item;
                targetIndex = index;
            }
        }
        if (target is null) throw new InvalidDataException("新清单没有待发布内容");
        var targetId = Required(target, "id");
        var targetFolder = Required(target, "folder_name");
        if (!request.PackageKey.StartsWith(options.Root + "/" + listKey + "/" + targetId + "/", StringComparison.Ordinal) ||
            !request.PackageKey.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            request.PackageKey.Contains("..", StringComparison.Ordinal) ||
            request.PackageKey.Contains('\\', StringComparison.Ordinal) ||
            String(target, "sha256") != request.PackageSha256 ||
            String(target, "content_sha256") != request.ContentSha256 ||
            Number(target, "size") != request.PackageSize)
            throw new InvalidDataException("发布包对象路径或内容校验字段不一致");
        if (request.ThumbnailKey is not null)
        {
            if (request.Kind != "map" ||
                !request.ThumbnailKey.StartsWith(options.Root + "/thumbnails/" + targetId + "-", StringComparison.Ordinal) ||
                !request.ThumbnailKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                String(target, "thumbnail") != request.ThumbnailKey ||
                !IsHash(request.ThumbnailSha256) ||
                request.ThumbnailSize is <= 0 or > 10 * 1024 * 1024)
                throw new InvalidDataException("缩略图参数不一致");
        }
        else if (request.ThumbnailSha256 is not null || request.ThumbnailSize is not null)
            throw new InvalidDataException("缩略图参数不完整");
        var oldIndex = -1;
        for (var index = 0; index < oldList.Count; index++)
        {
            var old = oldList[index] as JsonObject ?? throw new InvalidDataException("旧清单条目无效");
            if (String(old, "id").Equals(targetId, StringComparison.OrdinalIgnoreCase)) oldIndex = index;
        }
        if (oldIndex >= 0)
        {
            if (targetIndex != oldIndex || nextList.Count != oldList.Count)
                throw new InvalidDataException("更新不能改变其他条目位置");
            var old = (JsonObject)oldList[oldIndex]!;
            var oldFolder = String(old, "folder_name") is { Length: > 0 } existingFolder
                ? existingFolder : Required(old, "id");
            if (!oldFolder.Equals(targetFolder, StringComparison.OrdinalIgnoreCase) ||
                !Higher(Required(target, "version"), Required(old, "version")) ||
                !NotLower(Required(target, "game_version"), String(old, "game_version") is { Length: > 0 } game ? game : Required(old, "version")))
                throw new InvalidDataException("发布版本、游戏版本或目录不符合安全升级规则");
        }
        else if (targetIndex != oldList.Count || nextList.Count != oldList.Count + 1)
            throw new InvalidDataException("新增内容必须追加到清单末尾");
        for (var index = 0; index < oldList.Count; index++)
            if (index != oldIndex && !JsonNode.DeepEquals(oldList[index], nextList[index]))
                throw new InvalidDataException("发布不能修改其他线上内容");
        return new PublicationTarget(targetId, targetFolder, Required(target, "game_version"));
    }

    private static bool IsHash(string? value) => value is not null && HashPattern().IsMatch(value);
    private static string Required(JsonObject item, string key) =>
        String(item, key) is { Length: > 0 } value ? value : throw new InvalidDataException("清单缺少 " + key);
    private static string String(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text?.Trim() ?? "" : "";
    private static long Number(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : -1;

    private static bool Higher(string next, string old) => Compare(next, old) is > 0;
    private static bool NotLower(string next, string old) => Compare(next, old) is >= 0;

    private static int? Compare(string left, string right)
    {
        static string? Canonical(string value)
        {
            var raw = value.Trim().ToLowerInvariant();
            foreach (var prefix in new[] { "version", "ver", "v" })
                if (raw.StartsWith(prefix, StringComparison.Ordinal)) { raw = raw[prefix.Length..].Trim(); break; }
            if (!VersionPattern().IsMatch(raw)) return null;
            var parts = raw.Split('.').Select(part =>
            {
                var normalized = part.TrimStart('0');
                return normalized.Length == 0 ? "0" : normalized;
            }).ToList();
            while (parts.Count > 1 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
            return string.Join('.', parts);
        }
        var a = Canonical(left);
        var b = Canonical(right);
        if (a is null || b is null) return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : null;
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : "0";
            var y = i < pb.Length ? pb[i] : "0";
            if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
            var result = string.CompareOrdinal(x, y);
            if (result != 0) return result;
        }
        return 0;
    }

    private static bool ValidFolder(string folder)
    {
        if (folder.Length is < 1 or > 240 || folder.Contains('/') || folder.Contains((char)92)) return false;
        if (folder.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return false;
        try { return SafeArchive.ValidateRelativePath(folder) == folder; }
        catch (InvalidDataException) { return false; }
    }

    public static PublicationTarget TargetFromNext(PublicationIntentRequest request, GatewayOptions options)
    {
        if (CosTransport.Sha256(request.NextManifest ?? "") != request.NextManifestSha256 ||
            request.Kind is not ("map" or "mod"))
            throw new InvalidDataException("发布任务清单数据无效");
        var listKey = request.Kind == "map" ? "maps" : "mods";
        var expectedManifestKey = options.Root + "/manifest/" + (request.Kind == "map" ? "latest.json" : "mods.json");
        if (request.ManifestKey != expectedManifestKey) throw new InvalidDataException("发布任务清单路径无效");
        var root = JsonNode.Parse(request.NextManifest ?? "") as JsonObject ?? throw new InvalidDataException("发布任务清单格式无效");
        var list = root[listKey] as JsonArray ?? throw new InvalidDataException("发布任务清单缺少列表");
        var targets = list.OfType<JsonObject>().Where(item => String(item, "file") == request.PackageKey).ToArray();
        if (targets.Length != 1 || String(targets[0], "sha256") != request.PackageSha256 ||
            String(targets[0], "content_sha256") != request.ContentSha256 ||
            Number(targets[0], "size") != request.PackageSize)
            throw new InvalidDataException("已发布清单与发布任务的包信息不一致");
        return new PublicationTarget(Required(targets[0], "id"), Required(targets[0], "folder_name"),
            Required(targets[0], "game_version"));
    }
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex HashPattern();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,120}$")] private static partial Regex IdentityPattern();
    [GeneratedRegex("^[0-9]+(?:\\.[0-9]+)*$")] private static partial Regex VersionPattern();
}
