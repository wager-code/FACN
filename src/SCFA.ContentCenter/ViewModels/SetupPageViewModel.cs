using System.Collections.ObjectModel;
using Microsoft.Win32;
using SCFA.ContentCenter.Commands;
using SCFA.ContentCenter.Services;

namespace SCFA.ContentCenter.ViewModels;

public sealed class SetupPageViewModel : ViewModelBase
{
    private string _gameRoot;
    private string _mapsDir;
    private string _modsDir;
    private string _selectedDetectedRoot = "";
    private string _status = "请完成游戏与内容目录设置。";

    public SetupPageViewModel()
    {
        var config = App.Services.Config.Current;
        _gameRoot = config.GameRoot;
        _mapsDir = string.IsNullOrWhiteSpace(config.MapsDir) ? App.Services.Paths.GetDefaultPlayerContentDirectory("地图") : config.MapsDir;
        _modsDir = string.IsNullOrWhiteSpace(config.ModsDir) ? App.Services.Paths.GetDefaultPlayerContentDirectory("MOD") : config.ModsDir;
        DetectCommand = new AsyncRelayCommand(DetectAsync);
        UseDefaultCommand = new RelayCommand(UseDefaultDirectories);
        BrowseGameCommand = new RelayCommand(() => Browse(value => GameRoot = value));
        BrowseMapsCommand = new RelayCommand(() => Browse(value => MapsDir = value));
        BrowseModsCommand = new RelayCommand(() => Browse(value => ModsDir = value));
        ApplyDetectedCommand = new RelayCommand(ApplyDetected, () => !string.IsNullOrWhiteSpace(SelectedDetectedRoot));
        CreateAndSaveCommand = new AsyncRelayCommand(CreateAndSaveAsync);
        _ = DetectAsync();
    }

