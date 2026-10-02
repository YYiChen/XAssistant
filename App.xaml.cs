using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using XAssistant.Services;
using XAssistant.Services.Interfaces;
using XAssistant.Services.QuickNote;
using XAssistant.ViewModels;
using XAssistant.Views;

namespace XAssistant;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private NotifyIcon? _notifyIcon;
    private QuickNoteCaptureService? _quickNoteCapture;
    private GlobalHotkeyService? _globalHotkey;
    internal static bool IsShuttingDown { get; private set; }

    private ILogger<App>? _appLogger;
    private bool _isDataSaved;

    public App()
    {
        // 显式声明退出模式：主窗口关闭/隐藏都不退出进程，只有托盘「退出」
        // （ShutdownApplication）或系统注销时才结束。
        // WPF 默认是 OnLastWindowClose —— 任何窗口关闭就退出，
        // 对「常驻后台静默记录」是致命的。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局 UI 线程异常
        DispatcherUnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.Exception, "未处理的 UI 线程异常");
            e.Handled = true; // 防止进程崩溃，但记录日志
        };

        // 应用程序域未处理异常（通常导致进程退出）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _appLogger?.LogCritical(e.ExceptionObject as Exception, "未处理的应用程序域异常");
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 准备日志目录
        string logDir = AppDataPathHelper.GetAppDataFolder();
        string logPath = System.IO.Path.Combine(logDir, "logs", "xassistant-.log");

        var services = new ServiceCollection();

        // 创建 LogBufferService 实例并提前注册
        // 这样 Serilog 配置和 DI 都使用同一个实例，且无需提前 Build 容器
        var logBuffer = new LogBufferService();
        services.AddSingleton<ILogBufferService>(logBuffer);

        // 注册其他应用服务
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<IMouseClickHookService, MouseClickHookService>();
        services.AddSingleton<IClickDatabaseService, ClickDatabaseService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        // 输入记录缓冲：把数据库 I/O 移出低级钩子回调，消除输入延迟。
        // 必须是单例，且在 ViewModel 之前注册。
        services.AddSingleton<IMouseClickBuffer, MouseClickBuffer>();
        services.AddSingleton<IKeyPressBuffer, KeyPressBuffer>();
#if DEBUG
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant_Dev"));
#else
        services.AddSingleton<IStartupService>(_ => new StartupService("XAssistant"));
