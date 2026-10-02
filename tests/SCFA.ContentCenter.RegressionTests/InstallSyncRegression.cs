using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using static ContentPackageFixtures;

internal static class InstallSyncRegression
{
    public static async Task RunAsync(string configDirectory, string mapsRoot, ConfigService config,
        GamePathService pathService, LocalContentService localContent, BackupService backups,
        TaskService tasks, LogService log, Action<bool, string> check)
    {
        var packagePayload = CreateMapPackage("recent_map");
        using var packageClient = new HttpClient(new StaticPackageHandler(packagePayload)) { Timeout = Timeout.InfiniteTimeSpan };
        var packageCloud = new CloudCatalogService(config, packageClient);
        var installer = new InstallService(packageCloud, pathService, localContent, backups, tasks, log, config);
        await installer.InstallAsync(new CloudContentEntry
        {
            Kind = "地图",
            Id = "recent-map",
            Name = "最近安装测试地图",
            Version = "1",
            File = "packages/recent-map.zip",
            FolderName = "recent_map",
            Size = packagePayload.Length
        });
        check(Directory.Exists(Path.Combine(mapsRoot, "recent_map")), "云端内容包可安装到玩家地图目录");
        check(config.Current.RecentContentKeys.FirstOrDefault() == "地图:recent-map", "成功安装后自动写入最近内容记录");

        var installedRecentMap = Path.Combine(mapsRoot, "recent_map");
        var expectedContentHash = await ContentHash.DirectorySha256Async(installedRecentMap);
        var extraFile = Path.Combine(installedRecentMap, "player_extra.lua");
        await File.WriteAllTextAsync(extraFile, "extra file must be removed by strict repair");
        check(!string.Equals(await ContentHash.DirectorySha256Async(installedRecentMap), expectedContentHash, StringComparison.OrdinalIgnoreCase), "同版本目录多出一个文件会改变完整内容指纹");
        await installer.InstallAsync(new CloudContentEntry
        {
            Kind = "地图",
            Id = "recent-map",
            Name = "严格一致性测试地图",
            Version = "1",
            File = "packages/recent-map.zip",
            FolderName = "recent_map",
            Size = packagePayload.Length,
            ContentSha256 = expectedContentHash
        }, installedRecentMap, existingVersion: "1");
        check(!File.Exists(extraFile) && string.Equals(await ContentHash.DirectorySha256Async(installedRecentMap), expectedContentHash, StringComparison.OrdinalIgnoreCase), "严格修复采用整目录替换并删除活动目录中的多余文件");
        var strictRepairBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == "严格一致性测试地图");
        check(strictRepairBackup is not null && File.Exists(Path.Combine(strictRepairBackup.ContentRoot, "player_extra.lua")), "严格修复前备份仍保留被移出的多余文件以便回滚");

