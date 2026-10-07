// Services/Interfaces/IConfigurationService.cs
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

/// <summary>
/// 应用程序配置服务，管理各类持久化设置。
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// 当前应用配置（含速记唤起等各段设置）。
    /// </summary>
    AppSettings Settings { get; }
    /// <summary>
    /// 获取上次程序退出时是否处于鼠标点击录制状态的配置。
    /// </summary>
    bool GetRecordingAutoStart();

    /// <summary>
    /// 设置鼠标点击录制状态（用于下次启动自动恢复）。
    /// </summary>
    void SetRecordingAutoStart(bool isRecording);

    /// <summary>
    /// 获取上次程序退出时是否处于键盘按键录制状态的配置。
    /// </summary>
    bool GetKeyRecordingAutoStart();

    /// <summary>
    /// 设置键盘按键录制状态。
    /// </summary>
    void SetKeyRecordingAutoStart(bool autoStart);

    // ---------- 窗口位置/状态 ----------
    double GetWindowWidth();
    void SetWindowWidth(double width);

    double GetWindowHeight();
    void SetWindowHeight(double height);

    // ---------- 启动行为 ----------
    /// <summary>启动时是否直接最小化到托盘（不弹主窗口）。</summary>
    bool GetStartMinimized();

    void SetStartMinimized(bool minimized);

    /// <summary>
    /// 把当前设置写回磁盘。
    /// 直接改 <see cref="Settings"/> 上的属性后需要调用它才会持久化。
    /// </summary>
    void Save();

    // ---------- 日志面板状态 ----------
    bool GetIsLogExpanded();
    void SetIsLogExpanded(bool expanded);
}
