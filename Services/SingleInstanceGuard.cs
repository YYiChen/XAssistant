using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace XAssistant.Services;

/// <summary>
/// 单实例守卫：保证同一用户会话内只运行一个采集进程。
///
/// 为什么必须有：键盘/鼠标钩子是**按进程**安装的。若允许重复启动，
/// 两个进程会各自装一套钩子，同一次按键被两个进程各记一条 ——
/// 数据直接翻倍。这类问题在统计数字上表现为「莫名其妙变多」，
/// 且因为两个进程都在正常写库，从数据本身很难看出原因。
///
/// 实现：命名互斥体判定首实例；非首实例通过命名管道请求首实例显示主窗口，
/// 然后自行退出（即「双击图标 = 唤起已有窗口」，这是桌面软件的通行语义）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// 互斥体名。刻意**不加** <c>Global\</c> 前缀：
    /// 创建全局命名对象需要 SeCreateGlobalPrivilege，普通用户不具备
    /// （只有管理员与服务默认持有）。本程序以普通权限运行，
    /// 用会话级（Local）命名空间即可满足「同一用户不重复启动」的需求。
    /// </summary>
    private const string MutexName = "XAssistant.SingleInstance.Mutex";

    /// <summary>与 UsageTracker 的管道区分开，避免混淆。</summary>
    private const string PipeName = "XAssistant.ShowWindow.Pipe";

    private const string ShowCommand = "SHOW";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;

    /// <summary>本进程是否为首实例（true 表示应继续正常启动）。</summary>
    public bool IsFirstInstance { get; private set; }

    /// <summary>
    /// 尝试成为首实例。返回 true 表示本进程可以继续启动；
    /// 返回 false 表示已有实例在运行，调用方应当唤起它并退出。
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            // initiallyOwned: true —— 创建成功即持有，无需再 WaitOne
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            IsFirstInstance = createdNew;
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者异常退出（例如崩溃）而遗留了互斥体。
            // 这种情况仍算拿到所有权，可以正常继续。
            IsFirstInstance = true;
            return true;
        }
        catch (Exception)
        {
            // 互斥体本身出问题（极少见）：宁可不阻止启动，也不要让程序打不开。
            // 退化为「不启用单实例保护」，功能优先。
            IsFirstInstance = true;
            return true;
        }
    }

    /// <summary>
    /// 开始监听来自后续实例的「显示窗口」请求（仅首实例调用）。
    /// </summary>
    /// <param name="onShowRequest">收到请求时的回调，会被派发到 UI 线程执行。</param>
    public void StartListening(Action onShowRequest)
    {
        if (!IsFirstInstance)
            return;

        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;

        _listenTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                    if (string.Equals(command, ShowCommand, StringComparison.Ordinal))
                        onShowRequest();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // 单次连接异常（客户端提前断开等）不应终止监听循环。
                    // 短暂退避后重建管道实例继续等待。
                    try
                    {
                        await Task.Delay(200, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        });
    }

    /// <summary>
    /// 请求已在运行的实例显示主窗口（由后续实例调用）。
    ///
    /// 带重试：首实例可能仍在启动过程中、监听尚未就绪。
    /// 这个窗口期通常只有几十到几百毫秒。
    /// </summary>
    /// <returns>是否成功送达请求。</returns>
    public static bool SignalFirstInstance(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out
                );
                client.Connect(600);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ShowCommand);
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(150); // 首实例可能还在建监听，稍等再试
            }
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _listenCts?.Cancel();
            // 不等待过久：监听循环会在下一次 WaitForConnectionAsync 取消时退出
            _listenTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        { /* 关闭阶段的异常无需处理 */
        }
        finally
        {
            _listenCts?.Dispose();
            _mutex?.Dispose();
        }
    }
}
