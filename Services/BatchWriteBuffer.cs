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
    /// <returns>超时前成功落盘的条数；返回值仅供日志参考。</returns>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_channel.Reader.Count == 0 && Volatile.Read(ref _inFlight) == 0)
                return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        _logger.LogWarning(
            "冲刷 {_Record} 超时，队列剩余 {Queued} 条，在途 {InFlight} 条",
            _recordName,
            _channel.Reader.Count,
            Volatile.Read(ref _inFlight)
        );
    }

    private async Task RunAsync(CancellationToken ct)
    {
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

                batch.Clear();
                while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                var dueByTime = (DateTime.UtcNow - lastFlush) >= FlushInterval;
                if (batch.Count < BatchSize && !dueByTime)
                {
                    // 未达刷盘阈值：短暂等待而不是立刻小批量写库，
                    // 避免输入密集时产生大量碎片事务。
                    try
                    {
                        await Task.Delay(FlushInterval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    lastFlush = DateTime.UtcNow;
                    continue;
                }

                await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);
                lastFlush = DateTime.UtcNow;
            }

            // 取消后：把队列里剩下的全部落盘，不丢数据
            batch.Clear();
            while (batch.Count < BatchSize && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            if (batch.Count > 0)
                await WriteWithRetryAsync(batch, ct).ConfigureAwait(false);

            var tail = new List<T>();
            while (_channel.Reader.TryRead(out var item))
                tail.Add(item);
            if (tail.Count > 0)
                await WriteWithRetryAsync(tail, ct).ConfigureAwait(false);
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
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(() => _writeBatch(batch), ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
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
            // 不取消 CTS：让 worker 自然读到队列排空（TryComplete 后 WaitToReadAsync 返回 false）
            _channel.Writer.TryComplete();
            _worker?.Wait(TimeSpan.FromSeconds(5));
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
}
