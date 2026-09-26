using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;
using System.IO;
using System.IO.Compression;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

var failures = new List<string>();

void Check(bool condition, string name)
{
    if (condition) Console.WriteLine("PASS  " + name);
    else
    {
        Console.WriteLine("FAIL  " + name);
        failures.Add(name);
    }
}

async Task CheckThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
{
    try { await action(); Check(false, name); }
    catch (TException) { Check(true, name); }
}

if (args.Contains("--login-ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var width = args.Length > 1 && double.TryParse(args[^2], out var parsedWidth) ? parsedWidth : 1040;
    var height = args.Length > 0 && double.TryParse(args[^1], out var parsedHeight) ? parsedHeight : 660;
    Exception? previewError = null;
    var uiThread = new Thread(() =>
    {
        var oldConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var oldData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        var isolated = Path.Combine(Path.GetTempPath(), "scfa_login_preview_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(isolated);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", isolated);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(isolated, "data"));
            var application = new SCFA.ContentCenter.App();
            application.InitializeComponent();
            var services = ServiceRegistry.CreateAsync().GetAwaiter().GetResult();
            typeof(SCFA.ContentCenter.App).GetProperty("Services")!.SetValue(null, services);
            var window = new SCFA.ContentCenter.Views.LoginWindow();
            ((System.Windows.Controls.ComboBox)window.FindName("AccountBox")).Text = "player@example.com";
            ((System.Windows.Controls.PasswordBox)window.FindName("PasswordBox")).Password = "Preview-Password-123";
            ((System.Windows.Controls.CheckBox)window.FindName("RememberAccountBox")).IsChecked = true;
            ((System.Windows.Controls.CheckBox)window.FindName("RememberPasswordBox")).IsChecked = false;
            ((System.Windows.Controls.CheckBox)window.FindName("AutoLoginBox")).IsChecked = false;
            var root = (System.Windows.FrameworkElement)window.Content;
            var size = new System.Windows.Size(width, height);
            root.Measure(size);
            root.Arrange(new System.Windows.Rect(size));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-dev33", $"login-{(int)width}x{(int)height}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var stream = File.Create(output)) encoder.Save(stream);
            application.Shutdown();
        }
        catch (Exception ex) { previewError = ex; }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", oldConfig);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", oldData);
            try { if (Directory.Exists(isolated)) Directory.Delete(isolated, true); } catch { }
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (previewError is not null) Console.Error.WriteLine("FAIL  登录页视觉预览异常：" + previewError);
    Environment.Exit(previewError is null ? 0 : 1);
    return;
}

if (args.Contains("--profile-ui-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? profileError = null;
    var uiThread = new Thread(() =>
    {
        var previousConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
        var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
        var isolated = Path.Combine(Path.GetTempPath(), "scfa_profile_smoke_" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", isolated);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(isolated, "data"));
            var application = new SCFA.ContentCenter.App();
            application.InitializeComponent();
            var services = ServiceRegistry.CreateAsync().GetAwaiter().GetResult();
            typeof(SCFA.ContentCenter.App).GetProperty("Services")!.SetValue(null, services);
            var window = new SCFA.ContentCenter.Views.ProfileWindow("offline")
            {
                ShowInTaskbar = false,
                Opacity = 0
            };
            var root = (System.Windows.FrameworkElement)window.Content;
            root.Measure(new System.Windows.Size(560, 570));
            root.Arrange(new System.Windows.Rect(0, 0, 560, 570));
            root.UpdateLayout();
            var preview = new RenderTargetBitmap(560, 570, 96, 96, PixelFormats.Pbgra32);
            preview.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-dev34", "profile-560x570.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var stream = File.Create(output)) encoder.Save(stream);
            window.Show();
            window.UpdateLayout();
            window.Close();
            application.Shutdown();
        }
        catch (Exception ex) { profileError = ex; }
        finally
        {
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
            try { if (Directory.Exists(isolated)) Directory.Delete(isolated, true); } catch { }
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (profileError is null) Console.WriteLine("PASS  个人资料窗口可实际构造和布局");
    else Console.Error.WriteLine("FAIL  个人资料窗口运行异常：" + profileError);
    Environment.Exit(profileError is null ? 0 : 1);
    return;
}

if (args.Contains("--admin-ui-binding-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? bindingError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var application = new SCFA.ContentCenter.App();
            application.InitializeComponent();
            var previews = new (System.Windows.Controls.UserControl View, object Probe)[]
            {
                (new SCFA.ContentCenter.Views.PublicationView(), new PublicationBindingProbe()),
                (new SCFA.ContentCenter.Views.UsersView(), new UsersBindingProbe()),
                (new SCFA.ContentCenter.Views.OperationsView(), new OperationsBindingProbe())
            };
            foreach (var preview in previews)
            {
                preview.View.DataContext = preview.Probe;
                var host = new System.Windows.Window { Content = preview.View, Width = 980, Height = 680, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Opacity = 0 };
                host.Show();
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                host.Close();
            }
            application.Shutdown();
        }
        catch (Exception ex) { bindingError = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (bindingError is null) Console.WriteLine("PASS  发布、用户与审计页面可在真实 WPF 布局中完成绑定");
    else Console.Error.WriteLine("FAIL  管理员页面运行时绑定异常：" + bindingError);
    Environment.Exit(bindingError is null ? 0 : 1);
    return;
}

if (args.Contains("--admin-ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var flagIndex = Array.FindIndex(args, value => value.Equals("--admin-ui-preview", StringComparison.OrdinalIgnoreCase));
    var page = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1].ToLowerInvariant() : "users";
    var width = args.Length > 1 && double.TryParse(args[^2], out var parsedWidth) ? parsedWidth : 1200;
    var height = args.Length > 0 && double.TryParse(args[^1], out var parsedHeight) ? parsedHeight : 800;
    var uiThread = new Thread(() =>
    {
        var application = new SCFA.ContentCenter.App();
        application.InitializeComponent();
        var (view, probe) = page switch
        {
            "publication" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.PublicationView(), (object)new PublicationBindingProbe()),
            "users" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.UsersView(), (object)new UsersBindingProbe()),
            "operations" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.OperationsView(), (object)new OperationsBindingProbe()),
            _ => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.UsersView(), (object)new UsersBindingProbe())
        };
        view.DataContext = probe;
        var root = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)application.Resources["AppBackgroundBrush"],
            Padding = new System.Windows.Thickness(26),
            Child = view
        };
        var host = new System.Windows.Window { Content = root, Width = width, Height = height, MinWidth = 900, MinHeight = 640, WindowStyle = System.Windows.WindowStyle.None };
        application.MainWindow = host;
        host.Show();
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-dev20", $"{page}-{(int)width}x{(int)height}.png");
        using (var stream = File.Create(output)) encoder.Save(stream);
        host.Close();
        application.Shutdown();
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    return;
}

if (args.Contains("--settings-ui-binding-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? bindingError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var application = new SCFA.ContentCenter.App();
            application.InitializeComponent();
            var previews = new (System.Windows.Controls.UserControl View, object Probe)[]
            {
                (new SCFA.ContentCenter.Views.SetupView(), new SetupBindingProbe()),
                (new SCFA.ContentCenter.Views.SettingsView(), new SettingsBindingProbe())
            };
            foreach (var preview in previews)
            {
                preview.View.DataContext = preview.Probe;
                var host = new System.Windows.Window { Content = preview.View, Width = 980, Height = 680, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Opacity = 0 };
                host.Show();
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                host.Close();
            }
            application.Shutdown();
        }
        catch (Exception ex) { bindingError = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (bindingError is null) Console.WriteLine("PASS  首次设置与软件设置页面可在真实 WPF 布局中完成绑定");
    else Console.Error.WriteLine("FAIL  设置页面运行时绑定异常：" + bindingError);
    Environment.Exit(bindingError is null ? 0 : 1);
    return;
}

if (args.Contains("--settings-ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var flagIndex = Array.FindIndex(args, value => value.Equals("--settings-ui-preview", StringComparison.OrdinalIgnoreCase));
    var page = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1].ToLowerInvariant() : "settings";
    var width = args.Length > 1 && double.TryParse(args[^2], out var parsedWidth) ? parsedWidth : 1200;
    var height = args.Length > 0 && double.TryParse(args[^1], out var parsedHeight) ? parsedHeight : 800;
    var uiThread = new Thread(() =>
    {
        var application = new SCFA.ContentCenter.App();
        application.InitializeComponent();
        var (view, probe) = page switch
        {
            "setup" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.SetupView(), (object)new SetupBindingProbe()),
            _ => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.SettingsView(), (object)new SettingsBindingProbe())
        };
        view.DataContext = probe;
        if (view.FindName("SettingsTabs") is System.Windows.Controls.TabControl settingsTabs)
        {
            settingsTabs.SelectedIndex = page switch
            {
                "settings-startup" => 2,
                "settings-storage" => 4,
                _ => 0
            };
        }
        var root = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)application.Resources["AppBackgroundBrush"],
            Padding = new System.Windows.Thickness(26),
            Child = view
        };
        var host = new System.Windows.Window { Content = root, Width = width, Height = height, MinWidth = 900, MinHeight = 640, WindowStyle = System.Windows.WindowStyle.None };
        application.MainWindow = host;
        host.Show();
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-dev21", $"{page}-{(int)width}x{(int)height}.png");
        using (var stream = File.Create(output)) encoder.Save(stream);
        host.Close();
        application.Shutdown();
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    return;
}

LocalContentEntry Local(string id, string folder, string name = "Local Name", bool valid = true) => new()
{
    Kind = "地图",
    Id = id,
    Folder = folder,
    Name = name,
    Root = Path.Combine(@"C:\maps", folder),
    Version = "1",
    Valid = valid
};

CloudContentEntry Cloud(string id, string folder, string name = "Cloud Name", params string[] aliases) => new()
{
    Kind = "地图",
    Id = id,
    FolderName = folder,
    Name = name,
    Version = "2",
    Aliases = aliases.ToList()
};

var nameOnly = ContentIdentity.FindBestResult(
    [Local("local-id", "local-folder", "Same Display Name")],
    Cloud("remote-id", "remote-folder", "Same Display Name"));
Check(nameOnly.Entry is null && !nameOnly.Ambiguous, "显示名称相同不能单独触发自动匹配");

var normalizedId = ContentIdentity.FindBestResult(
    [Local("2f3a_test-map", "unrelated-folder")],
    Cloud("2F3A-TEST.MAP", "remote-folder"));
Check(normalizedId.Entry is not null, "规范化 ID 相同仍可匹配");

var aliasFolder = ContentIdentity.FindBestResult(
    [Local("local-id", "legacy_map_folder")],
    Cloud("remote-id", "new-folder", aliases: ["legacy-map-folder"]));
Check(aliasFolder.Entry is not null, "云端文件夹别名仍可匹配旧目录");

var labeled = ContentIdentity.CompareVersions("alpha1", "beta1");
Check(!ContentIdentity.VersionsEquivalent("alpha1", "beta1") && !labeled.Ordered, "不同标签版本不能因数字相同而等价");
Check(ContentIdentity.VersionsEquivalent("v1.0.0", "1"), "常规版本前缀和尾零保持兼容");
var numeric = ContentIdentity.CompareVersions("1.9", "1.10");
Check(numeric.Ordered && numeric.Compare < 0, "数字版本按分段数值排序");
Check(!ContentIdentity.CompareVersions("3", "v2026.08.15").Ordered,
    "带 v 前缀的日期版本不能与普通数字版本误判大小后自动覆盖");

var futureMetadata = JsonSerializer.Deserialize<CloudContentEntry>("""
{
  "id":"future-map",
  "name":"Future Map",
  "version":"1",
  "author":"Mapper",
  "description":"Description",
  "category":"竞技",
  "tags":["2v2","平衡"],
  "aliases":["old-folder"],
  "published_at":"2026-08-20T12:00:00+08:00"
}
""")!;
Check(futureMetadata.AuthorText == "Mapper" && futureMetadata.CategoryText == "竞技" && futureMetadata.TagsText.Contains("2v2", StringComparison.Ordinal), "兼容未来作者、说明、分类和标签字段");
Check(futureMetadata.PublishedAtValue != DateTimeOffset.MinValue, "发布时间可用于稳定排序");
var splitVersionMetadata = JsonSerializer.Deserialize<CloudContentEntry>("""{"version":"13","game_version":"3"}""")!;
Check(splitVersionMetadata.Version == "13" && splitVersionMetadata.EffectiveGameVersion == "3" &&
      splitVersionMetadata.VersionDisplay.Contains("游戏版本 3"),
    "云端清单可分别读取发布标签与游戏内部版本");
