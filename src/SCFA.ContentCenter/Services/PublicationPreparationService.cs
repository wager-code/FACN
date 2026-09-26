using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed record PublicationMetadata(string Name, string ReleaseVersion, string Author, string Description, string Category, string TagsText);

public sealed record PublicationBundle(string Directory, string PackagePath, string ManifestPath, string OriginalManifestPath,
    string PackageKey, string ManifestKey, string PackageSha256, string ContentSha256, string OriginalManifestSha256,
    long PackageSize, int FileCount, string? ThumbnailPath, string? ThumbnailKey);

/// <summary>Creates locally checked administrator publication materials. This class never writes to COS.</summary>
public sealed class PublicationPreparationService(LocalContentService local)
{
    private const int MaxFiles = 100000;
    private const long MaxBytes = CloudCatalogService.MaxPackageBytes;
    private static readonly Regex ReleaseVersionPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.Compiled);

    public async Task<PublicationBundle> PrepareAsync(LocalContentEntry selected, PublicationMetadata metadata,
        string manifestText, string outputRoot, AppConfig config, UserInfo user, CancellationToken ct = default)
    {
        if (!AccessPolicy.CanPublishContent(user)) throw new UnauthorizedAccessException("只有管理员账号可以准备发布材料");
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(config);
        if (selected.Kind is not ("地图" or "MOD")) throw new InvalidDataException("请选择地图或 MOD");
        var refreshed = await local.AnalyzeDirectoryAsync(selected.Root, ct);
        if (!refreshed.Valid || refreshed.IsSharedMap) throw new InvalidDataException("该内容不是可独立发布的完整地图或 MOD：" + refreshed.Detail);
        if (!string.Equals(refreshed.Kind, selected.Kind, StringComparison.Ordinal) ||
            !string.Equals(refreshed.Id, selected.Id, StringComparison.OrdinalIgnoreCase) ||
            !ContentIdentity.VersionsEquivalent(refreshed.Version, selected.Version))
            throw new InvalidDataException("本地内容在选择后已变化，请刷新列表再准备发布");

        var name = Required(metadata.Name, "发布名称", 120);
        var release = Required(metadata.ReleaseVersion, "发布版本", 80);
        if (!ReleaseVersionPattern.IsMatch(release)) throw new InvalidDataException("发布版本只能使用字母、数字、点、短横线和下划线");
        var author = Required(metadata.Author, "作者", 120);
        var description = Required(metadata.Description, "说明", 2000);
        var category = Required(metadata.Category, "分类", 80);
        var tags = metadata.TagsText.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length > 10 || tags.Any(tag => tag.Length > 40)) throw new InvalidDataException("标签最多 10 个，每个不超过 40 字");

        var root = config.Root.Trim('/');
        if (root.Length == 0 || root.Split('/').Any(part => part.Length == 0 || SafeArchive.ValidateRelativePath(part) != part))
            throw new InvalidDataException("COS 根路径配置无效");
        var manifestKey = $"{root}/manifest/{(refreshed.Kind == "地图" ? "latest.json" : "mods.json")}";
        var listKey = refreshed.Kind == "地图" ? "maps" : "mods";
        var (manifest, entries, replaceIndex) = ParseAndCheckManifest(manifestText, listKey, refreshed, release);
        var files = EnumerateFiles(refreshed.Root, ct);
        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes > MaxBytes) throw new InvalidDataException("内容超过 4GB 安全上限");
        var outputPath = Path.GetFullPath(outputRoot);
        var drive = new DriveInfo(Path.GetPathRoot(outputPath)!);
        if (drive.AvailableFreeSpace < totalBytes + 256L * 1024 * 1024)
            throw new IOException("发布暂存目录空间不足，至少需要内容大小加 256MB 余量");
        var contentHash = await ContentHash.DirectorySha256Async(refreshed.Root, ct);

        var stage = Path.Combine(outputPath, DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        var packageName = $"{refreshed.Id}-{release}-{contentHash[..12]}.zip";
        var packagePath = Path.Combine(stage, packageName);
        var thumbnailPath = Path.Combine(stage, "thumbnail.png");
        try
        {
            await CreateAndVerifyZipAsync(refreshed.Root, files, packagePath, ct);
            var packageSize = new FileInfo(packagePath).Length;
            if (packageSize is <= 0 or > MaxBytes) throw new InvalidDataException("生成的 ZIP 大小无效");
            var packageHash = await ContentHash.FileSha256Async(packagePath, ct);
            if (!string.Equals(contentHash, await ContentHash.DirectorySha256Async(refreshed.Root, ct), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("打包过程中源目录发生变化，请重新准备发布");

            var packageKey = $"{root}/{(refreshed.Kind == "地图" ? "maps" : "mods")}/{refreshed.Id}/{packageName}";
            string? thumbnailKey = null;
            if (refreshed.Kind == "地图" && TryWriteThumbnail(refreshed.Root, thumbnailPath))
                thumbnailKey = $"{root}/thumbnails/{refreshed.Id}-{release}-{contentHash[..12]}.png";

            var old = replaceIndex >= 0 ? (JsonObject)entries[replaceIndex]!.DeepClone() : new JsonObject();
            old["id"] = refreshed.Id;
            old["name"] = name;
            old["game_name"] = refreshed.Name;
            old["version"] = release;
            old["game_version"] = refreshed.Version;
            old["folder_name"] = refreshed.Folder;
            old["sha256"] = packageHash;
            old["content_sha256"] = contentHash;
            old["size"] = packageSize;
            old["file"] = packageKey;
            old["author"] = author;
            old["description"] = description;
            old["category"] = category;
            old["tags"] = JsonSerializer.SerializeToNode(tags);
            old["published_at"] = DateTimeOffset.UtcNow.ToString("O");
            if (thumbnailKey is not null) old["thumbnail"] = thumbnailKey;
            if (replaceIndex >= 0) entries[replaceIndex] = old;
            else entries.Add(old);
            manifest["updated_at"] = DateTimeOffset.UtcNow.ToString("O");

            var originalPath = Path.Combine(stage, "manifest-before.json");
            var nextPath = Path.Combine(stage, "manifest-next.json");
            await File.WriteAllTextAsync(originalPath, manifestText, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(nextPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), ct);
            var originalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestText))).ToLowerInvariant();
            var instructions = $"此目录只是待上传材料，尚未发布。\n\n1. 在 COS 控制台上传 {packageName} 到对象键：{packageKey}\n" +
                (thumbnailKey is null ? "" : $"2. 上传 thumbnail.png 到对象键：{thumbnailKey}\n") +
                $"3. 上传 manifest-next.json 覆盖对象键：{manifestKey}\n" +
                "上传清单前重新下载线上清单，确认 SHA-256 仍与下列原始清单一致；如已变化，重新生成材料。\n" +
                $"原始清单 SHA-256：{originalHash}\nZIP SHA-256：{packageHash}\n内容 SHA-256：{contentHash}\n" +
                "上传后从软件安装到隔离目录，再次同步应跳过；保留 manifest-before.json 供回滚。\n";
            await File.WriteAllTextAsync(Path.Combine(stage, "发布步骤.txt"), instructions, new UTF8Encoding(false), ct);
            return new PublicationBundle(stage, packagePath, nextPath, originalPath, packageKey, manifestKey,
                packageHash, contentHash, originalHash, packageSize, files.Count,
                thumbnailKey is null ? null : thumbnailPath, thumbnailKey);
        }
        catch
        {
            foreach (var path in new[] { packagePath, thumbnailPath, Path.Combine(stage, "manifest-before.json"),
                         Path.Combine(stage, "manifest-next.json"), Path.Combine(stage, "发布步骤.txt") })
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { Directory.Delete(stage); } catch { }
            throw;
        }
    }

    private static (JsonObject Manifest, JsonArray Entries, int ReplaceIndex) ParseAndCheckManifest(
        string raw, string listKey, LocalContentEntry item, string release)
    {
        var manifest = JsonNode.Parse(raw) as JsonObject ?? throw new InvalidDataException("云端清单不是 JSON 对象");
        var entries = manifest[listKey] as JsonArray ?? throw new InvalidDataException("云端清单缺少 " + listKey + " 列表");
        if (entries.Count >= 10000) throw new InvalidDataException("云端清单条目已达到安全上限");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replace = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JsonObject old) throw new InvalidDataException("云端清单存在无效条目");
            var id = old["id"]?.GetValue<string>()?.Trim() ?? "";
            var folder = old["folder_name"]?.GetValue<string>()?.Trim() ?? id;
            if (id.Length == 0 || !ids.Add(id) || folder.Length == 0 || !folders.Add(folder))
                throw new InvalidDataException("云端清单存在空 ID、重复 ID 或重复目标目录");
            if (id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (!folder.Equals(item.Folder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("相同内容 ID 已指向不同的游戏目录，禁止覆盖");
                var oldRelease = old["version"]?.GetValue<string>() ?? "";
                var order = ContentIdentity.CompareVersions(release, oldRelease);
                if (!order.Ordered || order.Compare <= 0)
                    throw new InvalidDataException($"新发布版本 {release} 必须明确高于线上版本 {oldRelease}");
                var oldGameVersion = old["game_version"]?.GetValue<string>() ?? oldRelease;
                var gameOrder = ContentIdentity.CompareVersions(item.Version, oldGameVersion);
                if (!gameOrder.Ordered || gameOrder.Compare < 0)
                    throw new InvalidDataException($"游戏内部版本 {item.Version} 低于或无法安全比较线上版本 {oldGameVersion}");
                replace = index;
            }
            else if (folder.Equals(item.Folder, StringComparison.OrdinalIgnoreCase) ||
                     ContentIdentity.NormalizeKey(id) == ContentIdentity.NormalizeKey(item.Id))
                throw new InvalidDataException("云端已有其他 ID 使用同一目标目录或相同内容身份");
        }
        return (manifest, entries, replace);
    }

    private static async Task CreateAndVerifyZipAsync(string source, List<FileInfo> files, string destination, CancellationToken ct)
    {
        var rootName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _ = SafeArchive.ValidateRelativePath(rootName);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, file.FullName).Replace('\\', '/');
                var zipPath = rootName + "/" + relative;
                _ = SafeArchive.ValidateRelativePath(zipPath);
                var entry = zip.CreateEntry(zipPath, CompressionLevel.Optimal);
                await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                await using var target = entry.Open();
                await input.CopyToAsync(target, ct);
            }

        using var check = ZipFile.OpenRead(destination);
        if (check.Entries.Count != files.Count) throw new InvalidDataException("生成 ZIP 后文件数量不一致");
        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            var expectedPath = rootName + "/" + Path.GetRelativePath(source, file.FullName).Replace('\\', '/');
            var archived = check.Entries[index];
            if (!archived.FullName.Equals(expectedPath, StringComparison.Ordinal) || archived.Length != file.Length)
                throw new InvalidDataException("生成 ZIP 后文件名或大小不一致");
            if (archived.Length > 64L * 1024 * 1024 && archived.CompressedLength > 0 &&
                archived.Length / (double)archived.CompressedLength > 1000)
                throw new InvalidDataException("生成的 ZIP 包含安装器会拒绝的异常压缩比文件：" + file.Name);
            await using var stream = archived.Open();
            var archivedHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            await using var original = File.OpenRead(file.FullName);
            var originalHash = Convert.ToHexString(await SHA256.HashDataAsync(original, ct));
            if (!archivedHash.Equals(originalHash, StringComparison.Ordinal))
                throw new InvalidDataException("生成 ZIP 后文件内容不一致：" + file.Name);
        }
    }

    private static List<FileInfo> EnumerateFiles(string root, CancellationToken ct)
    {
        var files = new List<FileInfo>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("内容目录包含符号链接/重解析点");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("内容包含符号链接/重解析点：" + path);
                if ((attributes & FileAttributes.Directory) != 0) stack.Push(path);
                else
                {
                    if (files.Count >= MaxFiles) throw new InvalidDataException("内容文件数超过安全上限");
                    if (Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".scr" or ".lnk")
                        throw new InvalidDataException("内容包含禁止的执行文件：" + Path.GetFileName(path));
                    files.Add(new FileInfo(path));
                }
            }
        }
        return files.OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryWriteThumbnail(string root, string output)
    {
        var bitmap = MapPreviewService.TryLoad(root);
        if (bitmap is null) return false;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(file);
        return true;
    }

    private static string Required(string? value, string label, int max)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is 0 or > 2000 || normalized.Length > max || normalized.Any(char.IsControl))
            throw new InvalidDataException($"{label}不能为空、不能包含控制字符，且不得超过 {max} 字");
        return normalized;
    }
}
