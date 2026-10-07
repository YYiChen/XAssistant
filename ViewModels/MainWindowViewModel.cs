using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>
/// 侧边栏导航项。
/// </summary>
/// <param name="Key">页面标识，用于解析目标 ViewModel。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形（无需图片资源）。</param>
/// <param name="Title">显示名称。</param>
public sealed record NavItem(string Key, string Glyph, string Title);

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;
    private readonly HomeViewModel _homeViewModel;

    // ────────────── 窗口尺寸 ──────────────

    [ObservableProperty]
    private double _windowWidth;

    [ObservableProperty]
    private double _windowHeight;

    // 最小化时 WPF 会把窗口尺寸报成极小值（本机实测 160x28），
    // 若照单写入配置，用户每次启动的窗口大小都会被毁掉。
    // 这里加下限守卫：明显小于最小可用尺寸的值一律不落盘。
    // 窗口本身另有 MinWidth/MinHeight（见 MainWindow.xaml），
    // 两者是配套的：前者防异常值，后者保证用户拖不到守卫阈值以下。
    private const double MinWindowWidth = 400;
    private const double MinWindowHeight = 300;

    partial void OnWindowWidthChanged(double value)
    {
        if (value >= MinWindowWidth)
            _configService.SetWindowWidth(value);
    }

    partial void OnWindowHeightChanged(double value)
    {
        if (value >= MinWindowHeight)
            _configService.SetWindowHeight(value);
    }

    // ────────────── 导航 ──────────────

    /// <summary>
    /// 导航项。顺序即侧边栏顺序。
    /// 「诊断」与「设置」放在末尾 —— 前者是排障入口，后者是配置入口，
    /// 都不属于日常查看数据的主动线。
    /// </summary>
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("Home", "\uE80F", "首页"),
        new NavItem("ClickCounter", "\uE962", "鼠标点击"),
        new NavItem("KeyCounter", "\uE765", "键盘记录"),
        new NavItem("Usage", "\uE916", "电脑使用"),
        new NavItem("AppUsage", "\uE71D", "软件使用"),
        new NavItem("Diagnostics", "\uE9D9", "诊断"),
        new NavItem("Settings", "\uE713", "设置"),
    };

    /// <summary>
    /// 当前选中的导航项。由侧边栏 ListBox 双向绑定，
    /// 变化即切换右侧内容区。
    /// </summary>
    [ObservableProperty]
    private NavItem? _selectedNavItem;

    /// <summary>右侧内容区当前显示的 ViewModel。</summary>
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value == null)
            return;

        CurrentViewModel = value.Key switch
        {
            "Home" => _homeViewModel,
            "ClickCounter" => App.Services.GetRequiredService<ClickCounterViewModel>(),
            "KeyCounter" => App.Services.GetRequiredService<KeyCounterViewModel>(),
            "Usage" => App.Services.GetRequiredService<UsageViewModel>(),
            "AppUsage" => App.Services.GetRequiredService<AppUsageViewModel>(),
            "Diagnostics" => App.Services.GetRequiredService<DiagnosticsViewModel>(),
            "Settings" => App.Services.GetRequiredService<SettingsViewModel>(),
            _ => CurrentViewModel,
        };
    }

    // ────────────── 构造 ──────────────

    /// <summary>侧边栏底部显示的版本号。</summary>
    public string VersionText => Services.AppInfo.VersionText;

    public MainWindowViewModel(
        IConfigurationService configService,
        HomeViewModel homeViewModel
    )
    {
        _configService = configService;
        _homeViewModel = homeViewModel;

        WindowWidth = _configService.GetWindowWidth();
        WindowHeight = _configService.GetWindowHeight();

        // 默认停在首页。赋 SelectedNavItem 会触发 OnSelectedNavItemChanged，
        // 因此无需再单独给 CurrentViewModel 赋值。
        // 注意顺序：_homeViewModel 必须在此行之前完成赋值。
        SelectedNavItem = NavItems[0];
    }
}
