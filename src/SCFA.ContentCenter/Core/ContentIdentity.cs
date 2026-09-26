using System.Text.RegularExpressions;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Core;

public readonly record struct ContentMatchResult(LocalContentEntry? Entry, bool Ambiguous, int Score);

public static class ContentIdentity
{
    private const int MinimumAutomaticMatchScore = 76;
    private static readonly Regex NumericVersion = new(@"^[0-9]+(?:\.[0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex DateVersion = new(@"^(?:19|20)[0-9]{2}[._-][0-9]{1,2}(?:[._-][0-9]{1,2})?$", RegexOptions.Compiled);

    public static string NormalizeKey(string? value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        foreach (var c in new[] { "_", "-", ".", " ", "[", "]", "(", ")" }) s = s.Replace(c, "");
        return s;
    }

    public static string CanonicalVersion(string? value)
    {
        var s = StripVersionPrefix(value);
        if (!NumericVersion.IsMatch(s) && !DateVersion.IsMatch(s)) return NormalizeKey(s);
        var nums = Regex.Matches(s, @"[0-9]+").Select(m => m.Value.TrimStart('0')).Select(v => v.Length == 0 ? "0" : v).ToList();
        if (nums.Count == 0) return NormalizeKey(s);
        while (nums.Count > 1 && nums[^1] == "0") nums.RemoveAt(nums.Count - 1);
        return string.Join('.', nums);
    }

    public static bool VersionsEquivalent(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var ca = CanonicalVersion(a); var cb = CanonicalVersion(b);
        return ca.Length > 0 && ca == cb;
    }

    public static (int Compare, bool Ordered) CompareVersions(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return (0, false);
        if (VersionsEquivalent(a, b)) return (0, true);
        if (DateVersion.IsMatch(StripVersionPrefix(a)) != DateVersion.IsMatch(StripVersionPrefix(b))) return (0, false);
        var ca = CanonicalVersion(a); var cb = CanonicalVersion(b);
        if (!NumericVersion.IsMatch(ca) || !NumericVersion.IsMatch(cb)) return (0, false);
        var pa = ca.Split('.'); var pb = cb.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i].TrimStart('0') : "0"; if (va == "") va = "0";
            var vb = i < pb.Length ? pb[i].TrimStart('0') : "0"; if (vb == "") vb = "0";
            if (va.Length != vb.Length) return (va.Length < vb.Length ? -1 : 1, true);
            var c = string.CompareOrdinal(va, vb); if (c != 0) return (c < 0 ? -1 : 1, true);
        }
        return (0, true);
    }

    private static string StripVersionPrefix(string? value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        foreach (var p in new[] { "version", "ver", "v" })
            if (s.StartsWith(p, StringComparison.Ordinal)) return s[p.Length..].Trim();
        return s;
    }

    public static int MatchScore(LocalContentEntry local, CloudContentEntry cloud)
    {
        var best = 0;
        void Set(int x) { if (x > best) best = x; }
        bool Exact(string? x, string? y) => !string.IsNullOrWhiteSpace(x) && string.Equals(x.Trim(), y?.Trim(), StringComparison.OrdinalIgnoreCase);
        bool Norm(string? x, string? y) { var a = NormalizeKey(x); var b = NormalizeKey(y); return a.Length > 0 && a == b; }
        if (Exact(local.Id, cloud.Id)) Set(120);
        if (Exact(local.Folder, cloud.FolderName)) Set(112);
        if (Exact(local.Folder, cloud.Id)) Set(108);
        // 显示名称可重复或被作者改名，只能作为弱提示，不能单独驱动自动覆盖。
        if (Exact(local.Name, cloud.Name)) Set(70);
        foreach (var alias in cloud.Aliases)
        {
            if (Exact(local.Folder, alias)) Set(104);
            else if (Exact(local.Id, alias)) Set(102);
            else if (Exact(local.Name, alias)) Set(68);
            else if (Norm(local.Folder, alias)) Set(78);
            else if (Norm(local.Id, alias)) Set(76);
            else if (Norm(local.Name, alias)) Set(58);
        }
        if (Norm(local.Id, cloud.Id)) Set(86);
        if (Norm(local.Folder, cloud.FolderName)) Set(82);
        if (Norm(local.Folder, cloud.Id)) Set(80);
        if (Norm(local.Name, cloud.Name)) Set(60);
        return best;
    }

    public static ContentMatchResult FindBestResult(LocalContentEntry[] locals, CloudContentEntry cloud)
    {
        LocalContentEntry? best = null; var score = 0; var ambiguous = false;
        foreach (var local in locals)
        {
            var s = MatchScore(local, cloud);
            if (s > score) { score = s; best = local; ambiguous = false; }
            else if (s > 0 && s == score && best is not null && !string.Equals(best.Root, local.Root, StringComparison.OrdinalIgnoreCase)) ambiguous = true;
        }
        if (score < MinimumAutomaticMatchScore) return new ContentMatchResult(null, false, score);
        return new ContentMatchResult(ambiguous ? null : best, ambiguous, score);
    }

    public static LocalContentEntry? FindBest(LocalContentEntry[] locals, CloudContentEntry cloud) => FindBestResult(locals, cloud).Entry;

    public static async Task<LocalContentEntry?> FindVerifiedCopyAsync(
        LocalContentEntry[] locals, CloudContentEntry cloud, int matchScore, CancellationToken ct = default)
    {
        if (matchScore < MinimumAutomaticMatchScore || string.IsNullOrWhiteSpace(cloud.EffectiveContentHash)) return null;
        foreach (var candidate in locals)
        {
            ct.ThrowIfCancellationRequested();
            if (!candidate.Valid || MatchScore(candidate, cloud) != matchScore ||
                !VersionsEquivalent(candidate.Version, cloud.EffectiveGameVersion)) continue;
            var hash = await ContentHash.DirectorySha256Async(candidate.Root, ct);
            if (hash.Equals(cloud.EffectiveContentHash.Trim(), StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }
}
