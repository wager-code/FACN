using System.Runtime.InteropServices;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class DiagnosticsService(
    ConfigService config,
    GamePathService paths,
    CloudCatalogService cloud,
    AuthApiClient auth,
    UserSessionService session,
    LogService log)
{
    public async Task<DiagnosticReport> RunAsync(bool includeNetwork = true, CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        Add(items, "软件", "客户端版本", true, AppVersion.Informational);
        Add(items, "软件", "运行环境", true, $"{RuntimeInformation.OSDescription} · .NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}");
        Add(items, "软件", "配置文件", File.Exists(config.ConfigPath), config.ConfigPath);
        Add(items, "软件", "应用数据目录", Directory.Exists(ConfigService.ResolveDataDirectory()), ConfigService.ResolveDataDirectory());

        var configuredRoot = config.Current.GameRoot?.Trim() ?? "";
        var detected = paths.DiscoverGameRoots();
        Add(items, "游戏", "游戏安装目录", configuredRoot.Length > 0 && Directory.Exists(configuredRoot),
            configuredRoot.Length > 0 ? configuredRoot : detected.Count > 0 ? "未保存；检测到 " + detected[0] : "未配置且未自动检测到");
        AddGameRuntimeChecks(items, configuredRoot.Length > 0 ? configuredRoot : detected.FirstOrDefault() ?? "");
        TryCheckConfiguredContentDirectory(items, paths, "地图", "地图目录");
        TryCheckConfiguredContentDirectory(items, paths, "MOD", "MOD 目录");

        try
        {
            var saved = await session.LoadAsync();
            Add(items, "账号", "本地安全会话", saved is not null && !saved.IsExpired,
                saved is null ? "未保存登录会话" : saved.IsExpired ? "会话已过期" : $"{saved.User.Username} · {saved.User.RoleLabel}");
        }
        catch (Exception ex)
        {
            Add(items, "账号", "本地安全会话", false, ex.Message);
        }

        if (includeNetwork)
        {
            try
            {
                AuthApiClient.ValidateConfiguration(auth.BaseUrl, auth.PinnedCertSha256);
                var health = await auth.HealthAsync(ct);
                Add(items, "网络", "登录服务", health.Ok, health.Ok ? $"连接正常 · 服务版本 {health.Version}" : "连接异常");
                Add(items, "网络", "更新服务", health.UpdaterReady, health.UpdaterReady ? $"服务端已就绪 {health.UpdaterVersion}".Trim() : "服务端未就绪");
            }
            catch (Exception ex)
            {
                Add(items, "网络", "账号 API", false, ex.Message);
            }

            try
            {
                var mapsTask = cloud.FetchAsync("地图", ct);
                var modsTask = cloud.FetchAsync("MOD", ct);
                await Task.WhenAll(mapsTask, modsTask);
                Add(items, "网络", "COS 正式清单", true, $"地图 {mapsTask.Result.Count} 项 · MOD {modsTask.Result.Count} 项");
            }
            catch (Exception ex)
            {
                Add(items, "网络", "COS 正式清单", false, ex.Message);
            }
        }
        else
        {
            Add(items, "网络", "联网检测", true, "本次已跳过");
        }

        var report = new DiagnosticReport
        {
            AppVersion = AppVersion.Informational,
            OperatingSystem = RuntimeInformation.OSDescription,
            Items = items
        };
        log.Info($"诊断完成: 通过 {report.Passed}, 问题 {report.Problems}");
        return report;
    }

    private static void AddGameRuntimeChecks(List<DiagnosticItem> items, string gameRoot)
    {
        if (!GamePathService.TryFindScfaExecutable(gameRoot, out var executable))
        {
            Add(items, "游戏运行环境", "游戏主程序", false, "尚未识别可执行文件；支持标准名称和带 SCFA 特征文件的改名 EXE");
            Add(items, "游戏运行环境", "运行补丁策略", true, "仅执行只读检测；没有可信清单、SHA-256 与签名时不会修改游戏文件");
            return;
        }

        Add(items, "游戏运行环境", "游戏主程序", true, executable);
        try
        {
            var largeAddressAware = HasLargeAddressAwareFlag(executable);
            Add(items, "游戏运行环境", "4GB / LAA 标志", largeAddressAware,
                largeAddressAware ? "主程序已启用 Large Address Aware" : "主程序未检测到 LAA 标志；当前只读检测，不自动改写 EXE");
        }
        catch (Exception ex) { Add(items, "游戏运行环境", "4GB / LAA 标志", false, "读取失败：" + ex.Message); }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dx = new[] { Path.Combine(windows, "SysWOW64", "d3dx9_43.dll"), Path.Combine(windows, "System32", "d3dx9_43.dll") }.FirstOrDefault(File.Exists);
        Add(items, "游戏运行环境", "DirectX 9 旧版组件", dx is not null, dx ?? "未发现 d3dx9_43.dll；可从微软官方 DirectX End-User Runtime 安装");

        var bin = Path.GetDirectoryName(executable)!;
        var vcFiles = new[] { "msvcp80.dll", "msvcr80.dll" };
        var missing = vcFiles.Where(name => !File.Exists(Path.Combine(bin, name))).ToArray();
        Add(items, "游戏运行环境", "Visual C++ x86 运行库", missing.Length == 0,
            missing.Length == 0 ? "游戏目录已包含 msvcp80.dll 与 msvcr80.dll" : "游戏目录缺少：" + string.Join("、", missing));
        Add(items, "游戏运行环境", "运行补丁策略", true, "安装入口保持锁定：只有服务器提供可信清单、SHA-256/签名、明确确认与可回滚备份后才允许安装");
    }

    private static bool HasLargeAddressAwareFlag(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("不是有效 PE 文件");
        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset < 64 || peOffset > stream.Length - 24) throw new InvalidDataException("PE 头偏移无效");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("PE 签名无效");
        stream.Position += 18;
        return (reader.ReadUInt16() & 0x20) != 0;
    }

    private static void CheckContentDirectory(List<DiagnosticItem> items, string label, string path)
    {
        if (!Directory.Exists(path))
        {
            Add(items, "游戏", label, false, "目录不存在：" + path);
            return;
        }
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                Add(items, "游戏", label, false, "目录是符号链接/重解析点：" + path);
                return;
            }
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            var drive = string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
            var free = drive is { IsReady: true } ? $" · 可用 {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB" : "";
            Add(items, "游戏", label, true, path + free);
        }
        catch (Exception ex)
        {
            Add(items, "游戏", label, false, ex.Message);
        }
    }

    private static void TryCheckConfiguredContentDirectory(List<DiagnosticItem> items, GamePathService paths, string kind, string label)
    {
        try { CheckContentDirectory(items, label, paths.GetContentDirectory(kind)); }
        catch (Exception ex) { Add(items, "游戏", label, false, ex.Message); }
    }

    private static void Add(List<DiagnosticItem> items, string category, string check, bool passed, string detail) =>
        items.Add(new DiagnosticItem { Category = category, Check = check, Passed = passed, Status = passed ? "正常" : "需处理", Detail = detail });
}
