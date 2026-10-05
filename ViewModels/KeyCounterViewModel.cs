using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Services.Interfaces;
using WpfApplication = System.Windows.Application;

namespace XAssistant.ViewModels;

public partial class KeyCounterViewModel : ViewModelBase
{
    private readonly IKeyboardHookService _hookService;
    private readonly IKeyDatabaseService _dbService;
    private readonly IConfigurationService _configService;
    private readonly IKeyPressBuffer _buffer;
    private DateTime _currentDate = DateTime.Today;

    /// <summary>跨天检测定时器（每 30 秒查一次日期是否变化）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _dayRolloverTimer;

    /// <summary>节流派发定时器（复用单实例，见 ScheduleUiFlush）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _uiTimer;

    // UI 更新节流：合并为最多每 50ms 一次，避免高频按键把 Dispatcher 队列打满。
    private static readonly TimeSpan UiThrottleInterval = TimeSpan.FromMilliseconds(50);
    private readonly object _uiGate = new();
    private readonly Dictionary<string, int> _pendingKeys = new();
    private bool _uiFlushScheduled;
    private DateTime _lastUiFlush = DateTime.MinValue;

    // 总计
    public ObservableCollection<KeyCountItem> KeyCounts { get; } = new();

    // 今天
    public ObservableCollection<KeyCountItem> TodayKeyCounts { get; } = new();

    // 昨天
    public ObservableCollection<KeyCountItem> YesterdayKeyCounts { get; } = new();

    // 前天
    public ObservableCollection<KeyCountItem> DayBeforeYesterdayKeyCounts { get; } = new();

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private int _selectedTabIndex;

    // ===== 新增：首页用聚合属性 =====
    public int KeyTodayPresses => TodayKeyCounts.Sum(item => item.Count);
    public int KeyTotalPresses => KeyCounts.Sum(item => item.Count);

