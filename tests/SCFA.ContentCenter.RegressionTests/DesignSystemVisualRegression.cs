using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SCFA.ContentCenter;
using SCFA.ContentCenter.DesignSystem;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;
using SCFA.ContentCenter.ViewModels;
using SCFA.ContentCenter.Views;

internal static class DesignSystemVisualRegression
{
    internal static void Run(Action<bool, string> check, bool baseline = false)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var previousConfig = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR");
            var previousData = Environment.GetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR");
            App? app = null;
            MainWindow? main = null;
            LoginWindow? login = null;
            try
            {
                var isolated = Path.Combine(Path.GetTempPath(), "scfa_design_" + Guid.NewGuid().ToString("N"));
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", isolated);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", Path.Combine(isolated, "data"));
                app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var services = ServiceRegistry.CreateAsync().GetAwaiter().GetResult();
                services.CurrentUser = new UserInfo { Username = "视觉评审", DisplayName = "视觉评审", RoleKey = "super_admin" };
                services.Config.Current.AutoLogin = false;
                services.Config.Current.AutoCheckUpdates = false;
                services.Auth.Reconfigure("https://127.0.0.1:1", "");
                typeof(App).GetProperty("Services")!.SetValue(null, services);
                main = new MainWindow { Opacity = 0, ShowInTaskbar = false };
                var probe = new DashboardProbe();
                var dashboard = new DashboardView { DataContext = probe };
                typeof(MainViewModel).GetProperty("CurrentPage")!.SetValue(main.DataContext, dashboard);
                typeof(MainViewModel).GetProperty("CurrentPageTitle")!.SetValue(main.DataContext, "总览");
                typeof(MainViewModel).GetProperty("CurrentPageSubtitle")!.SetValue(main.DataContext, "本地内容与服务状态概览");
                ((RadioButton)main.FindName("HomeButton")).IsChecked=true;

                services.Config.Current.LastLoginAccount="测试账号";
                services.Config.Current.RememberLoginPassword=true;
                services.Config.Current.LoginAccounts=[new LoginAccountRecord {Account="测试账号",PasswordEncrypted=LoginCredentialProtector.Protect("fixture-password")}];
                login = new LoginWindow { Opacity = 0, ShowInTaskbar = false };
                ((ComboBox)login.FindName("AccountBox")).Text = "测试账号";
                ((PasswordBox)login.FindName("PasswordBox")).Password = "fixture-password";

