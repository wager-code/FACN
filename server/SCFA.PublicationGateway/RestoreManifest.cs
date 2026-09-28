using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Core;

namespace SCFA.PublicationGateway;

public static partial class RestoreManifest
{
    public static byte[] Add(string current, string kind, string cosRoot, JsonObject archived)
    {
        var id = String(archived, "id");
        var folder = String(archived, "folder_name");
        var file = String(archived, "file");
        var hash = String(archived, "sha256");
        var version = String(archived, "version");
        var size = archived["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var number) ? number : -1;
        if (kind is not ("map" or "mod") || !IdPattern().IsMatch(id) ||
            !ValidFolder(folder) ||
            version.Length is < 1 or > 120 || size is <= 0 or > 4L * 1024 * 1024 * 1024 ||
            !HashPattern().IsMatch(hash) ||
            !file.StartsWith(cosRoot.Trim('/') + "/" + (kind == "map" ? "maps/" : "mods/") + id + "/", StringComparison.Ordinal) ||
            !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("历史条目缺少安全恢复所需的目录、ZIP 校验或版本信息");
        if (Encoding.UTF8.GetByteCount(current) > 16 * 1024 * 1024)
            throw new InvalidDataException("正式清单超过大小限制");
        var root = JsonNode.Parse(current) as JsonObject ?? throw new InvalidDataException("正式清单格式无效");
        var list = root[kind == "map" ? "maps" : "mods"] as JsonArray ?? throw new InvalidDataException("正式清单缺少内容列表");
        if (list.Count >= 10000 || list.OfType<JsonObject>().Any(x =>
                String(x, "id").Equals(id, StringComparison.OrdinalIgnoreCase) ||
                (String(x, "folder_name") is { Length: > 0 } oldFolder && oldFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("内容 ID 或目录已存在，不能通过恢复覆盖正式版本");
        list.Add(archived.DeepClone());
        root["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("恢复后的清单超过大小限制");
        return bytes;
    }

    private static bool ValidFolder(string folder)
    {
        if (folder.Length is < 1 or > 240 || folder.Contains('/') || folder.Contains('\\')) return false;
        try { return SafeArchive.ValidateRelativePath(folder) == folder; }
        catch (InvalidDataException) { return false; }
    }

    private static string String(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text?.Trim() ?? "" : "";
    [GeneratedRegex("^[A-Za-z0-9_-]{1,120}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex HashPattern();
}
