using System.Diagnostics;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Core;

namespace SCFA.ContentCenter.Services;

public static class UpdateApplier
{
    public static Task ApplyAsync(string[] args) => ApplyAsync(args,
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定更新程序路径"), Process.Start);

    internal static async Task ApplyAsync(string[] args, string executablePath, Func<ProcessStartInfo, Process?> startProcess)
    {
        if (args.Length != 4 || args[0] != "--apply-update") throw new ArgumentException("更新应用参数无效");
        var target = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out var parentId) || parentId <= 0) throw new ArgumentException("父进程参数无效");
        var expectedHash = args[3].Trim().ToLowerInvariant();
        ValidateHash(expectedHash);
        var staged = Path.GetFullPath(executablePath);
        EnsureStagedPath(staged, StagingRoot);
        if (!Path.GetFileName(target).Equals("SCFA内容中心.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新目标文件名无效");
        if (target.Equals(staged, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新目标不能与暂存程序相同");
        if (!File.Exists(target)) throw new FileNotFoundException("更新目标程序不存在", target);
        if (!(await ContentHash.FileSha256Async(staged)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("待应用更新 SHA-256 不匹配");
        await WaitForExitAsync(parentId, TimeSpan.FromSeconds(30));

        await ClientUpdateTransaction.ApplyAsync(staged, target, expectedHash, async (current, backup) =>
        {
            var token = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(current)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current)!
            };
            foreach (var value in new[] { "--cleanup-update", staged, backup,
                         Environment.ProcessId.ToString(), token, expectedHash })
                start.ArgumentList.Add(value);
            using var child = startProcess(start) ?? throw new InvalidOperationException("更新完成但无法启动新客户端");
            await UpdateStartupHandshake.WaitAsync(child, staged, token, current, expectedHash, TimeSpan.FromSeconds(120));
        });
    }

    public static Task CleanupAsync(string[] args) => CleanupAsync(args,
        Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前客户端路径")), StagingRoot);

    // Called only after services and the login window have initialized successfully.
    internal static async Task CleanupAsync(string[] args, string current, string stagingRoot)
    {
        if (args.Length is not (4 or 6) || args[0] != "--cleanup-update")
            throw new ArgumentException("更新清理参数无效");
        var staged = Path.GetFullPath(args[1]);
        var old = Path.GetFullPath(args[2]);
        current = Path.GetFullPath(current);
        if (!int.TryParse(args[3], out var parentId) || parentId <= 0)
            throw new ArgumentException("更新清理父进程参数无效");
        EnsureStagedPath(staged, stagingRoot);
        if (staged.Equals(current, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(old), Path.GetDirectoryName(current), StringComparison.OrdinalIgnoreCase) ||
            !old.StartsWith(current + ".pre-update_", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(old[(current.Length + ".pre-update_".Length)..], @"^\d{8}_\d{6}(?:_[a-f0-9]{32})?$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("更新旧文件清理路径越界");

        if (args.Length == 6)
        {
            ValidateHash(args[5]);
            await UpdateStartupHandshake.SignalReadyAsync(staged, args[4], current, args[5]);
        }
        // Legacy four-argument updates retain delayed cleanup without a receipt.
        await WaitForExitAsync(parentId, TimeSpan.FromSeconds(30));
        TryDelete(staged);
        TryDelete(old);
        try
        {
            var directory = Path.GetDirectoryName(staged);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }

    private static string StagingRoot => Path.Combine(ConfigService.ResolveDataDirectory(), "Updates");
    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("更新 SHA-256 参数无效");
    }
    private static void EnsureStagedPath(string path, string stagingRoot)
    {
        var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新暂存程序路径越界");
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
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
