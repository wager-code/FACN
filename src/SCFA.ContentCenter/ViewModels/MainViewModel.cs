using System.Windows.Input;
using System.Windows;
using System.ComponentModel;
using System.Collections.Specialized;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Services;
using SCFA.ContentCenter.Views;

namespace SCFA.ContentCenter.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly SyncPageViewModel _syncPage = new();
    private readonly Lazy<DashboardPageViewModel> _dashboardPage;
    private readonly Lazy<CloudPageViewModel> _cloudMapsPage;
    private readonly Lazy<CloudPageViewModel> _cloudModsPage;
    private readonly Lazy<LocalPageViewModel> _localMapsPage;
    private readonly Lazy<LocalPageViewModel> _localModsPage;
    private readonly Lazy<DownloadsPageViewModel> _downloadsPage;
    private readonly Lazy<BackupsPageViewModel> _backupsPage;
    private readonly Lazy<CloudHistoryPageViewModel> _cloudHistoryPage;
    private readonly Lazy<SettingsPageViewModel> _settingsPage;
    private readonly Lazy<DiagnosticsPageViewModel> _diagnosticsPage;
    private readonly Lazy<UpdatesPageViewModel> _updatesPage;
    private readonly Lazy<SubmissionsPageViewModel> _submissionsPage;
    private readonly Lazy<ReviewPageViewModel> _reviewPage;
    private readonly Lazy<UsersPageViewModel> _usersPage;
    private readonly Lazy<OperationsPageViewModel> _operationsPage;
    private object _currentPage;
    private string _currentPageTitle;
    private string _currentPageSubtitle;
    public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
    public string CurrentPageTitle { get => _currentPageTitle; private set => Set(ref _currentPageTitle, value); }
    public string CurrentPageSubtitle { get => _currentPageSubtitle; private set => Set(ref _currentPageSubtitle, value); }
    private string UserKey => App.Services.OfflineMode ? "offline" : string.IsNullOrWhiteSpace(App.Services.CurrentUser.Id) ? App.Services.CurrentUser.Username : App.Services.CurrentUser.Id;
    private LocalUserProfile? LocalProfile => App.Services.Config.Current.LocalUserProfiles.FirstOrDefault(x => x.UserKey.Equals(UserKey, StringComparison.OrdinalIgnoreCase));
    public string UserDisplay => !string.IsNullOrWhiteSpace(LocalProfile?.DisplayName)
        ? LocalProfile.DisplayName
        : App.Services.OfflineMode ? "离线模式" : string.IsNullOrWhiteSpace(App.Services.CurrentUser.DisplayName) ? App.Services.CurrentUser.Username : App.Services.CurrentUser.DisplayName;
    public string UserInitial => string.IsNullOrWhiteSpace(UserDisplay) ? "?" : UserDisplay[..1].ToUpperInvariant();
    public string AvatarPath => File.Exists(LocalProfile?.AvatarPath) ? LocalProfile!.AvatarPath : "";
    public bool HasAvatar => AvatarPath.Length > 0;
    public string RoleDisplay => App.Services.OfflineMode ? "离线使用" : App.Services.CurrentUser.RoleKey.Trim().ToLowerInvariant() switch
    {
        "user" => "普通用户",
        "reviewer" => "内容审核员",
        "publisher" => "内容发布员",
        "admin" => "管理员",
        "super_admin" => "超级管理员",
        _ => string.IsNullOrWhiteSpace(App.Services.CurrentUser.RoleLabel) ? "普通用户" : App.Services.CurrentUser.RoleLabel
    };
    public string WorkspaceDisplay => App.Services.OfflineMode ? "离线工作区" : "在线内容服务";
    public string Version => AppVersion.Display;
    public string SyncBadgeText => _syncPage.IsRunning ? $"{_syncPage.Progress}%" : _syncPage.ResultCount > 0 ? _syncPage.ResultCount.ToString() : "";
    public Visibility SyncBadgeVisibility => _syncPage.IsRunning || _syncPage.ResultCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public int ActiveTaskCount => App.Services.Tasks.Tasks.Count(x => x.Status is "运行中" or "等待");
    public string DownloadBadgeText => ActiveTaskCount.ToString();
    public Visibility DownloadBadgeVisibility => ActiveTaskCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiagnosticBadgeVisibility
    {
        get
        {
            try { return Directory.Exists(App.Services.Paths.GetContentDirectory("地图")) && Directory.Exists(App.Services.Paths.GetContentDirectory("MOD")) ? Visibility.Collapsed : Visibility.Visible; }
            catch { return Visibility.Visible; }
        }
    }
    public ICommand HomeCommand { get; }
    public ICommand CloudMapsCommand { get; }
    public ICommand CloudModsCommand { get; }
    public ICommand LocalMapsCommand { get; }
    public ICommand LocalModsCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand DownloadsCommand { get; }
    public ICommand BackupsCommand { get; }
    public ICommand CloudHistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand UpdatesCommand { get; }
    public ICommand SubmissionsCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand UsersCommand { get; }
    public ICommand OperationsCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand EditProfileCommand { get; }
    public Visibility AccountToolsVisibility => !App.Services.OfflineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.Token) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdminToolsVisibility => IsAdminUser() ? Visibility.Visible : Visibility.Collapsed;

    public MainViewModel()
    {
        _dashboardPage = new(() => new DashboardPageViewModel(_syncPage));
        _cloudMapsPage = new(() => new CloudPageViewModel("地图"));
        _cloudModsPage = new(() => new CloudPageViewModel("MOD"));
        _localMapsPage = new(() => new LocalPageViewModel("地图"));
        _localModsPage = new(() => new LocalPageViewModel("MOD"));
        _downloadsPage = new(() => new DownloadsPageViewModel());
        _backupsPage = new(() => new BackupsPageViewModel());
        _cloudHistoryPage = new(() => new CloudHistoryPageViewModel());
        _settingsPage = new(() => new SettingsPageViewModel());
        _diagnosticsPage = new(() => new DiagnosticsPageViewModel());
        _updatesPage = new(() => new UpdatesPageViewModel());
        _submissionsPage = new(() => new SubmissionsPageViewModel());
        _reviewPage = new(() => new ReviewPageViewModel());
        _usersPage = new(() => new UsersPageViewModel());
        _operationsPage = new(() => new OperationsPageViewModel());

        var needsSetup = SetupPageViewModel.NeedsSetup();
        _currentPage = needsSetup ? new SetupPageViewModel() : _dashboardPage.Value;
        _currentPageTitle = needsSetup ? "首次设置" : "总览";
        _currentPageSubtitle = needsSetup ? "连接游戏目录与玩家内容空间" : "本地内容与服务状态概览";
        HomeCommand = new RelayCommand(() => Navigate(_dashboardPage.Value, "总览", "本地内容与服务状态概览"));
        CloudMapsCommand = new RelayCommand(() => Navigate(_cloudMapsPage.Value, "云端地图", "发现、筛选并安装社区地图"));
        CloudModsCommand = new RelayCommand(() => Navigate(_cloudModsPage.Value, "云端 MOD", "浏览并管理社区模组"));
        LocalMapsCommand = new RelayCommand(() => Navigate(_localMapsPage.Value, "本地地图", "检查玩家地图与安全卸载"));
        LocalModsCommand = new RelayCommand(() => Navigate(_localModsPage.Value, "本地 MOD", "检查已安装模组与版本"));
        SyncCommand = new RelayCommand(() => Navigate(_syncPage, "同步中心", "安全补齐、更新与修复内容"));
        DownloadsCommand = new RelayCommand(() => Navigate(_downloadsPage.Value, "下载任务", "查看进度、取消与重试"));
        BackupsCommand = new RelayCommand(() => Navigate(_backupsPage.Value, "历史回滚", "浏览安装快照并恢复内容"));
        CloudHistoryCommand = new RelayCommand(() => Navigate(_cloudHistoryPage.Value, "云端历史", "查看并安装服务器历史版本"));
        SettingsCommand = new RelayCommand(() => Navigate(_settingsPage.Value, "软件设置", "目录、启动与软件数据"));
        DiagnosticsCommand = new RelayCommand(() => Navigate(_diagnosticsPage.Value, "诊断中心", "检查环境、网络与配置健康"));
        UpdatesCommand = new RelayCommand(() => Navigate(_updatesPage.Value, "客户端更新", "检查并安全应用新版本"));
        SubmissionsCommand = new RelayCommand(() => Navigate(_submissionsPage.Value, "我的投稿", "准备内容并跟踪审核状态"));
        ReviewCommand = new RelayCommand(() => Navigate(_reviewPage.Value, "投稿审核", "审核并发布玩家内容"));
        UsersCommand = new RelayCommand(() => Navigate(_usersPage.Value, "用户管理", "管理角色、状态与登录会话"));
        OperationsCommand = new RelayCommand(() => Navigate(_operationsPage.Value, "服务器审计", "服务健康、权限与审计记录"));
        LogoutCommand = new AsyncRelayCommand(LogoutAsync);
        EditProfileCommand = new RelayCommand(EditProfile);
        _syncPage.PropertyChanged += SyncPagePropertyChanged;
        foreach (var task in App.Services.Tasks.Tasks) task.PropertyChanged += TaskPropertyChanged;
        App.Services.Tasks.Tasks.CollectionChanged += TasksCollectionChanged;
    }

    private void EditProfile()
    {
        var dialog = new ProfileWindow(UserKey) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true) RefreshProfile();
    }

    public void RefreshProfile()
    {
        OnPropertyChanged(nameof(UserDisplay));
        OnPropertyChanged(nameof(UserInitial));
        OnPropertyChanged(nameof(AvatarPath));
        OnPropertyChanged(nameof(HasAvatar));
        OnPropertyChanged(nameof(RoleDisplay));
    }

    private void TasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (AppTask task in e.OldItems) task.PropertyChanged -= TaskPropertyChanged;
        if (e.NewItems is not null) foreach (AppTask task in e.NewItems) task.PropertyChanged += TaskPropertyChanged;
        NotifyTaskBadges();
    }

    private void TaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppTask.Status)) NotifyTaskBadges();
    }

    private void NotifyTaskBadges()
    {
        OnPropertyChanged(nameof(ActiveTaskCount));
        OnPropertyChanged(nameof(DownloadBadgeText));
        OnPropertyChanged(nameof(DownloadBadgeVisibility));
    }

    private void SyncPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SyncPageViewModel.Progress) or nameof(SyncPageViewModel.IsRunning) or nameof(SyncPageViewModel.ResultCount))
        {
            OnPropertyChanged(nameof(SyncBadgeText));
            OnPropertyChanged(nameof(SyncBadgeVisibility));
        }
    }

    private void Navigate(object page, string title, string subtitle)
    {
        CurrentPage = page;
        CurrentPageTitle = title;
        CurrentPageSubtitle = subtitle;
        OnPropertyChanged(nameof(DiagnosticBadgeVisibility));
    }

    private static bool IsAdminUser()
    {
        var user = App.Services.CurrentUser;
        return !App.Services.OfflineMode && (user.RoleKey.Contains("admin", StringComparison.OrdinalIgnoreCase) || user.Permissions.Any(x => x.Equals("*", StringComparison.OrdinalIgnoreCase) || x.StartsWith("users.", StringComparison.OrdinalIgnoreCase) || x.StartsWith("submissions.", StringComparison.OrdinalIgnoreCase) || x.StartsWith("audit.", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task LogoutAsync()
    {
        if (MessageBox.Show("确定退出当前账号吗？", "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var offlineMode = App.Services.OfflineMode;
        try
        {
            if (!offlineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.Token))
                await App.Services.Auth.LogoutAsync();
        }
        catch (Exception ex)
        {
            App.Services.Log.Error("服务端退出登录失败，已清除本地会话", ex);
        }
        finally
        {
            if (!offlineMode) App.Services.Session.Clear();
            App.Services.Auth.SetToken("");
            App.Services.CurrentUser = new();
            App.Services.OfflineMode = false;
            App.OpenLoginWindow();
        }
    }
}
