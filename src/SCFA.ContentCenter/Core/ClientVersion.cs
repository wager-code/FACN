using System.Text.RegularExpressions;

namespace SCFA.ContentCenter.Core;

/// <summary>Orders application releases without changing map/MOD version semantics.</summary>
internal static class ClientVersion
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(?<core>[0-9]+(?:\.[0-9]+){1,3})(?:-(?<label>dev|alpha|beta|rc)[.-]?(?<number>[0-9]+))?(?:\+[A-Za-z0-9.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static (int Compare, bool Ordered) Compare(string? current, string? latest)
    {
        if (!TryParse(current, out var left) || !TryParse(latest, out var right)) return (0, false);
        var core = left.Core.CompareTo(right.Core);
        if (core != 0) return (core, true);
        var channel = left.Rank.CompareTo(right.Rank);
        return (channel != 0 ? channel : left.Number.CompareTo(right.Number), true);
    }

    private static bool TryParse(string? raw, out (Version Core, int Rank, int Number) value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 128) return false;
        var match = Pattern.Match(raw.Trim());
        if (!match.Success || !Version.TryParse(match.Groups["core"].Value, out var core)) return false;
        var normalized = new Version(core.Major, core.Minor, Math.Max(0, core.Build), Math.Max(0, core.Revision));
        var label = match.Groups["label"].Value.ToLowerInvariant();
        var rank = label switch { "dev" => 0, "alpha" => 1, "beta" => 2, "rc" => 3, "" => 4, _ => -1 };
        var number = 0;
        if (rank < 0 || (label.Length > 0 && !int.TryParse(match.Groups["number"].Value, out number))) return false;
        value = (normalized, rank, number);
        return true;
    }
}
