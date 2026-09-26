using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class LocalPageViewModel : ViewModelBase
{
    private string _status = "等待扫描";
    private string _searchText = "";
    private LocalContentEntry? _selected;
    private BitmapSource? _selectedPreview;
    public string Kind { get; }
    public string Title => Kind == "地图" ? "本地地图" : "本地 MOD";
    public string Subtitle => Kind == "地图" ? "管理本机地图；可在列表中直接删除，删除前自动备份。" : "管理本机 MOD；可在列表中直接删除，删除前自动备份。";
    public string LibraryLabel => Kind == "地图" ? "LOCAL MAPS" : "LOCAL MODS";
    public Visibility MapPreviewVisibility => Kind == "地图" ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<LocalContentEntry> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ItemsView.Refresh(); UpdateSummaries(); } } }
    public int TotalCount => Items.Count;
    public int VisibleCount => ItemsView.Cast<object>().Count();
    public int ValidCount => Items.Count(x => x.Valid);
    public int IssueCount => Items.Count(x => !x.Valid);
    public string SelectedState => SelectedItem is null ? "尚未选择内容" : SelectedItem.Valid ? "结构校验通过" : "需要检查";
    public BitmapSource? SelectedPreview { get => _selectedPreview; private set { if (Set(ref _selectedPreview, value)) OnPropertyChanged(nameof(PreviewStateText)); } }
    public string PreviewStateText => SelectedPreview is not null ? "游戏地图预览" : SelectedItem is null ? "选择地图查看预览" : "该地图暂无可用预览图";
    public LocalContentEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedState));
            SelectedPreview = value?.Preview;
            OnPropertyChanged(nameof(PreviewStateText));
            OpenFolderCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncItemCommand<LocalContentEntry> DeleteItemCommand { get; }

    public LocalPageViewModel(string kind)
    {
        Kind = kind;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
        DeleteItemCommand = new AsyncItemCommand<LocalContentEntry>(DeleteAsync, item => Directory.Exists(item.Root));
        _ = RefreshAsync();
    }

    private bool FilterItem(object value)
    {
        if (value is not LocalContentEntry item || string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OpenFolder()
    {
        if (SelectedItem is null || !Directory.Exists(SelectedItem.Root)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedItem.Root}\"") { UseShellExecute = true });
    }

    private async Task UninstallAsync()
    {
        var item = SelectedItem;
        if (item is not null) await DeleteAsync(item);
    }

    private async Task DeleteAsync(LocalContentEntry item)
    {
        if (!Directory.Exists(item.Root)) return;
        var answer = MessageBox.Show(
            $"确定从本机删除{item.Kind}“{item.Name}”吗？\n\n目录：{item.Root}\n\n删除前会自动备份，可在“历史备份”页面恢复。云端已有的内容以后运行一键同步时可能再次安装。",
            "确认删除本地内容",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Status = $"正在备份并删除 {item.Name}…";
            await App.Services.Install.UninstallAsync(item);
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
            await RefreshAsync();
            Status = "本地内容已删除；安全备份已保留。";
        }
        catch (OperationCanceledException)
        {
            Status = "删除已取消，原内容保持不变。";
        }
        catch (Exception ex)
        {
            Status = "删除失败：" + ex.Message;
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在扫描真实游戏目录…";
            var items = await App.Services.Local.ScanAsync(Kind);
            if (Kind == "地图") await Task.Run(() =>
            {
                foreach (var item in items) item.Preview = MapPreviewService.TryLoad(item.Root);
            });
            SelectedItem = null; Items.Clear(); foreach (var x in items) Items.Add(x);
            ItemsView.Refresh();
            UpdateSummaries();
            Status = $"扫描完成 · 有效 {Items.Count(x => x.Valid)} · 异常 {Items.Count(x => !x.Valid)}";
        }
        catch (Exception ex) { Status = "扫描失败：" + ex.Message; }
    }

    private void UpdateSummaries()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(IssueCount));
    }
}
