using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Core;

public static class AccessPolicy
{
    public static bool CanReadUsers(UserInfo user) => Has(user, "users.read", "users.manage", "users.write");
    public static bool CanManageUsers(UserInfo user) => Has(user, "users.manage", "users.write");
    public static bool CanRevokeSessions(UserInfo user) => Has(user, "sessions.revoke", "users.manage", "users.write");
    public static bool CanReadAudit(UserInfo user) => Has(user, "audit.read");
    public static bool CanUnpublish(UserInfo user) => Has(user, "content.manage", "content.unpublish");
    public static bool CanOpenAdminWorkspace(UserInfo user) => Has(user, "users.read", "users.manage", "users.write", "audit.read", "server.read");
    public static bool CanManageSettings(UserInfo user) => Has(user, "settings.cloud");

    private static bool Has(UserInfo user, params string[] permissions)
    {
        if (user is null) return false;
        var role = user.RoleKey?.Trim();
        if (string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "super_admin", StringComparison.OrdinalIgnoreCase)) return true;
        return (user.Permissions ?? []).Any(value =>
            string.Equals(value, "*", StringComparison.OrdinalIgnoreCase) ||
            permissions.Any(expected => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)));
    }
}