    public string KeyRecordingStatus => IsRecording ? "记录中" : "已停止";
    public System.Windows.Media.Brush KeyRecordingColor =>
        IsRecording
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)) // 绿色
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E)); // 灰色

    // =============================

    public KeyCounterViewModel(
        IKeyboardHookService hookService,
        IKeyDatabaseService dbService,
        IConfigurationService configService,
        IKeyPressBuffer buffer
    )
    {
        _hookService = hookService;
        _dbService = dbService;
        _configService = configService;
        _buffer = buffer;

        // 先建好节流定时器：StartRecording() 之后随时可能有按键回调进来
        _uiTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            WpfApplication.Current.Dispatcher
        );
        _uiTimer.Tick += (_, _) =>
        {
            _uiTimer.Stop(); // 单次触发
            FlushUiCounters();
        };

        _hookService.KeyPressed += OnKeyPressed;

        LoadAllCounts();

        // 跨天自动刷新。
        // 本程序常驻后台（开机自启、静默运行），必然跨越自然日。
        // 而「今日按键」原先只在 OnKeyPressed 的跨天分支刷新 ——
        // 跨天后若用户只打开界面查看而不按键，会一直显示昨天的数字当作「今日」。
        _dayRolloverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _dayRolloverTimer.Tick += (_, _) => CheckDayRollover();
        _dayRolloverTimer.Start();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
    }

    /// <summary>
    /// 检测是否跨天；跨天则重建各时段统计并把待刷计数清零。
    ///
    /// 清空 <c>_pendingKeys</c> 是必要的：这些计数对应的记录已入队、会正常落库，
    /// 但 <see cref="ReloadAllCountsCore"/> 会用数据库值重建「今日」集合。
    /// 若不清空，残留的旧计数会在下一次 flush 时被 <c>+=</c> 到新的一天，造成
    /// 「今天的数字里混进昨天的按键」。
    /// </summary>
    private void CheckDayRollover()
    {
        if (DateTime.Today <= _currentDate)
            return;

        _currentDate = DateTime.Today;
        lock (_uiGate)
        {
            _pendingKeys.Clear();
        }
        LoadAllCounts();
    }

    // IsRecording 变化时通知状态属性
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(KeyRecordingStatus));
        OnPropertyChanged(nameof(KeyRecordingColor));
    }

    private void OnKeyPressed(string key)
    {
        // 热路径：只入队。数据库写入由后台批量刷盘完成，
        // 绝不在低级钩子回调里做 I/O（原实现会导致输入延迟）。
        _buffer.Enqueue(new Models.KeyPressRecord { Key = key, PressTime = DateTime.Now });

        // 检测是否跨天（与定时器共用同一逻辑）
        if (DateTime.Today > _currentDate)
        {
            CheckDayRollover();
            return;
        }

        // 累计到待刷新字典，按节流合并派发
        lock (_uiGate)
        {
            _pendingKeys.TryGetValue(key, out var n);
            _pendingKeys[key] = n + 1;

            var now = DateTime.UtcNow;
            if (_uiFlushScheduled || (now - _lastUiFlush) < UiThrottleInterval)
                return;

            _uiFlushScheduled = true;
            _lastUiFlush = now;
        }

        ScheduleUiFlush();
    }

    /// <summary>
    /// 节流派发用的单次定时器（复用同一个实例）。
    ///
    /// 原实现在每次调度时 <c>new DispatcherTimer</c>：高频输入下每 50ms 就新建一个，
    /// 一天可产生上百万个短命对象，纯属 GC 压力。复用单实例没有副作用 ——
    /// <c>_uiFlushScheduled</c> 已保证同一时刻至多只有一个待刷新任务。
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
        Dictionary<string, int> pending;
        lock (_uiGate)
        {
            if (_pendingKeys.Count == 0)
            {
                _uiFlushScheduled = false;
                return;
            }
            pending = new Dictionary<string, int>(_pendingKeys);
            _pendingKeys.Clear();
            _uiFlushScheduled = false;
            _lastUiFlush = DateTime.UtcNow;
        }

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            foreach (var kv in pending)
            {
                UpdateCollection(KeyCounts, kv.Key, kv.Value);
                UpdateCollection(TodayKeyCounts, kv.Key, kv.Value);
            }

            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
        });

        // 节流窗口内可能又攒了新计数，继续排一次
        lock (_uiGate)
        {
            if (_pendingKeys.Count == 0)
                return;
            _uiFlushScheduled = true;
        }
        ScheduleUiFlush();
    }

    private void UpdateCollection(
        ObservableCollection<KeyCountItem> collection,
        string key,
        int delta
    )
    {
        var item = collection.FirstOrDefault(x => x.Key == key);
        if (item != null)
            item.Count += delta;
        else
            collection.Add(new KeyCountItem { Key = key, Count = delta });
    }

    /// <summary>
    /// 重新加载各时间段统计。
    ///
    /// 注意：这里用 <c>Dispatcher.InvokeAsync</c> 而非 <c>Invoke</c>。
    /// <c>Invoke</c> 是同步阻塞的，而本方法由 OnKeyPressed 触发 —— 后者运行在
    /// 低级键盘钩子回调线程上。若在这里同步等 UI 线程执行 4 次数据库聚合查询，
    /// 跨天那一刻会把 UI 线程卡住，直接体现为「键盘突然卡一下」。
    /// 数据量越大（当前已有数千行）越明显。
    /// </summary>
    private void LoadAllCounts()
    {
        WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                ReloadAllCountsCore();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"重新加载按键统计失败: {ex.Message}");
            }
        });
    }

    private void ReloadAllCountsCore()
    {
        // 总计
        var totalDict = _dbService.GetKeyCounts();
        KeyCounts.Clear();
        foreach (var kv in totalDict.OrderByDescending(x => x.Value))
            KeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 今天
        var todayDict = _dbService.GetKeyCounts(DateTime.Today, DateTime.Today.AddDays(1));
        TodayKeyCounts.Clear();
        foreach (var kv in todayDict.OrderByDescending(x => x.Value))
            TodayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 昨天
        var yesterdayDict = _dbService.GetKeyCounts(DateTime.Today.AddDays(-1), DateTime.Today);
        YesterdayKeyCounts.Clear();
        foreach (var kv in yesterdayDict.OrderByDescending(x => x.Value))
            YesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 前天
        var dayBeforeDict = _dbService.GetKeyCounts(
            DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-1)
        );
        DayBeforeYesterdayKeyCounts.Clear();
        foreach (var kv in dayBeforeDict.OrderByDescending(x => x.Value))
            DayBeforeYesterdayKeyCounts.Add(new KeyCountItem { Key = kv.Key, Count = kv.Value });

        // 通知聚合属性更新
        OnPropertyChanged(nameof(KeyTodayPresses));
        OnPropertyChanged(nameof(KeyTotalPresses));
    }

    [RelayCommand]
    private void StartRecording()
    {
        _hookService.Start();
        IsRecording = true;
        _configService.SetKeyRecordingAutoStart(true);
    }

    [RelayCommand]
    private void StopRecording()
    {
        _hookService.Stop();
        IsRecording = false;
        _configService.SetKeyRecordingAutoStart(false);
    }

    [RelayCommand]
    private void RefreshData()
    {
        _currentDate = DateTime.Today;
        LoadAllCounts();
    }
}

// 辅助类，用于绑定
public partial class KeyCountItem : ObservableObject
{
    private int _count;
    public string Key { get; set; } = string.Empty;

    public int Length => Key?.Length ?? 0; // 用于排序

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}
