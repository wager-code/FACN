using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class SyncPreferenceService(Func<string> accountScope)
{
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxKeys = 10000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<bool> IsExcludedAsync(CloudContentEntry entry, CancellationToken ct = default)
    {
        var key = ContentKey(entry);
        var path = CurrentPath();
        await _gate.WaitAsync(ct);
        try { return (await ReadAsync(path, ct)).Keys.Contains(key, StringComparer.OrdinalIgnoreCase); }
        finally { _gate.Release(); }
    }

    public async Task SetExcludedAsync(CloudContentEntry entry, bool excluded, CancellationToken ct = default)
    {
        var key = ContentKey(entry);
        var path = CurrentPath();
        await _gate.WaitAsync(ct);
        try
        {
            var document = await ReadAsync(path, ct);
            var keys = document.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!(excluded ? keys.Add(key) : keys.Remove(key))) return;
            if (keys.Count > MaxKeys) throw new InvalidOperationException("不喜欢列表已达到安全上限");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp_" + Guid.NewGuid().ToString("N");
            try
            {
                var updated = new PreferenceDocument { Keys = keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() };
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, _json), ct);
                File.Move(temporary, path, true);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        finally { _gate.Release(); }
    }

    private string CurrentPath()
    {
        var scope = accountScope()?.Trim();
        if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("尚未确定当前登录账号，不能保存同步偏好");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToLowerInvariant())));
        return Path.Combine(ConfigService.ResolveDataDirectory(), "SyncPreferences", hash + ".json");
    }

    private async Task<PreferenceDocument> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new PreferenceDocument();
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("同步偏好文件过大，已停止自动同步");
        var document = JsonSerializer.Deserialize<PreferenceDocument>(await File.ReadAllTextAsync(path, ct), _json);
        if (document is null || document.SchemaVersion != 1 || document.Keys is null || document.Keys.Count > MaxKeys ||
            document.Keys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 250 ||
                !(key.StartsWith("地图:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MOD:", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("同步偏好文件无效，已停止自动同步以免安装不想要的内容");
        return document;
    }

    private static string ContentKey(CloudContentEntry entry)
    {
        if (entry.Kind is not ("地图" or "MOD") || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 200)
            throw new InvalidDataException("云端内容缺少有效类型或 ID，无法保存同步偏好");
        return entry.Kind + ":" + entry.Id.Trim();
    }

    private sealed class PreferenceDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<string> Keys { get; set; } = [];
    }
}
