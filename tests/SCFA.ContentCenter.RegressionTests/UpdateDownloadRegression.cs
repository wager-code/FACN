using System.IO;
using System.Net.Http;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class UpdateDownloadRegression
{
    public static async Task RunAsync(ConfigService config, TaskService tasks, LogService log, Action<bool, string> check)
    {
    var updaterPayload = new byte[2048];
    updaterPayload[0] = (byte)'M';
    updaterPayload[1] = (byte)'Z';
    for (var i = 2; i < updaterPayload.Length; i++) updaterPayload[i] = (byte)(i % 251);
    var updaterHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updaterPayload)).ToLowerInvariant();
    using var updaterAuth = new AuthApiClient("https://updates.example.test", "");
    var updater = new UpdateService(config, updaterAuth, tasks, log,
        _ => new HttpClient(new StaticPackageHandler(updaterPayload)));
    var updateInfo = new AppUpdateInfo
    {
        Available = true,
        MetadataComplete = true,
        LatestVersion = "4.0.0-regression-valid",
        DownloadUrl = "https://updates.example.test/client.exe",
        Size = updaterPayload.Length,
        Sha256 = updaterHash
    };
    try
    {
        var stagedUpdate = await updater.DownloadAsync(updateInfo);
        check(File.Exists(stagedUpdate) &&
              (await ContentHash.FileSha256Async(stagedUpdate)).Equals(updaterHash, StringComparison.OrdinalIgnoreCase),
            "客户端更新下载后关闭独占文件句柄并通过 SHA-256 与 PE 校验");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("更新下载回归异常: " + ex);
        check(false, "客户端更新下载后关闭独占文件句柄并通过 SHA-256 与 PE 校验");
    }
    var cachedWrongSize = new AppUpdateInfo
    {
        Available = true, MetadataComplete = true, LatestVersion = updateInfo.LatestVersion,
        DownloadUrl = updateInfo.DownloadUrl, Size = updaterPayload.Length + 1, Sha256 = updaterHash
    };
    await CheckThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(cachedWrongSize), check,
        "缓存更新包复用时仍须与当前清单大小一致");
    await CheckConcurrentDownloadAsync(config, updaterAuth, tasks, log, updaterPayload, updaterHash, check);
    var invalidUpdateInfo = new AppUpdateInfo
    {
        Available = true,
        MetadataComplete = true,
        LatestVersion = "4.0.0-regression-invalid",
        DownloadUrl = "https://updates.example.test/client.exe",
        Size = updaterPayload.Length,
        Sha256 = new string('0', 64)
    };
    await CheckThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(invalidUpdateInfo), check,
        "客户端更新拒绝 SHA-256 不匹配的下载包");
    check(!File.Exists(Path.Combine(updater.StagingRoot, invalidUpdateInfo.LatestVersion, "SCFA内容中心.exe")),
        "客户端更新校验失败后删除暂存文件");
    }

    private static async Task CheckConcurrentDownloadAsync(ConfigService config, AuthApiClient auth,
        TaskService tasks, LogService log, byte[] payload, string hash, Action<bool, string> check)
    {
        var handler = new GatedUpdateHandler(payload);
        var updater = new UpdateService(config, auth, tasks, log, _ => new HttpClient(handler, disposeHandler: false));
        var info = new AppUpdateInfo
        {
            Available = true, MetadataComplete = true, LatestVersion = "4.0.0-regression-concurrent",
            DownloadUrl = "https://updates.example.test/client.exe", Size = payload.Length, Sha256 = hash
        };
        var first = updater.DownloadAsync(info);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = updater.DownloadAsync(info);
        handler.Release.SetResult();
        var completed = false;
        try
        {
            var paths = await Task.WhenAll(first, second);
            completed = paths[0] == paths[1] && File.Exists(paths[0]) &&
                await ContentHash.FileSha256Async(paths[0]) == hash;
        }
        catch (IOException) { }
        check(completed && handler.Requests == 1,
            "并发下载同一客户端更新只传输一次且复用经过校验的完整包");
        handler.Dispose();
    }

    private sealed class GatedUpdateHandler(byte[] payload) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }
    }

    private static async Task CheckThrowsAsync<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    {
        try { await action(); check(false, name); }
        catch (T) { check(true, name); }
    }
}
