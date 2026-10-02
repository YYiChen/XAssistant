using System.Collections.Generic;
using XAssistant.Models;

namespace XAssistant.Services.Interfaces;

public interface IKeyDatabaseService
{
    /// <summary>
    /// 单条写入（仅用于非热路径，如测试或补录）。热路径请使用 <see cref="IKeyPressBuffer"/>。
    /// </summary>
    void SaveKeyPress(KeyPressRecord record);

    /// <summary>
    /// 批量写入：单连接 + 单事务，供后台刷盘使用。表结构不变，无需迁移历史数据。
    /// </summary>
    void SaveKeyPressBatch(IReadOnlyList<KeyPressRecord> records);

    Dictionary<string, int> GetKeyCounts();
    Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to);
}
