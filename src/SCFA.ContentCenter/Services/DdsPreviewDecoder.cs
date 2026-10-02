using System.Buffers.Binary;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SCFA.ContentCenter.Services;

/// <summary>Bounded first-surface decoder for legacy MOD icon DDS files; no graphics driver required.</summary>
internal static class DdsPreviewDecoder
{
    internal static BitmapSource? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 128 or > 5 * 1024 * 1024 || U32(data, 0) != 0x20534444 ||
            U32(data, 4) != 124 || U32(data, 76) != 32) return null;
        var width = (int)U32(data, 16); var height = (int)U32(data, 12);
        if (width is < 1 or > 1024 || height is < 1 or > 1024 ||
            U32(data, 24) > 1 || U32(data, 112) != 0) return null;
        var flags = U32(data, 80);
        byte[] pixels;
        if ((flags & 4) != 0)
        {
            var format = U32(data, 84);
            var blockSize = format == 0x31545844 ? 8 : format is 0x33545844 or 0x35545844 ? 16 : 0;
            if (blockSize == 0) return null;
            var columns = (width + 3) / 4; var rows = (height + 3) / 4;
            if (data.Length - 128 < columns * rows * blockSize) return null;
            pixels = new byte[width * height * 4];
            Span<byte> colors = stackalloc byte[16]; Span<byte> alphas = stackalloc byte[8];
            for (var by = 0; by < rows; by++)
            for (var bx = 0; bx < columns; bx++)
            {
                var block = data.Slice(128 + (by * columns + bx) * blockSize, blockSize);
                var color = block[(blockSize == 8 ? 0 : 8)..];
                var c0 = BinaryPrimitives.ReadUInt16LittleEndian(color);
                var c1 = BinaryPrimitives.ReadUInt16LittleEndian(color[2..]);
                Color565(c0, colors); Color565(c1, colors[4..]);
                var transparent = blockSize == 8 && c0 <= c1;
                for (var c = 0; c < 3; c++)
                {
                    colors[8 + c] = transparent ? (byte)((colors[c] + colors[4 + c]) / 2)
                        : (byte)((2 * colors[c] + colors[4 + c]) / 3);
                    colors[12 + c] = transparent ? (byte)0
                        : (byte)((colors[c] + 2 * colors[4 + c]) / 3);
                }
                colors[11] = 255; colors[15] = transparent ? (byte)0 : (byte)255;
                if (format == 0x35545844)
                {
                    alphas[0] = block[0]; alphas[1] = block[1];
                    if (alphas[0] > alphas[1])
                        for (var a = 2; a < 8; a++) alphas[a] = (byte)(((8 - a) * alphas[0] + (a - 1) * alphas[1]) / 7);
                    else
                    {
                        for (var a = 2; a < 6; a++) alphas[a] = (byte)(((6 - a) * alphas[0] + (a - 1) * alphas[1]) / 5);
                        alphas[6] = 0; alphas[7] = 255;
                    }
                }
                ulong alphaIndices = 0;
                if (format == 0x35545844)
                    for (var a = 0; a < 6; a++) alphaIndices |= (ulong)block[2 + a] << (8 * a);
                var indices = U32(color, 4);
                for (var i = 0; i < 16; i++)
                {
                    var x = bx * 4 + i % 4; var y = by * 4 + i / 4;
                    if (x >= width || y >= height) continue;
                    var choice = (int)((indices >> (2 * i)) & 3);
                    var offset = (y * width + x) * 4;
                    colors.Slice(choice * 4, 4).CopyTo(pixels.AsSpan(offset, 4));
                    if (format == 0x33545844) pixels[offset + 3] = (byte)(((block[i / 2] >> (4 * (i % 2))) & 15) * 17);
                    if (format == 0x35545844) pixels[offset + 3] = alphas[(int)((alphaIndices >> (3 * i)) & 7)];
                }
            }
        }
        else if ((flags & 0x40) != 0)
        {
            var bits = (int)U32(data, 88);
            if (bits is not (16 or 24 or 32)) return null;
            var r = U32(data, 92); var g = U32(data, 96); var b = U32(data, 100);
            var a = (flags & 1) != 0 ? U32(data, 104) : 0;
            if (!ValidMask(r, bits) || !ValidMask(g, bits) || !ValidMask(b, bits) ||
                (a != 0 && !ValidMask(a, bits)) || (r & g) != 0 || (r & b) != 0 ||
                (g & b) != 0 || ((r | g | b) & a) != 0) return null;
            var minimumStride = width * (bits / 8);
            var stride = (U32(data, 8) & 8) != 0 ? (long)U32(data, 20) : minimumStride;
            if (stride < minimumStride || stride * height > data.Length - 128) return null;
            pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = 128 + (int)(y * stride) + x * (bits / 8);
                uint value = 0;
                for (var c = 0; c < bits / 8; c++) value |= (uint)data[offset + c] << (8 * c);
                var dest = (y * width + x) * 4;
                pixels[dest] = Component(value, b); pixels[dest + 1] = Component(value, g);
                pixels[dest + 2] = Component(value, r); pixels[dest + 3] = a == 0 ? (byte)255 : Component(value, a);
            }
        }
        else return null;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static void Color565(ushort value, Span<byte> color)
    {
        var b = value & 31; var g = (value >> 5) & 63; var r = (value >> 11) & 31;
        color[0] = (byte)((b << 3) | (b >> 2)); color[1] = (byte)((g << 2) | (g >> 4));
        color[2] = (byte)((r << 3) | (r >> 2)); color[3] = 255;
    }
    private static bool ValidMask(uint mask, int bits)
    {
        if (mask == 0 || (bits < 32 && (mask >> bits) != 0)) return false;
        var normalized = mask >> BitOperations.TrailingZeroCount(mask);
        return (normalized & (normalized + 1)) == 0;
    }
    private static byte Component(uint value, uint mask)
    {
        var shift = BitOperations.TrailingZeroCount(mask);
        return (byte)(((ulong)((value & mask) >> shift) * 255) / (mask >> shift));
    }
}
