namespace XAssistant.Models;

public class AppSettings
{
    public RecordingSettings Recording { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public QuickNoteSettings QuickNote { get; set; } = new();
}

public class QuickNoteSettings
{
    public string HotKey { get; set; } = "Win+Numpad0"; // 速记全局热键（默认 Win+小键盘0）
    public string? ConnectionString { get; set; } // 速记库 PostgreSQL 连接串（复制自 xapp .env 的 DATABASE_URL，直插 quick_notes 表）；为空时自动读取 DatabaseUrlEnvPath 指向的 .env
    public string? DatabaseUrlEnvPath { get; set; } // xapp .env 路径；ConnectionString 未配置时自动读取其 DATABASE_URL（默认 C:\xapp-2026-06-30\.env）
    public string? SpaBaseUrl { get; set; } // xapp SPA 基址；null 则按构建配置默认（Debug :3009 / Release :9009）
    public int WindowWidth { get; set; } = 420;
    public int WindowHeight { get; set; } = 560;
}

public class RecordingSettings
{
    // 个人自用默认：程序启动即自动开始记录，不需手动点「开始记录」。
    // 注意：这两个开关指的是「启动时自动开启录制」，与「开机自启注册表」是两件事，
    // 后者由 StartupService 管理（在 OnStartup 时自动注册）。
    public bool AutoStartRecording { get; set; } = true; // 鼠标点击录制自动启动
    public bool AutoStartKeyRecording { get; set; } = true; // 键盘按键录制自动启动
}

public class GeneralSettings
{
    // 启动时直接最小化到托盘，不弹主窗口（静默后台运行的前提）。
    // 托盘图标左键单击可恢复显示。
    public bool StartMinimized { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 720;
    public bool IsLogExpanded { get; set; } = false;
}
