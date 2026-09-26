using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class CloudPageViewModel : ViewModelBase
{
    private CancellationTokenSource? _batchCts;
    private string _status = "等待刷新";
    private string _searchText = "";
    private string _statusFilter = "全部状态";
    private string _categoryFilter = "全部分类";
    private string _sortOption = "名称";
    private string _libraryFilter = "全部内容";
    private CloudContentEntry? _selected;

    public string Kind { get; }
    public string Title => Kind == "地图" ? "云端地图" : "云端 MOD";
    public string Subtitle => Kind == "地图" ? "正式地图目录：点破心标记不喜欢，一键同步会跳过。" : "正式 MOD 目录：点破心标记不喜欢，一键同步会跳过。";
    public ObservableCollection<CloudContentEntry> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public IReadOnlyList<string> StatusFilters { get; } = ["全部状态", "未安装", "可更新", "需要修复", "已安装", "本地较新", "匹配冲突", "已跳过自动同步"];
    public ObservableCollection<string> CategoryFilters { get; } = ["全部分类"];
    public IReadOnlyList<string> SortOptions { get; } = ["名称", "最新发布", "文件从大到小", "文件从小到大", "安装状态"];
    public IReadOnlyList<string> LibraryFilters { get; } = ["全部内容", "只看收藏", "最近安装"];
    public string Status { get => _status; set => Set(ref _status, value); }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RefreshFilteredView(); } }
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) RefreshFilteredView(); } }
    public string CategoryFilter { get => _categoryFilter; set { if (Set(ref _categoryFilter, value)) RefreshFilteredView(); } }
    public string SortOption { get => _sortOption; set { if (Set(ref _sortOption, value)) { ApplySort(); UpdateSummaries(); } } }
    public string LibraryFilter { get => _libraryFilter; set { if (Set(ref _libraryFilter, value)) RefreshFilteredView(); } }
    public string FavoriteActionText => SelectedItem?.IsFavorite == true ? "取消收藏" : "收藏所选内容";
    public Visibility AdminActionVisibility => CanUnpublish ? Visibility.Visible : Visibility.Collapsed;
    public bool CanUnpublish => AccessPolicy.CanUnpublish(App.Services.CurrentUser);
    public int TotalCount => Items.Count;
    public int VisibleCount => ItemsView.Cast<object>().Count();
    public int SelectedCount => Items.Count(x => x.IsSelected);
    public int InstalledCount => Items.Count(x => x.InstallStateCode is "current" or "newer" or "unknown");
    public int AttentionCount => Items.Count(x => !x.IsSyncExcluded && x.InstallStateCode is "missing" or "update" or "repair" or "conflict");
    public string CatalogLabel => Kind == "地图" ? "MAP CATALOG" : "MOD CATALOG";
    public CloudContentEntry? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(FavoriteActionText));
            OnPropertyChanged(nameof(SelectedItem));
            InstallCommand.RaiseCanExecuteChanged();
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
            UnpublishCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public AsyncItemCommand<CloudContentEntry> ToggleSyncSkipCommand { get; }
    public AsyncRelayCommand UnpublishCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand CancelBatchCommand { get; }

    public CloudPageViewModel(string kind)
    {
        Kind = kind;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanStart);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SelectedItem is not null && CanStart());
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, () => SelectedCount > 0 && CanStart());
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, () => SelectedItem is not null && CanStart());
        ToggleSyncSkipCommand = new AsyncItemCommand<CloudContentEntry>(ToggleSyncSkipAsync, _ => CanStart());
        UnpublishCommand = new AsyncRelayCommand(UnpublishAsync, () => SelectedItem is not null && CanUnpublish && CanStart());
        SelectVisibleCommand = new RelayCommand(SelectVisible, () => VisibleCount > 0 && CanStart());
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0 && CanStart());
        CancelBatchCommand = new RelayCommand(CancelBatch, () => _batchCts is { IsCancellationRequested: false });
        ApplySort();
        _ = RefreshAsync();
    }

    private bool FilterItem(object value)
    {
        if (value is not CloudContentEntry item || !MatchesStatusFilter(item) || !MatchesCategoryFilter(item) || !MatchesLibraryFilter(item)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.InstallState.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Aliases.Any(x => x.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
               item.Tags.Any(x => x.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private bool MatchesStatusFilter(CloudContentEntry item) => StatusFilter switch
    {
        "未安装" => item.InstallStateCode == "missing",
        "可更新" => item.InstallStateCode == "update",
        "需要修复" => item.InstallStateCode == "repair",
        "已安装" => item.InstallStateCode is "current" or "newer" or "unknown",
        "本地较新" => item.InstallStateCode == "newer",
        "匹配冲突" => item.InstallStateCode == "conflict",
        "已跳过自动同步" => item.IsSyncExcluded,
        _ => true
    };

    private bool MatchesCategoryFilter(CloudContentEntry item) =>
        CategoryFilter == "全部分类" || string.Equals(item.CategoryText, CategoryFilter, StringComparison.CurrentCultureIgnoreCase);

    private bool MatchesLibraryFilter(CloudContentEntry item) => LibraryFilter switch
    {
        "只看收藏" => item.IsFavorite,
        "最近安装" => item.IsRecent,
        _ => true
    };

    private void ApplySort()
    {
        using var defer = ItemsView.DeferRefresh();
        ItemsView.SortDescriptions.Clear();
        switch (SortOption)
        {
            case "最新发布":
                ItemsView.SortDescriptions.Add(new SortDescription(nameof(CloudContentEntry.PublishedAtValue), ListSortDirection.Descending));
                break;
            case "文件从大到小":
                ItemsView.SortDescriptions.Add(new SortDescription(nameof(CloudContentEntry.Size), ListSortDirection.Descending));
                break;
            case "文件从小到大":
                ItemsView.SortDescriptions.Add(new SortDescription(nameof(CloudContentEntry.Size), ListSortDirection.Ascending));
                break;
            case "安装状态":
                ItemsView.SortDescriptions.Add(new SortDescription(nameof(CloudContentEntry.InstallStateCode), ListSortDirection.Ascending));
                break;
        }
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(CloudContentEntry.Name), ListSortDirection.Ascending));
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在读取云端清单并核对本地状态…";
            var remote = await App.Services.Cloud.FetchAsync(Kind);
            var locals = (await App.Services.Local.ScanAsync(Kind)).ToArray();
            foreach (var existing in Items) existing.PropertyChanged -= ItemPropertyChanged;
            Items.Clear();
            foreach (var item in remote)
            {
                var key = FavoriteKey(item);
                item.IsFavorite = App.Services.Config.Current.FavoriteContentKeys.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
                item.IsRecent = App.Services.Config.Current.RecentContentKeys.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
                item.IsSyncExcluded = await App.Services.SyncPreferences.IsExcludedAsync(item);
                UpdateInstallState(item, locals);
                item.PropertyChanged += ItemPropertyChanged;
                Items.Add(item);
            }
            RefreshCategoryFilters();
            ItemsView.Refresh();
            UpdateSummaries();
            Status = $"云端目录已刷新 · 共 {Items.Count} 项 · 跳过自动同步 {Items.Count(x => x.IsSyncExcluded)} · 未安装 {Items.Count(x => x.InstallStateCode == "missing")}";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("云端清单读取失败", ex);
        }
    }

    private void RefreshCategoryFilters()
    {
        var previous = CategoryFilter;
        var categories = Items.Select(x => x.CategoryText).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
        CategoryFilters.Clear();
        CategoryFilters.Add("全部分类");
        foreach (var category in categories) CategoryFilters.Add(category);
        CategoryFilter = CategoryFilters.Contains(previous) ? previous : "全部分类";
    }

    private void UpdateInstallState(CloudContentEntry item, LocalContentEntry[] locals)
    {
        var match = ContentIdentity.FindBestResult(locals, item);
        if (match.Ambiguous)
        {
            item.InstallStateCode = "conflict";
            item.InstallState = "本地匹配冲突";
            return;
        }
        var local = match.Entry;
        item.LocalRoot = local?.Root ?? "";
        try
        {
            var root = App.Services.Paths.GetContentDirectory(Kind);
            item.InstallPath = Path.Combine(root, string.IsNullOrWhiteSpace(item.FolderName) ? item.Id : item.FolderName);
        }
        catch { item.InstallPath = "尚未配置已有内容目录"; }
        item.PreviewSource = FindExplicitPreview(local?.Root) ?? item.ThumbnailUrl;
        if (local is null)
        {
            item.InstallStateCode = "missing";
            item.InstallState = "未安装";
            return;
        }
        if (!local.Valid)
        {
            item.InstallStateCode = "repair";
            item.InstallState = "需要修复 · " + local.Detail;
            return;
        }

        var decision = SyncService.Decide(local, item);
        if (decision.VerifyContentHash)
        {
            item.InstallStateCode = "current";
            item.InstallState = "当前版本 · 同步时校验";
        }
        else if (decision.ShouldInstall)
        {
            item.InstallStateCode = "update";
            item.InstallState = $"可更新 · 本地 {local.Version}";
        }
        else
        {
            var comparison = ContentIdentity.CompareVersions(local.Version, item.EffectiveGameVersion);
            item.InstallStateCode = !comparison.Ordered ? "unknown" : comparison.Compare > 0 ? "newer" : "current";
            item.InstallState = !comparison.Ordered
                ? $"版本不可比 · 本地 {local.Version}"
                : comparison.Compare > 0 ? $"本地较新 · {local.Version}"
                : string.IsNullOrWhiteSpace(item.EffectiveContentHash) ? "同版本 · 云端无内容指纹，自动跳过" : "已是当前版本";
        }
    }

    private static string? FindExplicitPreview(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var acceptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "preview.png", "preview.jpg", "preview.jpeg", "thumbnail.png", "thumbnail.jpg", "thumbnail.jpeg",
            "cover.png", "cover.jpg", "cover.jpeg", "map_preview.png", "map_preview.jpg", "map_preview.jpeg"
        };
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => acceptedNames.Contains(Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .Where(file => file.Length is > 0 and <= 5 * 1024 * 1024 && IsValidExplicitPreview(file.FullName))
                .OrderBy(file => file.Name.StartsWith("preview", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => file.FullName.Length)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsValidExplicitPreview(string path)
    {
        try
        {
            var dimensions = SubmissionService.ValidatePreviewImage(path);
            return dimensions.Width >= 320 && dimensions.Height >= 180;
        }
        catch { return false; }
    }

    private async Task InstallAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        try
        {
            Status = $"正在安装 {item.Name}…";
            var locals = (await App.Services.Local.ScanAsync(Kind)).ToArray();
            var match = ContentIdentity.FindBestResult(locals, item);
            if (match.Ambiguous) throw new InvalidOperationException("存在多个同等匹配的本地目录，请先处理重复内容后再安装。");
            var matched = match.Entry;
            if (matched is { Valid: true })
            {
                var comparison = ContentIdentity.CompareVersions(matched.Version, item.EffectiveGameVersion);
                if ((comparison.Ordered && comparison.Compare > 0) || !comparison.Ordered)
                {
                    var detail = comparison.Ordered
                        ? $"本地游戏版本 {matched.Version} 高于云端包内版本 {item.EffectiveGameVersion}，继续会降级。"
                        : $"无法安全比较本地游戏版本 {matched.Version} 与云端包内版本 {item.EffectiveGameVersion}。";
                    var answer = MessageBox.Show(detail + "\n\n是否仍要手动覆盖？", "确认覆盖本地内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) { Status = "已取消安装"; return; }
                }
                else if (comparison.Compare == 0)
                {
                    var answer = MessageBox.Show(
                        "本地已有相同版本。若文件内容不同，手动重装会先备份，再替换本地目录。\n\n是否继续？",
                        "确认重装同版本内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) { Status = "已取消安装"; return; }
                }
            }
            var changed = await App.Services.Install.InstallAsync(item, matched?.Root, existingVersion: matched?.Version);
            Status = changed ? "安装完成" : "内容已相同，未重复覆盖";
            await RefreshAsync();
        }
        catch (OperationCanceledException) { Status = "安装已取消"; }
        catch (Exception ex)
        {
            Status = "安装失败：" + ex.Message;
            MessageBox.Show(ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InstallSelectedAsync()
    {
        var selected = Items.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            Status = "请先勾选要批量处理的内容。";
            return;
        }
        var answer = MessageBox.Show(
            $"将处理已勾选的 {selected.Length} 项。\n\n只安装缺失、旧版或损坏内容；不会批量降级本地较新版本。是否继续？",
            "确认批量安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        using var cts = new CancellationTokenSource();
        _batchCts = cts;
        RaiseCommandStates();
        var installed = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<string>();
        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var item = selected[index];
                Status = $"批量处理 {index + 1}/{selected.Length}：{item.Name}";
                try
                {
                    if (await InstallIfNeededAsync(item, cts.Token)) installed++;
                    else skipped++;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add(item.Name + "：" + ex.Message);
                    App.Services.Log.Error("批量安装失败: " + item.Name, ex);
                }
                finally { item.IsSelected = false; }
            }
            await RefreshAsync();
            Status = $"批量处理完成 · 安装/修复 {installed} · 跳过 {skipped} · 失败 {failed}";
            if (failures.Count > 0)
                MessageBox.Show(string.Join("\n", failures.Take(8)), "部分内容处理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Status = $"批量处理已取消 · 已安装/修复 {installed} · 已跳过 {skipped} · 失败 {failed}";
        }
        finally
        {
            if (ReferenceEquals(_batchCts, cts)) _batchCts = null;
            RaiseCommandStates();
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var config = App.Services.Config.Current;
        var previous = config.FavoriteContentKeys.ToList();
        var wasFavorite = item.IsFavorite;
        var key = FavoriteKey(item);
        try
        {
            config.FavoriteContentKeys.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
            if (!wasFavorite) config.FavoriteContentKeys.Add(key);
            item.IsFavorite = !wasFavorite;
            await App.Services.Config.SaveAsync();
            OnPropertyChanged(nameof(FavoriteActionText));
            ItemsView.Refresh();
            UpdateSummaries();
            Status = item.IsFavorite ? $"已收藏：{item.Name}" : $"已取消收藏：{item.Name}";
        }
        catch (Exception ex)
        {
            config.FavoriteContentKeys = previous;
            item.IsFavorite = wasFavorite;
            OnPropertyChanged(nameof(FavoriteActionText));
            Status = "保存收藏失败：" + ex.Message;
        }
    }

    private async Task ToggleSyncSkipAsync(CloudContentEntry item)
    {
        var excluded = !item.IsSyncExcluded;
        try
        {
            await App.Services.SyncPreferences.SetExcludedAsync(item, excluded);
            item.IsSyncExcluded = excluded;
            RefreshFilteredView();
            Status = excluded
                ? $"已标记不喜欢：{item.Name}；一键同步将跳过，本地文件保持不变。"
                : $"已恢复自动同步：{item.Name}。";
        }
        catch (Exception ex)
        {
            Status = "保存同步偏好失败：" + ex.Message;
            MessageBox.Show(ex.Message, "保存同步偏好失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task UnpublishAsync()
    {
        var item = SelectedItem;
        if (item is null || !CanUnpublish) return;
        var answer = MessageBox.Show(
            $"确定从正式云端目录下架以下内容吗？\n\n{item.Kind}：{item.Name}\n版本：{item.Version}\nID：{item.Id}\n\n下架不会删除玩家电脑上已经安装的内容，服务器会保留审计记录。",
            "确认下架云端内容",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Status = $"正在下架 {item.Name}…";
            await App.Services.Management.UnpublishContentAsync(item.Kind, item.Id, "管理员在云端内容目录中手动下架");
            Status = $"已下架：{item.Name}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "下架失败：" + ex.Message;
            MessageBox.Show(ex.Message, "下架失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task<bool> InstallIfNeededAsync(CloudContentEntry item, CancellationToken ct)
    {
        if (await App.Services.SyncPreferences.IsExcludedAsync(item, ct)) return false;
        var locals = (await App.Services.Local.ScanAsync(Kind, ct)).ToArray();
        var match = ContentIdentity.FindBestResult(locals, item);
        if (match.Ambiguous) throw new InvalidOperationException("存在多个同等匹配的本地目录");
        var matched = match.Entry;
        var decision = SyncService.Decide(matched, item);
        var shouldInstall = decision.ShouldInstall;
        if (decision.VerifyContentHash)
        {
            var localHash = await ContentHash.DirectorySha256Async(matched!.Root, ct);
            shouldInstall = !localHash.Equals(item.EffectiveContentHash, StringComparison.OrdinalIgnoreCase);
        }
        if (!shouldInstall) return false;
        return await App.Services.Install.InstallAsync(item, matched?.Root, ct, matched?.Version, automatic: true);
    }

    private void SelectVisible()
    {
        var visible = ItemsView.Cast<CloudContentEntry>().ToArray();
        foreach (var item in visible) item.IsSelected = true;
        UpdateSummaries();
        Status = $"已勾选当前筛选结果 {visible.Length} 项。";
    }

    private void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
        UpdateSummaries();
        Status = "已清除全部勾选。";
    }

    private void CancelBatch()
    {
        if (_batchCts is null) return;
        Status = "正在取消批量处理…";
        _batchCts.Cancel();
        CancelBatchCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart() => _batchCts is null;

    private static string FavoriteKey(CloudContentEntry item) => InstallService.ContentKey(item.Kind, item.Id);

    private void RefreshFilteredView()
    {
        ItemsView.Refresh();
        UpdateSummaries();
    }

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudContentEntry.IsSelected) && sender is CloudContentEntry { IsSelected: true } selected)
            SelectedItem = selected;
        if (e.PropertyName is nameof(CloudContentEntry.IsSelected) or nameof(CloudContentEntry.IsFavorite) or nameof(CloudContentEntry.IsRecent) or nameof(CloudContentEntry.IsSyncExcluded) or nameof(CloudContentEntry.InstallStateCode))
            UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AttentionCount));
        InstallSelectedCommand.RaiseCanExecuteChanged();
        SelectVisibleCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        InstallSelectedCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        SelectVisibleCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        CancelBatchCommand.RaiseCanExecuteChanged();
    }
}
