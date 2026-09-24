using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Core;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class SubmissionsPageViewModel : ViewModelBase
{
    private CancellationTokenSource? _submitCts;
    private int _draftLoadGeneration;
    private LocalContentEntry? _selectedLocal;
    private SubmissionRecord? _selectedSubmission;
    private string _status = "等待刷新";
    private string _draftName = "";
    private string _draftVersion = "";
    private string _draftAuthor = "";
    private string _draftDescription = "";
    private string _draftCategory = "";
    private string _draftTags = "";
    private string _previewPath = "";
    private string _previewSummary = "未选择预览图（可选，PNG/JPEG，最大5MB）";
    private BitmapSource? _previewSource;

    public SubmissionsPageViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanStart);
        BrowseContentCommand = new AsyncRelayCommand(BrowseContentAsync, CanStart);
        BrowsePreviewCommand = new RelayCommand(BrowsePreview, () => SelectedLocal is not null && CanStart());
        ClearPreviewCommand = new RelayCommand(ClearPreview, () => PreviewPath.Length > 0 && CanStart());
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, () => SelectedLocal is not null && CanStart());
        ClearDraftCommand = new AsyncRelayCommand(ClearDraftAsync, () => SelectedLocal is not null && CanStart());
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, CanSubmit);
        CancelSubmitCommand = new RelayCommand(CancelSubmit, () => _submitCts is { IsCancellationRequested: false });
        _ = RefreshAsync();
    }

    public ObservableCollection<LocalContentEntry> LocalItems { get; } = [];
    public ObservableCollection<SubmissionRecord> MyItems { get; } = [];
    public IReadOnlyList<string> Categories { get; } = ["地图", "MOD", "竞技", "合作", "AI", "工具", "界面", "平衡性", "其他"];
    public LocalContentEntry? SelectedLocal
    {
        get => _selectedLocal;
        set
        {
            if (!Set(ref _selectedLocal, value)) return;
            OnPropertyChanged(nameof(SelectedContentLabel));
            NotifyDraftMetrics();
            RaiseCommandStates();
            _ = LoadDraftForSelectionAsync(value, ++_draftLoadGeneration);
        }
    }
    public SubmissionRecord? SelectedSubmission { get => _selectedSubmission; set => Set(ref _selectedSubmission, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string DraftName { get => _draftName; set { if (Set(ref _draftName, value)) NotifyDraftMetrics(); } }
    public string DraftVersion { get => _draftVersion; private set { if (Set(ref _draftVersion, value)) NotifyDraftMetrics(); } }
    public string DraftAuthor { get => _draftAuthor; set { if (Set(ref _draftAuthor, value)) NotifyDraftMetrics(); } }
    public string DraftDescription { get => _draftDescription; set { if (Set(ref _draftDescription, value)) NotifyDraftMetrics(); } }
    public string DraftCategory { get => _draftCategory; set { if (Set(ref _draftCategory, value)) NotifyDraftMetrics(); } }
    public string DraftTags { get => _draftTags; set { if (Set(ref _draftTags, value)) NotifyDraftMetrics(); } }
    public string PreviewPath { get => _previewPath; private set { if (Set(ref _previewPath, value)) RaiseCommandStates(); } }
    public string PreviewSummary { get => _previewSummary; private set => Set(ref _previewSummary, value); }
    public BitmapSource? PreviewSource { get => _previewSource; private set => Set(ref _previewSource, value); }
    public int LocalCount => LocalItems.Count;
    public int SubmissionCount => MyItems.Count;
    public int DescriptionCount => DraftDescription.Length;
    public int TagCount => DraftTags.Split([',', '，', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
    public int DraftCompletedCount => new[] { SelectedLocal is not null, !string.IsNullOrWhiteSpace(DraftName), !string.IsNullOrWhiteSpace(DraftVersion), !string.IsNullOrWhiteSpace(DraftAuthor), !string.IsNullOrWhiteSpace(DraftCategory), DraftDescription.Trim().Length >= 10 }.Count(x => x);
    public int DraftCompletion => DraftCompletedCount * 100 / 6;
    public string DraftRequirementHint
    {
        get
        {
            var missing = new List<string>();
            if (SelectedLocal is null) missing.Add("选择投稿内容");
            if (string.IsNullOrWhiteSpace(DraftName)) missing.Add("填写名称");
            if (string.IsNullOrWhiteSpace(DraftVersion)) missing.Add("识别内容版本");
            if (string.IsNullOrWhiteSpace(DraftAuthor)) missing.Add("填写作者");
            if (string.IsNullOrWhiteSpace(DraftCategory)) missing.Add("选择分类");
            if (DraftDescription.Trim().Length < 10) missing.Add("内容说明至少 10 个字");
            return missing.Count == 0 ? "资料已完整，可以校验并投稿" : "还需完成：" + string.Join("、", missing);
        }
    }
    public string SelectedContentLabel => SelectedLocal is null ? "尚未选择投稿内容" : $"{SelectedLocal.Kind} · {SelectedLocal.Name} · {SelectedLocal.Version}";
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BrowseContentCommand { get; }
    public RelayCommand BrowsePreviewCommand { get; }
    public RelayCommand ClearPreviewCommand { get; }
    public AsyncRelayCommand SaveDraftCommand { get; }
    public AsyncRelayCommand ClearDraftCommand { get; }
    public AsyncRelayCommand SubmitCommand { get; }
    public RelayCommand CancelSubmitCommand { get; }

    private async Task RefreshAsync()
    {
        try
        {
            var selectedKey = SelectedLocal is null ? "" : InstallService.ContentKey(SelectedLocal.Kind, SelectedLocal.Id);
            Status = "正在扫描可投稿内容…";
            var mapsTask = App.Services.Local.ScanAsync("地图");
            var modsTask = App.Services.Local.ScanAsync("MOD");
            await Task.WhenAll(mapsTask, modsTask);
            LocalItems.Clear();
            foreach (var item in mapsTask.Result.Concat(modsTask.Result).Where(x => x.Valid).OrderBy(x => x.Kind).ThenBy(x => x.Name)) LocalItems.Add(item);
            OnPropertyChanged(nameof(LocalCount));
            SelectedLocal = LocalItems.FirstOrDefault(x => InstallService.ContentKey(x.Kind, x.Id).Equals(selectedKey, StringComparison.OrdinalIgnoreCase)) ?? LocalItems.FirstOrDefault();

            MyItems.Clear();
            if (!App.Services.OfflineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.Token))
            {
                try
                {
                    foreach (var item in await App.Services.Submissions.FetchMineAsync()) MyItems.Add(item);
                    OnPropertyChanged(nameof(SubmissionCount));
                    Status = $"已读取 · 本地可投稿 {LocalItems.Count} 项 · 我的投稿 {MyItems.Count} 项";
                }
                catch (Exception ex)
                {
                    Status = $"本地可投稿 {LocalItems.Count} 项；读取服务器投稿失败：{ex.Message}";
                    App.Services.Log.Error("读取我的投稿失败", ex);
                }
            }
            else Status = $"离线模式 · 本地可投稿 {LocalItems.Count} 项；登录服务器账号后可上传";
            OnPropertyChanged(nameof(SubmissionCount));
        }
        catch (Exception ex)
        {
            Status = "刷新失败：" + ex.Message;
            App.Services.Log.Error("投稿中心刷新失败", ex);
        }
    }

    private async Task BrowseContentAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择玩家 Maps/Mods 中的内容文件夹" };
        if (dialog.ShowDialog() != true) return;
        await LoadFolderAsync(dialog.FolderName);
    }

    public async Task LoadFolderAsync(string path)
    {
        try
        {
            Status = "正在识别所选内容…";
            var item = await App.Services.Local.AnalyzeDirectoryAsync(path);
            var existing = LocalItems.FirstOrDefault(x => string.Equals(x.Root, item.Root, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                LocalItems.Add(item);
                OnPropertyChanged(nameof(LocalCount));
                existing = item;
            }
            SelectedLocal = existing;
            Status = $"已识别：{item.Kind} · {item.Name} · {item.Version}";
        }
        catch (Exception ex)
        {
            Status = "识别失败：" + ex.Message;
            MessageBox.Show(ex.Message, "无法识别投稿内容", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LoadDraftForSelectionAsync(LocalContentEntry? item, int generation)
    {
        if (item is null)
        {
            ApplyDraft(new SubmissionDraft());
            return;
        }
        try
        {
            var user = App.Services.CurrentUser;
            var fallbackAuthor = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
            var draft = await App.Services.Submissions.LoadDraftAsync(item, fallbackAuthor);
            if (generation != _draftLoadGeneration || !ReferenceEquals(SelectedLocal, item)) return;
            ApplyDraft(draft);
        }
        catch (Exception ex)
        {
            if (generation == _draftLoadGeneration) Status = "读取投稿草稿失败：" + ex.Message;
        }
    }

    private void ApplyDraft(SubmissionDraft draft)
    {
        DraftName = draft.Name;
        DraftVersion = draft.Version;
        DraftAuthor = draft.Author;
        DraftDescription = draft.Description;
        DraftCategory = draft.Category;
        DraftTags = draft.TagsText;
        ApplyPreview(draft.PreviewPath);
    }

    private SubmissionDraft BuildDraft() => new()
    {
        Name = DraftName,
        Version = DraftVersion,
        Author = DraftAuthor,
        Description = DraftDescription,
        Category = DraftCategory,
        TagsText = DraftTags,
        PreviewPath = PreviewPath
    };

    private void NotifyDraftMetrics()
    {
        OnPropertyChanged(nameof(DescriptionCount));
        OnPropertyChanged(nameof(TagCount));
        OnPropertyChanged(nameof(DraftCompletedCount));
        OnPropertyChanged(nameof(DraftCompletion));
        OnPropertyChanged(nameof(DraftRequirementHint));
    }

    private void BrowsePreview()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择投稿预览图",
            Filter = "PNG/JPEG 图片|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ApplyPreview(dialog.FileName);
            Status = "预览图校验通过。";
        }
        catch (Exception ex)
        {
            ApplyPreview("");
            Status = "预览图无效：" + ex.Message;
            MessageBox.Show(ex.Message, "预览图无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            PreviewPath = "";
            PreviewSource = null;
            PreviewSummary = "未选择预览图（可选，PNG/JPEG，最大5MB）";
            return;
        }
        var dimensions = SubmissionService.ValidatePreviewImage(path);
        var full = Path.GetFullPath(path);
        using var stream = File.OpenRead(full);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 640;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        PreviewPath = full;
        PreviewSource = image;
        PreviewSummary = $"{dimensions.Width}×{dimensions.Height} · {new FileInfo(full).Length / 1024d:F1} KB · {Path.GetFileName(full)}";
    }

    private void ClearPreview()
    {
        ApplyPreview("");
        Status = "已清除预览图。";
    }

    private async Task SaveDraftAsync()
    {
        var item = SelectedLocal;
        if (item is null) return;
        try
        {
            await App.Services.Submissions.SaveDraftAsync(item, BuildDraft());
            Status = "投稿草稿已安全保存。";
        }
        catch (Exception ex) { Status = "保存草稿失败：" + ex.Message; }
    }

    private async Task ClearDraftAsync()
    {
        var item = SelectedLocal;
        if (item is null) return;
        await App.Services.Submissions.DeleteDraftAsync(item);
        var user = App.Services.CurrentUser;
        ApplyDraft(App.Services.Submissions.CreateDefaultDraft(item, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName));
        Status = "已删除所选内容的投稿草稿。";
    }

    private async Task SubmitAsync()
    {
        var item = SelectedLocal;
        if (item is null) return;
        var draft = BuildDraft();
        ValidatedSubmissionDraft validated;
        try { validated = SubmissionValidator.Validate(draft, item); }
        catch (Exception ex)
        {
            Status = "投稿信息不完整：" + ex.Message;
            MessageBox.Show(ex.Message, "请检查投稿信息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(
            $"将重新校验并打包以下内容，然后上传到服务器审核：\n\n{item.Kind} · {validated.Name} · {validated.Version}\n作者：{validated.Author}\n标签：{(validated.Tags.Count == 0 ? "无" : string.Join("、", validated.Tags))}\n预览图：{(validated.PreviewPath.Length == 0 ? "无" : Path.GetFileName(validated.PreviewPath))}\n\n{item.Root}",
            "确认投稿", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        using var cts = new CancellationTokenSource();
        _submitCts = cts;
        RaiseCommandStates();
        try
        {
            await App.Services.Submissions.SaveDraftAsync(item, draft, cts.Token);
            Status = $"正在投稿 {validated.Name}…上传进度可在下载任务页查看。";
            await App.Services.Submissions.SubmitAsync(item, draft, cts.Token);
            Status = "投稿已提交，等待审核。";
            MyItems.Clear();
            foreach (var submission in await App.Services.Submissions.FetchMineAsync(cts.Token)) MyItems.Add(submission);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { Status = "投稿已取消，草稿已经保留。"; }
        catch (Exception ex)
        {
            Status = "投稿失败：" + ex.Message;
            MessageBox.Show(ex.Message, "投稿失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_submitCts, cts)) _submitCts = null;
            RaiseCommandStates();
        }
    }

    private void CancelSubmit()
    {
        if (_submitCts is null) return;
        Status = "正在取消投稿…";
        _submitCts.Cancel();
        CancelSubmitCommand.RaiseCanExecuteChanged();
    }

    private bool CanStart() => _submitCts is null;
    private bool CanSubmit() => SelectedLocal is not null && CanStart() && !App.Services.OfflineMode && !string.IsNullOrWhiteSpace(App.Services.Auth.Token);

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        BrowseContentCommand.RaiseCanExecuteChanged();
        BrowsePreviewCommand.RaiseCanExecuteChanged();
        ClearPreviewCommand.RaiseCanExecuteChanged();
        SaveDraftCommand.RaiseCanExecuteChanged();
        ClearDraftCommand.RaiseCanExecuteChanged();
        SubmitCommand.RaiseCanExecuteChanged();
        CancelSubmitCommand.RaiseCanExecuteChanged();
    }
}
