namespace SCFA.ContentCenter.Services;

public sealed class LogService
{
    private static readonly object Gate = new();
    public string LogDirectory { get; }
    public string CurrentLogPath { get; }
    public LogService()
    {
        LogDirectory = Path.Combine(ConfigService.ResolveDataDirectory(), "Logs");
        Directory.CreateDirectory(LogDirectory);
        CurrentLogPath = Path.Combine(LogDirectory, $"scfa-{DateTime.Now:yyyyMMdd}.log");
    }
    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    public static void Bootstrap(string message, Exception? ex = null)
    {
        try
        {
            var directory = Path.Combine(ConfigService.ResolveDataDirectory(), "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"scfa-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTime.Now:O} [STARTUP] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(CurrentLogPath, $"{DateTime.Now:O} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
