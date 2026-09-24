using System.Security.Cryptography;
using System.Text;

namespace SCFA.ContentCenter.Services;

public static class LoginCredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SCFA.ContentCenter.LoginCredential.v1");

    public static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static bool TryUnprotect(string encrypted, out string password)
    {
        password = "";
        if (string.IsNullOrWhiteSpace(encrypted) || encrypted.Length > 8192) return false;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
            password = Encoding.UTF8.GetString(plain);
            return password.Length > 0;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
}
