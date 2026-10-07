using System;
using System.IO;

namespace XAssistant.Services
{
    /// <summary>
    /// 应用数据目录解析。
    ///
    /// 原实现只是 <c>Path.Combine</c> 加 <c>CreateDirectory</c>，一旦目录不可写
    /// （磁盘满、权限异常、被安全软件拦截）就会在后续任何一次写库时抛异常，
    /// 而启动路径此前没有异常保护 —— 表现为「双击图标闪一下，什么都没发生」。
    ///
    /// 现在会**实际验证可写性**，失败时降级到临时目录，并通过
    /// <see cref="IsUsingFallbackFolder"/> 让界面与日志能如实告知用户
    /// （降级目录可能被系统清理，数据不保证长期保存）。
    /// </summary>
    public static class AppDataPathHelper
    {
        private static readonly object Gate = new();
        private static string? _resolvedPath;

        /// <summary>
        /// 是否正在使用降级目录（临时目录）。
        /// 为 true 时说明标准数据目录不可写，数据不会长期保留。
        /// </summary>
        public static bool IsUsingFallbackFolder { get; private set; }

        /// <summary>标准数据目录（不考虑可写性），用于在提示中说明原本应在哪里。</summary>
        public static string PreferredFolder =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                FolderName()
            );

        public static string GetAppDataFolder()
        {
            if (_resolvedPath != null)
                return _resolvedPath;

            lock (Gate)
            {
                if (_resolvedPath != null)
                    return _resolvedPath;

                string preferred = PreferredFolder;
                if (EnsureWritable(preferred))
                {
                    _resolvedPath = preferred;
                    IsUsingFallbackFolder = false;
                    return preferred;
                }

                // 降级：临时目录。加后缀以便与标准目录区分，
                // 也避免与其他程序的临时文件混在一起。
                string fallback = Path.Combine(Path.GetTempPath(), FolderName() + "-fallback");
                try
                {
                    Directory.CreateDirectory(fallback);
                }
                catch
                {
                    // 连临时目录都建不了：返回首选路径，让调用方在真正写入时报错，
                    // 也好过在这里抛异常导致启动中断。
                    _resolvedPath = preferred;
                    IsUsingFallbackFolder = false;
                    return preferred;
                }

                _resolvedPath = fallback;
                IsUsingFallbackFolder = true;
                return fallback;
            }
        }

        /// <summary>
        /// 验证目录确实可写 —— 仅 CreateDirectory 成功还不够：
        /// 目录可能存在但没有写权限，那种情况要等到真正写文件才暴露。
        /// </summary>
        private static bool EnsureWritable(string path)
        {
            try
            {
                Directory.CreateDirectory(path);

                string probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}.tmp");
                using (
                    var fs = new FileStream(
                        probe,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                {
                    fs.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FolderName()
        {
#if DEBUG
            return "XAssistant_Dev"; // 开发版专用文件夹
#else
            return "XAssistant"; // 生产版专用文件夹
#endif
        }
    }
}
