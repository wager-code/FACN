using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class SubmissionService(ConfigService config, AuthApiClient auth, GamePathService paths, LocalContentService local, TaskService tasks, LogService log)
{
    private const int MaxFiles = 100000;
    private const int MaxDrafts = 100;
    private const int MaxDraftStoreBytes = 1024 * 1024;
    private const long MaxBytes = CloudCatalogService.MaxPackageBytes;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private string DraftPath => Path.Combine(ConfigService.ResolveDataDirectory(), "Submissions", "drafts.json");
    private string UserPath => NormalizePath(config.Current.SubmissionApiPath, "/v1/submissions");
    private string AdminPath => NormalizePath(config.Current.SubmissionAdminPath, "/v1/admin/submissions");

    public async Task<IReadOnlyList<SubmissionRecord>> FetchMineAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var root = await auth.GetJsonAsync<JsonElement>(UserPath.TrimEnd('/') + "/mine", ct);
        return ExtractList<SubmissionRecord>(root, "submissions", "items", "data");
    }

    public async Task<IReadOnlyList<SubmissionRecord>> FetchForReviewAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var root = await auth.GetJsonAsync<JsonElement>(AdminPath, ct);
        return ExtractList<SubmissionRecord>(root, "submissions", "items", "data");
    }

    public SubmissionDraft CreateDefaultDraft(LocalContentEntry entry, string? author = null) => new()
    {
        ContentKey = InstallService.ContentKey(entry.Kind, entry.Id),
        Name = entry.Name,
        Version = entry.Version,
        Author = author?.Trim() ?? "",
        Category = entry.Kind == "地图" ? "地图" : "MOD"
    };

    public async Task<SubmissionDraft> LoadDraftAsync(LocalContentEntry entry, string? fallbackAuthor = null, CancellationToken ct = default)
    {
        await _draftGate.WaitAsync(ct);
        try
        {
            var key = InstallService.ContentKey(entry.Kind, entry.Id);
            var found = (await ReadDraftsUnsafeAsync(ct)).FirstOrDefault(x => string.Equals(x.ContentKey, key, StringComparison.OrdinalIgnoreCase));
            if (found is null) return CreateDefaultDraft(entry, fallbackAuthor);
            found.Name = string.IsNullOrWhiteSpace(found.Name) ? entry.Name : found.Name;
            found.Version = entry.Version;
            if (!string.IsNullOrWhiteSpace(found.PreviewPath) && !File.Exists(found.PreviewPath)) found.PreviewPath = "";
            return found;
        }
        finally { _draftGate.Release(); }
    }

    public async Task SaveDraftAsync(LocalContentEntry entry, SubmissionDraft draft, CancellationToken ct = default)
    {
        ValidateDraftForStorage(entry, draft);
        await _draftGate.WaitAsync(ct);
        try
        {
            var drafts = await ReadDraftsUnsafeAsync(ct);
            var key = InstallService.ContentKey(entry.Kind, entry.Id);
            drafts.RemoveAll(x => string.Equals(x.ContentKey, key, StringComparison.OrdinalIgnoreCase));
            draft.ContentKey = key;
            draft.Version = entry.Version;
            draft.SavedAt = DateTimeOffset.Now;
            drafts.Add(CloneDraft(draft));
            await WriteDraftsUnsafeAsync(drafts.OrderByDescending(x => x.SavedAt).Take(MaxDrafts).ToList(), ct);
        }
        finally { _draftGate.Release(); }
    }

    public async Task DeleteDraftAsync(LocalContentEntry entry, CancellationToken ct = default)
    {
        await _draftGate.WaitAsync(ct);
        try
        {
            var drafts = await ReadDraftsUnsafeAsync(ct);
            var key = InstallService.ContentKey(entry.Kind, entry.Id);
            if (drafts.RemoveAll(x => string.Equals(x.ContentKey, key, StringComparison.OrdinalIgnoreCase)) == 0) return;
            await WriteDraftsUnsafeAsync(drafts, ct);
        }
        finally { _draftGate.Release(); }
    }

    public Task<SubmissionRecord> SubmitAsync(LocalContentEntry entry, CancellationToken ct = default)
    {
        var draft = CreateDefaultDraft(entry, "未提供");
        draft.Description = "由 SCFA 内容中心客户端提交，未填写补充说明。";
        return SubmitAsync(entry, draft, ct);
    }

    public async Task<SubmissionRecord> SubmitAsync(LocalContentEntry entry, SubmissionDraft draft, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        ValidateLocalRoot(entry);
        var refreshed = await local.AnalyzeDirectoryAsync(entry.Root, ct);
        ValidateRefreshedContent(entry, refreshed);
        entry = refreshed;
        var validated = SubmissionValidator.Validate(draft, entry);
        if (validated.PreviewPath.Length > 0) ValidatePreviewImage(validated.PreviewPath);
        var retryDraft = CloneDraft(draft);
        var workRoot = Path.Combine(ConfigService.ResolveDataDirectory(), "Submissions", ".work_" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(workRoot, SafeLabel(entry.Id.Length > 0 ? entry.Id : entry.Folder) + ".zip");
        Directory.CreateDirectory(workRoot);
        var task = tasks.Create("投稿", $"{entry.Kind} · {entry.Name}", "准备打包");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        task.ConfigureCancellation(operationCts.Cancel);
        try
        {
            task.Status = "运行中";
            var contentHash = await ContentHash.DirectorySha256Async(entry.Root, operationCts.Token);
            task.Detail = "正在创建安全投稿 ZIP";
            var stats = await CreatePackageAsync(entry.Root, package, new Progress<int>(p => task.Progress = Math.Min(50, p / 2)), operationCts.Token);
            var packageHash = await ContentHash.FileSha256Async(package, operationCts.Token);
            var packageSize = new FileInfo(package).Length;
            if (packageSize <= 0 || packageSize > MaxBytes) throw new InvalidDataException("投稿 ZIP 大小超出 4GB 安全上限");
            task.Progress = 55;
            task.Detail = "正在上传到投稿服务";
            var metadata = new
            {
                kind = entry.Kind,
                content_id = entry.Id,
                name = validated.Name,
                version = validated.Version,
                author = validated.Author,
                description = validated.Description,
                category = validated.Category,
                tags = validated.Tags,
                folder_name = entry.Folder,
                content_sha256 = contentHash,
                package_sha256 = packageHash,
                size = packageSize,
                files = stats.Files,
                preview_present = validated.PreviewPath.Length > 0,
                preview_file_name = validated.PreviewPath.Length == 0 ? "" : Path.GetFileName(validated.PreviewPath),
                client_version = AppVersion.Informational
            };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json"), "metadata");
            var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var uploadProgress = new Progress<int>(p =>
            {
                task.Progress = 55 + Math.Min(40, p * 40 / 100);
                task.Detail = $"正在上传投稿包 · {p}%";
            });
            var file = new ProgressStreamContent(stream, packageSize, uploadProgress);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            file.Headers.ContentLength = packageSize;
            form.Add(file, "package", Path.GetFileName(package));
            if (validated.PreviewPath.Length > 0)
            {
                var previewInfo = new FileInfo(validated.PreviewPath);
                var previewStream = new FileStream(validated.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var preview = new StreamContent(previewStream);
                preview.Headers.ContentLength = previewInfo.Length;
                preview.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(Path.GetExtension(validated.PreviewPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
                form.Add(preview, "preview", previewInfo.Name);
            }
            var root = await auth.SendContentAsync<JsonElement>(HttpMethod.Post, UserPath, form, operationCts.Token);
            var submitted = ExtractOne<SubmissionRecord>(root, "submission", "data") ?? new SubmissionRecord
            {
                Kind = entry.Kind,
                ContentId = entry.Id,
                Name = validated.Name,
                Version = validated.Version,
                Author = validated.Author,
                Description = validated.Description,
                Category = validated.Category,
                Tags = validated.Tags.ToList(),
                Status = "已提交",
                Size = packageSize,
                Sha256 = packageHash,
                ContentSha256 = contentHash,
                Files = stats.Files
            };
            try { await DeleteDraftAsync(entry); }
            catch (Exception ex) { log.Error("清理已提交草稿失败: " + entry.Name, ex); }
            task.Progress = 100;
            task.Status = "完成";
            task.Detail = "投稿已发送，等待服务器审核";
            log.Info($"投稿已提交: {entry.Kind} {entry.Name} {entry.Version} files={stats.Files} bytes={packageSize}");
            return submitted;
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            task.Status = "已取消";
            task.Detail = "用户取消了投稿";
            task.ConfigureRetry(async () => { await SubmitAsync(entry, retryDraft); });
            throw;
        }
        catch (Exception ex)
        {
            task.Progress = 100;
            task.Status = "失败";
            task.Detail = ex.Message;
            task.ConfigureRetry(async () => { await SubmitAsync(entry, retryDraft); });
            log.Error("投稿失败: " + entry.Name, ex);
            throw;
        }
        finally
        {
            task.ConfigureCancellation(null);
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); } catch (Exception ex) { log.Error("清理投稿临时目录失败", ex); }
        }
    }

    public async Task ReviewAsync(string submissionId, string decision, string message, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        if (string.IsNullOrWhiteSpace(submissionId)) throw new ArgumentException("投稿 ID 为空");
        decision = decision.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject")) throw new ArgumentException("审核决定无效");
        var path = AdminPath.TrimEnd('/') + "/" + Uri.EscapeDataString(submissionId) + "/review";
        _ = await auth.PostJsonAsync<JsonElement>(path, new { decision, message = message?.Trim() ?? "" }, ct);
        log.Info($"投稿审核操作已发送: {submissionId} decision={decision}");
    }

    private async Task<(int Files, long Bytes)> CreatePackageAsync(string source, string destination, IProgress<int>? progress, CancellationToken ct)
    {
        var files = EnumerateFiles(source, ct);
        var totalBytes = files.Sum(x => x.Length);
        if (totalBytes > MaxBytes) throw new InvalidDataException("投稿内容超过 4GB 安全上限");
        var rootName = Path.GetFileName(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        long written = 0;
        foreach (var item in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, item.FullName).Replace('\\', '/');
            _ = SafeArchive.ValidateRelativePath(rootName + "/" + relative);
            var zipEntry = archive.CreateEntry(rootName + "/" + relative, CompressionLevel.Optimal);
            await using var input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = zipEntry.Open();
            await input.CopyToAsync(target, 128 * 1024, ct);
            written += item.Length;
            progress?.Report(totalBytes == 0 ? 100 : (int)Math.Min(100, written * 100d / totalBytes));
        }
        return (files.Count, totalBytes);
    }

    private static List<FileInfo> EnumerateFiles(string root, CancellationToken ct)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("投稿根目录是符号链接/重解析点");
        var result = new List<FileInfo>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                var attr = File.GetAttributes(item);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("投稿内容包含符号链接/重解析点：" + item);
                if ((attr & FileAttributes.Directory) != 0) stack.Push(item);
                else
                {
                    if (result.Count >= MaxFiles) throw new InvalidDataException("投稿文件数量超过安全上限");
                    var extension = Path.GetExtension(item).ToLowerInvariant();
                    if (extension is ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".scr" or ".lnk")
                        throw new InvalidDataException("投稿内容包含禁止执行文件：" + Path.GetFileName(item));
                    result.Add(new FileInfo(item));
                }
            }
        }
        return result.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ValidateLocalRoot(LocalContentEntry entry)
    {
        var contentRoot = Path.GetFullPath(paths.GetContentDirectory(entry.Kind)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var localRoot = Path.GetFullPath(entry.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!localRoot.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("投稿内容不在当前配置的玩家内容目录内");
    }

    private static void ValidateRefreshedContent(LocalContentEntry selected, LocalContentEntry refreshed)
    {
        if (!refreshed.Valid) throw new InvalidDataException("投稿前重新扫描失败：" + refreshed.Detail);
        if (!string.Equals(selected.Kind, refreshed.Kind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"投稿类型已变化：选择时为 {selected.Kind}，当前文件实际为 {refreshed.Kind}");
        if (!ContentIdentity.VersionsEquivalent(selected.Version, refreshed.Version))
            throw new InvalidDataException($"投稿内容在选择后发生变化：选择时版本 {selected.Version}，文件内当前版本 {refreshed.Version}。请刷新本地内容后重新投稿。");
        if (ContentIdentity.NormalizeKey(selected.Id) != ContentIdentity.NormalizeKey(refreshed.Id))
            throw new InvalidDataException($"投稿内容身份在选择后发生变化：选择时 ID {selected.Id}，当前文件 ID {refreshed.Id}。请刷新本地内容后重新投稿。");
        if (!string.Equals(Path.GetFullPath(selected.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           Path.GetFullPath(refreshed.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("投稿内容目录在选择后发生变化，请刷新后重新投稿");
    }

    public static (int Width, int Height) ValidatePreviewImage(string path)
    {
        path = SubmissionValidator.ValidatePreviewPath(path);
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            var isPng = signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var isJpeg = signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if ((extension == ".png" && !isPng) || (extension is ".jpg" or ".jpeg" && !isJpeg))
                throw new InvalidDataException("预览图扩展名与实际文件格式不一致");
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("预览图没有可用画面");
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width < 320 || height < 180) throw new InvalidDataException("预览图分辨率至少需要 320×180");
            if (width > 4096 || height > 4096) throw new InvalidDataException("预览图分辨率不能超过 4096×4096");
            return (width, height);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("预览图无法解码：" + ex.Message, ex); }
    }

    private async Task<List<SubmissionDraft>> ReadDraftsUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(DraftPath)) return [];
        try
        {
            if (new FileInfo(DraftPath).Length > MaxDraftStoreBytes) throw new InvalidDataException("投稿草稿文件超过 1MB 安全上限");
            var drafts = JsonSerializer.Deserialize<List<SubmissionDraft>>(await File.ReadAllTextAsync(DraftPath, ct), _json) ?? [];
            return drafts.Where(x => !string.IsNullOrWhiteSpace(x.ContentKey) && x.ContentKey.Length <= 200)
                .OrderByDescending(x => x.SavedAt)
                .Take(MaxDrafts)
                .Select(CloneDraft)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var invalid = DraftPath + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss.fff");
            try { File.Move(DraftPath, invalid, false); } catch { }
            log.Error("投稿草稿文件损坏，已隔离: " + invalid, ex);
            return [];
        }
    }

    private async Task WriteDraftsUnsafeAsync(List<SubmissionDraft> drafts, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
        var json = JsonSerializer.Serialize(drafts, new JsonSerializerOptions(_json) { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > MaxDraftStoreBytes) throw new InvalidDataException("投稿草稿文件超过 1MB 安全上限");
        var temp = DraftPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, json, ct);
            File.Move(temp, DraftPath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void ValidateDraftForStorage(LocalContentEntry entry, SubmissionDraft draft)
    {
        static void Limit(string? value, int max, string label)
        {
            if ((value ?? "").Length > max) throw new InvalidDataException($"{label}不能超过 {max} 个字符");
        }
        Limit(draft.Name, 120, "投稿名称");
        Limit(draft.Version, 80, "投稿版本");
        Limit(draft.Author, 80, "作者");
        Limit(draft.Description, 2000, "内容说明");
        Limit(draft.Category, 40, "分类");
        Limit(draft.TagsText, 500, "标签");
        Limit(draft.PreviewPath, 1000, "预览图路径");
        if (!string.IsNullOrWhiteSpace(draft.Version) && !ContentIdentity.VersionsEquivalent(draft.Version, entry.Version))
            throw new InvalidDataException("草稿版本必须与内容文件版本一致");
        _ = SubmissionValidator.ParseTags(draft.TagsText);
        if (!string.IsNullOrWhiteSpace(draft.PreviewPath)) ValidatePreviewImage(draft.PreviewPath);
    }

    private static SubmissionDraft CloneDraft(SubmissionDraft value) => new()
    {
        ContentKey = value.ContentKey,
        Name = value.Name,
        Version = value.Version,
        Author = value.Author,
        Description = value.Description,
        Category = value.Category,
        TagsText = value.TagsText,
        PreviewPath = value.PreviewPath,
        SavedAt = value.SavedAt
    };

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(auth.Token)) throw new InvalidOperationException("请先使用服务器账号登录，离线模式不能访问投稿服务");
    }
    private static string NormalizePath(string value, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!path.StartsWith('/') || path.StartsWith("//") || Uri.TryCreate(path, UriKind.Absolute, out _)) throw new InvalidOperationException("投稿 API 路径配置无效");
        return path.TrimEnd('/');
    }
    private static string SafeLabel(string value) => new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(80).ToArray());
    private IReadOnlyList<T> ExtractList<T>(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.Deserialize<List<T>>(_json) ?? [];
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value.Deserialize<List<T>>(_json) ?? [];
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) return items.Deserialize<List<T>>(_json) ?? [];
            }
        return [];
    }
    private T? ExtractOne<T>(JsonElement root, params string[] names) where T : class
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object) return value.Deserialize<T>(_json);
        return root.Deserialize<T>(_json);
    }
}
