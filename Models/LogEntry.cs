namespace XAssistant.Models;

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
    public string? SourceContext { get; set; }

    /// <summary>
    /// 仅类名（去掉命名空间前缀），供日志列表展示。
    ///
    /// 完整限定名如 "XAssistant.Services.ProcessUsageTracker" 有 43 个字符，
    /// 在列表里必然被截成 "XAssistant.Services.ProcessUsageTrac…" ——
    /// 而前缀对本项目而言是恒定冗余的，去掉后既放得下又更容易扫读。
    /// 完整名仍保留在 <see cref="SourceContext"/> 中，导出的日志文件用那一份。
    /// </summary>
    public string SourceShort
    {
        get
        {
            if (string.IsNullOrEmpty(SourceContext))
                return string.Empty;

            int lastDot = SourceContext.LastIndexOf('.');
            return lastDot >= 0 && lastDot < SourceContext.Length - 1
                ? SourceContext[(lastDot + 1)..]
                : SourceContext;
        }
    }
}
