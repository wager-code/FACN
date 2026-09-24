using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed record SavedUserSession(
    UserInfo User,
    string Token,
    DateTimeOffset? ExpiresAt,
    string Authority,
    string CertFingerprint)
{
    public bool IsExpired => ExpiresAt is { } value && value <= DateTimeOffset.UtcNow.AddMinutes(1);

    public bool MatchesAuthority(string authority, string certFingerprint) =>
        !string.IsNullOrWhiteSpace(Authority) &&
        string.Equals(Authority, authority, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(CertFingerprint, certFingerprint, StringComparison.OrdinalIgnoreCase);
}

public sealed class UserSessionService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SCFA.ContentCenter.Session.v1");
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private readonly string _file;
    private readonly string _legacyFile;

    public UserSessionService()
    {
        var dataDirectory = ConfigService.ResolveDataDirectory();
        _file = Path.Combine(dataDirectory, "session.dat");
        _legacyFile = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SCFAContentCenter", "session.json")
            : Path.Combine(dataDirectory, "legacy-session.json");
    }

    public async Task SaveAsync(
        UserInfo user,
        string token,
        string? expiresAt = null,
        string? authority = null,
        string? certFingerprint = null)
    {
        token = token?.Trim() ?? "";
        if (token.Length == 0) throw new ArgumentException("登录会话 Token 为空", nameof(token));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        DateTimeOffset? expiry = null;
        if (DateTimeOffset.TryParse(expiresAt, out var parsed)) expiry = parsed.ToUniversalTime();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SessionPayload
        {
            User = user,
            Token = token,
            ExpiresAt = expiry,
            Authority = authority?.Trim() ?? "",
            CertFingerprint = certFingerprint?.Trim() ?? ""
        }, _json);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        var tmp = _file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, protectedBytes);
            File.Move(tmp, _file, true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public async Task<SavedUserSession?> LoadAsync()
    {
        if (!File.Exists(_file)) return await LoadLegacyAsync();
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_file);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var data = JsonSerializer.Deserialize<SessionPayload>(plain, _json);
            if (data?.User is null || string.IsNullOrWhiteSpace(data.Token)) return null;
            return new SavedUserSession(data.User, data.Token.Trim(), data.ExpiresAt, data.Authority, data.CertFingerprint);
        }
        catch
        {
            Clear();
            return null;
        }
    }

    public void Clear()
    {
        TryDelete(_file);
        TryDelete(_file + ".tmp");
        TryDelete(_legacyFile);
    }

    private async Task<SavedUserSession?> LoadLegacyAsync()
    {
        if (!File.Exists(_legacyFile)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<SessionPayload>(await File.ReadAllBytesAsync(_legacyFile), _json);
            if (data?.User is null || string.IsNullOrWhiteSpace(data.Token)) return null;
            await SaveAsync(data.User, data.Token, data.ExpiresAt?.ToString("O"), data.Authority, data.CertFingerprint);
            TryDelete(_legacyFile);
            return new SavedUserSession(data.User, data.Token.Trim(), data.ExpiresAt, data.Authority, data.CertFingerprint);
        }
        catch
        {
            TryDelete(_legacyFile);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class SessionPayload
    {
        public UserInfo? User { get; set; }
        public string Token { get; set; } = "";
        public DateTimeOffset? ExpiresAt { get; set; }
        public string Authority { get; set; } = "";
        public string CertFingerprint { get; set; } = "";
    }
}
