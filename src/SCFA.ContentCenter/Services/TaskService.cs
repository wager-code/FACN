using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using SCFA.ContentCenter.Models;

namespace SCFA.ContentCenter.Services;

public sealed class TaskService
{
    private const int MaxSavedTasks = 200;
    private readonly string _path = Path.Combine(ConfigService.ResolveDataDirectory(), "tasks.json");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _saveGate = new();
    private CancellationTokenSource? _saveCts;
    public ObservableCollection<AppTask> Tasks { get; } = [];

    public TaskService() => Load();

    public AppTask Create(string type, string name, string detail = "")
    {
        var task = new AppTask { Type = type, Name = name, Detail = detail };
        task.PropertyChanged += TaskChanged;
        RunOnUi(() =>
        {
            Tasks.Insert(0, task);
            while (Tasks.Count > MaxSavedTasks) Tasks.RemoveAt(Tasks.Count - 1);
        });
        ScheduleSave();
        return task;
    }

    public void ClearFinished()
    {
        RunOnUi(() =>
        {
            foreach (var task in Tasks.Where(x => x.Status is "完成" or "失败" or "已取消" or "已中断").ToArray())
            {
                task.PropertyChanged -= TaskChanged;
                Tasks.Remove(task);
            }
        });
        ScheduleSave();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 2 * 1024 * 1024) return;
            var saved = JsonSerializer.Deserialize<List<AppTask>>(File.ReadAllText(_path), _json) ?? [];
            foreach (var task in saved.OrderByDescending(x => x.CreatedAt).Take(MaxSavedTasks))
            {
                if (task.Status is "运行中" or "等待")
                {
                    task.Status = "已中断";
                    task.Detail = string.IsNullOrWhiteSpace(task.Detail) ? "上次程序退出时任务尚未结束" : task.Detail + " · 上次程序退出时中断";
                }
                task.PropertyChanged += TaskChanged;
                Tasks.Add(task);
            }
        }
        catch
        {
            try { File.Move(_path, _path + ".invalid_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
        }
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        CancellationToken token;
        lock (_saveGate)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            token = _saveCts.Token;
        }
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            var snapshot = Array.Empty<AppTask>();
            RunOnUi(() => snapshot = Tasks.Take(MaxSavedTasks).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, _json), ct);
            File.Move(temp, _path, true);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static void RunOnUi(Action action)
    {
        var application = System.Windows.Application.Current;
        if (application?.Dispatcher is null || application.Dispatcher.CheckAccess()) action();
        else application.Dispatcher.Invoke(action);
    }
}
