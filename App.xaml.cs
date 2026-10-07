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
    private SingleInstanceGuard? _singleInstance;
    private Window? _mainWindow;
    internal static bool IsShuttingDown { get; private set; }

    private ILogger<App>? _appLogger;
    private bool _isDataSaved;

    /// <summary>
    /// DI 容器是否已成功构建。
    /// 「第二实例」路径会在构建之前就退出，此时 <see cref="Services"/> 仍为 null，
    /// 退出流程必须据此跳过保存逻辑，否则会走到无效的服务解析路径。
    /// </summary>
    private bool _servicesInitialized;

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

        // ────────────────────────────────────────────────────────────
        // 单实例检查：必须在任何采集初始化之前完成。
        //
        // 键盘/鼠标钩子是按进程安装的。若允许第二个进程继续启动，
        // 它会再装一套钩子，同一次输入被两个进程各记一条 —— 数据翻倍。
        // 因此这里判定后要么继续，要么唤起已有实例并立即退出。
        // ────────────────────────────────────────────────────────────
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.TryAcquire())
        {
            // 已是「第二实例」：请求首实例显示主窗口，然后安静退出。
            bool signaled = SingleInstanceGuard.SignalFirstInstance(TimeSpan.FromSeconds(3));
            WriteEmergencyLog(
                signaled
                    ? "检测到已有实例运行，已请求其显示主窗口，本进程退出。"
                    : "检测到已有实例运行，但未能通知它显示窗口（可能仍在启动中），本进程退出。"
            );
            Shutdown(0);
            return;
        }

        // ────────────────────────────────────────────────────────────
        // 启动主体包在 try/catch 内。
        //
        // 原先 OnStartup 整个方法体没有任何异常保护，而 _appLogger 要到
        // 构建 DI 之后才可用 —— 意味着启动早期（配置损坏、数据库损坏、
        // DI 解析失败）抛异常时既没有日志、也没有提示，
        // 用户看到的是「双击图标闪一下，什么都没发生」。
        // AppDomain.UnhandledException 无法阻止进程终止（已实测），
        // 所以这里必须显式兜底。
        // ────────────────────────────────────────────────────────────
        try
        {
            InitializeApplication();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown(1);
        }
    }

    /// <summary>
    /// 启动主体：构建 DI、启动各采集模块、创建主窗口与托盘。
    /// 任何异常都由 <see cref="OnStartup"/> 捕获并转为用户可见的提示。
    /// </summary>
    private void InitializeApplication()
    {
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

        // 诊断页：原先的「应用日志」面板从主界面移到这里
        services.AddSingleton<DiagnosticsViewModel>();
        // 设置页：集中管理记录开关、启动行为、数据与关于信息
        services.AddSingleton<SettingsViewModel>();

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
        _servicesInitialized = true;

        // 日志器提前取得：后面的启动自检与各模块都要用它
        _appLogger = provider.GetRequiredService<ILogger<App>>();

        // ────────────────────────────────────────────────────────────
        // 启动自检（B3）：验证数据目录可写性、检查数据库完整性。
        //
        // 必须放在任何采集模块启动之前 —— 缓冲刷盘循环一旦先跑起来，
        // 遇到损坏的数据库就会持续报错。这里先修好或隔离掉。
        // 自检自身失败不应阻断启动，故单独包裹异常。
        // ────────────────────────────────────────────────────────────
        try
        {
            var selfCheck = StartupSelfCheck.Run(_appLogger);

            if (selfCheck.RecoveredDatabases.Count > 0)
            {
                string list = string.Join(
                    Environment.NewLine,
                    selfCheck.RecoveredDatabases
                );
                System.Windows.MessageBox.Show(
                    "检测到数据库文件损坏，已重命名保留原文件并重建："
                        + Environment.NewLine
                        + Environment.NewLine
                        + list
                        + Environment.NewLine
                        + Environment.NewLine
                        + "原始文件保留在同一目录，文件名含 .corrupt- 与时间戳，"
                        + "如需人工抢救可从该文件尝试。程序将继续正常启动。",
                    "XAssistant 数据修复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
        }
        catch (Exception ex)
        {
            _appLogger.LogError(ex, "启动自检失败（已忽略，继续启动）");
        }

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
        _appLogger.LogInformation("═══════ XAssistant 启动成功 ═══════");

        // 后续主窗口
        var mainVM = provider.GetRequiredService<MainWindowViewModel>();

        var mainWindow = new MainWindow { DataContext = mainVM };
        MainWindow = mainWindow;

        // ────────────────────────────────────────────────────────────
        // 开机自启的注册策略（B4）。
        //
        // 此前是「每次启动都无条件 SetAutoStart(true)」，导致用户在设置页
        // 取消勾选后、下次启动又被重新打开，设置项形同虚设。
        //
        // 现在改为只在首次运行时注册（符合「开机即录」的使用意图），
        // 之后完全尊重用户在设置页的选择，不再改写注册表。
        // 老配置没有 FirstRunCompleted 字段，反序列化后为 false，
        // 会被视为首次运行而注册一次 —— 这与老版本的既有行为一致
        // （老版本总是开启自启），用户此后仍可正常关闭。
        // ────────────────────────────────────────────────────────────
        var startupService = provider.GetRequiredService<IStartupService>();
        var configService = provider.GetRequiredService<IConfigurationService>();

        if (!configService.Settings.General.FirstRunCompleted)
        {
            startupService.SetAutoStart(true);
            configService.Settings.General.FirstRunCompleted = true;
            configService.Save();
            _appLogger.LogInformation(
                "首次运行：已注册开机自启并写入初始化标记 → {Path}",
                Environment.ProcessPath
            );
        }
        else
        {
            _appLogger.LogInformation(
                "开机自启状态：{State}（由设置页决定，程序不再自动改写）",
                startupService.IsStartWithWindowsEnabled() ? "已启用" : "已关闭"
            );
        }

        // 启动时最小化到托盘，不弹窗口。托盘图标左键单击可恢复。
        if (configService.GetStartMinimized())
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

        // 保存主窗口引用，供「第二实例请求显示窗口」时使用
        _mainWindow = mainWindow;

        // 首实例开始监听显示请求：用户再次双击图标时，新进程会通过管道
        // 发来 SHOW，这里把已有窗口唤到前台 —— 这是桌面软件
        // 「双击图标 = 回到已有窗口」的通行语义。
        _singleInstance?.StartListening(() =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_mainWindow != null)
                    ShowMainWindow(_mainWindow);
            });
        });
    }

    /// <summary>
    /// 启动失败时的应急处理：写一条**不依赖 DI/Serilog** 的日志，并弹窗告知用户。
    ///
    /// 之所以自己拼字符串写文件，是因为失败可能发生在日志系统就绪之前
    /// （例如 DI 构建阶段）。此时用 _appLogger 是拿不到东西的。
    /// </summary>
    private static void ReportStartupFailure(Exception ex)
    {
        string logPath = WriteEmergencyLog(
            "启动失败。" + Environment.NewLine + ex
        );

        try
        {
            // 完全限定：本项目同时引用 WinForms（NotifyIcon）与 WPF，
            // 裸写 MessageBox 会让编译器无法区分两个同名类型。
            System.Windows.MessageBox.Show(
                "XAssistant 启动失败，程序未能运行。"
                    + Environment.NewLine
                    + Environment.NewLine
                    + "原因："
                    + ex.GetType().Name
                    + " - "
                    + ex.Message
                    + Environment.NewLine
                    + Environment.NewLine
                    + "详细信息已写入："
                    + Environment.NewLine
                    + logPath,
                "XAssistant 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
        catch
        {
            // 连弹窗都失败时就只能作罢，避免在错误处理里再抛异常
        }
    }

    /// <summary>
    /// 写应急日志。刻意不依赖 DI/Serilog，保证在最早期失败时也能留下痕迹。
    /// </summary>
    /// <returns>日志文件路径。</returns>
    private static string WriteEmergencyLog(string message)
    {
        string path;
        try
        {
            string dir = AppDataPathHelper.GetAppDataFolder();
            string logDir = System.IO.Path.Combine(dir, "logs");
            System.IO.Directory.CreateDirectory(logDir);
            path = System.IO.Path.Combine(logDir, "startup-error.log");

            string line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            System.IO.File.AppendAllText(path, line);
        }
        catch
        {
            // AppData 都不可写（极少见）：退到临时目录再试一次
            path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "xassistant-startup-error.log"
            );
            try
            {
                System.IO.File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"
                );
            }
            catch
            { /* 彻底无法记录，只能放弃 */
            }
        }

        return path;
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

        // 「第二实例」与「启动失败」路径都会在 DI 构建之前退出，
        // 此时没有需要保存的数据，也不应去解析服务。
        if (!_servicesInitialized)
            return;

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
        _singleInstance?.Dispose(); // 释放互斥体，否则下次启动会被自己挡住
        // 移除事件订阅，避免内存泄漏
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }
}
