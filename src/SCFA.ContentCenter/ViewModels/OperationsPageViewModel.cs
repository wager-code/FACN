using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class OperationsPageViewModel : ViewModelBase
{
    private string _status = "等待刷新";
    private string _apiStatus = "—";
    private string _updaterStatus = "—";
    private string _directStatus = "—";
    private string _auditSearch = "";
    private AuditRecord? _selectedAudit;
    private int _readyServices;
    private string _lastRefresh = "尚未刷新";
    private bool? _auditIntegrity;

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
    public string HealthLabel => ReadyServices == 3 ? "全部服务就绪" : $"{ReadyServices}/3 项服务就绪";
    public string AuditAccessLabel => !CanReadAudit() ? "当前账号无审计读取权限" : _auditIntegrity == false ? "审计完整性异常" : _auditIntegrity == true ? "审计完整性已验证" : "审计读取权限已验证";
    public string SelectedAuditTitle => SelectedAudit is null ? "请选择一条审计记录" : $"{SelectedAudit.Action} · {SelectedAudit.Result}";
    public string SelectedAuditMetadata => SelectedAudit is null ? "选择后查看操作者、目标、IP 和详细结果" : $"{SelectedAudit.EffectiveActor} → {SelectedAudit.EffectiveTarget} · {SelectedAudit.EffectiveTime}";
    public string SelectedAuditDetail => SelectedAudit is null ? "尚未选择记录" : string.IsNullOrWhiteSpace(SelectedAudit.Detail) ? "服务器未返回详细说明" : SelectedAudit.Detail;
    public AsyncRelayCommand RefreshCommand { get; }

    private bool FilterAudit(object value)
    {
        if (value is not AuditRecord item || string.IsNullOrWhiteSpace(AuditSearch)) return true;
        var query = AuditSearch.Trim();
        return item.EffectiveActor.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Action.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.EffectiveTarget.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Result.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.EffectiveIp.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        try
        {
            Status = "正在读取服务器运行状态…";
            var health = await App.Services.Auth.HealthAsync();
            ApiStatus = health.Ok ? $"正常 · API {health.Version}" : "异常";
            UpdaterStatus = health.UpdaterReady ? string.IsNullOrWhiteSpace(health.UpdaterVersion) ? "已就绪 · 暂无发布版本" : "已就绪 · " + health.UpdaterVersion : "未就绪";
            DirectStatus = health.DirectReady ? health.DirectUrl : "未就绪";
            ReadyServices = new[] { health.Ok, health.UpdaterReady, health.DirectReady }.Count(x => x);
            OnPropertyChanged(nameof(HealthLabel));
            AuditItems.Clear();
            _auditIntegrity = null;
            if (CanReadAudit())
            {
                try
                {
                    var audit = await App.Services.Management.FetchAuditResultAsync();
                    foreach (var item in audit.Records) AuditItems.Add(item);
                    _auditIntegrity = audit.IntegrityOk;
                }
                catch (Exception ex) { Status = "服务状态正常；审计日志读取失败：" + ex.Message; return; }
            }
            AuditView.Refresh();
            OnPropertyChanged(nameof(AuditCount));
            OnPropertyChanged(nameof(VisibleAuditCount));
            OnPropertyChanged(nameof(FailedAuditCount));
            OnPropertyChanged(nameof(AuditAccessLabel));
            LastRefresh = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Status = !CanReadAudit() ? "服务状态已读取；当前账号没有审计日志权限" :
                _auditIntegrity == false ? $"警告：服务器审计完整性验证失败 · 已读取 {AuditItems.Count} 条" :
                $"服务状态与真实审计日志已读取 · {AuditItems.Count} 条";
        }
        catch (Exception ex)
        {
            Status = "刷新失败：" + ex.Message;
            App.Services.Log.Error("运营状态刷新失败", ex);
        }
    }

    private static bool CanReadAudit() => AccessPolicy.CanReadAudit(App.Services.CurrentUser);
}
