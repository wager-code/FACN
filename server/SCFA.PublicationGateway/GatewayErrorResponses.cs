using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SCFA.PublicationGateway;

internal static class GatewayErrorResponses
{
    internal static IResult CosTimeout(CosTimeoutException ex, ILogger logger)
    {
        logger.LogWarning("COS {Operation} timed out after {TimeoutSeconds} seconds",
            ex.Operation, ex.Timeout.TotalSeconds);
        return Results.Json(new { message = $"COS {ex.Operation} 请求超时，请稍后重试" }, statusCode: 504);
    }
}
