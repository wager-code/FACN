using System.Windows;
using System.Windows.Controls;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Views;

public partial class CloudContentView : UserControl
{
    public CloudContentView() => InitializeComponent();

    private void EnlargePreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CloudContentEntry { HasPreview: true } item }) return;
        var preview = new CloudPreviewWindow(item);
        var owner = Window.GetWindow(this);
        if (owner is not null) preview.Owner = owner;
        preview.ShowDialog();
    }
}
