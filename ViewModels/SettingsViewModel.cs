using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>一个数据库文件的信息（用于设置页展示体积）。</summary>
public sealed class DatabaseInfo
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string SizeText { get; init; }
}

/// <summary>
/// 设置页。
///
/// 原先没有任何设置界面，所有配置都得手改 appsettings.json ——
/// 这是"测试品"的另一个特征。本页把散落各处的开关集中起来：
/// 记录开关（原先键盘页与鼠标页各有一套按钮，且一个用 Visibility、
/// 一个用 IsEnabled，行为还不一致）、启动行为、速记、数据位置、关于。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigurationService _config;
    private readonly IStartupService _startupService;
    private readonly KeyCounterViewModel _keyCounter;
    private readonly ClickCounterViewModel _clickCounter;

    public SettingsViewModel(
        IConfigurationService config,
        IStartupService startupService,
        KeyCounterViewModel keyCounter,
        ClickCounterViewModel clickCounter
    )
    {
        _config = config;
        _startupService = startupService;
        _keyCounter = keyCounter;
        _clickCounter = clickCounter;

        // 记录状态也可能从别处变化（例如托盘菜单、自动启动），
        // 订阅通知以便本页复选框跟随刷新，避免显示与实际不一致。
        _keyCounter.PropertyChanged += OnKeyCounterChanged;
        _clickCounter.PropertyChanged += OnClickCounterChanged;
    }

    private void OnKeyCounterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyCounterViewModel.IsRecording))
        {
            OnPropertyChanged(nameof(KeyRecording));
            OnPropertyChanged(nameof(KeyRecordingStatusText));
        }
    }

    private void OnClickCounterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClickCounterViewModel.IsRecording))
        {
            OnPropertyChanged(nameof(MouseRecording));
            OnPropertyChanged(nameof(MouseRecordingStatusText));
        }
    }

    // ══════════════ 记录 ══════════════

    /// <summary>
    /// 键盘记录总开关。setter 通过命令启停钩子 ——
    /// 直接给 IsRecording 赋值只会改标记位，不会真正装卸钩子。
    /// </summary>
    public bool KeyRecording
    {
        get => _keyCounter.IsRecording;
        set
        {
            if (value == _keyCounter.IsRecording)
                return;

            if (value)
                _keyCounter.StartRecordingCommand.Execute(null);
            else
                _keyCounter.StopRecordingCommand.Execute(null);

            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyRecordingStatusText));
        }
    }

    public string KeyRecordingStatusText => _keyCounter.IsRecording ? "记录中" : "已停止";

    public bool MouseRecording
    {
        get => _clickCounter.IsRecording;
        set
        {
            if (value == _clickCounter.IsRecording)
                return;

            if (value)
                _clickCounter.StartRecordingCommand.Execute(null);
            else
                _clickCounter.StopRecordingCommand.Execute(null);

            OnPropertyChanged();
            OnPropertyChanged(nameof(MouseRecordingStatusText));
        }
    }

    public string MouseRecordingStatusText => _clickCounter.IsRecording ? "记录中" : "已停止";

    // ══════════════ 启动 ══════════════

    public bool StartWithWindows
    {
        get => _startupService.IsStartWithWindowsEnabled();
        set
        {
            if (value == _startupService.IsStartWithWindowsEnabled())
                return;
            _startupService.SetAutoStart(value);
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => _config.GetStartMinimized();
        set
        {
            if (value == _config.GetStartMinimized())
                return;
            _config.SetStartMinimized(value);
            OnPropertyChanged();
        }
    }

    // ══════════════ 速记 ══════════════

    public string QuickNoteHotKey => _config.Settings.QuickNote.HotKey;

    /// <summary>
    /// 速记库连接状态。只显示「已配置/未配置」而不显示连接串本身 ——
    /// 连接串含数据库密码，不应在界面上明文展示。
    /// </summary>
    public string QuickNoteConnectionStatus
    {
        get
        {
            var qn = _config.Settings.QuickNote;
            if (!string.IsNullOrWhiteSpace(qn.ConnectionString))
                return "已配置（直接填写连接串）";
            if (!string.IsNullOrWhiteSpace(qn.DatabaseUrlEnvPath))
                return $"已配置（从 .env 读取：{qn.DatabaseUrlEnvPath}）";

            string defaultEnv = Path.Combine(@"C:\xapp-2026-06-30", ".env");
            return File.Exists(defaultEnv)
                ? $"已配置（自动读取 {defaultEnv}）"
                : "未配置 —— 速记将无法保存到数据库";
        }
    }

    // ══════════════ 数据 ══════════════

    public string DataFolderPath => AppDataPathHelper.GetAppDataFolder();

    public ObservableCollection<DatabaseInfo> Databases { get; } = new();

    [RelayCommand]
    private void RefreshDataInfo()
    {
        Databases.Clear();
        foreach (var (name, file) in new[]
        {
            ("键盘记录", "key_data.db"),
            ("鼠标点击", "click_data.db"),
            ("应用使用", "app_usage.db"),
        })
        {
            Databases.Add(new DatabaseInfo
            {
                Name = name,
                FileName = file,
                SizeText = FormatSize(Path.Combine(DataFolderPath, file)),
            });
        }

        OnPropertyChanged(nameof(TotalSizeText));
    }

    /// <summary>三个数据库合计体积 —— 让「占了多少地方」有个直观数字。</summary>
    public string TotalSizeText
    {
        get
        {
            long total = 0;
            foreach (var (_, file) in new[]
            {
                ("", "key_data.db"),
                ("", "click_data.db"),
                ("", "app_usage.db"),
            })
            {
                var info = new FileInfo(Path.Combine(DataFolderPath, file));
                if (info.Exists)
                    total += info.Length;
            }
            return $"合计 {total / 1024.0 / 1024.0:F1} MB";
        }
    }

    private static string FormatSize(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return "尚未创建";

        double kb = info.Length / 1024.0;
        return kb < 1024 ? $"{kb:F0} KB" : $"{kb / 1024.0:F1} MB";
    }

    [RelayCommand]
    private static void OpenDataFolder()
    {
        try
        {
            string path = AppDataPathHelper.GetAppDataFolder();
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                }
            );
        }
        catch
        { /* 打不开资源管理器不是关键路径 */
        }
    }

    // ══════════════ 关于 ══════════════

    public string VersionText => AppInfo.VersionText;

    public string BuildDateText => AppInfo.BuildDateText;

    public string RepositoryUrl => AppInfo.RepositoryUrl;

    [RelayCommand]
    private static void OpenRepository()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppInfo.RepositoryUrl,
                    UseShellExecute = true,
                }
            );
        }
        catch
        { /* 打不开浏览器不是关键路径 */
        }
    }
}