var selectionNotified = false;
futureMetadata.PropertyChanged += (_, e) => selectionNotified |= e.PropertyName == nameof(CloudContentEntry.IsSelected);
futureMetadata.IsSelected = true;
Check(selectionNotified, "批量勾选状态可即时通知界面");
var favoriteNotified = false;
futureMetadata.PropertyChanged += (_, e) => favoriteNotified |= e.PropertyName == nameof(CloudContentEntry.IsFavorite);
futureMetadata.IsFavorite = true;
Check(favoriteNotified, "收藏状态可即时通知界面");
Check(InstallService.ContentKey("地图", " test-map ") == "地图:test-map", "收藏与最近安装使用稳定内容键");
var protectedLoginPassword = LoginCredentialProtector.Protect("Regression-Password-123!");
Check(LoginCredentialProtector.TryUnprotect(protectedLoginPassword, out var unprotectedLoginPassword) && unprotectedLoginPassword == "Regression-Password-123!", "记住密码使用 Windows 当前用户加密并可安全恢复");
Check(!LoginCredentialProtector.TryUnprotect("not-valid-base64", out _), "损坏或伪造的已保存密码不会被使用");
var userManager = new UserInfo { RoleKey = "manager", Permissions = ["users.manage"] };
var misleadingRole = new UserInfo { RoleKey = "not_admin", Permissions = [] };
Check(AccessPolicy.CanReadUsers(userManager) && AccessPolicy.CanManageUsers(userManager) && AccessPolicy.CanRevokeSessions(userManager),
    "服务器用户管理权限可读取、修改和撤销会话");
Check(!AccessPolicy.CanReadAudit(misleadingRole) && !AccessPolicy.CanUnpublish(misleadingRole),
    "非管理员角色名不能因为含有 admin 字样获得管理操作入口");
Check(AccessPolicy.CanOpenAdminWorkspace(userManager) && !AccessPolicy.CanManageSettings(userManager),
    "用户管理员可进入管理工作区但不能调整服务器连接设置");
var settingsManager = new UserInfo { RoleKey = "configurator", Permissions = ["settings.cloud"] };
Check(AccessPolicy.CanManageSettings(settingsManager) && !AccessPolicy.CanOpenAdminWorkspace(settingsManager),
    "服务器设置权限只开放高级设置，不扩大到其他管理页面");
Check(!AccessPolicy.CanOpenAdminWorkspace(misleadingRole) && !AccessPolicy.CanManageSettings(misleadingRole),
    "伪装管理员角色名不会显示管理导航或高级设置");
Check(AccessPolicy.CanPublishContent(new UserInfo { RoleKey = "admin" }) &&
      AccessPolicy.CanPublishContent(new UserInfo { RoleKey = "super_admin" }) &&
      !AccessPolicy.CanPublishContent(new UserInfo { RoleKey = "publisher", Permissions = ["content.publish"] }) &&
      !AccessPolicy.CanPublishContent(new UserInfo { RoleKey = "user" }),
      "发布材料仅允许明确的管理员角色准备");
var auditEnvelope = JsonSerializer.Deserialize<AuditFetchResult>("""
{"records":[{"id":"audit-1","time":"2026-09-25T01:00:00Z","actor_name":"admin","action":"user.update","target_name":"player-one","result":"success","detail":"updated","remote_ip":"127.0.0.1"}],"integrity_ok":true,"integrity_message":"ok","total":1}
""")!;
Check(auditEnvelope.Records.Count == 1 && auditEnvelope.IntegrityOk == true && auditEnvelope.Total == 1,
    "生产审计 records 包装结构与完整性字段可读取");
Check(auditEnvelope.Records[0].EffectiveActor == "admin" && auditEnvelope.Records[0].EffectiveTarget == "player-one" &&
      auditEnvelope.Records[0].EffectiveIp == "127.0.0.1", "生产审计操作者、目标和来源 IP 字段可显示");

var damaged = Local("damaged-map", "damaged-map", valid: false);
damaged.Detail = "缺少 scenario.lua";
var damagedDecision = SyncService.Decide(damaged, Cloud("damaged-map", "damaged-map"));
Check(damagedDecision.ShouldInstall && damagedDecision.Reason.Contains("本地内容损坏", StringComparison.Ordinal), "损坏内容会进入自动修复流程");
var newer = Local("newer-map", "newer-map");
newer.Version = "3";
var newerDecision = SyncService.Decide(newer, Cloud("newer-map", "newer-map"));
Check(!newerDecision.ShouldInstall && newerDecision.Reason.Contains("保护不降级", StringComparison.Ordinal), "本地较新版本仍受降级保护");
var strictWithoutManifestHash = Local("strict-map", "strict-map");
var strictRemote = Cloud("strict-map", "strict-map");
strictRemote.Version = strictWithoutManifestHash.Version;
var strictDecision = SyncService.Decide(strictWithoutManifestHash, strictRemote);
Check(!strictDecision.ShouldInstall && strictDecision.Reason.Contains("避免重复覆盖", StringComparison.Ordinal), "同版本且清单缺少内容指纹时不自动重复覆盖");
var releaseLabeled = Cloud("release-map", "release-map");
releaseLabeled.Version = "13";
releaseLabeled.GameVersion = "3";
releaseLabeled.ContentSha256 = new string('a', 64);
var releaseLocal = Local("release-map", "release-map");
releaseLocal.Version = "3";
var releaseDecision = SyncService.Decide(releaseLocal, releaseLabeled);
Check(releaseLabeled.EffectiveGameVersion == "3" && releaseLabeled.VersionDisplay.Contains("游戏版本 3") &&
      releaseDecision.VerifyContentHash && !releaseDecision.ShouldInstall,
    "发布标签与游戏内部版本分开时按真实版本决定同步并继续校验内容");

Check(SafeArchive.ValidateRelativePath("map/file.scmap").EndsWith(Path.Combine("map", "file.scmap"), StringComparison.Ordinal), "安全相对路径通过");
CheckThrows(() => SafeArchive.ValidateRelativePath("../outside.txt"), "拒绝目录穿越");
CheckThrows(() => SafeArchive.ValidateRelativePath("map/CON.txt"), "拒绝 Windows 设备名");
CheckThrows(() => SafeArchive.ValidateRelativePath("map/file. "), "拒绝危险尾部字符");
CheckArgumentThrows(() => GamePathService.ValidateContentDirectoryPair(@"C:\SCFA\Maps", @"C:\SCFA\Maps\Mods"), "拒绝地图与 MOD 目录互相嵌套");

string? FindSourceRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SCFA.ContentCenter");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
        }
    }

    return null;
}

var uiRoot = FindSourceRoot();
if (uiRoot is null)
{
    Console.Error.WriteLine("找不到项目源码。请从包含 src/SCFA.ContentCenter 的项目目录运行回归测试，或使用项目中的 run-regression-tests.ps1。");
    Environment.ExitCode = 2;
    return;
}
var xamlFiles = Directory.GetFiles(uiRoot, "*.xaml", SearchOption.AllDirectories);
var parsedXaml = new List<XDocument>();
try { parsedXaml.AddRange(xamlFiles.Select(XDocument.Load)); Check(xamlFiles.Length >= 18, "全部 WPF 页面与全局资源均为有效 XML"); }
catch { Check(false, "全部 WPF 页面与全局资源均为有效 XML"); }
var appXaml = await File.ReadAllTextAsync(Path.Combine(uiRoot, "App.xaml"));
Check(new[] { "PageTitle", "SectionTitle", "CardBorder", "PrimaryButton", "SecondaryButton", "NavRadioButton" }.All(appXaml.Contains), "全局设计系统包含页面、卡片、按钮和导航样式");
Check(new[] { "ToolbarBorder", "InsetBorder", "PillBorder", "FieldLabel", "MetricValue", "CompactButton" }.All(appXaml.Contains), "内容管理页面具备统一工具栏、统计卡片和紧凑操作样式");
var mainWindowXaml = await File.ReadAllTextAsync(Path.Combine(uiRoot, "MainWindow.xaml"));
Check(mainWindowXaml.Contains("WindowChrome", StringComparison.Ordinal) && mainWindowXaml.Contains("NavRadioButton", StringComparison.Ordinal) && appXaml.Contains("PrimaryNavigation", StringComparison.Ordinal), "主窗口使用自绘窗口框架和统一导航选中态");
var referencedIcons = System.Text.RegularExpressions.Regex.Matches(mainWindowXaml, "ImageSource=\"/Assets/Fluent/(?<name>[^\"]+)\"")
    .Select(match => Path.Combine(uiRoot, "Assets", "Fluent", match.Groups["name"].Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
Check(referencedIcons.Length >= 10 && referencedIcons.All(File.Exists), "主导航引用的 Fluent 图标资源全部存在");
Check(mainWindowXaml.Contains("SyncBadgeVisibility", StringComparison.Ordinal) && mainWindowXaml.Contains("DownloadBadgeVisibility", StringComparison.Ordinal) && mainWindowXaml.Contains("DiagnosticBadgeVisibility", StringComparison.Ordinal), "方案 A 左导航持续显示同步、下载与诊断状态");
Check(!mainWindowXaml.Contains("x:Name=\"SetupButton\"", StringComparison.Ordinal) &&
      mainWindowXaml.Contains("Content=\"退出登录\"", StringComparison.Ordinal),
      "首次设置不再占用常驻导航且账号区提供明确退出按钮");
Check(!mainWindowXaml.Contains("DrawingBrush", StringComparison.Ordinal) && mainWindowXaml.Contains("C 260,500", StringComparison.Ordinal), "主内容区不显示网格并保留行星弧线与等高线背景");
Check(mainWindowXaml.Contains("LinearGradientBrush", StringComparison.Ordinal) && mainWindowXaml.Contains("StrokeDashArray", StringComparison.Ordinal) && mainWindowXaml.Contains("RotateTransform", StringComparison.Ordinal), "主内容区背景包含斜向光带、分段航迹和多层空间结构");
Check(mainWindowXaml.Contains("command_deck_background.png", StringComparison.Ordinal) && File.Exists(Path.Combine(uiRoot, "Assets", "command_deck_background.png")), "主内容区使用确认的图片背景资源");
Check(mainWindowXaml.Contains("Opacity=\"0.72\"", StringComparison.Ordinal) && mainWindowXaml.Contains("#4D07111C", StringComparison.Ordinal) &&
      appXaml.Contains("#B80E1B2B", StringComparison.Ordinal) && appXaml.Contains("#C213243A", StringComparison.Ordinal),
      "背景图片和主要卡片采用兼顾可读性的半透明层级");
var loginXaml = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Views", "LoginWindow.xaml"));
Check(loginXaml.Contains("RememberAccountBox", StringComparison.Ordinal) && loginXaml.Contains("RememberPasswordBox", StringComparison.Ordinal) && loginXaml.Contains("AutoLoginBox", StringComparison.Ordinal) && loginXaml.Contains("IsDefault=\"True\"", StringComparison.Ordinal), "登录页提供记住账号、记住密码、自动登录和回车提交");
Check(loginXaml.Contains("ComboBox x:Name=\"AccountBox\"", StringComparison.Ordinal) && loginXaml.Contains("TabIndex=\"0\"", StringComparison.Ordinal) && loginXaml.Contains("TabIndex=\"8\"", StringComparison.Ordinal) && loginXaml.Contains("PreviewKeyDown", StringComparison.Ordinal), "登录页支持多个账号下拉切换和完整键盘导航");
var syncHistorySource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Services", "SyncHistoryService.cs"));
var syncHistoryModel = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Models", "SyncHistoryModels.cs"));
var syncPageSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "ViewModels", "SyncPageViewModel.cs"));
var syncViewSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Views", "SyncView.xaml"));
Check(syncHistorySource.Contains("sync-history.json", StringComparison.Ordinal) && syncHistorySource.Contains("MaxRecords", StringComparison.Ordinal) && syncHistorySource.Contains("File.Move(temp", StringComparison.Ordinal), "同步历史使用本地原子文件持久化并限制记录数量");
Check(syncHistoryModel.Contains("SyncRunRecord", StringComparison.Ordinal) && syncPageSource.Contains("LoadHistory", StringComparison.Ordinal) && syncPageSource.Contains("SaveHistoryAsync", StringComparison.Ordinal), "同步中心可恢复最近一次记录并保存完成、取消、失败结果");
Check(syncHistorySource.Contains("ClearAsync", StringComparison.Ordinal) &&
      syncPageSource.Contains("SyncResultEntry", StringComparison.Ordinal) &&
      syncViewSource.Contains("ClearCurrentCommand", StringComparison.Ordinal) &&
      syncViewSource.Contains("ClearHistoryCommand", StringComparison.Ordinal) &&
      syncViewSource.Contains("TimeText", StringComparison.Ordinal),
      "同步执行记录显示时间并支持分别清除本次记录和持久历史");
