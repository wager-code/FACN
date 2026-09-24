using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class LocalPageViewModel : ViewModelBase
{
    private string _status = "等待扫描";
    private string _searchText = "";
    private LocalContentEntry? _selected;
    public string Kind { get; }
    public string Title => Kind == "地图" ? "本地地图" : "本地 MOD";
    public string Subtitle => Kind == "地图" ? "检查玩家地图目录、结构完整性与占用空间。" : "检查玩家模组目录、版本信息与结构完整性。";
    public string LibraryLabel => Kind == "地图" ? "LOCAL MAPS" : "LOCAL MODS";
    public ObservableCollection<LocalContentEntry> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ItemsView.Refresh(); UpdateSummaries(); } } }
    public int TotalCount => Items.Count;
    public int VisibleCount => ItemsView.Cast<object>().Count();
    public int ValidCount => Items.Count(x => x.Valid);
    public int IssueCount => Items.Count(x => !x.Valid);
    public string SelectedState => SelectedItem is null ? "尚未选择内容" : SelectedItem.Valid ? "结构校验通过" : "需要检查";
    public LocalContentEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedState));
            OpenFolderCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }

    public LocalPageViewModel(string kind)
    {
        Kind = kind;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
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
        if (item is null || !Directory.Exists(item.Root)) return;
        var answer = MessageBox.Show(
            $"确定卸载{item.Kind}“{item.Name}”吗？\n\n删除前会自动创建完整备份，可在“历史备份”页面恢复。",
            "确认卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Status = $"正在备份并卸载 {item.Name}…";
            await App.Services.Install.UninstallAsync(item);
            SelectedItem = null;
            await RefreshAsync();
            Status = "卸载完成；安全备份已保留。";
        }
        catch (OperationCanceledException)
        {
            Status = "卸载已取消，原内容保持不变。";
        }
        catch (Exception ex)
        {
            Status = "卸载失败：" + ex.Message;
            MessageBox.Show(ex.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在扫描真实游戏目录…"; var items = await App.Services.Local.ScanAsync(Kind); Items.Clear(); foreach (var x in items) Items.Add(x);
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
