using SCFA.ContentCenter.Core;

namespace SCFA.ContentCenter.Services;

/// <summary>Owns the on-disk switch and backup until startup has been verified.</summary>
internal static class ClientUpdateTransaction
{
    internal static async Task<string> ApplyAsync(string staged, string target, string expectedHash,
        Func<string, string, Task> startAndVerify)
    {
        var lockPath = target + ".update-lock";
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var newPath = target + ".new_" + Guid.NewGuid().ToString("N");
        var oldPath = target + ".pre-update_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N");
        var failedPath = target + ".failed-update_" + Guid.NewGuid().ToString("N");
        var oldHash = await ContentHash.FileSha256Async(target);
        var replaced = false;
        var restored = false;
        try
        {
            File.Copy(staged, newPath, false);
            var copiedHash = await ContentHash.FileSha256Async(newPath);
            if (!copiedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新复制后 SHA-256 不匹配");
            // All three files are on the target volume. Replace retains the old
            // executable atomically instead of leaving a missing-target window.
            File.Replace(newPath, target, oldPath);
            replaced = true;
            if (!(await ContentHash.FileSha256Async(oldPath)).Equals(oldHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新备份与原程序 SHA-256 不一致");
            await startAndVerify(target, oldPath);
            return oldPath;
        }
        catch (UpdateChildStillRunningException stopError) when (replaced)
        {
            throw new IOException($"新客户端未停止，已保留旧程序备份（{oldPath}），关闭新客户端后再恢复", stopError);
        }
        catch (Exception updateError) when (replaced)
        {
            try
            {
                if (!(await ContentHash.FileSha256Async(oldPath)).Equals(oldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("旧程序备份在恢复前发生变化");
                File.Replace(oldPath, target, failedPath);
                if (!(await ContentHash.FileSha256Async(target)).Equals(oldHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("恢复后的旧程序 SHA-256 不一致");
                restored = true;
            }
            catch (Exception restoreError)
            {
                throw new IOException($"新客户端启动失败，且旧客户端恢复失败（旧文件位于 {oldPath}）：更新错误={updateError.Message}；恢复错误={restoreError.Message}", updateError);
            }
            throw new IOException("新客户端启动失败，旧客户端已恢复：" + updateError.Message, updateError);
        }
        finally
        {
            TryDelete(newPath);
            if (restored) TryDelete(failedPath);
            // The reusable lock file remains; exclusivity follows the open handle.
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}
