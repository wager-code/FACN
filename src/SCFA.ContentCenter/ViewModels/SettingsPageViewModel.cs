using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class SettingsPageViewModel : ViewModelBase
{
    private AppConfig _draft;
    private bool _busy;
    private bool _hasUnsavedChanges;
    private string _status = "当前显示已保存配置；修改内容在点击“保存并应用”前不会影响软件运行。";
    private string _directoryStatus = "尚未验证";
    private string _apiStatus = "尚未测试";
    private string _cosStatus = "尚未测试";
    private string _serverStatus = "尚未测试";

    public SettingsPageViewModel()
    {
        _draft = ConfigService.Clone(App.Services.Config.Current);
        BrowseGameCommand = new RelayCommand(() => Browse("选择最高指挥官：钢铁联盟游戏目录", value => GameRoot = value), CanStart);
        BrowseMapsCommand = new RelayCommand(() => Browse("选择玩家 Maps 目录", value => MapsDir = value), CanStart);
        BrowseModsCommand = new RelayCommand(() => Browse("选择玩家 Mods 目录", value => ModsDir = value), CanStart);
        AutoDetectCommand = new RelayCommand(AutoDetect, CanStart);
        UsePlayerDirectoriesCommand = new RelayCommand(UsePlayerDirectories, CanStart);
        ValidateDirectoriesCommand = new RelayCommand(ValidateDirectories, CanStart);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasUnsavedChanges && CanStart());
        DiscardCommand = new RelayCommand(DiscardChanges, () => HasUnsavedChanges && CanStart());
        RestoreDefaultsCommand = new RelayCommand(RestoreRecommendedDefaults, CanStart);
        TestApiCommand = new AsyncRelayCommand(TestApiAsync, CanStart);
        TestCosCommand = new AsyncRelayCommand(TestCosAsync, CanStart);
        TestServerFeaturesCommand = new AsyncRelayCommand(TestServerFeaturesAsync, CanStart);
        TestAllCommand = new AsyncRelayCommand(TestAllAsync, CanStart);
        OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(Path.GetDirectoryName(ConfigPath)!));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(DataDirectory));
    }

    public string GameRoot { get => _draft.GameRoot; set => SetDraft(value, () => _draft.GameRoot, x => _draft.GameRoot = x); }
    public string MapsDir { get => _draft.MapsDir; set => SetDraft(value, () => _draft.MapsDir, x => _draft.MapsDir = x); }
    public string ModsDir { get => _draft.ModsDir; set => SetDraft(value, () => _draft.ModsDir, x => _draft.ModsDir = x); }
    public string ApiUrl { get => _draft.ApiDirectUrl; set => SetDraft(value, () => _draft.ApiDirectUrl, x => _draft.ApiDirectUrl = x); }
    public string ApiPin { get => _draft.ApiDirectCertSha256; set => SetDraft(value, () => _draft.ApiDirectCertSha256, x => _draft.ApiDirectCertSha256 = x); }
    public string Bucket { get => _draft.Bucket; set => SetDraft(value, () => _draft.Bucket, x => _draft.Bucket = x); }
    public string Region { get => _draft.Region; set => SetDraft(value, () => _draft.Region, x => _draft.Region = x); }
    public string Root { get => _draft.Root; set => SetDraft(value, () => _draft.Root, x => _draft.Root = x); }
    public string UpdateChannel { get => _draft.UpdateChannel; set => SetDraft(value ?? "stable", () => _draft.UpdateChannel, x => _draft.UpdateChannel = x); }
    public string UpdateManifestUrl { get => _draft.UpdateManifestUrl; set => SetDraft(value, () => _draft.UpdateManifestUrl, x => _draft.UpdateManifestUrl = x); }
    public bool AutoCheckUpdates { get => _draft.AutoCheckUpdates; set => SetDraft(value, () => _draft.AutoCheckUpdates, x => _draft.AutoCheckUpdates = x); }
    public bool AutoLayout { get => _draft.AutoLayout; set => SetDraft(value, () => _draft.AutoLayout, x => _draft.AutoLayout = x); }
    public bool OfflineAllowed { get => _draft.OfflineAllowed; set => SetDraft(value, () => _draft.OfflineAllowed, x => _draft.OfflineAllowed = x); }
    public string SubmissionApiPath { get => _draft.SubmissionApiPath; set => SetDraft(value, () => _draft.SubmissionApiPath, x => _draft.SubmissionApiPath = x); }
    public string SubmissionAdminPath { get => _draft.SubmissionAdminPath; set => SetDraft(value, () => _draft.SubmissionAdminPath, x => _draft.SubmissionAdminPath = x); }
    public string AdminUsersPath { get => _draft.AdminUsersPath; set => SetDraft(value, () => _draft.AdminUsersPath, x => _draft.AdminUsersPath = x); }
    public string AuditApiPath { get => _draft.AuditApiPath; set => SetDraft(value, () => _draft.AuditApiPath, x => _draft.AuditApiPath = x); }
    public string ContentHistoryApiPath { get => _draft.ContentHistoryApiPath; set => SetDraft(value, () => _draft.ContentHistoryApiPath, x => _draft.ContentHistoryApiPath = x); }
    public string ConfigPath => App.Services.Config.ConfigPath;
    public string DataDirectory => ConfigService.ResolveDataDirectory();
    public bool IsAdvancedSettingsAvailable => HasAdminAccess();
    public Visibility AdvancedSettingsVisibility => IsAdvancedSettingsAvailable ? Visibility.Visible : Visibility.Collapsed;
    public string SettingsAudienceHint => IsAdvancedSettingsAvailable
        ? "可调整游戏目录、客户端行为和受控连接参数。"
        : "您只需要设置游戏目录和使用偏好；连接服务由平台统一维护。";
    public bool HasUnsavedChanges { get => _hasUnsavedChanges; private set { if (Set(ref _hasUnsavedChanges, value)) { OnPropertyChanged(nameof(ChangeState)); OnPropertyChanged(nameof(DraftStateLabel)); } } }
    public string ChangeState => HasUnsavedChanges ? "有未保存更改" : "已与磁盘配置同步";
    public string DraftStateLabel => HasUnsavedChanges ? "草稿尚未应用" : "运行配置已同步";
    public bool IsBusy => _busy;
    public string BusyStateLabel => IsBusy ? "正在执行设置操作" : "设置控制台就绪";
    public int ConfigurationScore
    {
        get
        {
            var fields = new List<bool>
            {
                !string.IsNullOrWhiteSpace(MapsDir),
                !string.IsNullOrWhiteSpace(ModsDir),
                !string.IsNullOrWhiteSpace(UpdateChannel)
            };
            if (IsAdvancedSettingsAvailable)
            {
                fields.AddRange([
                    !string.IsNullOrWhiteSpace(ApiUrl),
                    !string.IsNullOrWhiteSpace(ApiPin),
                    !string.IsNullOrWhiteSpace(Bucket),
                    !string.IsNullOrWhiteSpace(Region),
                    !string.IsNullOrWhiteSpace(Root),
                    !string.IsNullOrWhiteSpace(SubmissionApiPath)
                ]);
            }
            return fields.Count(x => x) * 100 / fields.Count;
        }
    }
    public int PassedCheckCount => new[] { DirectoryStatus, ApiStatus, CosStatus, ServerStatus }.Count(IsPassingStatus);
    public string ValidationSummary => $"{PassedCheckCount}/4 组检查通过";
    public string DirectoryHealthLabel => HealthLabel(DirectoryStatus, "尚未验证");
    public string ApiHealthLabel => HealthLabel(ApiStatus, "尚未测试");
    public string CosHealthLabel => HealthLabel(CosStatus, "尚未测试");
    public string ServerHealthLabel => HealthLabel(ServerStatus, "尚未测试");
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string DirectoryStatus { get => _directoryStatus; private set { if (Set(ref _directoryStatus, value)) NotifyValidationState(nameof(DirectoryHealthLabel)); } }
    public string ApiStatus { get => _apiStatus; private set { if (Set(ref _apiStatus, value)) NotifyValidationState(nameof(ApiHealthLabel)); } }
    public string CosStatus { get => _cosStatus; private set { if (Set(ref _cosStatus, value)) NotifyValidationState(nameof(CosHealthLabel)); } }
    public string ServerStatus { get => _serverStatus; private set { if (Set(ref _serverStatus, value)) NotifyValidationState(nameof(ServerHealthLabel)); } }

    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseMapsCommand { get; }
    public RelayCommand BrowseModsCommand { get; }
    public RelayCommand AutoDetectCommand { get; }
    public RelayCommand UsePlayerDirectoriesCommand { get; }
    public RelayCommand ValidateDirectoriesCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public AsyncRelayCommand TestApiCommand { get; }
    public AsyncRelayCommand TestCosCommand { get; }
    public AsyncRelayCommand TestServerFeaturesCommand { get; }
    public AsyncRelayCommand TestAllCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    private void SetDraft<T>(T value, Func<T> getter, Action<T> setter, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(getter(), value)) return;
        setter(value);
        OnPropertyChanged(propertyName);
        HasUnsavedChanges = true;
        Status = "设置已修改，尚未保存；当前运行配置没有改变。";
        OnPropertyChanged(nameof(ConfigurationScore));
        RaiseCommandStates();
    }

    private void AutoDetect()
    {
        var detected = App.Services.Paths.AutoDetectGameRoot();
        if (detected.Length == 0) Status = "未检测到包含游戏可执行文件的安装目录。";
        else
        {
            GameRoot = detected;
            Status = "已填入检测到的游戏目录，保存前可先验证目录。";
        }
    }

    private void UsePlayerDirectories()
    {
        if (string.IsNullOrWhiteSpace(GameRoot)) { Status = "请先选择或自动检测游戏目录。"; return; }
        var maps = Path.Combine(GameRoot, "maps");
        var mods = Path.Combine(GameRoot, "mods");
        MapsDir = Directory.Exists(maps) ? maps : "";
        ModsDir = Directory.Exists(mods) ? mods : "";
        Status = Directory.Exists(maps) && Directory.Exists(mods)
            ? "已填入游戏目录中现有的 maps/mods，尚未保存。"
            : "游戏目录中未同时找到现有 maps/mods；请手动选择，软件不会创建目录。";
    }

    private void ValidateDirectories()
    {
        try
        {
            var result = SettingsValidator.ValidateDirectories(_draft, App.Services.Paths, createDirectories: false);
            var gameLabel = GamePathService.TryFindScfaExecutable(result.GameRoot, out var executable)
                ? Path.GetRelativePath(result.GameRoot, executable)
                : "未配置（同步仍可使用玩家目录）";
            DirectoryStatus = $"验证通过 · 主程序 {gameLabel} · Maps {result.MapsDir} · Mods {result.ModsDir}";
            Status = "目录验证通过；软件不会创建或改写内容根目录。";
        }
        catch (Exception ex)
        {
            DirectoryStatus = "验证失败 · " + ex.Message;
            Status = DirectoryStatus;
        }
    }

    private async Task SaveAsync()
    {
        SetBusy(true);
        try
        {
            Status = "正在校验全部设置…";
            var validated = SettingsValidator.ValidateAndNormalize(_draft, App.Services.Paths, createDirectories: true);
            await App.Services.Config.SaveAsync(validated);
            _draft = ConfigService.Clone(App.Services.Config.Current);
            RaiseEditorProperties();
            HasUnsavedChanges = false;
            DirectoryStatus = "已验证并保存";
            var authChanged = App.Services.ReconfigureAuth();
            App.Services.Log.Info("软件设置已保存并应用");
            if (authChanged)
            {
                Status = "设置已保存；账号 API 或证书发生变化，需要重新登录。";
                App.Services.CurrentUser = new();
                App.Services.OfflineMode = false;
                App.OpenLoginWindow();
                return;
            }
            Status = "设置已保存并立即应用。自适应布局会在窗口尺寸变化时刷新。";
        }
        catch (Exception ex)
        {
            Status = "保存失败，原运行配置保持不变：" + ex.Message;
            App.Services.Log.Error("保存软件设置失败", ex);
        }
        finally { SetBusy(false); }
    }

    private void DiscardChanges()
    {
        if (MessageBox.Show("放弃本页所有尚未保存的修改？", "放弃设置修改", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _draft = ConfigService.Clone(App.Services.Config.Current);
        RaiseEditorProperties();
        HasUnsavedChanges = false;
        DirectoryStatus = ApiStatus = CosStatus = ServerStatus = "尚未测试";
        Status = "已放弃修改并重新载入磁盘配置。";
        RaiseCommandStates();
    }

    private void RestoreRecommendedDefaults()
    {
        var defaults = AppConfig.Defaults();
        var restored = ConfigService.Clone(App.Services.Config.Current);
        restored.GameRoot = App.Services.Config.Current.GameRoot;
        restored.MapsDir = App.Services.Config.Current.MapsDir;
        restored.ModsDir = App.Services.Config.Current.ModsDir;
        restored.ApiBaseUrl = defaults.ApiBaseUrl;
        restored.ApiDirectUrl = defaults.ApiDirectUrl;
        restored.ApiDirectCertSha256 = defaults.ApiDirectCertSha256;
        restored.Bucket = defaults.Bucket;
        restored.Region = defaults.Region;
        restored.Root = defaults.Root;
        restored.UpdateChannel = defaults.UpdateChannel;
        restored.UpdateManifestUrl = defaults.UpdateManifestUrl;
        restored.AutoCheckUpdates = defaults.AutoCheckUpdates;
        restored.AutoLayout = defaults.AutoLayout;
        restored.OfflineAllowed = defaults.OfflineAllowed;
        restored.SubmissionApiPath = defaults.SubmissionApiPath;
        restored.SubmissionAdminPath = defaults.SubmissionAdminPath;
        restored.AdminUsersPath = defaults.AdminUsersPath;
        restored.AuditApiPath = defaults.AuditApiPath;
        restored.ContentHistoryApiPath = defaults.ContentHistoryApiPath;
        _draft = restored;
        RaiseEditorProperties();
        HasUnsavedChanges = true;
        DirectoryStatus = ApiStatus = CosStatus = ServerStatus = "尚未测试";
        Status = "已载入推荐默认值，但尚未保存；账号记录、收藏和最近内容未被清除。";
        RaiseCommandStates();
    }

    private async Task TestApiAsync() => await RunSingleTestAsync(async () =>
    {
        ApiStatus = "正在连接…";
        await TestApiCoreAsync();
        Status = ApiStatus;
    }, ex => ApiStatus = "测试失败 · " + ex.Message);

    private async Task TestCosAsync() => await RunSingleTestAsync(async () =>
    {
        CosStatus = "正在读取地图和 MOD 正式清单…";
        await TestCosCoreAsync();
        Status = CosStatus;
    }, ex => CosStatus = "测试失败 · " + ex.Message);

    private async Task TestServerFeaturesAsync() => await RunSingleTestAsync(async () =>
    {
        ServerStatus = "正在执行只读服务检测…";
        await TestServerFeaturesCoreAsync();
        Status = ServerStatus;
    }, ex => ServerStatus = "测试失败 · " + ex.Message);

    private async Task TestAllAsync()
    {
        SetBusy(true);
        var failures = 0;
        try
        {
            Status = "正在依次测试未保存的 API、COS 和服务器功能设置…";
            try { await TestApiCoreAsync(); } catch (Exception ex) { ApiStatus = "测试失败 · " + ex.Message; failures++; }
            try { await TestCosCoreAsync(); } catch (Exception ex) { CosStatus = "测试失败 · " + ex.Message; failures++; }
            try { await TestServerFeaturesCoreAsync(); } catch (Exception ex) { ServerStatus = "测试失败 · " + ex.Message; failures++; }
            Status = failures == 0 ? "全部只读连接测试通过；这些参数仍需点击“保存并应用”才会生效。" : $"连接测试完成，{failures} 个分组失败；请查看各分组状态。";
        }
        finally { SetBusy(false); }
    }

    private async Task TestApiCoreAsync()
    {
        var apiSettings = SettingsValidator.ValidateApiSettings(_draft);
        using var api = new AuthApiClient(apiSettings.Endpoint, apiSettings.Fingerprint);
        var health = await api.HealthAsync();
        if (!health.Ok) throw new InvalidDataException("账号 API 健康检查返回异常");
        ApiStatus = $"连接正常 · {api.BaseUrl} · API {health.Version}";
    }

    private async Task TestCosCoreAsync()
    {
        var cos = SettingsValidator.ValidateCosSettings(_draft);
        var snapshot = ConfigService.Clone(_draft);
        snapshot.Bucket = cos.Bucket;
        snapshot.Region = cos.Region;
        snapshot.Root = cos.Root;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var cloud = new CloudCatalogService(snapshot, http);
        var mapsTask = cloud.FetchAsync("地图");
        var modsTask = cloud.FetchAsync("MOD");
        await Task.WhenAll(mapsTask, modsTask);
        CosStatus = $"连接正常 · 地图 {mapsTask.Result.Count} 项 · MOD {modsTask.Result.Count} 项";
    }

    private async Task TestServerFeaturesCoreAsync()
    {
        var apiSettings = SettingsValidator.ValidateApiSettings(_draft);
        var submissionPath = SettingsValidator.NormalizeApiPath(_draft.SubmissionApiPath, "普通投稿 API");
        _ = SettingsValidator.NormalizeApiPath(_draft.SubmissionAdminPath, "投稿审核 API");
        _ = SettingsValidator.NormalizeApiPath(_draft.AdminUsersPath, "用户管理 API");
        _ = SettingsValidator.NormalizeApiPath(_draft.AuditApiPath, "审计日志 API");
        _ = SettingsValidator.NormalizeApiPath(_draft.ContentHistoryApiPath, "内容历史 API");
        using var api = new AuthApiClient(apiSettings.Endpoint, apiSettings.Fingerprint);
        var health = await api.HealthAsync();
        if (!health.Ok) throw new InvalidDataException("服务器健康检查返回异常");
        var mine = "我的投稿路由未登录检测";
        var sameAuthenticatedServer = !App.Services.OfflineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.Token) &&
            string.Equals(api.BaseUrl, App.Services.Auth.BaseUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(api.PinnedCertSha256, App.Services.Auth.PinnedCertSha256, StringComparison.OrdinalIgnoreCase);
        if (sameAuthenticatedServer)
        {
            api.SetToken(App.Services.Auth.Token);
            _ = await api.GetJsonAsync<JsonElement>(submissionPath + "/mine");
            mine = "我的投稿路由可读";
        }
        ServerStatus = $"只读检测通过 · 投稿{(health.SubmissionReady ? "已就绪" : "未就绪")} · 更新{(health.UpdaterReady ? "已就绪" : "未就绪")} · {mine}";
    }

    private async Task RunSingleTestAsync(Func<Task> test, Action<Exception> onError)
    {
        SetBusy(true);
        try { await test(); }
        catch (Exception ex)
        {
            onError(ex);
            Status = "连接测试失败：" + ex.Message;
            App.Services.Log.Error("软件设置连接测试失败", ex);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyStateLabel));
        RaiseCommandStates();
    }

    private bool CanStart() => !_busy;

    private void RaiseCommandStates()
    {
        BrowseGameCommand.RaiseCanExecuteChanged();
        BrowseMapsCommand.RaiseCanExecuteChanged();
        BrowseModsCommand.RaiseCanExecuteChanged();
        AutoDetectCommand.RaiseCanExecuteChanged();
        UsePlayerDirectoriesCommand.RaiseCanExecuteChanged();
        ValidateDirectoriesCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        RestoreDefaultsCommand.RaiseCanExecuteChanged();
        TestApiCommand.RaiseCanExecuteChanged();
        TestCosCommand.RaiseCanExecuteChanged();
        TestServerFeaturesCommand.RaiseCanExecuteChanged();
        TestAllCommand.RaiseCanExecuteChanged();
    }

    private void RaiseEditorProperties()
    {
        foreach (var name in new[]
        {
            nameof(GameRoot), nameof(MapsDir), nameof(ModsDir), nameof(ApiUrl), nameof(ApiPin), nameof(Bucket), nameof(Region), nameof(Root),
            nameof(UpdateChannel), nameof(UpdateManifestUrl), nameof(AutoCheckUpdates), nameof(AutoLayout), nameof(OfflineAllowed),
            nameof(SubmissionApiPath), nameof(SubmissionAdminPath), nameof(AdminUsersPath), nameof(AuditApiPath), nameof(ContentHistoryApiPath)
        }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(ConfigurationScore));
    }

    private void NotifyValidationState(string healthProperty)
    {
        OnPropertyChanged(healthProperty);
        OnPropertyChanged(nameof(PassedCheckCount));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    private static bool IsPassingStatus(string value) => value.Contains("通过", StringComparison.CurrentCultureIgnoreCase) || value.Contains("正常", StringComparison.CurrentCultureIgnoreCase) || value.Contains("已验证", StringComparison.CurrentCultureIgnoreCase) || value.Contains("已保存", StringComparison.CurrentCultureIgnoreCase);

    private static bool HasAdminAccess()
    {
        var user = App.Services.CurrentUser;
        return !App.Services.OfflineMode &&
               (user.RoleKey.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                user.Permissions.Any(x => x.Equals("*", StringComparison.OrdinalIgnoreCase) ||
                                          x.StartsWith("users.", StringComparison.OrdinalIgnoreCase) ||
                                          x.StartsWith("submissions.", StringComparison.OrdinalIgnoreCase) ||
                                          x.StartsWith("audit.", StringComparison.OrdinalIgnoreCase)));
    }

    private static string HealthLabel(string value, string emptyLabel)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("尚未", StringComparison.CurrentCultureIgnoreCase)) return emptyLabel;
        if (value.Contains("失败", StringComparison.CurrentCultureIgnoreCase) || value.Contains("异常", StringComparison.CurrentCultureIgnoreCase)) return "需要处理";
        if (value.Contains("正在", StringComparison.CurrentCultureIgnoreCase)) return "检查中";
        return IsPassingStatus(value) ? "检查通过" : "已有结果";
    }

    private static void Browse(string title, Action<string> setter)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true) setter(dialog.FolderName);
    }

    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
