using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

/// <summary>Local account history. Passwords use Windows user encryption; server accounts are never modified.</summary>
public sealed class LoginAccountHistoryService(ConfigService config, UserSessionService session)
{
    public async Task SaveAsync(string account, string password, bool rememberPassword, bool autoLogin)
    {
        account = account.Trim();
        if (account.Length is 0 or > 200) throw new ArgumentException("账号长度必须为 1–200 个字符。", nameof(account));
        var next = ConfigService.Clone(config.Current);
        next.LoginAccounts.RemoveAll(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
        var encrypted = rememberPassword ? LoginCredentialProtector.Protect(password) : "";
        next.LoginAccounts.Insert(0, new LoginAccountRecord
        {
            Account = account,
            PasswordEncrypted = encrypted,
            LastUsedAt = DateTimeOffset.UtcNow
        });
        next.RememberLoginAccount = true;
        next.RememberLoginPassword = rememberPassword;
        next.AutoLogin = rememberPassword && autoLogin;
        next.LastLoginAccount = account;
        next.LoginPasswordEncrypted = encrypted;
        await config.SaveAsync(next);
    }

    public async Task DeleteAsync(string account)
    {
        account = account.Trim();
        var next = ConfigService.Clone(config.Current);
        next.LoginAccounts.RemoveAll(x => string.Equals(x.Account, account, StringComparison.OrdinalIgnoreCase));
        var wasLastAccount = string.Equals(next.LastLoginAccount.Trim(), account, StringComparison.OrdinalIgnoreCase);
        if (wasLastAccount)
        {
            // Clear legacy mirrors as well, so configuration migration cannot restore a deleted account.
            next.LastLoginAccount = "";
            next.LoginPasswordEncrypted = "";
            next.RememberLoginPassword = false;
            next.AutoLogin = false;
        }
        next.RememberLoginAccount = true;
        await config.SaveAsync(next);
        if (wasLastAccount) session.Clear();
    }
}
