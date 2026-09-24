using System.Globalization;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions CloneJson = new() { PropertyNameCaseInsensitive = true };
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public string ConfigPath { get; }
    public AppConfig Current { get; private set; } = AppConfig.Defaults();
    public string LoadWarning { get; private set; } = "";

    public ConfigService()
    {
        var env = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var dir = string.IsNullOrWhiteSpace(env)
            ? ResolveDataDirectory()
            : env;
        Directory.CreateDirectory(dir!);
        ConfigPath = Path.Combine(dir!, "config.json");
    }

    public static string ResolveDataDirectory()
    {
        var isolated = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        return string.IsNullOrWhiteSpace(isolated)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCFAContentHub")
            : Path.GetFullPath(isolated.Trim());
    }

    public async Task LoadAsync()
    {
        LoadWarning = "";
        if (!File.Exists(ConfigPath)) { Current = AppConfig.Defaults(); return; }
        try
        {
            await using var fs = File.OpenRead(ConfigPath);
            Current = await JsonSerializer.DeserializeAsync<AppConfig>(fs, _json) ?? AppConfig.Defaults();
            ApplyDefaults(Current);
        }
        catch (Exception ex)
        {
            Current = AppConfig.Defaults();
            var backup = ConfigPath + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss.fff", CultureInfo.InvariantCulture);
            try
            {
                File.Copy(ConfigPath, backup, false);
                LoadWarning = $"配置文件读取失败，已使用默认设置；原文件已备份到 {backup}。原因：{ex.Message}";
            }
            catch (Exception backupError)
            {
                LoadWarning = $"配置文件读取失败，已使用默认设置；备份原文件也失败：{backupError.Message}。原始原因：{ex.Message}";
            }
        }
    }

    public Task SaveAsync() => SaveAsync(Current);

    public async Task SaveAsync(AppConfig value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = Clone(value);
        ApplyDefaults(normalized);
        await _saveGate.WaitAsync();
        var tmp = ConfigPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(normalized, _json));
            File.Move(tmp, ConfigPath, true);
            Current = normalized;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            _saveGate.Release();
        }
    }

    public static AppConfig Clone(AppConfig value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(value, CloneJson), CloneJson) ?? AppConfig.Defaults();
    }

    private static void ApplyDefaults(AppConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.Bucket)) c.Bucket = "scfa-map-center-1317535019";
        if (string.IsNullOrWhiteSpace(c.Region)) c.Region = "ap-shanghai";
        if (string.IsNullOrWhiteSpace(c.Root)) c.Root = "scfa";
        if (string.IsNullOrWhiteSpace(c.ApiBaseUrl) && string.IsNullOrWhiteSpace(c.ApiDirectUrl)) c.ApiBaseUrl = "https://124.223.170.117:18443";
        if (string.IsNullOrWhiteSpace(c.UpdateChannel)) c.UpdateChannel = "stable";
        c.FavoriteContentKeys = (c.FavoriteContentKeys ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 200)
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToList();
        c.RecentContentKeys = (c.RecentContentKeys ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 200)
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        c.LoginAccounts = (c.LoginAccounts ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Account) && x.Account.Trim().Length <= 200)
            .Select(x => new LoginAccountRecord
            {
                Account = x.Account.Trim(),
                PasswordEncrypted = x.PasswordEncrypted?.Trim() ?? "",
                LastUsedAt = x.LastUsedAt == default ? DateTimeOffset.UtcNow : x.LastUsedAt
            })
            .GroupBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(item => item.LastUsedAt).First())
            .OrderByDescending(x => x.LastUsedAt)
            .Take(8)
            .ToList();
        c.LocalUserProfiles = (c.LocalUserProfiles ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.UserKey) && x.UserKey.Trim().Length <= 200)
            .Select(x => new LocalUserProfile
            {
                UserKey = x.UserKey.Trim(),
                DisplayName = (x.DisplayName ?? "").Trim()[..Math.Min((x.DisplayName ?? "").Trim().Length, 40)],
                QQ = (x.QQ ?? "").Trim()[..Math.Min((x.QQ ?? "").Trim().Length, 20)],
                Phone = (x.Phone ?? "").Trim()[..Math.Min((x.Phone ?? "").Trim().Length, 30)],
                AvatarPath = (x.AvatarPath ?? "").Trim()
            })
            .GroupBy(x => x.UserKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .Take(20)
            .ToList();
        if (c.LoginAccounts.Count == 0 && !string.IsNullOrWhiteSpace(c.LastLoginAccount))
        {
            c.LoginAccounts.Add(new LoginAccountRecord
            {
                Account = c.LastLoginAccount.Trim(),
                PasswordEncrypted = c.LoginPasswordEncrypted?.Trim() ?? "",
                LastUsedAt = DateTimeOffset.UtcNow
            });
        }
        if (c.LoginAccounts.Count == 0) c.AutoLogin = false;
    }
}
