namespace SCFA.ContentCenter.Models;

public sealed class CloudHistoryResult
{
    public List<CloudContentEntry> Entries { get; init; } = [];
    public string Warning { get; init; } = "";
}
