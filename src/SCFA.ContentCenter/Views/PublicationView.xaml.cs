using System.Windows;
using System.Windows.Controls;
using SCFA.ContentCenter.ViewModels;

namespace SCFA.ContentCenter.Views;

public partial class PublicationView : UserControl
{
    public PublicationView() => InitializeComponent();

    private void ConfigureCosCredential_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PublicationPageViewModel viewModel || !viewModel.CanConfigureCredential) return;
        var window = new Window
        {
            Title = "设置服务器 COS 上传密钥",
            Width = 480,
            Height = 315,
            MinWidth = 420,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var owner = Window.GetWindow(this);
        if (owner is not null) window.Owner = owner;
        var fields = new StackPanel { Margin = new Thickness(22) };
        fields.Children.Add(new TextBlock
        {
            Text = "只填写拥有指定 COS 桶发布权限的子账号密钥。验证成功后由服务器保存，不写入玩家电脑。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        fields.Children.Add(new TextBlock { Text = "SecretId" });
        var idBox = new TextBox { Margin = new Thickness(0, 4, 0, 12), MaxLength = 256 };
        fields.Children.Add(idBox);
        fields.Children.Add(new TextBlock { Text = "SecretKey" });
        var keyBox = new PasswordBox { Margin = new Thickness(0, 4, 0, 18), MaxLength = 256 };
        fields.Children.Add(keyBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "验证并保存到服务器", MinWidth = 150, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = "取消", MinWidth = 75, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => window.DialogResult = false;
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                await viewModel.RotateCredentialAsync(idBox.Text, keyBox.Password);
                keyBox.Clear();
                idBox.Clear();
                window.DialogResult = true;
                MessageBox.Show(owner, "COS 上传密钥已由服务器验证并保存。", "设置完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex.Message, "COS 密钥未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { save.IsEnabled = true; }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        fields.Children.Add(buttons);
        window.Content = fields;
        window.ShowDialog();
    }
}