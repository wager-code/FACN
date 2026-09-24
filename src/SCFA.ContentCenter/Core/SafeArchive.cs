namespace SCFA.ContentCenter.Core;

public static class SafeArchive
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public static string ValidateRelativePath(string raw)
    {
        var name = raw.Replace('\\', '/');
        if (name.Length == 0) return "";
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException($"压缩包路径首尾包含空白：{raw}");
        if (name.StartsWith('/') || name.StartsWith("//") || Path.IsPathRooted(name) || name.Contains(':')) throw new InvalidDataException($"压缩包包含不安全路径：{raw}");
        if (name.Contains("//", StringComparison.Ordinal)) throw new InvalidDataException($"压缩包路径包含空段：{raw}");
        name = name.TrimEnd('/');
        if (name.Length == 0) return "";
        if (name.Length > 1024) throw new InvalidDataException("压缩包路径长度超过安全上限");
        var parts = name.Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var part in parts)
        {
            if (part is "." or "..") throw new InvalidDataException($"压缩包包含目录穿越路径：{raw}");
            if (part.Length == 0 || part.Length > 240) throw new InvalidDataException($"压缩包路径段长度无效：{raw}");
            if (part.EndsWith(' ') || part.EndsWith('.')) throw new InvalidDataException($"Windows 路径尾部不安全：{raw}");
            if (part.Any(c => char.IsControl(c) || invalid.Contains(c))) throw new InvalidDataException($"压缩包包含 Windows 非法文件名：{raw}");
            var stem = part.Split('.')[0];
            if (DeviceNames.Contains(stem)) throw new InvalidDataException($"压缩包包含 Windows 设备名：{raw}");
        }
        return string.Join(Path.DirectorySeparatorChar, parts);
    }
}
