using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SCFA.PublicationGateway;

public sealed class PublicationCoordinator(GatewayOptions options, CredentialStore credentialStore, CosTransport cos)
{
    private const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private string TicketsPath => Path.Combine(options.DataDirectory, "tickets");
    private string TempPath => Path.Combine(options.DataDirectory, "temp");
    private string HistoryPath => Path.Combine(options.DataDirectory, "history");

    public async Task<PublicationArchiveResponse> ReadArchiveAsync(string kind, CancellationToken ct)
    {
        if (kind is not ("map" or "mod")) throw new InvalidDataException("未知内容类型");
        var credentials = await credentialStore.ReadAsync(ct)
            ?? throw new InvalidOperationException("COS 写入凭据不可用");
        var key = options.Root + "/manifest/" + (kind == "map" ? "latest.json" : "mods.json");
        var current = Encoding.UTF8.GetString(await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, ct));
        if (!Directory.Exists(HistoryPath)) return PublicationArchive.Build(kind, options.Root, current, []);
        var files = Directory.EnumerateFiles(HistoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Take(501).ToArray();
        var snapshots = new List<string>();
        long loadedBytes = 0;
        var truncated = files.Length > 500;
        foreach (var file in files.Take(500))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Length is <= 0 or > 16 * 1024 * 1024) continue;
            if (loadedBytes + file.Length > 64 * 1024 * 1024) { truncated = true; break; }
            try { snapshots.Add(await File.ReadAllTextAsync(file.FullName, ct)); }
            catch (IOException) { continue; }
            loadedBytes += file.Length;
        }
        return PublicationArchive.Build(kind, options.Root, current, snapshots, truncated);
    }

    public async Task<RestoreResponse> RestoreAsync(RestoreRequest request, AdminIdentity user, CancellationToken ct)
    {
        if (request.Kind is not ("map" or "mod") || string.IsNullOrWhiteSpace(request.ContentId) ||
            string.IsNullOrWhiteSpace(request.PackageKey) || string.IsNullOrWhiteSpace(request.PackageSha256))
            throw new InvalidDataException("恢复参数不完整");
        await _commitGate.WaitAsync(ct);
        try
        {
            // Complete the manifest operation even if the administrator disconnects.
            var operationCt = CancellationToken.None;
            var credentials = await credentialStore.ReadAsync(operationCt)
                ?? throw new InvalidOperationException("COS 写入凭据不可用");
            var key = options.Root + "/manifest/" + (request.Kind == "map" ? "latest.json" : "mods.json");
            var oldBytes = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            var archive = await ReadArchiveAsync(request.Kind, operationCt);
            var group = archive.Items.FirstOrDefault(x => x.Id.Equals(request.ContentId, StringComparison.OrdinalIgnoreCase));
            if (group is null || !group.Downlisted)
                throw new InvalidDataException("该内容不是已下架状态，请刷新档案");
            var version = group.Versions.FirstOrDefault(x =>
                string.Equals(x.Entry["file"]?.ToString(), request.PackageKey, StringComparison.Ordinal) &&
                string.Equals(x.Entry["sha256"]?.ToString(), request.PackageSha256, StringComparison.OrdinalIgnoreCase));
            if (version is null) throw new InvalidDataException("所选历史包不在服务器档案中");
            var nextBytes = RestoreManifest.Add(Encoding.UTF8.GetString(oldBytes), request.Kind, options.Root, version.Entry);
            await cos.VerifyObjectHashAsync(credentials, request.PackageKey,
                version.Entry["size"]?.GetValue<long>() ?? 0, request.PackageSha256.ToLowerInvariant(), operationCt);
            Directory.CreateDirectory(HistoryPath);
            var backup = Path.Combine(HistoryPath, DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") +
                "_restore_" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllBytesAsync(backup, oldBytes, operationCt);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var latest = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            if (CosTransport.Sha256(latest) != CosTransport.Sha256(oldBytes))
                throw new InvalidDataException("云端清单在恢复前发生变化，请刷新后重试");
            await cos.PutBytesAsync(credentials, key, nextBytes, operationCt);
            var hash = CosTransport.Sha256(nextBytes);
            var verified = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            if (CosTransport.Sha256(verified) != hash)
                throw new InvalidDataException("恢复清单写入后复核失败，请检查云端状态");
            return new RestoreResponse(hash, request.ContentId);
        }
        finally { _commitGate.Release(); }
    }

    public async Task<UnpublishResponse> UnpublishAsync(UnpublishRequest request, AdminIdentity user, CancellationToken ct)
    {
        // Publish and unpublish share the same manifest lock so this service cannot lose an update.
        await _commitGate.WaitAsync(ct);
        try
        {
            var operationCt = CancellationToken.None;
            var credentials = await credentialStore.ReadAsync(operationCt)
                ?? throw new InvalidOperationException("COS 写入凭据不可用");
            if (request.Kind is not ("map" or "mod")) throw new InvalidDataException("未知内容类型");
            var key = options.Root + "/manifest/" + (request.Kind == "map" ? "latest.json" : "mods.json");
            var oldBytes = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            var nextBytes = UnpublishManifest.Remove(Encoding.UTF8.GetString(oldBytes), request);
            var nextHash = CosTransport.Sha256(nextBytes);
            Directory.CreateDirectory(HistoryPath);
            var backup = Path.Combine(HistoryPath, DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") +
                "_unpublish_" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllBytesAsync(backup, oldBytes, operationCt);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // The archive is retained. Downlisting never removes package or thumbnail objects.
            var latestBytes = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            if (CosTransport.Sha256(latestBytes) != CosTransport.Sha256(oldBytes))
                throw new InvalidDataException("云端清单在下架前发生变化，请刷新后重试");
            await cos.PutBytesAsync(credentials, key, nextBytes, operationCt);
            var verified = await cos.GetBytesAsync(credentials, key, 16 * 1024 * 1024, operationCt);
            if (CosTransport.Sha256(verified) != nextHash)
                throw new InvalidDataException("下架清单写入后复核失败，请检查云端状态");
            return new UnpublishResponse(nextHash, request.ContentId);
        }
        finally { _commitGate.Release(); }
    }

    public async Task<PublicationIntentResponse> CreateIntentAsync(
        PublicationIntentRequest request, AdminIdentity user, CancellationToken ct)
    {
        var credentials = await credentialStore.ReadAsync(ct)
            ?? throw new InvalidOperationException("尚未配置服务器 COS 写入凭据");
        var current = Encoding.UTF8.GetString(await cos.GetBytesAsync(credentials, request.ManifestKey, 16 * 1024 * 1024, ct));
        _ = ManifestValidator.Validate(request, current, options);
        if (await cos.ExistsAsync(credentials, request.PackageKey, ct))
            throw new InvalidDataException("正式包对象键已经存在，禁止覆盖");
        if (request.ThumbnailKey is not null && await cos.ExistsAsync(credentials, request.ThumbnailKey, ct))
            throw new InvalidDataException("正式缩略图对象键已经存在，禁止覆盖");
        var id = Guid.NewGuid().ToString("D");
        var prefix = options.Root + "/publication-staging/" + id + "/";
        var ticket = new PublicationTicket(id, user.UserId, DateTimeOffset.UtcNow.AddHours(12), request,
            prefix + "package.zip", request.ThumbnailKey is null ? null : prefix + "thumbnail.png");
        Directory.CreateDirectory(TicketsPath);
        var ticketPath = TicketPath(id);
        await File.WriteAllTextAsync(ticketPath, JsonSerializer.Serialize(ticket), ct);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(ticketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new PublicationIntentResponse(
            id, ticket.PackageStagingKey, cos.SignedUrl(credentials, "PUT", ticket.PackageStagingKey, 43200),
            ticket.ThumbnailStagingKey,
            ticket.ThumbnailStagingKey is null ? null : cos.SignedUrl(credentials, "PUT", ticket.ThumbnailStagingKey, 43200));
    }

    public async Task<PublicationCommitResponse> CommitAsync(
        string publicationId, PublicationCommitRequest confirmation, AdminIdentity user, CancellationToken ct)
    {
        if (!Guid.TryParse(publicationId, out _)) throw new InvalidDataException("发布任务编号无效");
        await _commitGate.WaitAsync(ct);
        try
        {
            // Once commit begins, finish or recover even if the client disconnects.
            var operationCt = CancellationToken.None;
            var ticketPath = TicketPath(publicationId);
            if (!File.Exists(ticketPath)) throw new FileNotFoundException("发布任务不存在");
            var ticket = JsonSerializer.Deserialize<PublicationTicket>(await File.ReadAllTextAsync(ticketPath, operationCt))
                ?? throw new InvalidDataException("发布任务数据无效");
            if (ticket.UserId != user.UserId) throw new UnauthorizedAccessException("只能提交本人创建的发布任务");
            if (ticket.Completed) return new PublicationCommitResponse(ticket.Request.NextManifestSha256);
            if (DateTimeOffset.UtcNow > ticket.ExpiresAt) throw new TimeoutException("发布任务已过期，请重新打包");
            var request = ticket.Request;
            if (!string.Equals(confirmation.PackageSha256, request.PackageSha256, StringComparison.Ordinal) ||
                !string.Equals(confirmation.NextManifestSha256, request.NextManifestSha256, StringComparison.Ordinal))
                throw new InvalidDataException("提交确认哈希与发布意图不符");
            var credentials = await credentialStore.ReadAsync(operationCt)
                ?? throw new InvalidOperationException("COS 写入凭据不可用");
            var currentBytes = await cos.GetBytesAsync(credentials, request.ManifestKey, 16 * 1024 * 1024, operationCt);
            var current = Encoding.UTF8.GetString(currentBytes);
            var alreadyCommitted = CosTransport.Sha256(currentBytes) == request.NextManifestSha256;
            var target = alreadyCommitted
                ? ManifestValidator.TargetFromNext(request, options)
                : ManifestValidator.Validate(request, current, options);
            var packageExists = await cos.ExistsAsync(credentials, request.PackageKey, operationCt);
            var thumbnailExists = request.ThumbnailKey is not null &&
                await cos.ExistsAsync(credentials, request.ThumbnailKey, operationCt);

            Directory.CreateDirectory(TempPath);
            var downloadedPath = Path.Combine(TempPath, publicationId + ".zip");
            var verifiedCopyPath = Path.Combine(TempPath, publicationId + ".verify.zip");
            if (File.Exists(downloadedPath)) File.Delete(downloadedPath);
            if (File.Exists(verifiedCopyPath)) File.Delete(verifiedCopyPath);
            byte[]? thumbnail = null;
            try
            {
                await cos.DownloadToFileAsync(credentials, ticket.PackageStagingKey, downloadedPath, MaxPackageBytes, operationCt);
                await PackageValidator.ValidateAsync(downloadedPath, request, target, operationCt);
                if (ticket.ThumbnailStagingKey is not null)
                {
                    thumbnail = await cos.GetBytesAsync(credentials, ticket.ThumbnailStagingKey, 10 * 1024 * 1024, operationCt);
                    if (thumbnail.LongLength != request.ThumbnailSize ||
                        CosTransport.Sha256(thumbnail) != request.ThumbnailSha256 ||
                        !thumbnail.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidDataException("缩略图大小、格式或哈希不符");
                }
                if (!packageExists) await cos.PutFileAsync(credentials, request.PackageKey, downloadedPath, operationCt);
                await cos.DownloadToFileAsync(credentials, request.PackageKey, verifiedCopyPath, MaxPackageBytes, operationCt);
                await PackageValidator.ValidateAsync(verifiedCopyPath, request, target, operationCt);
                if (thumbnail is not null)
                {
                    if (thumbnailExists)
                    {
                        var existingThumbnail = await cos.GetBytesAsync(credentials, request.ThumbnailKey!, 10 * 1024 * 1024, operationCt);
                        if (!existingThumbnail.AsSpan().SequenceEqual(thumbnail))
                            throw new InvalidDataException("正式缩略图已存在且与发布内容不同，禁止覆盖");
                    }
                    else await cos.PutBytesAsync(credentials, request.ThumbnailKey!, thumbnail, operationCt);
                }
                if (alreadyCommitted)
                {
                    await MarkCompletedAsync(ticketPath, ticket, operationCt);
                    return new PublicationCommitResponse(request.NextManifestSha256);
                }
                Directory.CreateDirectory(HistoryPath);
                var backup = Path.Combine(HistoryPath, DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + publicationId + ".json");
                await File.WriteAllBytesAsync(backup, currentBytes, operationCt);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var nextBytes = Encoding.UTF8.GetBytes(request.NextManifest);
                await cos.PutBytesAsync(credentials, request.ManifestKey, nextBytes, operationCt);
                var published = await cos.GetBytesAsync(credentials, request.ManifestKey, 16 * 1024 * 1024, operationCt);
                if (CosTransport.Sha256(published) != request.NextManifestSha256)
                    throw new InvalidDataException("新清单提交后复核失败，需检查云端状态");
                await MarkCompletedAsync(ticketPath, ticket, operationCt);
                return new PublicationCommitResponse(request.NextManifestSha256);
            }
            finally
            {
                if (File.Exists(downloadedPath)) File.Delete(downloadedPath);
                if (File.Exists(verifiedCopyPath)) File.Delete(verifiedCopyPath);
            }
        }
        finally { _commitGate.Release(); }
    }

    private static async Task MarkCompletedAsync(string path, PublicationTicket ticket, CancellationToken ct)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(ticket with { Completed = true }), ct);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private string TicketPath(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("发布任务编号无效");
        return Path.Combine(TicketsPath, id + ".json");
    }
}
