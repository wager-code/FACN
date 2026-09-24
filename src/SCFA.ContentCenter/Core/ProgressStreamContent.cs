using System.Net;

namespace SCFA.ContentCenter.Core;

public sealed class ProgressStreamContent(Stream source, long length, IProgress<int>? progress = null) : HttpContent
{
    private const int BufferSize = 128 * 1024;
    private readonly Stream _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly long _length = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopyWithProgressAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        CopyWithProgressAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }

    private async Task CopyWithProgressAsync(Stream destination, CancellationToken ct)
    {
        if (_source.CanSeek) _source.Position = 0;
        var buffer = new byte[BufferSize];
        long written = 0;
        progress?.Report(_length == 0 ? 100 : 0);
        while (true)
        {
            var count = await _source.ReadAsync(buffer, ct);
            if (count == 0) break;
            written += count;
            if (written > _length) throw new InvalidDataException("上传流长度超过声明值");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            progress?.Report(_length == 0 ? 100 : (int)Math.Min(100, written * 100d / _length));
        }
        if (written != _length) throw new EndOfStreamException($"上传流长度不完整：期望 {_length}，实际 {written}");
        progress?.Report(100);
    }
}