                var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "design-system-review", baseline ? "before" : "after");
                Directory.CreateDirectory(output);
                var sizes = baseline ? new[] { (1440, 900) } : new[] { (1280, 720), (1366, 768), (1440, 900), (1920, 1080), (2560, 1440) };
                foreach (var (width, height) in sizes)
                {
                    Capture(main, width, height, Path.Combine(output, $"dashboard-{width}x{height}.png"));
                    Capture(login, width, height, Path.Combine(output, $"login-{width}x{height}.png"));
                    var submit = (Button)login.FindName("SubmitButton");
                    var point = submit.TranslatePoint(new Point(), (FrameworkElement)login.Content);
                    check(point.X >= 0 && point.Y >= 0 && point.X + submit.ActualWidth <= width &&
                          point.Y + submit.ActualHeight <= height, $"登录主要操作在{width}x{height}完整可见");
                    check(((FrameworkElement)main.FindName("ContentHost")).ActualWidth <= (baseline ? width : 1560),
                        $"首页内容在{width}x{height}受宽度约束");
                }
                if (!baseline)
                {
                    var cases = new[] {
                        ("loading","正在扫描本地地图与 MOD…",0,0,0,"在线模式"),
                        ("empty","本地内容读取完成",0,0,0,"在线模式"),
                        ("offline","当前为离线模式，无法检查账号服务。",16,6,0,"离线模式"),
                        ("error","读取失败：连接不可用",16,6,0,"在线模式"),
                        ("warning","本地内容读取完成 · 2 项需要检查",16,6,2,"在线模式"),
                        ("success","本地内容读取完成 · 未发现结构问题",16,6,0,"在线模式"),
                        ("syncing","同步任务已交给同步中心",16,6,0,"在线模式"),
                        ("disabled","当前操作不可用",16,6,0,"在线模式"),
                        ("partial","部分失败：3项完成，1项失败",16,6,0,"在线模式")
                    };
                    foreach (var (name,status,maps,mods,issues,mode) in cases)
                    {
                        dashboard.DataContext = new DashboardProbe { Status=status, MapCount=maps, ModCount=mods,
                            IssueCount=issues, Mode=mode, RunningTaskCount=name=="syncing"?1:0, Enabled=name!="disabled" && name!="loading" };
                        Capture(main,1440,900,Path.Combine(output,$"dashboard-state-{name}.png"));
                        var notice=(StateNotice)dashboard.FindName("DashboardState");
                        var expected=name switch {"loading"=>UiState.Loading,"empty"=>UiState.Empty,"offline"=>UiState.Offline,
                            "error"=>UiState.Error,"warning"=>UiState.Warning,"success"=>UiState.Success,
                            "syncing"=>UiState.Syncing,"disabled"=>UiState.Disabled,_=>UiState.PartialFailure};
                        check(notice.State==expected && notice.Message==status, "首页真实绑定显示正确状态："+name);
                        check(((Button)dashboard.FindName("SyncAction")).IsEnabled==(name!="disabled" && name!="loading"),
                            "首页保留命令可用性："+name);
                    }
                    ComponentGallery(output,check);
                    var loginOutput=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","login-cinematic-review","after");
                    Directory.CreateDirectory(loginOutput);
                    foreach(var (w,h) in new[]{(1280,720),(1366,768),(1440,900),(1575,999),(1920,1080),(2560,1440)})
                    {
                        Capture(login,w,h,Path.Combine(loginOutput,$"login-{w}x{h}.png"));
                        var root=(FrameworkElement)login.Content;
                        var version=(TextBlock)login.FindName("VersionText");
                        var versionPoint=version.TranslatePoint(new Point(),root);
                        check(version.Text==SCFA.ContentCenter.Core.AppVersion.Display && versionPoint.X<w/2 &&
                            versionPoint.Y>h*0.8 && versionPoint.Y+version.ActualHeight<=h,
                            $"真实版本在{w}x{h}左下角完整可见");
                        var account=(ComboBox)login.FindName("AccountBox");
                        var offline=(Button)login.FindName("OfflineButton");
                        var p=offline.TranslatePoint(new Point(),root);
                        check(account.ActualWidth>=280 && p.Y>=0 && p.Y+offline.ActualHeight<=h,
                            $"账号输入和离线操作在{w}x{h}完整可用");
                    }
                    ((Button)login.FindName("SwitchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Capture(login,1280,720,Path.Combine(loginOutput,"login-register-1280x720.png"));
                    check(((StackPanel)login.FindName("EmailPanel")).Visibility==Visibility.Visible &&
                        ((TextBlock)login.FindName("ModeTitle")).Text=="注册 SCFA 账号",
                        "视觉改造保留原注册表单切换");
                    ((Button)login.FindName("SwitchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((TextBlock)login.FindName("StatusText")).Text="登录失败：网络连接不可用，请稍后重试。";
                    ((Border)login.FindName("StatusPanel")).Visibility=Visibility.Visible;
                    Capture(login,1280,720,Path.Combine(loginOutput,"login-error-1280x720.png"));
                    ((Border)login.FindName("StatusPanel")).Visibility=Visibility.Collapsed;
                    var servicesConfig = ConfigService.Clone(services.Config.Current);
                    foreach(var size in new[]{(1280,720),(2560,1440)})
                        check(((FrameworkElement)main.FindName("ContentHost")).MaxWidth==1560, "宽屏最大内容宽度保持1560");
                    check(services.Config.Current.LoginAccounts.Count == servicesConfig.LoginAccounts.Count,
                        "视觉检查不改变账号保存配置");
                }
                check(true, "实际WPF登录/首页截图已保存");
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                login?.PrepareForWindowHandoff(); login?.Close();
                main?.PrepareForWindowHandoff(); main?.Close(); app?.Shutdown();
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_CONFIG_DIR", previousConfig);
                Environment.SetEnvironmentVariable("SCFA_CONTENT_HUB_DATA_DIR", previousData);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("设计系统视觉检查超时");
        check(error is null, "设计系统视觉检查完成"+(error is null?"":": "+error));
    }

    private static void Capture(Window window, int width, int height, string path)
    {
        window.Width=width; window.Height=height;
        var root=(FrameworkElement)window.Content;
        // Detach the real content from the native monitor-sized HWND before off-screen layout.
        // This avoids silently cropping large logical viewports to the developer's physical monitor.
        window.Content=null;
        root.DataContext=window.DataContext;
        root.Width=width; root.Height=height;
        var host=new Decorator {Child=root,Width=width,Height=height};
        TextElement.SetFontFamily(host,(FontFamily)Application.Current.FindResource("FontUI"));
        host.Measure(new Size(width,height)); host.Arrange(new Rect(0,0,width,height)); host.UpdateLayout();
        var until=DateTime.UtcNow.AddMilliseconds(240);
        while(DateTime.UtcNow<until)
        {
            var frame=new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        host.UpdateLayout();
        if (Math.Abs(root.ActualWidth-width)>0.5 || Math.Abs(root.ActualHeight-height)>0.5)
            throw new InvalidOperationException("离屏内容尺寸与目标窗口不一致");
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(host);
        var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream=File.Create(path)) png.Save(stream);
        host.Child=null;
        window.Content=root;
    }

    private static void ComponentGallery(string output,Action<bool,string> check)
    {
        var stack=new StackPanel {Margin=(Thickness)Application.Current.FindResource("PagePadding")};
        stack.Children.Add(new TextBlock {Text="SCFA 基础组件",Style=(Style)Application.Current.FindResource("TypePageTitle")});
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=(Thickness)Application.Current.FindResource("GapAbove24")};
        foreach(var key in new[]{"PrimaryButton","SecondaryButton","GhostButton","DangerButton"})
            actions.Children.Add(new Button {Content=key switch {"PrimaryButton"=>"主要操作","SecondaryButton"=>"次要操作","GhostButton"=>"轻量操作",_=>"危险操作"},
                Style=(Style)Application.Current.FindResource(key),Margin=(Thickness)Application.Current.FindResource("GapRight12")});
        var disabled=new Button {Content="暂不可用",IsEnabled=false,Style=(Style)Application.Current.FindResource("PrimaryButton")};
        actions.Children.Add(disabled);stack.Children.Add(actions);
        var input=new TextBox {Text="默认输入",Margin=(Thickness)Application.Current.FindResource("GapAbove16")};stack.Children.Add(input);
        var invalid=new TextBox {Margin=(Thickness)Application.Current.FindResource("GapAbove12")};
        invalid.SetBinding(TextBox.TextProperty,new Binding(nameof(DashboardProbe.Mode)){Source=new DashboardProbe(),Mode=BindingMode.TwoWay});
        Validation.MarkInvalid(invalid.GetBindingExpression(TextBox.TextProperty),new ValidationError(new RequiredRule(),invalid.GetBindingExpression(TextBox.TextProperty),"输入错误",null));
        stack.Children.Add(invalid);
        var grids=new List<DataGrid>();
        foreach(var density in Enum.GetValues<ListDensity>())
        {
            var grid=new DataGrid {Height=160,Margin=(Thickness)Application.Current.FindResource("GapAbove16"),AutoGenerateColumns=false,
                ItemsSource=new[]{new GalleryRow("地图 / MOD",density.ToString()),new GalleryRow("任务 / 版本","统一数据行")} };
            grid.Columns.Add(new DataGridTextColumn {Header="内容",Binding=new Binding(nameof(GalleryRow.Name)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            grid.Columns.Add(new DataGridTextColumn {Header="密度",Binding=new Binding(nameof(GalleryRow.Detail)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            Density.SetMode(grid,density);grids.Add(grid);stack.Children.Add(grid);
        }
        var dialog=new Border {Style=(Style)Application.Current.FindResource("DialogSurface"),Margin=(Thickness)Application.Current.FindResource("GapAbove16"),
            Child=new TextBlock {Text="浮层 · 纯深色表面，不使用背景图片",Style=(Style)Application.Current.FindResource("TypeBody")}};
        stack.Children.Add(dialog);
        var gallery=new Window {Content=new Border {Background=(Brush)Application.Current.FindResource("BackgroundDeepBrush"),Child=stack}};
        Capture(gallery,1280,900,Path.Combine(output,"foundation-components.png"));
        check(grids.Select(g=>g.RowHeight).SequenceEqual(new double[]{40,48,32}),"真实表格组件支持默认40、舒适48、紧凑32行高");
        var row=(DataGridRow)grids[2].ItemContainerGenerator.ContainerFromIndex(0);
        check(row is not null && row.ActualHeight==32,"紧凑表格实际数据行按32px布局："+(row?.ActualHeight.ToString()??"未生成"));
        var border=(Border)invalid.Template.FindName("InputBorder",invalid);
        check(Equals(border.BorderBrush,Application.Current.FindResource("DangerBrush")),"输入错误使用语义红色且没有强光");
        check(((Border)disabled.Template.FindName("ButtonBorder",disabled)).Opacity==(double)Application.Current.FindResource("OpacityDisabled"),"禁用按钮遵守全局透明度");
        check(((SolidColorBrush)dialog.Background).Color.A>=250,"浮层使用接近不透明的统一深色表面");
        gallery.Close();
    }
    private sealed record GalleryRow(string Name,string Detail);
    private sealed class RequiredRule:ValidationRule
    {
        public override ValidationResult Validate(object value,System.Globalization.CultureInfo cultureInfo)=>ValidationResult.ValidResult;
    }

    internal sealed class DashboardProbe
    {
        public int MapCount {get;set;}=16;
        public int ModCount {get;set;}=6;
        public int TaskCount {get;set;}=4;
        public int IssueCount {get;set;}
        public int RunningTaskCount {get;set;}
        public string Status {get;set;}="本地内容读取完成 · 地图 16 · MOD 6 · 未发现结构问题";
        public string LastUpdated=>"最后扫描 · 18:30:00";
        public string Mode {get;set;}="在线模式";
        public string HealthSummary=>"4 / 4 项正常";
        public bool Enabled {get;set;}=true;
        public RelayCommand SyncAllCommand=>new(()=>{},()=>Enabled);
        public RelayCommand RefreshCommand=>new(()=>{},()=>Enabled);
        public RelayCommand CheckServiceCommand=>new(()=>{},()=>Enabled);
        public ObservableCollection<DashboardActivityItem> RecentActivities {get;}=[
            new(DateTime.Now,"内容扫描完成","地图 16 · MOD 6 · 未发现结构问题", Brushes.LightGreen),
            new(DateTime.Now.AddMinutes(-12),"地图安装完成","任务完成 · 已创建覆盖前备份", Brushes.LightGreen),
            new(DateTime.Now.AddMinutes(-35),"MOD同步完成","3项完成 · 没有重复安装", Brushes.LightGreen)
        ];
        public ObservableCollection<DashboardHealthItem> HealthItems {get;}=[
            new("内容目录","正常","地图与 MOD 目录可用",true,"/Assets/Fluent/folder_regular.png"),
            new("存储空间","正常","可用 429.5 GB",true,"/Assets/Fluent/server_regular.png"),
            new("网络连接","已配置","按需检查账号服务",true,"/Assets/Fluent/cloud_regular.png"),
            new("客户端","正常","4.0.0-rc1 · x64",true,"/Assets/Fluent/shield_regular.png")
        ];
    }
}
