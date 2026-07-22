using System;
using System.IO;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 轻量级结构化日志（JSON Lines），写入 %LocalAppData%\DiskCleanerPro\logs。
    /// 失败时静默，避免影响主流程。
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDirectory;

        static Logger()
        {
            try
            {
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DiskCleanerPro", "logs");
                Directory.CreateDirectory(LogDirectory);
            }
            catch
            {
                LogDirectory = null;
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Error(string message, Exception exception = null)
            => Write("ERROR", exception == null ? message : $"{message} | {exception}");

        private static void Write(string level, string message)
        {
            if (LogDirectory == null) return;
            try
            {
                var file = Path.Combine(LogDirectory, $"diskcleaner-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{{\"ts\":\"{DateTime.Now:O}\",\"level\":\"{level}\",\"msg\":\"{Escape(message)}\"}}{Environment.NewLine}";
                File.AppendAllText(file, line);
            }
            catch
            {
                // 日志写入失败不应打断业务
            }
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
