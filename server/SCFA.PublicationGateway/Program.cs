using System.Text.Json;
using SCFA.PublicationGateway;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18081");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});
var settings = new GatewayOptions();
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<AdminAuthenticator>();
builder.Services.AddSingleton<CosTransport>();
builder.Services.AddSingleton<PublicationCoordinator>();
var app = builder.Build();

app.MapGet("/publication-healthz", () => Results.Ok(new { ok = true, service = "scfa-publication" }));

app.MapGet("/v1/admin/publications/capabilities",
    async (HttpContext context, AdminAuthenticator auth, CredentialStore credentials, CancellationToken ct) =>
        await Guard(async () =>
        {
            if (await auth.VerifyAsync(context, ct) is null) return Results.Unauthorized();
            var configured = await credentials.ReadAsync(ct) is not null;
            return Results.Ok(new
            {
                available = configured,
                message = configured ? "服务器 COS 发布能力已就绪" : "管理员尚未在服务器配置 COS 写入凭据"
            });
        }, app.Logger));

app.MapGet("/v1/admin/cos/credentials/status",
    async (HttpContext context, AdminAuthenticator auth, CredentialStore credentials, CancellationToken ct) =>
        await Guard(async () =>
        {
            if (await auth.VerifyAsync(context, ct) is null) return Results.Unauthorized();
            var current = await credentials.ReadAsync(ct);
            return Results.Ok(new { configured = current is not null, updated_at = current?.UpdatedAt });
        }, app.Logger));

app.MapPost("/v1/admin/cos/credentials/rotate",
    async (HttpContext context, CredentialRotationRequest request, AdminAuthenticator auth,
        CredentialStore credentials, CosTransport cos, CancellationToken ct) =>
        await Guard(async () =>
        {
            var user = await auth.VerifyAsync(context, ct);
            if (user is null) return Results.Unauthorized();
            if (user.RoleKey != "super_admin") return Results.Forbid();
            if (request.SecretId.Length is < 8 or > 256 || request.SecretKey.Length is < 8 or > 256 ||
                request.SecretId.Any(char.IsWhiteSpace) || request.SecretKey.Any(char.IsWhiteSpace))
                throw new InvalidDataException("COS 密钥格式无效");
            var candidate = new CosCredentials(request.SecretId, request.SecretKey, DateTimeOffset.UtcNow);
            await cos.VerifyCredentialAsync(candidate, ct);
            await credentials.SaveAsync(candidate, ct);
            app.Logger.LogInformation("COS write credential rotated by administrator {UserId}", user.UserId);
            return Results.Ok(new { configured = true, updated_at = candidate.UpdatedAt });
        }, app.Logger));

app.MapPost("/v1/admin/publications/intents",
    async (HttpContext context, PublicationIntentRequest request, AdminAuthenticator auth,
        PublicationCoordinator publications, CancellationToken ct) =>
        await Guard(async () =>
        {
            var user = await auth.VerifyAsync(context, ct);
            if (user is null) return Results.Unauthorized();
            var result = await publications.CreateIntentAsync(request, user, ct);
            app.Logger.LogInformation("Publication intent created {PublicationId} by {UserId}", result.PublicationId, user.UserId);
            return Results.Ok(result);
        }, app.Logger));

app.MapPost("/v1/admin/publications/{publicationId}/commit",
    async (HttpContext context, string publicationId, PublicationCommitRequest request,
        AdminAuthenticator auth, PublicationCoordinator publications, CancellationToken ct) =>
        await Guard(async () =>
        {
            var user = await auth.VerifyAsync(context, ct);
            if (user is null) return Results.Unauthorized();
            var result = await publications.CommitAsync(publicationId, request, user, ct);
            app.Logger.LogInformation("Publication committed {PublicationId} by {UserId}", publicationId, user.UserId);
            return Results.Ok(result);
        }, app.Logger));

app.MapPost("/v1/admin/publications/unpublish",
    async (HttpContext context, UnpublishRequest request, AdminAuthenticator auth,
        PublicationCoordinator publications, CancellationToken ct) =>
        await Guard(async () =>
        {
            var user = await auth.VerifyAsync(context, ct);
            if (user is null) return Results.Unauthorized();
            var result = await publications.UnpublishAsync(request, user, ct);
            app.Logger.LogInformation("Content downlisted {Kind} {ContentId} by {UserId}",
                request.Kind, request.ContentId, user.UserId);
            return Results.Ok(result);
        }, app.Logger));

app.MapGet("/v1/admin/publications/archive",
    async (HttpContext context, string kind, AdminAuthenticator auth,
        PublicationCoordinator publications, CancellationToken ct) =>
        await Guard(async () =>
        {
            if (await auth.VerifyAsync(context, ct) is null) return Results.Unauthorized();
            return Results.Ok(await publications.ReadArchiveAsync(kind, ct));
        }, app.Logger));

app.MapPost("/v1/admin/publications/restore",
    async (HttpContext context, RestoreRequest request, AdminAuthenticator auth,
        PublicationCoordinator publications, CancellationToken ct) =>
        await Guard(async () =>
        {
            var user = await auth.VerifyAsync(context, ct);
            if (user is null) return Results.Unauthorized();
            var result = await publications.RestoreAsync(request, user, ct);
            app.Logger.LogInformation("Content restored {Kind} {ContentId} by {UserId}",
                request.Kind, request.ContentId, user.UserId);
            return Results.Ok(result);
        }, app.Logger));

app.Run();

static async Task<IResult> Guard(Func<Task<IResult>> work, ILogger logger)
{
    try { return await work(); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { message = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (CosTimeoutException ex) { return GatewayErrorResponses.CosTimeout(ex, logger); }
    catch (TimeoutException ex) { return Results.BadRequest(new { message = ex.Message }); }
    catch (CosRequestException ex)
    {
        logger.LogWarning("COS {Operation} failed with HTTP {Status} and code {CosCode}",
            ex.Operation, (int)ex.StatusCode!.Value, ex.CosCode ?? "unknown");
        var code = ex.CosCode is null ? "" : $"，错误码 {ex.CosCode}";
        return Results.Json(new { message = $"COS {ex.Operation} 请求失败：HTTP {(int)ex.StatusCode!.Value}{code}" }, statusCode: 502);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning("Publication upstream request failed with status {Status}", ex.StatusCode);
        return Results.Problem("云端或账号服务暂时不可用", statusCode: 503);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Publication operation failed");
        return Results.Problem("发布服务内部错误，请查看服务器日志", statusCode: 500);
    }
}
