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
    private string _selectedKind = "地图";
    private LocalContentEntry? _selectedLocal;
    private string _name = "";
    private string _releaseVersion = "";
    private string _author = "";
    private string _description = "";
    private string _category = "";
    private string _tagsText = "";
    private string _status = "选择地图或 MOD，准备待上传材料";
    private string _outputDirectory = "";
    private string _searchText = "";
    private string _scannedDirectory = "";
    private int _scanGeneration;

    public PublicationPageViewModel()
    {
        ItemsView = CollectionViewSource.GetDefaultView(LocalItems);
        ItemsView.Filter = FilterItem;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => Directory.Exists(OutputDirectory));
        _ = RefreshAsync();
    }

    public string[] Kinds { get; } = ["地图", "MOD"];
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
    public string SelectedKind
    {
        get => _selectedKind;
        set { if (Set(ref _selectedKind, value)) _ = RefreshAsync(); }
    }
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
            OnPropertyChanged(nameof(CanPrepare));
            PrepareCommand.RaiseCanExecuteChanged();
        }
    }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ReleaseVersion { get => _releaseVersion; set => Set(ref _releaseVersion, value); }
    public string Author { get => _author; set => Set(ref _author, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string TagsText { get => _tagsText; set => Set(ref _tagsText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string OutputDirectory
    {
        get => _outputDirectory;
        private set { if (Set(ref _outputDirectory, value)) OpenOutputCommand.RaiseCanExecuteChanged(); }
    }
    public string SelectedSummary => SelectedLocal is null ? "请选择一项可独立发布的本地内容"
        : $"{SelectedLocal.Name} · 目录 {SelectedLocal.Folder} · ID {SelectedLocal.Id} · 游戏版本 {SelectedLocal.Version} · {SelectedLocal.Files} 个文件";
    public bool CanPrepare => !App.Services.OfflineMode && AccessPolicy.CanPublishContent(App.Services.CurrentUser) &&
        !string.IsNullOrWhiteSpace(App.Services.Auth.Token) && SelectedLocal is { Valid: true, IsSharedMap: false };
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PrepareCommand { get; }
    public RelayCommand OpenOutputCommand { get; }

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

    private async Task PrepareAsync()
    {
        if (!CanPrepare || SelectedLocal is null) return;
        try
        {
            Status = "正在读取线上清单并校验版本…";
            var manifest = await App.Services.Cloud.FetchManifestTextAsync(SelectedKind);
            var metadata = new PublicationMetadata(Name, ReleaseVersion, Author, Description, Category, TagsText);
            Status = "正在生成并核对 ZIP 包…";
            var outputRoot = Path.Combine(ConfigService.ResolveDataDirectory(), "PublicationStaging");
            var result = await App.Services.Publication.PrepareAsync(SelectedLocal, metadata, manifest,
                outputRoot, App.Services.Config.Current, App.Services.CurrentUser);
            OutputDirectory = result.Directory;
            Status = $"待上传材料已生成：{result.FileCount} 个文件、{result.PackageSize / 1024d / 1024d:F1} MB。尚未发布到云端。";
            App.Services.Log.Info($"管理员发布材料已准备：{SelectedKind} {SelectedLocal.Id} {ReleaseVersion}，ZIP SHA-256={result.PackageSha256}");
        }
        catch (Exception ex)
        {
            Status = "准备失败：" + ex.Message;
            App.Services.Log.Error("管理员发布材料准备失败", ex);
            MessageBox.Show(ex.Message, "发布材料未生成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenOutput()
    {
        if (!Directory.Exists(OutputDirectory)) return;
        Process.Start(new ProcessStartInfo { FileName = OutputDirectory, UseShellExecute = true });
    }
}
