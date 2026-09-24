using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class SyncHistoryService
{
    private const int MaxRecords = 30;
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncHistoryService(string? dataDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(dataDirectory) ? ConfigService.ResolveDataDirectory() : Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "sync-history.json");
    }

    public IReadOnlyList<SyncRunRecord> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 2 * 1024 * 1024) return [];
            var records = JsonSerializer.Deserialize<List<SyncRunRecord>>(File.ReadAllText(_path), _json) ?? [];
            return records.Where(IsValid).OrderByDescending(x => x.EndedAt).Take(MaxRecords).ToArray();
        }
        catch
        {
            try { File.Move(_path, _path + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
            return [];
        }
    }

    public async Task AppendAsync(SyncRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Messages = record.Messages.Where(x => !string.IsNullOrWhiteSpace(x)).Take(5000).ToList();
        await _gate.WaitAsync(ct);
        var temp = _path + ".tmp";
        try
        {
            var records = Load().ToList();
            records.Insert(0, record);
            records = records.Take(MaxRecords).ToList();
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(records, _json), ct);
            File.Move(temp, _path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var temp = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, "[]", ct);
            File.Move(temp, _path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            _gate.Release();
        }
    }

    private static bool IsValid(SyncRunRecord record) =>
        record is not null && !string.IsNullOrWhiteSpace(record.Scope) &&
        !string.IsNullOrWhiteSpace(record.Status) && record.Messages is not null;
}
