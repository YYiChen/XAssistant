using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 按键写入缓冲区：把数据库 I/O 移出低级钩子回调路径。
///
/// 设计要点：
/// - <see cref="Enqueue"/> 非阻塞（无界通道 TryWrite），钩子回调里只做这一件事；
/// - 刷盘由后台任务驱动，节奏为「累计 64 条」或「距上次刷盘超过 100ms」二者先到者触发；
/// - 刷盘失败时批次保留并重试，绝不丢记录；
/// - <see cref="Start"/> 在应用启动时调用一次；<c>Dispose</c> 会把队列残留全部落盘。
/// </summary>
public interface IKeyPressBuffer
{
    /// <summary>入队一条按键记录。非阻塞，永不因下游慢而卡住钩子回调。</summary>
    void Enqueue(KeyPressRecord record);

    /// <summary>启动后台刷盘循环。幂等。</summary>
    void Start();

    /// <summary>等待队列排空且在途批次落盘。</summary>
    Task FlushAsync(TimeSpan timeout);
}
