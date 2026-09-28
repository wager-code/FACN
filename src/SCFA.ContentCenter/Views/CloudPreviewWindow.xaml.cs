using System.Windows;
using System.Windows.Input;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Views;

public partial class CloudPreviewWindow : Window
{
    public CloudPreviewWindow(CloudContentEntry item)
    {
        InitializeComponent();
        DataContext = item;
        Title = $"预览：{item.DisplayName}";
        Width = Math.Min(1000, SystemParameters.WorkArea.Width * 0.9);
        Height = Math.Min(760, SystemParameters.WorkArea.Height * 0.9);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
