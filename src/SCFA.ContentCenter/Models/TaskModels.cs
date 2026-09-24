using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using SCFA.ContentCenter.Commands;

namespace SCFA.ContentCenter.Models;

public sealed class AppTask : INotifyPropertyChanged
{
    private string _status = "等待";
    private int _progress;
    private string _detail = "";
    private Action? _cancel;
    private Func<Task>? _retry;

    public AppTask()
    {
        CancelCommand = new RelayCommand(Cancel, () => _cancel is not null && Status is "等待" or "运行中");
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => _retry is not null && Status is "失败" or "已取消");
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get => _status; set { _status = value; OnChanged(); RefreshCommands(); } }
    public int Progress { get => _progress; set { _progress = Math.Clamp(value, 0, 100); OnChanged(); } }
    public string Detail { get => _detail; set { _detail = value; OnChanged(); } }
    [JsonIgnore] public RelayCommand CancelCommand { get; }
    [JsonIgnore] public AsyncRelayCommand RetryCommand { get; }

    public void ConfigureCancellation(Action? cancel)
    {
        _cancel = cancel;
        CancelCommand.RaiseCanExecuteChanged();
    }

    public void ConfigureRetry(Func<Task>? retry)
    {
        _retry = retry;
        RetryCommand.RaiseCanExecuteChanged();
    }

    private void Cancel()
    {
        if (_cancel is null) return;
        Detail = "正在取消，请稍候…";
        var cancel = _cancel;
        _cancel = null;
        CancelCommand.RaiseCanExecuteChanged();
        cancel();
    }

    private async Task RetryAsync()
    {
        var retry = _retry;
        if (retry is null) return;
        try { await retry(); }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void RefreshCommands()
    {
        CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
