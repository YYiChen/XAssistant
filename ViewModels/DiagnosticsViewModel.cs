using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XAssistant.Models;
using XAssistant.Services;
using XAssistant.Services.Interfaces;

namespace XAssistant.ViewModels;

/// <summary>
/// 诊断页。
///
/// 原先「应用日志」面板常驻在主界面底部，占 200px 高 —— 把开发者向的诊断信息
/// 摆在主线 UI 上，是"测试品"最明显的特征之一。这里把它收进独立页面：
/// 功能不减（筛选、清空仍然保留），并补充日志路径、打开目录、导出，
/// 让它从"界面噪音"变成"排障工具"。
/// </summary>
public partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly ILogBufferService _logBuffer;

    public DiagnosticsViewModel(ILogBufferService logBuffer)
    {
        _logBuffer = logBuffer;

        // 日志集合变化时，刷新筛选后的视图
        _logBuffer.LogEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FilteredLogs));
            OnPropertyChanged(nameof(VisibleCountText));
        };

        OnPropertyChanged(nameof(FilteredLogs));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    // ────────────── 日志浏览 ──────────────

    public ObservableCollection<LogEntry> AllLogs => _logBuffer.LogEntries;

    [ObservableProperty]
    private string _logLevelFilter = "All";

    public string[] LogLevelOptions { get; } =
        { "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    public IEnumerable<LogEntry> FilteredLogs =>
        LogLevelFilter == "All"
            ? AllLogs
            : AllLogs.Where(l => l.Level.Equals(LogLevelFilter, StringComparison.OrdinalIgnoreCase));

    partial void OnLogLevelFilterChanged(string value) => OnPropertyChanged(nameof(FilteredLogs));

    /// <summary>当前可见条数，给界面一个"这屏有多少条"的明确反馈。</summary>
    public string VisibleCountText
    {
        get
        {
            int shown = FilteredLogs.Count();
            int total = AllLogs.Count;
            return shown == total
                ? $"共 {total} 条"
                : $"显示 {shown} / {total} 条";
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logBuffer.LogEntries.Clear();
        OnPropertyChanged(nameof(FilteredLogs));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    [RelayCommand]
    private void RefreshLogs()
    {
        OnPropertyChanged(nameof(FilteredLogs));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    // ────────────── 文件位置 ──────────────

    /// <summary>数据目录（数据库、配置、日志都在此目录下）。</summary>
    public string DataFolderPath => AppDataPathHelper.GetAppDataFolder();

    public string LogFolderPath => Path.Combine(DataFolderPath, "logs");

    /// <summary>启动失败时写入的应急日志（不经 Serilog，仅启动异常时产生）。</summary>
    public string StartupErrorLogPath =>
        Path.Combine(LogFolderPath, "startup-error.log");

    public bool HasStartupErrors => File.Exists(StartupErrorLogPath);

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(DataFolderPath);

    [RelayCommand]
    private void OpenLogFolder() => OpenInExplorer(LogFolderPath);

    /// <summary>
    /// 导出当前日志文件。用「另存为」让用户决定位置，而不是写到桌面上 ——
    /// 个人自用工具不该在用户没要求的地方产生文件。
    /// </summary>
    [RelayCommand]
    private void ExportLogs()
    {
        try
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            string source = Path.Combine(LogFolderPath, $"xassistant-{today}.log");

            if (!File.Exists(source))
            {
                // 当天还没有日志文件时，退而求其次导出缓冲区内容
                ExportFromBuffer();
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出日志",
                FileName = $"xassistant-{today}.log",
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".log",
            };

            if (dialog.ShowDialog() == true)
            {
                File.Copy(source, dialog.FileName, overwrite: true);
                _exportHint = $"已导出到 {dialog.FileName}";
            }
            else
            {
                _exportHint = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _exportHint = $"导出失败：{ex.Message}";
        }

        OnPropertyChanged(nameof(ExportHint));
    }

    /// <summary>日志文件不存在时，把内存中缓冲的条目写成文件导出。</summary>
    private void ExportFromBuffer()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出日志（当前会话）",
            FileName = $"xassistant-{DateTime.Now:yyyyMMdd-HHmm}.log",
            Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            DefaultExt = ".log",
        };

        if (dialog.ShowDialog() != true)
        {
            _exportHint = string.Empty;
            return;
        }

        var lines = AllLogs.Select(l =>
            $"[{l.Timestamp:yyyy-MM-dd HH:mm:ss}] [{l.Level}] {l.SourceContext}: {l.Message}"
            + (string.IsNullOrEmpty(l.Exception) ? string.Empty : Environment.NewLine + l.Exception)
        );
        File.WriteAllLines(dialog.FileName, lines);
        _exportHint = $"已导出当前会话 {AllLogs.Count} 条到 {dialog.FileName}";
    }

    private string _exportHint = string.Empty;

    public string ExportHint => _exportHint;

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // 完全限定：项目同时引用 WinForms，避免与其他同名类型冲突
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                }
            );
        }
        catch
        {
            // 打不开资源管理器不是关键路径，静默忽略
        }
    }
}
