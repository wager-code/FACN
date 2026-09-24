using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.ViewModels;

public sealed class DownloadsPageViewModel : ViewModelBase
{
    public ObservableCollection<AppTask> Items => App.Services.Tasks.Tasks;
    public int TotalCount => Items.Count;
    public int RunningCount => Items.Count(x => x.Status is "等待" or "运行中");
    public int CompletedCount => Items.Count(x => x.Status == "完成");
    public int FailedCount => Items.Count(x => x.Status is "失败" or "已中断");
    public string QueueStatus => RunningCount > 0 ? $"当前有 {RunningCount} 项任务正在处理" : Items.Count == 0 ? "任务队列为空" : "当前没有运行中的任务";
    public RelayCommand ClearFinishedCommand { get; }

    public DownloadsPageViewModel()
    {
        foreach (var item in Items) item.PropertyChanged += ItemPropertyChanged;
        Items.CollectionChanged += ItemsCollectionChanged;
        ClearFinishedCommand = new RelayCommand(() => App.Services.Tasks.ClearFinished(), () => Items.Any(x => x.Status is "完成" or "失败" or "已取消" or "已中断"));
    }

    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (AppTask item in e.OldItems) item.PropertyChanged -= ItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (AppTask item in e.NewItems) item.PropertyChanged += ItemPropertyChanged;
        UpdateSummaries();
    }

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppTask.Status)) UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(QueueStatus));
        ClearFinishedCommand.RaiseCanExecuteChanged();
    }
}
