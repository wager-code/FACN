using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SCFA.PublicationGateway;

public static partial class UnpublishManifest
{
    public static byte[] Remove(string current, UnpublishRequest request)
    {
        if (request.Kind is not ("map" or "mod") ||
            request.ContentId is null || !IdPattern().IsMatch(request.ContentId) ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500 ||
            Encoding.UTF8.GetByteCount(current) > 16 * 1024 * 1024)
            throw new InvalidDataException("下架参数或清单大小无效");

        var root = JsonNode.Parse(current) as JsonObject ?? throw new InvalidDataException("云端清单格式无效");
        var listKey = request.Kind == "map" ? "maps" : "mods";
        var list = root[listKey] as JsonArray ?? throw new InvalidDataException("云端清单缺少内容列表");
        var matches = list.Select((item, index) => (item, index))
            .Where(pair => pair.item is JsonObject entry &&
                string.Equals(entry["id"]?.GetValue<string>(), request.ContentId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("清单中不存在唯一匹配的内容 ID，未执行下架");

        list.RemoveAt(matches[0]);
        root["updated_at"] = DateTimeOffset.UtcNow.ToString("O");
        return Encoding.UTF8.GetBytes(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,120}$")]
    private static partial Regex IdPattern();
}

