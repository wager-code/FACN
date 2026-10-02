using System.Diagnostics;
using System.Windows;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class UpdatesPageViewModel : ViewModelBase
{
    private string _selectedChannel;
    private string _status = "等待检查更新";
    private AppUpdateInfo? _info;
    private string _stagedPath = "";
    private bool _busy;

    private readonly ConfigService _config;
    private readonly UpdateService _updates;
    private readonly LogService _log;

    public UpdatesPageViewModel() : this(App.Services.Config, App.Services.Updates, App.Services.Log) { }

    internal UpdatesPageViewModel(ConfigService config, UpdateService updates, LogService log)
    {
        _config = config;
        _updates = updates;
        _log = log;
        _selectedChannel = NormalizeChannel(_config.Current.UpdateChannel);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && Info is { Available: true, MetadataComplete: true });
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && Info is { Available: true, MetadataComplete: true } && File.Exists(StagedPath));
        OpenFolderCommand = new RelayCommand(OpenFolder, () => Directory.Exists(_updates.StagingRoot));
        if (_config.Current.AutoCheckUpdates) _ = CheckAsync();
    }

    public string[] Channels { get; } = ["stable", "beta", "developer"];
    public string SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (!Set(ref _selectedChannel, NormalizeChannel(value))) return;
            Info = null;
            StagedPath = "";
            Status = "通道已切换，请重新检查更新。";
            OnPropertyChanged(nameof(ChannelDisplay));
        }
    }
    public bool IsBusy => _busy;
    public bool IsChannelSelectionEnabled => !IsBusy;
    public string Status { get => _status; set => Set(ref _status, value); }
    public AppUpdateInfo? Info { get => _info; private set { if (Set(ref _info, value)) { DownloadCommand.RaiseCanExecuteChanged(); ApplyCommand.RaiseCanExecuteChanged(); NotifyUpdateState(); } } }
    public string StagedPath { get => _stagedPath; private set { if (Set(ref _stagedPath, value)) { ApplyCommand.RaiseCanExecuteChanged(); NotifyUpdateState(); } } }
    public string CurrentVersion => AppVersion.Informational;
    public string ChannelDisplay => SelectedChannel switch { "developer" => "开发版 · 开发通道", "beta" => "测试版 · 测试通道", _ => "稳定版 · 稳定通道" };
    public string LatestVersionDisplay => Info?.LatestVersion ?? "尚未检查";
    public string UpdateStateLabel => Info?.Status ?? "等待检查";
    public string SecurityStateLabel => Info is null ? "等待发布元数据" : Info.MetadataComplete ? "下载校验信息已就绪" : "发布元数据不完整";
    public string StageStateLabel => File.Exists(StagedPath) ? "安装包已下载并验证" : "尚未暂存安装包";
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    private async Task CheckAsync()
    {
        if (!TryBeginOperation()) return;
        var previous = Info;
        var previousPath = StagedPath;
        Info = null;
        StagedPath = "";
        try
        {
            _config.Current.UpdateChannel = SelectedChannel;
            await _config.SaveAsync();
            Status = $"正在检查{ChannelDisplay}…";
            var next = await _updates.CheckAsync();
            Info = next;
            if (IsSamePackage(previous, next) && File.Exists(previousPath)) StagedPath = previousPath;
            Status = next.Status;
        }
        catch (Exception ex)
        {
            Status = "检查失败：" + ex.Message;
            _log.Error("检查客户端更新失败", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task DownloadAsync()
    {
        if (Info is not { Available: true, MetadataComplete: true } info || !TryBeginOperation()) return;
        StagedPath = "";
        try
        {
            Status = $"正在下载客户端 {info.LatestVersion}…";
            StagedPath = await _updates.DownloadAsync(info);
            Status = "更新已下载并通过完整性校验，可以安装。";
        }
        catch (OperationCanceledException) { Status = "更新下载已取消。"; }
        catch (Exception ex)
        {
            Status = "下载失败：" + ex.Message;
            MessageBox.Show(ex.Message, "更新下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private async Task ApplyAsync()
    {
        if (Info is not { Available: true, MetadataComplete: true } info ||
            !File.Exists(StagedPath) || !TryBeginOperation()) return;
        var stagedPath = StagedPath;
        try
        {
            var answer = MessageBox.Show(
                $"将退出当前客户端并安装 {info.LatestVersion}。\n\n旧程序会在新程序成功启动后清理，是否继续？",
                "安装客户端更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            await _updates.BeginApplyAsync(stagedPath, info);
            Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动更新失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetBusy(false); }
    }

    private bool TryBeginOperation()
    {
        if (IsBusy) return false;
        SetBusy(true);
        return true;
    }

    private void SetBusy(bool value)
    {
        if (!Set(ref _busy, value, nameof(IsBusy))) return;
        OnPropertyChanged(nameof(IsChannelSelectionEnabled));
        CheckCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static bool IsSamePackage(AppUpdateInfo? previous, AppUpdateInfo next) =>
        previous is { Available: true, MetadataComplete: true } && next is { Available: true, MetadataComplete: true } &&
        previous.Size == next.Size &&
        string.Equals(previous.Channel, next.Channel, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(previous.LatestVersion, next.LatestVersion, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(previous.Sha256, next.Sha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(previous.DownloadUrl, next.DownloadUrl, StringComparison.Ordinal);

    private void OpenFolder()
    {
        Directory.CreateDirectory(_updates.StagingRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_updates.StagingRoot}\"") { UseShellExecute = true });
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
