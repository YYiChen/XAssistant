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

        _hookService.KeyPressed += OnKeyPressed;

        LoadAllCounts();

        if (_configService.GetKeyRecordingAutoStart())
        {
            StartRecording();
        }
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

        // 检测是否跨天
        DateTime today = DateTime.Today;
        if (today > _currentDate)
        {
            _currentDate = today;
            // 重新加载所有时间段数据
            LoadAllCounts();
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

    private void LoadAllCounts()
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
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
                DayBeforeYesterdayKeyCounts.Add(
                    new KeyCountItem { Key = kv.Key, Count = kv.Value }
                );

            // 通知聚合属性更新
            OnPropertyChanged(nameof(KeyTodayPresses));
            OnPropertyChanged(nameof(KeyTotalPresses));
        });
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
