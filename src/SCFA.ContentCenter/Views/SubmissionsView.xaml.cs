using System.Windows;
using System.Windows.Controls;
using SCFA.ContentCenter.ViewModels;

namespace SCFA.ContentCenter.Views;

public partial class SubmissionsView : UserControl
{
    public SubmissionsView() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleDirectory(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!TryGetSingleDirectory(e.Data, out var directory) || DataContext is not SubmissionsPageViewModel viewModel) return;
        await viewModel.LoadFolderAsync(directory);
    }

    private static bool TryGetSingleDirectory(IDataObject data, out string directory)
    {
        directory = "";
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return false;
        if (!Directory.Exists(paths[0])) return false;
        directory = paths[0];
        return true;
    }
}
