using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Timer = System.Threading.Timer;

namespace XAssistant.Services;

public sealed class ProcessUsageTracker : IDisposable
{
    private readonly ILogger<ProcessUsageTracker> _logger;
    private readonly string _dbPath;
    private const string DbFile = "app_usage.db";
    private const double MinimumSessionSeconds = 1.0;
    private bool _isStopped;

    private readonly ConcurrentDictionary<string, AppSessionState> _appSessions = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<uint, string> _pidToAppName = new();
    private readonly ConcurrentDictionary<uint, PendingProcessInfo> _pendingProcesses = new();

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Timer? _titleRefreshTimer;

    private static readonly string SelfProcessName = NormalizeProcessName(
        Process.GetCurrentProcess().ProcessName
    );

    public ProcessUsageTracker(ILogger<ProcessUsageTracker> logger)
    {
        _logger = logger;
        _dbPath = Path.Combine(AppDataPathHelper.GetAppDataFolder(), DbFile);
    }

    public void Start()
    {
        InitializeDatabase();
        RecoverUnfinishedSessions(); // 恢复未完成的会话
        CaptureExistingProcesses();
        StartWatchers();

        // 每 5 秒检查待确认进程并累计各会话时长
        _titleRefreshTimer = new Timer(
            _ => RefreshAndAccumulate(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5)
        );

        _logger.LogInformation(
            "进程使用追踪已启动（数据库：{Db}，最小会话时长：{MinSec}s）",
            _dbPath,
            MinimumSessionSeconds
        );
    }

    public void Stop()
    {
        if (_isStopped)
            return;
        _isStopped = true;
        _titleRefreshTimer?.Dispose();
        StopWatchers();

        // 立即关闭所有活跃会话，写入最终时长和结束时间
        CloseAllRunningSessions();

        _appSessions.Clear();
        _pidToAppName.Clear();
        _pendingProcesses.Clear();
    }

    private void CloseAllRunningSessions()
    {
        var now = DateTime.Now;
        foreach (var kvp in _appSessions)
        {
            var state = kvp.Value;
            if (state.ProcessCount <= 0)
                continue;

            double delta = (now - state.LastUpdateTime).TotalSeconds;
            double total = state.AccumulatedSeconds + (delta > 0 ? delta : 0);

            if (total < MinimumSessionSeconds)
                DeleteSession(state.SessionId);
            else
                CloseSession(state.SessionId, now, total);
        }
    }

