using System.IO;
using System.IO.Compression;
using System.Net.Http;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class BackupIntegrityRegression
{
    internal static async Task RunAsync(string ownerRoot, LogService log, Action<bool, string> check)
    {
        var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        var root = Path.Combine(ownerRoot, "backup_integrity_" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(root, "data"));
            var config = new ConfigService();
            config.Current.GameRoot = "";
            config.Current.MapsDir = Path.Combine(root, "maps");
            config.Current.ModsDir = Path.Combine(root, "mods");
            Directory.CreateDirectory(config.Current.MapsDir);
            Directory.CreateDirectory(config.Current.ModsDir);
            var paths = new GamePathService(config);
            var normal = new BackupService(paths, log);
            var mutationSource = MakeContent(paths, "地图", "changing_backup", "before");
            var mutationBackup = new BackupService(paths, log, (point, _, _) =>
            {
                if (point == BackupService.Checkpoint.Copied) File.WriteAllText(Path.Combine(mutationSource, "marker.txt"), "after");
                return Task.CompletedTask;
            });
            var rejected = await FailsAsync<InvalidDataException>(() => mutationBackup.CreateAsync(mutationSource,
                "地图", "changing_backup", "changing", "1", "test"));
            check(rejected && File.ReadAllText(Path.Combine(mutationSource, "marker.txt")) == "after",
                "备份源在复制期间变化时拒绝完成备份并保留用户最新内容");
            var badRoot = Path.Combine(mutationBackup.Root, "Maps", "changing_backup");
            check(!Directory.Exists(badRoot) || !Directory.EnumerateFiles(badRoot, "backup.json", SearchOption.AllDirectories).Any(),
                "不一致快照不会留下可用备份元数据");

            foreach (var kind in new[] { "地图", "MOD" })
            {
                var name = kind == "地图" ? "restore_map" : "restore_mod";
                var destination = MakeContent(paths, kind, name, "backup");
                var entry = await normal.CreateAsync(destination, kind, name, name, "1", "test");
                File.WriteAllText(Path.Combine(destination, "marker.txt"), "current");
                var original = await ContentHash.DirectorySha256Async(destination);
                var corrupting = new BackupService(paths, log, (point, path, _) =>
                {
                    if (point == BackupService.Checkpoint.Restored) File.WriteAllText(Path.Combine(path, "marker.txt"), "corrupt-installed");
                    return Task.CompletedTask;
                });
                var corruptRejected = await FailsAsync<IOException>(() => corrupting.RestoreAsync(entry));
                check(corruptRejected && await ContentHash.DirectorySha256Async(destination) == original,
                    kind + "历史恢复落盘复核失败时完整恢复当前目录");
                File.WriteAllText(Path.Combine(destination, "marker.txt"), "current");
                using var canceled = new CancellationTokenSource();
                var canceling = new BackupService(paths, log, (point, _, _) =>
                {
                    if (point == BackupService.Checkpoint.Restored) canceled.Cancel();
                    return Task.CompletedTask;
                });
                var cancellation = await FailsAsync<OperationCanceledException>(() => canceling.RestoreAsync(entry, canceled.Token));
                check(cancellation && await ContentHash.DirectorySha256Async(destination) == original,
                    kind + "恢复替换后取消保持取消语义且原目录完整");
                File.WriteAllText(Path.Combine(destination, "marker.txt"), "current");
                var legacy = WithoutHash(entry);
                var legacyCorrupt = new BackupService(paths, log, (point, path, _) =>
                {
                    if (point == BackupService.Checkpoint.Copied && path.Contains(".scfa_restore_"))
                        File.WriteAllText(Path.Combine(path, "marker.txt"), "legacy-corrupt");
                    return Task.CompletedTask;
                });
                check(await FailsAsync<InvalidDataException>(() => legacyCorrupt.RestoreAsync(legacy)) &&
                    await ContentHash.DirectorySha256Async(destination) == original,
                    kind + "无历史指纹的旧格式备份仍校验完整暂存快照");
                File.WriteAllText(Path.Combine(destination, "marker.txt"), "current");
                await normal.RestoreAsync(legacy);
                check(await ContentHash.DirectorySha256Async(destination) == entry.ContentHash,
                    kind + "有效旧格式备份仍可正常恢复");
                var beforeRestore = (await normal.ListAsync()).Any(x => x.ContentHash == original);
                check(beforeRestore, kind + "历史恢复保留恢复前完整指纹的安全备份");
            }

            var lockedDestination = MakeContent(paths, "MOD", "locked_restore", "backup");
            var lockedEntry = await normal.CreateAsync(lockedDestination, "MOD", "locked_restore", "locked", "1", "test");
            File.WriteAllText(Path.Combine(lockedDestination, "marker.txt"), "current-locked");
            var lockedOriginal = await ContentHash.DirectorySha256Async(lockedDestination);
            FileStream? retainedHandle = null;
            var locked = new BackupService(paths, log, (point, path, _) =>
            {
                if (point == BackupService.Checkpoint.Restored)
                {
                    File.WriteAllText(Path.Combine(path, "marker.txt"), "broken-locked");
                    retainedHandle = new FileStream(Path.Combine(path, "marker.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return Task.CompletedTask;
            });
            var failedRollback = false;
            try { failedRollback = await FailsAsync<IOException>(() => locked.RestoreAsync(lockedEntry)); }
            finally { retainedHandle?.Dispose(); }
            var retired = Directory.GetDirectories(config.Current.ModsDir, "locked_restore.scfa_before_restore_*");
            check(failedRollback && retired.Length == 1 && await ContentHash.DirectorySha256Async(retired[0]) == lockedOriginal,
                "恢复回滚受文件占用阻止时报告失败并保留完整原目录");

            await CheckMutualExclusionAsync(config, paths, normal, log, check, uninstall: false);
            await CheckMutualExclusionAsync(config, paths, normal, log, check, uninstall: true);
            await CheckListCancellationAsync(root, paths, log, check);
            await CheckPathBoundariesAsync(root, paths, normal, log, check);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CheckMutualExclusionAsync(ConfigService config, GamePathService paths, BackupService normal,
        LogService log, Action<bool, string> check, bool uninstall)
    {
        var name = uninstall ? "restore_uninstall" : "restore_concurrent";
        var destination = MakeContent(paths, "MOD", name, "first");
        var firstEntry = await normal.CreateAsync(destination, "MOD", name, name, "1", "first");
        File.WriteAllText(Path.Combine(destination, "marker.txt"), "second");
        var secondEntry = await normal.CreateAsync(destination, "MOD", name, name, "1", "second");
        File.WriteAllText(Path.Combine(destination, "marker.txt"), "current");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visits = 0;
        var service = new BackupService(paths, log, async (point, _, ct) =>
        {
            if (point == BackupService.Checkpoint.Restored && Interlocked.Increment(ref visits) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        });
        var first = service.RestoreAsync(firstEntry);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? second = null;
        try
        {
            if (uninstall)
            {
                var local = new LocalContentService(paths, log);
                var current = await local.AnalyzeDirectoryAsync(destination);
                using var http = new HttpClient(new StaticPackageHandler([]));
                var installer = new InstallService(new CloudCatalogService(config, http), paths, local, service,
                    new TaskService(), log, config);
                second = installer.UninstallAsync(current);
            }
            else second = service.RestoreAsync(secondEntry);
            await Task.WhenAny(second, Task.Delay(150));
            check(!second.IsCompleted && Directory.Exists(destination) &&
                await ContentHash.DirectorySha256Async(destination) == firstEntry.ContentHash,
                uninstall ? "历史恢复与本地卸载共享互斥门避免交错删除" : "两次历史恢复共享互斥门避免交错覆盖");
        }
        finally { release.TrySetResult(); await first; if (second is not null) await second; }
        check(uninstall ? !Directory.Exists(destination) : await ContentHash.DirectorySha256Async(destination) == secondEntry.ContentHash,
            uninstall ? "等待中的卸载在恢复确认后执行" : "等待中的第二次恢复在首次确认后正常完成");
    }

    private static async Task CheckListCancellationAsync(string root, GamePathService paths, LogService log, Action<bool, string> check)
    {
        var previous = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(root, "list-data"));
        try
        {
            using var canceled = new CancellationTokenSource();
            var service = new BackupService(paths, log, (point, _, _) =>
            {
                if (point == BackupService.Checkpoint.MetadataRead) canceled.Cancel();
                return Task.CompletedTask;
            });
            var destination = MakeContent(paths, "MOD", "list_cancel", "list");
            await service.CreateAsync(destination, "MOD", "list_cancel", "list", "1", "test");
            check(await FailsAsync<OperationCanceledException>(() => service.ListAsync(canceled.Token)),
                "读取最后一份备份时取消不会被当作损坏条目吞掉");
            check((await service.ListAsync()).Count == 1, "取消备份列表读取不删除有效备份");
        }
        finally { Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previous); }
    }

    private static async Task CheckPathBoundariesAsync(string root, GamePathService paths, BackupService normal,
        LogService log, Action<bool, string> check)
    {
        var source = MakeContent(paths, "MOD", "path_boundary", "intact");
        var entry = await normal.CreateAsync(source, "MOD", "path_boundary", "path", "1", "test");
        var nestedEntry = new ContentBackupEntry { RevisionRoot = entry.ContentRoot };
        check(await FailsAsync<InvalidOperationException>(() => normal.DeleteAsync(nestedEntry)) &&
            await ContentHash.DirectorySha256Async(entry.ContentRoot) == entry.ContentHash,
            "备份删除拒绝把内容子目录当作备份修订目录");
        var previous = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", source);
            var nested = new BackupService(paths, log);
            check(await FailsAsync<InvalidDataException>(() => nested.CreateAsync(source, "MOD", "path_boundary", "path", "1", "test")) &&
                !Directory.Exists(nested.Root), "备份根目录位于源目录内部时拒绝递归复制且不创建目录");
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(root, "link-data"));
            var linked = new BackupService(paths, log);
            var kindRoot = Path.Combine(linked.Root, "Mods");
            Directory.CreateDirectory(kindRoot);
            var external = Path.Combine(root, "external");
            var externalRevision = Path.Combine(external, "revision");
            Directory.CreateDirectory(externalRevision);
            File.WriteAllText(Path.Combine(externalRevision, "marker.txt"), "must-stay");
            var junction = Path.Combine(kindRoot, "linked-id");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
                junction.Replace("'", "''") + "' -Value '" + external.Replace("'", "''") + "' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("隔离 junction 夹具创建失败: " + error);
            try
            {
                var malicious = new ContentBackupEntry { RevisionRoot = Path.Combine(junction, "revision") };
                check(await FailsAsync<InvalidDataException>(() => linked.DeleteAsync(malicious)) &&
                    File.ReadAllText(Path.Combine(externalRevision, "marker.txt")) == "must-stay",
                    "备份删除拒绝祖先目录连接并保留连接目标内容");
                check(await FailsAsync<InvalidDataException>(() => linked.RestoreAsync(malicious)),
                    "备份恢复拒绝祖先目录连接");
                check(await FailsAsync<InvalidDataException>(async () => await linked.ListAsync()),
                    "备份列表拒绝遍历连接到外部的索引目录");
            }
            finally { Directory.Delete(junction); }
        }
        finally { Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previous); }
    }

    private static string MakeContent(GamePathService paths, string kind, string folder, string marker)
    {
        var root = paths.GetContentDirectory(kind);
        using var stream = new MemoryStream(kind == "地图" ? ContentPackageFixtures.CreateMapPackage(folder) : ContentPackageFixtures.CreateModPackage(folder));
        using var archive = new ZipArchive(stream);
        archive.ExtractToDirectory(root);
        var directory = Path.Combine(root, folder);
        File.WriteAllText(Path.Combine(directory, "marker.txt"), marker);
        return directory;
    }
    private static ContentBackupEntry WithoutHash(ContentBackupEntry entry) => new()
    {
        Kind = entry.Kind, ContentId = entry.ContentId, Name = entry.Name, SourceVersion = entry.SourceVersion,
        FolderName = entry.FolderName, OriginalRoot = entry.OriginalRoot, RevisionRoot = entry.RevisionRoot,
        ContentRoot = entry.ContentRoot, ContentHash = "", Reason = entry.Reason, CreatedAt = entry.CreatedAt, Bytes = entry.Bytes, Files = entry.Files
    };
    private static async Task<bool> FailsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }
}