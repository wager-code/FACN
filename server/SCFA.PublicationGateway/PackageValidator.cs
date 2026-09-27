using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Core;

namespace SCFA.PublicationGateway;

public static class PackageValidator
{
    private const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;

    public static async Task ValidateAsync(string zipPath, PublicationIntentRequest request, PublicationTarget target, CancellationToken ct)
    {
        var info = new FileInfo(zipPath);
        if (info.Length != request.PackageSize ||
            !string.Equals(await HashFileAsync(zipPath, ct), request.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("COS 暂存 ZIP 的大小或 SHA-256 不符");
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is < 1 or > 100000) throw new InvalidDataException("ZIP 文件数无效");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentFiles = new List<(string Relative, string Sha)>();
        ZipArchiveEntry? infoLua = null;
        var scmapCount = 0;
        var scenarioCount = 0;
        var modInfoCount = 0;
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = entry.FullName.Replace('\\', '/');
            if (full.EndsWith('/') || SafeArchive.ValidateRelativePath(full).Length == 0 ||
                !full.StartsWith(target.Folder + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP 必须仅含目标内容顶层目录内的文件");
            if (full.Split('/').Any(part => part.IndexOfAny(['<' , '>', '"', '|', '?', '*']) >= 0))
                throw new InvalidDataException("ZIP 包含 Windows 非法路径字符");
            if (!paths.Add(full)) throw new InvalidDataException("ZIP 包含重复文件路径");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP 包含符号链接");
            var relative = full[(target.Folder.Length + 1)..];
            if (relative.Length == 0) throw new InvalidDataException("ZIP 包含空文件名");
            if (System.IO.Path.GetExtension(relative).ToLowerInvariant() is ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".scr" or ".lnk")
                throw new InvalidDataException("ZIP 包含禁止的可执行文件");
            expandedBytes += entry.Length;
            if (expandedBytes > MaxUnpackedBytes || entry.Length < 0 ||
                (entry.Length > 64L * 1024 * 1024 && entry.CompressedLength > 0 &&
                 entry.Length / (double)entry.CompressedLength > 1000))
                throw new InvalidDataException("ZIP 解包大小或压缩比超过安全上限");
            if (relative.EndsWith(".scmap", StringComparison.OrdinalIgnoreCase))
            {
                scmapCount++;
                if (relative.Contains('/', StringComparison.Ordinal)) throw new InvalidDataException(".scmap 必须在地图根目录");
            }
            if (relative.EndsWith("_scenario.lua", StringComparison.OrdinalIgnoreCase) && !relative.Contains('/', StringComparison.Ordinal))
            {
                scenarioCount++;
                if (request.Kind == "map") infoLua = entry;
            }
            if (relative.Equals("mod_info.lua", StringComparison.OrdinalIgnoreCase))
            {
                modInfoCount++;
                if (request.Kind == "mod") infoLua = entry;
            }
            contentFiles.Add((relative.ToLowerInvariant(), await HashEntryAsync(entry, ct)));
        }
        if (infoLua is null || (request.Kind == "map" && (scmapCount != 1 || scenarioCount != 1 || modInfoCount != 0)) ||
            (request.Kind == "mod" && (modInfoCount != 1 || scmapCount != 0)))
            throw new InvalidDataException("地图或 MOD 缺少必要文件");
        if (request.Kind == "map") await VerifyMapReferencesAsync(infoLua, target.Folder, paths, ct);
        var version = await ReadVersionAsync(infoLua, request.Kind == "map", ct);
        if (!version.Equals(target.GameVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 内部游戏版本与发布清单不一致");
        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in contentFiles.OrderBy(item => item.Relative, StringComparer.Ordinal))
        {
            combined.AppendData(Encoding.UTF8.GetBytes(file.Relative));
            combined.AppendData([0]);
            combined.AppendData(Encoding.UTF8.GetBytes(file.Sha));
            combined.AppendData([0]);
        }
        var contentHash = Convert.ToHexString(combined.GetHashAndReset()).ToLowerInvariant();
        if (!contentHash.Equals(request.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 文件内容指纹与发布清单不一致");
    }

    private static async Task VerifyMapReferencesAsync(ZipArchiveEntry scenario, string folder, HashSet<string> paths, CancellationToken ct)
    {
        if (scenario.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("地图 scenario 文件大小异常");
        await using var input = scenario.Open();
        using var reader = new StreamReader(input, Encoding.UTF8, true);
        var text = await reader.ReadToEndAsync(ct);
        foreach (var (field, suffix) in new[] { ("map", ".scmap"), ("save", "_save.lua"), ("script", "_script.lua") })
        {
            var match = Regex.Match(text, "(?mi)^\\s*" + Regex.Escape(field) + "\\s*=\\s*[\"']([^\"']+)[\"']");
            var reference = match.Success ? match.Groups[1].Value.Replace('\\', '/').Trim('/') : "";
            var parts = reference.Split('/');
            if (parts.Length != 3 || !parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase) ||
                !parts[1].Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                !parts[2].EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                !paths.Contains(folder + "/" + parts[2]))
                throw new InvalidDataException("地图 scenario 引用文件不在发布包中：" + field);
        }
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
    }

    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            total += count;
            if (total > entry.Length || total > MaxUnpackedBytes)
                throw new InvalidDataException("ZIP 实际解包大小与目录信息不一致");
            hash.AppendData(buffer.AsSpan(0, count));
        }
        if (total != entry.Length) throw new InvalidDataException("ZIP 文件长度与目录信息不一致");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ReadVersionAsync(ZipArchiveEntry entry, bool map, CancellationToken ct)
    {
        if (entry.Length is <= 0 or > 2 * 1024 * 1024)
            throw new InvalidDataException("地图或 MOD 信息文件大小异常");
        await using var input = entry.Open();
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        foreach (var field in map ? new[] { "map_version", "version" } : new[] { "version" })
        {
            var match = Regex.Match(text, "(?mi)^\\s*" + Regex.Escape(field) + "\\s*=\\s*[\"']?([A-Za-z0-9][A-Za-z0-9._-]*)[\"']?\\s*,?");
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        throw new InvalidDataException("地图或 MOD 信息文件缺少游戏版本");
    }
}