namespace SCFA.ContentCenter.Models;

public sealed class DiagnosticItem
{
    public string Category { get; init; } = "";
    public string Check { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Passed { get; init; }
}

public sealed class DiagnosticReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string AppVersion { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public List<DiagnosticItem> Items { get; init; } = [];
    public int Passed => Items.Count(x => x.Passed);
    public int Problems => Items.Count - Passed;
}
