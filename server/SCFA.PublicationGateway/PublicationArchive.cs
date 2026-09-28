using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SCFA.PublicationGateway;

public static partial class PublicationArchive
{
    public static PublicationArchiveResponse Build(string kind, string cosRoot, string current, IEnumerable<string> snapshots, bool truncated = false)
    {
        if (kind is not ("map" or "mod") || string.IsNullOrWhiteSpace(cosRoot))
            throw new InvalidDataException("历史目录参数无效");
        var groups = new Dictionary<string, List<ArchiveVersion>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddManifest(string text, bool currentManifest)
        {
            if (Encoding.UTF8.GetByteCount(text) > 16 * 1024 * 1024)
                throw new InvalidDataException("历史清单超过大小限制");
            var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("历史清单格式无效");
            var list = root[kind == "map" ? "maps" : "mods"] as JsonArray;
            if (list is null) return;
            if (list.Count > 10000) throw new InvalidDataException("历史清单条目过多");
            foreach (var node in list)
            {
                if (node is not JsonObject entry) continue;
                var id = String(entry, "id");
                var version = String(entry, "version");
                var file = String(entry, "file");
                if (!IdPattern().IsMatch(id) || version.Length is < 1 or > 120 ||
                    !file.StartsWith(cosRoot.Trim('/') + "/" + (kind == "map" ? "maps/" : "mods/") + id + "/", StringComparison.Ordinal) ||
                    !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains("..", StringComparison.Ordinal) || file.Contains('\\')) continue;
                var name = String(entry, "name");
                if (!groups.TryGetValue(id, out var versions)) groups[id] = versions = [];
                if (currentManifest) activeIds.Add(id);
                var identity = file + "|" + String(entry, "sha256");
                if (versions.Any(x => String(x.Entry, "file") + "|" + String(x.Entry, "sha256") == identity)) continue;
                versions.Add(new ArchiveVersion((JsonObject)entry.DeepClone(), currentManifest ? "current" : "history"));
                if (!names.ContainsKey(id)) names[id] = name.Length == 0 ? id : name;
            }
        }

        AddManifest(current, true);
        foreach (var snapshot in snapshots)
        {
            try { AddManifest(snapshot, false); }
            catch (System.Text.Json.JsonException) { continue; }
            catch (InvalidDataException) { continue; }
        }
        var result = groups.Select(pair =>
        {
            var downlisted = !activeIds.Contains(pair.Key);
            if (downlisted && pair.Value.Count > 0)
                pair.Value[0] = pair.Value[0] with { State = "downlisted" };
            return new ArchivedContent(pair.Key, names[pair.Key], downlisted, pair.Value);
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new PublicationArchiveResponse(result, truncated);
    }

    private static string String(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text?.Trim() ?? "" : "";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,120}$")]
    private static partial Regex IdPattern();
}
