using System.Diagnostics;
using System.Windows;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class UpdatesPageViewModel : ViewModelBase
{
    private string _selectedChannel;
    private string _status = "等待检查更新";
    private AppUpdateInfo? _info;
    private string _stagedPath = "";

    public UpdatesPageViewModel()
    {
        _selectedChannel = NormalizeChannel(App.Services.Config.Current.UpdateChannel);
        CheckCommand = new AsyncRelayCommand(CheckAsync);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => Info is { Available: true, MetadataComplete: true });
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => Info is not null && File.Exists(StagedPath));
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(App.Services.Updates.StagingRoot));
        if (App.Services.Config.Current.AutoCheckUpdates) _ = CheckAsync();
    }

    public string[] Channels { get; } = ["stable", "beta", "developer"];
    public string SelectedChannel { get => _selectedChannel; set { if (Set(ref _selectedChannel, NormalizeChannel(value))) OnPropertyChanged(nameof(ChannelDisplay)); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public AppUpdateInfo? Info { get => _info; private set { if (Set(ref _info, value)) { DownloadCommand.RaiseCanExecuteChanged(); ApplyCommand.RaiseCanExecuteChanged(); NotifyUpdateState(); } } }
    public string StagedPath { get => _stagedPath; private set { if (Set(ref _stagedPath, value)) { ApplyCommand.RaiseCanExecuteChanged(); NotifyUpdateState(); } } }
    public string CurrentVersion => AppVersion.Informational;
    public string ChannelDisplay => SelectedChannel switch { "developer" => "Developer · 开发预览", "beta" => "Beta · 测试通道", _ => "Stable · 稳定通道" };
    public string LatestVersionDisplay => Info?.LatestVersion ?? "尚未检查";
    public string UpdateStateLabel => Info is null ? "等待检查" : Info.Available ? "发现可用更新" : "当前已是最新版本";
    public string SecurityStateLabel => Info is null ? "等待发布元数据" : Info.MetadataComplete ? "大小、PE 与 SHA-256 校验就绪" : "发布元数据不完整";
    public string StageStateLabel => File.Exists(StagedPath) ? "安装包已下载并验证" : "尚未暂存安装包";
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    private async Task CheckAsync()
    {
        try
        {
            App.Services.Config.Current.UpdateChannel = SelectedChannel;
            await App.Services.Config.SaveAsync();
            Status = $"正在检查 {SelectedChannel} 通道…";
            Info = await App.Services.Updates.CheckAsync();
            StagedPath = "";
            Status = Info.Status;
        }
        catch (Exception ex)
        {
            Info = null;
            Status = "检查失败：" + ex.Message;
            App.Services.Log.Error("检查客户端更新失败", ex);
        }
    }

    private async Task DownloadAsync()
    {
        if (Info is null) return;
        try
        {
            Status = $"正在下载客户端 {Info.LatestVersion}…";
            StagedPath = await App.Services.Updates.DownloadAsync(Info);
            Status = "更新已下载并通过 SHA-256 校验，可以安装。";
        }
        catch (OperationCanceledException) { Status = "更新下载已取消。"; }
        catch (Exception ex)
        {
            Status = "下载失败：" + ex.Message;
            MessageBox.Show(ex.Message, "更新下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplyAsync()
    {
        if (Info is null || !File.Exists(StagedPath)) return;
        var answer = MessageBox.Show(
            $"将退出当前客户端并安装 {Info.LatestVersion}。\n\n旧程序会在新程序成功启动后清理，是否继续？",
            "安装客户端更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await App.Services.Updates.BeginApplyAsync(StagedPath, Info);
            Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动更新失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static void OpenFolder()
    {
        Directory.CreateDirectory(App.Services.Updates.StagingRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Services.Updates.StagingRoot}\"") { UseShellExecute = true });
    }
    private void NotifyUpdateState()
    {
        OnPropertyChanged(nameof(LatestVersionDisplay));
        OnPropertyChanged(nameof(UpdateStateLabel));
        OnPropertyChanged(nameof(SecurityStateLabel));
        OnPropertyChanged(nameof(StageStateLabel));
    }
    private static string NormalizeChannel(string value) => value?.Trim().ToLowerInvariant() switch { "developer" or "dev" => "developer", "beta" => "beta", _ => "stable" };
}
