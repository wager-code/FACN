using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class BackupService(GamePathService paths, LogService log)
{
    private const int MaxBackupFiles = 100000;
    private const string MetadataName = "backup.json";
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; } = Path.Combine(ConfigService.ResolveDataDirectory(), "Backups");

    public async Task<ContentBackupEntry> CreateAsync(
        string sourceRoot,
        string kind,
        string contentId,
        string name,
        string sourceVersion,
        string reason,
        CancellationToken ct = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("备份源目录不存在：" + sourceRoot);
        RejectRootOrLink(sourceRoot, "备份源目录");
        var safeId = SafeLabel(string.IsNullOrWhiteSpace(contentId) ? Path.GetFileName(sourceRoot) : contentId);
        var revisionRoot = Path.Combine(Root, KindDirectory(kind), safeId, DateTime.Now.ToString("yyyyMMdd_HHmmss.fff") + "_" + Guid.NewGuid().ToString("N")[..6]);
        var contentRoot = Path.Combine(revisionRoot, Path.GetFileName(sourceRoot));
        try
        {
            Directory.CreateDirectory(revisionRoot);
            var (files, bytes) = await CopyDirectoryNoLinksAsync(sourceRoot, contentRoot, ct);
            var hash = await ContentHash.DirectorySha256Async(contentRoot, ct);
            var metadata = new ContentBackupMetadata
            {
                Kind = kind,
                ContentId = contentId,
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(sourceRoot) : name,
                SourceVersion = sourceVersion,
                FolderName = Path.GetFileName(sourceRoot),
                OriginalRoot = sourceRoot,
                ContentHash = hash,
                Reason = reason,
                CreatedAt = DateTimeOffset.Now,
                Bytes = bytes,
                Files = files
            };
            var metadataPath = Path.Combine(revisionRoot, MetadataName);
            await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, _json), ct);
            log.Info($"已创建内容备份: {kind} {metadata.Name} {metadata.SourceVersion} -> {revisionRoot}");
            return ToEntry(metadata, revisionRoot, contentRoot);
        }
        catch
        {
            try { if (Directory.Exists(revisionRoot)) Directory.Delete(revisionRoot, true); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<ContentBackupEntry>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<ContentBackupEntry>();
        foreach (var kindDirectory in Directory.EnumerateDirectories(Root).Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var kind = Path.GetFileName(kindDirectory).Equals("Maps", StringComparison.OrdinalIgnoreCase) ? "地图" : "MOD";
            foreach (var idDirectory in Directory.EnumerateDirectories(kindDirectory).Take(20000))
            {
                foreach (var revisionRoot in Directory.EnumerateDirectories(idDirectory).Take(20000))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var contentRoot = Directory.EnumerateDirectories(revisionRoot).SingleOrDefault();
                        if (contentRoot is null) continue;
                        var metadataPath = Path.Combine(revisionRoot, MetadataName);
                        ContentBackupMetadata metadata;
                        if (File.Exists(metadataPath))
                        {
                            metadata = JsonSerializer.Deserialize<ContentBackupMetadata>(await File.ReadAllTextAsync(metadataPath, ct), _json) ?? throw new InvalidDataException("备份元数据无效");
                        }
                        else
                        {
                            var info = new DirectoryInfo(revisionRoot);
                            var stats = CountTree(contentRoot, ct);
                            metadata = new ContentBackupMetadata
                            {
                                Kind = kind,
                                ContentId = Path.GetFileName(idDirectory),
                                Name = Path.GetFileName(contentRoot),
                                FolderName = Path.GetFileName(contentRoot),
                                CreatedAt = info.CreationTime,
                                Bytes = stats.Bytes,
                                Files = stats.Files,
                                Reason = "旧版自动备份"
                            };
                        }
                        result.Add(ToEntry(metadata, revisionRoot, contentRoot));
                    }
                    catch (Exception ex)
                    {
                        log.Error("读取内容备份失败: " + revisionRoot, ex);
                    }
                }
            }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task RestoreAsync(ContentBackupEntry entry, CancellationToken ct = default)
    {
        var revisionRoot = EnsureUnderRoot(entry.RevisionRoot);
        var source = Path.GetFullPath(entry.ContentRoot);
        if (!string.Equals(Path.GetDirectoryName(source), revisionRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
            throw new InvalidDataException("备份内容目录无效");
        RejectRootOrLink(source, "备份内容目录");
        var folderName = ValidateFolderName(entry.FolderName);

        var installRoot = Path.GetFullPath(paths.GetContentDirectory(entry.Kind, create: true)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectRootOrLink(installRoot, "安装目录");
        var destination = ResolveDestination(entry, installRoot);
        ContentBackupEntry? safetyBackup = null;
        if (Directory.Exists(destination))
        {
            safetyBackup = await CreateAsync(destination, entry.Kind, entry.ContentId, entry.Name, "", "恢复历史版本前安全备份", ct);
        }

        var workRoot = Path.Combine(installRoot, ".scfa_restore_" + Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(workRoot, folderName);
        var old = destination + ".scfa_before_restore_" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(workRoot);
            await CopyDirectoryNoLinksAsync(source, staged, ct);
            if (!string.IsNullOrWhiteSpace(entry.ContentHash))
            {
                var stagedHash = await ContentHash.DirectorySha256Async(staged, ct);
                if (!stagedHash.Equals(entry.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("恢复暂存内容 SHA-256 与备份元数据不一致");
            }
            ValidateContentRoot(staged, entry.Kind);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) Directory.Move(destination, old);
            try
            {
                Directory.Move(staged, destination);
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(destination)) Directory.Move(old, destination);
                throw;
            }
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
            log.Info($"已恢复内容备份: {entry.Kind} {entry.Name} {entry.SourceVersion} -> {destination}; safety={safetyBackup?.RevisionRoot}");
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); } catch { }
        }
    }

    public Task DeleteAsync(ContentBackupEntry entry)
    {
        var revisionRoot = EnsureUnderRoot(entry.RevisionRoot);
        if (Directory.Exists(revisionRoot)) Directory.Delete(revisionRoot, true);
        log.Info("已删除内容备份: " + revisionRoot);
        return Task.CompletedTask;
    }

    private string ResolveDestination(ContentBackupEntry entry, string installRoot)
    {
        var rootPrefix = installRoot + Path.DirectorySeparatorChar;
        if (!string.IsNullOrWhiteSpace(entry.OriginalRoot))
        {
            var original = Path.GetFullPath(entry.OriginalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (original.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(original), installRoot, StringComparison.OrdinalIgnoreCase)) return original;
        }
        return Path.Combine(installRoot, ValidateFolderName(entry.FolderName));
    }

    private string EnsureUnderRoot(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("备份路径越界");
        var relative = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length < 3 || relative.Any(x => x is "" or "." or "..")) throw new InvalidOperationException("备份修订目录层级无效");
        return full;
    }

    private static async Task<(int Files, long Bytes)> CopyDirectoryNoLinksAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var stack = new Stack<(string Source, string Destination)>();
        stack.Push((source, destination));
        var files = 0;
        long bytes = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            foreach (var item in Directory.EnumerateFileSystemEntries(current.Source))
            {
                ct.ThrowIfCancellationRequested();
                var attr = File.GetAttributes(item);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("目录包含符号链接/重解析点：" + item);
                var target = Path.Combine(current.Destination, Path.GetFileName(item));
                if ((attr & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    stack.Push((item, target));
                }
                else
                {
                    if (++files > MaxBackupFiles) throw new InvalidDataException("备份文件数量超过安全上限");
                    var length = new FileInfo(item).Length;
                    bytes = checked(bytes + length);
                    await using var input = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, 128 * 1024, ct);
                }
            }
        }
        return (files, bytes);
    }

    private static (int Files, long Bytes) CountTree(string root, CancellationToken ct)
    {
        var files = 0;
        long bytes = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var item in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                var attr = File.GetAttributes(item);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("备份包含重解析点");
                if ((attr & FileAttributes.Directory) != 0) stack.Push(item);
                else
                {
                    if (++files > MaxBackupFiles) throw new InvalidDataException("备份文件数量超过安全上限");
                    bytes = checked(bytes + new FileInfo(item).Length);
                }
            }
        }
        return (files, bytes);
    }

    private static void RejectRootOrLink(string path, string label)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(label + "不能是磁盘根目录");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(label + "是符号链接/重解析点");
    }

    private static void ValidateContentRoot(string root, string kind)
    {
        if (kind == "地图")
        {
            if (Directory.GetFiles(root, "*.scmap", SearchOption.TopDirectoryOnly).Length != 1 || Directory.GetFiles(root, "*_scenario.lua", SearchOption.TopDirectoryOnly).Length != 1)
                throw new InvalidDataException("备份不是有效地图内容");
        }
        else if (!File.Exists(Path.Combine(root, "mod_info.lua"))) throw new InvalidDataException("备份不是有效 MOD 内容");
    }

    private static string KindDirectory(string kind) => kind == "地图" ? "Maps" : kind == "MOD" ? "Mods" : throw new ArgumentException("未知内容类型：" + kind);
    private static string ValidateFolderName(string value)
    {
        var normalized = SafeArchive.ValidateRelativePath(value);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(Path.DirectorySeparatorChar) || normalized.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("备份内容文件夹名称无效");
        return normalized;
    }
    private static string SafeLabel(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var label = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(label) ? "content" : label[..Math.Min(label.Length, 80)];
    }

    private static ContentBackupEntry ToEntry(ContentBackupMetadata metadata, string revisionRoot, string contentRoot) => new()
    {
        Kind = metadata.Kind,
        ContentId = metadata.ContentId,
        Name = metadata.Name,
        SourceVersion = metadata.SourceVersion,
        FolderName = metadata.FolderName,
        OriginalRoot = metadata.OriginalRoot,
        RevisionRoot = revisionRoot,
        ContentRoot = contentRoot,
        ContentHash = metadata.ContentHash,
        Reason = metadata.Reason,
        CreatedAt = metadata.CreatedAt,
        Bytes = metadata.Bytes,
        Files = metadata.Files
    };
}
