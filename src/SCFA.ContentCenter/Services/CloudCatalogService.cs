using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class CloudCatalogService
{
    public const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private const int MaxManifestEntries = 10000;
    private const int MaxDownloadAttempts = 3;
    private const long DiskReserveBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadHeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(45);
    private readonly Func<AppConfig> _getConfig;
    // 清单使用独立的短超时；内容包允许长时间传输，只在连接或持续无数据时超时。
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    public CloudCatalogService(ConfigService config) : this(config, new HttpClient { Timeout = Timeout.InfiniteTimeSpan }) { }
    public CloudCatalogService(ConfigService config, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(config);
        _getConfig = () => config.Current;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }
    public CloudCatalogService(AppConfig config, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(config);
        _getConfig = () => config;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string PublicUrl(string key)
    {
        var c = _getConfig();
        var encoded = string.Join('/', key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"https://{c.Bucket}.cos.{c.Region}.myqcloud.com/{encoded}";
    }

    public async Task<IReadOnlyList<CloudContentEntry>> FetchAsync(string kind, CancellationToken ct = default)
    {
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind, nameof(kind));
        var root = _getConfig().Root.Trim('/');
        var key = kind == "地图" ? $"{root}/manifest/latest.json" : $"{root}/manifest/mods.json";
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(ManifestTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, PublicUrl(key) + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();
            var payload = await ReadLimitedAsync(response.Content, MaxManifestBytes, requestCts.Token);
            if (kind == "地图")
            {
                var manifest = JsonSerializer.Deserialize<MapManifest>(payload, _json) ?? new();
                ValidateEntries(manifest.Maps, "地图");
                return manifest.Maps;
            }
            else
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(payload, _json) ?? new();
                ValidateEntries(manifest.Mods, "MOD");
                return manifest.Mods;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && requestCts.IsCancellationRequested)
        {
            throw new TimeoutException($"获取{kind}云端清单超过 {ManifestTimeout.TotalSeconds:F0} 秒");
        }
    }

    private void Prepare(CloudContentEntry item, string kind)
    {
        item.Kind = kind;
        item.Aliases ??= [];
        item.Tags ??= [];
        if (!string.IsNullOrWhiteSpace(item.Thumbnail)) item.ThumbnailUrl = PublicUrl(item.Thumbnail);
    }

    public async Task DownloadAsync(string key, string destination, long maxBytes, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("下载对象路径为空", nameof(key));
        if (maxBytes <= 0 || maxBytes > MaxPackageBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException("下载目标文件已存在：" + destination);
        var state = new DownloadResumeState();
        try
        {
            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadAttemptAsync(key, destination, maxBytes, state, progress, ct);
                    progress?.Report(100);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsTransientDownloadError(ex))
                {
                    if (attempt == MaxDownloadAttempts)
                        throw new IOException($"下载连续失败 {MaxDownloadAttempts} 次：{ex.Message}", ex);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                }
            }
        }
        catch
        {
            try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            throw;
        }
    }

    private async Task DownloadAttemptAsync(
        string key,
        string destination,
        long maxBytes,
        DownloadResumeState state,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var offset = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (offset > maxBytes) throw new InvalidDataException($"下载临时文件超过安全上限：{offset} 字节");
        if (offset > 0 && string.IsNullOrWhiteSpace(state.EntityTag))
        {
            File.Delete(destination);
            offset = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, PublicUrl(key));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(state.EntityTag!));
        }

        HttpResponseMessage response;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(DownloadHeaderTimeout);
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && headerCts.IsCancellationRequested)
            {
                throw new TimeoutException($"下载连接超过 {DownloadHeaderTimeout.TotalSeconds:F0} 秒未响应");
            }
        }

        using (response)
        {
            if (offset > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(destination);
                state.EntityTag = null;
                throw new IOException("服务器拒绝续传范围，将重新完整下载");
            }
            response.EnsureSuccessStatusCode();

            if (offset == 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                throw new InvalidDataException("服务器在未请求续传时返回了部分内容");

            var append = offset > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange?.From != offset)
                throw new InvalidDataException("服务器返回的续传起点与本地临时文件不一致");
            if (!append) offset = 0;

            var responseTag = response.Headers.ETag;
            state.EntityTag = responseTag is { IsWeak: false } ? responseTag.ToString() : null;
            var contentLength = response.Content.Headers.ContentLength ?? -1;
            var total = append
                ? response.Content.Headers.ContentRange?.Length ?? (contentLength >= 0 ? offset + contentLength : -1)
                : contentLength;
            if (total > maxBytes) throw new InvalidDataException($"下载文件超过安全上限：{total} 字节");
            if (total > offset) EnsureAvailableSpace(destination, total - offset);

            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(
                destination,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            var readTotal = offset;
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(DownloadIdleTimeout);
            while (true)
            {
                int count;
                try
                {
                    count = await input.ReadAsync(buffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && idleCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"下载连续 {DownloadIdleTimeout.TotalSeconds:F0} 秒没有收到数据");
                }
                if (count <= 0) break;
                idleCts.CancelAfter(DownloadIdleTimeout);
                readTotal += count;
                if (readTotal > maxBytes) throw new InvalidDataException($"下载文件超过安全上限：{maxBytes} 字节");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                if (total > 0) progress?.Report((int)Math.Min(100, readTotal * 100d / total));
            }
            await output.FlushAsync(ct);
            if (total >= 0 && readTotal != total)
                throw new EndOfStreamException($"下载长度不完整：期望 {total}，实际 {readTotal}");
        }
    }

    private static bool IsTransientDownloadError(Exception error)
    {
        if (error is InvalidDataException) return false;
        if (error is TimeoutException or EndOfStreamException) return true;
        if (error is HttpRequestException http)
        {
            if (http.StatusCode is null) return true;
            var status = (int)http.StatusCode.Value;
            return status is 408 or 429 || status >= 500;
        }
        return error is IOException;
    }

    private sealed class DownloadResumeState
    {
        public string? EntityTag { get; set; }
    }

    private void ValidateEntries(List<CloudContentEntry> entries, string kind)
    {
        if (entries.Count > MaxManifestEntries) throw new InvalidDataException($"{kind}清单条目超过安全上限 {MaxManifestEntries}");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Version) || string.IsNullOrWhiteSpace(item.File))
                throw new InvalidDataException($"{kind}清单包含缺少 id/name/version/file 的条目");
            if (!ids.Add(item.Id.Trim())) throw new InvalidDataException($"{kind}清单包含重复 ID：{item.Id}");
            if (!string.IsNullOrWhiteSpace(item.FolderName) && !folders.Add(item.FolderName.Trim()))
                throw new InvalidDataException($"{kind}清单包含重复目标目录：{item.FolderName}。已停止读取整份清单，避免覆盖玩家内容");
            if (item.Size < 0 || item.Size > MaxPackageBytes) throw new InvalidDataException($"{kind} {item.Name} 的 size 超出安全范围");
            if (!IsOptionalSha256(item.Sha256) || !IsOptionalSha256(item.EffectiveContentHash))
                throw new InvalidDataException($"{kind} {item.Name} 的 SHA-256 格式无效");
            Prepare(item, kind);
        }
    }

    private static bool IsOptionalSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var hash = value.Trim();
        return hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxManifestBytes) throw new InvalidDataException("云端清单超过 16MB 安全上限");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var n = await input.ReadAsync(buffer, ct); if (n <= 0) break;
            if (output.Length + n > maxBytes) throw new InvalidDataException("云端清单超过 16MB 安全上限");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    private static void EnsureAvailableSpace(string destination, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < bytes + DiskReserveBytes)
            throw new IOException($"磁盘可用空间不足：至少需要 {(bytes + DiskReserveBytes) / 1024d / 1024d:F0} MB");
    }
}
