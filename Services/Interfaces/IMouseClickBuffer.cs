using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 鼠标点击写入缓冲区，语义与 <see cref="IKeyPressBuffer"/> 相同。
/// 高频点击时同步写库同样会顶破 <c>LowLevelHooksTimeout</c>，故一并改造。
/// </summary>
public interface IMouseClickBuffer
{
    /// <summary>入队一条点击记录。非阻塞。</summary>
    void Enqueue(MouseClickRecord record);

    /// <summary>启动后台刷盘循环。幂等。</summary>
    void Start();

    /// <summary>等待队列排空且在途批次落盘。</summary>
    Task FlushAsync(TimeSpan timeout);
}
