using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class PublicationPageViewModel : ViewModelBase
{
    private readonly string _selectedKind;
    private LocalContentEntry? _selectedLocal;
    private string _name = "";
    private string _releaseVersion = "";
    private string _author = "";
    private string _description = "";
    private string _category = "";
    private string _tagsText = "";
    private string _status = "选择地图或 MOD";
    private string _outputDirectory = "";
    private string _searchText = "";
    private string _scannedDirectory = "";
    private string _publisherStatus = "正在检查云端发布服务…";
    private bool _publisherReady;
    private bool _publishing;
    private CancellationTokenSource? _publishCts;
    private int _scanGeneration;

    public PublicationPageViewModel(string kind)
    {
        if (kind is not ("地图" or "MOD")) throw new ArgumentException("未知内容类型", nameof(kind));
        _selectedKind = kind;
        ItemsView = CollectionViewSource.GetDefaultView(LocalItems);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
        PublishCommand = new AsyncRelayCommand(PublishAsync, () => CanPublish);
        CancelPublishCommand = new RelayCommand(CancelPublish, () => IsPublishing);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => Directory.Exists(OutputDirectory));
        _ = RefreshAsync();
        _ = RefreshCapabilityAsync();
    }

    public string PageTitle => SelectedKind == "地图" ? "发布地图" : "发布 MOD";
    public ObservableCollection<LocalContentEntry> LocalItems { get; } = [];
    public ICollectionView ItemsView { get; }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            ItemsView.Refresh();
            if (SelectedLocal is not null && !MatchesSearch(SelectedLocal, value)) SelectedLocal = null;
            UpdateScanStatus();
        }
    }
    public string SelectedKind => _selectedKind;
    public LocalContentEntry? SelectedLocal
    {
        get => _selectedLocal;
        set
        {
            if (!Set(ref _selectedLocal, value)) return;
            Name = value?.Name ?? "";
            ReleaseVersion = value?.Version ?? "";
            Category = value?.Kind ?? "";
            Author = string.IsNullOrWhiteSpace(App.Services.CurrentUser.DisplayName)
                ? App.Services.CurrentUser.Username : App.Services.CurrentUser.DisplayName;
            Description = "";
            TagsText = "";
            OnPropertyChanged(nameof(SelectedSummary));
            RaisePublishState();
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ReleaseVersion { get => _releaseVersion; set => Set(ref _releaseVersion, value); }
    public string Author { get => _author; set => Set(ref _author, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string TagsText { get => _tagsText; set => Set(ref _tagsText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string PublisherStatus { get => _publisherStatus; private set => Set(ref _publisherStatus, value); }
    public bool PublisherReady
    {
        get => _publisherReady;
        private set { if (Set(ref _publisherReady, value)) RaisePublishState(); }
    }
    public bool IsPublishing
    {
        get => _publishing;
        private set
        {
            if (!Set(ref _publishing, value)) return;
            CancelPublishCommand.RaiseCanExecuteChanged();
            RaisePublishState();
        }
    }
    public string OutputDirectory
    {
        get => _outputDirectory;
        private set { if (Set(ref _outputDirectory, value)) OpenOutputCommand.RaiseCanExecuteChanged(); }
    }
    public string SelectedSummary => SelectedLocal is null ? "请选择一项可独立发布的本地内容"
        : $"{SelectedLocal.Name} · 目录 {SelectedLocal.Folder} · ID {SelectedLocal.Id} · 游戏版本 {SelectedLocal.Version} · {SelectedLocal.Files} 个文件";
    public bool CanPrepare => !IsPublishing && !App.Services.OfflineMode && AccessPolicy.CanPublishContent(App.Services.CurrentUser) &&
        !string.IsNullOrWhiteSpace(App.Services.Auth.Token) && SelectedLocal is { Valid: true, IsSharedMap: false };
    public bool CanPublish => CanPrepare && PublisherReady;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PrepareCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public RelayCommand CancelPublishCommand { get; }
    public RelayCommand OpenOutputCommand { get; }

    private void RaisePublishState()
    {
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanPublish));
        PrepareCommand.RaiseCanExecuteChanged();
        PublishCommand.RaiseCanExecuteChanged();
    }

    public async Task RefreshCapabilityAsync()
    {
        if (App.Services.OfflineMode || !AccessPolicy.CanPublishContent(App.Services.CurrentUser))
        {
            PublisherStatus = "仅管理员登录后可以发布到云端";
            return;
        }
        try
        {
            var capability = await App.Services.PublicationUpload.GetCapabilityAsync();
            PublisherReady = capability.Available;
            PublisherStatus = capability.Available ? "云端发布服务已就绪，可直接打包上传"
                : string.IsNullOrWhiteSpace(capability.Message) ? "云端发布服务尚未就绪" : capability.Message;
        }
        catch (Exception ex)
        {
            PublisherReady = false;
            PublisherStatus = "无法连接云端发布服务：" + ex.Message;
            App.Services.Log.Error("检查管理员发布服务失败", ex);
        }
    }

    private async Task RefreshAsync()
    {
        var generation = ++_scanGeneration;
        var kind = SelectedKind;

        try
        {
            Status = $"正在扫描本地{kind}…";
            var directory = App.Services.Paths.GetContentDirectory(kind);
            var items = await App.Services.Local.ScanAsync(kind);
            if (generation != _scanGeneration) return;
            _scannedDirectory = directory;
            LocalItems.Clear();
            foreach (var item in items) LocalItems.Add(item);
            ItemsView.Refresh();
            SelectedLocal = null;
            UpdateScanStatus();
        }
        catch (Exception ex)
        {
            if (generation != _scanGeneration) return;
            Status = "扫描失败：" + ex.Message;
            App.Services.Log.Error("发布中心扫描失败", ex);
        }
    }

    private bool FilterItem(object value) => value is LocalContentEntry item && MatchesSearch(item, SearchText);

    public static bool MatchesSearch(LocalContentEntry item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var query = search.Trim();
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateScanStatus()
    {
        if (string.IsNullOrEmpty(_scannedDirectory)) return;
        Status = $"目录 {_scannedDirectory} · 显示 {ItemsView.Cast<object>().Count()}/{LocalItems.Count} 项 · " +
            $"可发布 {LocalItems.Count(item => item.Valid && !item.IsSharedMap)} 项";
    }

    private async Task<PublicationBundle> PrepareMaterialsAsync(LocalContentEntry selected, string kind, PublicationMetadata metadata, CancellationToken ct = default)
    {

        Status = "正在读取线上清单并校验版本…";
        var manifest = await App.Services.Cloud.FetchManifestTextAsync(kind, ct);

        Status = "正在生成并核对 ZIP 包…";
        var outputRoot = Path.Combine(ConfigService.ResolveDataDirectory(), "PublicationStaging");
        var result = await App.Services.Publication.PrepareAsync(selected, metadata, manifest,
            outputRoot, App.Services.Config.Current, App.Services.CurrentUser, ct);
        OutputDirectory = result.Directory;
        App.Services.Log.Info($"管理员发布材料已准备：{kind} {selected.Id} {metadata.ReleaseVersion}，ZIP SHA-256={result.PackageSha256}");
        return result;
    }

    private async Task PrepareAsync()
    {
        if (!CanPrepare || SelectedLocal is null) return;
        var selected = SelectedLocal;
        var kind = SelectedKind;
        var metadata = new PublicationMetadata(Name, ReleaseVersion, Author, Description, Category, TagsText);
        try
        {
            var result = await PrepareMaterialsAsync(selected, kind, metadata);
            Status = $"待上传材料已生成：{result.FileCount} 个文件、{result.PackageSize / 1024d / 1024d:F1} MB。尚未发布到云端。";
        }
        catch (Exception ex)
        {
            Status = "准备失败：" + ex.Message;
            App.Services.Log.Error("管理员发布材料准备失败", ex);
            MessageBox.Show(ex.Message, "发布材料未生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PublishAsync()
    {
        if (!CanPublish || SelectedLocal is null) return;
        var selected = SelectedLocal;
        var itemId = selected.Id;
        var kind = SelectedKind;
        var metadata = new PublicationMetadata(Name, ReleaseVersion, Author, Description, Category, TagsText);
        _publishCts = new CancellationTokenSource();
        IsPublishing = true;
        try
        {
            var bundle = await PrepareMaterialsAsync(selected, kind, metadata, _publishCts.Token);
            Status = "正在向 COS 上传发布包并核对清单…";
            await App.Services.PublicationUpload.PublishAsync(bundle, kind, App.Services.CurrentUser, _publishCts.Token);
            Status = $"{kind} {itemId} 已发布到云端并通过清单核对。";
            App.Services.Log.Info($"管理员云端发布成功：{kind} {itemId} ZIP SHA-256={bundle.PackageSha256}");
        }
        catch (OperationCanceledException)
        {
            Status = "发布操作已取消。云端可能留有未提交的暂存文件，正式清单未由客户端提交。";
        }
        catch (Exception ex)
        {
            Status = "发布失败：" + ex.Message;
            App.Services.Log.Error("管理员云端发布失败", ex);
            MessageBox.Show(ex.Message, "云端发布未确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _publishCts.Dispose();
            _publishCts = null;
            IsPublishing = false;
        }
    }

    private void CancelPublish() => _publishCts?.Cancel();

    private void OpenOutput()
    {
        if (!Directory.Exists(OutputDirectory)) return;
        Process.Start(new ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true });
    }
}
