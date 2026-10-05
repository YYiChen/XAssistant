using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class ClickCounterViewModel : ViewModelBase
{
    private readonly IMouseClickHookService _hookService;
    private readonly IClickDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private readonly IMouseClickBuffer _buffer;

    [ObservableProperty]
    private int _leftClickCount;

    [ObservableProperty]
    private int _middleClickCount;

    [ObservableProperty]
    private int _rightClickCount;

    [ObservableProperty]
    private bool _isRecording;

    // 今天
    [ObservableProperty]
    private int _leftClickToday;

    [ObservableProperty]
    private int _middleClickToday;

    [ObservableProperty]
    private int _rightClickToday;

    // 日历选中日期及对应点击量
    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _selectedDateLeftCount;

    [ObservableProperty]
    private int _selectedDateMiddleCount;

    [ObservableProperty]
    private int _selectedDateRightCount;

    // ===== 新增：首页用聚合属性 =====
    public int MouseTodayClicks => LeftClickToday + MiddleClickToday + RightClickToday;
    public int MouseTotalClicks => LeftClickCount + MiddleClickCount + RightClickCount;

    public string MouseRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush MouseRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public ClickCounterViewModel(
        IMouseClickHookService hookService,
        IClickDatabaseService dbService,
        IConfigurationService configService,
        IMouseClickBuffer buffer
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;
        _buffer = buffer;

        // 先建好节流定时器：StartRecording() 之后随时可能有点击回调进来
        _uiTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            WpfApplication.Current.Dispatcher
        );
        _uiTimer.Tick += (_, _) =>
        {
            _uiTimer.Stop(); // 单次触发
            FlushUiCounters();
        };

        // 加载历史总计
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);

        _hookService.MouseClicked += OnMouseClicked;

        // 跨天自动刷新。
        // 本程序是常驻后台的（开机自启、静默运行），会跨越多个自然日。
        // 而「今日点击」的刷新原先只挂在 OnMouseClicked 的跨天分支上 ——
        // 意味着跨天时若用户只打开界面查看、不做任何点击，界面会一直显示
        // 昨天的数字当作「今日」。对每天都会发生的事，这是必然出现的显示错误。
        _dayRolloverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _dayRolloverTimer.Tick += (_, _) => CheckDayRollover();
        _dayRolloverTimer.Start();

        if (_configService.GetRecordingAutoStart())
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 检测是否跨天；跨天则重载「今日」计数并清零待刷计数。
    ///
    /// 清零是必要的：<see cref="RefreshDailyCounts"/> 是直接赋值，
    /// 但待刷计数会在下一次 <see cref="FlushUiCounters"/> 时以 <c>+=</c> 累加，
    /// 若不清零，昨天的点击会被算进今天。
    /// </summary>
    private void CheckDayRollover()
    {
        if (DateTime.Today == _lastRefreshDate)
            return;

        _lastRefreshDate = DateTime.Today;
        lock (_uiGate)
        {
            _pendingLeft = 0;
            _pendingMiddle = 0;
            _pendingRight = 0;
        }
        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);
    }

    // 当 IsRecording 变化时，自动通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(MouseRecordingStatus));
        OnPropertyChanged(nameof(MouseRecordingColor));
    }

    // SelectedDate 变更时自动加载对应日期的点击量
    partial void OnSelectedDateChanged(DateTime value)
    {
        LoadCountsForDate(value);
    }

    private void LoadCountsForDate(DateTime date)
    {
        var dayCounts = _dbService.GetClickCountsByDate(date);
        SelectedDateLeftCount = dayCounts["Left"];
        SelectedDateMiddleCount = dayCounts["Middle"];
        SelectedDateRightCount = dayCounts["Right"];
    }

    public void RefreshDailyCounts()
    {
        var today = _dbService.GetClickCountsByDate(DateTime.Today);
        LeftClickToday = today["Left"];
        MiddleClickToday = today["Middle"];
        RightClickToday = today["Right"];
    }

    private DateTime _lastRefreshDate = DateTime.Today;

    /// <summary>跨天检测定时器（每 30 秒查一次日期是否变化）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _dayRolloverTimer;

    /// <summary>节流派发定时器（复用单实例，见 ScheduleUiFlush）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;

    // UI 更新节流：高频点击时不必每次都切到 UI 线程派发，
    // 合并为最多每 50ms 一次，避免 Dispatcher 队列积压导致界面卡顿。
    private static readonly TimeSpan UiThrottleInterval = TimeSpan.FromMilliseconds(50);
    private readonly object _uiGate = new();
    private int _pendingLeft;
    private int _pendingMiddle;
    private int _pendingRight;
    private bool _uiFlushScheduled;
    private DateTime _lastUiFlush = DateTime.MinValue;

    private void OnMouseClicked(string button)
    {
        if (!IsRecording)
            return;

        // 热路径：只做入队，不碰数据库、不切 UI 线程
        _buffer.Enqueue(new MouseClickRecord { Button = button, ClickTime = DateTime.Now });

        if (DateTime.Today != _lastRefreshDate)
        {
            CheckDayRollover();
            return;
        }

        // 累计到待刷新计数，按节流合并派发
        lock (_uiGate)
        {
            switch (button)
            {
                case "Left":
                    _pendingLeft++;
                    break;
                case "Middle":
                    _pendingMiddle++;
                    break;
                case "Right":
                    _pendingRight++;
                    break;
            }

            var now = DateTime.UtcNow;
            if (_uiFlushScheduled || (now - _lastUiFlush) < UiThrottleInterval)
                return;

            _uiFlushScheduled = true;
            _lastUiFlush = now;
        }

        ScheduleUiFlush();
    }

    /// <summary>
    /// 节流派发用的单次定时器（复用同一个实例，避免热路径反复 new 造成 GC 压力）。
    /// </summary>
    private void ScheduleUiFlush()
    {
        var delay = UiThrottleInterval - (DateTime.UtcNow - _lastUiFlush);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        _uiTimer.Stop(); // 重置计时，避免上一次的残留
        _uiTimer.Interval = delay;
        _uiTimer.Start();
    }

    private void FlushUiCounters()
    {
        int left;
        int middle;
        int right;
        lock (_uiGate)
        {
            left = _pendingLeft;
            middle = _pendingMiddle;
            right = _pendingRight;
            _pendingLeft = 0;
            _pendingMiddle = 0;
            _pendingRight = 0;
            _uiFlushScheduled = false;
            _lastUiFlush = DateTime.UtcNow;
        }

        if (left == 0 && middle == 0 && right == 0)
            return;

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            LeftClickCount += left;
            MiddleClickCount += middle;
            RightClickCount += right;
            LeftClickToday += left;
            MiddleClickToday += middle;
            RightClickToday += right;

            OnPropertyChanged(nameof(MouseTodayClicks));
            OnPropertyChanged(nameof(MouseTotalClicks));
        });

        // 节流窗口内可能又攒了新计数，继续排一次
        lock (_uiGate)
        {
            if (_pendingLeft == 0 && _pendingMiddle == 0 && _pendingRight == 0)
                return;
            _uiFlushScheduled = true;
        }
        ScheduleUiFlush();
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetRecordingAutoStart(false);
    }
}
