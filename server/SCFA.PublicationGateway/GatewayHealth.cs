using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SCFA.PublicationGateway;

public static class GatewayHealth
{
    private static readonly GatewayHealthResponse Response = FromAssembly(typeof(GatewayHealth).Assembly);

    public static IResult CreateResult() => Results.Ok(Response);
    public static GatewayHealthResponse CreateResponse() => Response;

    private static GatewayHealthResponse FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        return FromMetadata(
            metadata.FirstOrDefault(x => x.Key == "GatewayRelease")?.Value,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            metadata.FirstOrDefault(x => x.Key == "GatewayCommit")?.Value);
    }

    internal static GatewayHealthResponse FromMetadata(string? release, string? informationalVersion, string? commit)
    {
        var sourceVersion = informationalVersion?.Split('+', 2)[0];
        var version = SafeMatch(release, "^gateway-v[0-9]+$", 80)
            ? release!
            : SafeMatch(sourceVersion, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?$", 80)
                ? sourceVersion! : "unknown";
        var revision = SafeMatch(commit, "^[a-fA-F0-9]{40}$", 40)
            ? commit!.ToLowerInvariant() : "unknown";
        return new GatewayHealthResponse(true, "scfa-publication", version, revision);
    }

    private static bool SafeMatch(string? value, string pattern, int limit) =>
        value is { Length: > 0 } && value.Length <= limit &&
        Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
}

public sealed record GatewayHealthResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("commit")] string Commit);
