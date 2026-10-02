using SCFA.ContentCenter.Models;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

internal static class ContentPackageFixtures
{
    public static byte[] CreateMapPackage(string folder)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(archive, $"{folder}/{folder}.scmap", "map-bytes");
            AddText(archive, $"{folder}/{folder}_save.lua", "Scenario = {}");
            AddText(archive, $"{folder}/{folder}_script.lua", "function OnPopulate() end");
            AddText(archive, $"{folder}/{folder}_scenario.lua", $"""
    name = "Regression Map"
    map_version = 1
    map = "/maps/{folder}/{folder}.scmap"
    save = "/maps/{folder}/{folder}_save.lua"
    script = "/maps/{folder}/{folder}_script.lua"
    """);
        }
        return output.ToArray();
    }

    public static byte[] CreateModPackage(string folder)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddText(archive, $"{folder}/mod_info.lua", "name = \"Regression Mod\"\nuid = \"sync_mod\"\nversion = 1\n");
            AddText(archive, $"{folder}/hook/units.lua", "return {}\n");
        }
        return output.ToArray();
    }

    private static void AddText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

}

sealed class StaticPackageHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Headers.ETag = new EntityTagHeaderValue("\"static-package-v1\"");
        return Task.FromResult(response);
    }
}

sealed class CatalogPackageHandler(MapManifest manifest, byte[] package) : HttpMessageHandler
{
    private readonly byte[] _manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/manifest/latest.json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifest) });
        if (path.EndsWith("/packages/sync-map.zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class ModCatalogPackageHandler(ModManifest manifest, byte[] package) : HttpMessageHandler
{
    private readonly byte[] _manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/manifest/mods.json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifest) });
        if (path.EndsWith("/packages/sync-mod.zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

