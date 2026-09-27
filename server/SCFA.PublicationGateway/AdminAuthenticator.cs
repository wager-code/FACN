using System.Net.Http.Headers;
using System.Text.Json;

namespace SCFA.PublicationGateway;

public sealed record AdminIdentity(string UserId, string RoleKey);

public sealed class AdminAuthenticator(GatewayOptions options)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<AdminIdentity?> VerifyAsync(HttpContext context, CancellationToken ct)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authorization.Length is < 20 or > 8192) return null;
        var token = authorization[7..].Trim();
        if (token.Length < 12 || token.Any(char.IsWhiteSpace)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, options.AccountBaseUrl.TrimEnd('/') + "/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 64 * 1024) return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(body, new JsonDocumentOptions { MaxDepth = 16 }, ct);
        if (!doc.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return null;
        var id = Get(user, "id");
        var role = Get(user, "role_key");
        var status = Get(user, "status");
        if (id.Length == 0 || role is not ("admin" or "super_admin") ||
            (status.Length > 0 && status != "active")) return null;
        return new AdminIdentity(id, role);
    }

    private static string Get(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() ?? "" : "";
}