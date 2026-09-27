using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCFA.ContentCenter.Core;
using SCFA.PublicationGateway;

var root = Path.Combine(Path.GetTempPath(), "scfa-publication-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var options = new GatewayOptions
    {
        Bucket = "examplebucket-1250000000",
        Region = "ap-shanghai",
        Root = "scfa",
        DataDirectory = Path.Combine(root, "data"),
        AccountBaseUrl = "http://127.0.0.1:18080",
        MasterKey = RandomNumberGenerator.GetBytes(32)
    };
    options.Validate();
    var credentials = new CosCredentials("AKIDEXAMPLEID1234", "test-only-secret-key-not-real", DateTimeOffset.UtcNow);
    var store = new CredentialStore(options);
    await store.SaveAsync(credentials);
    var loaded = await store.ReadAsync();
    Check(loaded?.SecretId == credentials.SecretId && loaded.SecretKey == credentials.SecretKey, "服务器加密凭据可安全读取");
    var encryptedText = await File.ReadAllTextAsync(Path.Combine(options.DataDirectory, "cos-credentials.json"));
    Check(!encryptedText.Contains(credentials.SecretKey, StringComparison.Ordinal), "凭据文件不含 COS 明文密钥");

    var cos = new CosTransport(options);
    var signed = cos.SignedUrl(credentials, "PUT", "scfa/publication-staging/test/package.zip", 600);
    var url = new Uri(signed);
    Check(url.Scheme == "https" && url.Host == "examplebucket-1250000000.cos.ap-shanghai.myqcloud.com" &&
          url.AbsolutePath == "/scfa/publication-staging/test/package.zip" && url.Query.Contains("q-signature", StringComparison.Ordinal),
          "官方 SDK 生成的签名上传 URL 绑定指定桶与对象");

    var folder = "test_map";
    var mapRoot = Path.Combine(root, folder);
    Directory.CreateDirectory(mapRoot);
    await File.WriteAllTextAsync(Path.Combine(mapRoot, folder + ".scmap"), "map-bytes");
    await File.WriteAllTextAsync(Path.Combine(mapRoot, folder + "_save.lua"), "Scenario = {}");
    await File.WriteAllTextAsync(Path.Combine(mapRoot, folder + "_script.lua"), "function OnPopulate() end");
    await File.WriteAllTextAsync(Path.Combine(mapRoot, folder + "_scenario.lua"),
        "ScenarioInfo = {\n  name = 'Test Map',\n  map_version = 2,\n  map = '/maps/test_map/test_map.scmap',\n" +
        "  save = '/maps/test_map/test_map_save.lua',\n  script = '/maps/test_map/test_map_script.lua',\n}\n");
    var zipPath = Path.Combine(root, "package.zip");
    ZipFile.CreateFromDirectory(mapRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
    var packageHash = await ContentHash.FileSha256Async(zipPath);
    var contentHash = await ContentHash.DirectorySha256Async(mapRoot);
    var packageKey = "scfa/maps/test_map/test_map-2-" + contentHash[..12] + ".zip";
    var oldManifest = JsonSerializer.Serialize(new { manifest_version = 1, maps = Array.Empty<object>(), preserved_field = true });
    var nextManifest = JsonSerializer.Serialize(new
    {
        manifest_version = 1,
        maps = new[]
        {
            new { id = folder, name = "Test Map", version = "2", game_version = "2", folder_name = folder,
                file = packageKey, sha256 = packageHash, content_sha256 = contentHash, size = new FileInfo(zipPath).Length }
        },
        preserved_field = true
    });
    var request = new PublicationIntentRequest("map", packageKey, packageHash, new FileInfo(zipPath).Length,
        contentHash, "scfa/manifest/latest.json", CosTransport.Sha256(oldManifest),
        nextManifest, CosTransport.Sha256(nextManifest), null, null, null);
    var target = ManifestValidator.Validate(request, oldManifest, options);
    Check(target.Folder == folder && target.GameVersion == "2", "服务端独立识别待发布地图和游戏版本");
    await PackageValidator.ValidateAsync(zipPath, request, target, CancellationToken.None);
    Check(true, "服务端重新验证 ZIP 路径、引用文件、版本和目录内容指纹");
    var legacy = JsonNode.Parse("""{"id":"old_map","name":"Old Map","version":"1","folder_name":"Old Map","file":"scfa/maps/old_map/1.zip"}""")!;
    var oldWithSpaces = JsonNode.Parse(oldManifest)!.AsObject();
    oldWithSpaces["maps"]!.AsArray().Add(legacy.DeepClone());
    var nextWithSpaces = JsonNode.Parse(nextManifest)!.AsObject();
    nextWithSpaces["maps"]!.AsArray().Insert(0, legacy.DeepClone());
    var oldWithSpacesText = oldWithSpaces.ToJsonString();
    var nextWithSpacesText = nextWithSpaces.ToJsonString();
    ManifestValidator.Validate(request with
    {
        OriginalManifestSha256 = CosTransport.Sha256(oldWithSpacesText),
        NextManifest = nextWithSpacesText,
        NextManifestSha256 = CosTransport.Sha256(nextWithSpacesText)
    }, oldWithSpacesText, options);
    Check(true, "既有清单的含空格游戏目录可以继续发布新内容");
    await ExpectInvalidAsync(() => PackageValidator.ValidateAsync(zipPath,
        request with { ContentSha256 = new string('0', 64) }, target, CancellationToken.None),
        "服务端拒绝 ZIP 内容指纹错误");
    await ExpectInvalidAsync(() => Task.Run(() => ManifestValidator.Validate(
        request with { PackageKey = "scfa/maps/other/overwrite.zip" }, oldManifest, options)),
        "服务端拒绝清单指向不同正式对象");
    Console.WriteLine("全部服务端隔离回归通过。");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAIL " + title);
    Console.WriteLine("PASS " + title);
}

static async Task ExpectInvalidAsync(Func<Task> action, string title)
{
    try { await action(); }
    catch (InvalidDataException) { Console.WriteLine("PASS " + title); return; }
    throw new Exception("FAIL " + title);
}