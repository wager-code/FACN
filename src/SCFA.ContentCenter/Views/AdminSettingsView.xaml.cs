using System.Windows;
using System.Windows.Controls;
using SCFA.ContentCenter.ViewModels;

namespace SCFA.ContentCenter.Views;

public partial class AdminSettingsView : UserControl
{
    public AdminSettingsView() => InitializeComponent();

    private async void AdminSettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdminSettingsPageViewModel viewModel) await viewModel.RefreshAsync();
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdminSettingsPageViewModel viewModel) await viewModel.RefreshAsync();
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AdminSettingsPageViewModel viewModel || !viewModel.CanSave) return;
        var secretKey = SecretKeyInput.Password;
        try
        {
            await viewModel.SaveAsync(SecretIdInput.Text, secretKey);
        }
        finally
        {
            SecretKeyInput.Clear();
            SecretIdInput.Clear();
        }
    }
}
