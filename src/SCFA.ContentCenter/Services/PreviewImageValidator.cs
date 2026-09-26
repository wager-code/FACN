using System.Windows.Media.Imaging;

namespace SCFA.ContentCenter.Services;

/// <summary>Checks a local image before displaying it as a content preview.</summary>
public static class PreviewImageValidator
{
    public static (int Width, int Height) Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("预览图必须使用完整绝对路径");
        var full = Path.GetFullPath(path);
        var extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("预览图只支持 PNG 或 JPEG");
        var info = new FileInfo(full);
        if (!info.Exists || info.Length is <= 0 or > 5 * 1024 * 1024 ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("预览图不存在、超过 5MB 或指向其他文件");
        try
        {
            using var stream = File.OpenRead(full);
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            var isPng = signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var isJpeg = signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF;
            if ((extension == ".png" && !isPng) || (extension is ".jpg" or ".jpeg" && !isJpeg))
                throw new InvalidDataException("预览图扩展名与实际文件格式不一致");
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("预览图没有可用画面");
            if (frame.PixelWidth is < 320 or > 4096 || frame.PixelHeight is < 180 or > 4096)
                throw new InvalidDataException("预览图分辨率不在 320×180 至 4096×4096 范围内");
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException("预览图无法解码：" + ex.Message, ex); }
    }
}
