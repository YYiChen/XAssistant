using System;
using System.IO;
using System.Reflection;

namespace XAssistant.Services;

/// <summary>
/// 应用级元信息（版本、构建日期、仓库地址）。
///
/// 抽成一处是因为侧边栏与设置页都要显示版本号 ——
/// 之前项目里完全没有版本号，「关于」无处可显示，出问题时也无法确认用户跑的是哪一版。
/// </summary>
public static class AppInfo
{
    /// <summary>形如 "1.1.0"。取自 csproj 的 &lt;Version&gt;。</summary>
    public static string VersionText { get; } = ResolveVersion();

    /// <summary>主程序文件的最后写入时间，作为构建日期的近似值。</summary>
    public static string BuildDateText { get; } = ResolveBuildDate();

    public const string RepositoryUrl = "https://github.com/YYiChen/XAssistant";

    private static string ResolveVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "未知" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "未知";
        }
    }

    private static string ResolveBuildDate()
    {
        try
        {
            // 用 ProcessPath 而非 Assembly.Location：后者在单文件发布时为空串
            string? exe = Environment.ProcessPath;
            return !string.IsNullOrEmpty(exe) && File.Exists(exe)
                ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd")
                : "未知";
        }
        catch
        {
            return "未知";
        }
    }
}