var readOnlyEditorBindings = xamlFiles.SelectMany(path => System.Text.RegularExpressions.Regex.Matches(
        File.ReadAllText(path),
        "<TextBox[^>]*Text=\"\\{Binding (?<binding>[^\"]+)\"[^>]*IsReadOnly=\"True\"|<TextBox[^>]*IsReadOnly=\"True\"[^>]*Text=\"\\{Binding (?<binding>[^\"]+)\"")
    .Select(match => match.Groups["binding"].Value))
    .ToArray();
Check(readOnlyEditorBindings.Length >= 1 && readOnlyEditorBindings.All(binding => binding.Contains("Mode=OneWay", StringComparison.Ordinal)), "只读文本框不会向只读视图模型属性回写");
var dev18Pages = new[] { "CloudContentView.xaml", "LocalContentView.xaml", "SyncView.xaml", "DownloadsView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev18Pages[0].Contains("InstallStateCode", StringComparison.Ordinal) && dev18Pages[0].Contains("AttentionCount", StringComparison.Ordinal) &&
      dev18Pages[1].Contains("SelectedState", StringComparison.Ordinal) && dev18Pages[1].Contains("IssueCount", StringComparison.Ordinal) &&
      dev18Pages[2].Contains("RunScope", StringComparison.Ordinal) && dev18Pages[2].Contains("ResultCount", StringComparison.Ordinal) &&
      dev18Pages[3].Contains("RunningCount", StringComparison.Ordinal) && dev18Pages[3].Contains("TaskState", StringComparison.Ordinal),
      "云端、本地、同步和下载页面均使用真实状态与统计绑定");
Check(dev18Pages[0].Contains("PreviewSource", StringComparison.Ordinal) && dev18Pages[0].Contains("InstallPath", StringComparison.Ordinal) && dev18Pages[0].Contains("TagsText", StringComparison.Ordinal), "云端地图与 MOD 详情显示真实预览、安装路径和标签");
Check(appXaml.Contains("<Setter Property=\"IsReadOnly\" Value=\"True\"/>", StringComparison.Ordinal) &&
      dev18Pages[0].Contains("<DataGridTemplateColumn Header=\"选择\"", StringComparison.Ordinal) &&
      dev18Pages[0].Contains("IsSelected, Mode=TwoWay", StringComparison.Ordinal),
      "展示表格统一只读且云端批量勾选仍可操作");
var commandSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Commands", "RelayCommand.cs"));
var cloudPageSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "ViewModels", "CloudPageViewModel.cs"));
Check(commandSource.Contains("CommandManager.RequerySuggested", StringComparison.Ordinal) &&
      commandSource.Contains("CommandManager.InvalidateRequerySuggested()", StringComparison.Ordinal),
      "所有 WPF 命令会在表格选择和焦点变化后统一重新计算可用状态");
Check(dev18Pages[0].Contains("SelectedItem, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged", StringComparison.Ordinal) &&
      dev18Pages[1].Contains("SelectedItem, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged", StringComparison.Ordinal),
      "云端与本地表格会立即把当前选择同步到操作命令");
Check(cloudPageSource.Contains("sender is CloudContentEntry { IsSelected: true } selected", StringComparison.Ordinal) &&
      cloudPageSource.Contains("SelectedCount > 0 && CanStart()", StringComparison.Ordinal),
      "云端勾选会同步当前项且批量处理仅在实际勾选后启用");
var mainViewModelSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "ViewModels", "MainViewModel.cs"));
var dashboardViewModelSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "ViewModels", "DashboardPageViewModel.cs"));
var dashboardViewSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Views", "DashboardView.xaml"));
var syncServiceSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Services", "SyncService.cs"));
var installServiceSource = await File.ReadAllTextAsync(Path.Combine(uiRoot, "Services", "InstallService.cs"));
Check(mainViewModelSource.Contains("new DashboardPageViewModel(_syncPage)", StringComparison.Ordinal) &&
      dashboardViewModelSource.Contains("_syncPage.RunAllAsync()", StringComparison.Ordinal),
      "总览与同步中心共用同一同步任务状态");
Check(mainViewModelSource.Contains("Lazy<DashboardPageViewModel>", StringComparison.Ordinal) &&
      dashboardViewModelSource.Contains("Task.WhenAll(mapsTask, modsTask)", StringComparison.Ordinal),
      "页面切换复用已加载页面且总览并行扫描地图与 MOD");
Check(dashboardViewModelSource.Contains("RecentActivities", StringComparison.Ordinal) &&
      dashboardViewModelSource.Contains("HealthItems", StringComparison.Ordinal) &&
      dashboardViewModelSource.Contains("App.Services.Tasks.Tasks", StringComparison.Ordinal) &&
      dashboardViewModelSource.Contains("GetContentDirectory", StringComparison.Ordinal),
      "总览新增最近活动和系统健康真实数据源");
Check(dashboardViewSource.Contains("最近活动", StringComparison.Ordinal) &&
      dashboardViewSource.Contains("系统健康", StringComparison.Ordinal) &&
      dashboardViewSource.Contains("ItemsSource=\"{Binding RecentActivities}\"", StringComparison.Ordinal) &&
      dashboardViewSource.Contains("ItemsSource=\"{Binding HealthItems}\"", StringComparison.Ordinal),
      "总览界面包含最近活动和系统健康展示区域");
Check(syncServiceSource.Contains("_syncGate.WaitAsync(0, ct)", StringComparison.Ordinal) &&
      installServiceSource.Contains("_operationGate.WaitAsync(ct)", StringComparison.Ordinal),
      "同步、安装与卸载具备进程内并发保护");
Check(new[] { "WorkflowStepBorder", "StatusBadgeBorder", "PreviewFrame" }.All(appXaml.Contains),
      "维护工作区具备统一流程、状态和预览组件");
var dev19Pages = new[] { "BackupsView.xaml", "CloudHistoryView.xaml", "DiagnosticsView.xaml", "UpdatesView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev19Pages[0].Contains("SelectedIntegrity", StringComparison.Ordinal) && dev19Pages[0].Contains("TotalSizeText", StringComparison.Ordinal) &&
      dev19Pages[1].Contains("SelectedVersionLabel", StringComparison.Ordinal) && dev19Pages[1].Contains("VersionCount", StringComparison.Ordinal) &&
      dev19Pages[2].Contains("HealthLabel", StringComparison.Ordinal) && dev19Pages[2].Contains("ProblemCount", StringComparison.Ordinal) &&
      dev19Pages[3].Contains("SecurityStateLabel", StringComparison.Ordinal) && dev19Pages[3].Contains("StageStateLabel", StringComparison.Ordinal),
      "备份、云历史、诊断和更新页面均使用真实状态与统计绑定");
Check(dev19Pages[1].Contains("SearchText", StringComparison.Ordinal) && dev19Pages[1].Contains("FilteredContentCount", StringComparison.Ordinal), "云端历史提供实时搜索、清除与结果计数");
Check(dev19Pages[1].Contains("ContentCount, Mode=OneWay", StringComparison.Ordinal) && dev19Pages[1].Contains("FilteredContentCount, Mode=OneWay", StringComparison.Ordinal) && dev19Pages[1].Contains("VersionCount, Mode=OneWay", StringComparison.Ordinal), "云端历史只读统计使用单向绑定");
var dev20Pages = new[] { "UsersView.xaml", "OperationsView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev20Pages[0].Contains("RestrictedCount", StringComparison.Ordinal) && dev20Pages[0].Contains("SelectedPermissionSummary", StringComparison.Ordinal) &&
      dev20Pages[1].Contains("AuditView", StringComparison.Ordinal) && dev20Pages[1].Contains("SelectedAuditDetail", StringComparison.Ordinal),
      "用户和审计页面均使用真实身份与审计状态绑定");
var publicationViewSource = File.ReadAllText(Path.Combine(uiRoot, "Views", "PublicationView.xaml"));
Check(mainWindowXaml.Contains("PublicationVisibility", StringComparison.Ordinal) &&
      publicationViewSource.Contains("不会上传到 COS", StringComparison.Ordinal) &&
      publicationViewSource.Contains("PrepareCommand", StringComparison.Ordinal),
      "管理员发布入口与待上传状态有明确界面提示");
var dev21Pages = new[] { "SetupView.xaml", "SettingsView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev21Pages[0].Contains("SetupProgress", StringComparison.Ordinal) && dev21Pages[0].Contains("ContentStateLabel", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("ConfigurationScore", StringComparison.Ordinal) && dev21Pages[1].Contains("ValidationSummary", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("DraftStateLabel", StringComparison.Ordinal) && dev21Pages[1].Contains("Header=\"服务器路径\"", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("AdvancedSettingsVisibility", StringComparison.Ordinal) && dev21Pages[1].Contains("SettingsAudienceHint", StringComparison.Ordinal),
      "首次设置与软件设置页面使用真实进度、草稿、健康状态和分组配置绑定");
