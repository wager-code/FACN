using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SCFA.PublicationGateway;

internal static class GatewayHealthRegression
{
    public static async Task RunAsync()
    {
        var revision = new string('A', 40);
        var release = GatewayHealth.FromMetadata("gateway-v62", "4.0.0-dev61", revision);
        Check(release.Version == "gateway-v62" && release.Commit == revision.ToLowerInvariant(),
            "健康接口使用编译时 Release 标签和提交号");
        var development = GatewayHealth.FromMetadata(null, "4.0.0-dev61+" + revision, null);
        Check(development.Version == "4.0.0-dev61" && development.Commit == "unknown",
            "本地构建明确显示源码版本和未知提交号");
        var unsafeMetadata = GatewayHealth.FromMetadata("/private/deployment/path",
            "secret-and-config-value", "https://private-host/token");
        Check(unsafeMetadata.Version == "unknown" && unsafeMetadata.Commit == "unknown",
            "健康接口拒绝路径、地址和非版本元数据");
        var expectedVersion = Environment.GetEnvironmentVariable("SCFA_TEST_HEALTH_VERSION");
        if (!string.IsNullOrEmpty(expectedVersion))
            Check(GatewayHealth.CreateResponse().Version == expectedVersion &&
                  GatewayHealth.CreateResponse().Commit == Environment.GetEnvironmentVariable("SCFA_TEST_HEALTH_COMMIT"),
                "编译后的程序集确实包含注入的 Release 和 Git 提交号");
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        await GatewayHealth.CreateResult().ExecuteAsync(context);
        using var json = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        var fields = json.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Check(context.Response.StatusCode == 200 &&
              fields.SequenceEqual(new[] { "commit", "ok", "service", "version" }) &&
              json.RootElement.GetProperty("ok").GetBoolean() &&
              json.RootElement.GetProperty("service").GetString() == "scfa-publication" &&
              json.RootElement.GetProperty("version").GetString() == GatewayHealth.CreateResponse().Version,
            "健康响应仅公开四个固定字段并保留原有存活契约");
    }

    private static void Check(bool condition, string title)
    {
        if (!condition) throw new Exception("FAIL " + title);
        Console.WriteLine("PASS " + title);
    }
}
