using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class DashboardPageViewModel : ViewModelBase
{
    private readonly SyncPageViewModel _syncPage;
    private int _mapCount, _modCount, _taskCount, _issueCount, _runningTaskCount;
    private string _status = "正在读取本地状态…";
    private string _lastUpdated = "等待首次扫描";
    private string _healthSummary = "等待检查";
    private DashboardActivityItem? _lastScanActivity;

    public int MapCount { get => _mapCount; set => Set(ref _mapCount, value); }
    public int ModCount { get => _modCount; set => Set(ref _modCount, value); }
    public int TaskCount { get => _taskCount; set => Set(ref _taskCount, value); }
    public int IssueCount { get => _issueCount; set => Set(ref _issueCount, value); }
    public int RunningTaskCount { get => _runningTaskCount; set => Set(ref _runningTaskCount, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string LastUpdated { get => _lastUpdated; set => Set(ref _lastUpdated, value); }
    public string Version => AppVersion.Display;
    public string Mode => App.Services.OfflineMode ? "离线模式" : "在线模式";
    public string UserDisplay => string.IsNullOrWhiteSpace(App.Services.CurrentUser.DisplayName) ? App.Services.CurrentUser.Username : App.Services.CurrentUser.DisplayName;
    public string HealthSummary { get => _healthSummary; private set => Set(ref _healthSummary, value); }
    public ObservableCollection<DashboardActivityItem> RecentActivities { get; } = [];
    public ObservableCollection<DashboardHealthItem> HealthItems { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SyncAllCommand { get; }
    public AsyncRelayCommand CheckServiceCommand { get; }

    public DashboardPageViewModel(SyncPageViewModel syncPage)
    {
        _syncPage = syncPage;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SyncAllCommand = new AsyncRelayCommand(SyncAllAsync, () => !_syncPage.IsRunning);
        CheckServiceCommand = new AsyncRelayCommand(CheckServiceAsync);
        App.Services.Tasks.Tasks.CollectionChanged += TasksChanged;
        foreach (var task in App.Services.Tasks.Tasks) task.PropertyChanged += TaskChanged;
        RefreshTaskStats();
        RefreshActivities();
        RefreshHealth();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在扫描本地地图与 MOD…";
            var mapsTask = App.Services.Local.ScanAsync("地图");
            var modsTask = App.Services.Local.ScanAsync("MOD");
            await Task.WhenAll(mapsTask, modsTask);
            var maps = mapsTask.Result;
            var mods = modsTask.Result;
            MapCount = maps.Count(x => x.Valid);
            ModCount = mods.Count(x => x.Valid);
            IssueCount = maps.Count(x => !x.Valid) + mods.Count(x => !x.Valid);
            RefreshTaskStats();
            LastUpdated = "最后扫描 · " + DateTime.Now.ToString("HH:mm:ss");
            Status = IssueCount == 0
                ? $"本地内容读取完成 · 地图 {MapCount} · MOD {ModCount} · 未发现结构问题"
                : $"本地内容读取完成 · 地图 {MapCount} · MOD {ModCount} · {IssueCount} 项需要检查";
            _lastScanActivity = new DashboardActivityItem(DateTime.Now, "内容扫描完成",
                IssueCount == 0 ? $"地图 {MapCount} · MOD {ModCount} · 未发现结构问题" : $"地图 {MapCount} · MOD {ModCount} · {IssueCount} 项需要检查", AccentBrush);
            RefreshActivities();
            RefreshHealth();
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            _lastScanActivity = new DashboardActivityItem(DateTime.Now, "内容扫描失败", ex.Message, WarningBrush);
            RefreshActivities();
            RefreshHealth();
        }
    }

    private async Task SyncAllAsync()
    {
        try
        {
            Status = "同步任务已交给同步中心；切换页面不会暂停。";
            await _syncPage.RunAllAsync();
            RefreshTaskStats();
            LastUpdated = "最后同步 · " + DateTime.Now.ToString("HH:mm:ss");
            Status = _syncPage.Status;
            RefreshActivities();
            RefreshHealth();
        }
        catch (Exception ex)
        {
            Status = "同步失败：" + ex.Message;
            App.Services.Log.Error("首页同步失败", ex);
            RefreshActivities();
        }
    }

    private async Task CheckServiceAsync()
    {
        if (App.Services.OfflineMode)
        {
            Status = "当前为离线模式，无法检查账号服务。";
            RefreshHealth();
            return;
        }
        try
        {
            Status = "正在检查账号服务…";
            var health = await App.Services.Auth.HealthAsync();
            var version = string.IsNullOrWhiteSpace(health.Version) ? "未知版本" : health.Version;
            var updater = health.UpdaterReady
                ? string.IsNullOrWhiteSpace(health.UpdaterVersion) ? "Updater 已就绪（暂无发布版本）" : $"Updater {health.UpdaterVersion}"
                : "Updater 未就绪";
            Status = $"账号服务正常 · {version} · {updater}";
            RefreshHealth();
            RefreshActivities();
        }
        catch (Exception ex)
        {
            Status = "服务检查失败：" + ex.Message;
            App.Services.Log.Error("账号服务检查失败", ex);
            RefreshHealth();
            RefreshActivities();
        }
    }

    private void RefreshTaskStats()
    {
        TaskCount = App.Services.Tasks.Tasks.Count;
        RunningTaskCount = App.Services.Tasks.Tasks.Count(x => x.Status is "等待" or "运行中");
    }

    private void RefreshActivities()
    {
        RecentActivities.Clear();
        if (_lastScanActivity is not null) RecentActivities.Add(_lastScanActivity);
        foreach (var task in App.Services.Tasks.Tasks.OrderByDescending(x => x.CreatedAt).Take(Math.Max(0, 4 - RecentActivities.Count)))
        {
            var brush = task.Status switch
            {
                "完成" => AccentBrush,
                "失败" or "已中断" => WarningBrush,
                "运行中" or "等待" => PrimaryBrush,
                _ => MutedBrush
            };
            RecentActivities.Add(new DashboardActivityItem(task.CreatedAt, $"{task.Type} · {task.Name}",
                string.IsNullOrWhiteSpace(task.Detail) ? task.Status : $"{task.Status} · {task.Detail}", brush));
        }
        if (RecentActivities.Count == 0)
            RecentActivities.Add(new DashboardActivityItem(DateTime.Now, "暂无最近活动", "完成同步或安装后，任务记录会显示在这里。", MutedBrush));
    }

    private void RefreshHealth()
    {
        HealthItems.Clear();
        var mapsPath = TryGetContentPath("地图");
        var modsPath = TryGetContentPath("MOD");
        var contentReady = mapsPath is not null && modsPath is not null && Directory.Exists(mapsPath) && Directory.Exists(modsPath);
        HealthItems.Add(new DashboardHealthItem("内容目录", contentReady ? "正常" : "待设置",
            contentReady ? "地图与 MOD 目录可用" : "请在软件设置中选择游戏目录内已有路径", contentReady, "/Assets/Fluent/folder_regular.png"));

        var drive = TryGetDrive(mapsPath ?? modsPath);
        var storageReady = drive is { IsReady: true };
        var storageDetail = storageReady ? $"可用 {drive!.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB" : "无法读取存储空间";
        HealthItems.Add(new DashboardHealthItem("存储空间", storageReady ? "正常" : "不可用", storageDetail, storageReady, "/Assets/Fluent/server_regular.png"));

        var networkReady = !App.Services.OfflineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.BaseUrl);
        HealthItems.Add(new DashboardHealthItem("网络连接", networkReady ? "已配置" : "离线",
            networkReady ? "账号 API 已配置，按需检查" : "当前不会访问云端服务", networkReady, "/Assets/Fluent/cloud_regular.png"));
        HealthItems.Add(new DashboardHealthItem("客户端", "正常", $"{AppVersion.Display} · {(Environment.Is64BitProcess ? "x64" : "x86")}", true, "/Assets/Fluent/shield_regular.png"));
        HealthSummary = $"{HealthItems.Count(x => x.IsHealthy)} / {HealthItems.Count} 项正常";
    }

    private static string? TryGetContentPath(string kind)
    {
        try { return App.Services.Paths.GetContentDirectory(kind, create: false); }
        catch { return null; }
    }

    private static DriveInfo? TryGetDrive(string? path)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(path) ? null : Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? null : new DriveInfo(root);
        }
        catch { return null; }
    }

    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (AppTask task in e.NewItems) task.PropertyChanged += TaskChanged;
        RefreshTaskStats();
        RefreshActivities();
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTaskStats();
        RefreshActivities();
    }

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(48, 215, 190));
    private static readonly Brush PrimaryBrush = new SolidColorBrush(Color.FromRgb(104, 143, 255));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(255, 181, 72));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(116, 141, 169));
}

public sealed record DashboardActivityItem(DateTime CreatedAt, string Title, string Detail, Brush AccentBrush)
{
    public string TimeText => CreatedAt.ToString("HH:mm:ss");
}

public sealed record DashboardHealthItem(string Name, string Status, string Detail, bool IsHealthy, string IconSource)
{
    public Brush StatusBrush => IsHealthy
        ? new SolidColorBrush(Color.FromRgb(48, 215, 190))
        : new SolidColorBrush(Color.FromRgb(255, 181, 72));
}