Check(dev21Pages[1].Contains("Header=\"启动与更新\"", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("Header=\"存储与数据\"", StringComparison.Ordinal) &&
       dev19Pages[1].Contains("刷新内容列表", StringComparison.Ordinal) &&
       dev19Pages[1].Contains("查询历史版本", StringComparison.Ordinal),
       "客户界面使用清晰的设置和云端历史操作文案");

var previousConfigDirectory = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
var previousDataDirectory = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
var configDirectory = Path.Combine(Path.GetTempPath(), "scfa_config_regression_" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(configDirectory);
    Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", configDirectory);
    Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(configDirectory, "data"));
    await File.WriteAllTextAsync(Path.Combine(configDirectory, "config.json"), "{ invalid json");
    var config = new ConfigService();
    await config.LoadAsync();
    Check(!string.IsNullOrWhiteSpace(config.LoadWarning), "损坏配置会向用户报告警告");
    Check(Directory.GetFiles(configDirectory, "config.json.invalid_*").Length == 1, "损坏配置会在覆盖前备份");
    var defaults = AppConfig.Defaults();
    Check(defaults.RememberLoginAccount && !defaults.RememberLoginPassword && !defaults.AutoLogin, "登录默认记住账号但不默认保存密码或自动登录");
    var configDraft = ConfigService.Clone(config.Current);
    var originalBucket = config.Current.Bucket;
    configDraft.Bucket = "draft-only-bucket-123";
    Check(config.Current.Bucket == originalBucket, "设置草稿修改不会提前污染运行配置");
    configDraft.Bucket = originalBucket;
    configDraft.AutoLayout = !config.Current.AutoLayout;
    using (var futureValue = JsonDocument.Parse("{\"enabled\":true}"))
    {
        configDraft.ExtensionData = new Dictionary<string, JsonElement> { ["future_setting"] = futureValue.RootElement.Clone() };
    }
    await config.SaveAsync(configDraft);
    Check(config.Current.AutoLayout == configDraft.AutoLayout && !ReferenceEquals(config.Current, configDraft), "候选设置成功落盘后才替换运行配置快照");
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => config.SaveAsync()));
    using var savedConfig = JsonDocument.Parse(await File.ReadAllTextAsync(config.ConfigPath));
    Check(savedConfig.RootElement.ValueKind == JsonValueKind.Object, "并发保存仍生成有效配置 JSON");
    config.Current.FavoriteContentKeys = ["地图:favorite-map", "地图:favorite-map", ""];
    config.Current.RecentContentKeys = ["MOD:recent-mod", "MOD:recent-mod", ""];
    config.Current.RememberLoginAccount = true;
    config.Current.RememberLoginPassword = true;
    config.Current.LoginPasswordEncrypted = protectedLoginPassword;
    config.Current.AutoLogin = true;
    config.Current.LoginAccounts = [new LoginAccountRecord { Account = "player@example.com", PasswordEncrypted = protectedLoginPassword }];
    config.Current.LocalUserProfiles = [new LocalUserProfile { UserKey = "offline", DisplayName = "本机玩家", QQ = "12345678", Phone = "13800138000" }];
    await config.SaveAsync();
    var reloadedConfig = new ConfigService();
    await reloadedConfig.LoadAsync();
    Check(reloadedConfig.Current.FavoriteContentKeys.SequenceEqual(["地图:favorite-map"]), "收藏列表去重后可持久保存");
    Check(reloadedConfig.Current.RecentContentKeys.SequenceEqual(["MOD:recent-mod"]), "最近安装列表去重后可持久保存");
    Check(reloadedConfig.Current.ExtensionData?.ContainsKey("future_setting") == true, "事务式保存继续保留未来版本未知配置字段");
    Check(reloadedConfig.Current.RememberLoginAccount && reloadedConfig.Current.RememberLoginPassword && reloadedConfig.Current.AutoLogin && reloadedConfig.Current.LoginPasswordEncrypted == protectedLoginPassword && reloadedConfig.Current.LoginAccounts.Count == 1, "记住账号、密码、自动登录和多账号记录可持久保存");
    Check(reloadedConfig.Current.LocalUserProfiles.Count == 1 && reloadedConfig.Current.LocalUserProfiles[0].DisplayName == "本机玩家" && reloadedConfig.Current.LocalUserProfiles[0].QQ == "12345678", "本机个人资料可持久保存");
    var syncHistoryDirectory = Path.Combine(configDirectory, "sync-history");
    var history = new SyncHistoryService(syncHistoryDirectory);
    await history.AppendAsync(new SyncRunRecord { Scope = "地图专项同步", Status = "完成", Installed = 2, Messages = ["地图 A：已安装"] });
    var reloadedHistory = new SyncHistoryService(syncHistoryDirectory).Load();
    Check(reloadedHistory.Count == 1 && reloadedHistory[0].Scope == "地图专项同步" && reloadedHistory[0].Messages.SequenceEqual(["地图 A：已安装"]), "同步历史完成记录可跨实例保存并恢复");
    await history.ClearAsync();
    Check(new SyncHistoryService(syncHistoryDirectory).Load().Count == 0, "同步历史可清除且不会留下不可解析数据");

    var fakeGameRoot = Path.Combine(configDirectory, "game");
    Directory.CreateDirectory(Path.Combine(fakeGameRoot, "Maps"));
    config.Current.GameRoot = fakeGameRoot;
    config.Current.MapsDir = "";
    var pathService = new GamePathService(config);
    Check(string.Equals(pathService.GetContentDirectory("地图"), Path.Combine(fakeGameRoot, "Maps"), StringComparison.OrdinalIgnoreCase), "未配置玩家目录时只采用游戏目录中已经存在的 Maps");

    var settingsGameRoot = Path.Combine(configDirectory, "settings-game");
    Directory.CreateDirectory(settingsGameRoot);
    await File.WriteAllBytesAsync(Path.Combine(settingsGameRoot, "ForgedAlliance.exe"), [0x4D, 0x5A]);
    var settingsMaps = Path.Combine(configDirectory, "settings-player", "Maps");
    var settingsMods = Path.Combine(configDirectory, "settings-player", "Mods");
    var settingsDraft = ConfigService.Clone(config.Current);
    settingsDraft.GameRoot = settingsGameRoot;
    settingsDraft.MapsDir = settingsMaps;
    settingsDraft.ModsDir = settingsMods;
    settingsDraft.Root = "/scfa/";
    settingsDraft.UpdateChannel = "dev";
    settingsDraft.AdminUsersPath = "/v1/admin/users/";
    CheckDirectoryThrows(() => SettingsValidator.ValidateDirectories(settingsDraft, pathService, createDirectories: false), "设置拒绝不存在的玩家内容根目录且不会自动创建");
    Check(!Directory.Exists(settingsMaps) && !Directory.Exists(settingsMods), "目录验证失败后仍不会创建目录或写盘");
    Directory.CreateDirectory(settingsMaps);
    Directory.CreateDirectory(settingsMods);
    var checkedDirectories = SettingsValidator.ValidateDirectories(settingsDraft, pathService, createDirectories: false);
    Check(checkedDirectories.GameRoot == settingsGameRoot, "设置接受用户选择的已有 Maps/Mods 目录");
    var normalizedSettings = SettingsValidator.ValidateAndNormalize(settingsDraft, pathService, createDirectories: true);
    Check(Directory.Exists(settingsMaps) && Directory.Exists(settingsMods), "保存设置只使用已有玩家目录，不另建默认路径");
    Check(normalizedSettings.Root == "scfa" && normalizedSettings.UpdateChannel == "developer" && normalizedSettings.AdminUsersPath == "/v1/admin/users", "设置保存前统一规范化 COS、更新通道和服务器路径");
    Check(settingsDraft.Root == "/scfa/" && settingsDraft.UpdateChannel == "dev", "设置校验不会反向修改编辑草稿");
    var nestedSettings = ConfigService.Clone(settingsDraft);
    nestedSettings.ModsDir = Path.Combine(settingsMaps, "NestedMods");
    CheckArgumentThrows(() => SettingsValidator.ValidateDirectories(nestedSettings, pathService, createDirectories: false), "设置拒绝互相嵌套的 Maps/Mods 目录");
    var invalidCosSettings = ConfigService.Clone(settingsDraft);
    invalidCosSettings.Bucket = "INVALID_BUCKET";
    CheckArgumentThrows(() => SettingsValidator.ValidateCosSettings(invalidCosSettings), "设置拒绝不安全的 COS Bucket");
    CheckArgumentThrows(() => SettingsValidator.NormalizeApiPath("https://outside.example/admin/users", "用户 API"), "设置拒绝跨主机服务器功能路径");
    CheckArgumentThrows(() => SettingsValidator.NormalizeApiPath("/v1/admin/users?redirect=outside", "用户 API"), "设置拒绝服务器功能路径携带查询或跳转参数");
    var renamedGameRoot = Path.Combine(configDirectory, "renamed-game");
    var renamedGameBin = Path.Combine(renamedGameRoot, "bin");
    Directory.CreateDirectory(renamedGameBin);
    await File.WriteAllBytesAsync(Path.Combine(renamedGameBin, "game.dat"), [0x01]);
    await File.WriteAllBytesAsync(Path.Combine(renamedGameBin, "GDFBinary.dll"), [0x01]);
    await File.WriteAllBytesAsync(Path.Combine(renamedGameBin, "MohoEngine.dll"), [0x01]);
    var renamedExecutable = new byte[128];
    renamedExecutable[0] = (byte)'M';
    renamedExecutable[1] = (byte)'Z';
    renamedExecutable[0x3C] = 0x40;
    renamedExecutable[0x40] = (byte)'P';
    renamedExecutable[0x41] = (byte)'E';
    await File.WriteAllBytesAsync(Path.Combine(renamedGameBin, "BsSndRpt.exe"), renamedExecutable);
    await File.WriteAllBytesAsync(Path.Combine(renamedGameBin, "game.exe"), renamedExecutable);
    var renamedExecutableSettings = ConfigService.Clone(settingsDraft);
    renamedExecutableSettings.GameRoot = renamedGameRoot;
    Check(SettingsValidator.ValidateDirectories(renamedExecutableSettings, pathService, createDirectories: false).GameRoot == renamedGameRoot,
        "设置接受 bin 下具备 SCFA 安装特征的改名游戏主程序");
    Check(GamePathService.TryFindScfaExecutable(renamedGameRoot, out var detectedRenamedExecutable) &&
          string.Equals(detectedRenamedExecutable, Path.Combine(renamedGameBin, "game.exe"), StringComparison.OrdinalIgnoreCase),
        "自动检测可定位 bin 下的改名 PE 主程序");
    var missingExecutableSettings = ConfigService.Clone(settingsDraft);
    missingExecutableSettings.GameRoot = Path.Combine(configDirectory, "empty-game");
    Directory.CreateDirectory(missingExecutableSettings.GameRoot);
    CheckThrows(() => SettingsValidator.ValidateDirectories(missingExecutableSettings, pathService, createDirectories: false), "设置拒绝不含游戏可执行文件的安装目录");

    var payload = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
    var handler = new ResumeDownloadHandler(payload, 96 * 1024);
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var cloud = new CloudCatalogService(config, client);
    var download = Path.Combine(configDirectory, "resumed-package.zip");
    await cloud.DownloadAsync("packages/test.zip", download, payload.Length);
    Check(handler.SawRangeRequest && handler.Requests == 2, "下载中断后使用 ETag 和 Range 自动续传");
    Check((await File.ReadAllBytesAsync(download)).SequenceEqual(payload), "续传后的文件内容完整一致");

    var mapsRoot = Path.Combine(configDirectory, "player", "Maps");
    Directory.CreateDirectory(mapsRoot);
    config.Current.MapsDir = mapsRoot;
    var previewRoot = Path.Combine(configDirectory, "preview_fixture");
    Directory.CreateDirectory(previewRoot);
    var embeddedPreviewPath = Path.Combine(previewRoot, "preview_fixture.scmap");
    await File.WriteAllBytesAsync(embeddedPreviewPath, CreatePreviewMapBytes());
    var gamePreview = await Task.Run(() => MapPreviewService.TryLoad(previewRoot));
    var previewPixels = new byte[16];
    gamePreview?.CopyPixels(previewPixels, 8, 0);
    Check(gamePreview is { PixelWidth: 2, PixelHeight: 2, IsFrozen: true } &&
          previewPixels.Take(4).SequenceEqual(new byte[] { 0, 0, 255, 255 }),
        "可从 .scmap 内嵌 DDS 读取与游戏地图选择界面一致的预览，并可跨线程显示");
    var localPreviewEntry = new LocalContentEntry();
    var localPreviewChanges = 0;
    localPreviewEntry.PropertyChanged += (_, args) =>
    {
        if (args.PropertyName == nameof(LocalContentEntry.Preview)) localPreviewChanges++;
    };
    localPreviewEntry.Preview = gamePreview;
    localPreviewEntry.Preview = gamePreview;
    Check(localPreviewChanges == 1, "选中地图后补读预览时会通知本地列表更新缩略图");
    var truncatedPreview = (await File.ReadAllBytesAsync(embeddedPreviewPath))[..100];
    await File.WriteAllBytesAsync(embeddedPreviewPath, truncatedPreview);
    Check(MapPreviewService.TryLoad(previewRoot) is null, "不完整的地图预览不会令目录扫描或页面崩溃");
    var namedPreviewPath = Path.Combine(previewRoot, "preview.png");
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(gamePreview!));
    using (var output = File.Create(namedPreviewPath)) encoder.Save(output);
    Check(MapPreviewService.TryLoad(previewRoot) is { PixelWidth: > 0, PixelHeight: > 0 },
        "不支持的 .scmap 预览可以回退到地图文件夹中的 PNG");
    await File.WriteAllBytesAsync(namedPreviewPath, [1, 2, 3, 4]);
    Check(MapPreviewService.TryLoad(previewRoot) is null, "损坏的 PNG 预览不会阻止地图列表加载");
    var displayMap = new CloudContentEntry { Id = "preview_fixture", Name = "云端目录标题", FolderName = "preview_fixture", Kind = "地图", GameName = "游戏大厅名称" };
    Check(displayMap.DisplayName == "游戏大厅名称" && displayMap.NameContextText.Contains("云端目录标题") &&
          displayMap.NameContextText.Contains("preview_fixture"),
        "云端地图优先显示游戏名称，同时保留云端标题和目录供辨认");
    var packagePayload = CreateMapPackage("recent_map");
    using var packageClient = new HttpClient(new StaticPackageHandler(packagePayload)) { Timeout = Timeout.InfiniteTimeSpan };
    var packageCloud = new CloudCatalogService(config, packageClient);
    var log = new LogService();
    var tasks = new TaskService();
    var backups = new BackupService(pathService, log);
    var localContent = new LocalContentService(pathService, log);
    var referencedMap = Path.Combine(mapsRoot, "shared_preview");
    var scenarioOnlyMap = Path.Combine(mapsRoot, "scenario_only");
    Directory.CreateDirectory(referencedMap);
    Directory.CreateDirectory(scenarioOnlyMap);
    await File.WriteAllBytesAsync(Path.Combine(referencedMap, "shared_preview.scmap"), CreatePreviewMapBytes());
    await File.WriteAllTextAsync(Path.Combine(scenarioOnlyMap, "scenario_only_scenario.lua"),
        "version = 3\nScenarioInfo = {\n    name = 'Official Shared Terrain',\n    type = 'campaign',\n    map = '/maps/shared_preview/shared_preview.scmap',\n    save = '/maps/scenario_only/scenario_only_save.lua',\n    script = '/maps/scenario_only/scenario_only_script.lua',\n}\n");
    await File.WriteAllTextAsync(Path.Combine(scenarioOnlyMap, "scenario_only_save.lua"), "save");
    await File.WriteAllTextAsync(Path.Combine(scenarioOnlyMap, "scenario_only_script.lua"), "script");
    Check(MapPreviewService.TryLoad(scenarioOnlyMap) is { PixelWidth: 2, PixelHeight: 2 },
        "无 .scmap 的场景可从同一地图库中被引用的地图读取游戏预览");
    var sharedMapEntry = (await localContent.ScanAsync("地图")).Single(x => x.Folder == "scenario_only");
    Check(sharedMapEntry.IsSharedMap && !sharedMapEntry.Valid && sharedMapEntry.Name == "Official Shared Terrain" &&
          sharedMapEntry.Version == "3" && sharedMapEntry.Files == 3 && sharedMapEntry.Bytes > 0 &&
          sharedMapEntry.Detail.Contains("shared_preview", StringComparison.Ordinal),
        "共用地形战役地图会显示名称、版本和自身文件大小，不会被误判为异常或独立发布包");
    var sharedMapPackageRejected = false;
    try { await localContent.AnalyzeDirectoryAsync(scenarioOnlyMap); }
    catch (InvalidDataException) { sharedMapPackageRejected = true; }
    Check(sharedMapPackageRejected, "共用地形场景不能作为独立地图包上传或安装");
    Directory.Delete(referencedMap, recursive: true);
    var missingSharedMap = (await localContent.ScanAsync("地图")).Single(x => x.Folder == "scenario_only");
    Check(!missingSharedMap.IsSharedMap && !missingSharedMap.Valid,
        "共用的 .scmap 消失后，场景会重新标记为真正需要检查");
    Directory.Delete(scenarioOnlyMap, recursive: true);
    var installer = new InstallService(packageCloud, pathService, localContent, backups, tasks, log, config);
    await installer.InstallAsync(new CloudContentEntry
    {
        Kind = "地图",
        Id = "recent-map",
        Name = "最近安装测试地图",
        Version = "1",
        File = "packages/recent-map.zip",
        FolderName = "recent_map",
        Size = packagePayload.Length
    });
    Check(Directory.Exists(Path.Combine(mapsRoot, "recent_map")), "云端内容包可安装到玩家地图目录");
    Check(config.Current.RecentContentKeys.FirstOrDefault() == "地图:recent-map", "成功安装后自动写入最近内容记录");

    var installedRecentMap = Path.Combine(mapsRoot, "recent_map");
    var expectedContentHash = await ContentHash.DirectorySha256Async(installedRecentMap);
    var extraFile = Path.Combine(installedRecentMap, "player_extra.lua");
    await File.WriteAllTextAsync(extraFile, "extra file must be removed by strict repair");
    Check(!string.Equals(await ContentHash.DirectorySha256Async(installedRecentMap), expectedContentHash, StringComparison.OrdinalIgnoreCase), "同版本目录多出一个文件会改变完整内容指纹");
    await installer.InstallAsync(new CloudContentEntry
    {
        Kind = "地图",
        Id = "recent-map",
        Name = "严格一致性测试地图",
        Version = "1",
        File = "packages/recent-map.zip",
        FolderName = "recent_map",
        Size = packagePayload.Length,
        ContentSha256 = expectedContentHash
    }, installedRecentMap, existingVersion: "1");
    Check(!File.Exists(extraFile) && string.Equals(await ContentHash.DirectorySha256Async(installedRecentMap), expectedContentHash, StringComparison.OrdinalIgnoreCase), "严格修复采用整目录替换并删除活动目录中的多余文件");
    var strictRepairBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == "严格一致性测试地图");
    Check(strictRepairBackup is not null && File.Exists(Path.Combine(strictRepairBackup.ContentRoot, "player_extra.lua")), "严格修复前备份仍保留被移出的多余文件以便回滚");

    var syncPackage = CreateMapPackage("sync_map");
    var syncPackagePath = Path.Combine(configDirectory, "sync-package.zip");
    await File.WriteAllBytesAsync(syncPackagePath, syncPackage);
    var syncSource = Path.Combine(configDirectory, "sync-package-content");
    ZipFile.ExtractToDirectory(syncPackagePath, syncSource);
    var syncContentHash = await ContentHash.DirectorySha256Async(Path.Combine(syncSource, "sync_map"));
    var syncEntry = new CloudContentEntry
    {
        Kind = "地图",
        Id = "sync-map",
        Name = "同步回归地图",
        Version = "1",
        FolderName = "sync_map",
        File = "packages/sync-map.zip",
        Size = syncPackage.Length,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(syncPackage)),
        ContentSha256 = syncContentHash
    };
    using var syncClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [syncEntry] }, syncPackage))
    { Timeout = Timeout.InfiniteTimeSpan };
    var syncCloud = new CloudCatalogService(config, syncClient);
    var syncInstaller = new InstallService(syncCloud, pathService, localContent, backups, tasks, log, config);
    var syncService = new SyncService(syncCloud, localContent, syncInstaller, log);
    var syncRoot = Path.Combine(mapsRoot, "sync_map");
    var firstSync = await syncService.SyncAllAsync(["地图"]);
    Check(firstSync.Installed == 1 && firstSync.Failed == 0 && Directory.Exists(syncRoot), "一键同步会从云端清单安装缺失地图");
    var unchangedSync = await syncService.SyncAllAsync(["地图"]);
    Check(unchangedSync.Skipped == 1 && unchangedSync.Installed == 0, "一键同步对内容指纹一致的地图不会重复安装");
    var activeAccount = "test-server|player-a";
    var syncPreferences = new SyncPreferenceService(() => activeAccount);
    await syncPreferences.SetExcludedAsync(syncEntry, true);
    Check(await new SyncPreferenceService(() => activeAccount).IsExcludedAsync(syncEntry), "不喜欢标记重新启动后仍保留");
    activeAccount = "test-server|player-b";
    Check(!await syncPreferences.IsExcludedAsync(syncEntry), "不同登录账号不会共享不喜欢列表");
    activeAccount = "test-server|player-a";
    var preferredInstaller = new InstallService(syncCloud, pathService, localContent, backups, tasks, log, config, syncPreferences);
    var preferredSync = new SyncService(syncCloud, localContent, preferredInstaller, log, syncPreferences);
    Directory.Delete(syncRoot, recursive: true);
    var excludedSync = await preferredSync.SyncAllAsync(["地图"]);
    Check(excludedSync.Skipped == 1 && excludedSync.Installed == 0 && excludedSync.Failed == 0 &&
          !Directory.Exists(syncRoot) && excludedSync.Messages.Any(x => x.Contains("不喜欢")),
        "玩家删除已标记不喜欢的地图后，一键同步不会重新安装");
    await preferredInstaller.InstallAsync(syncEntry);
    Check(Directory.Exists(syncRoot) && await syncPreferences.IsExcludedAsync(syncEntry),
        "玩家仍可手动安装不喜欢的内容，且手动安装不会取消跳过设置");
    await syncPreferences.SetExcludedAsync(syncEntry, false);
    Check(!await syncPreferences.IsExcludedAsync(syncEntry), "再次点击可恢复自动同步");
    var alternatePackage = CreateMapPackage("sync_map_v2");
    var alternatePackagePath = Path.Combine(configDirectory, "alternate-map.zip");
    await File.WriteAllBytesAsync(alternatePackagePath, alternatePackage);
    ZipFile.ExtractToDirectory(alternatePackagePath, mapsRoot);
    var alternateRoot = Path.Combine(mapsRoot, "sync_map_v2");
    var ambiguousEntry = new CloudContentEntry
    {
        Kind = "地图", Id = "sync_map", Name = syncEntry.Name, Version = syncEntry.Version,
        FolderName = syncEntry.FolderName, File = syncEntry.File, Size = syncEntry.Size,
        Sha256 = syncEntry.Sha256, ContentSha256 = syncEntry.ContentSha256
    };
    Check(ContentIdentity.FindBestResult((await localContent.ScanAsync("地图")).ToArray(), ambiguousEntry).Ambiguous,
        "两个地图版本共享同一 ID 时会识别为多个候选目录");
    var ambiguousLocals = (await localContent.ScanAsync("地图")).ToArray();
    var verifiedDuplicate = await ContentIdentity.FindVerifiedCopyAsync(ambiguousLocals, ambiguousEntry,
        ContentIdentity.FindBestResult(ambiguousLocals, ambiguousEntry).Score);
    Check(verifiedDuplicate is not null && verifiedDuplicate.Root == syncRoot,
        "多个本地版本中已存在云端指纹一致的副本时可识别为已安装");
    var mismatchedDuplicate = new CloudContentEntry
    {
        Kind = "地图", Id = ambiguousEntry.Id, Name = ambiguousEntry.Name, Version = ambiguousEntry.Version,
        FolderName = ambiguousEntry.FolderName, ContentSha256 = new string('0', 64), Aliases = ambiguousEntry.Aliases
    };
    Check(await ContentIdentity.FindVerifiedCopyAsync(ambiguousLocals, mismatchedDuplicate,
          ContentIdentity.FindBestResult(ambiguousLocals, mismatchedDuplicate).Score) is null,
        "多个本地目录都不符合云端内容指纹时仍拒绝擅自选择覆盖目标");
    using var ambiguousClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [ambiguousEntry] }, syncPackage))
    { Timeout = Timeout.InfiniteTimeSpan };
    var ambiguousCloud = new CloudCatalogService(config, ambiguousClient);
    var ambiguousSync = new SyncService(ambiguousCloud, localContent,
        new InstallService(ambiguousCloud, pathService, localContent, backups, tasks, log, config), log);
    var beforeAmbiguousBackupCount = (await backups.ListAsync()).Count;
    var verifiedAmbiguous = await ambiguousSync.SyncAllAsync(["地图"]);
    Check(verifiedAmbiguous.Skipped == 1 && verifiedAmbiguous.Failed == 0 &&
          verifiedAmbiguous.Messages.Any(x => x.Contains("其他本地副本保留")) &&
          Directory.Exists(alternateRoot) && (await backups.ListAsync()).Count == beforeAmbiguousBackupCount,
        "多个同 ID 地图中已有云端指纹一致的版本时保留全部副本且不重复覆盖");
    var conflictCandidates = ambiguousLocals.Where(x => ContentIdentity.MatchScore(x, ambiguousEntry) ==
        ContentIdentity.FindBestResult(ambiguousLocals, ambiguousEntry).Score).ToArray();
    var conflictInstaller = new InstallService(ambiguousCloud, pathService, localContent, backups, tasks, log, config);
    var wrongVersionConflict = new CloudContentEntry
    {
        Kind = "地图", Id = ambiguousEntry.Id, Name = ambiguousEntry.Name, Version = "999",
        FolderName = ambiguousEntry.FolderName, File = ambiguousEntry.File, Size = ambiguousEntry.Size,
        Sha256 = ambiguousEntry.Sha256, ContentSha256 = ambiguousEntry.ContentSha256
    };
    var rejectedConflictPackage = false;
    try { await conflictInstaller.ReplaceConflictsAsync(wrongVersionConflict, conflictCandidates); }
    catch (InvalidDataException) { rejectedConflictPackage = true; }
    Check(rejectedConflictPackage && Directory.Exists(syncRoot) && Directory.Exists(alternateRoot),
        "清理冲突前先校验云端包内版本，异常包不会删除本地目录");
    await conflictInstaller.ReplaceConflictsAsync(ambiguousEntry, conflictCandidates);
    var conflictBackups = (await backups.ListAsync()).Where(x => x.Reason == "清理重复版本并安装云端内容前自动备份").ToArray();
    Check(Directory.Exists(syncRoot) && !Directory.Exists(alternateRoot) &&
          string.Equals(await ContentHash.DirectorySha256Async(syncRoot), syncContentHash, StringComparison.OrdinalIgnoreCase) &&
          conflictBackups.Length == 2 && conflictBackups.All(x => Directory.Exists(x.ContentRoot)) &&
          conflictBackups.Any(x => x.OriginalRoot == syncRoot) && conflictBackups.Any(x => x.OriginalRoot == alternateRoot),
        "手动清理重复目录会安装经校验的云端版本，并保存两个原目录的历史备份");
    var syncExtraFile = Path.Combine(syncRoot, "player_extra.lua");
    await File.WriteAllTextAsync(syncExtraFile, "local change for rollback");
    var repairSync = await syncService.SyncAllAsync(["地图"]);
    Check(repairSync.Installed == 1 && !File.Exists(syncExtraFile) &&
          string.Equals(await ContentHash.DirectorySha256Async(syncRoot), syncContentHash, StringComparison.OrdinalIgnoreCase),
        "一键同步会备份并修复同版本内容差异");
    var syncRepairBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == "同步回归地图");
    Check(syncRepairBackup is not null && File.Exists(Path.Combine(syncRepairBackup.ContentRoot, "player_extra.lua")),
        "同步修复前的玩家文件仍可从备份取回");
    if (syncRepairBackup is not null)
    {
        await backups.RestoreAsync(syncRepairBackup);
        Check(File.Exists(syncExtraFile), "历史回滚可把同步修复前的文件恢复到玩家目录");
    }
    var syncScenario = Path.Combine(syncRoot, "sync_map_scenario.lua");
    await File.WriteAllTextAsync(syncScenario, (await File.ReadAllTextAsync(syncScenario)).Replace("map_version = 1", "map_version = 9"));
    var newerSync = await syncService.SyncAllAsync(["地图"]);
    Check(newerSync.Skipped == 1 && newerSync.Installed == 0 && (await File.ReadAllTextAsync(syncScenario)).Contains("map_version = 9"),
        "一键同步保护玩家目录中较新的地图版本");

    var duplicateEntry = new CloudContentEntry
    {
        Id = "other-sync-map", Name = "重复目录地图", Version = "1", FolderName = "sync_map",
        File = syncEntry.File, Size = syncEntry.Size, Sha256 = syncEntry.Sha256, ContentSha256 = syncEntry.ContentSha256
    };
    using var duplicateClient = new HttpClient(new CatalogPackageHandler(new MapManifest { ManifestVersion = 1, Maps = [syncEntry, duplicateEntry] }, syncPackage))
    { Timeout = Timeout.InfiniteTimeSpan };
    var duplicateCloud = new CloudCatalogService(config, duplicateClient);
    var duplicateSync = new SyncService(duplicateCloud, localContent,
        new InstallService(duplicateCloud, pathService, localContent, backups, tasks, log, config), log);
    var duplicateBlocked = false;
    try { await duplicateSync.SyncAllAsync(["地图"]); }
    catch (InvalidDataException ex) when (ex.Message.Contains("重复目标目录")) { duplicateBlocked = true; }
    Check(duplicateBlocked && (await File.ReadAllTextAsync(syncScenario)).Contains("map_version = 9"),
        "云端清单目标目录重复时整批停止，任何玩家目录均不会先被覆盖");

    var nestedParent = Path.Combine(mapsRoot, "wrapper");
    ZipFile.ExtractToDirectory(syncPackagePath, nestedParent);
    var nestedRoot = Path.Combine(nestedParent, "sync_map");
    var nestedBlocked = false;
    try { await syncInstaller.InstallAsync(syncEntry, nestedRoot, existingVersion: "1"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("直接子文件夹")) { nestedBlocked = true; }
    Check(nestedBlocked && Directory.Exists(nestedRoot), "自动更新不会覆盖嵌套在包装目录中的玩家内容");

    var modsRoot = Path.Combine(configDirectory, "player", "Mods");
    Directory.CreateDirectory(modsRoot);
    config.Current.ModsDir = modsRoot;
    var modPackage = CreateModPackage("sync_mod");
    var modEntry = new CloudContentEntry
    {
        Kind = "MOD", Id = "sync-mod", Name = "同步回归 MOD", Version = "1",
        FolderName = "sync_mod", File = "packages/sync-mod.zip", Size = modPackage.Length,
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modPackage))
    };
    using var modClient = new HttpClient(new ModCatalogPackageHandler(
        new ModManifest { ManifestVersion = 1, Mods = [modEntry] }, modPackage))
    { Timeout = Timeout.InfiniteTimeSpan };
    var modCloud = new CloudCatalogService(config, modClient);
    var modInstaller = new InstallService(modCloud, pathService, localContent, backups, tasks, log, config);
    var modSync = new SyncService(modCloud, localContent, modInstaller, log);
    var modRoot = Path.Combine(modsRoot, "sync_mod");
    await syncPreferences.SetExcludedAsync(modEntry, true);
    var preferredModSync = new SyncService(modCloud, localContent,
        new InstallService(modCloud, pathService, localContent, backups, tasks, log, config, syncPreferences), log, syncPreferences);
    var excludedModSync = await preferredModSync.SyncAllAsync(["MOD"]);
    Check(excludedModSync.Skipped == 1 && excludedModSync.Installed == 0 && !Directory.Exists(modRoot),
        "不喜欢的云端 MOD 也不会被一键同步自动安装");
    await syncPreferences.SetExcludedAsync(modEntry, false);
    var firstModSync = await modSync.SyncAllAsync(["MOD"]);
    Check(firstModSync.Installed == 1 && firstModSync.Failed == 0 && File.Exists(Path.Combine(modRoot, "mod_info.lua")),
        "一键同步会安装并识别有效 MOD");
    var modBackupsBeforeRepeat = (await backups.ListAsync()).Count;
    var repeatedModSync = await modSync.SyncAllAsync(["MOD"]);
    Check(repeatedModSync.Skipped == 1 && repeatedModSync.Installed == 0 &&
          (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
        "无内容指纹的同版本 MOD 再次同步不会重复安装或创建备份");
    var repeatedManualMod = await modInstaller.InstallAsync(modEntry, modRoot, existingVersion: "1");
    Check(!repeatedManualMod && (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
        "手动再次安装完全相同的 MOD 包不会重复覆盖或备份");
    var releaseLabeledMod = new CloudContentEntry
    {
        Kind = "MOD", Id = modEntry.Id, Name = modEntry.Name, Version = "2026.08.15", GameVersion = "1",
        FolderName = modEntry.FolderName, File = modEntry.File, Size = modEntry.Size, Sha256 = modEntry.Sha256
    };
    var releaseLabeledInstall = await modInstaller.InstallAsync(releaseLabeledMod, modRoot, existingVersion: "1");
    Check(!releaseLabeledInstall && (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
        "发布版本是日期但包内游戏版本一致时能安全验包且不重复覆盖");
    var changedVersionBlocked = false;
    try { await modInstaller.InstallAsync(modEntry, modRoot, existingVersion: "0"); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("版本在安装期间发生变化")) { changedVersionBlocked = true; }
    Check(changedVersionBlocked && File.Exists(Path.Combine(modRoot, "mod_info.lua")) &&
          (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
        "安装前复核本地版本，版本已变化时不覆盖玩家 MOD");
    var wrongModVersion = new CloudContentEntry
    {
        Kind = "MOD", Id = modEntry.Id, Name = modEntry.Name, Version = "2",
        FolderName = modEntry.FolderName, File = modEntry.File, Size = modEntry.Size, Sha256 = modEntry.Sha256
    };
    var wrongPackageBlocked = false;
    try { await modInstaller.InstallAsync(wrongModVersion, modRoot, existingVersion: "1"); }
    catch (InvalidDataException ex) when (ex.Message.Contains("版本不一致")) { wrongPackageBlocked = true; }
    Check(wrongPackageBlocked && File.Exists(Path.Combine(modRoot, "mod_info.lua")) &&
          (await backups.ListAsync()).Count == modBackupsBeforeRepeat,
        "云端标注版本与 MOD 包内真实版本不一致时保留玩家原文件");
    var scannedMod = (await localContent.ScanAsync("MOD")).Single(x => x.Root == modRoot);
    Check(scannedMod.Valid && scannedMod.Id == "sync_mod" && scannedMod.Version == "1", "安装后的 MOD 可被本地扫描识别");
    var modExtra = Path.Combine(modRoot, "player_extra.lua");
    await File.WriteAllTextAsync(modExtra, "player mod change");
    var editedModSync = await modSync.SyncAllAsync(["MOD"]);
    Check(editedModSync.Skipped == 1 && File.Exists(modExtra),
        "缺少云端内容指纹时不会擅自覆盖同版本 MOD 的玩家改动");
    await modInstaller.UninstallAsync(scannedMod);
    Check(!Directory.Exists(modRoot), "MOD 安全卸载会移除目标目录");
    var modBackup = (await backups.ListAsync()).FirstOrDefault(x => x.Name == scannedMod.Name);
    Check(modBackup is not null && File.Exists(Path.Combine(modBackup.ContentRoot, "player_extra.lua")),
        "MOD 卸载前会保留玩家文件备份");
    if (modBackup is not null)
    {
        await backups.RestoreAsync(modBackup);
        Check(File.Exists(modExtra) && (await localContent.AnalyzeDirectoryAsync(modRoot)).Valid,
            "MOD 历史备份可恢复为有效内容");
    }

    var legacyMapRoot = Path.Combine(mapsRoot, "legacy_map");
    Directory.CreateDirectory(legacyMapRoot);
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map.scmap"), "legacy-map");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_save.lua"), "Scenario = {}");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_script.lua"), "function OnPopulate() end");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_scenario.lua"), "name = \"Legacy Map\"\nversion = 3\nmap = \"/maps/legacy_map/legacy_map.scmap\"\nsave = \"/maps/legacy_map/legacy_map_save.lua\"\nscript = \"/maps/legacy_map/legacy_map_script.lua\"\n");
    var legacyMap = await localContent.AnalyzeDirectoryAsync(legacyMapRoot);
    Check(legacyMap.Valid && legacyMap.Version == "3", "旧版标准地图 scenario.lua 的 version 字段可以正常识别");
    var publishService = new PublicationPreparationService(localContent);
    var publishSource = await localContent.AnalyzeDirectoryAsync(Path.Combine(mapsRoot, "recent_map"));
    var publishMetadata = new PublicationMetadata("回归测试发布地图", publishSource.Version, "测试管理员", "隔离目录内的发布材料回归检查。", "地图", "测试,安全");
    const string emptyMapManifest = """{"manifest_version":1,"updated_at":"2026-01-01T00:00:00Z","maps":[],"preserved_field":{"enabled":true}}""";
    var publishBundle = await publishService.PrepareAsync(publishSource, publishMetadata, emptyMapManifest,
        Path.Combine(configDirectory, "publish-staging"), config.Current, new UserInfo { RoleKey = "admin" });
    using (var preparedZip = ZipFile.OpenRead(publishBundle.PackagePath))
        Check(preparedZip.Entries.Count == publishSource.Files && preparedZip.Entries.All(x => x.FullName.StartsWith(publishSource.Folder + "/", StringComparison.Ordinal)),
            "管理员发布包只包含目标地图顶层目录且文件数量一致");
    var preparedManifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(publishBundle.ManifestPath))!;
    Check(preparedManifest["preserved_field"]?["enabled"]?.GetValue<bool>() == true &&
          preparedManifest["maps"]?[0]?["game_version"]?.GetValue<string>() == publishSource.Version &&
          preparedManifest["maps"]?[0]?["sha256"]?.GetValue<string>() == publishBundle.PackageSha256 &&
          publishBundle.PackageKey.StartsWith(config.Current.Root.Trim('/') + "/maps/", StringComparison.Ordinal),
          "待上传清单保留线上未知字段并记录真实游戏版本、对象键和 ZIP 哈希");
    Check(publishBundle.ContentSha256 == await ContentHash.DirectorySha256Async(publishSource.Root) &&
          publishBundle.PackageSha256 == await ContentHash.FileSha256Async(publishBundle.PackagePath),
          "发布材料中的目录与 ZIP 双哈希均可重新核对");
    var sameVersionManifest = $$"""{"manifest_version":1,"maps":[{"id":"{{publishSource.Id}}","name":"已发布","version":"{{publishSource.Version}}","folder_name":"{{publishSource.Folder}}","file":"scfa/maps/old.zip"}]}""";
    await CheckThrowsAsync<InvalidDataException>(() => publishService.PrepareAsync(publishSource, publishMetadata, sameVersionManifest,
        Path.Combine(configDirectory, "publish-staging"), config.Current, new UserInfo { RoleKey = "admin" }),
        "同 ID 同发布版本不能覆盖已有云端记录");
    await CheckThrowsAsync<UnauthorizedAccessException>(() => publishService.PrepareAsync(publishSource, publishMetadata, emptyMapManifest,
        Path.Combine(configDirectory, "publish-staging"), config.Current, new UserInfo { RoleKey = "user" }),
        "普通用户不能生成发布材料");
    var previewPath = Path.Combine(configDirectory, "cloud-preview.png");
    CreatePreviewPng(previewPath, 320, 180);
    var dimensions = PreviewImageValidator.Validate(previewPath);
    Check(dimensions == (320, 180), "云端预览图验证真实格式和最低尺寸");
    var fakeJpegPath = Path.Combine(configDirectory, "fake-preview.jpg");
    File.Copy(previewPath, fakeJpegPath);
    CheckThrows(() => PreviewImageValidator.Validate(fakeJpegPath), "云端预览图拒绝扩展名与真实格式不一致");

    var removableRoot = Path.Combine(mapsRoot, "map_to_remove");
    var removablePackagePath = Path.Combine(configDirectory, "removable-map.zip");
    await File.WriteAllBytesAsync(removablePackagePath, CreateMapPackage("map_to_remove"));
    ZipFile.ExtractToDirectory(removablePackagePath, mapsRoot);
    await File.WriteAllTextAsync(Path.Combine(removableRoot, "marker.txt"), "original content");
    var removableHash = await ContentHash.DirectorySha256Async(removableRoot);
    await installer.UninstallAsync(new LocalContentEntry
    {
        Kind = "地图",
        Root = removableRoot,
        Folder = "map_to_remove",
        Id = "map_to_remove",
        Name = "卸载测试地图",
        Version = "1",
        Valid = true
    });
    var uninstallBackups = await backups.ListAsync();
    Check(!Directory.Exists(removableRoot), "卸载会从玩家内容目录移除目标文件夹");
    var uninstallBackup = uninstallBackups.FirstOrDefault(x => x.Name == "卸载测试地图");
    Check(uninstallBackup is not null && File.Exists(Path.Combine(uninstallBackup.ContentRoot, "marker.txt")) &&
          uninstallBackup.ContentHash.Equals(removableHash, StringComparison.OrdinalIgnoreCase) &&
          (await ContentHash.DirectorySha256Async(uninstallBackup.ContentRoot)).Equals(removableHash, StringComparison.OrdinalIgnoreCase),
        "本地删除前会留下与原目录完整内容指纹一致的备份");
    if (uninstallBackup is not null)
    {
        await backups.RestoreAsync(uninstallBackup);
        Check(File.Exists(Path.Combine(removableRoot, "marker.txt")), "安全卸载后的内容可从历史备份恢复");
    }
    await Task.Delay(500);
}
finally
{
    Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfigDirectory);
    Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousDataDirectory);
    try { if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true); } catch { }
}

