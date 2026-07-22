using System;
using System.IO;
using System.Text;

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
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case (char)0x22: sb.Append((char)0x5C); sb.Append((char)0x22); break;
                    case (char)0x5C: sb.Append((char)0x5C); sb.Append((char)0x5C); break;
                    case (char)0x0D: sb.Append((char)0x5C); sb.Append((char)0x72); break;
                    case (char)0x0A: sb.Append((char)0x5C); sb.Append((char)0x6E); break;
                    case (char)0x09: sb.Append((char)0x5C); sb.Append((char)0x74); break;
                    case (char)0x08: sb.Append((char)0x5C); sb.Append((char)0x62); break;
                    case (char)0x0C: sb.Append((char)0x5C); sb.Append((char)0x66); break;
                    default:
                        if (c < 0x20) { sb.Append((char)0x5C); sb.Append((char)0x75); sb.Append(((int)c).ToString("x4")); }
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }



    }
}
