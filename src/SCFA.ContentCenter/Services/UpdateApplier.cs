using System.Diagnostics;
using SCFA.ContentCenter.Core;

namespace SCFA.ContentCenter.Services;

public static class UpdateApplier
{
    public static async Task ApplyAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--apply-update") throw new ArgumentException("更新应用参数无效");
        var target = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out var parentId) || parentId <= 0) throw new ArgumentException("父进程参数无效");
        var expectedHash = args[3].Trim().ToLowerInvariant();
        if (expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("更新 SHA-256 参数无效");
        var staged = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定更新程序路径"));
        EnsureStagedPath(staged);
        if (!Path.GetFileName(target).Equals("SCFA内容中心.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新目标文件名无效");
        if (!File.Exists(target)) throw new FileNotFoundException("更新目标程序不存在", target);
        var stagedHash = await ContentHash.FileSha256Async(staged);
        if (!stagedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("待应用更新 SHA-256 不匹配");
        await WaitForExitAsync(parentId, TimeSpan.FromSeconds(30));

        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("更新目标目录无效");
        var newPath = target + ".new_" + Guid.NewGuid().ToString("N");
        var oldPath = target + ".pre-update_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try
        {
            File.Copy(staged, newPath, false);
            var copiedHash = await ContentHash.FileSha256Async(newPath);
            if (!copiedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新复制后 SHA-256 不匹配");
            File.Move(target, oldPath);
            try { File.Move(newPath, target); }
            catch { File.Move(oldPath, target); throw; }
            var start = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = directory };
            start.ArgumentList.Add("--cleanup-update");
            start.ArgumentList.Add(staged);
            start.ArgumentList.Add(oldPath);
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            try
            {
                _ = Process.Start(start) ?? throw new InvalidOperationException("更新完成但无法启动新客户端");
            }
            catch (Exception startError)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (File.Exists(oldPath)) File.Move(oldPath, target);
                }
                catch (Exception restoreError)
                {
                    throw new IOException($"新客户端启动失败，且旧客户端恢复失败（旧文件位于 {oldPath}）：启动错误={startError.Message}；恢复错误={restoreError.Message}", startError);
                }
                throw new IOException("新客户端启动失败，旧客户端已恢复：" + startError.Message, startError);
            }
        }
        finally { try { if (File.Exists(newPath)) File.Delete(newPath); } catch { } }
    }

    public static async Task CleanupAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--cleanup-update") return;
        var staged = Path.GetFullPath(args[1]);
        var old = Path.GetFullPath(args[2]);
        if (!int.TryParse(args[3], out var parentId) || parentId <= 0) return;
        EnsureStagedPath(staged);
        var current = Path.GetFullPath(Environment.ProcessPath ?? "");
        var expectedOldPrefix = current + ".pre-update_";
        if (!old.StartsWith(expectedOldPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新旧文件清理路径越界");
        await WaitForExitAsync(parentId, TimeSpan.FromSeconds(30));
        TryDelete(staged);
        TryDelete(old);
        try
        {
            var directory = Path.GetDirectoryName(staged);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }

    private static void EnsureStagedPath(string path)
    {
        var root = Path.GetFullPath(Path.Combine(ConfigService.ResolveDataDirectory(), "Updates")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新暂存程序路径越界");
    }
    private static async Task WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (ArgumentException) { }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
