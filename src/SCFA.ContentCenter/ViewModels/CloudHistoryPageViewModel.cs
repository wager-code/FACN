using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class CloudHistoryPageViewModel : ViewModelBase
{
    private string _selectedKind = "地图";
    private CloudContentEntry? _selectedContent;
    private CloudContentEntry? _selectedVersion;
    private string _status = "等待刷新";
    private string _searchText = "";
    private bool _showDownlistedOnly;
    private List<CloudContentEntry> _archive = [];

    public CloudHistoryPageViewModel()
    {
        RefreshContentsCommand = new AsyncRelayCommand(RefreshContentsAsync);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedVersion is not null && HasVerifiedPackage);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => SelectedContent?.HistoryState == "已下架" && SelectedVersion is not null && HasVerifiedPackage);
        ContentsView = CollectionViewSource.GetDefaultView(Contents);
        ContentsView.Filter = FilterContent;
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);
    }

    public string[] Kinds { get; } = ["地图", "MOD"];
    public ObservableCollection<CloudContentEntry> Contents { get; } = [];
    public ICollectionView ContentsView { get; }
    public ObservableCollection<CloudContentEntry> Versions { get; } = [];
    public string SelectedKind { get => _selectedKind; set { if (Set(ref _selectedKind, value)) _ = RefreshContentsAsync(); } }
    public bool ShowDownlistedOnly { get => _showDownlistedOnly; set { if (Set(ref _showDownlistedOnly, value)) { ContentsView.Refresh(); OnPropertyChanged(nameof(FilteredContentCount)); SelectedContent = ContentsView.Cast<CloudContentEntry>().FirstOrDefault(); } } }
    public CloudContentEntry? SelectedContent { get => _selectedContent; set { if (Set(ref _selectedContent, value)) { LoadVersions(); OnPropertyChanged(nameof(SelectedContentLabel)); RestoreCommand.RaiseCanExecuteChanged(); } } }
    public CloudContentEntry? SelectedVersion { get => _selectedVersion; set { if (Set(ref _selectedVersion, value)) { InstallCommand.RaiseCanExecuteChanged(); RestoreCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(SelectedVersionLabel)); } } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ContentsView.Refresh(); OnPropertyChanged(nameof(FilteredContentCount)); ClearSearchCommand.RaiseCanExecuteChanged(); } } }
    public int ContentCount => Contents.Count;
    public int FilteredContentCount => ContentsView.Cast<object>().Count();
    public int VersionCount => Versions.Count;
    public int DownlistedCount => Contents.Count(x => x.HistoryState == "已下架");
    public string SelectedContentLabel => SelectedContent is null ? "尚未选择内容" : $"{SelectedContent.Name} · {SelectedContent.HistoryState}";
    public string SelectedVersionLabel => SelectedVersion is null ? "尚未选择版本" : $"{SelectedVersion.HistoryState} · {SelectedVersion.VersionDisplay}";
    public AsyncRelayCommand RefreshContentsCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    private bool HasVerifiedPackage => SelectedVersion is { Size: > 0 } item && item.Sha256.Length == 64 && item.Sha256.All(Uri.IsHexDigit);

    public void Open(bool downlistedOnly)
    {
        ShowDownlistedOnly = downlistedOnly;
        if (Contents.Count == 0) _ = RefreshContentsAsync();
    }

    private bool FilterContent(object value)
    {
        if (value is not CloudContentEntry item) return false;
        if (ShowDownlistedOnly && item.HistoryState != "已下架") return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void LoadVersions()
    {
        Versions.Clear();
        if (SelectedContent is not null)
            foreach (var item in _archive.Where(x => x.Id.Equals(SelectedContent.Id, StringComparison.OrdinalIgnoreCase)))
                Versions.Add(item);
        OnPropertyChanged(nameof(VersionCount));
        SelectedVersion = Versions.FirstOrDefault();
    }

    private async Task RefreshContentsAsync()
    {
        try
        {
            if (App.Services.OfflineMode || !AccessPolicy.CanPublishContent(App.Services.CurrentUser))
                throw new UnauthorizedAccessException("只有管理员可以查看云端版本档案");
            Status = $"正在读取{SelectedKind}版本档案…";
            var result = await App.Services.CloudHistory.FetchArchiveAsync(SelectedKind, App.Services.CurrentUser);
            _archive = result.Entries;
            Contents.Clear();
            foreach (var item in _archive.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
                Contents.Add(item);
            ContentsView.Refresh();
            OnPropertyChanged(nameof(ContentCount));
            OnPropertyChanged(nameof(FilteredContentCount));
            OnPropertyChanged(nameof(DownlistedCount));
            SelectedContent = ContentsView.Cast<CloudContentEntry>().FirstOrDefault();
            Status = $"档案已刷新 · {Contents.Count} 项，已下架 {DownlistedCount} 项。" + result.Warning;
        }
        catch (Exception ex)
        {
            _archive = [];
            Contents.Clear();
            Versions.Clear();
            SelectedContent = null;
            OnPropertyChanged(nameof(ContentCount));
            OnPropertyChanged(nameof(DownlistedCount));
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("云端版本档案读取失败", ex);
        }
    }

    private async Task RestoreAsync()
    {
        var item = SelectedVersion;
        if (item is null || SelectedContent?.HistoryState != "已下架") return;
        if (MessageBox.Show($"将 {item.Name} · {item.VersionDisplay} 恢复为玩家可见的正式版。\n服务器将重新校验云端 ZIP，不会覆盖已有正式内容。是否继续？",
                "恢复已下架内容", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Status = "正在核验云端文件并恢复清单…";
            await App.Services.PublicationUpload.RestoreAsync(SelectedKind, item, App.Services.CurrentUser);
            await RefreshContentsAsync();
            Status = "恢复成功；玩家刷新云端目录后可安装此版本。";
        }
        catch (Exception ex)
        {
            Status = "恢复失败：" + ex.Message;
            MessageBox.Show(ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InstallAsync()
    {
        var item = SelectedVersion;
        if (item is null) return;
        try
        {
            var locals = (await App.Services.Local.ScanAsync(SelectedKind)).Where(x => x.Valid).ToArray();
            var match = ContentIdentity.FindBestResult(locals, item);
            if (match.Ambiguous) throw new InvalidOperationException("存在多个同等匹配的本地目录，已阻止历史版本覆盖");
            var local = match.Entry;
            if (MessageBox.Show($"将 {item.Name} · {item.VersionDisplay} 安装到本机。\n继续前会自动备份现有内容。",
                    "确认安装版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Status = $"正在安装 {item.Name} {item.Version}…";
            var changed = await App.Services.Install.InstallAsync(item, local?.Root, existingVersion: local?.Version);
            Status = changed ? "安装完成；原版本可在历史回滚页面恢复。" : "内容已相同，未重复覆盖。";
        }
        catch (OperationCanceledException) { Status = "安装已取消。"; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            MessageBox.Show(ex.Message, "历史版本安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
