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

if (args.Contains("--submission-ui-binding-smoke", StringComparer.OrdinalIgnoreCase))
{
    Exception? bindingError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var application = new SCFA.ContentCenter.App();
            application.InitializeComponent();
            var view = new SCFA.ContentCenter.Views.SubmissionsView { DataContext = new ReadOnlySubmissionBindingProbe() };
            var host = new System.Windows.Window
            {
                Content = view,
                Width = 900,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
                Opacity = 0
            };
            host.Show();
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            host.Close();
            application.Shutdown();
        }
        catch (Exception ex) { bindingError = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (bindingError is null) Console.WriteLine("PASS  投稿页面运行时可绑定只读内容版本而不弹出异常");
    else Console.Error.WriteLine("FAIL  投稿页面运行时绑定异常：" + bindingError);
    Environment.Exit(bindingError is null ? 0 : 1);
    return;
}

if (args.Contains("--submission-ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var width = args.Length > 1 && double.TryParse(args[^2], out var parsedWidth) ? parsedWidth : 1200;
    var height = args.Length > 0 && double.TryParse(args[^1], out var parsedHeight) ? parsedHeight : 800;
    var uiThread = new Thread(() =>
    {
        var application = new SCFA.ContentCenter.App();
        application.InitializeComponent();
        var view = new SCFA.ContentCenter.Views.SubmissionsView { DataContext = new ReadOnlySubmissionBindingProbe() };
        var root = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)application.Resources["AppBackgroundBrush"],
            Padding = new System.Windows.Thickness(26),
            Child = view
        };
        var host = new System.Windows.Window
        {
            Title = "SCFA Dev19 投稿视觉预览",
            Content = root,
            Width = width,
            Height = height,
            MinWidth = 900,
            MinHeight = 640,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
        };
        application.MainWindow = host;
        host.Show();
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "ui-dev19", $"submissions-{(int)width}x{(int)height}.png");
        using (var stream = File.Create(output)) encoder.Save(stream);
        host.Close();
        application.Shutdown();
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
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
                (new SCFA.ContentCenter.Views.ReviewView(), new ReviewBindingProbe()),
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
    if (bindingError is null) Console.WriteLine("PASS  三个管理员页面可在真实 WPF 布局中完成绑定");
    else Console.Error.WriteLine("FAIL  管理员页面运行时绑定异常：" + bindingError);
    Environment.Exit(bindingError is null ? 0 : 1);
    return;
}

