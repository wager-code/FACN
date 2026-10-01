using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class AuthAuthorityRegression
{
    internal static void Run(Action<bool, string> check)
    {
        using var auth = new AuthApiClient("https://accounts.example.test/tenantA", "");
        auth.SetToken("fixture-token-not-real");
        auth.Reconfigure("https://accounts.example.test/tenantA/", "");
        check(auth.Token.Length > 0, "等价账号连接配置保留当前登录令牌");
        auth.Reconfigure("https://different.example.test/tenantA", "");
        check(auth.Token.Length == 0, "账号客户端自身在服务器变化时清除旧令牌");
        auth.SetToken("fixture-token-not-real");
        auth.Reconfigure("https://different.example.test/tenanta", "");
        check(auth.Token.Length == 0, "账号API路径大小写变化不会沿用旧令牌");
        auth.SetToken("fixture-token-not-real");
        auth.Reconfigure(auth.BaseUrl, new string('a', 64));
        check(auth.Token.Length == 0, "证书身份变化时账号客户端自身清除旧令牌");
        auth.SetToken("fixture-token-not-real");
        var original = auth.BaseUrl;
        try { auth.Reconfigure("not-a-url", ""); } catch (ArgumentException) { }
        check(auth.BaseUrl == original && auth.Token.Length > 0, "无效新连接不会破坏原账号客户端和令牌");
        var saved = new SavedUserSession(new UserInfo(), "fixture-token-not-real", null,
            "https://accounts.example.test/tenantA", "");
        check(!saved.MatchesAuthority("https://accounts.example.test/tenanta", ""), "保存的会话严格区分账号API路径大小写");
        check(saved.MatchesAuthority("https://ACCOUNTS.EXAMPLE.TEST/tenantA/", ""), "保存的会话允许主机大小写和尾斜线的等价规范地址");
        foreach (var url in new[] { "https://fixture@accounts.example.test", "https://accounts.example.test?route=other",
                     "https://accounts.example.test#other", "ftp://localhost/resource", "file://localhost/resource" })
        {
            var rejected = false;
            try { AuthApiClient.NormalizeBaseUrl(url); } catch (ArgumentException) { rejected = true; }
            check(rejected, "账号连接拒绝含凭据、查询、片段或非HTTP协议：" + (url.Contains('@') ? "userinfo" : url.Split(':')[0]));
        }
    }
}