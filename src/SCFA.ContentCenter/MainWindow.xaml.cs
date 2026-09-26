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
        _navButtons = [HomeButton, CloudMapsButton, CloudModsButton, LocalMapsButton, LocalModsButton, SyncButton, DownloadsButton, BackupsButton, CloudHistoryButton, PublicationButton, UsersButton, OperationsButton, DiagnosticsButton, UpdatesButton, SettingsButton];
        _navLabels = [HomeLabel, CloudMapsLabel, CloudModsLabel, LocalMapsLabel, LocalModsLabel, SyncLabel, DownloadsLabel, BackupsLabel, CloudHistoryLabel, PublicationLabel, UsersLabel, OperationsLabel, DiagnosticsLabel, UpdatesLabel, SettingsLabel];
        if (viewModel.CurrentPage is not SetupPageViewModel) HomeButton.IsChecked = true;
        SizeChanged += (_, _) => UpdateResponsiveNavigation();
        Loaded += (_, _) => UpdateResponsiveNavigation();
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void UpdateResponsiveNavigation()
    {
        var compact = ActualWidth < 1160;
        SidebarColumn.Width = new GridLength(compact ? 82 : 244);
        BrandTextPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CloudGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LocalGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AdminGroupLabel.Visibility = compact ? Visibility.Collapsed : ((MainViewModel)DataContext).AdminToolsVisibility;
        SystemGroupLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UserIdentityPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UserCard.Padding = compact ? new Thickness(10) : new Thickness(12);
        UserCard.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        UserCard.Width = compact ? 58 : double.NaN;
        foreach (var button in _navButtons)
        {
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.Padding = compact ? new Thickness(13, 11, 13, 11) : new Thickness(13, 10, 13, 10);
        }
        foreach (var label in _navLabels) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ContentHost.Margin = ActualWidth < 1180 ? new Thickness(22, 22, 22, 26) : new Thickness(30, 26, 30, 30);
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