if (args.Contains("--admin-ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var flagIndex = Array.FindIndex(args, value => value.Equals("--admin-ui-preview", StringComparison.OrdinalIgnoreCase));
    var page = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1].ToLowerInvariant() : "review";
    var width = args.Length > 1 && double.TryParse(args[^2], out var parsedWidth) ? parsedWidth : 1200;
    var height = args.Length > 0 && double.TryParse(args[^1], out var parsedHeight) ? parsedHeight : 800;
    var uiThread = new Thread(() =>
    {
        var application = new SCFA.ContentCenter.App();
        application.InitializeComponent();
        var (view, probe) = page switch
        {
            "users" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.UsersView(), (object)new UsersBindingProbe()),
            "operations" => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.OperationsView(), (object)new OperationsBindingProbe()),
            _ => ((System.Windows.Controls.UserControl)new SCFA.ContentCenter.Views.ReviewView(), (object)new ReviewBindingProbe())
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
Check(strictDecision.ShouldInstall && strictDecision.Reason.Contains("严格一致性", StringComparison.Ordinal), "同版本但清单缺少内容指纹时仍执行严格整目录同步");

Check(SafeArchive.ValidateRelativePath("map/file.scmap").EndsWith(Path.Combine("map", "file.scmap"), StringComparison.Ordinal), "安全相对路径通过");
CheckThrows(() => SafeArchive.ValidateRelativePath("../outside.txt"), "拒绝目录穿越");
CheckThrows(() => SafeArchive.ValidateRelativePath("map/CON.txt"), "拒绝 Windows 设备名");
CheckThrows(() => SafeArchive.ValidateRelativePath("map/file. "), "拒绝危险尾部字符");
CheckArgumentThrows(() => GamePathService.ValidateContentDirectoryPair(@"C:\SCFA\Maps", @"C:\SCFA\Maps\Mods"), "拒绝地图与 MOD 目录互相嵌套");

var uiRoot = Path.Combine(Directory.GetCurrentDirectory(), "src", "SCFA.ContentCenter");
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
Check(readOnlyEditorBindings.Length >= 3 && readOnlyEditorBindings.All(binding => binding.Contains("Mode=OneWay", StringComparison.Ordinal)), "只读文本框不会向只读视图模型属性回写");
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
      "投稿与维护工作区具备统一流程、状态和预览组件");
var dev19Pages = new[] { "SubmissionsView.xaml", "BackupsView.xaml", "CloudHistoryView.xaml", "DiagnosticsView.xaml", "UpdatesView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev19Pages[0].Contains("DraftCompletion", StringComparison.Ordinal) && dev19Pages[0].Contains("SubmissionCount", StringComparison.Ordinal) &&
      dev19Pages[1].Contains("SelectedIntegrity", StringComparison.Ordinal) && dev19Pages[1].Contains("TotalSizeText", StringComparison.Ordinal) &&
      dev19Pages[2].Contains("SelectedVersionLabel", StringComparison.Ordinal) && dev19Pages[2].Contains("VersionCount", StringComparison.Ordinal) &&
      dev19Pages[3].Contains("HealthLabel", StringComparison.Ordinal) && dev19Pages[3].Contains("ProblemCount", StringComparison.Ordinal) &&
      dev19Pages[4].Contains("SecurityStateLabel", StringComparison.Ordinal) && dev19Pages[4].Contains("StageStateLabel", StringComparison.Ordinal),
      "投稿、备份、云历史、诊断和更新页面均使用真实状态与统计绑定");
Check(dev19Pages[2].Contains("SearchText", StringComparison.Ordinal) && dev19Pages[2].Contains("FilteredContentCount", StringComparison.Ordinal), "云端历史提供实时搜索、清除与结果计数");
Check(dev19Pages[2].Contains("ContentCount, Mode=OneWay", StringComparison.Ordinal) && dev19Pages[2].Contains("FilteredContentCount, Mode=OneWay", StringComparison.Ordinal) && dev19Pages[2].Contains("VersionCount, Mode=OneWay", StringComparison.Ordinal), "云端历史只读统计使用单向绑定");
var dev20Pages = new[] { "ReviewView.xaml", "UsersView.xaml", "OperationsView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev20Pages[0].Contains("QueueCount", StringComparison.Ordinal) && dev20Pages[0].Contains("SelectedPackage", StringComparison.Ordinal) &&
      dev20Pages[1].Contains("RestrictedCount", StringComparison.Ordinal) && dev20Pages[1].Contains("SelectedPermissionSummary", StringComparison.Ordinal) &&
      dev20Pages[2].Contains("AuditView", StringComparison.Ordinal) && dev20Pages[2].Contains("SelectedAuditDetail", StringComparison.Ordinal),
      "审核、用户和审计页面均使用真实队列、身份与审计状态绑定");
var dev21Pages = new[] { "SetupView.xaml", "SettingsView.xaml" }
    .Select(name => File.ReadAllText(Path.Combine(uiRoot, "Views", name))).ToArray();
Check(dev21Pages[0].Contains("SetupProgress", StringComparison.Ordinal) && dev21Pages[0].Contains("ContentStateLabel", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("ConfigurationScore", StringComparison.Ordinal) && dev21Pages[1].Contains("ValidationSummary", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("DraftStateLabel", StringComparison.Ordinal) && dev21Pages[1].Contains("Header=\"服务器路径\"", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("AdvancedSettingsVisibility", StringComparison.Ordinal) && dev21Pages[1].Contains("SettingsAudienceHint", StringComparison.Ordinal),
      "首次设置与软件设置页面使用真实进度、草稿、健康状态和分组配置绑定");
Check(dev21Pages[1].Contains("Header=\"启动与更新\"", StringComparison.Ordinal) &&
      dev21Pages[1].Contains("Header=\"存储与数据\"", StringComparison.Ordinal) &&
      dev19Pages[0].Contains("资料完整度", StringComparison.Ordinal) &&
      dev19Pages[2].Contains("刷新内容列表", StringComparison.Ordinal) &&
      dev19Pages[2].Contains("查询历史版本", StringComparison.Ordinal),
      "客户界面使用清晰的设置、投稿完整度和云端历史操作文案");

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
    settingsDraft.SubmissionApiPath = "/v1/submissions/";
    CheckDirectoryThrows(() => SettingsValidator.ValidateDirectories(settingsDraft, pathService, createDirectories: false), "设置拒绝不存在的玩家内容根目录且不会自动创建");
    Check(!Directory.Exists(settingsMaps) && !Directory.Exists(settingsMods), "目录验证失败后仍不会创建目录或写盘");
    Directory.CreateDirectory(settingsMaps);
    Directory.CreateDirectory(settingsMods);
    var checkedDirectories = SettingsValidator.ValidateDirectories(settingsDraft, pathService, createDirectories: false);
    Check(checkedDirectories.GameRoot == settingsGameRoot, "设置接受用户选择的已有 Maps/Mods 目录");
    var normalizedSettings = SettingsValidator.ValidateAndNormalize(settingsDraft, pathService, createDirectories: true);
    Check(Directory.Exists(settingsMaps) && Directory.Exists(settingsMods), "保存设置只使用已有玩家目录，不另建默认路径");
    Check(normalizedSettings.Root == "scfa" && normalizedSettings.UpdateChannel == "developer" && normalizedSettings.SubmissionApiPath == "/v1/submissions", "设置保存前统一规范化 COS、更新通道和服务器路径");
    Check(settingsDraft.Root == "/scfa/" && settingsDraft.UpdateChannel == "dev", "设置校验不会反向修改编辑草稿");
    var nestedSettings = ConfigService.Clone(settingsDraft);
    nestedSettings.ModsDir = Path.Combine(settingsMaps, "NestedMods");
    CheckArgumentThrows(() => SettingsValidator.ValidateDirectories(nestedSettings, pathService, createDirectories: false), "设置拒绝互相嵌套的 Maps/Mods 目录");
    var invalidCosSettings = ConfigService.Clone(settingsDraft);
    invalidCosSettings.Bucket = "INVALID_BUCKET";
    CheckArgumentThrows(() => SettingsValidator.ValidateCosSettings(invalidCosSettings), "设置拒绝不安全的 COS Bucket");
    CheckArgumentThrows(() => SettingsValidator.NormalizeApiPath("https://outside.example/submissions", "投稿 API"), "设置拒绝跨主机服务器功能路径");
    CheckArgumentThrows(() => SettingsValidator.NormalizeApiPath("/v1/submissions?redirect=outside", "投稿 API"), "设置拒绝服务器功能路径携带查询或跳转参数");
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
    var packagePayload = CreateMapPackage("recent_map");
    using var packageClient = new HttpClient(new StaticPackageHandler(packagePayload)) { Timeout = Timeout.InfiniteTimeSpan };
    var packageCloud = new CloudCatalogService(config, packageClient);
    var log = new LogService();
    var tasks = new TaskService();
    var backups = new BackupService(pathService, log);
    var localContent = new LocalContentService(pathService, log);
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

    var submissionEntry = await localContent.AnalyzeDirectoryAsync(Path.Combine(mapsRoot, "recent_map"));
    var legacyMapRoot = Path.Combine(mapsRoot, "legacy_map");
    Directory.CreateDirectory(legacyMapRoot);
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map.scmap"), "legacy-map");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_save.lua"), "Scenario = {}");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_script.lua"), "function OnPopulate() end");
    await File.WriteAllTextAsync(Path.Combine(legacyMapRoot, "legacy_map_scenario.lua"), "name = \"Legacy Map\"\nversion = 3\nmap = \"/maps/legacy_map/legacy_map.scmap\"\nsave = \"/maps/legacy_map/legacy_map_save.lua\"\nscript = \"/maps/legacy_map/legacy_map_script.lua\"\n");
    var legacyMap = await localContent.AnalyzeDirectoryAsync(legacyMapRoot);
    Check(legacyMap.Valid && legacyMap.Version == "3", "旧版标准地图 scenario.lua 的 version 字段可以正常识别");
    var validDraft = new SubmissionDraft
    {
        Name = "回归测试地图",
        Version = submissionEntry.Version,
        Author = "Regression Mapper",
        Description = "这是一段满足长度要求的投稿说明，用于验证投稿表单。",
        Category = "竞技",
        TagsText = "2v2，平衡; 2V2"
    };
    var validatedDraft = SubmissionValidator.Validate(validDraft, submissionEntry);
    Check(validatedDraft.Tags.SequenceEqual(["2v2", "平衡"]), "投稿标签支持多种分隔符并忽略重复项");
    var mismatchedDraft = CloneDraft(validDraft);
    mismatchedDraft.Version = "999";
    CheckThrows(() => SubmissionValidator.Validate(mismatchedDraft, submissionEntry), "投稿版本必须与实际内容版本一致");
    var shortDescriptionDraft = CloneDraft(validDraft);
    shortDescriptionDraft.Description = "太短";
    CheckThrows(() => SubmissionValidator.Validate(shortDescriptionDraft, submissionEntry), "投稿说明拒绝低于最小长度");
    var tooManyTagsDraft = CloneDraft(validDraft);
    tooManyTagsDraft.TagsText = string.Join(',', Enumerable.Range(1, 11).Select(i => "tag" + i));
    CheckThrows(() => SubmissionValidator.Validate(tooManyTagsDraft, submissionEntry), "投稿标签数量有安全上限");

    var progressValues = new List<int>();
    var uploadPayload = Enumerable.Range(0, 384 * 1024).Select(i => (byte)(i % 239)).ToArray();
    using (var progressContent = new ProgressStreamContent(new MemoryStream(uploadPayload, writable: false), uploadPayload.Length, new InlineProgress(progressValues.Add)))
    using (var uploaded = new MemoryStream())
    {
        await progressContent.CopyToAsync(uploaded);
        Check(uploaded.ToArray().SequenceEqual(uploadPayload), "投稿上传进度流不会改变包内容");
        Check(progressValues.Count > 1 && progressValues[0] == 0 && progressValues[^1] == 100, "投稿上传进度从 0 准确推进到 100");
    }

    var previewPath = Path.Combine(configDirectory, "submission-preview.png");
    CreatePreviewPng(previewPath, 320, 180);
    var dimensions = SubmissionService.ValidatePreviewImage(previewPath);
    Check(dimensions == (320, 180), "投稿预览图验证真实格式和最低尺寸");
    var fakeJpegPath = Path.Combine(configDirectory, "fake-preview.jpg");
    File.Copy(previewPath, fakeJpegPath);
    CheckThrows(() => SubmissionService.ValidatePreviewImage(fakeJpegPath), "投稿预览图拒绝扩展名与真实格式不一致");

    using var submissionAuth = new AuthApiClient("http://localhost:18080", "");
    var submissions = new SubmissionService(config, submissionAuth, pathService, localContent, tasks, log);
    validDraft.PreviewPath = previewPath;
    await submissions.SaveDraftAsync(submissionEntry, validDraft);
    var loadedDraft = await submissions.LoadDraftAsync(submissionEntry);
    Check(loadedDraft.Author == validDraft.Author && loadedDraft.Description == validDraft.Description && loadedDraft.PreviewPath == previewPath, "投稿草稿可安全持久化并恢复元数据与预览图");
    await submissions.DeleteDraftAsync(submissionEntry);
    var clearedDraft = await submissions.LoadDraftAsync(submissionEntry, "默认作者");
    Check(clearedDraft.Description.Length == 0 && clearedDraft.Author == "默认作者", "清除投稿草稿后恢复内容默认值");

    var removableRoot = Path.Combine(mapsRoot, "map_to_remove");
    Directory.CreateDirectory(removableRoot);
    await File.WriteAllTextAsync(Path.Combine(removableRoot, "marker.txt"), "original content");
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
    Check(uninstallBackup is not null && File.Exists(Path.Combine(uninstallBackup.ContentRoot, "marker.txt")), "卸载前会留下可恢复的完整备份");
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
var selectionUiThread = new Thread(() =>
{
    var selectionRoot = Path.Combine(Path.GetTempPath(), "scfa_selection_ui_" + Guid.NewGuid().ToString("N"));
    SCFA.ContentCenter.App? application = null;
    System.Windows.Window? host = null;
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
        var uninstallButton = buttons.Single(button => Equals(button.Content, "卸载并自动备份"));
        grid.SelectedItem = probe.Items[0];
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        selectionCommandUiPassed = ReferenceEquals(probe.SelectedItem, probe.Items[0]) &&
                                   probe.SelectedState == "结构校验通过" &&
                                   openButton.IsEnabled && uninstallButton.IsEnabled;
    }
    catch (Exception ex) { selectionCommandUiError = ex; }
    finally
    {
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
Check(selectionCommandUiPassed, "真实 WPF 本地内容页面选中一行后打开与卸载按钮立即可用");

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

void AddText(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(content);
}

SubmissionDraft CloneDraft(SubmissionDraft value) => new()
{
    ContentKey = value.ContentKey,
    Name = value.Name,
    Version = value.Version,
    Author = value.Author,
    Description = value.Description,
    Category = value.Category,
    TagsText = value.TagsText,
    PreviewPath = value.PreviewPath,
    SavedAt = value.SavedAt
};

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

sealed class InlineProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}

sealed class ReadOnlySubmissionBindingProbe
{
    public string DraftVersion => "1";
    public string DraftName { get; set; } = "示例地图：北境回声";
    public string DraftAuthor { get; set; } = "玩家作者";
    public string DraftCategory { get; set; } = "竞技";
    public string DraftTags { get; set; } = "2v2, 平衡";
    public string DraftDescription { get; set; } = "为双人对战设计的对称地图，包含清晰的资源分布与多条进攻路线。";
    public int DraftCompletion => 100;
    public int DraftCompletedCount => 6;
    public string DraftRequirementHint => "资料已完整，可以校验并投稿";
    public int DescriptionCount => DraftDescription.Length;
    public int TagCount => 2;
    public int LocalCount => LocalItems.Count;
    public int SubmissionCount => 2;
    public string SelectedContentLabel => "地图 · 北境回声 · 1";
    public string PreviewSummary => "未选择预览图（可选，PNG/JPEG，最大5MB）";
    public string Status => "草稿仅保存在本机，提交前会执行完整安全校验。";
    public IReadOnlyList<string> Categories { get; } = ["地图", "MOD", "竞技", "合作", "AI", "其他"];
    public List<LocalContentEntry> LocalItems { get; } =
    [
        new() { Kind = "地图", Name = "北境回声", Version = "1", Bytes = 18 * 1024 * 1024, Valid = true },
        new() { Kind = "MOD", Name = "战术标记增强", Version = "2.4", Bytes = 3 * 1024 * 1024, Valid = true }
    ];
    public LocalContentEntry? SelectedLocal { get; set; }
    public List<SubmissionRecord> MyItems { get; } = [];
    public SubmissionRecord? SelectedSubmission { get; set; }
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
    public string ServerStatus => "投稿、用户与审计路径均可访问";
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
    public string SubmissionApiPath { get; set; } = "/api/submissions";
    public string SubmissionAdminPath { get; set; } = "/api/admin/submissions";
    public string AdminUsersPath { get; set; } = "/api/admin/users";
    public string AuditApiPath { get; set; } = "/api/admin/audit";
    public string ContentHistoryApiPath { get; set; } = "/api/content/history";
    public string ConfigPath => @"C:\Users\Commander\AppData\Local\SCFA.ContentCenter\config.json";
    public string DataDirectory => @"C:\Users\Commander\AppData\Local\SCFA.ContentCenter";
}

sealed class ReviewBindingProbe
{
    public ReviewBindingProbe() => SelectedItem = Items[0];
    public List<SubmissionRecord> Items { get; } =
    [
        new() { Id = "sub-1008", Kind = "地图", ContentId = "northern-echo", Name = "北境回声", Version = "1.2", Submitter = "MapperOne", Author = "MapperOne", Category = "竞技", Tags = ["2v2", "平衡"], Description = "对称资源分布与多路线进攻设计，已完成本地结构和双哈希校验。", Files = 28, Size = 18 * 1024 * 1024, CreatedAt = "2026-08-22 01:42", Sha256 = new string('A', 64), ContentSha256 = new string('B', 64) },
        new() { Id = "sub-1007", Kind = "MOD", ContentId = "tactical-markers", Name = "战术标记增强", Version = "2.4", Submitter = "ModPilot", Author = "ModPilot", Category = "界面", Files = 11, Size = 3 * 1024 * 1024, CreatedAt = "2026-08-22 01:18" }
    ];
    public SubmissionRecord? SelectedItem { get; set; }
    public string ReviewMessage { get; set; } = "内容结构与说明完整，等待最终决定。";
    public string Status => "审核队列已读取 · 2 项";
    public string PermissionLabel => "审核权限已验证";
    public int QueueCount => 2;
    public int MapCount => 1;
    public int ModCount => 1;
    public string TotalSizeText => "21.0 MB";
    public int ReviewMessageCount => ReviewMessage.Length;
    public string SelectedTitle => SelectedItem?.Name ?? "请选择一条投稿";
    public string SelectedMetadata => "地图 · 1.2 · 投稿人 MapperOne";
    public string SelectedPackage => "28 个文件 · 18.0 MB";
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
        new() { Id = "usr-review-08", Username = "Reviewer08", DisplayName = "审核员 08", Email = "reviewer08@example.com", RoleKey = "reviewer", RoleLabel = "审核员", Status = "active", ActiveSessions = 1, LastLoginAt = "2026-08-21 22:31", CreatedAt = "2026-07-10", Permissions = ["submissions.review"] },
        new() { Id = "usr-player-42", Username = "Player42", Email = "player42@example.com", RoleKey = "user", RoleLabel = "玩家", Status = "suspended", ActiveSessions = 0, LastLoginAt = "2026-08-18 19:20", CreatedAt = "2026-08-01" }
    ];
    public System.ComponentModel.ICollectionView ItemsView { get; }
    public AdminUserRecord? SelectedItem { get; set; }
    public string SearchText { get; set; } = "";
    public string SelectedRole { get; set; } = "admin";
    public string SelectedStatus { get; set; } = "active";
    public string Status => "真实用户列表已读取 · 3 人";
    public string[] Roles { get; } = ["user", "reviewer", "publisher", "admin", "super_admin"];
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
        new() { Id = "audit-901", Time = "2026-08-22 01:45", Actor = "Commander", Action = "submission.approve", Target = "北境回声 1.2", Result = "成功", Detail = "投稿已通过审核并进入服务器发布队列。", Ip = "10.24.8.16" },
        new() { Id = "audit-900", Time = "2026-08-22 01:31", Actor = "SecurityBot", Action = "session.revoke", Target = "Player42", Result = "成功", Detail = "撤销 2 个活动会话。", Ip = "10.24.8.10" },
        new() { Id = "audit-899", Time = "2026-08-22 01:12", Actor = "Reviewer08", Action = "user.update", Target = "Player42", Result = "拒绝", Detail = "当前角色缺少 users.write 权限。", Ip = "10.24.8.28" }
    ];
    public System.ComponentModel.ICollectionView AuditView { get; }
    public AuditRecord? SelectedAudit { get; set; }
    public string AuditSearch { get; set; } = "";
    public string Status => "服务状态与真实审计日志已读取 · 3 条";
    public string ApiStatus => "正常 · API 8";
    public string SubmissionStatus => "已就绪";
    public string UpdaterStatus => "已就绪 · 4.0.0-dev21.1";
    public string DirectStatus => "wss://direct.example.com";
    public string HealthLabel => "全部服务就绪";
    public string AuditAccessLabel => "审计读取权限已验证";
    public int VisibleAuditCount => 3;
    public int FailedAuditCount => 1;
    public string SelectedAuditTitle => "submission.approve · 成功";
    public string SelectedAuditMetadata => "Commander → 北境回声 1.2 · 2026-08-22 01:45";
    public string SelectedAuditDetail => "投稿已通过审核并进入服务器发布队列。";
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
        RefreshCommand = new AsyncRelayCommand(() => Task.CompletedTask);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public List<LocalContentEntry> Items { get; }
    public ICollectionView ItemsView { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
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
