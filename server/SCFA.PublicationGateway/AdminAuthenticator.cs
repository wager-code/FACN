using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SCFA.PublicationGateway;

public sealed record AdminIdentity(string UserId, string RoleKey);

public sealed class AdminAuthenticator(GatewayOptions options)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http = Http;
    private readonly TimeSpan _deadline = TimeSpan.FromSeconds(8);
    internal AdminAuthenticator(GatewayOptions options, HttpClient http, TimeSpan deadline) : this(options)
    {
        _http = http;
        _deadline = deadline;
    }

    public async Task<AdminIdentity?> VerifyAsync(HttpContext context, CancellationToken ct)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authorization.Length is < 20 or > 8192) return null;
        var token = authorization[7..].Trim();
        if (token.Length < 12 || token.Any(char.IsWhiteSpace)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, options.AccountBaseUrl.TrimEnd('/') + "/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            const int maxBytes = 64 * 1024;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > maxBytes) return null;
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await body.ReadAsync(buffer, deadline.Token);
                if (count == 0) break;
                if (bounded.Length + count > maxBytes) return null;
                bounded.Write(buffer, 0, count);
            }
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object) return null;
            var id = Get(user, "id");
            var role = Get(user, "role_key");
            var status = Get(user, "status");
            if (id.Length == 0 || role is not ("admin" or "super_admin") ||
                (status.Length > 0 && status != "active")) return null;
            return new AdminIdentity(id, role);
        }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new HttpRequestException("账号身份校验超时", null, HttpStatusCode.GatewayTimeout);
        }
    }

    private static string Get(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim().ToLowerInvariant() ?? "" : "";
}