using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

internal static class UpdateStartupProcessRegression
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var current = Path.Combine(repository, "src", "SCFA.ContentCenter", "bin", configuration,
            "net8.0-windows", "SCFA内容中心.exe");
        if (!File.Exists(current)) throw new FileNotFoundException("客户端启动回归缺少实际构建的 apphost", current);
        await RunCaseAsync(root, current, false, false, check);
        await RunCaseAsync(root, current, true, false, check);
        await RunCaseAsync(root, current, true, true, check);
    }

    private static async Task RunCaseAsync(string root, string current, bool failStartup, bool legacy,
        Action<bool, string> check)
    {
        var isolated = Path.Combine(root, "real-update-startup-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(isolated, "data");
        var staging = Path.Combine(data, "Updates", "fixture");
        var staged = Path.Combine(staging, "SCFA内容中心.exe");
        var old = current + ".pre-update_20261001_120000_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var config = new AppConfig
        {
            ApiBaseUrl = failStartup ? "invalid-endpoint" : "https://updates.example.test",
            ApiDirectUrl = "", ApiDirectCertSha256 = "",
            PublicationApiBaseUrl = "https://updates.example.test",
            AutoLogin = false, AutoCheckUpdates = false,
            Bucket = "fixture-bucket-123", Region = "ap-test", Root = "fixture"
        };
        await File.WriteAllTextAsync(Path.Combine(isolated, "config.json"), JsonSerializer.Serialize(config));
        File.Copy(current, staged);
        File.Copy(current, old);
        var hash = await ContentHash.FileSha256Async(current);
        var token = Guid.NewGuid().ToString("N");
        var ready = UpdateStartupHandshake.ReadyPath(staged, token);
        var start = new ProcessStartInfo(current)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(current)!
        };
        foreach (var value in new[] { "--cleanup-update", staged, old, int.MaxValue.ToString() })
            start.ArgumentList.Add(value);
        if (!legacy) { start.ArgumentList.Add(token); start.ArgumentList.Add(hash); }
        start.ArgumentList.Add("--update-startup-smoke");
        start.Environment["SCFA_CONTENT_HUB_CONFIG_DIR"] = isolated;
        start.Environment["SCFA_CONTENT_HUB_DATA_DIR"] = data;
        using var child = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(timeout.Token);
            if (failStartup)
            {
                check(child.ExitCode != 0 && File.Exists(old) && File.Exists(staged) && !File.Exists(ready),
                    legacy ? "真实旧四参数更新的服务初始化失败仍保留旧程序和暂存文件"
                           : "真实 WPF 新客户端初始化失败不确认启动、不清理回滚文件");
            }
            else
            {
                using var receipt = JsonDocument.Parse(File.ReadAllText(ready));
                check(child.ExitCode == 0 && receipt.RootElement.GetProperty("Token").GetString() == token &&
                      receipt.RootElement.GetProperty("Sha256").GetString() == hash &&
                      !File.Exists(old) && !File.Exists(staged),
                    "真实 WPF 新客户端完成服务和登录窗口布局后确认启动并清理");
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (File.Exists(old)) File.Delete(old);
            Directory.Delete(isolated, true);
        }
    }
}