var selectionCommandUiPassed = false;
Exception? selectionCommandUiError = null;
var cloudSkipUiPassed = false;
var selectionUiThread = new Thread(() =>
{
    var selectionRoot = Path.Combine(Path.GetTempPath(), "scfa_selection_ui_" + Guid.NewGuid().ToString("N"));
    SCFA.ContentCenter.App? application = null;
    System.Windows.Window? host = null;
    System.Windows.Window? cloudHost = null;
    try
    {
        Directory.CreateDirectory(selectionRoot);
        application = new SCFA.ContentCenter.App();
        application.InitializeComponent();
        var probe = new SelectionCommandBindingProbe(selectionRoot);
        var view = new SCFA.ContentCenter.Views.LocalContentView { DataContext = probe };
        host = new System.Windows.Window
        {
            Content = view,
            Width = 980,
            Height = 680,
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None,
            Opacity = 0
        };
        application.MainWindow = host;
        host.Show();
        host.UpdateLayout();
        var grid = VisualTreeProbe.Find<System.Windows.Controls.DataGrid>(view);
        var buttons = VisualTreeProbe.FindAll<System.Windows.Controls.Button>(view).ToArray();
        var openButton = buttons.Single(button => Equals(button.Content, "打开所在文件夹"));
        var uninstallButton = buttons.Single(button => Equals(button.Content, "删除所选（保留备份）"));
        var rowDeleteButton = buttons.Single(button => Equals(button.Content, "删除本地"));
        var sizeColumn = grid.Columns.OfType<System.Windows.Controls.DataGridTextColumn>()
            .Single(column => Equals(column.Header, "占用空间"));
        grid.SelectedItem = probe.Items[0];
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        selectionCommandUiPassed = grid.IsReadOnly &&
                                   (sizeColumn.Binding as System.Windows.Data.Binding)?.Mode == System.Windows.Data.BindingMode.OneWay &&
                                   ReferenceEquals(probe.SelectedItem, probe.Items[0]) &&
                                   probe.SelectedState == "结构校验通过" &&
                                   openButton.IsEnabled && uninstallButton.IsEnabled &&
                                   rowDeleteButton.IsEnabled && ReferenceEquals(rowDeleteButton.CommandParameter, probe.Items[0]);
        var cloudProbe = new CloudSkipBindingProbe();
        var cloudView = new SCFA.ContentCenter.Views.CloudContentView { DataContext = cloudProbe };
        cloudHost = new System.Windows.Window
        {
            Content = cloudView,
            Width = 1200,
            Height = 760,
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None,
            Opacity = 0
        };
        cloudHost.Show();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        cloudHost.UpdateLayout();
        var cloudGrid = VisualTreeProbe.Find<System.Windows.Controls.DataGrid>(cloudView);
        cloudGrid.ScrollIntoView(cloudProbe.Items[0]);
        cloudGrid.SelectedItem = cloudProbe.Items[0];
        cloudHost.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var cloudButtons = VisualTreeProbe.FindAll<System.Windows.Controls.Button>(cloudView).ToArray();
        var skipButton = cloudButtons.SingleOrDefault(button => Equals(button.Content, "♡ 不喜欢")) ??
            throw new InvalidOperationException("未找到不喜欢按钮；实际按钮：" + string.Join("、", cloudButtons.Select(button => button.Content?.ToString())));
        var versionRun = VisualTreeProbe.FindAll<System.Windows.Controls.TextBlock>(cloudView)
            .SelectMany(block => block.Inlines.OfType<System.Windows.Documents.Run>())
            .Single(run => System.Windows.Data.BindingOperations.GetBinding(run, System.Windows.Documents.Run.TextProperty)?.Path?.Path == "SelectedItem.VersionDisplay");
        cloudSkipUiPassed = skipButton.IsEnabled && ReferenceEquals(skipButton.CommandParameter, cloudProbe.Items[0]) &&
                            ReferenceEquals(cloudProbe.SelectedItem, cloudProbe.Items[0]) &&
                            System.Windows.Data.BindingOperations.GetBinding(versionRun, System.Windows.Documents.Run.TextProperty)?.Mode == System.Windows.Data.BindingMode.OneWay;
    }
    catch (Exception ex) { selectionCommandUiError = ex; }
    finally
    {
        try { cloudHost?.Close(); } catch { }
        try { host?.Close(); } catch { }
        try { application?.Shutdown(); } catch { }
        try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { }
        try { if (Directory.Exists(selectionRoot)) Directory.Delete(selectionRoot, true); } catch { }
    }
});
selectionUiThread.SetApartmentState(ApartmentState.STA);
selectionUiThread.Start();
selectionUiThread.Join();
if (selectionCommandUiError is not null) Console.Error.WriteLine("本地内容选择命令 UI 测试异常：" + selectionCommandUiError);
Check(selectionCommandUiPassed, "真实 WPF 本地内容页面的逐行删除与选中项操作正确绑定目标目录");
Check(cloudSkipUiPassed, "真实 WPF 云端列表选中地图后版本详情保持只读绑定且不喜欢按钮指向当前行");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"回归测试失败：{failures.Count} 项");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("全部核心回归测试通过。");
}

