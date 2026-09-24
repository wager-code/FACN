using System.IO.Compression;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class InstallService(CloudCatalogService cloud, GamePathService paths, LocalContentService local, BackupService backups, TaskService tasks, LogService log, ConfigService config)
{
    private const int MaxArchiveEntries = 100000;
    private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;
    private const long DiskReserveBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _recentGate = new(1, 1);

    public async Task<bool> InstallAsync(CloudContentEntry entry, string? existingRoot = null, CancellationToken ct = default, string? existingVersion = null)
    {
        await _operationGate.WaitAsync(ct);
        try { return await InstallCoreAsync(entry, existingRoot, ct, existingVersion); }
        finally { _operationGate.Release(); }
    }

    private async Task<bool> InstallCoreAsync(CloudContentEntry entry, string? existingRoot, CancellationToken ct, string? existingVersion)
    {
        if (string.IsNullOrWhiteSpace(entry.File)) throw new InvalidOperationException("云端清单没有 ZIP 文件路径");
        if (entry.Size < 0 || entry.Size > CloudCatalogService.MaxPackageBytes) throw new InvalidDataException("云端包大小超出安全范围");

        var installRoot = paths.GetContentDirectory(entry.Kind, create: true);
        if (string.IsNullOrWhiteSpace(installRoot)) throw new InvalidOperationException($"尚未配置 {entry.Kind} 安装目录");
        installRoot = Path.GetFullPath(installRoot);
        if ((File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("安装目录根路径是符号链接/重解析点：" + installRoot);
        if (entry.Size > 0) EnsureAvailableSpace(installRoot, entry.Size);

        // 暂存目录放在最终内容盘符上。这样最后 Directory.Move 是同卷操作，避免游戏在 D:/E: 时跨卷移动失败。
        var workRoot = Path.Combine(installRoot, ".scfa_install_" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(workRoot, "package.zip");
        var extract = Path.Combine(workRoot, "extract");
        Directory.CreateDirectory(workRoot);

        var task = tasks.Create("安装", $"{entry.Kind} · {entry.Name}", "准备下载");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operationCt = operationCts.Token;
        task.ConfigureCancellation(operationCts.Cancel);
        try
        {
            task.Status = "运行中";
            task.Detail = "正在下载云端 ZIP";
            var downloadLimit = entry.Size > 0 ? entry.Size : CloudCatalogService.MaxPackageBytes;
            await cloud.DownloadAsync(entry.File, zipPath, downloadLimit, new Progress<int>(p => task.Progress = Math.Min(55, p * 55 / 100)), operationCt);
            if (entry.Size > 0)
            {
                var downloadedBytes = new FileInfo(zipPath).Length;
                if (downloadedBytes != entry.Size)
                    throw new InvalidDataException($"ZIP 大小与云端清单不一致：期望 {entry.Size} 字节，实际 {downloadedBytes} 字节");
            }

            if (!string.IsNullOrWhiteSpace(entry.Sha256))
            {
                task.Detail = "正在校验 ZIP SHA-256";
                var got = await ContentHash.FileSha256Async(zipPath, operationCt);
                if (!got.Equals(entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"ZIP SHA-256 不匹配：期望 {entry.Sha256}，实际 {got}");
            }

            task.Progress = 60;
            task.Detail = "正在安全检查并解压";
            await ExtractSafelyAsync(zipPath, extract, operationCt);

            var topDirs = Directory.GetDirectories(extract, "*", SearchOption.TopDirectoryOnly);
            var topFiles = Directory.GetFiles(extract, "*", SearchOption.TopDirectoryOnly);
            if (topDirs.Length != 1 || topFiles.Length != 0)
                throw new InvalidDataException("ZIP 必须只有一个顶层内容文件夹");

            var source = topDirs[0];
            var top = Path.GetFileName(source);
            if (!string.IsNullOrWhiteSpace(entry.FolderName) &&
                !string.Equals(entry.FolderName.Trim(), top, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包内目录 {top} 与清单 folder_name {entry.FolderName} 不一致");

            ValidatePackageRoot(source, entry.Kind);

            task.Detail = "正在核对包内真实版本与内容身份";
            var packageContent = await local.AnalyzeDirectoryAsync(source, operationCt);
            ValidateContentMetadata(packageContent, entry, "云端安装包");

            task.Progress = 70;
            task.Detail = "正在校验内容指纹";
            if (!string.IsNullOrWhiteSpace(entry.EffectiveContentHash))
            {
                var contentHash = await ContentHash.DirectorySha256Async(source, operationCt);
                if (!contentHash.Equals(entry.EffectiveContentHash.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{entry.Kind} 内容 SHA-256 与云端正式清单不一致");
            }

            task.Progress = 80;
            task.Detail = "正在安装到游戏目录";
            var result = string.IsNullOrWhiteSpace(existingRoot)
                ? await InstallFreshAsync(source, installRoot, top, entry, operationCt)
                : await InstallOverLocalAsync(source, installRoot, top, existingRoot, entry, existingVersion ?? "", operationCt);

            await RememberInstalledAsync(entry);
            task.Progress = 100;
            task.Status = "完成";
            task.Detail = (result.Changed ? "安装并复检完成：" : "本地内容已相同，未重复覆盖：") + result.Destination;
            log.Info($"{(result.Changed ? "Installed and verified" : "Already identical, skipped replacement")} {entry.Kind} {entry.Name} {entry.Version} -> {result.Destination}");
            return result.Changed;
        }
        catch (OperationCanceledException) when (operationCt.IsCancellationRequested)
        {
            task.Status = "已取消";
            task.Detail = "用户取消了安装";
            task.ConfigureRetry(() => InstallAsync(entry, existingRoot, existingVersion: existingVersion));
            log.Info("安装已取消: " + entry.Name);
            throw;
        }
        catch (Exception ex)
        {
            task.Status = "失败";
            task.Progress = 100;
            task.Detail = ex.Message;
            task.ConfigureRetry(() => InstallAsync(entry, existingRoot, existingVersion: existingVersion));
            log.Error("安装失败: " + entry.Name, ex);
            throw;
        }
        finally
        {
            task.ConfigureCancellation(null);
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); }
            catch (Exception ex) { log.Error("清理安装临时目录失败: " + workRoot, ex); }
        }
    }

    public static string ContentKey(string kind, string id) => kind + ":" + id.Trim();

    private async Task RememberInstalledAsync(CloudContentEntry entry)
    {
        await _recentGate.WaitAsync();
        try
        {
            var key = ContentKey(entry.Kind, entry.Id);
            config.Current.RecentContentKeys.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
            config.Current.RecentContentKeys.Insert(0, key);
            if (config.Current.RecentContentKeys.Count > 50)
                config.Current.RecentContentKeys.RemoveRange(50, config.Current.RecentContentKeys.Count - 50);
            await config.SaveAsync();
        }
        catch (Exception ex)
        {
            // 内容已经安装成功，最近记录保存失败不能反向判定安装失败。
            log.Error("保存最近安装记录失败: " + entry.Name, ex);
        }
        finally
        {
            _recentGate.Release();
        }
    }

    public async Task UninstallAsync(LocalContentEntry entry, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct);
        try { await UninstallCoreAsync(entry, ct); }
        finally { _operationGate.Release(); }
    }

    private async Task UninstallCoreAsync(LocalContentEntry entry, CancellationToken ct)
    {
        if (entry.Kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + entry.Kind, nameof(entry));
        var installRoot = Path.GetFullPath(paths.GetContentDirectory(entry.Kind));
        var localRoot = Path.GetFullPath(entry.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(localRoot)) throw new DirectoryNotFoundException("准备卸载的本地目录已不存在：" + localRoot);
        if (!string.Equals(Path.GetDirectoryName(localRoot), installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许卸载内容目录下的直接子文件夹；嵌套内容请先打开所在文件夹人工确认：" + localRoot);
        if ((File.GetAttributes(installRoot) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(localRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("卸载路径不能是符号链接/重解析点：" + localRoot);

        var task = tasks.Create("卸载", $"{entry.Kind} · {entry.Name}", "准备创建安全备份");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        task.ConfigureCancellation(operationCts.Cancel);
        try
        {
            task.Status = "运行中";
            task.Progress = 10;
            var backup = await backups.CreateAsync(localRoot, entry.Kind, entry.Id, entry.Name, entry.Version, "卸载前自动备份", operationCts.Token);
            operationCts.Token.ThrowIfCancellationRequested();

            // 真正删除开始后不再接受取消，避免给用户留下只删除了一部分的目录。
            task.ConfigureCancellation(null);
            task.Progress = 80;
            task.Detail = "安全备份完成，正在从游戏目录移除";
            await Task.Run(() => Directory.Delete(localRoot, recursive: true));
            task.Progress = 100;
            task.Status = "完成";
            task.Detail = "卸载完成；可从历史备份恢复：" + backup.RevisionRoot;
            log.Info($"Uninstalled {entry.Kind} {entry.Name} {entry.Version}; backup={backup.RevisionRoot}");
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            task.Status = "已取消";
            task.Detail = "用户在删除开始前取消了卸载";
            task.ConfigureRetry(() => UninstallAsync(entry));
            throw;
        }
        catch (Exception ex)
        {
            task.Status = "失败";
            task.Progress = 100;
            task.Detail = ex.Message;
            task.ConfigureRetry(() => UninstallAsync(entry));
            log.Error("卸载失败: " + entry.Name, ex);
            throw;
        }
        finally
        {
            task.ConfigureCancellation(null);
        }
    }

    private async Task<(string Destination, bool Changed)> InstallFreshAsync(string source, string installRoot, string top, CloudContentEntry entry, CancellationToken ct)
    {
        var destination = Path.Combine(installRoot, top);
        if (Directory.Exists(destination))
        {
            if (!string.IsNullOrWhiteSpace(entry.EffectiveContentHash))
            {
                var got = await ContentHash.DirectorySha256Async(destination, ct);
                if (got.Equals(entry.EffectiveContentHash.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    await VerifyInstalledAsync(destination, entry, ct);
                    return (destination, false);
                }
            }
            throw new IOException($"目标目录已存在但尚未确认属于同一{entry.Kind}：{destination}；请先扫描本地内容后再更新");
        }
        Directory.Move(source, destination);
        try
        {
            await VerifyInstalledAsync(destination, entry, ct);
            return (destination, true);
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            throw;
        }
    }

    private async Task<(string Destination, bool Changed)> InstallOverLocalAsync(
        string source,
        string installRoot,
        string top,
        string existingRoot,
        CloudContentEntry entry,
        string existingVersion,
        CancellationToken ct)
    {
        var fullInstallRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var localRoot = Path.GetFullPath(existingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!localRoot.StartsWith(fullInstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("匹配到的本地内容不在当前安装目录内，已阻止自动覆盖");
        if (!string.Equals(Path.GetDirectoryName(localRoot), installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只允许自动更新内容目录下的直接子文件夹；嵌套内容请先人工确认");
        if (!Directory.Exists(localRoot))
            throw new DirectoryNotFoundException("准备更新的本地目录已不存在：" + localRoot);
        if ((File.GetAttributes(localRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("匹配的本地目录是符号链接/重解析点，已阻止自动覆盖：" + localRoot);

        // 下载期间本地版本可能已经被游戏或另一个进程改动，替换前必须重新读取。
        var current = await local.AnalyzeDirectoryAsync(localRoot, ct);
        if (!string.IsNullOrWhiteSpace(existingVersion) &&
            !ContentIdentity.VersionsEquivalent(current.Version, existingVersion))
            throw new InvalidOperationException($"本地版本在安装期间发生变化（原 {existingVersion}，现 {current.Version}），已取消覆盖；请重新扫描后再试");

        var parent = Path.GetDirectoryName(localRoot) ?? installRoot;
        var destination = Path.Combine(parent, top);
        if (!string.Equals(destination, localRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(destination))
            throw new IOException("目标目录已存在，拒绝覆盖：" + destination);

        // 即使用户手动点击安装，也不要给完全相同的目录再建备份或执行替换。
        if (current.Valid && ContentIdentity.VersionsEquivalent(current.Version, entry.Version))
        {
            var currentHash = await ContentHash.DirectorySha256Async(localRoot, ct);
            var packageHash = await ContentHash.DirectorySha256Async(source, ct);
            if (currentHash.Equals(packageHash, StringComparison.OrdinalIgnoreCase))
                return (localRoot, false);
        }

        await backups.CreateAsync(localRoot, entry.Kind, entry.Id, entry.Name, existingVersion, $"安装 {entry.Version} 前自动备份", ct);
        ct.ThrowIfCancellationRequested();

        // 真正替换前把旧目录在原盘改名暂存。失败时可以立即原地恢复。
        var old = localRoot + ".scfa_old_" + Guid.NewGuid().ToString("N");
        Directory.Move(localRoot, old);
        try
        {
            Directory.Move(source, destination);
            await VerifyInstalledAsync(destination, entry, ct);
        }
        catch (Exception installError)
        {
            try
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(old, localRoot);
            }
            catch (Exception restoreError)
            {
                throw new IOException($"安装新内容失败，且自动恢复失败（旧版仍保留在 {old}）：安装错误={installError.Message}；恢复错误={restoreError.Message}", installError);
            }
            throw new IOException("安装新内容失败，旧版已恢复：" + installError.Message, installError);
        }

        try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        return (destination, true);
    }

    private async Task VerifyInstalledAsync(string destination, CloudContentEntry entry, CancellationToken ct)
    {
        var installed = await local.AnalyzeDirectoryAsync(destination, ct);
        ValidateContentMetadata(installed, entry, "安装结果");
        if (!string.IsNullOrWhiteSpace(entry.EffectiveContentHash))
        {
            var installedHash = await ContentHash.DirectorySha256Async(destination, ct);
            if (!installedHash.Equals(entry.EffectiveContentHash.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装后的内容指纹与云端正式清单不一致，已阻止把任务标记为完成");
        }
    }

    private static void ValidateContentMetadata(LocalContentEntry actual, CloudContentEntry expected, string stage)
    {
        if (!actual.Valid) throw new InvalidDataException($"{stage}结构校验失败：{actual.Detail}");
        if (!string.Equals(actual.Kind, expected.Kind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{stage}类型不一致：清单为 {expected.Kind}，实际为 {actual.Kind}");
        if (!ContentIdentity.VersionsEquivalent(actual.Version, expected.Version))
            throw new InvalidDataException($"{stage}版本不一致：清单版本 {expected.Version}，文件内真实版本 {actual.Version}。请管理员重新审核并发布正确的安装包。");
        if (ContentIdentity.MatchScore(actual, expected) < 76)
            throw new InvalidDataException($"{stage}内容身份不一致：清单 ID {expected.Id} 与包内 ID/目录 {actual.Id}/{actual.Folder} 无法安全匹配");
    }

    private static async Task ExtractSafelyAsync(string zipPath, string destination, CancellationToken ct)
    {
        var fullDest = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("ZIP 文件数量超过安全上限");
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = SafeArchive.ValidateRelativePath(entry.FullName);
            if (rel == "") continue;
            var key = rel.Replace('\\', '/');
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (!explicitPaths.Add(key)) throw new InvalidDataException("ZIP 包含重复路径：" + entry.FullName);
            var parts = key.Split('/');
            var prefix = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
                if (filePaths.Contains(prefix)) throw new InvalidDataException("ZIP 文件/目录路径冲突：" + entry.FullName);
                directoryPaths.Add(prefix);
            }
            if (isDirectory)
            {
                if (filePaths.Contains(key)) throw new InvalidDataException("ZIP 文件/目录路径冲突：" + entry.FullName);
                directoryPaths.Add(key);
                continue;
            }
            if (directoryPaths.Contains(key) || !filePaths.Add(key)) throw new InvalidDataException("ZIP 文件/目录路径冲突：" + entry.FullName);
            if (entry.Length < 0 || total > MaxExtractedBytes - entry.Length) throw new InvalidDataException("ZIP 解压后大小超过 8GB 安全上限");
            total += entry.Length;
            if (entry.Length > 64L * 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 1000)
                throw new InvalidDataException("ZIP 包含异常压缩比文件：" + entry.FullName);
        }

        EnsureAvailableSpace(destination, total);
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = SafeArchive.ValidateRelativePath(entry.FullName);
            if (rel == "") continue;
            var target = Path.GetFullPath(Path.Combine(destination, rel));
            if (!target.StartsWith(fullDest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP 路径越界：" + entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 128 * 1024, ct);
        }
    }

    private static void EnsureAvailableSpace(string path, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < bytes + DiskReserveBytes)
            throw new IOException($"安装盘可用空间不足：至少需要 {(bytes + DiskReserveBytes) / 1024d / 1024d:F0} MB");
    }

    private static void ValidatePackageRoot(string root, string kind)
    {
        if (kind == "地图")
        {
            if (Directory.GetFiles(root, "*.scmap", SearchOption.TopDirectoryOnly).Length != 1)
                throw new InvalidDataException("地图 ZIP 顶层目录必须有且只有一个 .scmap");
            if (Directory.GetFiles(root, "*_scenario.lua", SearchOption.TopDirectoryOnly).Length != 1)
                throw new InvalidDataException("地图 ZIP 顶层目录必须有且只有一个 *_scenario.lua");
        }
        else if (!File.Exists(Path.Combine(root, "mod_info.lua")))
        {
            throw new InvalidDataException("MOD ZIP 顶层目录缺少 mod_info.lua");
        }
    }
}