    public ObservableCollection<string> DetectedRoots { get; } = [];
    public string GameRoot { get => _gameRoot; set { if (Set(ref _gameRoot, value)) NotifySetupState(); } }
    public string MapsDir { get => _mapsDir; set { if (Set(ref _mapsDir, value)) NotifySetupState(); } }
    public string ModsDir { get => _modsDir; set { if (Set(ref _modsDir, value)) NotifySetupState(); } }
    public string SelectedDetectedRoot { get => _selectedDetectedRoot; set { if (Set(ref _selectedDetectedRoot, value)) ApplyDetectedCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool IsReady => Directory.Exists(MapsDir) && Directory.Exists(ModsDir);
    public int DetectedCount => DetectedRoots.Count;
    public int ConfiguredPathCount => new[] { GameRoot, MapsDir, ModsDir }.Count(x => !string.IsNullOrWhiteSpace(x));
    public int SetupProgress => ConfiguredPathCount * 100 / 3;
    public string GameStateLabel => string.IsNullOrWhiteSpace(GameRoot) ? "可稍后设置" : GamePathService.IsScfaGameRoot(GameRoot) ? "游戏目录已识别" : Directory.Exists(GameRoot) ? "未识别游戏文件" : "路径需要确认";
    public string ContentStateLabel => string.IsNullOrWhiteSpace(MapsDir) || string.IsNullOrWhiteSpace(ModsDir) ? "内容目录未完整" : IsReady ? "内容目录已就绪" : "目录不存在，请重新选择";
    public string ReadyLabel => IsReady ? "首次设置已完成" : "等待验证并保存";
    public AsyncRelayCommand DetectCommand { get; }
    public RelayCommand UseDefaultCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand BrowseMapsCommand { get; }
    public RelayCommand BrowseModsCommand { get; }
    public RelayCommand ApplyDetectedCommand { get; }
    public AsyncRelayCommand CreateAndSaveCommand { get; }

    public static bool NeedsSetup()
    {
        try
        {
            return !Directory.Exists(App.Services.Paths.GetContentDirectory("地图")) ||
                   !Directory.Exists(App.Services.Paths.GetContentDirectory("MOD"));
        }
        catch
        {
            // 旧配置中的相对路径、磁盘根目录或重解析点应回到设置向导修正，不能让启动崩溃。
            return true;
        }
    }

    private async Task DetectAsync()
    {
        try
        {
            Status = "正在检测 Steam 与常用安装位置…";
            var roots = await Task.Run(() => App.Services.Paths.DiscoverGameRoots());
            DetectedRoots.Clear();
            foreach (var root in roots) DetectedRoots.Add(root);
            OnPropertyChanged(nameof(DetectedCount));
            if (DetectedRoots.Count > 0)
            {
                SelectedDetectedRoot = DetectedRoots[0];
                Status = $"检测到 {DetectedRoots.Count} 个可用游戏目录。";
            }
            else
            {
                Status = "未自动检测到游戏安装目录；请手动选择游戏及其已有 Maps/Mods 目录。";
            }
        }
        catch (Exception ex)
        {
            Status = "自动检测失败：" + ex.Message;
            App.Services.Log.Error("游戏目录自动检测失败", ex);
        }
    }

    private void ApplyDetected()
    {
        if (string.IsNullOrWhiteSpace(SelectedDetectedRoot)) return;
        GameRoot = SelectedDetectedRoot;
        UseDefaultDirectories();
        Status = "已填入检测到的游戏目录；请确认内容目录后保存。";
    }

    private void UseDefaultDirectories()
    {
        if (string.IsNullOrWhiteSpace(GameRoot))
        {
            Status = "请先选择或检测游戏目录。";
            return;
        }
        var maps = Path.Combine(GameRoot, "maps");
        var mods = Path.Combine(GameRoot, "mods");
        MapsDir = Directory.Exists(maps) ? maps : "";
        ModsDir = Directory.Exists(mods) ? mods : "";
        Status = IsReady ? "已使用游戏目录中现有的 maps/mods。" : "游戏目录中没有同时找到现有 maps/mods；请手动选择，软件不会创建它们。";
    }

    private async Task CreateAndSaveAsync()
    {
        try
        {
            var maps = App.Services.Paths.NormalizeContentDirectory(MapsDir, "地图");
            var mods = App.Services.Paths.NormalizeContentDirectory(ModsDir, "MOD");
            GamePathService.ValidateContentDirectoryPair(maps, mods);
            if (!string.IsNullOrWhiteSpace(GameRoot) && !Directory.Exists(GameRoot)) throw new DirectoryNotFoundException("游戏安装目录不存在：" + GameRoot);
            if (!Directory.Exists(maps)) throw new DirectoryNotFoundException("地图目录不存在，请选择已有目录：" + maps);
            if (!Directory.Exists(mods)) throw new DirectoryNotFoundException("MOD 目录不存在，请选择已有目录：" + mods);

            var config = App.Services.Config.Current;
            config.GameRoot = string.IsNullOrWhiteSpace(GameRoot) ? "" : Path.GetFullPath(GameRoot.Trim());
            config.MapsDir = maps;
            config.ModsDir = mods;
            await App.Services.Config.SaveAsync();
            GameRoot = config.GameRoot;
            MapsDir = maps;
            ModsDir = mods;
            OnPropertyChanged(nameof(IsReady));
            NotifySetupState();
            Status = "首次设置已完成，可以扫描、同步和安装内容。";
            App.Services.Log.Info($"首次设置完成: game={config.GameRoot}, maps={maps}, mods={mods}");
        }
        catch (Exception ex)
        {
            Status = "保存失败：" + ex.Message;
            App.Services.Log.Error("首次设置保存失败", ex);
        }
    }

    private static void Browse(Action<string> setter)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) setter(dialog.FolderName);
    }

    private void NotifySetupState()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ConfiguredPathCount));
        OnPropertyChanged(nameof(SetupProgress));
        OnPropertyChanged(nameof(GameStateLabel));
        OnPropertyChanged(nameof(ContentStateLabel));
        OnPropertyChanged(nameof(ReadyLabel));
    }
}
