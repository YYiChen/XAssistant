using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace XAssistant.Services;

/// <summary>
/// 启动自检。
///
/// 长期运行的工具最可能的故障是数据库文件损坏（断电、磁盘错误、
/// 被其他程序截断写入）。损坏后原本会直接在第一次写库时抛异常，
/// 而启动路径此前没有异常保护 —— 用户看到的是「程序打不开了」。
///
/// 这里在启动早期做一次完整性检查，损坏的库**改名保留**后重建，
/// 既不让程序卡死，也不丢掉原始文件（万一还能人工恢复）。
/// </summary>
public static class StartupSelfCheck
{
    /// <summary>自检结果。</summary>
    /// <param name="RecoveredDatabases">因损坏而被重命名重建的数据库名。</param>
    /// <param name="UsingFallbackFolder">数据目录是否降级到了临时目录。</param>
    public sealed record Outcome(
        IReadOnlyList<string> RecoveredDatabases,
        bool UsingFallbackFolder
    );

    private static readonly string[] Databases = { "key_data.db", "click_data.db", "app_usage.db" };

    public static Outcome Run(ILogger logger)
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        var recovered = new List<string>();

        // ── 数据目录可写性 ──
        if (AppDataPathHelper.IsUsingFallbackFolder)
        {
            logger.LogWarning(
                "标准数据目录不可写（预期位置：{Preferred}），已降级到 {Fallback}。"
                    + "注意：临时目录可能被系统清理，数据不保证长期保存。",
                AppDataPathHelper.PreferredFolder,
                folder
            );
        }

        // ── 数据库完整性 ──
        foreach (string name in Databases)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path))
                continue; // 尚未创建，属正常（首次运行）

            if (IsHealthy(path))
                continue;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = $"{path}.corrupt-{stamp}";

            try
            {
                // 主库与 WAL/SHM 一起改名：它们描述同一份数据，
                // 只移走主库会让残余的 WAL 与新库不匹配。
                File.Move(path, backup);
                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    string side = path + suffix;
                    if (File.Exists(side))
                        File.Move(side, backup + suffix);
                }

                recovered.Add(name);
                logger.LogError(
                    "数据库 {Name} 未通过完整性检查，已重命名为 {Backup} 并在后续访问时重建。"
                        + "原始文件已保留，如需人工抢救可从该文件尝试。",
                    name,
                    Path.GetFileName(backup)
                );
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "处理疑似损坏的数据库 {Name} 时失败，将继续尝试正常启动", name);
            }
        }

        return new Outcome(recovered, AppDataPathHelper.IsUsingFallbackFolder);
    }

    /// <summary>
    /// 用 SQLite 自带的 integrity_check 判断数据库是否可正常使用。
    /// 正常返回 "ok"；其余情况（含无法打开）一律视为不健康。
    /// </summary>
    private static bool IsHealthy(string path)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";

            // integrity_check 正常时只返回一行 "ok"
            return cmd.ExecuteScalar() is string result
                && string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