void CheckThrows(Action action, string name)
{
    try
    {
        action();
        Check(false, name);
    }
    catch (InvalidDataException)
    {
        Check(true, name);
    }
}

void CheckArgumentThrows(Action action, string name)
{
    try
    {
        action();
        Check(false, name);
    }
    catch (ArgumentException)
    {
        Check(true, name);
    }
}

void CheckDirectoryThrows(Action action, string name)
{
    try
    {
        action();
        Check(false, name);
    }
    catch (DirectoryNotFoundException)
    {
        Check(true, name);
    }
}

byte[] CreateMapPackage(string folder)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        AddText(archive, $"{folder}/{folder}.scmap", "map-bytes");
        AddText(archive, $"{folder}/{folder}_save.lua", "Scenario = {}");
        AddText(archive, $"{folder}/{folder}_script.lua", "function OnPopulate() end");
        AddText(archive, $"{folder}/{folder}_scenario.lua", $"""
name = "Regression Map"
map_version = 1
map = "/maps/{folder}/{folder}.scmap"
save = "/maps/{folder}/{folder}_save.lua"
script = "/maps/{folder}/{folder}_script.lua"
""");
    }
    return output.ToArray();
}

byte[] CreatePreviewMapBytes()
{
    var bytes = new byte[34 + 128 + 16 + 4];
    bytes[0] = (byte)'M'; bytes[1] = (byte)'a'; bytes[2] = (byte)'p'; bytes[3] = 0x1A;
    BitConverter.GetBytes(2).CopyTo(bytes, 4);
    BitConverter.GetBytes(128 + 16).CopyTo(bytes, 30);
    bytes[34] = (byte)'D'; bytes[35] = (byte)'D'; bytes[36] = (byte)'S'; bytes[37] = (byte)' ';
    BitConverter.GetBytes(124).CopyTo(bytes, 38);
    BitConverter.GetBytes(2).CopyTo(bytes, 34 + 12);
    BitConverter.GetBytes(2).CopyTo(bytes, 34 + 16);
    BitConverter.GetBytes(32).CopyTo(bytes, 34 + 76);
    BitConverter.GetBytes(32).CopyTo(bytes, 34 + 88);
    BitConverter.GetBytes(0x00FF0000u).CopyTo(bytes, 34 + 92);
    BitConverter.GetBytes(0x0000FF00u).CopyTo(bytes, 34 + 96);
    BitConverter.GetBytes(0x000000FFu).CopyTo(bytes, 34 + 100);
    BitConverter.GetBytes(0xFF000000u).CopyTo(bytes, 34 + 104);
    for (var i = 0; i < 4; i++) new byte[] { 0, 0, 255, 255 }.CopyTo(bytes, 34 + 128 + i * 4);
    return bytes;
}

