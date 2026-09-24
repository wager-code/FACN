using System.Collections.ObjectModel;
using System.Windows;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class SyncPageViewModel : ViewModelBase
{
    private CancellationTokenSource? _syncCts;
    private string _status = "一键同步只补齐缺失、更新旧版本、修复校验失败项；不会自动降级本地较新版本。";
    private int _progress;
    private string _runScope = "等待选择同步范围";
    private string _lastRun = "尚未运行";
    private DateTimeOffset _startedAt;
    public string Status { get => _status; set => Set(ref _status, value); }
    public int Progress { get => _progress; set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressLabel)); } }
    public string RunScope { get => _runScope; private set => Set(ref _runScope, value); }
    public string LastRun { get => _lastRun; private set => Set(ref _lastRun, value); }
    public string ProgressLabel => $"{Progress}%";
    public int ResultCount => Results.Count;
    public bool IsRunning => _syncCts is not null;
    public ObservableCollection<SyncResultEntry> Results { get; } = [];
    public ObservableCollection<SyncRunRecord> History { get; } = [];
    public int HistoryCount => History.Count;
    public AsyncRelayCommand SyncAllCommand { get; }
    public AsyncRelayCommand SyncMapsCommand { get; }
    public AsyncRelayCommand SyncModsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCurrentCommand { get; }
    public AsyncRelayCommand ClearHistoryCommand { get; }
    public SyncPageViewModel()
    {
        SyncAllCommand = new AsyncRelayCommand(() => RunAsync(["地图", "MOD"]), CanStart);
        SyncMapsCommand = new AsyncRelayCommand(() => RunAsync(["地图"]), CanStart);
        SyncModsCommand = new AsyncRelayCommand(() => RunAsync(["MOD"]), CanStart);
        CancelCommand = new RelayCommand(Cancel, () => _syncCts is { IsCancellationRequested: false });
        ClearCurrentCommand = new RelayCommand(ClearCurrent, () => !IsRunning && Results.Count > 0);
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync, () => !IsRunning && History.Count > 0);
        LoadHistory();
    }

    public Task RunAllAsync() => RunAsync(["地图", "MOD"]);

    private async Task RunAsync(string[] kinds)
    {
        if (_syncCts is not null) return;
        using var cts = new CancellationTokenSource();
        _syncCts = cts;
        RunScope = kinds.Length == 2 ? "地图 + MOD 全量同步" : kinds[0] + "专项同步";
        _startedAt = DateTimeOffset.Now;
        LastRun = "运行中 · " + DateTime.Now.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(IsRunning));
        RaiseCommandStates();
        Results.Clear(); OnPropertyChanged(nameof(ResultCount)); Progress = 5;
        try
        {
            var p = new Progress<string>(s => { Status = s; if (Progress < 90) Progress += 3; });
            var summary = await App.Services.Sync.SyncAllAsync(kinds, p, cts.Token);
            var completedAt = DateTimeOffset.Now;
            foreach (var m in summary.Messages) Results.Add(new SyncResultEntry(completedAt, m));
            OnPropertyChanged(nameof(ResultCount));
            Progress = 100; Status = $"同步完成 · 安装/更新 {summary.Installed} · 跳过 {summary.Skipped} · 失败 {summary.Failed}";
            LastRun = "完成 · " + DateTime.Now.ToString("HH:mm:ss");
            await SaveHistoryAsync("完成", summary.Installed, summary.Skipped, summary.Failed);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Progress = 0;
            Status = "同步已取消；已经完成的安装保持不变。";
            Results.Add(new SyncResultEntry(DateTimeOffset.Now, "用户取消了同步。未开始或正在下载的项目已停止。"));
            OnPropertyChanged(nameof(ResultCount));
            LastRun = "已取消 · " + DateTime.Now.ToString("HH:mm:ss");
            await SaveHistoryAsync("已取消", 0, 0, 0);
        }
        catch (Exception ex)
        {
            Progress = 100;
            Status = "同步失败：" + ex.Message;
            Results.Add(new SyncResultEntry(DateTimeOffset.Now, "同步失败：" + ex.Message));
            OnPropertyChanged(nameof(ResultCount));
            LastRun = "失败 · " + DateTime.Now.ToString("HH:mm:ss");
            App.Services.Log.Error("同步中心执行失败", ex);
            await SaveHistoryAsync("失败：" + ex.Message, 0, 0, 1);
        }
        finally
        {
            if (ReferenceEquals(_syncCts, cts)) _syncCts = null;
            OnPropertyChanged(nameof(IsRunning));
            RaiseCommandStates();
        }
    }

    private bool CanStart() => _syncCts is null;

    private void Cancel()
    {
        if (_syncCts is null) return;
        Status = "正在取消同步…";
        _syncCts.Cancel();
        CancelCommand.RaiseCanExecuteChanged();
        ClearCurrentCommand.RaiseCanExecuteChanged();
        ClearHistoryCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        SyncAllCommand.RaiseCanExecuteChanged();
        SyncMapsCommand.RaiseCanExecuteChanged();
        SyncModsCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private void LoadHistory()
    {
        try
        {
            foreach (var record in App.Services.SyncHistory.Load()) History.Add(record);
            OnPropertyChanged(nameof(HistoryCount));
            var latest = History.FirstOrDefault();
            if (latest is null) return;
            RunScope = latest.Scope;
            Status = "上次同步 · " + latest.Summary;
            LastRun = FormatLastRun(latest);
            foreach (var message in latest.Messages) Results.Add(new SyncResultEntry(latest.EndedAt, message));
            OnPropertyChanged(nameof(ResultCount));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("读取同步历史失败", ex);
        }
    }

    private async Task SaveHistoryAsync(string status, int installed, int skipped, int failed)
    {
        var record = new SyncRunRecord
        {
            StartedAt = _startedAt,
            EndedAt = DateTimeOffset.Now,
            Scope = RunScope,
            Status = status,
            Installed = installed,
            Skipped = skipped,
            Failed = failed,
            Messages = Results.Select(x => x.Message).ToList()
        };
        try
        {
            await App.Services.SyncHistory.AppendAsync(record);
            History.Insert(0, record);
            while (History.Count > 30) History.RemoveAt(History.Count - 1);
            OnPropertyChanged(nameof(HistoryCount));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("保存同步历史失败", ex);
        }
    }

    private static string FormatLastRun(SyncRunRecord record) => record.EndedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + record.Status;

    private void ClearCurrent()
    {
        Results.Clear();
        OnPropertyChanged(nameof(ResultCount));
        Status = "本次显示记录已清除；已保存的历史记录仍然保留。";
        RaiseCommandStates();
    }

    private async Task ClearHistoryAsync()
    {
        if (MessageBox.Show("确定清除全部同步历史吗？此操作不会删除地图、MOD 或备份文件。", "清除同步历史", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            await App.Services.SyncHistory.ClearAsync();
            History.Clear();
            OnPropertyChanged(nameof(HistoryCount));
            Status = "同步历史已清除；本次显示记录仍然保留。";
        }
        catch (Exception ex)
        {
            Status = "清除同步历史失败：" + ex.Message;
            App.Services.Log.Error("清除同步历史失败", ex);
        }
        finally
        {
            RaiseCommandStates();
        }
    }
}

public sealed record SyncResultEntry(DateTimeOffset Timestamp, string Message)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
}
