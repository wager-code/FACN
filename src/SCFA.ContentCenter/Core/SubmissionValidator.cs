using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Core;

public sealed record ValidatedSubmissionDraft(
    string Name,
    string Version,
    string Author,
    string Description,
    string Category,
    IReadOnlyList<string> Tags,
    string PreviewPath);

public static class SubmissionValidator
{
    public const long MaxPreviewBytes = 5L * 1024 * 1024;

    public static ValidatedSubmissionDraft Validate(SubmissionDraft draft, LocalContentEntry entry)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.Valid) throw new InvalidOperationException("只能投稿通过本地校验的内容");

        var name = RequiredLine(draft.Name, "投稿名称", 120);
        var version = RequiredLine(draft.Version, "投稿版本", 80);
        if (!ContentIdentity.VersionsEquivalent(version, entry.Version))
            throw new InvalidDataException($"投稿版本 {version} 与内容文件中的版本 {entry.Version} 不一致");
        var author = RequiredLine(draft.Author, "作者", 80);
        var description = RequiredMultiline(draft.Description, "内容说明", 10, 2000);
        var category = OptionalLine(draft.Category, "分类", 40);
        var tags = ParseTags(draft.TagsText);
        var preview = ValidatePreviewPath(draft.PreviewPath);
        return new ValidatedSubmissionDraft(name, version, author, description, category, tags, preview);
    }

    public static IReadOnlyList<string> ParseTags(string? value)
    {
        var result = new List<string>();
        foreach (var raw in (value ?? "").Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = OptionalLine(raw, "标签", 24);
            if (tag.Length == 0 || result.Contains(tag, StringComparer.CurrentCultureIgnoreCase)) continue;
            result.Add(tag);
            if (result.Count > 10) throw new InvalidDataException("标签最多填写 10 个");
        }
        return result;
    }

    public static string ValidatePreviewPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(expanded)) throw new InvalidDataException("预览图必须使用完整绝对路径");
        var full = Path.GetFullPath(expanded);
        if (!File.Exists(full)) throw new FileNotFoundException("预览图不存在", full);
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException("预览图只支持 PNG 或 JPEG");
        var bytes = new FileInfo(full).Length;
        if (bytes <= 0 || bytes > MaxPreviewBytes) throw new InvalidDataException("预览图必须大于 0 且不超过 5MB");
        return full;
    }

    private static string RequiredLine(string? value, string label, int maxLength)
    {
        var text = OptionalLine(value, label, maxLength);
        if (text.Length == 0) throw new InvalidDataException(label + "不能为空");
        return text;
    }

    private static string OptionalLine(string? value, string label, int maxLength)
    {
        var text = (value ?? "").Trim();
        if (text.Length > maxLength) throw new InvalidDataException($"{label}不能超过 {maxLength} 个字符");
        if (text.Any(char.IsControl)) throw new InvalidDataException(label + "不能包含控制字符或换行");
        return text;
    }

    private static string RequiredMultiline(string? value, string label, int minLength, int maxLength)
    {
        var text = (value ?? "").Trim();
        if (text.Length < minLength) throw new InvalidDataException($"{label}至少需要 {minLength} 个字符");
        if (text.Length > maxLength) throw new InvalidDataException($"{label}不能超过 {maxLength} 个字符");
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new InvalidDataException(label + "包含不支持的控制字符");
        return text;
    }
}