#endif
        services.AddSingleton<IKeyboardHookService, KeyboardHookService>();
        services.AddSingleton<IKeyDatabaseService, KeyDatabaseService>();
        services.AddSingleton<ClickCounterViewModel>();
        services.AddSingleton<KeyCounterViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<UsageViewModel>();
        services.AddSingleton<AppUsageViewModel>();
        services.AddSingleton<LogViewerViewModel>();

        services.AddSingleton<ProcessUsageTracker>();

        // 速记唤起（全局热键 + 原生捕获窗 + 直插速记库 + 保存确认 toast）
        services.AddSingleton<IQuickNoteDatabaseService, QuickNoteDatabaseService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<QuickNoteCaptureService>();

        // 配置 Serilog Logger
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
            )
            .WriteTo.Sink(new UiLogSink(logBuffer))
            .CreateLogger();

        // 添加日志服务到 DI
        services.AddLogging(builder => builder.AddSerilog());

        // 构建容器
        var provider = services.BuildServiceProvider();
        Services = provider;

        // 启动输入记录缓冲的后台刷盘循环
        provider.GetRequiredService<IMouseClickBuffer>().Start();
        provider.GetRequiredService<IKeyPressBuffer>().Start();

        // 订阅注销/关机事件，确保会话数据落盘。
        // 上游只写了退订（OnExit 里 -= OnSessionEnding）却从未订阅，
        // 导致 OnSessionEnding 形同虚设，注销时未结束的会话只能等下次启动
        // 靠启发式猜结束时间。此处补上缺失的订阅。
        SystemEvents.SessionEnding += OnSessionEnding;

        // 启动进程追踪
        var processTracker = provider.GetRequiredService<ProcessUsageTracker>();
        processTracker.Start();

        // 获取系统日志记录器
        _appLogger = provider.GetRequiredService<ILogger<App>>();
        _appLogger.LogInformation("═══════ XAssistant 启动成功 ═══════");

        // 后续主窗口
        var mainVM = provider.GetRequiredService<MainWindowViewModel>();

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;

        // 开机自启：每次启动都重写注册表项。
        // 上游只在界面勾选时才写注册表，等于「要先看见窗口才能开自启」——
        // 与「静默后台、开机即录」的目标矛盾。这里改为启动即注册；
        // 用户若在界面取消勾选，下次启动会重新注册（这正是「默认开」与
        // 「用户可覆盖」之间的取舍：自启本身是必须的，界面开关仅控制注册表项，
        // 取消后本次运行内不再自动注册）。
        var startupService = provider.GetRequiredService<IStartupService>();
        startupService.SetAutoStart(true);
        _appLogger.LogInformation("已注册开机自启：{Path}", Environment.ProcessPath);

        // 启动时最小化到托盘，不弹窗口。托盘图标左键单击可恢复。
        var configService = provider.GetRequiredService<IConfigurationService>();
        if (configService.Settings.General.StartMinimized)
        {
            // 先 Show 再设 Minimized：WPF 要求窗口已创建才能改变 WindowState
            mainWindow.Show();
            mainWindow.WindowState = WindowState.Minimized;
        }
        else
        {
            mainWindow.Show();
        }

        // 速记唤起：注册全局热键，热键按下 → 唤起捕获窗
        _quickNoteCapture = provider.GetRequiredService<QuickNoteCaptureService>();
        _globalHotkey = provider.GetRequiredService<GlobalHotkeyService>();
        _globalHotkey.HotKeyPressed += () => _quickNoteCapture.InvokeCapture();
        _globalHotkey.Start();

        InitializeNotifyIcon(mainWindow);
    }

    private void InitializeNotifyIcon(Window mainWindow)
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = GetAppIcon(),
            Visible = true,
            Text = "XAssistant",
        };

        // 左键单击托盘图标 → 显示主窗口
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                ShowMainWindow(mainWindow);
            }
        };

        // 右键菜单
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("显示", null, (_, _) => ShowMainWindow(mainWindow));
        contextMenu.Items.Add("打开速记窗", null, (_, _) => _quickNoteCapture?.InvokeCapture());
        contextMenu.Items.Add("速记列表", null, (_, _) => _quickNoteCapture?.OpenList());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("退出", null, (_, _) => ShutdownApplication());
        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private static void ShowMainWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private void ShutdownApplication()
    {
        if (_isDataSaved)
            return;
        IsShuttingDown = true;
        _notifyIcon!.Visible = false;
        _notifyIcon.Dispose();

        SaveDataAndStopTracker(); // 复用统一逻辑
        Shutdown();
    }

    private static Icon GetAppIcon()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (icon != null)
                return icon;
        }
        catch
        {
            // 忽略错误，使用默认图标
        }
        return SystemIcons.Application;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        SaveDataAndStopTracker();
    }

    private void SaveDataAndStopTracker()
    {
        if (_isDataSaved)
            return;
        _isDataSaved = true;

        _appLogger?.LogInformation("系统正在关闭/注销，保存进程使用数据...");
        try
        {
            var tracker = Services.GetRequiredService<ProcessUsageTracker>();
            tracker.Stop();
            tracker.Dispose();
        }
        catch (Exception ex)
        {
            _appLogger?.LogError(ex, "停止进程追踪器失败");
        }

        // 冲刷输入缓冲：把内存里尚未落盘的按键/点击记录写进数据库。
        // 同步等待（最多各 3 秒），因为关机流程不会等我们。
        try
        {
            Services.GetRequiredService<IMouseClickBuffer>()
                .FlushAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();
            Services.GetRequiredService<IKeyPressBuffer>()
                .FlushAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _appLogger?.LogError(ex, "冲刷输入记录缓冲失败");
        }

        Log.CloseAndFlush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 防止重复保存（如果已经通过 SessionEnding 或 ShutdownApplication 保存过）
        SaveDataAndStopTracker();

        _globalHotkey?.Dispose();
        _notifyIcon?.Dispose();
        // 移除事件订阅，避免内存泄漏
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }
}
