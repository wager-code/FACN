using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class BackupsPageViewModel : ViewModelBase
{
    private ContentBackupEntry? _selected;
    private string _searchText = "";
    private string _status = "等待读取备份";

    public BackupsPageViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => SelectedItem is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedItem is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedItem is not null && Directory.Exists(SelectedItem.RevisionRoot));
        _ = RefreshAsync();
    }

    public ObservableCollection<ContentBackupEntry> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public ContentBackupEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            RestoreCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedIntegrity));
        }
    }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { ItemsView.Refresh(); UpdateSummaries(); } } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int TotalCount => Items.Count;
    public int VisibleCount => ItemsView.Cast<object>().Count();
    public int MapCount => Items.Count(x => x.Kind == "地图");
    public int ModCount => Items.Count(x => x.Kind == "MOD");
    public string TotalSizeText => FormatBytes(Items.Sum(x => x.Bytes));
    public string SelectedIntegrity => SelectedItem is null ? "等待选择备份" : string.IsNullOrWhiteSpace(SelectedItem.ContentHash) ? "旧格式 · 恢复前重新校验" : "SHA-256 内容指纹可用";
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    private bool FilterItem(object value)
    {
        if (value is not ContentBackupEntry item || string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.ContentId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.SourceVersion.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Reason.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在读取备份历史…";
            var backups = await App.Services.Backups.ListAsync();
            Items.Clear();
            foreach (var item in backups) Items.Add(item);
            ItemsView.Refresh();
            UpdateSummaries();
            Status = $"备份历史已读取 · {Items.Count} 项";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("读取备份历史失败", ex);
        }
    }

    private async Task RestoreAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var answer = MessageBox.Show(
            $"将恢复 {item.Kind}“{item.Name}”的备份版本 {item.DisplayVersion}。\n\n当前目录会先自动创建安全备份，是否继续？",
            "确认恢复历史版本", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Status = $"正在校验并恢复 {item.Name}…";
            await App.Services.Backups.RestoreAsync(item);
            Status = "恢复完成；恢复前的当前内容也已保存为安全备份。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "恢复失败：" + ex.Message;
            App.Services.Log.Error("恢复备份失败: " + item.Name, ex);
            MessageBox.Show(ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var answer = MessageBox.Show(
            $"确定永久删除这份备份？\n\n{item.Kind} · {item.Name} · {item.CreatedAt:yyyy-MM-dd HH:mm:ss}",
            "删除备份", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await App.Services.Backups.DeleteAsync(item);
            Status = "备份已删除。";
            await RefreshAsync();
        }
        catch (Exception ex) { Status = "删除失败：" + ex.Message; }
    }

    private void OpenFolder()
    {
        if (SelectedItem is null || !Directory.Exists(SelectedItem.RevisionRoot)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SelectedItem.RevisionRoot}\"") { UseShellExecute = true });
    }

    private void UpdateSummaries()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(MapCount));
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(TotalSizeText));
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:F1} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / 1024d / 1024d:F1} MB";
        return $"{value / 1024d / 1024d / 1024d:F2} GB";
    }
}
