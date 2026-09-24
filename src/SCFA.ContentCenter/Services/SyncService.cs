using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public readonly record struct SyncDecision(bool ShouldInstall, bool VerifyContentHash, string Reason);

public sealed class SyncService(CloudCatalogService cloud, LocalContentService local, InstallService installer, LogService log)
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public static SyncDecision Decide(LocalContentEntry? matched, CloudContentEntry remote)
    {
        if (matched is null) return new(true, false, "本地缺失");
        if (!matched.Valid)
        {
            var detail = string.IsNullOrWhiteSpace(matched.Detail) ? "" : "（" + matched.Detail + "）";
            return new(true, false, "本地内容损坏" + detail);
        }

        var comparison = ContentIdentity.CompareVersions(matched.Version, remote.Version);
        if (comparison.Ordered && comparison.Compare < 0) return new(true, false, "本地版本较旧");
        if (comparison.Ordered && comparison.Compare > 0) return new(false, false, "本地版本较新，保护不降级");
        if (comparison.Ordered && comparison.Compare == 0 && !string.IsNullOrWhiteSpace(remote.EffectiveContentHash))
            return new(false, true, "正在校验内容指纹");
        if (comparison.Ordered && comparison.Compare == 0)
            return new(false, false, "版本相同，但云端缺少内容指纹；为避免重复覆盖已跳过");
        if (!comparison.Ordered) return new(false, false, "版本无法安全排序，避免自动覆盖");
        return new(false, false, "已是最新");
    }

    public async Task<SyncSummary> SyncAllAsync(IEnumerable<string> kinds, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (!await _syncGate.WaitAsync(0, ct))
            throw new InvalidOperationException("已有同步任务正在运行，请在同步中心查看进度或取消当前任务");
        try
        {
            var summary = new SyncSummary();
            foreach (var kind in kinds)
            {
                ct.ThrowIfCancellationRequested(); status?.Report($"正在刷新{kind}云端清单…");
                var remote = await cloud.FetchAsync(kind, ct);
                status?.Report($"正在扫描本地{kind}…");
                // 无效内容也必须参与精确 ID/目录匹配，否则损坏目录会被当作“本地缺失”，
                // 随后的全新安装又会因为目标目录已存在而失败，最终永远无法自动修复。
                var locals = (await local.ScanAsync(kind, ct)).ToArray();
                // 先完成整份清单的匹配。冲突必须在任何目录被改动之前发现，
                // 否则清单中排在前面的项目可能已经覆盖了冲突目录。
                var plans = remote.Select(entry => (Entry: entry, Match: ContentIdentity.FindBestResult(locals, entry))).ToArray();
                var conflictingRoots = plans
                    .Where(plan => !plan.Match.Ambiguous && plan.Match.Entry is not null)
                    .GroupBy(plan => Path.GetFullPath(plan.Match.Entry!.Root), StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (entry, match) in plans)
                {
                    ct.ThrowIfCancellationRequested();
                    if (match.Ambiguous)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.EffectiveContentHash))
                        {
                            var verifiedCopy = false;
                            try
                            {
                                // 多个版本共享同一地图 ID 时，只要其中已有经过版本和整目录指纹
                                // 双重确认的云端副本，就无需选择或覆盖任何一个玩家目录。
                                foreach (var candidate in locals.Where(x =>
                                    x.Valid && ContentIdentity.MatchScore(x, entry) == match.Score &&
                                    ContentIdentity.VersionsEquivalent(x.Version, entry.Version)))
                                {
                                    status?.Report($"核对 {entry.Name} 的重复副本…");
                                    var hash = await ContentHash.DirectorySha256Async(candidate.Root, ct);
                                    if (!hash.Equals(entry.EffectiveContentHash.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                                    verifiedCopy = true;
                                    break;
                                }
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception ex)
                            {
                                summary.Failed++;
                                summary.Messages.Add($"{kind} {entry.Name}：重复副本校验失败 - {ex.Message}");
                                log.Error("同步重复副本校验失败: " + entry.Name, ex);
                                continue;
                            }
                            if (verifiedCopy)
                            {
                                summary.Skipped++;
                                summary.Messages.Add($"{kind} {entry.Name}：已有与云端完全一致的副本；其他本地副本保留，未重复覆盖");
                                continue;
                            }
                        }
                        summary.Failed++;
                        summary.Messages.Add($"{kind} {entry.Name}：存在多个同等匹配的本地目录，已阻止自动安装");
                        log.Error($"同步匹配冲突: {kind} {entry.Name} ({entry.Id})");
                        continue;
                    }
                    var matched = match.Entry;
                    if (matched is not null && conflictingRoots.Contains(Path.GetFullPath(matched.Root)))
                    {
                        summary.Failed++;
                        summary.Messages.Add($"{kind} {entry.Name}：同一本地目录匹配了多个云端项目，已阻止重复覆盖");
                        log.Error($"同步本地目录重复匹配: {kind} {matched.Root}");
                        continue;
                    }
                    var decision = Decide(matched, entry);
                    var shouldInstall = decision.ShouldInstall;
                    var reason = decision.Reason;
                    if (decision.VerifyContentHash)
                    {
                        try
                        {
                            status?.Report($"校验 {entry.Name} 内容指纹…");
                            var localHash = await ContentHash.DirectorySha256Async(matched!.Root, ct);
                            if (!localHash.Equals(entry.EffectiveContentHash, StringComparison.OrdinalIgnoreCase))
                            {
                                shouldInstall = true;
                                reason = "版本相同但内容校验不一致";
                            }
                            else reason = "已是最新";
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            summary.Failed++;
                            summary.Messages.Add($"{kind} {entry.Name}：内容指纹校验失败 - {ex.Message}");
                            log.Error("同步内容指纹校验失败: " + entry.Name, ex);
                            continue;
                        }
                    }
                    if (!shouldInstall) { summary.Skipped++; summary.Messages.Add($"{kind} {entry.Name}：{reason}"); continue; }
                    try
                    {
                        status?.Report($"{reason}，正在安装 {entry.Name} V{entry.Version}…");
                        if (await installer.InstallAsync(entry, matched?.Root, ct, matched?.Version))
                        {
                            summary.Installed++;
                            summary.Messages.Add($"{kind} {entry.Name}：{reason} → 已安装");
                        }
                        else
                        {
                            summary.Skipped++;
                            summary.Messages.Add($"{kind} {entry.Name}：内容已相同，未重复覆盖");
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { summary.Failed++; summary.Messages.Add($"{kind} {entry.Name}：失败 - {ex.Message}"); log.Error("同步失败: " + entry.Name, ex); }
                }
            }
            return summary;
        }
        finally
        {
            _syncGate.Release();
        }
    }
}
