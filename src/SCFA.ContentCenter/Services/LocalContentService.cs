using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class LocalContentService(GamePathService paths, LogService log)
{
    private const int MaxDiscoveryDepth = 8;
    private const int MaxDiscoveryDirectories = 20000;
    private const int MaxContentFiles = 100000;
    private static readonly Regex VersionSuffix = new(@"(?i)([._\-\s])v(?:ersion)?[._\-\s]*[0-9]+(?:\.[0-9]+){0,2}.*$", RegexOptions.Compiled);
    private static readonly Regex ValidVersion = new(@"^(?:[A-Za-z0-9][A-Za-z0-9._-]*[A-Za-z0-9]|[A-Za-z0-9])$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<LocalContentEntry>> ScanAsync(string kind, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型：" + kind, nameof(kind));
            var dir = paths.GetContentDirectory(kind);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return (IReadOnlyList<LocalContentEntry>)Array.Empty<LocalContentEntry>();
            var roots = DiscoverRoots(dir, kind, ct);
            var list = new List<LocalContentEntry>();
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                try { list.Add(kind == "地图" ? AnalyzeMap(root, ct) : AnalyzeMod(root, ct)); }
                catch (Exception ex)
                {
                    list.Add(new LocalContentEntry { Kind = kind, Root = root, Folder = Path.GetFileName(root), Name = Path.GetFileName(root), Valid = false, Detail = ex.Message });
                    log.Error($"分析{kind}失败: {root}", ex);
                }
            }
            return (IReadOnlyList<LocalContentEntry>)list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }, ct);
    }

    public async Task<LocalContentEntry> AnalyzeDirectoryAsync(string directory, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("内容目录为空", nameof(directory));
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("内容目录不存在：" + full);
            var isMod = File.Exists(Path.Combine(full, "mod_info.lua"));
            var isMap = Directory.EnumerateFiles(full, "*.scmap", SearchOption.TopDirectoryOnly).Any();
            if (isMod == isMap) throw new InvalidDataException(isMod ? "目录同时具有地图和 MOD 特征，无法安全识别" : "未识别到地图 .scmap 或 MOD mod_info.lua");
            var kind = isMap ? "地图" : "MOD";
            var configuredRoot = Path.GetFullPath(paths.GetContentDirectory(kind)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(configuredRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"所选{kind}不在当前配置的玩家内容目录内：{configuredRoot}");
            return isMap ? AnalyzeMap(full, ct) : AnalyzeMod(full, ct);
        }, ct);
    }

    private IEnumerable<string> DiscoverRoots(string input, string kind, CancellationToken ct)
    {
        input = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(input)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(input, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("地图/MOD 内容目录不能直接设置为磁盘根目录：" + input);
        if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("内容目录根路径是符号链接/重解析点：" + input);

        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((input, 0));
        var visitedCount = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            var dir = Path.GetFullPath(current.Path);
            if (!visited.Add(dir)) continue;
            if (++visitedCount > MaxDiscoveryDirectories)
                throw new InvalidDataException($"扫描目录数量超过安全上限 {MaxDiscoveryDirectories}，请检查内容目录配置");
            try
            {
                var attr = File.GetAttributes(dir); if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                if (IsRoot(dir, kind)) { if (!string.Equals(dir, input, StringComparison.OrdinalIgnoreCase)) result.Add(dir); continue; }
                var subDirectories = Directory.EnumerateDirectories(dir).ToArray();
                if (current.Depth >= MaxDiscoveryDepth)
                {
                    if (subDirectories.Length > 0) throw new InvalidDataException($"内容目录层级超过安全上限 {MaxDiscoveryDepth}：{dir}");
                    continue;
                }
                foreach (var sub in subDirectories)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".scfa_install_", StringComparison.OrdinalIgnoreCase) || name.Contains(".scfa_old_", StringComparison.OrdinalIgnoreCase)) continue;
                    var subAttr = File.GetAttributes(sub);
                    if ((subAttr & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push((sub, current.Depth + 1));
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                log.Error("扫描目录失败，已跳过：" + dir, ex);
            }
        }
        if (IsRoot(input, kind)) result.Insert(0, input);
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRoot(string dir, string kind)
    {
        if (kind == "MOD") return File.Exists(Path.Combine(dir, "mod_info.lua"));
        try { return Directory.EnumerateFiles(dir, "*.scmap", SearchOption.TopDirectoryOnly).Any() ||
                     Directory.EnumerateFiles(dir, "*_scenario.lua", SearchOption.TopDirectoryOnly).Any(); }
        catch { return false; }
    }

    private static LocalContentEntry AnalyzeMod(string root, CancellationToken ct)
    {
        var info = Path.Combine(root, "mod_info.lua");
        var text = File.ReadAllText(info); if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("mod_info.lua 为空");
        var name = ParseQuoted(text, "name"); if (name == "") name = Path.GetFileName(root);
        var id = SanitizeId(ParseQuoted(text, "uid")); if (id == "") id = SanitizeId(StripVersion(Path.GetFileName(root)));
        if (id == "") id = "mod_" + ShortHash(root);
        var version = ParseVersion(text, "version");
        if (version == "") throw new InvalidDataException("mod_info.lua 未找到 version");
        if (!IsVersionValid(version)) throw new InvalidDataException("mod_info.lua 的版本格式无效：" + version);
        var files = EnumerateContentFiles(root, ct);
        return new LocalContentEntry { Kind = "MOD", Root = root, Folder = Path.GetFileName(root), Name = name, Id = id, Version = version, Files = files.Count, Bytes = files.Sum(x => x.Length), Valid = true, Detail = "mod_info.lua 校验通过" };
    }

    private static LocalContentEntry AnalyzeMap(string root, CancellationToken ct)
    {
        var files = EnumerateContentFiles(root, ct);
        var scmaps = files.Where(x => string.Equals(x.DirectoryName, root, StringComparison.OrdinalIgnoreCase) && x.Extension.Equals(".scmap", StringComparison.OrdinalIgnoreCase)).ToArray();
        var allScmaps = files.Where(x => x.Extension.Equals(".scmap", StringComparison.OrdinalIgnoreCase)).ToArray();
        var sharedMap = allScmaps.Length == 0;
        if (allScmaps.Length > 1) throw new InvalidDataException($"地图目录必须且只能包含一个 .scmap，实际 {allScmaps.Length} 个");
        if (!sharedMap && scmaps.Length != 1) throw new InvalidDataException(".scmap 必须直接位于地图文件夹根目录");
        var scenarios = files.Where(x => string.Equals(x.DirectoryName, root, StringComparison.OrdinalIgnoreCase) && x.Name.EndsWith("_scenario.lua", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (scenarios.Length != 1) throw new InvalidDataException($"地图目录必须且只能包含一个 *_scenario.lua，实际 {scenarios.Length} 个");
        var text = File.ReadAllText(scenarios[0].FullName);
        var name = ParseScenarioName(text); if (name == "") name = Path.GetFileName(root);
        var version = ParseVersion(text, "map_version");
        if (version == "") version = ParseVersion(text, "version");
        if (version == "") throw new InvalidDataException("scenario.lua 未找到 map_version 或 version");
        if (!IsVersionValid(version)) throw new InvalidDataException("地图版本格式无效：" + version);
        string? referencedScmap = null;
        if (sharedMap)
        {
            referencedScmap = MapPreviewService.FindReferencedScmap(root);
            if (referencedScmap is null || (File.GetAttributes(referencedScmap) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("地图目录缺少 .scmap，且未找到安全的同级共用地形文件");
        }
        else ValidateReference(root, text, "map", ".scmap");
        ValidateReference(root, text, "save", "_save.lua");
        ValidateReference(root, text, "script", "_script.lua");
        var id = SanitizeId(StripVersion(Path.GetFileName(root))); if (id == "") id = "map_" + ShortHash(root);
        return new LocalContentEntry
        {
            Kind = "地图", Root = root, Folder = Path.GetFileName(root), Name = name, Id = id, Version = version,
            Files = files.Count, Bytes = files.Sum(x => x.Length), Valid = !sharedMap, IsSharedMap = sharedMap,
            Detail = sharedMap
                ? $"共用地形地图：引用 {Path.GetFileName(Path.GetDirectoryName(referencedScmap))} 的 .scmap；游戏可读取，不能作为独立地图包发布"
                : "scenario.lua 归属与版本校验通过"
        };
    }

    private static void ValidateReference(string root, string text, string field, string suffix)
    {
        var value = ParseQuoted(text, field).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"scenario.lua 缺少 {field} 路径");
        var parts = value.Trim('/').Split('/');
        if (parts.Length != 3 || !parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"scenario.lua 的 {field} 路径与地图目录不一致：{value}");
        var file = parts[^1];
        if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, file))) throw new InvalidDataException($"scenario.lua 引用文件不存在：{file}");
    }

    private static string ParseQuoted(string text, string field)
    {
        var m = Regex.Match(text, "(?mi)^\\s*" + Regex.Escape(field) + "\\s*=\\s*[\"']([^\"']+)[\"']");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
    private static string ParseScenarioName(string text)
    {
        var table = Regex.Match(text, @"(?mi)^\s*ScenarioInfo\s*=\s*\{");
        if (!table.Success) return ParseQuoted(text, "name");
        var depth = 0;
        var quote = '\0';
        var lineComment = false;
        var blockComment = false;
        for (var i = table.Index + table.Length - 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (lineComment) { if (ch == '\n') lineComment = false; continue; }
            if (blockComment)
            {
                if (ch == ']' && i + 1 < text.Length && text[i + 1] == ']') { blockComment = false; i++; }
                continue;
            }
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < text.Length) { i++; continue; }
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                blockComment = i + 3 < text.Length && text[i + 2] == '[' && text[i + 3] == '[';
                lineComment = !blockComment;
                i += blockComment ? 3 : 1;
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '{') { depth++; continue; }
            if (ch == '}') { if (--depth == 0) break; continue; }
            if (depth != 1 || !text.AsSpan(i).StartsWith("name", StringComparison.OrdinalIgnoreCase) ||
                (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
                continue;
            var end = i + 4;
            if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) continue;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            if (end >= text.Length || text[end] != '=') continue;
            end++;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            if (end >= text.Length || text[end] is not ('\'' or '"')) continue;
            var nameQuote = text[end++];
            var result = new StringBuilder();
            while (end < text.Length)
            {
                if (text[end] == '\\' && end + 1 < text.Length) { result.Append(text[end + 1]); end += 2; continue; }
                if (text[end] == nameQuote) return result.ToString().Trim();
                result.Append(text[end++]);
            }
            return "";
        }
        return "";
    }
    private static string ParseVersion(string text, string field)
    {
        var m = Regex.Match(text, "(?mi)^\\s*" + Regex.Escape(field) + "\\s*=\\s*[\"']?([A-Za-z0-9][A-Za-z0-9._-]*)[\"']?\\s*,?");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
    private static bool IsVersionValid(string v) => v.Length <= 80 && ValidVersion.IsMatch(v);
    private static string StripVersion(string s) => VersionSuffix.Replace(s, "");
    private static string SanitizeId(string s)
    {
        var b = new StringBuilder(); var underscore = false;
        foreach (var c in s.Trim().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || char.IsDigit(c)) { b.Append(c); underscore = false; }
            else if (!underscore && b.Length > 0) { b.Append('_'); underscore = true; }
        }
        return b.ToString().Trim('_');
    }
    private static string ShortHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..8].ToLowerInvariant();
    private static List<FileInfo> EnumerateContentFiles(string root, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("内容根目录是符号链接/重解析点：" + root);
        var files = new List<FileInfo>();
        var stack = new Stack<string>();
        stack.Push(root);
        var directoryCount = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (++directoryCount > MaxDiscoveryDirectories)
                throw new InvalidDataException($"单个内容目录数量超过安全上限 {MaxDiscoveryDirectories}：{root}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("内容包含符号链接/重解析点：" + entry);
                if ((attr & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                    continue;
                }
                files.Add(new FileInfo(entry));
                if (files.Count > MaxContentFiles)
                    throw new InvalidDataException($"单个内容文件数量超过安全上限 {MaxContentFiles}：{root}");
            }
        }
        return files;
    }
}
