using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;

namespace SCFA.ContentCenter.Services;

/// <summary>Reads the game's embedded map preview, including a sibling map referenced by a scenario, without changing files.</summary>
public static class MapPreviewService
{
    private const int PreviewLengthOffset = 30;
    private const int DdsOffset = 34;
    private const int DdsHeaderBytes = 128;
    private const int MaxPreviewBytes = 4 * 1024 * 1024;

    public static BitmapSource? TryLoad(string? mapRoot)
    {
        if (string.IsNullOrWhiteSpace(mapRoot) || !Directory.Exists(mapRoot)) return null;
        try
        {
            if ((File.GetAttributes(mapRoot) & FileAttributes.ReparsePoint) != 0) return null;
            var scmaps = Directory.EnumerateFiles(mapRoot, "*.scmap", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            var scmap = scmaps.Length == 1 ? scmaps[0] : scmaps.Length == 0 ? FindReferencedScmap(mapRoot) : null;
            if (scmap is not null && (File.GetAttributes(scmap) & FileAttributes.ReparsePoint) == 0)
            {
                var embedded = TryReadEmbedded(scmap);
                if (embedded is not null) return embedded;
            }
            return TryLoadNamedImage(mapRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal static string? FindReferencedScmap(string mapRoot)
    {
        var scenarios = Directory.EnumerateFiles(mapRoot, "*_scenario.lua", SearchOption.TopDirectoryOnly).Take(2).ToArray();
        if (scenarios.Length != 1 || (File.GetAttributes(scenarios[0]) & FileAttributes.ReparsePoint) != 0) return null;
        var scenario = File.ReadAllText(scenarios[0]);
        var match = Regex.Match(scenario, "(?mi)^\\s*map\\s*=\\s*['\"]([^'\"]+)['\"]");
        if (!match.Success) return null;
        var parts = match.Groups[1].Value.Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length != 3 || !parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].EndsWith(".scmap", StringComparison.OrdinalIgnoreCase) ||
            parts.Skip(1).Any(part => part is "." or ".." || part.Length == 0 ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        var mapsRoot = Directory.GetParent(mapRoot)?.FullName;
        if (mapsRoot is null) return null;
        var referencedFolder = Path.Combine(mapsRoot, parts[1]);
        if (!Directory.Exists(referencedFolder) || (File.GetAttributes(referencedFolder) & FileAttributes.ReparsePoint) != 0) return null;
        var referencedScmap = Path.Combine(referencedFolder, parts[2]);
        return File.Exists(referencedScmap) ? referencedScmap : null;
    }

    private static BitmapSource? TryReadEmbedded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length < DdsOffset + DdsHeaderBytes + 4) return null;
        using var reader = new BinaryReader(stream);
        var header = reader.ReadBytes(DdsOffset + DdsHeaderBytes);
        if (header.Length != DdsOffset + DdsHeaderBytes ||
            header[0] != (byte)'M' || header[1] != (byte)'a' || header[2] != (byte)'p' || header[3] != 0x1A ||
            BitConverter.ToInt32(header, 4) != 2 ||
            header[DdsOffset] != (byte)'D' || header[DdsOffset + 1] != (byte)'D' ||
            header[DdsOffset + 2] != (byte)'S' || header[DdsOffset + 3] != (byte)' ' ||
            BitConverter.ToInt32(header, DdsOffset + 4) != 124 ||
            BitConverter.ToInt32(header, DdsOffset + 76) != 32) return null;

        var length = BitConverter.ToInt32(header, PreviewLengthOffset);
        var height = BitConverter.ToInt32(header, DdsOffset + 12);
        var width = BitConverter.ToInt32(header, DdsOffset + 16);
        if (width is < 1 or > 1024 || height is < 1 or > 1024 ||
            length < DdsHeaderBytes || length > MaxPreviewBytes ||
            stream.Length < DdsOffset + (long)length + 4 ||
            BitConverter.ToInt32(header, DdsOffset + 88) != 32 ||
            BitConverter.ToUInt32(header, DdsOffset + 92) != 0x00FF0000 ||
            BitConverter.ToUInt32(header, DdsOffset + 96) != 0x0000FF00 ||
            BitConverter.ToUInt32(header, DdsOffset + 100) != 0x000000FF ||
            BitConverter.ToUInt32(header, DdsOffset + 104) != 0xFF000000 ||
            (long)width * height * 4 > length - DdsHeaderBytes) return null;

        var pixels = reader.ReadBytes(width * height * 4);
        if (pixels.Length != width * height * 4) return null;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? TryLoadNamedImage(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(".small.png", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(".large.png", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path).Contains("small", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(8);
        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > 5 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 512;
                image.StreamSource = input;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Try the next explicitly named preview.
            }
        }
        return null;
    }
}
