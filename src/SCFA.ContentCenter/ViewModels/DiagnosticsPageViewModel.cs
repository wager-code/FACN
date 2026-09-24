using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Models;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class DiagnosticsPageViewModel : ViewModelBase
{
    private string _status = "等待检查";
    private bool _includeNetwork = true;
    private DiagnosticReport? _report;
    private string _lastRun = "尚未完成诊断";

    public DiagnosticsPageViewModel()
    {
        RunCommand = new AsyncRelayCommand(RunAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _report is not null);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        _ = RunAsync();
    }

    public ObservableCollection<DiagnosticItem> Items { get; } = [];
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IncludeNetwork { get => _includeNetwork; set { if (Set(ref _includeNetwork, value)) OnPropertyChanged(nameof(NetworkModeLabel)); } }
    public int TotalCount => _report?.Items.Count ?? 0;
    public int PassedCount => _report?.Passed ?? 0;
    public int ProblemCount => _report?.Problems ?? 0;
    public string HealthLabel => _report is null ? "等待检查" : _report.Problems == 0 ? "全部检查通过" : $"发现 {_report.Problems} 项需要处理";
    public string NetworkModeLabel => IncludeNetwork ? "本机 + 联网检查" : "仅检查本机";
    public string LastRun { get => _lastRun; private set => Set(ref _lastRun, value); }
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    private async Task RunAsync()
    {
        try
        {
            Status = "正在执行完整诊断…";
            Items.Clear();
            _report = await App.Services.Diagnostics.RunAsync(IncludeNetwork);
            foreach (var item in _report.Items) Items.Add(item);
            LastRun = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(PassedCount));
            OnPropertyChanged(nameof(ProblemCount));
            OnPropertyChanged(nameof(HealthLabel));
            Status = $"诊断完成 · 正常 {_report.Passed} · 需处理 {_report.Problems}";
            ExportCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Status = "诊断失败：" + ex.Message;
            App.Services.Log.Error("诊断中心运行失败", ex);
        }
    }

    private async Task ExportAsync()
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出 SCFA 诊断报告",
            FileName = $"SCFA诊断报告_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "文本报告 (*.txt)|*.txt|JSON 报告 (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var text = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true })
                : BuildTextReport(_report);
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(true));
            Status = "诊断报告已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
    }

    private static string BuildTextReport(DiagnosticReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("SCFA 内容中心诊断报告");
        text.AppendLine($"生成时间：{report.GeneratedAt:O}");
        text.AppendLine($"客户端：{report.AppVersion}");
        text.AppendLine($"系统：{report.OperatingSystem}");
        text.AppendLine($"汇总：正常 {report.Passed}，需处理 {report.Problems}");
        text.AppendLine();
        foreach (var item in report.Items) text.AppendLine($"[{item.Status}] {item.Category} / {item.Check}：{item.Detail}");
        return text.ToString();
    }

    private static void OpenLogs()
    {
        var directory = Path.Combine(ConfigService.ResolveDataDirectory(), "Logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }
}
