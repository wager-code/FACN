using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class OperationsPageViewModel : ViewModelBase
{
    private string _status = "等待刷新";
    private string _apiStatus = "—";
    private string _submissionStatus = "—";
    private string _updaterStatus = "—";
    private string _directStatus = "—";
    private string _auditSearch = "";
    private AuditRecord? _selectedAudit;
    private int _readyServices;
    private string _lastRefresh = "尚未刷新";

    public OperationsPageViewModel()
    {
        AuditView = CollectionViewSource.GetDefaultView(AuditItems);
        AuditView.Filter = FilterAudit;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }

    public ObservableCollection<AuditRecord> AuditItems { get; } = [];
    public ICollectionView AuditView { get; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ApiStatus { get => _apiStatus; set => Set(ref _apiStatus, value); }
    public string SubmissionStatus { get => _submissionStatus; set => Set(ref _submissionStatus, value); }
    public string UpdaterStatus { get => _updaterStatus; set => Set(ref _updaterStatus, value); }
    public string DirectStatus { get => _directStatus; set => Set(ref _directStatus, value); }
    public string AuditSearch { get => _auditSearch; set { if (Set(ref _auditSearch, value)) { AuditView.Refresh(); OnPropertyChanged(nameof(VisibleAuditCount)); } } }
    public AuditRecord? SelectedAudit
    {
        get => _selectedAudit;
        set
        {
            if (!Set(ref _selectedAudit, value)) return;
            OnPropertyChanged(nameof(SelectedAuditTitle));
            OnPropertyChanged(nameof(SelectedAuditMetadata));
            OnPropertyChanged(nameof(SelectedAuditDetail));
        }
    }
    public int ReadyServices { get => _readyServices; private set => Set(ref _readyServices, value); }
    public string LastRefresh { get => _lastRefresh; private set => Set(ref _lastRefresh, value); }
    public int AuditCount => AuditItems.Count;
    public int VisibleAuditCount => AuditView.Cast<AuditRecord>().Count();
    public int FailedAuditCount => AuditItems.Count(x => x.Result.Contains("fail", StringComparison.OrdinalIgnoreCase) || x.Result.Contains("失败", StringComparison.CurrentCultureIgnoreCase) || x.Result.Contains("拒绝", StringComparison.CurrentCultureIgnoreCase));
    public string HealthLabel => ReadyServices == 4 ? "全部服务就绪" : $"{ReadyServices}/4 项服务就绪";
    public string AuditAccessLabel => CanReadAudit() ? "审计读取权限已验证" : "当前账号无审计读取权限";
    public string SelectedAuditTitle => SelectedAudit is null ? "请选择一条审计记录" : $"{SelectedAudit.Action} · {SelectedAudit.Result}";
    public string SelectedAuditMetadata => SelectedAudit is null ? "选择后查看操作者、目标、IP 和详细结果" : $"{SelectedAudit.Actor} → {SelectedAudit.Target} · {SelectedAudit.EffectiveTime}";
    public string SelectedAuditDetail => SelectedAudit is null ? "尚未选择记录" : string.IsNullOrWhiteSpace(SelectedAudit.Detail) ? "服务器未返回详细说明" : SelectedAudit.Detail;
    public AsyncRelayCommand RefreshCommand { get; }

    private bool FilterAudit(object value)
    {
        if (value is not AuditRecord item || string.IsNullOrWhiteSpace(AuditSearch)) return true;
        var query = AuditSearch.Trim();
        return item.Actor.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Action.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Result.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Ip.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在读取服务器运行状态…";
            var health = await App.Services.Auth.HealthAsync();
            ApiStatus = health.Ok ? $"正常 · API {health.Version}" : "异常";
            SubmissionStatus = health.SubmissionReady ? "已就绪" : "未就绪";
            UpdaterStatus = health.UpdaterReady ? string.IsNullOrWhiteSpace(health.UpdaterVersion) ? "已就绪 · 暂无发布版本" : "已就绪 · " + health.UpdaterVersion : "未就绪";
            DirectStatus = health.DirectReady ? health.DirectUrl : "未就绪";
            ReadyServices = new[] { health.Ok, health.SubmissionReady, health.UpdaterReady, health.DirectReady }.Count(x => x);
            OnPropertyChanged(nameof(HealthLabel));
            AuditItems.Clear();
            if (CanReadAudit())
            {
                try { foreach (var item in await App.Services.Management.FetchAuditAsync()) AuditItems.Add(item); }
                catch (Exception ex) { Status = "服务状态正常；审计日志读取失败：" + ex.Message; return; }
            }
            AuditView.Refresh();
            OnPropertyChanged(nameof(AuditCount));
            OnPropertyChanged(nameof(VisibleAuditCount));
            OnPropertyChanged(nameof(FailedAuditCount));
            LastRefresh = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Status = CanReadAudit() ? $"服务状态与真实审计日志已读取 · {AuditItems.Count} 条" : "服务状态已读取；当前账号没有审计日志权限";
        }
        catch (Exception ex)
        {
            Status = "刷新失败：" + ex.Message;
            App.Services.Log.Error("运营状态刷新失败", ex);
        }
    }

    private static bool CanReadAudit()
    {
        var user = App.Services.CurrentUser;
        return user.RoleKey.Contains("admin", StringComparison.OrdinalIgnoreCase) || user.Permissions.Any(x => x.Equals("audit.read", StringComparison.OrdinalIgnoreCase) || x.Equals("*", StringComparison.OrdinalIgnoreCase));
    }
}
