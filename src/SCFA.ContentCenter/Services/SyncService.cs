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
            return new(true, false, "正式清单缺少内容指纹，严格一致性模式重新安装");
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
                foreach (var entry in remote)
                {
                    ct.ThrowIfCancellationRequested();
                    var match = ContentIdentity.FindBestResult(locals, entry);
                    if (match.Ambiguous)
                    {
                        summary.Failed++;
                        summary.Messages.Add($"{kind} {entry.Name}：存在多个同等匹配的本地目录，已阻止自动安装");
                        log.Error($"同步匹配冲突: {kind} {entry.Name} ({entry.Id})");
                        continue;
                    }
                    var matched = match.Entry;
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
                        await installer.InstallAsync(entry, matched?.Root, ct, matched?.Version); summary.Installed++; summary.Messages.Add($"{kind} {entry.Name}：{reason} → 已安装");
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
