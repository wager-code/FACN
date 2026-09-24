using System.Security.Cryptography;
using System.Text;

namespace SCFA.ContentCenter.Core;

public static class ContentHash
{
    public const string Algorithm = "scfa-content-sha256-v1";

    public static async Task<string> FileSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task<string> DirectorySha256Async(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        var rootAttr = File.GetAttributes(root);
        if ((rootAttr & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("内容根目录是符号链接/重解析点：" + root);

        var files = EnumerateFilesNoLinks(root, ct)
            .Select(p => new { Path = p, Rel = Path.GetRelativePath(root, p).Replace('\\', '/').ToLowerInvariant() })
            .OrderBy(x => x.Rel, StringComparer.Ordinal)
            .ToArray();

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in files)
        {
            ct.ThrowIfCancellationRequested();
            var fh = await FileSha256Async(item.Path, ct);
            incremental.AppendData(Encoding.UTF8.GetBytes(item.Rel));
            incremental.AppendData([0]);
            incremental.AppendData(Encoding.UTF8.GetBytes(fh));
            incremental.AppendData([0]);
        }
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateFilesNoLinks(string root, CancellationToken ct)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("内容包含符号链接/重解析点：" + entry);
                if ((attr & FileAttributes.Directory) != 0) stack.Push(entry);
                else files.Add(entry);
            }
        }
        return files;
    }
}
