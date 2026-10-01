using SCFA.ContentCenter.Services;
using System.IO;

internal static class InstallTransactionRegression
{
    public static async Task RunAsync(string configDirectory, Action<bool, string> check)
    {
        var root = Path.Combine(configDirectory, "install-transactions");
        Directory.CreateDirectory(root);
        foreach (var count in new[] { 1, 2 })
        {
            var trial = Path.Combine(root, "cancel-" + count);
            var source = Path.Combine(trial, "source");
            var destination = Path.Combine(trial, "original-0");
            var replacements = await PrepareAsync(trial, source, count);
            var originalHashes = await HashesAsync(replacements);
            using var cts = new CancellationTokenSource();
            var reachedVerification = false;
            var canceled = false;
            try
            {
                await ContentInstallTransaction.ReplaceAsync(source, destination, replacements, ct =>
                {
                    reachedVerification = File.Exists(Path.Combine(destination, "new.txt")) &&
                        replacements.All(x => Directory.Exists(x.Retired));
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }, cts.Token, () => { });
            }
            catch (OperationCanceledException ex) { canceled = ex.CancellationToken == cts.Token; }
            catch (IOException) { }
            check(reachedVerification && canceled,
                count == 1 ? "普通更新替换后取消仍向调用方报告取消" : "冲突清理替换后取消仍向调用方报告取消");
            check((await HashesAsync(replacements)).SequenceEqual(originalHashes) &&
                replacements.All(x => !Directory.Exists(x.Retired)) && !File.Exists(Path.Combine(destination, "new.txt")),
                count == 1 ? "普通更新取消恢复原目录完整指纹" : "冲突清理取消恢复全部原目录完整指纹");
        }

        var failureTrial = Path.Combine(root, "verify-failure");
        var failureSource = Path.Combine(failureTrial, "source");
        var failureCopies = await PrepareAsync(failureTrial, failureSource, 2);
        var failureHashes = await HashesAsync(failureCopies);
        var verificationError = new InvalidDataException("fixture verification failed");
        var preservedError = false;
        try
        {
            await ContentInstallTransaction.ReplaceAsync(failureSource, failureCopies[0].Original,
                failureCopies, _ => Task.FromException(verificationError), CancellationToken.None, () => { });
        }
        catch (IOException ex) { preservedError = ReferenceEquals(ex.InnerException, verificationError); }
        check(preservedError && (await HashesAsync(failureCopies)).SequenceEqual(failureHashes),
            "复检失败恢复所有原目录并保留原始错误");

        var rollbackTrial = Path.Combine(root, "rollback-failure");
        var rollbackSource = Path.Combine(rollbackTrial, "source");
        var rollbackCopies = await PrepareAsync(rollbackTrial, rollbackSource, 2);
        var rollbackHashes = await HashesAsync(rollbackCopies);
        using var rollbackCts = new CancellationTokenSource();
        var preserveCalled = false;
        var rollbackFailed = false;
        try
        {
            await ContentInstallTransaction.ReplaceAsync(rollbackSource, rollbackCopies[0].Original,
                rollbackCopies, _ =>
                {
                    // Simulate another program occupying an original directory during verification.
                    Directory.CreateDirectory(rollbackCopies[1].Original);
                    File.WriteAllText(Path.Combine(rollbackCopies[1].Original, "occupied.txt"), "external");
                    rollbackCts.Cancel();
                    return Task.FromCanceled(rollbackCts.Token);
                }, rollbackCts.Token, () => preserveCalled = true);
        }
        catch (IOException ex)
        {
            rollbackFailed = ex.InnerException is OperationCanceledException &&
                ex.Message.Contains(rollbackCopies[1].Retired, StringComparison.Ordinal);
        }
        check(preserveCalled && rollbackFailed &&
            await SCFA.ContentCenter.Core.ContentHash.DirectorySha256Async(rollbackCopies[1].Retired) == rollbackHashes[1] &&
            await SCFA.ContentCenter.Core.ContentHash.DirectorySha256Async(rollbackCopies[0].Original) == rollbackHashes[0] &&
            File.Exists(Path.Combine(rollbackCopies[1].Original, "occupied.txt")),
            "取消但恢复失败报告失败并保留旧副本和外部占用目录");

        var moveTrial = Path.Combine(root, "move-failure");
        var moveSource = Path.Combine(moveTrial, "source");
        var moveCopies = await PrepareAsync(moveTrial, moveSource, 2);
        var missing = Path.Combine(moveTrial, "missing");
        var moveFailed = false;
        try
        {
            await ContentInstallTransaction.ReplaceAsync(moveSource, moveCopies[0].Original,
                [moveCopies[0], (missing, missing + ".retired")],
                _ => throw new InvalidOperationException("verification must not start"),
                CancellationToken.None, () => { });
        }
        catch (IOException) { moveFailed = true; }
        check(moveFailed && File.Exists(Path.Combine(moveCopies[0].Original, "old.txt")) &&
            Directory.Exists(moveSource) && !Directory.Exists(moveCopies[0].Retired),
            "移动部分旧目录失败时恢复已移动目录且不安装新内容");


        var occupiedTrial = Path.Combine(root, "occupied-target");
        var occupiedSource = Path.Combine(occupiedTrial, "source");
        var occupiedCopies = await PrepareAsync(occupiedTrial, occupiedSource, 1);
        var occupiedDestination = Path.Combine(occupiedTrial, "external-target");
        Directory.CreateDirectory(occupiedDestination);
        await File.WriteAllTextAsync(Path.Combine(occupiedDestination, "external.txt"), "retain");
        var occupiedFailed = false;
        try
        {
            await ContentInstallTransaction.ReplaceAsync(occupiedSource, occupiedDestination, occupiedCopies,
                _ => throw new InvalidOperationException("verification must not start"),
                CancellationToken.None, () => { });
        }
        catch (IOException) { occupiedFailed = true; }
        check(occupiedFailed && File.Exists(Path.Combine(occupiedDestination, "external.txt")) &&
            File.Exists(Path.Combine(occupiedCopies[0].Original, "old.txt")) && Directory.Exists(occupiedSource),
            "目标被其他程序占用导致移动失败时保留外部目录和原内容");

        var earlyTrial = Path.Combine(root, "early-cancel");
        var earlySource = Path.Combine(earlyTrial, "source");
        var earlyCopies = await PrepareAsync(earlyTrial, earlySource, 1);
        using var earlyCts = new CancellationTokenSource();
        earlyCts.Cancel();
        var earlyCanceled = false;
        try
        {
            await ContentInstallTransaction.ReplaceAsync(earlySource, earlyCopies[0].Original, earlyCopies,
                _ => throw new InvalidOperationException("verification must not start"),
                earlyCts.Token, () => { });
        }
        catch (OperationCanceledException) { earlyCanceled = true; }
        check(earlyCanceled && Directory.Exists(earlySource) && !Directory.Exists(earlyCopies[0].Retired) &&
            File.Exists(Path.Combine(earlyCopies[0].Original, "old.txt")),
            "目录事务开始前取消不移动玩家目录或暂存包");
        var successTrial = Path.Combine(root, "success");
        var successSource = Path.Combine(successTrial, "source");
        var successCopies = await PrepareAsync(successTrial, successSource, 2);
        await ContentInstallTransaction.ReplaceAsync(successSource, successCopies[0].Original, successCopies,
            _ => Task.CompletedTask, CancellationToken.None, () => { });
        check(File.Exists(Path.Combine(successCopies[0].Original, "new.txt")) &&
            !Directory.Exists(successCopies[1].Original) && successCopies.All(x => !Directory.Exists(x.Retired)),
            "成功复检后才清理普通更新与冲突目录的暂存旧副本");
    }

    private static async Task<(string Original, string Retired)[]> PrepareAsync(string trial, string source, int count)
    {
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new content");
        var copies = Enumerable.Range(0, count).Select(i =>
            (Original: Path.Combine(trial, "original-" + i), Retired: Path.Combine(trial, "retired-" + i))).ToArray();
        for (var i = 0; i < copies.Length; i++)
        {
            Directory.CreateDirectory(copies[i].Original);
            await File.WriteAllTextAsync(Path.Combine(copies[i].Original, "old.txt"), "player content " + i);
        }
        return copies;
    }

    private static async Task<string[]> HashesAsync(IEnumerable<(string Original, string Retired)> copies)
    {
        var hashes = new List<string>();
        foreach (var (original, _) in copies)
            hashes.Add(Directory.Exists(original)
                ? await SCFA.ContentCenter.Core.ContentHash.DirectorySha256Async(original)
                : "<missing>");
        return hashes.ToArray();
    }
}