byte[] CreateModPackage(string folder)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        AddText(archive, $"{folder}/mod_info.lua", "name = \"Regression Mod\"\nuid = \"sync_mod\"\nversion = 1\n");
        AddText(archive, $"{folder}/hook/units.lua", "return {}\n");
    }
    return output.ToArray();
}

void AddText(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(content);
}

void CreatePreviewPng(string path, int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = 0x72;
        pixels[index + 1] = 0x42;
        pixels[index + 2] = 0x18;
        pixels[index + 3] = 0xFF;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

sealed class ResumeDownloadHandler(byte[] payload, int interruptAfter) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool SawRangeRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Requests == 1)
        {
            var first = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptingStream(payload, interruptAfter))
            };
            first.Headers.ETag = new EntityTagHeaderValue("\"package-v1\"");
            first.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(first);
        }

        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        var from = range?.From ?? -1;
        SawRangeRequest = from == interruptAfter && request.Headers.IfRange?.EntityTag?.Tag == "\"package-v1\"";
        if (!SawRangeRequest)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var tail = payload.AsMemory((int)from).ToArray();
        var resumed = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(tail)
        };
        resumed.Headers.ETag = new EntityTagHeaderValue("\"package-v1\"");
        resumed.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
        return Task.FromResult(resumed);
    }
}

