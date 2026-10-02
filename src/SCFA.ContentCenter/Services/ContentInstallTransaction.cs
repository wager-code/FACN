namespace SCFA.ContentCenter.Services;

/// <summary>Moves prepared content into place and restores every retired directory on verification failure.</summary>
internal static class ContentInstallTransaction
{
    internal static async Task ReplaceAsync(string source, string destination,
        IReadOnlyList<(string Original, string Retired)> replacements,
        Func<CancellationToken, Task> verify, CancellationToken ct, Action preserveRetired)
    {
        ct.ThrowIfCancellationRequested();
        var moved = new List<(string Original, string Retired)>();
        var installed = false;
        try
        {
            foreach (var replacement in replacements)
            {
                Directory.Move(replacement.Original, replacement.Retired);
                moved.Add(replacement);
            }
            Directory.Move(source, destination);
            installed = true;
            await verify(ct);
        }
        catch (Exception installError)
        {
            var restoreErrors = new List<string>();
            if (installed && Directory.Exists(destination))
            {
                try { Directory.Delete(destination, true); }
                catch (Exception ex) { restoreErrors.Add("移除未完成的新目录失败：" + ex.Message); }
            }
            foreach (var (original, retired) in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(original)) throw new IOException("原目录已被其他程序占用：" + original);
                    Directory.Move(retired, original);
                }
                catch (Exception ex) { restoreErrors.Add("恢复 " + original + " 失败：" + ex.Message); }
            }
            if (restoreErrors.Count > 0)
            {
                preserveRetired();
                throw new IOException("安装未完成，部分原目录未能自动恢复；旧文件保留在 " +
                    string.Join("、", moved.Select(x => x.Retired)) + "，且有独立备份。" +
                    string.Join("；", restoreErrors), installError);
            }
            // A restored cancellation stays cancellation; an incomplete rollback remains a failure.
            if (installError is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw new IOException("安装新内容失败，原有地图/MOD目录已恢复：" + installError.Message, installError);
        }

        foreach (var (_, retired) in moved)
        {
            try { if (Directory.Exists(retired)) Directory.Delete(retired, true); } catch { }
        }
    }
}