        var syncPackage = CreateMapPackage("sync_map");
        var syncPackagePath = Path.Combine(configDirectory, "sync-package.zip");
        await File.WriteAllBytesAsync(syncPackagePath, syncPackage);
        var syncSource = Path.Combine(configDirectory, "sync-package-content");
        ZipFile.ExtractToDirectory(syncPackagePath, syncSource);
        var syncContentHash = await ContentHash.DirectorySha256Async(Path.Combine(syncSource, "sync_map"));
        var syncEntry = new CloudContentEntry
        {
            Kind = "地图",
            Id = "sync-map",
            Name = "同步回归地图",
            Version = "1",
            FolderName = "sync_map",
            File = "packages/sync-map.zip",
            Size = syncPackage.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(syncPackage)),
            ContentSha256 = syncContentHash
        };
        using var syncClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [syncEntry] }, syncPackage))
        { Timeout = Timeout.InfiniteTimeSpan };
        var syncCloud = new CloudCatalogService(config, syncClient);
        var syncInstaller = new InstallService(syncCloud, pathService, localContent, backups, tasks, log, config);
        var syncService = new SyncService(syncCloud, localContent, syncInstaller, log);
        var syncRoot = Path.Combine(mapsRoot, "sync_map");
        var firstSync = await syncService.SyncAllAsync(["地图"]);
        check(firstSync.Installed == 1 && firstSync.Failed == 0 && Directory.Exists(syncRoot), "一键同步会从云端清单安装缺失地图");
        var unchangedSync = await syncService.SyncAllAsync(["地图"]);
        check(unchangedSync.Skipped == 1 && unchangedSync.Installed == 0, "一键同步对内容指纹一致的地图不会重复安装");
        var activeAccount = "test-server|player-a";
        var syncPreferences = new SyncPreferenceService(() => activeAccount);
        await syncPreferences.SetExcludedAsync(syncEntry, true);
        check(await new SyncPreferenceService(() => activeAccount).IsExcludedAsync(syncEntry), "不喜欢标记重新启动后仍保留");
        activeAccount = "test-server|player-b";
        check(!await syncPreferences.IsExcludedAsync(syncEntry), "不同登录账号不会共享不喜欢列表");
        activeAccount = "test-server|player-a";
        var preferredInstaller = new InstallService(syncCloud, pathService, localContent, backups, tasks, log, config, syncPreferences);
        var preferredSync = new SyncService(syncCloud, localContent, preferredInstaller, log, syncPreferences);
        Directory.Delete(syncRoot, recursive: true);
        var excludedSync = await preferredSync.SyncAllAsync(["地图"]);
        check(excludedSync.Skipped == 1 && excludedSync.Installed == 0 && excludedSync.Failed == 0 &&
              !Directory.Exists(syncRoot) && excludedSync.Messages.Any(x => x.Contains("不喜欢")),
            "玩家删除已标记不喜欢的地图后，一键同步不会重新安装");
        await preferredInstaller.InstallAsync(syncEntry);
        check(Directory.Exists(syncRoot) && await syncPreferences.IsExcludedAsync(syncEntry),
            "玩家仍可手动安装不喜欢的内容，且手动安装不会取消跳过设置");
        await syncPreferences.SetExcludedAsync(syncEntry, false);
        check(!await syncPreferences.IsExcludedAsync(syncEntry), "再次点击可恢复自动同步");
        var alternatePackage = CreateMapPackage("sync_map_v2");
        var alternatePackagePath = Path.Combine(configDirectory, "alternate-map.zip");
        await File.WriteAllBytesAsync(alternatePackagePath, alternatePackage);
        ZipFile.ExtractToDirectory(alternatePackagePath, mapsRoot);
        var alternateRoot = Path.Combine(mapsRoot, "sync_map_v2");
        var ambiguousEntry = new CloudContentEntry
        {
            Kind = "地图", Id = "sync_map", Name = syncEntry.Name, Version = syncEntry.Version,
            FolderName = syncEntry.FolderName, File = syncEntry.File, Size = syncEntry.Size,
            Sha256 = syncEntry.Sha256, ContentSha256 = syncEntry.ContentSha256
        };
        check(ContentIdentity.FindBestResult((await localContent.ScanAsync("地图")).ToArray(), ambiguousEntry).Ambiguous,
            "两个地图版本共享同一 ID 时会识别为多个候选目录");
        var ambiguousLocals = (await localContent.ScanAsync("地图")).ToArray();
        var verifiedDuplicate = await ContentIdentity.FindVerifiedCopyAsync(ambiguousLocals, ambiguousEntry,
            ContentIdentity.FindBestResult(ambiguousLocals, ambiguousEntry).Score);
        check(verifiedDuplicate is not null && verifiedDuplicate.Root == syncRoot,
            "多个本地版本中已存在云端指纹一致的副本时可识别为已安装");
        var mismatchedDuplicate = new CloudContentEntry
        {
            Kind = "地图", Id = ambiguousEntry.Id, Name = ambiguousEntry.Name, Version = ambiguousEntry.Version,
            FolderName = ambiguousEntry.FolderName, ContentSha256 = new string('0', 64), Aliases = ambiguousEntry.Aliases
        };
        check(await ContentIdentity.FindVerifiedCopyAsync(ambiguousLocals, mismatchedDuplicate,
              ContentIdentity.FindBestResult(ambiguousLocals, mismatchedDuplicate).Score) is null,
            "多个本地目录都不符合云端内容指纹时仍拒绝擅自选择覆盖目标");
        using var ambiguousClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [ambiguousEntry] }, syncPackage))
        { Timeout = Timeout.InfiniteTimeSpan };
        var ambiguousCloud = new CloudCatalogService(config, ambiguousClient);
        var ambiguousSync = new SyncService(ambiguousCloud, localContent,
            new InstallService(ambiguousCloud, pathService, localContent, backups, tasks, log, config), log);
        var beforeAmbiguousBackupCount = (await backups.ListAsync()).Count;
        var verifiedAmbiguous = await ambiguousSync.SyncAllAsync(["地图"]);
        check(verifiedAmbiguous.Skipped == 1 && verifiedAmbiguous.Failed == 0 &&
              verifiedAmbiguous.Messages.Any(x => x.Contains("其他本地副本保留")) &&
              Directory.Exists(alternateRoot) && (await backups.ListAsync()).Count == beforeAmbiguousBackupCount,
            "多个同 ID 地图中已有云端指纹一致的版本时保留全部副本且不重复覆盖");
        var conflictCandidates = ambiguousLocals.Where(x => ContentIdentity.MatchScore(x, ambiguousEntry) ==
            ContentIdentity.FindBestResult(ambiguousLocals, ambiguousEntry).Score).ToArray();
        var conflictInstaller = new InstallService(ambiguousCloud, pathService, localContent, backups, tasks, log, config);
        var wrongVersionConflict = new CloudContentEntry
        {
            Kind = "地图", Id = ambiguousEntry.Id, Name = ambiguousEntry.Name, Version = "999",
            FolderName = ambiguousEntry.FolderName, File = ambiguousEntry.File, Size = ambiguousEntry.Size,
            Sha256 = ambiguousEntry.Sha256, ContentSha256 = ambiguousEntry.ContentSha256
        };
        var rejectedConflictPackage = false;
        try { await conflictInstaller.ReplaceConflictsAsync(wrongVersionConflict, conflictCandidates); }
        catch (InvalidDataException) { rejectedConflictPackage = true; }
        check(rejectedConflictPackage && Directory.Exists(syncRoot) && Directory.Exists(alternateRoot),
            "清理冲突前先校验云端包内版本，异常包不会删除本地目录");
        await conflictInstaller.ReplaceConflictsAsync(ambiguousEntry, conflictCandidates);
        var conflictBackups = (await backups.ListAsync()).Where(x => x.Reason == "清理重复版本并安装云端内容前自动备份").ToArray();
        check(Directory.Exists(syncRoot) && !Directory.Exists(alternateRoot) &&
              string.Equals(await ContentHash.DirectorySha256Async(syncRoot), syncContentHash, StringComparison.OrdinalIgnoreCase) &&
              conflictBackups.Length == 2 && conflictBackups.All(x => Directory.Exists(x.ContentRoot)) &&
              conflictBackups.Any(x => x.OriginalRoot == syncRoot) && conflictBackups.Any(x => x.OriginalRoot == alternateRoot),
            "手动清理重复目录会安装经校验的云端版本，并保存两个原目录的历史备份");
        var syncExtraFile = Path.Combine(syncRoot, "player_extra.lua");
        await File.WriteAllTextAsync(syncExtraFile, "local change for rollback");
        var repairSync = await syncService.SyncAllAsync(["地图"]);
        check(repairSync.Installed == 1 && !File.Exists(syncExtraFile) &&
              string.Equals(await ContentHash.DirectorySha256Async(syncRoot), syncContentHash, StringComparison.OrdinalIgnoreCase),
            "一键同步会备份并修复同版本内容差异");
        var syncRepairBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == "同步回归地图");
        check(syncRepairBackup is not null && File.Exists(Path.Combine(syncRepairBackup.ContentRoot, "player_extra.lua")),
            "同步修复前的玩家文件仍可从备份取回");
        if (syncRepairBackup is not null)
        {
            await backups.RestoreAsync(syncRepairBackup);
            check(File.Exists(syncExtraFile), "历史回滚可把同步修复前的文件恢复到玩家目录");
        }
        var syncScenario = Path.Combine(syncRoot, "sync_map_scenario.lua");
        await File.WriteAllTextAsync(syncScenario, (await File.ReadAllTextAsync(syncScenario)).Replace("map_version = 1", "map_version = 9"));
        var newerSync = await syncService.SyncAllAsync(["地图"]);
        check(newerSync.Skipped == 1 && newerSync.Installed == 0 && (await File.ReadAllTextAsync(syncScenario)).Contains("map_version = 9"),
            "一键同步保护玩家目录中较新的地图版本");

        var duplicateEntry = new CloudContentEntry
        {
            Id = "other-sync-map", Name = "重复目录地图", Version = "1", FolderName = "sync_map",
            File = syncEntry.File, Size = syncEntry.Size, Sha256 = syncEntry.Sha256, ContentSha256 = syncEntry.ContentSha256
        };
        using var duplicateClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [syncEntry, duplicateEntry] }, syncPackage))
        { Timeout = Timeout.InfiniteTimeSpan };
        var duplicateCloud = new CloudCatalogService(config, duplicateClient);
        var duplicateSync = new SyncService(duplicateCloud, localContent,
            new InstallService(duplicateCloud, pathService, localContent, backups, tasks, log, config), log);
        var duplicateBlocked = false;
        try { await duplicateSync.SyncAllAsync(["地图"]); }
        catch (InvalidDataException ex) when (ex.Message.Contains("重复目标目录")) { duplicateBlocked = true; }
        check(duplicateBlocked && (await File.ReadAllTextAsync(syncScenario)).Contains("map_version = 9"),
            "云端清单目标目录重复时整批停止，任何玩家目录均不会先被覆盖");

        var nestedParent = Path.Combine(mapsRoot, "wrapper");
        ZipFile.ExtractToDirectory(syncPackagePath, nestedParent);
        var nestedRoot = Path.Combine(nestedParent, "sync_map");
        var nestedBlocked = false;
        try { await syncInstaller.InstallAsync(syncEntry, nestedRoot, existingVersion: "1"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("直接子文件夹")) { nestedBlocked = true; }
        check(nestedBlocked && Directory.Exists(nestedRoot), "自动更新不会覆盖嵌套在包装目录中的玩家内容");

        var modsRoot = Path.Combine(configDirectory, "player", "Mods");
        Directory.CreateDirectory(modsRoot);
        config.Current.ModsDir = modsRoot;
        var modPackage = CreateModPackage("sync_mod");
        var modEntry = new CloudContentEntry
        {
            Kind = "MOD", Id = "sync-mod", Name = "同步回归 MOD", Version = "1",
            FolderName = "sync_mod", File = "packages/sync-mod.zip", Size = modPackage.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modPackage))
        };
        using var modClient = new HttpClient(new ModCatalogPackageHandler(
            new ModManifest { ManifestVersion = 1, Mods = [modEntry] }, modPackage))
        { Timeout = Timeout.InfiniteTimeSpan };
        var modCloud = new CloudCatalogService(config, modClient);
        var modInstaller = new InstallService(modCloud, pathService, localContent, backups, tasks, log, config);
        var modSync = new SyncService(modCloud, localContent, modInstaller, log);
        var modRoot = Path.Combine(modsRoot, "sync_mod");
        await syncPreferences.SetExcludedAsync(modEntry, true);
        var preferredModSync = new SyncService(modCloud, localContent,
            new InstallService(modCloud, pathService, localContent, backups, tasks, log, config, syncPreferences), log, syncPreferences);
        var excludedModSync = await preferredModSync.SyncAllAsync(["MOD"]);
        check(excludedModSync.Skipped == 1 && excludedModSync.Installed == 0 && !Directory.Exists(modRoot),
            "不喜欢的云端 MOD 也不会被一键同步自动安装");
        await syncPreferences.SetExcludedAsync(modEntry, false);
        var firstModSync = await modSync.SyncAllAsync(["MOD"]);
        check(firstModSync.Installed == 1 && firstModSync.Failed == 0 && File.Exists(Path.Combine(modRoot, "mod_info.lua")),
            "一键同步会安装并识别有效 MOD");
        var modBackupsBeforeRepeat = (await backups.ListAsync()).Count;
        var repeatedModSync = await modSync.SyncAllAsync(["MOD"]);
        check(repeatedModSync.Skipped == 1 && repeatedModSync.Installed == 0 &&
              (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
            "无内容指纹的同版本 MOD 再次同步不会重复安装或创建备份");
        var repeatedManualMod = await modInstaller.InstallAsync(modEntry, modRoot, existingVersion: "1");
        check(!repeatedManualMod && (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
            "手动再次安装完全相同的 MOD 包不会重复覆盖或备份");
        var releaseLabeledMod = new CloudContentEntry
        {
            Kind = "MOD", Id = modEntry.Id, Name = modEntry.Name, Version = "2026.08.15", GameVersion = "1",
            FolderName = modEntry.FolderName, File = modEntry.File, Size = modEntry.Size, Sha256 = modEntry.Sha256
        };
        var releaseLabeledInstall = await modInstaller.InstallAsync(releaseLabeledMod, modRoot, existingVersion: "1");
        check(!releaseLabeledInstall && (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
            "发布版本是日期但包内游戏版本一致时能安全验包且不重复覆盖");
        var changedVersionBlocked = false;
        try { await modInstaller.InstallAsync(modEntry, modRoot, existingVersion: "0"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("版本在安装期间发生变化")) { changedVersionBlocked = true; }
        check(changedVersionBlocked && File.Exists(Path.Combine(modRoot, "mod_info.lua")) &&
              (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
            "安装前复核本地版本，版本已变化时不覆盖玩家 MOD");
        var wrongModVersion = new CloudContentEntry
        {
            Kind = "MOD", Id = modEntry.Id, Name = modEntry.Name, Version = "2",
            FolderName = modEntry.FolderName, File = modEntry.File, Size = modEntry.Size, Sha256 = modEntry.Sha256
        };
        var wrongPackageBlocked = false;
        try { await modInstaller.InstallAsync(wrongModVersion, modRoot, existingVersion: "1"); }
        catch (InvalidDataException ex) when (ex.Message.Contains("版本不一致")) { wrongPackageBlocked = true; }
        check(wrongPackageBlocked && File.Exists(Path.Combine(modRoot, "mod_info.lua")) &&
              (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
            "云端标注版本与 MOD 包内真实版本不一致时保留玩家原文件");
        var scannedMod = (await localContent.ScanAsync("MOD")).Single(x => x.Root == modRoot);
        check(scannedMod.Valid && scannedMod.Id == "sync_mod" && scannedMod.Version == "1", "安装后的 MOD 可被本地扫描识别");
        var modExtra = Path.Combine(modRoot, "player_extra.lua");
        await File.WriteAllTextAsync(modExtra, "player mod change");
        var editedModSync = await modSync.SyncAllAsync(["MOD"]);
        check(editedModSync.Skipped == 1 && File.Exists(modExtra),
            "缺少云端内容指纹时不会擅自覆盖同版本 MOD 的玩家改动");
        await modInstaller.UninstallAsync(scannedMod);
        check(!Directory.Exists(modRoot), "MOD 安全卸载会移除目标目录");
        var modBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == scannedMod.Name);
        check(modBackup is not null && File.Exists(Path.Combine(modBackup.ContentRoot, "player_extra.lua")),
            "MOD 卸载前会保留玩家文件备份");
        if (modBackup is not null)
        {
            await backups.RestoreAsync(modBackup);
            check(File.Exists(modExtra) && (await localContent.AnalyzeDirectoryAsync(modRoot)).Valid,
                "MOD 历史备份可恢复为有效内容");
        }

        await CheckCancellationAsync(configDirectory, mapsRoot, config, pathService, localContent, backups, tasks, log, check);
    }

    private static async Task CheckCancellationAsync(string configDirectory, string mapsRoot, ConfigService config,
        GamePathService paths, LocalContentService local, BackupService backups, TaskService tasks,
        LogService log, Action<bool, string> check)
    {
        var payload = CreateMapPackage("cancel_map");
        var zipPath = Path.Combine(configDirectory, "cancel-map.zip");
        await File.WriteAllBytesAsync(zipPath, payload);
        ZipFile.ExtractToDirectory(zipPath, mapsRoot);
        var destination = Path.Combine(mapsRoot, "cancel_map");
        var officialHash = await ContentHash.DirectorySha256Async(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "player.txt"), "retain on cancellation");
        var originalHash = await ContentHash.DirectorySha256Async(destination);
        var entry = new CloudContentEntry
        {
            Kind = "地图", Id = "cancel_map", Name = "同步取消回归地图", Version = "1",
            FolderName = "cancel_map", File = "packages/sync-map.zip", Size = payload.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            ContentSha256 = officialHash
        };
        using var client = new HttpClient(new CatalogPackageHandler(
            new MapManifest { ManifestVersion = 1, Maps = [entry] }, payload)) { Timeout = Timeout.InfiniteTimeSpan };
        var catalog = new CloudCatalogService(config, client);
        var installer = new InstallService(catalog, paths, local, backups, tasks, log, config);
        var sync = new SyncService(catalog, local, installer, log);
        using var cts = new CancellationTokenSource();
        AppTask? observed = null;
        System.ComponentModel.PropertyChangedEventHandler cancelOnInstall = (sender, args) =>
        {
            if (args.PropertyName == nameof(AppTask.Detail) &&
                sender is AppTask task && task.Detail == "正在安装到游戏目录")
                cts.Cancel();
        };
        System.Collections.Specialized.NotifyCollectionChangedEventHandler watchTask = (_, args) =>
        {
            if (args.NewItems is null) return;
            foreach (AppTask task in args.NewItems)
                if (task.Name == "地图 · 同步取消回归地图")
                {
                    observed = task;
                    task.PropertyChanged += cancelOnInstall;
                }
        };
        tasks.Tasks.CollectionChanged += watchTask;
        var canceled = false;
        try { await sync.SyncAllAsync(["地图"], ct: cts.Token); }
        catch (OperationCanceledException) { canceled = true; }
        finally
        {
            tasks.Tasks.CollectionChanged -= watchTask;
            if (observed is not null) observed.PropertyChanged -= cancelOnInstall;
        }
        check(canceled && observed?.Status == "已取消" && observed.RetryCommand.CanExecute(null) &&
            await ContentHash.DirectorySha256Async(destination) == originalHash &&
            !Directory.EnumerateDirectories(mapsRoot, ".scfa_install_*").Any(),
            "真实同步流程取消安装会保留玩家文件、标记已取消并清理暂存");
        var retried = await sync.SyncAllAsync(["地图"]);
        check(retried.Installed == 1 && retried.Failed == 0 &&
            await ContentHash.DirectorySha256Async(destination) == officialHash,
            "同步取消后释放操作锁，再次同步可安全完成");
    }
}
