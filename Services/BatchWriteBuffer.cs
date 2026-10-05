using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace XAssistant.Services;

/// <summary>
/// 批量写入缓冲的通用实现：钩子回调只做 <c>Enqueue</c>，数据库 I/O 全部挪到后台。
///
/// 解决的问题：原实现在 Win32 低级钩子回调里同步写 SQLite，每次输入都要
/// <c>new SqliteConnection + Open + INSERT</c>。这段耗时直接计入
/// <c>LowLevelHooksTimeout</c>，高频输入下会导致输入延迟，极端情况下钩子被系统静默摘除。
///
/// 刷盘节奏：累计 <c>BatchSize</c> 条、或距上次刷盘超过 <c>FlushInterval</c>，先到者触发。
/// 写失败时保留批次并重试，不丢记录。
/// </summary>
/// <typeparam name="T">记录类型。</typeparam>
public sealed class BatchWriteBuffer<T> : IDisposable
{
    private const int BatchSize = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly Action<IReadOnlyList<T>> _writeBatch;
    private readonly ILogger _logger;
    private readonly string _recordName;
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cts = new();

    private int _inFlight;
    private Task? _worker;
    private int _disposed;

    /// <param name="writeBatch">批量落盘回调，内部应使用单连接 + 单事务。</param>
    /// <param name="logger">用于记录刷盘异常。</param>
    /// <param name="recordName">记录类型名，仅用于日志。</param>
    public BatchWriteBuffer(
        Action<IReadOnlyList<T>> writeBatch,
        ILogger logger,
        string recordName
    )
    {
        _writeBatch = writeBatch;
        _logger = logger;
        _recordName = recordName;
        // 无界队列：宁可内存里多攒，也绝不让写库变慢而阻塞或丢弃输入
        _channel = Channel.CreateUnbounded<T>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            }
        );
    }

    /// <summary>入队一条记录。非阻塞，钩子热路径只调用这一个方法。</summary>
    public void Enqueue(T record)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (!_channel.Writer.TryWrite(record))
        {
            _logger.LogWarning("{Record} 缓冲区已满，丢弃一条记录", _recordName);
        }
    }

    /// <summary>启动后台刷盘循环。幂等。</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _worker ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>等待队列排空且在途批次落盘。</summary>
    /// <param name="timeout">等待上限。</param>
    public async Task FlushAsync(TimeSpan timeout)
    {
        // 空闲判据不能用 _channel.Reader.Count：ChannelReader<T>.Count 只在
        // CanCount 为 true 时可用，对无界 Channel 会抛 NotSupportedException。
        // 改用「能否再读出一条」来判定队列是否排空——这是无界通道的通用做法。
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_channel.Reader.TryPeek(out _) && Volatile.Read(ref _inFlight) == 0)
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "冲刷 {_Record} 超时，队列仍有 {InFlight} 条在途",
            _recordName,
            Volatile.Read(ref _inFlight)
        );
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 累积缓冲区跨循环迭代复用。
        //
        // 关键：batch 不能在每轮循环开头 Clear()。
        // 若那样做，当「未达刷盘阈值」而走 continue 分支时，
        // 上一轮已从 channel 读出的记录会被清掉 —— 它们既不在 channel 里
        // 也不在任何 batch 里，永久丢失。
        // 这正是此前「按键数量莫名其妙少一截」的根因。
        var batch = new List<T>(BatchSize);
        var lastFlush = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 只在 batch 未满时继续补充，已满则直接刷盘
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                var dueByTime = (DateTime.UtcNow - lastFlush) >= FlushInterval;

                // 未满且未到时间：等一小会儿再看，期间累积的记录保留在 batch 里。
                // 绝对不能 Clear —— 那是丢数据的根源。
                if (batch.Count < BatchSize && !dueByTime)
                {
                    var wait = FlushInterval - (DateTime.UtcNow - lastFlush);
                    if (wait > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                    // 等待期间可能又攒够了：回到循环顶部的 TryRead 继续补充
                    continue;
                }

                if (batch.Count > 0)
                {
                    await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
                lastFlush = DateTime.UtcNow;
            }

            // 取消/退出前：把残留全部落盘，不丢数据
            while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            if (batch.Count > 0)
            {
                await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }

            while (_channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
                if (batch.Count >= BatchSize)
                {
                    await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Record} 缓冲区后台循环异常退出", _recordName);
        }
    }

    private async Task WriteWithRetryAsync(List<T> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        Interlocked.Add(ref _inFlight, batch.Count);
        try
        {
            while (true)
            {
                // 最后一次尝试不受 ct 约束：即便应用正在退出，
                // 也要尽力把这批数据落盘，而不是静默丢弃。
                if (ct.IsCancellationRequested)
                {
                    TryWriteDirect(batch);
                    return;
                }

                try
                {
                    await Task.Run(() => _writeBatch(batch), CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "批量写入 {_Record} 失败，保留 {Count} 条重试",
                        _recordName,
                        batch.Count
                    );
                    try
                    {
                        await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 已取消：做一次不带 ct 的收尾尝试，然后放弃
                        TryWriteDirect(batch);
                        return;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Add(ref _inFlight, -batch.Count);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            // 不取消 CTS：让 worker 自然读到队列排空（TryComplete 后
            // WaitToReadAsync 返回 false），从而把 batch + 队列残留全部落盘。
            _channel.Writer.TryComplete();
            _worker?.Wait(TimeSpan.FromSeconds(5));

            // 兜底：worker 若在重试循环里卡住或已超时退出，这里在当前线程
            // 同步把队列剩余直接写掉。不依赖后台任务，不抛异常到调用方。
            if (_worker != null && !_worker.IsCompleted)
            {
                _logger.LogWarning(
                    "{_Record} 后台刷盘任务未在 5s 内结束，当前线程接管剩余记录",
                    _recordName
                );
            }
            DrainRemainingSynchronously();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭 {_Record} 缓冲区时异常", _recordName);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 同步把队列中残留的记录直接写库。作为 Dispose 的最后兜底，
    /// 覆盖「后台任务未及排空就退出」的场景（关机/注销时常见）。
    /// </summary>
    private void DrainRemainingSynchronously()
    {
        var pending = new List<T>();
        while (_channel.Reader.TryRead(out var item))
        {
            pending.Add(item);
            if (pending.Count >= 512)
            {
                TryWriteDirect(pending);
                pending.Clear();
            }
        }
        if (pending.Count > 0)
            TryWriteDirect(pending);
    }

    private void TryWriteDirect(List<T> batch)
    {
        try
        {
            _writeBatch(batch);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "退出时兜底写入 {_Record} 失败，{Count} 条记录未能保存",
                _recordName,
                batch.Count
            );
        }
    }
}
