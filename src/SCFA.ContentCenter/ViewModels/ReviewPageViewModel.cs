using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class ReviewPageViewModel : ViewModelBase
{
    private SubmissionRecord? _selected;
    private string _reviewMessage = "";
    private string _status = "等待刷新";

    public ReviewPageViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApproveCommand = new AsyncRelayCommand(() => ReviewAsync("approve"), () => SelectedItem is not null && CanReview);
        RejectCommand = new AsyncRelayCommand(() => ReviewAsync("reject"), () => SelectedItem is not null && CanReview);
        _ = RefreshAsync();
    }

    public ObservableCollection<SubmissionRecord> Items { get; } = [];
    public SubmissionRecord? SelectedItem
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedMetadata));
            OnPropertyChanged(nameof(SelectedPackage));
            RefreshCommands();
        }
    }
    public string ReviewMessage { get => _reviewMessage; set { if (Set(ref _reviewMessage, value)) OnPropertyChanged(nameof(ReviewMessageCount)); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool CanRead => AccessPolicy.CanReadReviews(App.Services.CurrentUser);
    public bool CanReview => AccessPolicy.CanApproveReviews(App.Services.CurrentUser);
    public int QueueCount => Items.Count;
    public int MapCount => Items.Count(x => x.Kind.Contains("地图", StringComparison.CurrentCultureIgnoreCase) || x.Kind.Equals("map", StringComparison.OrdinalIgnoreCase));
    public int ModCount => Items.Count(x => x.Kind.Contains("mod", StringComparison.OrdinalIgnoreCase) || x.Kind.Contains("模组", StringComparison.CurrentCultureIgnoreCase));
    public long TotalBytes => Items.Sum(x => Math.Max(0, x.Size));
    public string TotalSizeText => FormatBytes(TotalBytes);
    public int ReviewMessageCount => ReviewMessage.Length;
    public string PermissionLabel => CanReview ? "可审核投稿" : CanRead ? "可查看审核队列" : "当前账号无审核权限";
    public string SelectedTitle => SelectedItem is null ? "请选择一条投稿" : SelectedItem.Name;
    public string SelectedMetadata => SelectedItem is null ? "选择后查看作者、分类、标签与内容说明" : $"{SelectedItem.Kind} · {SelectedItem.Version} · 投稿人 {SelectedItem.Submitter}";
    public string SelectedPackage => SelectedItem is null ? "—" : $"{SelectedItem.Files} 个文件 · {SelectedItem.SizeText}";
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand RejectCommand { get; }

    private async Task RefreshAsync()
    {
        if (!CanRead) { Status = "当前账号没有投稿审核读取权限。"; return; }
        try
        {
            Status = "正在读取待审核投稿…";
            var items = await App.Services.Submissions.FetchForReviewAsync();
            Items.Clear();
            foreach (var item in items.OrderByDescending(x => x.CreatedAt)) Items.Add(item);
            NotifySummaries();
            Status = $"审核队列已读取 · {Items.Count} 项";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Status = "服务器尚未提供当前配置的审核列表接口，请在软件设置中核对审核 API 路径。";
            App.Services.Log.Error("审核列表接口不存在", ex);
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
            App.Services.Log.Error("读取审核队列失败", ex);
        }
    }

    private async Task ReviewAsync(string decision)
    {
        var item = SelectedItem;
        if (item is null) return;
        var label = decision == "approve" ? "通过" : "拒绝";
        var answer = MessageBox.Show($"确定{label}投稿“{item.Name}”吗？", "确认审核", MessageBoxButton.YesNo, decision == "approve" ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            Status = $"正在提交{label}决定…";
            await App.Services.Submissions.ReviewAsync(item.Id, decision, ReviewMessage);
            ReviewMessage = "";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = "审核失败：" + ex.Message;
            MessageBox.Show(ex.Message, "审核失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(QueueCount));
        OnPropertyChanged(nameof(MapCount));
        OnPropertyChanged(nameof(ModCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalSizeText));
    }
    private static string FormatBytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024d:F1} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024d:F1} MB" : $"{bytes / 1024d / 1024d / 1024d:F2} GB";
    private void RefreshCommands() { ApproveCommand.RaiseCanExecuteChanged(); RejectCommand.RaiseCanExecuteChanged(); }
}
