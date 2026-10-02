using System.Diagnostics;
using System.Text.Json;
using SCFA.ContentCenter.Core;

namespace SCFA.ContentCenter.Services;

/// <summary>Confirms initialized startup, and stops a failed child before rollback.</summary>
internal static class UpdateStartupHandshake
{
    private sealed record Receipt(string Token, string Target, string Sha256);

    internal static string ReadyPath(string staged, string token)
    {
        if (!Guid.TryParseExact(token, "N", out _))
            throw new ArgumentException("更新启动确认参数无效");
        return staged + ".startup_" + token + ".json";
    }

    internal static async Task SignalReadyAsync(string staged, string token, string current, string expectedHash)
    {
        var actual = await ContentHash.FileSha256Async(current);
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("运行中的新客户端与更新 SHA-256 不一致");
        var ready = ReadyPath(staged, token);
        var temporary = ready + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Receipt(token, current, actual)));
            File.Move(temporary, ready, false);
        }
        finally { TryDelete(temporary); }
    }

    internal static async Task WaitAsync(Process child, string staged, string token, string target,
        string expectedHash, TimeSpan timeout)
    {
        var ready = ReadyPath(staged, token);
        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.Elapsed < timeout)
            {
                if (child.HasExited) throw new IOException("新客户端在完成初始化前退出");
                if (ReadMatchingReceipt(ready, token, target, expectedHash)) return;
                await Task.Delay(100);
            }
            throw new TimeoutException("新客户端未在规定时间内完成初始化");
        }
        catch
        {
            // Never restore a Windows executable while its failed child is still
            // using it. This Process handle refers only to the child we launched.
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await child.WaitForExitAsync(deadline.Token);
                }
                catch (Exception stopError)
                {
                    if (!child.HasExited) throw new UpdateChildStillRunningException(stopError);
                }
            }
            throw;
        }
        finally { TryDelete(ready); }
    }

    private static bool ReadMatchingReceipt(string path, string token, string target, string expectedHash)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4096) return false;
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(path));
            return receipt is not null && receipt.Token == token && receipt.Target is not null && receipt.Sha256 is not null &&
                receipt.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                receipt.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

internal sealed class UpdateChildStillRunningException(Exception inner)
    : IOException("新客户端未能停止，旧程序备份已保留，请关闭新客户端后恢复", inner) { }
