using System.Diagnostics;
using System.IO;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Services;

internal static class UpdateStartupRegression
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "update-startup-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(ConfigService.ResolveDataDirectory(), "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(staging);
        var target = Path.Combine(directory, "SCFA内容中心.exe");
        var staged = Path.Combine(staging, "SCFA内容中心.exe");
        var original = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(target, original);
        await File.WriteAllBytesAsync(staged, new byte[] { 5, 6, 7, 8 });
        var expectedHash = await ContentHash.FileSha256Async(staged);
        try
        {
            var rejected = false;
            try
            {
                await UpdateApplier.ApplyAsync(
                    ["--apply-update", target, int.MaxValue.ToString(), expectedHash], staged, _ =>
                    {
                        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
                        {
                            UseShellExecute = false, CreateNoWindow = true
                        };
                        start.ArgumentList.Add("/c");
                        start.ArgumentList.Add("exit 23");
                        var child = Process.Start(start)!;
                        child.WaitForExit();
                        return child; // Process creation succeeded, but startup already failed.
                    });
            }
            catch (IOException) { rejected = true; }
            check(rejected, "客户端更新不能把进程创建成功当成初始化成功");
            check(File.ReadAllBytes(target).SequenceEqual(original),
                "新客户端初始化失败后自动恢复旧程序");
            await CheckTransactionsAsync(directory, staging, check);
            await CheckCleanupAsync(directory, staging, check);
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(staging, true);
        }
    }
    private static async Task CheckTransactionsAsync(string directory, string staging, Action<bool, string> check)
    {
        var target = Path.Combine(directory, "SCFA内容中心.exe");
        var staged = Path.Combine(staging, "SCFA内容中心.exe");
        var oldBytes = new byte[] { 1, 2, 3, 4 };
        var newBytes = new byte[] { 5, 6, 7, 8 };
        var expectedHash = await ContentHash.FileSha256Async(staged);
        var backup = await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, (_, old) =>
        {
            check(File.Exists(old) && File.ReadAllBytes(old).SequenceEqual(oldBytes),
                "启动确认期间保留完整旧程序备份");
            return Task.CompletedTask;
        });
        check(File.ReadAllBytes(target).SequenceEqual(newBytes) && File.Exists(backup),
            "新客户端确认成功后保留新程序，旧备份交由启动完成后的清理");

        File.WriteAllBytes(target, oldBytes);
        var called = false;
        try
        {
            await ClientUpdateTransaction.ApplyAsync(staged, target, new string('0', 64), (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });
        }
        catch (InvalidDataException) { }
        check(!called && File.ReadAllBytes(target).SequenceEqual(oldBytes),
            "更新复制校验失败不替换旧程序或启动新进程");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = false;
        try { await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, (_, _) => Task.CompletedTask); }
        catch (IOException) { blocked = true; }
        release.SetResult();
        await first;
        check(blocked, "同一客户端目标的并发更新被独占锁阻止");

        File.WriteAllBytes(target, oldBytes);
        var token = Guid.NewGuid().ToString("N");
        using var child = StartWaitingChild();
        var timeoutRejected = false;
        try
        {
            await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash,
                (current, _) => UpdateStartupHandshake.WaitAsync(child, staged, token, current,
                    expectedHash, TimeSpan.FromMilliseconds(200)));
        }
        catch (IOException) { timeoutRejected = true; }
        check(timeoutRejected && child.HasExited && File.ReadAllBytes(target).SequenceEqual(oldBytes),
            "启动确认超时先停止新进程，再恢复并核对旧程序");

        File.WriteAllBytes(target, newBytes);
        using var readyChild = StartWaitingChild();
        try
        {
            File.WriteAllText(UpdateStartupHandshake.ReadyPath(staged, token),
                "{\"Token\":\"" + token + "\",\"Target\":null,\"Sha256\":null}");
            var waiting = UpdateStartupHandshake.WaitAsync(readyChild, staged, token, target,
                expectedHash, TimeSpan.FromSeconds(5));
            check(!waiting.IsCompleted, "损坏或空字段启动确认不会误报成功");
            File.Delete(UpdateStartupHandshake.ReadyPath(staged, token));
            await UpdateStartupHandshake.SignalReadyAsync(staged, token, target, expectedHash);
            await waiting;
            check(!readyChild.HasExited, "正确的启动确认保留已初始化的新进程");
        }
        finally
        {
            if (!readyChild.HasExited) readyChild.Kill(entireProcessTree: true);
            await readyChild.WaitForExitAsync();
        }

        var stoppedBackup = "";
        var stoppedRejected = false;
        try
        {
            await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, (_, old) =>
            {
                stoppedBackup = old;
                throw new UpdateChildStillRunningException(new IOException("simulated stop failure"));
            });
        }
        catch (IOException ex) { stoppedRejected = ex.Message.Contains("未停止"); }
        check(stoppedRejected && File.Exists(stoppedBackup) && File.ReadAllBytes(target).SequenceEqual(newBytes),
            "新进程无法停止时保留旧备份，不替换仍在运行的程序");

        var preserved = "";
        var recoveryError = false;
        try
        {
            await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, (_, old) =>
            {
                preserved = old;
                File.WriteAllBytes(old, new byte[] { 9 });
                throw new IOException("simulated failed startup");
            });
        }
        catch (IOException ex) { recoveryError = ex.Message.Contains("恢复失败"); }
        check(recoveryError && File.Exists(preserved),
            "回滚备份校验失败保留旧文件并明确报告，不能静默覆盖");
    }

    private static async Task CheckCleanupAsync(string directory, string staging, Action<bool, string> check)
    {
        var current = Path.Combine(directory, "SCFA内容中心.exe");
        var staged = Path.Combine(staging, "SCFA内容中心.exe");
        var old = current + ".pre-update_20261001_120000_" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(old, new byte[] { 1, 2, 3, 4 });
        var hash = await ContentHash.FileSha256Async(current);
        var token = Guid.NewGuid().ToString("N");
        await UpdateApplier.CleanupAsync(["--cleanup-update", staged, old, int.MaxValue.ToString(), token, hash],
            current, Path.GetDirectoryName(staging)!);
        check(!File.Exists(staged) && !File.Exists(old) && File.Exists(current),
            "完成启动确认后清理暂存和旧程序，保留当前程序");
        var ready = UpdateStartupHandshake.ReadyPath(staged, token);
        check(File.Exists(ready), "清理前写入独立随机令牌与程序哈希的启动确认");
        File.Delete(ready);

        File.WriteAllBytes(staged, new byte[] { 5, 6, 7, 8 });
        File.WriteAllBytes(old, new byte[] { 1, 2, 3, 4 });
        var rejected = false;
        try
        {
            await UpdateApplier.CleanupAsync(["--cleanup-update", staged, old,
                int.MaxValue.ToString(), Guid.NewGuid().ToString("N"), new string('0', 64)],
                current, Path.GetDirectoryName(staging)!);
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && File.Exists(staged) && File.Exists(old),
            "启动程序哈希不符时拒绝确认和清理，保留回滚文件");
        var invalidOld = current + ".pre-update_20261001_120000" + Path.DirectorySeparatorChar + "outside.exe";
        var pathRejected = false;
        try
        {
            await UpdateApplier.CleanupAsync(["--cleanup-update", staged, invalidOld, int.MaxValue.ToString()],
                current, Path.GetDirectoryName(staging)!);
        }
        catch (InvalidOperationException) { pathRejected = true; }
        check(pathRejected && File.Exists(old), "旧程序清理拒绝目录嵌套伪装的文件名前缀");

        await UpdateApplier.CleanupAsync(["--cleanup-update", staged, old, int.MaxValue.ToString()],
            current, Path.GetDirectoryName(staging)!);
        check(!File.Exists(staged) && !File.Exists(old), "兼容旧四参数更新协议的启动后清理");
        Directory.CreateDirectory(staging);
    }

    private static Process StartWaitingChild()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping 127.0.0.1 -n 60 > nul");
        return Process.Start(start)!;
    }

}
