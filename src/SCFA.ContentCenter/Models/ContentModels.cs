using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SCFA.ContentCenter.Models;

public sealed class CloudContentEntry : INotifyPropertyChanged
{
    private string _installState = "未检查";
    private string _installStateCode = "unchecked";
    private bool _isSelected;
    private bool _isFavorite;
    private bool _isSyncExcluded;
    private bool _isRecent;
    private string _previewSource = "";

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("game_version")] public string GameVersion { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("content_sha256")] public string ContentSha256 { get; set; } = "";
    [JsonPropertyName("content_hash")] public string LegacyContentHash { get; set; } = "";
    [JsonPropertyName("folder_name")] public string FolderName { get; set; } = "";
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = [];
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("published_at")] public string PublishedAt { get; set; } = "";
    [JsonPropertyName("thumbnail")] public string Thumbnail { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonIgnore] public string Kind { get; set; } = "";
    [JsonIgnore] public string EffectiveContentHash => string.IsNullOrWhiteSpace(ContentSha256) ? LegacyContentHash : ContentSha256;
    [JsonIgnore] public string EffectiveGameVersion => string.IsNullOrWhiteSpace(GameVersion) ? Version : GameVersion;
    [JsonIgnore] public string VersionDisplay => string.IsNullOrWhiteSpace(GameVersion) ||
        string.Equals(GameVersion.Trim(), Version.Trim(), StringComparison.OrdinalIgnoreCase)
        ? Version : $"{Version}（游戏版本 {GameVersion}）";
    [JsonIgnore] public string SizeText => FormatBytes(Size);
    [JsonIgnore] public string InstallState { get => _installState; set => Set(ref _installState, value); }
    [JsonIgnore] public string InstallStateCode { get => _installStateCode; set => Set(ref _installStateCode, value); }
    [JsonIgnore] public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    [JsonIgnore] public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }
    [JsonIgnore] public bool IsSyncExcluded
    {
        get => _isSyncExcluded;
        set
        {
            if (_isSyncExcluded == value) return;
            Set(ref _isSyncExcluded, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncSkipButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncSkipTooltip)));
        }
    }
    [JsonIgnore] public string SyncSkipButtonText => IsSyncExcluded ? "💔 已跳过" : "♡ 不喜欢";
    [JsonIgnore] public string SyncSkipTooltip => IsSyncExcluded ? "一键同步会跳过；点击恢复自动同步" : "点击后，一键同步将跳过此内容；不会删除本机文件";
    [JsonIgnore] public bool IsRecent { get => _isRecent; set => Set(ref _isRecent, value); }
    [JsonIgnore] public string ThumbnailUrl { get; set; } = "";
    [JsonIgnore] public string LocalRoot { get; set; } = "";
    [JsonIgnore] public string InstallPath { get; set; } = "";
    [JsonIgnore] public string PreviewSource { get => _previewSource; set { Set(ref _previewSource, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewStateText))); } }
    [JsonIgnore] public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewSource);
    [JsonIgnore] public string PreviewStateText => HasPreview ? "内容预览" : Kind == "地图" ? "该地图暂无预览图" : "该 MOD 暂无预览图";
    [JsonIgnore] public string HistoryState { get; set; } = "";
    [JsonIgnore] public string AuthorText => string.IsNullOrWhiteSpace(Author) ? "未提供" : Author.Trim();
    [JsonIgnore] public string CategoryText => string.IsNullOrWhiteSpace(Category) ? "未分类" : Category.Trim();
    [JsonIgnore] public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "暂无内容说明" : Description.Trim();
    [JsonIgnore] public string AliasesText => Aliases.Count == 0 ? "无" : string.Join("、", Aliases);
    [JsonIgnore] public string TagsText => Tags.Count == 0 ? "无" : string.Join("、", Tags);
    [JsonIgnore] public DateTimeOffset PublishedAtValue => DateTimeOffset.TryParse(PublishedAt, out var value) ? value : DateTimeOffset.MinValue;

    private static string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:F1} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / 1024d / 1024d:F1} MB";
        return $"{value / 1024d / 1024d / 1024d:F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class MapManifest
{
    [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
    [JsonPropertyName("maps")] public List<CloudContentEntry> Maps { get; set; } = [];
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class ModManifest
{
    [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
    [JsonPropertyName("mods")] public List<CloudContentEntry> Mods { get; set; } = [];
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class LocalContentEntry
{
    public string Kind { get; set; } = "";
    public string Root { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public bool Valid { get; set; }
    public string Detail { get; set; } = "";
    public string CloudState { get; set; } = "未核对";
    public string SizeText => Bytes < 1024 * 1024 ? $"{Bytes / 1024d:F1} KB" : $"{Bytes / 1024d / 1024d:F1} MB";
}

public sealed class SyncSummary
{
    public int Installed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Messages { get; set; } = [];
}
