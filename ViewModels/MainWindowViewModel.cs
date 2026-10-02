using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IStartupService _startupService;
    private readonly ILogBufferService _logBuffer;
    private readonly IConfigurationService _configService;
    private readonly HomeViewModel _homeViewModel;

    [ObservableProperty]
    private double _windowWidth;

    // 最小化时 WPF 会把窗口尺寸报成极小值（本机实测 160x28），
    // 若照单写入配置，用户每次启动的窗口大小都会被毁掉。
    // 这里加下限守卫：明显小于最小可用尺寸的值一律不落盘。
    private const double MinWindowWidth = 400;
    private const double MinWindowHeight = 300;

    partial void OnWindowWidthChanged(double value)
    {
        if (value >= MinWindowWidth)
            _configService.SetWindowWidth(value);
    }

    [ObservableProperty]
    private double _windowHeight;

    partial void OnWindowHeightChanged(double value)
    {
        if (value >= MinWindowHeight)
            _configService.SetWindowHeight(value);
    }

    [ObservableProperty]
    private bool _isLogExpanded;

    partial void OnIsLogExpandedChanged(bool value) => _configService.SetIsLogExpanded(value);

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private bool _isStartWithWindowsEnabled;

    // 日志集合（直接暴露底层集合，也可以做筛选）
    public ObservableCollection<LogEntry> AllLogs => _logBuffer.LogEntries;

    [ObservableProperty]
    private string _logLevelFilter = "All";

    public string[] LogLevelOptions { get; } =
        { "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    // 计算属性：展示筛选后的日志（也可以在 xaml 中用 CollectionViewSource 过滤）
    public IEnumerable<LogEntry> FilteredLogs =>
        LogLevelFilter == "All"
            ? AllLogs
            : AllLogs.Where(l =>
                l.Level.Equals(LogLevelFilter, StringComparison.OrdinalIgnoreCase)
            );

    public MainWindowViewModel(
        IStartupService startupService,
        ILogBufferService logBuffer,
        IConfigurationService configService,
        HomeViewModel homeViewModel
    )
    {
        _startupService = startupService;
        _logBuffer = logBuffer;
        _configService = configService;
        WindowWidth = _configService.GetWindowWidth();
        WindowHeight = _configService.GetWindowHeight();
        IsLogExpanded = _configService.GetIsLogExpanded();
        CurrentViewModel = homeViewModel;
        _homeViewModel = homeViewModel;
        IsStartWithWindowsEnabled = _startupService.IsStartWithWindowsEnabled();

        // 当日志集合变化时，通知 FilteredLogs 属性变化（简化方式）
        _logBuffer.LogEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilteredLogs));
        };
    }

    // 当日志筛选级别改变时，通知 FilteredLogs 更新
    partial void OnLogLevelFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredLogs));
    }

    // 清空日志
    [RelayCommand]
    private void ClearLogs()
    {
        _logBuffer.LogEntries.Clear();
    }

    partial void OnIsStartWithWindowsEnabledChanged(bool value)
    {
        _startupService.SetAutoStart(value);
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        CurrentViewModel = pageName switch
        {
            "Home" => _homeViewModel,
            "ClickCounter" => App.Services.GetRequiredService<ClickCounterViewModel>(),
            "KeyCounter" => App.Services.GetRequiredService<KeyCounterViewModel>(),
            "Usage" => App.Services.GetRequiredService<UsageViewModel>(),
            "AppUsage" => App.Services.GetRequiredService<AppUsageViewModel>(),
            _ => CurrentViewModel,
        };
    }
}
