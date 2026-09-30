using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    using var timeoutHttp = new HttpClient(new HangingCosHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    var timeoutCos = new CosTransport(options, timeoutHttp, TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
    using var safetyCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try
    {
        await timeoutCos.ExistsAsync(credentials, "scfa/manifest/latest.json", safetyCancellation.Token);
        Check(false, "COS 元数据请求在服务端无响应时有明确超时");
    }
    catch (TimeoutException)
    {
        Check(!safetyCancellation.IsCancellationRequested, "COS 元数据请求在服务端无响应时有明确超时");
    }
    catch (OperationCanceledException)
    {
        Check(false, "COS 元数据请求在服务端无响应时有明确超时");
    }
    using var bodyHttp = new HttpClient(new HangingCosBodyHandler(HttpStatusCode.OK))
        { Timeout = Timeout.InfiniteTimeSpan };
    var bodyCos = new CosTransport(options, bodyHttp, TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
    using var bodySafety = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try
    {
        await bodyCos.GetBytesAsync(credentials, "scfa/manifest/latest.json", 1024, bodySafety.Token);
        Check(false, "COS 已收到响应头但正文停滞时仍会超时");
    }
    catch (CosTimeoutException ex)
    {
        Check(ex.Operation == "GET" && !bodySafety.IsCancellationRequested,
            "COS 已收到响应头但正文停滞时仍会超时");
    }

    using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    var cancelCos = new CosTransport(options, timeoutHttp, TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    try
    {
        await cancelCos.ExistsAsync(credentials, "scfa/manifest/latest.json", callerCancellation.Token);
        Check(false, "调用方取消 COS 请求不会被误报为服务端超时");
    }
    catch (CosTimeoutException)
    {
        Check(false, "调用方取消 COS 请求不会被误报为服务端超时");
    }
    catch (OperationCanceledException)
    {
        Check(callerCancellation.IsCancellationRequested,
            "调用方取消 COS 请求不会被误报为服务端超时");
    }

    var transferPayload = Encoding.UTF8.GetBytes("COS large-transfer deadline regression payload");
    using var delayedHttp = new HttpClient(new DelayedCosHandler(transferPayload, TimeSpan.FromMilliseconds(120)))
        { Timeout = Timeout.InfiniteTimeSpan };
    var transferCos = new CosTransport(options, delayedHttp, TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(2));
    var transferPath = Path.Combine(root, "cos-transfer-policy.bin");
    await transferCos.DownloadToFileAsync(credentials, "scfa/test/package.zip", transferPath,
        transferPayload.Length + 1, CancellationToken.None);
    Check((await File.ReadAllBytesAsync(transferPath)).SequenceEqual(transferPayload),
        "COS 大包下载使用独立较长期限并保持内容完整");
    await transferCos.VerifyObjectHashAsync(credentials, "scfa/test/package.zip",
        transferPayload.Length, CosTransport.Sha256(transferPayload), CancellationToken.None);
    await transferCos.PutFileAsync(credentials, "scfa/test/package.zip", transferPath, CancellationToken.None);
    Check(true, "COS 大包复核和上传不受元数据短超时影响");

    using var errorBodyHttp = new HttpClient(new HangingCosBodyHandler(HttpStatusCode.Forbidden))
        { Timeout = Timeout.InfiniteTimeSpan };
    var errorBodyCos = new CosTransport(options, errorBodyHttp, TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));
    try
    {
        await errorBodyCos.GetBytesAsync(credentials, "scfa/manifest/latest.json", 1024, CancellationToken.None);
        Check(false, "COS 错误正文停滞也会按超时处理");
    }
    catch (CosTimeoutException ex)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await GatewayErrorResponses.CosTimeout(ex,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).ExecuteAsync(context);
        Check(context.Response.StatusCode == 504 &&
              !Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray())
                  .Contains("scfa/manifest/latest.json", StringComparison.Ordinal),
            "COS 错误正文停滞返回安全的 HTTP 504");
    }
    var signed = cos.SignedUrl(credentials, "PUT", "scfa/publication-staging/test/package.zip", 600);
    var url = new Uri(signed);
    Check(url.Scheme == "https" && url.Host == "examplebucket-1250000000.cos.ap-shanghai.myqcloud.com" &&
          url.AbsolutePath == "/scfa/publication-staging/test/package.zip" && url.Query.Contains("q-signature", StringComparison.Ordinal),
          "官方 SDK 生成的签名上传 URL 绑定指定桶与对象");

    using (var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("<Error><Code>SignatureDoesNotMatch</Code><Message>private-path-and-secret</Message></Error>", Encoding.UTF8)
    })
    {
        var failure = await CosRequestException.FromResponseAsync(forbidden, "PUT", CancellationToken.None);
        Check(failure.StatusCode == HttpStatusCode.Forbidden && failure.CosCode == "SignatureDoesNotMatch" &&
              !failure.Message.Contains("private-path-and-secret", StringComparison.Ordinal),
              "COS 拒绝时仅报告状态和安全的错误码，不泄露响应详情");
    }

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
    var downlist = new UnpublishRequest("map", folder, "管理员撤回此地图", "test");
    var downlisted = JsonNode.Parse(UnpublishManifest.Remove(nextManifest, downlist))!.AsObject();
    Check(downlisted["maps"]!.AsArray().Count == 0 &&
          downlisted["preserved_field"]!.GetValue<bool>() &&
          downlisted["updated_at"] is not null, "下架地图只移除目标记录并保留清单其他字段");
    var archive = PublicationArchive.Build("map", "scfa", Encoding.UTF8.GetString(UnpublishManifest.Remove(nextManifest, downlist)),
        [nextManifest, oldManifest]);
    Check(archive.Items.Count == 1 && archive.Items[0].Downlisted &&
          archive.Items[0].Versions.Count == 1 && archive.Items[0].Versions[0].State == "downlisted",
          "已下架地图仍可从清单备份查看，重复快照不制造假版本");
    var restored = JsonNode.Parse(RestoreManifest.Add(
        Encoding.UTF8.GetString(UnpublishManifest.Remove(nextManifest, downlist)),
        "map", "scfa", archive.Items[0].Versions[0].Entry))!.AsObject();
    Check(restored["maps"]!.AsArray().Count == 1 &&
          restored["maps"]![0]!["sha256"]!.GetValue<string>() == packageHash &&
          restored["preserved_field"]!.GetValue<bool>(),
          "恢复已下架地图保留原记录与无关清单字段");
    await ExpectInvalidAsync(() => Task.Run(() => RestoreManifest.Add(
        nextManifest, "map", "scfa", archive.Items[0].Versions[0].Entry)),
        "现有正式版本不得被历史恢复覆盖");
    var unsafeArchive = (JsonObject)archive.Items[0].Versions[0].Entry.DeepClone();
    unsafeArchive["folder_name"] = "..";
    await ExpectInvalidAsync(() => Task.Run(() => RestoreManifest.Add(
        oldManifest, "map", "scfa", unsafeArchive)), "不安全的历史目录不得恢复");
    var historicalMap = (JsonObject)JsonNode.Parse(nextManifest)!.DeepClone();
    historicalMap["maps"]![0]!["version"] = "3";
    historicalMap["maps"]![0]!["file"] = "scfa/maps/test_map/test_map-3.zip";
    var multiVersion = PublicationArchive.Build("map", "scfa", historicalMap.ToJsonString(), [nextManifest]);
    Check(multiVersion.Items.Count == 1 && !multiVersion.Items[0].Downlisted &&
          multiVersion.Items[0].Versions.Select(x => x.Entry["version"]!.GetValue<string>()).SequenceEqual(["3", "2"]),
          "当前地图和旧版按版本记录一同显示");
    await ExpectInvalidAsync(() => Task.Run(() => UnpublishManifest.Remove(nextManifest,
        downlist with { ContentId = "other_map" })), "不存在的地图 ID 不得下架");
    var duplicate = JsonNode.Parse(nextManifest)!.AsObject();
    duplicate["maps"]!.AsArray().Add(duplicate["maps"]!.AsArray()[0]!.DeepClone());
    await ExpectInvalidAsync(() => Task.Run(() => UnpublishManifest.Remove(duplicate.ToJsonString(), downlist)),
        "重复 ID 的清单不得模糊下架");
    var modsManifest = """{"mods":[{"id":"mod_a","file":"a.zip"},{"id":"mod_b","file":"b.zip"}],"preserved_field":true}""";
    var downlistedMods = JsonNode.Parse(UnpublishManifest.Remove(modsManifest,
        new UnpublishRequest("mod", "mod_a", "管理员撤回此 MOD", "test")))!.AsObject();
    Check(downlistedMods["mods"]!.AsArray().Count == 1 &&
          downlistedMods["mods"]![0]!["id"]!.GetValue<string>() == "mod_b",
          "下架 MOD 保留未选中的 MOD");
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
    var oldWithLegacy = JsonNode.Parse(oldManifest)!.AsObject();
    var nextWithLegacy = JsonNode.Parse(nextManifest)!.AsObject();
    foreach (var legacyId in new[] { "6_fields_of_isis", "xxx_survival" })
    {
        var legacyWithoutFolder = JsonNode.Parse($$"""{"id":"{{legacyId}}","name":"Legacy Map","version":"1","file":"scfa/maps/{{legacyId}}/1.zip"}""")!;
        oldWithLegacy["maps"]!.AsArray().Add(legacyWithoutFolder.DeepClone());
        nextWithLegacy["maps"]!.AsArray().Insert(nextWithLegacy["maps"]!.AsArray().Count - 1, legacyWithoutFolder.DeepClone());
    }
    var oldWithLegacyText = oldWithLegacy.ToJsonString();
    var nextWithLegacyText = nextWithLegacy.ToJsonString();
    var legacyRequest = request with
    {
        OriginalManifestSha256 = CosTransport.Sha256(oldWithLegacyText),
        NextManifest = nextWithLegacyText,
        NextManifestSha256 = CosTransport.Sha256(nextWithLegacyText)
    };
    ManifestValidator.Validate(legacyRequest, oldWithLegacyText, options);
    Check(true, "未改动的旧地图记录缺少 folder_name 时仍可发布新内容");
    var changedLegacy = (JsonObject)nextWithLegacy.DeepClone();
    changedLegacy["maps"]!.AsArray()[0]!["name"] = "Changed Legacy Map";
    var changedLegacyText = changedLegacy.ToJsonString();
    await ExpectInvalidAsync(() => Task.Run(() => ManifestValidator.Validate(legacyRequest with
    {
        NextManifest = changedLegacyText,
        NextManifestSha256 = CosTransport.Sha256(changedLegacyText)
    }, oldWithLegacyText, options)), "缺少目录字段的旧记录不得趁发布时修改");
    var newWithoutFolder = JsonNode.Parse(nextManifest)!.AsObject();
    newWithoutFolder["maps"]!.AsArray()[0]!.AsObject().Remove("folder_name");
    var newWithoutFolderText = newWithoutFolder.ToJsonString();
    await ExpectInvalidAsync(() => Task.Run(() => ManifestValidator.Validate(request with
    {
        NextManifest = newWithoutFolderText,
        NextManifestSha256 = CosTransport.Sha256(newWithoutFolderText)
    }, oldManifest, options)), "新发布条目必须明确指定 folder_name");
    var oldTargetWithoutFolder = JsonNode.Parse(oldManifest)!.AsObject();
    oldTargetWithoutFolder["maps"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{folder}}","name":"Old Map","version":"1","game_version":"1","file":"scfa/maps/{{folder}}/1.zip"}"""));
    var oldTargetWithoutFolderText = oldTargetWithoutFolder.ToJsonString();
    ManifestValidator.Validate(request with { OriginalManifestSha256 = CosTransport.Sha256(oldTargetWithoutFolderText) },
        oldTargetWithoutFolderText, options);
    Check(true, "更新同 ID 旧地图时可将缺失的 folder_name 补为内容 ID");
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

sealed class HangingCosHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The test COS request unexpectedly completed.");
    }
}
sealed class HangingCosBodyHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StreamContent(new HangingReadStream())
        });
}

sealed class DelayedCosHandler(byte[] payload, TimeSpan delay) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        if (request.Method == HttpMethod.Put)
        {
            var sent = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (!sent.SequenceEqual(payload)) throw new InvalidDataException("Test upload payload changed.");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
    }
}

sealed class HangingReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}