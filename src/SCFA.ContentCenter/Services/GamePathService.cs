using Microsoft.Win32;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class GamePathService(ConfigService config)
{
    private static readonly string[] StandardExecutableNames =
    [
        "ForgedAlliance.exe",
        "SupremeCommander.exe",
        "SupremeCommanderForgedAlliance.exe"
    ];

    private static readonly string[] InstallationMarkerNames =
    [
        "GDFBinary.dll",
        "MohoEngine.dll",
        "SupComDataPath.lua",
        "gpgcore.dll",
        "LuaPlus_1081.dll"
    ];

    public string AutoDetectGameRoot() => DiscoverGameRoots().FirstOrDefault() ?? "";

    public IReadOnlyList<string> DiscoverGameRoots()
    {
        var c = config.Current;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, c.GameRoot);
        foreach (var steamRoot in DiscoverSteamRoots())
        {
            AddCandidate(candidates, Path.Combine(steamRoot, "steamapps", "common", "Supreme Commander Forged Alliance"));
            var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            try
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    var library = match.Groups["path"].Value.Replace("\\\\", "\\");
                    AddCandidate(candidates, Path.Combine(library, "steamapps", "common", "Supreme Commander Forged Alliance"));
                }
            }
            catch { }
        }

        for (var d = 'C'; d <= 'Z'; d++)
        {
            var drive = $"{d}:\\";
            AddCandidate(candidates, Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Supreme Commander Forged Alliance"));
            AddCandidate(candidates, Path.Combine(drive, "Steam", "steamapps", "common", "Supreme Commander Forged Alliance"));
            AddCandidate(candidates, Path.Combine(drive, "Games", "Supreme Commander Forged Alliance"));
            AddCandidate(candidates, Path.Combine(drive, "SCFA"));
        }
        var configured = NormalizePathOrEmpty(c.GameRoot);
        return candidates
            .Where(IsScfaGameRoot)
            .OrderBy(x => string.Equals(x, configured, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string GetContentDirectory(string kind, bool create = false)
    {
        var c = config.Current;
        var manual = kind == "地图" ? c.MapsDir : c.ModsDir;
        var selected = string.IsNullOrWhiteSpace(manual) ? GetDefaultPlayerContentDirectory(kind) : manual;
        if (string.IsNullOrWhiteSpace(selected))
            throw new InvalidOperationException($"尚未选择真实存在的{kind}目录；软件不会自动在 C 盘或游戏目录中创建内容根目录");
        var normalized = NormalizeContentDirectory(selected, kind, create: false);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"{kind}目录不存在，请在首次设置或软件设置中选择已有目录：{normalized}");
        return normalized;
    }

    public string GetDefaultPlayerContentDirectory(string kind)
    {
        var name = kind == "地图" ? "maps" : kind == "MOD" ? "mods" : throw new ArgumentException("未知内容类型：" + kind, nameof(kind));
        if (string.IsNullOrWhiteSpace(config.Current.GameRoot)) return "";
        var candidate = Path.Combine(config.Current.GameRoot, name);
        return Directory.Exists(candidate) ? Path.GetFullPath(candidate) : "";
    }

    public string NormalizeContentDirectory(string path, string kind, bool create = false)
    {
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind, nameof(kind));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{kind}目录不能为空", nameof(path));
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"{kind}目录必须是完整绝对路径", nameof(path));
        var full = Path.GetFullPath(expanded)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{kind}目录不能使用磁盘根目录", nameof(path));
        if (File.Exists(full)) throw new IOException($"{kind}目录已被同名文件占用：{full}");
        // 内容根目录只能由用户或游戏安装程序建立。安装单个地图/MOD时只会在这个已存在
        // 的根目录内部创建内容文件夹，绝不在 C 盘“我的文档”或猜测位置建立根目录。
        _ = create;
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{kind}目录不能是符号链接/重解析点：{full}");
        return full;
    }

    public static void ValidateContentDirectoryPair(string mapsDirectory, string modsDirectory)
    {
        var maps = Path.GetFullPath(mapsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mods = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(maps, mods, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("地图目录与 MOD 目录不能相同");
        if (IsUnder(maps, mods) || IsUnder(mods, maps))
            throw new ArgumentException("地图目录与 MOD 目录不能互相嵌套");
    }

    private static IEnumerable<string> DiscoverSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString(),
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null)?.ToString(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        })
        {
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
        }
        return roots;
    }

    private static void AddCandidate(ISet<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { candidates.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))); }
        catch { }
    }

    private static string NormalizePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return ""; }
    }

    private static bool IsUnder(string candidate, string parent) =>
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool IsScfaGameRoot(string root) => TryFindScfaExecutable(root, out _);

    public static bool TryFindScfaExecutable(string root, out string executablePath)
    {
        executablePath = "";
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;

        foreach (var directory in new[] { root, Path.Combine(root, "bin") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;

            foreach (var name in StandardExecutableNames)
            {
                var standard = Path.Combine(directory, name);
                if (!File.Exists(standard)) continue;
                executablePath = standard;
                return true;
            }

            // 某些零售版、社区整合版或迁移安装会重命名主程序。此时不能只凭任意 EXE
            // 判断，而是要求同目录存在 SCFA 数据文件和至少两项特征文件，再验证 PE 头。
            if (!File.Exists(Path.Combine(directory, "game.dat")) ||
                InstallationMarkerNames.Count(name => File.Exists(Path.Combine(directory, name))) < 2)
                continue;

            try
            {
                var commonRenamedExecutable = Path.Combine(directory, "game.exe");
                if (File.Exists(commonRenamedExecutable) && HasPortableExecutableHeader(commonRenamedExecutable))
                {
                    executablePath = commonRenamedExecutable;
                    return true;
                }

                foreach (var candidate in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                             .Select(path => new FileInfo(path))
                             .Where(file => file.Length >= 1024 * 1024)
                             .OrderByDescending(file => file.Length)
                             .Take(64))
                {
                    if (!HasPortableExecutableHeader(candidate.FullName)) continue;
                    executablePath = candidate.FullName;
                    return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool HasPortableExecutableHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 68) return false;
            Span<byte> dosHeader = stackalloc byte[64];
            if (stream.Read(dosHeader) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z') return false;
            var peOffset = BitConverter.ToInt32(dosHeader[60..64]);
            if (peOffset < 64 || peOffset > stream.Length - 4) return false;
            stream.Position = peOffset;
            Span<byte> signature = stackalloc byte[4];
            return stream.Read(signature) == signature.Length &&
                   signature[0] == (byte)'P' && signature[1] == (byte)'E' && signature[2] == 0 && signature[3] == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
