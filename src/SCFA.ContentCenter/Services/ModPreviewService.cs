using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace SCFA.ContentCenter.Services;

/// <summary>Reads literal mod_info.lua icon references inside the selected MOD, without executing Lua.</summary>
public static class ModPreviewService
{
    private const int MaxIconBytes = 5 * 1024 * 1024;
    private static readonly Regex Icon = new("""(?m)^[\t ]*icon[\t ]*=[\t ]*(['"])(?<path>[^'"\r\n]+)\1[\t ]*[,;]?[\t ]*(?:--[^\r\n]*)?\r?$""",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static BitmapSource? TryLoad(string? modRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot)) return null;
            var root = Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!SafeAncestors(root)) return null;
            var info = Path.Combine(root, "mod_info.lua");
            if (!SafeFile(info, 256 * 1024)) return null;
            using var infoStream = File.OpenRead(info);
            if (infoStream.Length is <= 0 or > 256 * 1024) return null;
            using var reader = new StreamReader(infoStream);
            var text = reader.ReadToEnd();
            text = Regex.Replace(text, @"--\[(=*)\[.*?\]\1\]", "", RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
            var match = Icon.Match(text);
            if (!match.Success) return null;
            var icon = match.Groups["path"].Value.Replace('\\', '/');
            var parts = icon.Split('/');
            if (parts.Any(p => p is "." or ".." || p.Any(char.IsControl) || p.Contains(':'))) return null;
            string relative;
            if (icon.StartsWith("/mods/", StringComparison.OrdinalIgnoreCase))
            {
                // The package prefix is virtual; installed or exported folders can have been renamed.
                var segments = parts.Skip(2).ToArray();
                var folders = root.Split(Path.DirectorySeparatorChar);
                var best = 0;
                for (var n = 1; n < segments.Length && n <= folders.Length; n++)
                    if (folders.TakeLast(n).SequenceEqual(segments.Take(n), StringComparer.OrdinalIgnoreCase)) best = n;
                if (best == 0)
                {
                    var basename = Path.GetFileName(root);
                    var index = Array.FindIndex(segments, 0, Math.Max(0, segments.Length - 1),
                        p => p.Equals(basename, StringComparison.OrdinalIgnoreCase));
                    best = index >= 0 ? index + 1 : 1;
                }
                relative = string.Join(Path.DirectorySeparatorChar, segments.Skip(best));
            }
            else
            {
                if (icon.StartsWith('/') || Path.IsPathFullyQualified(icon)) return null;
                relative = icon;
            }
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !SafeAncestors(Path.GetDirectoryName(path)!) || !SafeFile(path, MaxIconBytes)) return null;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".dds")
            {
                using var input = File.OpenRead(path);
                if (input.Length is <= 0 or > MaxIconBytes) return null;
                var bytes = new byte[(int)input.Length];
                input.ReadExactly(bytes);
                return DdsPreviewDecoder.TryDecode(bytes);
            }
            if (extension is not (".png" or ".jpg" or ".jpeg")) return null;
            using var stream = File.OpenRead(path);
            if (stream.Length is <= 0 or > MaxIconBytes) return null;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth is < 1 or > 4096 || frame.PixelHeight is < 1 or > 4096) return null;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Min(frame.PixelWidth, 512); bitmap.StreamSource = stream;
            bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static bool SafeFile(string path, int maximum) =>
        new FileInfo(path) is { Exists: true } file && file.Length is > 0 && file.Length <= maximum &&
        (file.Attributes & FileAttributes.ReparsePoint) == 0;
    private static bool SafeAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
