using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class UpdateService(ConfigService config, AuthApiClient auth, TaskService tasks, LogService log)
{
    private const long MaxUpdateBytes = 1024L * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    public string StagingRoot { get; } = Path.Combine(ConfigService.ResolveDataDirectory(), "Updates");

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var channel = NormalizeChannel(config.Current.UpdateChannel);
        AppUpdateManifest manifest;
        if (!string.IsNullOrWhiteSpace(config.Current.UpdateManifestUrl))
        {
            manifest = await FetchManifestAsync(config.Current.UpdateManifestUrl.Trim(), ct);
        }
        else
        {
            var health = await auth.HealthAsync(ct);
            if (!health.UpdaterReady)
                return Empty(channel, "服务端更新功能尚未就绪");
            manifest = new AppUpdateManifest
            {
                Version = health.UpdaterVersion,
                Channel = channel,
                Url = health.UpdaterUrl,
                Sha256 = health.UpdaterSha256,
                Size = health.UpdaterSize,
                Notes = health.UpdaterNotes,
                PublishedAt = health.UpdaterPublishedAt
            };
        }

        if (string.IsNullOrWhiteSpace(manifest.Version)) return Empty(channel, "更新服务正常，当前通道尚未发布客户端版本");
        if (!string.IsNullOrWhiteSpace(manifest.Channel) && !NormalizeChannel(manifest.Channel).Equals(channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新清单通道 {manifest.Channel} 与当前通道 {channel} 不一致");
        var comparison = ContentIdentity.CompareVersions(AppVersion.Informational, manifest.Version);
        if (!comparison.Ordered) throw new InvalidDataException($"无法安全比较客户端版本 {AppVersion.Informational} 与 {manifest.Version}");
        var available = comparison.Compare < 0;
        var metadataComplete = IsSha256(manifest.Sha256) && manifest.Size is > 0 and <= MaxUpdateBytes && IsSafeDownloadUri(manifest.Url);
        var status = available
            ? metadataComplete ? "发现可安装的新版本" : "发现新版本，但服务端下载元数据不完整"
            : comparison.Compare == 0 ? "当前已是最新版本" : "当前客户端高于更新通道版本";
        return new AppUpdateInfo
        {
            CurrentVersion = AppVersion.Informational,
            LatestVersion = manifest.Version.Trim(),
            Channel = channel,
            DownloadUrl = manifest.Url.Trim(),
            Sha256 = manifest.Sha256.Trim().ToLowerInvariant(),
            Size = manifest.Size,
            Notes = manifest.Notes,
            PublishedAt = manifest.PublishedAt,
            Available = available,
            MetadataComplete = metadataComplete,
            Status = status
        };
    }

    public async Task<string> DownloadAsync(AppUpdateInfo info, CancellationToken ct = default)
    {
        if (!info.Available || !info.MetadataComplete) throw new InvalidOperationException("当前没有可安全下载的客户端更新");
        var versionDirectory = Path.Combine(StagingRoot, SafeLabel(info.LatestVersion));
        var destination = Path.Combine(versionDirectory, "SCFA内容中心.exe");
        Directory.CreateDirectory(versionDirectory);
        if (File.Exists(destination))
        {
            var existingHash = await ContentHash.FileSha256Async(destination, ct);
            if (existingHash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) return destination;
            File.Delete(destination);
        }

        var task = tasks.Create("客户端更新", $"SCFA 内容中心 {info.LatestVersion}", "准备下载");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        task.ConfigureCancellation(operationCts.Cancel);
        try
        {
            task.Status = "运行中";
            var uri = ResolveDownloadUri(info.DownloadUrl);
            using var client = CreateHttpClient(uri);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, operationCts.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.Size;
            if (total <= 0 || total > MaxUpdateBytes || (info.Size > 0 && total != info.Size)) throw new InvalidDataException("更新包响应大小与清单不一致");
            EnsureDiskSpace(destination, total);
            await using var input = await response.Content.ReadAsStreamAsync(operationCts.Token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long read = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, operationCts.Token);
                if (count == 0) break;
                read += count;
                if (read > MaxUpdateBytes || read > info.Size) throw new InvalidDataException("更新包超过清单大小");
                await output.WriteAsync(buffer.AsMemory(0, count), operationCts.Token);
                task.Progress = (int)Math.Min(95, read * 95d / total);
                task.Detail = $"正在下载 · {read / 1024d / 1024d:F1} / {total / 1024d / 1024d:F1} MB";
            }
            if (read != info.Size) throw new EndOfStreamException($"更新包下载不完整：期望 {info.Size}，实际 {read}");
            task.Detail = "正在校验 SHA-256";
            var hash = await ContentHash.FileSha256Async(destination, operationCts.Token);
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败");
            ValidatePortableExecutable(destination);
            task.Progress = 100;
            task.Status = "完成";
            task.Detail = "更新已安全下载，等待安装";
            log.Info($"客户端更新已下载: {info.LatestVersion} -> {destination}");
            return destination;
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            task.Status = "已取消";
            task.Detail = "用户取消了更新下载";
            TryDelete(destination);
            throw;
        }
        catch (Exception ex)
        {
            task.Status = "失败";
            task.Progress = 100;
            task.Detail = ex.Message;
            TryDelete(destination);
            log.Error("客户端下载更新失败", ex);
            throw;
        }
        finally { task.ConfigureCancellation(null); }
    }

    public async Task BeginApplyAsync(string stagedPath, AppUpdateInfo info, CancellationToken ct = default)
    {
        stagedPath = Path.GetFullPath(stagedPath);
        EnsureUnderStaging(stagedPath);
        if (!File.Exists(stagedPath)) throw new FileNotFoundException("更新暂存程序不存在", stagedPath);
        if (!IsSha256(info.Sha256)) throw new InvalidDataException("更新 SHA-256 元数据无效");
        var stagedHash = await ContentHash.FileSha256Async(stagedPath, ct);
        if (!stagedHash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新暂存程序在下载后发生变化，已阻止执行");
        ValidatePortableExecutable(stagedPath);
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前客户端路径");
        if (string.Equals(stagedPath, current, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新暂存程序不能与当前程序相同");
        var start = new ProcessStartInfo(stagedPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(stagedPath)! };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(current);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(info.Sha256);
        _ = Process.Start(start) ?? throw new InvalidOperationException("无法启动更新应用程序");
        log.Info($"已启动客户端更新应用: {info.LatestVersion}");
    }

    private async Task<AppUpdateManifest> FetchManifestAsync(string rawUrl, CancellationToken ct)
    {
        var uri = ResolveDownloadUri(rawUrl);
        using var client = CreateHttpClient(uri);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxManifestBytes) throw new InvalidDataException("更新清单超过 1MB 安全上限");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > MaxManifestBytes) throw new InvalidDataException("更新清单超过 1MB 安全上限");
            output.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<AppUpdateManifest>(output.ToArray(), _json) ?? throw new InvalidDataException("更新清单 JSON 无效");
    }

    private HttpClient CreateHttpClient(Uri uri)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var apiUri = new Uri(auth.BaseUrl);
        if (uri.Scheme == Uri.UriSchemeHttps && uri.Authority.Equals(apiUri.Authority, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(auth.PinnedCertSha256))
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => ValidatePinnedCertificate(certificate, auth.PinnedCertSha256);
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    private Uri ResolveDownloadUri(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) && Uri.TryCreate(new Uri(auth.BaseUrl + "/"), raw, out var relative)) uri = relative;
        if (uri is null || !IsSafeDownloadUri(uri.ToString())) throw new InvalidDataException("更新下载地址必须是 HTTPS 绝对地址");
        return uri;
    }

    private static bool IsSafeDownloadUri(string raw) => Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    private static bool IsSha256(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 64 && value.Trim().All(Uri.IsHexDigit);
    private static string NormalizeChannel(string value) => value.Trim().ToLowerInvariant() switch { "developer" or "dev" => "developer", "beta" => "beta", _ => "stable" };
    private static AppUpdateInfo Empty(string channel, string status) => new() { CurrentVersion = AppVersion.Informational, Channel = channel, Status = status };
    private static string SafeLabel(string value) => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_').Take(80).ToArray());
    private void EnsureUnderStaging(string path)
    {
        var root = Path.GetFullPath(StagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新暂存路径越界");
    }
    private static void ValidatePortableExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 1024 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("更新包不是有效 Windows 可执行程序");
    }
    private static bool ValidatePinnedCertificate(X509Certificate2? certificate, string expected)
    {
        if (certificate is null) return false;
        var got = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(got), Encoding.ASCII.GetBytes(expected));
    }
    private static void EnsureDiskSpace(string destination, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (!string.IsNullOrWhiteSpace(root) && root.Length >= 2 && root[1] == ':')
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + 256L * 1024 * 1024) throw new IOException("更新暂存磁盘空间不足");
        }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