    public void Dispose()
    {
        _titleRefreshTimer?.Dispose();
        _startWatcher?.Dispose();
        _stopWatcher?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================== 数据库初始化与迁移 ====================
    private void InitializeDatabase()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(dir);
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ProcessSession (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProcessName TEXT NOT NULL,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    WindowTitle TEXT,
                    Date TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 添加新增列（忽略重复错误）
        foreach (
            var col in new[] { "AccumulatedSeconds REAL NOT NULL DEFAULT 0", "LastUpdateTime TEXT" }
        )
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"ALTER TABLE ProcessSession ADD COLUMN {col}";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "添加列 {Col} 异常", col);
            }
        }

        // 数据迁移：为旧记录填充累计时长
        try
        {
            using var cmd = conn.CreateCommand();
            // 有 EndTime 的记录：用差值更新 AccumulatedSeconds，LastUpdateTime 设为 EndTime
            cmd.CommandText = """
                UPDATE ProcessSession
                SET AccumulatedSeconds = (julianday(EndTime) - julianday(StartTime)) * 86400,
                    LastUpdateTime = EndTime
                WHERE EndTime IS NOT NULL AND (AccumulatedSeconds = 0 OR AccumulatedSeconds IS NULL);
                """;
            cmd.ExecuteNonQuery();

            // 无 EndTime 的旧未完成会话（正常情况下不存在，但之前 bug 可能残留）：直接关闭
            cmd.CommandText = """
                UPDATE ProcessSession
                SET EndTime = StartTime,
                    AccumulatedSeconds = 0,
                    LastUpdateTime = StartTime
                WHERE EndTime IS NULL AND AccumulatedSeconds = 0;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据迁移失败");
        }

        // 索引
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_processsession_date ON ProcessSession(Date);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_processsession_process ON ProcessSession(ProcessName);";
            cmd.ExecuteNonQuery();
        }
    }

    // ==================== 会话恢复 ====================
    private void RecoverUnfinishedSessions()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, ProcessName, StartTime, Date, AccumulatedSeconds, LastUpdateTime FROM ProcessSession WHERE EndTime IS NULL";
            using var reader = cmd.ExecuteReader();

            var recovered =
                new List<(
                    long id,
                    string name,
                    string startStr,
                    string dateStr,
                    double acc,
                    string? lastUpdStr
                )>();
            while (reader.Read())
            {
                recovered.Add(
                    (
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetDouble(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)
                    )
                );
            }

            if (recovered.Count == 0)
                return;

            var todayStr = DateTime.Today.ToString("yyyy-MM-dd");
            var allProcs = Process.GetProcesses();

            foreach (var item in recovered)
            {
                if (!DateTime.TryParse(item.startStr, out var startTime))
                    startTime = DateTime.Now;

                DateTime lastUpdate =
                    item.lastUpdStr != null && DateTime.TryParse(item.lastUpdStr, out var lu)
                        ? lu
                        : startTime;
                double totalSec = item.acc; // 已记录的累计秒数

                // 理论结束时间 = 开始时间 + 累计秒数（即进程实际退出的时间点）
                var expectedEndTime = startTime.AddSeconds(totalSec);

                // 查找仍在运行的匹配进程（排除自身和 Session 0）
                var matchingProcs = allProcs
                    .Where(p =>
                    {
                        try
                        {
                            if (p.Id == Environment.ProcessId)
                                return false;
                            if (p.SessionId == 0)
                                return false;
                            return NormalizeProcessName(p.ProcessName) == item.name;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();

                // 如果会话日期不是今天 → 跨天场景
                if (item.dateStr != todayStr)
                {
                    DateTime oldDate = DateTime.Parse(item.dateStr);
                    DateTime endOfOldDay = oldDate.Date.AddDays(1).AddMilliseconds(-1);

                    if (matchingProcs.Count > 0)
                    {
                        // 获取匹配进程中最小的启动时间（通常就是进程真正的启动时间）
                        DateTime earliestStart = matchingProcs.Min(p => p.StartTime);
                        double gap = (earliestStart - lastUpdate).TotalSeconds;

                        if (gap <= 300) // 间隔 ≤ 5分钟，认为是连续运行（未关机场景）
                        {
                            // 原有跨天逻辑：旧会话结束于昨天午夜
                            double finalAcc = (endOfOldDay - startTime).TotalSeconds;
                            if (finalAcc < MinimumSessionSeconds)
                                DeleteSession(item.id);
                            else
                                CloseSession(item.id, endOfOldDay, finalAcc);

                            // 为今天创建新会话
                            var todayStart = DateTime.Today;
                            long newSessionId = InsertAppSession(item.name, todayStart);
                            var newState = new AppSessionState
                            {
                                ProcessCount = matchingProcs.Count,
                                SessionId = newSessionId,
                                StartTime = todayStart,
                                AccumulatedSeconds = (DateTime.Now - todayStart).TotalSeconds,
                                LastUpdateTime = DateTime.Now,
                            };
                            _appSessions[item.name] = newState;
                            foreach (var proc in matchingProcs)
                                _pidToAppName[(uint)proc.Id] = item.name;
                        }
                        else
                        {
                            // 间隔过大 → 进程是今天重新启动的，旧会话不应延续
                            // 使用理论结束时间关闭旧会话
                            if (totalSec < MinimumSessionSeconds)
                                DeleteSession(item.id);
                            else
                                CloseSession(item.id, expectedEndTime, totalSec);
                            // 不创建新会话，让 CaptureExistingProcesses 稍后为这些进程创建正确起始时间的会话
                        }
                    }
                    else
                    {
                        // 无匹配进程 → 进程已退出，使用理论结束时间关闭
                        if (totalSec < MinimumSessionSeconds)
                            DeleteSession(item.id);
                        else
                            CloseSession(item.id, expectedEndTime, totalSec);
                    }
                    continue;
                }

                // 日期相同（今天）
                if (matchingProcs.Count > 0)
                {
                    // 进程仍在运行，沿用原有会话
                    var state = new AppSessionState
                    {
                        ProcessCount = matchingProcs.Count,
                        SessionId = item.id,
                        StartTime = startTime,
                        AccumulatedSeconds = totalSec,
                        LastUpdateTime = lastUpdate,
                    };
                    _appSessions[item.name] = state;
                    foreach (var proc in matchingProcs)
                        _pidToAppName[(uint)proc.Id] = item.name;
                }
                else
                {
                    // 进程已退出，使用理论结束时间关闭
                    if (totalSec < MinimumSessionSeconds)
                        DeleteSession(item.id);
                    else
                        CloseSession(item.id, expectedEndTime, totalSec);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复未完成会话失败");
        }
    }

    // ==================== 现有进程捕获 ====================
    private void CaptureExistingProcesses()
    {
        try
        {
            var allProcs = Process.GetProcesses();
            _logger.LogInformation("开始捕获现有进程，共 {Count} 个", allProcs.Length);
            foreach (var proc in allProcs)
            {
                // 新增：跳过已经在追踪列表中的进程
                if (_pidToAppName.ContainsKey((uint)proc.Id))
                    continue;

                string procName = "unknown";
                try
                {
                    procName = proc.ProcessName;
                }
                catch { }

                if (!IsUserApplication(proc))
                    continue;

                DateTime startTime;
                try
                {
                    startTime = proc.StartTime;
                }
                catch
                {
                    startTime = DateTime.Now;
                }
                var appName = NormalizeProcessName(procName);

                // IsUserApplication 已确认该进程有主窗口（MainWindowHandle != 0），
                // 不再读取窗口标题本身——标题不落库。
                AddProcessToAppSession((uint)proc.Id, appName, startTime);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举现有进程失败");
        }
    }

    // ==================== 会话管理（插入/更新/关闭） ====================
    private long InsertAppSession(string processName, DateTime startTime)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ProcessSession (ProcessName, StartTime, Date, AccumulatedSeconds, LastUpdateTime)
                VALUES ($name, $start, $date, 0, $start);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$name", processName);
            cmd.Parameters.AddWithValue("$start", startTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$date", startTime.ToString("yyyy-MM-dd"));
            var result = cmd.ExecuteScalar();
            return result is long id ? id : -1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "插入会话失败: {Process}", processName);
            return -1;
        }
    }

    /// <summary>
    /// 关闭会话，写入结束时间与最终累计时长。
    ///
    /// 隐私决策（个人自用改造）：<b>不落库窗口标题</b>。窗口标题会包含文件名、
    /// 网页标题、聊天对象名等敏感信息，长期明文留存会形成完整的行为档案。
    /// 该字段在 UI 层没有任何消费方——<c>AppUsageViewModel</c> 的 SQL 只读
    /// ProcessName / AccumulatedSeconds / StartTime / EndTime，
    /// <c>Models.AppUsageItem</c> 也没有标题字段。因此去掉它对界面零影响。
    ///
    /// 数据库 schema 保留 WindowTitle 列（避免破坏已有库），只是恒为 NULL。
    /// </summary>
    private void CloseSession(long sessionId, DateTime endTime, double finalAccumulatedSeconds)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ProcessSession
                SET EndTime = $end, WindowTitle = NULL,
                    AccumulatedSeconds = $acc, LastUpdateTime = $end
                WHERE Id = $id
                """;
            cmd.Parameters.AddWithValue("$end", endTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("$acc", finalAccumulatedSeconds);
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭会话失败 Id={Id}", sessionId);
        }
    }

    private void DeleteSession(long sessionId)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ProcessSession WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除短时会话失败 Id={Id}", sessionId);
        }
    }

    // ==================== 定时刷新与累计 ====================
    private void RefreshAndAccumulate()
    {
        // 待确认进程检查仍需保留（用于把稍后才有窗口的进程晋升为正式会话），
        // 但不再刷新窗口标题——标题不再落库，采集它没有意义，
        // 还会每 5 秒对每个会话做一次 Process.GetProcessById + MainWindowTitle 读取。
        CheckPendingProcessesForWindow();
        AccumulateRunningSessions(); // 更新累计时长
    }

    private void AccumulateRunningSessions()
    {
        var now = DateTime.Now;
        var updates = new List<(long Id, double Sec, DateTime Time)>();

        foreach (var kvp in _appSessions)
        {
            var state = kvp.Value;
            if (state.ProcessCount <= 0)
                continue;
            double delta = (now - state.LastUpdateTime).TotalSeconds;
            if (delta > 0)
            {
                state.AccumulatedSeconds += delta;
                state.LastUpdateTime = now;
                updates.Add((state.SessionId, state.AccumulatedSeconds, now));
            }
        }

        if (updates.Count == 0)
            return;

        // 原实现是逐条 fire-and-forget 异步写（WMI 线程同时也在写），
        // 同库并发写会抛 SQLITE_BUSY，而异常只被记成一条 LogWarning，
        // 表现为累计时长偶尔少几秒却无任何提示。改为单连接 + 单事务批量写。
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "UPDATE ProcessSession SET AccumulatedSeconds = @sec, LastUpdateTime = @time WHERE Id = @id";
            var secParam = cmd.Parameters.Add("@sec", Microsoft.Data.Sqlite.SqliteType.Real);
            var timeParam = cmd.Parameters.Add("@time", Microsoft.Data.Sqlite.SqliteType.Text);
            var idParam = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Prepare();

            foreach (var (id, sec, time) in updates)
            {
                secParam.Value = sec;
                timeParam.Value = time.ToString("yyyy-MM-dd HH:mm:ss.fff");
                idParam.Value = id;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "批量更新累计时长失败（{Count} 条）", updates.Count);
        }
    }

    // ==================== 进程添加/移除 ====================
    /// <remarks>
    /// 隐私决策（个人自用改造）：只记录「哪个程序、开了多久」，<b>不采集窗口标题</b>。
    /// 标题会包含文件名、网页标题、聊天对象名，长期明文留存等于建立完整行为档案。
    /// 该字段在 UI 层无任何消费方，删除对界面零影响。
    /// </remarks>
    private void AddProcessToAppSession(
        uint processId,
        string appName,
        DateTime startTime
    )
    {
        _pidToAppName[processId] = appName;
        _appSessions.AddOrUpdate(
            appName,
            _ =>
            {
                var sessionId = InsertAppSession(appName, startTime);
                return new AppSessionState
                {
                    ProcessCount = 1,
                    SessionId = sessionId,
                    StartTime = startTime,
                    LastUpdateTime = startTime,
                    AccumulatedSeconds = 0,
                };
            },
            (_, state) =>
            {
                Interlocked.Increment(ref state.ProcessCount);
                return state;
            }
        );
    }

    private void RemoveProcessFromAppSession(uint processId)
    {
        if (!_pidToAppName.TryRemove(processId, out var appName))
            return;
        if (_appSessions.TryGetValue(appName, out var state))
        {
            int newCount = Interlocked.Decrement(ref state.ProcessCount);
            if (newCount == 0)
            {
                if (_appSessions.TryRemove(appName, out _))
                {
                    var now = DateTime.Now;
                    double delta = (now - state.LastUpdateTime).TotalSeconds;
                    double totalSeconds = state.AccumulatedSeconds + (delta > 0 ? delta : 0);

                    if (totalSeconds < MinimumSessionSeconds)
                    {
                        DeleteSession(state.SessionId);
                    }
                    else
                    {
                        CloseSession(state.SessionId, now, totalSeconds);
                    }
                }
            }
        }
    }

    // ==================== WMI 监视 ====================
    private void StartWatchers()
    {
        try
        {
            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace")
            );
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace")
            );
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "无法启动 WMI 进程监视");
        }
    }

    private void StopWatchers()
    {
        try
        {
            _startWatcher?.Stop();
            _stopWatcher?.Stop();
        }
        catch
        { /* ignore */
        }
        finally
        {
            _startWatcher?.Dispose();
            _stopWatcher?.Dispose();
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        var rawName = e.NewEvent.Properties["ProcessName"]?.Value?.ToString();
        var pidRaw = e.NewEvent.Properties["ProcessID"]?.Value;
        if (string.IsNullOrEmpty(rawName) || pidRaw == null)
        {
            // _logger.LogDebug("WMI 启动事件缺少名称或 PID，忽略");
            return;
        }

        var pid = Convert.ToUInt32(pidRaw);
        var appName = NormalizeProcessName(rawName);

        // _logger.LogInformation("检测到进程启动：{App} (PID {Pid})", appName, pid);

        if (!IsPotentialUserProcess(pid, rawName))
        {
            // _logger.LogDebug("进程 {App} (PID {Pid}) 被 IsPotentialUserProcess 过滤", appName, pid);
            return;
        }

        DateTime startTime = DateTime.Now;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            startTime = proc.StartTime;
        }
        catch
        {
            // _logger.LogDebug(ex, "获取进程 {App} 启动时间失败，使用当前时间", appName);
        }

        bool hasWindow = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            hasWindow = proc.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            // 进程可能已退出，取不到窗口句柄，按无窗口处理
        }

        if (hasWindow)
        {
            AddProcessToAppSession(pid, appName, startTime);
        }
        else
        {
            // 进程刚启动时窗口通常还没出现，先放入待确认队列，
            // 之后由 CheckPendingProcessesForWindow 定期检查并晋升。
            _pendingProcesses[pid] = new PendingProcessInfo
            {
                AppName = appName,
                StartTime = startTime,
            };
            // 之前的 3 秒超时丢弃代码删除，或至少把超时日志化
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        var pidRaw = e.NewEvent.Properties["ProcessID"]?.Value;
        if (pidRaw == null)
            return;
        var pid = Convert.ToUInt32(pidRaw);
        _pendingProcesses.TryRemove(pid, out _);
        RemoveProcessFromAppSession(pid);
    }

    // ==================== 待确认进程检查 ====================
    private void CheckPendingProcessesForWindow()
    {
        foreach (var kvp in _pendingProcesses)
        {
            var pid = kvp.Key;
            var pending = kvp.Value;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    // 这里仍需读取标题：仅用于判断 MainWindowHandle 是否已就绪，
                    // 不再保存到数据库。
                    if (
                        !string.IsNullOrWhiteSpace(proc.MainWindowTitle)
                        && _pendingProcesses.TryRemove(pid, out _)
                    )
                    {
                        AddProcessToAppSession(pid, pending.AppName, pending.StartTime);
                    }
                }
            }
            catch
            {
                // 进程已退出，移除
                _pendingProcesses.TryRemove(pid, out _);
            }
        }
    }

    // ==================== 过滤逻辑 ====================
    private static readonly HashSet<string> SystemProcessNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "explorer",
        "taskmgr",
        "sihost",
        "applicationframehost",
        "svchost",
        "conhost",
        "dwm",
        "tabtip",
        "phoneexperiencehost",
        "shellexperiencehost",
        "searchapp",
        "startmenuexperiencehost",
    };

    private bool IsPotentialUserProcess(uint processId, string rawProcessName)
    {
        if (processId == Environment.ProcessId)
            return false;
        var normalized = NormalizeProcessName(rawProcessName);
        // 排除所有同名的自身进程
        if (normalized == SelfProcessName)
            return false;
        if (SystemProcessNames.Contains(normalized))
            return false;
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            if (proc.SessionId == 0)
                return false;
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch { }
            if (!string.IsNullOrEmpty(path))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (dir.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    private bool IsUserApplication(Process proc)
    {
        if (proc.Id == Environment.ProcessId)
        {
            // _logger.LogDebug("跳过自身进程 PID {Pid}", proc.Id);
            return false;
        }
        string procName;
        try
        {
            procName = proc.ProcessName;
        }
        catch
        {
            return false;
        }
        if (NormalizeProcessName(procName) == SelfProcessName)
        {
            // _logger.LogDebug("跳过同名自身进程 {Proc}", procName);
            return false;
        }
        try
        {
            if (proc.SessionId == 0)
            {
                // _logger.LogDebug("跳过 Session 0 进程 {Proc}", procName);
                return false;
            }
            if (proc.MainWindowHandle == IntPtr.Zero)
            {
                // _logger.LogDebug("跳过无窗口进程 {Proc}", procName);
                return false;
            }
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch { }
            if (!string.IsNullOrEmpty(path))
            {
                var dir = Path.GetDirectoryName(path) ?? "";
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (dir.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                {
                    // _logger.LogDebug("跳过 Windows 目录进程 {Proc}，路径 {Path}", procName, path);
                    return false;
                }
            }
            if (SystemProcessNames.Contains(NormalizeProcessName(procName)))
            {
                // _logger.LogDebug("跳过系统进程名 {Proc}", procName);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProcessName(string rawName)
    {
        if (rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            rawName = rawName[..^4];
        return rawName.ToLowerInvariant();
    }

    // ==================== 内部类 ====================
    private class AppSessionState
    {
        public int ProcessCount;
        public long SessionId;
        public DateTime StartTime;
        public double AccumulatedSeconds;
        public DateTime LastUpdateTime;
    }

    private class PendingProcessInfo
    {
        public string AppName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
    }
}
