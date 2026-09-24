using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class ServiceRegistry
{
    public ConfigService Config { get; private init; } = null!;
    public LogService Log { get; private init; } = null!;
    public AuthApiClient Auth { get; private set; } = null!;
    public CloudCatalogService Cloud { get; private init; } = null!;
    public GamePathService Paths { get; private init; } = null!;
    public LocalContentService Local { get; private init; } = null!;
    public TaskService Tasks { get; private init; } = null!;
    public BackupService Backups { get; private init; } = null!;
    public InstallService Install { get; private init; } = null!;
    public SyncService Sync { get; private init; } = null!;
    public SyncHistoryService SyncHistory { get; private init; } = null!;
    public UserSessionService Session { get; private init; } = null!;
    public DiagnosticsService Diagnostics { get; private init; } = null!;
    public UpdateService Updates { get; private init; } = null!;
    public SubmissionService Submissions { get; private init; } = null!;
    public ManagementService Management { get; private init; } = null!;
    public CloudHistoryService CloudHistory { get; private init; } = null!;
    public UserInfo CurrentUser { get; set; } = new();
    public bool OfflineMode { get; set; }

    public static async Task<ServiceRegistry> CreateAsync()
    {
        var log = new LogService();
        var config = new ConfigService(); await config.LoadAsync();
        var endpoint = !string.IsNullOrWhiteSpace(config.Current.ApiDirectUrl) ? config.Current.ApiDirectUrl : config.Current.ApiBaseUrl;
        var auth = new AuthApiClient(endpoint, config.Current.ApiDirectCertSha256);
        var cloud = new CloudCatalogService(config);
        var paths = new GamePathService(config);
        var local = new LocalContentService(paths, log);
        var tasks = new TaskService();
        var backups = new BackupService(paths, log);
        var install = new InstallService(cloud, paths, local, backups, tasks, log, config);
        var sync = new SyncService(cloud, local, install, log);
        var syncHistory = new SyncHistoryService();
        var session = new UserSessionService();
        var diagnostics = new DiagnosticsService(config, paths, cloud, auth, session, log);
        var updates = new UpdateService(config, auth, tasks, log);
        var submissions = new SubmissionService(config, auth, paths, local, tasks, log);
        var management = new ManagementService(config, auth, log);
        var cloudHistory = new CloudHistoryService(config, auth, cloud, log);
        return new ServiceRegistry { Config = config, Log = log, Auth = auth, Cloud = cloud, Paths = paths, Local = local, Tasks = tasks, Backups = backups, Install = install, Sync = sync, SyncHistory = syncHistory, Session = session, Diagnostics = diagnostics, Updates = updates, Submissions = submissions, Management = management, CloudHistory = cloudHistory };
    }

    public bool ReconfigureAuth()
    {
        var c = Config.Current;
        var endpoint = !string.IsNullOrWhiteSpace(c.ApiDirectUrl) ? c.ApiDirectUrl : c.ApiBaseUrl;
        var previousBaseUrl = Auth.BaseUrl;
        var previousFingerprint = Auth.PinnedCertSha256;
        Auth.Reconfigure(endpoint, c.ApiDirectCertSha256);
        var changed = !string.Equals(previousBaseUrl, Auth.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(previousFingerprint, Auth.PinnedCertSha256, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            Auth.SetToken("");
            Session.Clear();
        }
        return changed;
    }
}
