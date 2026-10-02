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

        // 加载历史总计
        var counts = _dbService.GetClickCounts();
        LeftClickCount = counts["Left"];
        MiddleClickCount = counts["Middle"];
        RightClickCount = counts["Right"];

        RefreshDailyCounts();
        LoadCountsForDate(SelectedDate);

        _hookService.MouseClicked += OnMouseClicked;

        if (_configService.GetRecordingAutoStart())
        {
            StartRecording();
        }
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
            RefreshDailyCounts();
            _lastRefreshDate = DateTime.Today;
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

    private void ScheduleUiFlush()
    {
        var delay = UiThrottleInterval - (DateTime.UtcNow - _lastUiFlush);
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            WpfApplication.Current.Dispatcher
        )
        {
            Interval = delay,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FlushUiCounters();
        };
        timer.Start();
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
