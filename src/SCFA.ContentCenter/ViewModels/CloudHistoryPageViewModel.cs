using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using System.ComponentModel;
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

    public CloudHistoryPageViewModel()
    {
        RefreshContentsCommand = new AsyncRelayCommand(RefreshContentsAsync);
        LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, () => SelectedContent is not null);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedVersion is not null);
        ContentsView = CollectionViewSource.GetDefaultView(Contents);
        ContentsView.Filter = FilterContent;
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);
        _ = RefreshContentsAsync();
    }

    public string[] Kinds { get; } = ["地图", "MOD"];
    public ObservableCollection<CloudContentEntry> Contents { get; } = [];
    public ICollectionView ContentsView { get; }
    public ObservableCollection<CloudContentEntry> Versions { get; } = [];
    public string SelectedKind { get => _selectedKind; set { if (Set(ref _selectedKind, value)) _ = RefreshContentsAsync(); } }
    public CloudContentEntry? SelectedContent { get => _selectedContent; set { if (Set(ref _selectedContent, value)) { LoadHistoryCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(SelectedContentLabel)); } } }
    public CloudContentEntry? SelectedVersion { get => _selectedVersion; set { if (Set(ref _selectedVersion, value)) { InstallCommand.RaiseCanExecuteChanged(); OnPropertyChanged(nameof(SelectedVersionLabel)); } } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ContentsView.Refresh(); OnPropertyChanged(nameof(FilteredContentCount)); ClearSearchCommand.RaiseCanExecuteChanged(); } } }
    public int ContentCount => Contents.Count;
    public int FilteredContentCount => ContentsView.Cast<object>().Count();
    public int VersionCount => Versions.Count;
    public string SelectedContentLabel => SelectedContent is null ? "尚未选择内容" : $"{SelectedContent.Name} · 当前 {SelectedContent.Version}";
    public string SelectedVersionLabel => SelectedVersion is null ? "尚未选择版本" : $"{SelectedVersion.HistoryState} · {SelectedVersion.Version}";
    public AsyncRelayCommand RefreshContentsCommand { get; }
    public AsyncRelayCommand LoadHistoryCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public RelayCommand ClearSearchCommand { get; }

    private bool FilterContent(object value)
    {
        if (value is not CloudContentEntry item || string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Aliases.Any(x => x.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private async Task RefreshContentsAsync()
    {
        try
        {
            Status = $"正在读取{SelectedKind}正式清单…";
            var items = await App.Services.Cloud.FetchAsync(SelectedKind);
            Contents.Clear();
            Versions.Clear();
            foreach (var item in items) Contents.Add(item);
            ContentsView.Refresh();
            OnPropertyChanged(nameof(ContentCount));
            OnPropertyChanged(nameof(FilteredContentCount));
            OnPropertyChanged(nameof(VersionCount));
            SelectedContent = Contents.FirstOrDefault();
            Status = $"内容列表已刷新 · {Contents.Count} 项；请选择内容查询历史版本。";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("云端历史入口清单读取失败", ex);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var content = SelectedContent;
        if (content is null) return;
        try
        {
            Status = $"正在读取 {content.Name} 的真实历史版本…";
            var result = await App.Services.CloudHistory.FetchAsync(SelectedKind, content.Id);
            Versions.Clear();
            foreach (var version in result.Entries) Versions.Add(version);
            OnPropertyChanged(nameof(VersionCount));
            SelectedVersion = Versions.FirstOrDefault();
            Status = string.IsNullOrWhiteSpace(result.Warning) ? $"历史版本已读取 · {Versions.Count} 项" : result.Warning;
        }
        catch (Exception ex)
        {
            Status = "历史读取失败：" + ex.Message;
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
            var detail = item.HistoryState == "历史版本" ? "你选择的是云端历史版本，安装可能降级当前内容。" : "将安装当前正式版本。";
            if (MessageBox.Show($"{detail}\n\n{item.Name} · {item.Version}\n继续前会自动备份当前本地内容。", "确认安装版本", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Status = $"正在安装 {item.Name} {item.Version}…";
            await App.Services.Install.InstallAsync(item, local?.Root, existingVersion: local?.Version);
            Status = "安装完成；原版本可在历史回滚页面恢复。";
        }
        catch (OperationCanceledException) { Status = "安装已取消。"; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            MessageBox.Show(ex.Message, "历史版本安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
