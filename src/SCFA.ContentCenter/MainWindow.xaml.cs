using System.Windows;
using System.Windows.Controls;
using SCFA.ContentCenter.ViewModels;

namespace SCFA.ContentCenter;

public partial class MainWindow : Window
{
    private bool _handoffClose;
    private RadioButton[] _navButtons = [];
    private TextBlock[] _navLabels = [];

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        _navButtons = [HomeButton, CloudMapsButton, CloudModsButton, LocalMapsButton, LocalModsButton, SyncButton, DownloadsButton, BackupsButton, PublishMapsButton, PublishModsButton, CloudHistoryButton, DownlistedButton, UsersButton, OperationsButton, AdminSettingsButton, DiagnosticsButton, UpdatesButton, SettingsButton];
        _navLabels = [HomeLabel, CloudMapsLabel, CloudModsLabel, LocalMapsLabel, LocalModsLabel, SyncLabel, DownloadsLabel, BackupsLabel, PublishMapsLabel, PublishModsLabel, CloudHistoryLabel, DownlistedLabel, UsersLabel, OperationsLabel, AdminSettingsLabel, DiagnosticsLabel, UpdatesLabel, SettingsLabel];
        if (viewModel.CurrentPage is not SetupPageViewModel) HomeButton.IsChecked = true;
        SizeChanged += (_, _) => UpdateResponsiveNavigation();
        Loaded += (_, _) => UpdateResponsiveNavigation();
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void UpdateResponsiveNavigation()
    {
        var compact = ActualWidth < (double)FindResource("SidebarBreakpoint");
        SidebarColumn.Width = new GridLength(compact ? (double)FindResource("SidebarCompactWidth") : (double)FindResource("SidebarExpandedWidth"));
        BrandTextPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CloudGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LocalGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var viewModel = (MainViewModel)DataContext;
        PublishGroupLabel.SetCurrentValue(VisibilityProperty, compact ? Visibility.Collapsed : viewModel.PublicationVisibility);
        MaintenanceGroupLabel.SetCurrentValue(VisibilityProperty, compact ? Visibility.Collapsed : viewModel.PublicationVisibility);
        AdminGroupLabel.SetCurrentValue(VisibilityProperty, compact ? Visibility.Collapsed : viewModel.AdminToolsVisibility);
        SystemGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UserIdentityPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UserCard.Padding = (Thickness)FindResource("CompactPadding");
        UserCard.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        UserCard.Width = compact ? 58 : double.NaN;
        foreach (var button in _navButtons)
        {
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.Padding = (Thickness)FindResource(compact ? "NavPaddingCompact" : "NavPadding");
        }
        foreach (var label in _navLabels) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ContentHost.Margin = (Thickness)FindResource(compact ? "PagePaddingCompact" : "PagePadding");
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public void PrepareForWindowHandoff() => _handoffClose = true;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_handoffClose) Application.Current.Shutdown();
    }
}
