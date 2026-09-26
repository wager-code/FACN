using System.Text.RegularExpressions;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.Core;

public static partial class SettingsValidator
{
    public static AppConfig ValidateAndNormalize(AppConfig source, GamePathService paths, bool createDirectories)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(paths);
        var result = ConfigService.Clone(source);

        (result.GameRoot, result.MapsDir, result.ModsDir) = ValidateDirectories(result, paths, createDirectories);
        (result.ApiDirectUrl, result.ApiBaseUrl, result.ApiDirectCertSha256, _) = ValidateApiSettings(result);
        (result.Bucket, result.Region, result.Root) = ValidateCosSettings(result);
        (result.UpdateChannel, result.UpdateManifestUrl) = ValidateUpdateSettings(result);

        result.AdminUsersPath = NormalizeApiPath(result.AdminUsersPath, "用户管理 API");
        result.AuditApiPath = NormalizeApiPath(result.AuditApiPath, "审计日志 API");
        result.ContentHistoryApiPath = NormalizeApiPath(result.ContentHistoryApiPath, "内容历史 API");

        return result;
    }

    public static (string GameRoot, string MapsDir, string ModsDir) ValidateDirectories(AppConfig source, GamePathService paths, bool createDirectories)
    {
        var gameRoot = NormalizeGameRoot(source.GameRoot);
        var mapsInput = string.IsNullOrWhiteSpace(source.MapsDir) ? paths.GetDefaultPlayerContentDirectory("地图") : source.MapsDir;
        var modsInput = string.IsNullOrWhiteSpace(source.ModsDir) ? paths.GetDefaultPlayerContentDirectory("MOD") : source.ModsDir;
        if (string.IsNullOrWhiteSpace(mapsInput) || string.IsNullOrWhiteSpace(modsInput))
            throw new ArgumentException("请分别选择游戏实际使用且已经存在的 Maps 和 Mods 目录；软件不会自动创建默认目录");
        var maps = paths.NormalizeContentDirectory(mapsInput, "地图");
        var mods = paths.NormalizeContentDirectory(modsInput, "MOD");
        GamePathService.ValidateContentDirectoryPair(maps, mods);
        _ = createDirectories;
        if (!Directory.Exists(maps)) throw new DirectoryNotFoundException("地图目录不存在，请选择游戏实际使用的已有目录：" + maps);
        if (!Directory.Exists(mods)) throw new DirectoryNotFoundException("MOD 目录不存在，请选择游戏实际使用的已有目录：" + mods);
        return (gameRoot, maps, mods);
    }

    public static (string DirectUrl, string BaseUrl, string Fingerprint, string Endpoint) ValidateApiSettings(AppConfig source)
    {
        var directUrl = source.ApiDirectUrl.Trim();
        var baseUrl = source.ApiBaseUrl.Trim();
        var fingerprint = source.ApiDirectCertSha256.Trim();
        var endpoint = string.IsNullOrWhiteSpace(directUrl) ? baseUrl : directUrl;
        AuthApiClient.ValidateConfiguration(endpoint, fingerprint);
        return (directUrl, baseUrl, fingerprint, AuthApiClient.NormalizeBaseUrl(endpoint));
    }

    public static (string Bucket, string Region, string Root) ValidateCosSettings(AppConfig source)
    {
        var bucket = source.Bucket.Trim().ToLowerInvariant();
        var region = source.Region.Trim().ToLowerInvariant();
        var root = NormalizeCosRoot(source.Root);
        if (!BucketPattern().IsMatch(bucket)) throw new ArgumentException("COS Bucket 格式无效，只能使用 3—63 位小写字母、数字和连字符");
        if (!RegionPattern().IsMatch(region)) throw new ArgumentException("COS Region 格式无效");
        return (bucket, region, root);
    }

    public static (string Channel, string ManifestUrl) ValidateUpdateSettings(AppConfig source)
    {
        var channel = NormalizeUpdateChannel(source.UpdateChannel);
        var manifestUrl = source.UpdateManifestUrl.Trim();
        if (manifestUrl.Length > 0 &&
            (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var updateUri) || updateUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(updateUri.UserInfo)))
            throw new ArgumentException("自定义更新清单必须是无账号信息的 HTTPS 绝对地址");
        return (channel, manifestUrl);
    }

    public static string NormalizeApiPath(string? value, string label)
    {
        var path = (value ?? "").Trim().TrimEnd('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0 || !path.StartsWith('/') || path.StartsWith("//") || Uri.TryCreate(path, UriKind.Absolute, out _) ||
            path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || path.Contains('\\') || path.Contains('?') || path.Contains('#') ||
            segments.Length == 0 || segments.Any(x => x is "." or ".."))
            throw new ArgumentException(label + " 必须是以单个 / 开头的同服务器相对路径");
        return path;
    }

    public static string NormalizeUpdateChannel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "developer" or "dev" => "developer",
        "beta" => "beta",
        _ => "stable"
    };

    private static string NormalizeGameRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("游戏安装目录必须是完整绝对路径");
        var full = Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("游戏安装目录不存在：" + full);
        if (!GamePathService.TryFindScfaExecutable(full, out _))
            throw new InvalidDataException("游戏目录中未识别到 SCFA 主程序（支持根目录/bin、标准名称或带游戏特征文件的改名 EXE）：" + full);
        return full;
    }

    private static string NormalizeCosRoot(string? value)
    {
        var root = (value ?? "").Trim().Replace('\\', '/').Trim('/');
        if (root.Length == 0 || root.Length > 256) throw new ArgumentException("COS Root 不能为空且不能超过 256 个字符");
        if (root.Any(char.IsControl) || root.Contains('?') || root.Contains('#') || root.Split('/').Any(x => x.Length == 0 || x is "." or ".."))
            throw new ArgumentException("COS Root 包含不安全的对象路径字符");
        return root;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();
}