sealed class StaticPackageHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Headers.ETag = new EntityTagHeaderValue("\"static-package-v1\"");
        return Task.FromResult(response);
    }
}

sealed class CatalogPackageHandler(MapManifest manifest, byte[] package) : HttpMessageHandler
{
    private readonly byte[] _manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/manifest/latest.json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifest) });
        if (path.EndsWith("/packages/sync-map.zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class ModCatalogPackageHandler(ModManifest manifest, byte[] package) : HttpMessageHandler
{
    private readonly byte[] _manifest = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/manifest/mods.json", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_manifest) });
        if (path.EndsWith("/packages/sync-mod.zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

sealed class InlineProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}

sealed class SetupBindingProbe
{
    public int DetectedCount => DetectedRoots.Count;
    public int SetupProgress => 100;
    public string GameRoot { get; set; } = @"C:\Games\Supreme Commander Forged Alliance";
    public List<string> DetectedRoots { get; } = [@"C:\Games\Supreme Commander Forged Alliance"];
    public string? SelectedDetectedRoot { get; set; } = @"C:\Games\Supreme Commander Forged Alliance";
    public string MapsDir { get; set; } = @"C:\SCFA Content\Maps";
    public string ModsDir { get; set; } = @"C:\SCFA Content\Mods";
    public string GameStateLabel => "游戏目录已识别";
    public string ContentStateLabel => "玩家目录相互独立";
    public string ReadyLabel => "准备完成，可以保存";
    public string Status => "目录检查通过。保存时会创建缺失的玩家内容目录。";
}

sealed class SettingsBindingProbe
{
    public System.Windows.Visibility AdvancedSettingsVisibility => System.Windows.Visibility.Collapsed;
    public string DraftStateLabel => "有未保存更改";
    public int ConfigurationScore => 92;
    public string DirectoryHealthLabel => "目录 · 正常";
    public string ApiHealthLabel => "API · 已连接";
    public string CosHealthLabel => "对象存储 · 已连接";
    public string ServerHealthLabel => "服务器路径 · 可用";
    public string ValidationSummary => "4/4 项关键检查通过";
    public string BusyStateLabel => "就绪";
    public string Status => "所有配置均在草稿中，点击保存后才会应用。";
    public string DirectoryStatus => "游戏目录和玩家内容目录有效";
    public string ApiStatus => "连接成功 · 46 ms";
    public string CosStatus => "Bucket 与区域配置可用";
    public string ServerStatus => "账号 API 可访问";
    public string GameRoot { get; set; } = @"C:\Games\Supreme Commander Forged Alliance";
    public string MapsDir { get; set; } = @"C:\SCFA Content\Maps";
    public string ModsDir { get; set; } = @"C:\SCFA Content\Mods";
    public string ApiUrl { get; set; } = "https://content.example.com";
    public string ApiPin { get; set; } = "sha256/example-pin";
    public string Bucket { get; set; } = "scfa-content";
    public string Region { get; set; } = "ap-shanghai";
    public string Root { get; set; } = "releases/";
    public string UpdateChannel { get; set; } = "stable";
    public string UpdateManifestUrl { get; set; } = "https://content.example.com/client/manifest.json";
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoLayout { get; set; } = true;
    public bool OfflineAllowed { get; set; } = true;
    public string AdminUsersPath { get; set; } = "/api/admin/users";
    public string AuditApiPath { get; set; } = "/api/admin/audit";
    public string ContentHistoryApiPath { get; set; } = "/api/content/history";
    public string ConfigPath => @"C:\Users\Commander\AppData\Local\SCFA.ContentCenter\config.json";
    public string DataDirectory => @"C:\Users\Commander\AppData\Local\SCFA.ContentCenter";
}

sealed class PublicationBindingProbe
{
    public string[] Kinds { get; } = ["地图", "MOD"];
    public string SelectedKind { get; set; } = "地图";
    public List<LocalContentEntry> LocalItems { get; } =
    [
        new() { Kind = "地图", Name = "北境回声", Id = "northern_echo", Folder = "northern_echo", Version = "3", Files = 12, Valid = true, Detail = "结构校验通过" },
        new() { Kind = "地图", Name = "海岸防线", Id = "coastal_defense", Folder = "coastal_defense", Version = "2", Files = 9, Valid = true, Detail = "结构校验通过" }
    ];
    public LocalContentEntry? SelectedLocal { get; set; }
    public string Name { get; set; } = "北境回声";
    public string ReleaseVersion { get; set; } = "3";
    public string Author { get; set; } = "地图作者";
    public string Description { get; set; } = "适用于 Steam 版本的双人对战地图。";
    public string Category { get; set; } = "地图";
    public string TagsText { get; set; } = "2v2, 竞技";
    public string Status => "本地地图 2 项，其中 2 项可准备发布";
    public string SelectedSummary => "北境回声 · ID northern_echo · 游戏版本 3 · 12 个文件";
    public string OutputDirectory => "";
}

sealed class UsersBindingProbe
{
    public UsersBindingProbe()
    {
        ItemsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Items);
        SelectedItem = Items[0];
    }
    public List<AdminUserRecord> Items { get; } =
    [
        new() { Id = "usr-admin-01", Username = "Commander", DisplayName = "内容管理员", Email = "commander@example.com", RoleKey = "admin", RoleLabel = "管理员", Status = "active", ActiveSessions = 2, LastLoginAt = "2026-08-22 01:46", CreatedAt = "2026-06-01", Permissions = ["users.read", "users.write", "audit.read"] },
        new() { Id = "usr-publisher-08", Username = "Publisher08", DisplayName = "发布员 08", Email = "publisher08@example.com", RoleKey = "publisher", RoleLabel = "发布员", Status = "active", ActiveSessions = 1, LastLoginAt = "2026-08-21 22:31", CreatedAt = "2026-07-10", Permissions = [] },
        new() { Id = "usr-player-42", Username = "Player42", Email = "player42@example.com", RoleKey = "user", RoleLabel = "玩家", Status = "suspended", ActiveSessions = 0, LastLoginAt = "2026-08-18 19:20", CreatedAt = "2026-08-01" }
    ];
    public System.ComponentModel.ICollectionView ItemsView { get; }
    public AdminUserRecord? SelectedItem { get; set; }
    public string SearchText { get; set; } = "";
    public string SelectedRole { get; set; } = "admin";
    public string SelectedStatus { get; set; } = "active";
    public string Status => "真实用户列表已读取 · 3 人";
    public string[] Roles { get; } = ["user", "publisher", "admin", "super_admin"];
    public string[] UserStatuses { get; } = ["active", "disabled", "suspended"];
    public int TotalCount => 3;
    public int VisibleCount => 3;
    public int ActiveCount => 2;
    public int RestrictedCount => 1;
    public int SessionCount => 3;
    public string AccessLabel => "可修改角色与状态";
    public string SelectedUserLabel => "内容管理员";
    public string SelectedUserMetadata => "@Commander · commander@example.com";
    public string SelectedPermissionSummary => "3 项权限";
}

sealed class OperationsBindingProbe
{
    public OperationsBindingProbe()
    {
        AuditView = System.Windows.Data.CollectionViewSource.GetDefaultView(AuditItems);
        SelectedAudit = AuditItems[0];
    }
    public List<AuditRecord> AuditItems { get; } =
    [
        new() { Id = "audit-901", Time = "2026-08-22 01:45", Actor = "Commander", Action = "user.update", Target = "Player42", Result = "成功", Detail = "账号资料已更新。", Ip = "10.24.8.16" },
        new() { Id = "audit-900", Time = "2026-08-22 01:31", Actor = "SecurityBot", Action = "session.revoke", Target = "Player42", Result = "成功", Detail = "撤销 2 个活动会话。", Ip = "10.24.8.10" },
        new() { Id = "audit-899", Time = "2026-08-22 01:12", Actor = "Reviewer08", Action = "user.update", Target = "Player42", Result = "拒绝", Detail = "当前角色缺少 users.write 权限。", Ip = "10.24.8.28" }
    ];
    public System.ComponentModel.ICollectionView AuditView { get; }
    public AuditRecord? SelectedAudit { get; set; }
    public string AuditSearch { get; set; } = "";
    public string Status => "服务状态与真实审计日志已读取 · 3 条";
    public string ApiStatus => "正常 · API 8";
    public string UpdaterStatus => "已就绪 · 4.0.0-dev21.1";
    public string DirectStatus => "wss://direct.example.com";
    public string HealthLabel => "全部服务就绪";
    public string AuditAccessLabel => "审计读取权限已验证";
    public int VisibleAuditCount => 3;
    public int FailedAuditCount => 1;
    public string SelectedAuditTitle => "user.update · 成功";
    public string SelectedAuditMetadata => "Commander → Player42 · 2026-08-22 01:45";
    public string SelectedAuditDetail => "账号资料已更新。";
    public string LastRefresh => "2026-08-22 01:47:00";
}

sealed class SelectionCommandBindingProbe : INotifyPropertyChanged
{
    private LocalContentEntry? _selectedItem;

    public SelectionCommandBindingProbe(string root)
    {
        Items = [new LocalContentEntry { Kind = "地图", Name = "选择命令测试地图", Version = "1", Id = "selection-command-probe", Root = root, Folder = "selection-command-probe", Valid = true, Files = 1 }];
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        OpenFolderCommand = new RelayCommand(() => { }, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
        UninstallCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => SelectedItem is not null && Directory.Exists(SelectedItem.Root));
        DeleteItemCommand = new AsyncItemCommand<LocalContentEntry>(_ => Task.CompletedTask, item => Directory.Exists(item.Root));
        RefreshCommand = new AsyncRelayCommand(() => Task.CompletedTask);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public List<LocalContentEntry> Items { get; }
    public ICollectionView ItemsView { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncItemCommand<LocalContentEntry> DeleteItemCommand { get; }
    public string Title => "本地地图";
    public string Subtitle => "选择状态运行时测试";
    public string LibraryLabel => "LOCAL MAPS";
    public string Status => "扫描完成 · 有效 1 · 异常 0";
    public string SearchText { get; set; } = "";
    public int TotalCount => 1;
    public int VisibleCount => 1;
    public int ValidCount => 1;
    public int IssueCount => 0;
    public string SelectedState => SelectedItem is null ? "尚未选择内容" : "结构校验通过";
    public LocalContentEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedState)));
            OpenFolderCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }
}

sealed class CloudSkipBindingProbe
{
    public CloudSkipBindingProbe()
    {
        Items = [new CloudContentEntry { Kind = "地图", Id = "skip-ui-map", Name = "跳过按钮测试地图", Version = "1" }];
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ToggleSyncSkipCommand = new AsyncItemCommand<CloudContentEntry>(_ => Task.CompletedTask);
    }

    public List<CloudContentEntry> Items { get; }
    public ICollectionView ItemsView { get; }
    public AsyncItemCommand<CloudContentEntry> ToggleSyncSkipCommand { get; }
    public CloudContentEntry? SelectedItem { get; set; }
    public int TotalCount => Items.Count;
}

static class VisualTreeProbe
{
    public static T Find<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject =>
        FindAll<T>(root).First();

    public static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindAll<T>(child)) yield return descendant;
        }
    }
}

sealed class InterruptingStream(byte[] payload, int interruptAfter) : MemoryStream(payload, writable: false)
{
    private bool _interrupted;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_interrupted && Position >= interruptAfter)
        {
            _interrupted = true;
            return ValueTask.FromException<int>(new IOException("模拟网络中断"));
        }
        var remaining = interruptAfter - Position;
        if (!_interrupted && remaining < buffer.Length) buffer = buffer[..(int)remaining];
        return base.ReadAsync(buffer, cancellationToken);
    }
}
