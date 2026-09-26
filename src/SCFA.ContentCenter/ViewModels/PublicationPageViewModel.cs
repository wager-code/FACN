using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
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

    public PublicationPageViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => Directory.Exists(OutputDirectory));
        _ = RefreshAsync();
    }

    public string[] Kinds { get; } = ["地图", "MOD"];
    public ObservableCollection<LocalContentEntry> LocalItems { get; } = [];
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
        : $"{SelectedLocal.Name} · ID {SelectedLocal.Id} · 游戏版本 {SelectedLocal.Version} · {SelectedLocal.Files} 个文件";
    public bool CanPrepare => !App.Services.OfflineMode && AccessPolicy.CanPublishContent(App.Services.CurrentUser) &&
        !string.IsNullOrWhiteSpace(App.Services.Auth.Token) && SelectedLocal is { Valid: true, IsSharedMap: false };
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PrepareCommand { get; }
    public RelayCommand OpenOutputCommand { get; }

    private async Task RefreshAsync()
    {
        try
        {
            Status = $"正在扫描本地{SelectedKind}…";
            var items = await App.Services.Local.ScanAsync(SelectedKind);
            LocalItems.Clear();
            foreach (var item in items) LocalItems.Add(item);
            SelectedLocal = null;
            Status = $"本地{SelectedKind} {LocalItems.Count} 项，其中 {LocalItems.Count(item => item.Valid && !item.IsSharedMap)} 项可准备发布";
        }
        catch (Exception ex)
        {
            Status = "扫描失败：" + ex.Message;
            App.Services.Log.Error("发布中心扫描失败", ex);
        }
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
