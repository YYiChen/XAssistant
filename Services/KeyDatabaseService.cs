using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

public class KeyDatabaseService : IKeyDatabaseService
{
    private static readonly string ConnectionString = InitializeConnectionString();

    private static string InitializeConnectionString()
    {
        string folder = AppDataPathHelper.GetAppDataFolder();
        Directory.CreateDirectory(folder);
        string dbPath = Path.Combine(folder, "key_data.db");
        return $"Data Source={dbPath}";
    }

    public KeyDatabaseService()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            @"
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=256;
            CREATE TABLE IF NOT EXISTS KeyPressRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Key TEXT NOT NULL,
                PressTime TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_keypress_presstime ON KeyPressRecords(PressTime);
            ";
        cmd.ExecuteNonQuery();
    }

    public void SaveKeyPress(KeyPressRecord record)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";
        cmd.Parameters.AddWithValue("@k", record.Key);
        cmd.Parameters.AddWithValue("@t", record.PressTime.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 批量写入：一次连接、一次事务、参数复用。
    /// 表结构与单条写入完全一致，历史数据零迁移。
    /// </summary>
    public void SaveKeyPressBatch(IReadOnlyList<KeyPressRecord> records)
    {
        if (records.Count == 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t)";

        var keyParam = cmd.Parameters.Add("@k", Microsoft.Data.Sqlite.SqliteType.Text);
        var timeParam = cmd.Parameters.Add("@t", Microsoft.Data.Sqlite.SqliteType.Text);

        // 先预编译语句，避免每条记录重新解析 SQL
        cmd.Prepare();

        foreach (var r in records)
        {
            keyParam.Value = r.Key;
            timeParam.Value = r.PressTime.ToString("o");
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Dictionary<string, int> GetKeyCounts()
    {
        return GetKeyCounts(null, null);
    }

    public Dictionary<string, int> GetKeyCounts(DateTime? from, DateTime? to)
    {
        var counts = new Dictionary<string, int>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();

        if (from.HasValue && to.HasValue)
        {
            cmd.CommandText =
                @"
                SELECT Key, COUNT(*) 
                FROM KeyPressRecords 
                WHERE PressTime >= @from AND PressTime < @to 
                GROUP BY Key";
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));
        }
        else
        {
            cmd.CommandText = "SELECT Key, COUNT(*) FROM KeyPressRecords GROUP BY Key";
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }
}
