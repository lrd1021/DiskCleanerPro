using System;
using System.Globalization;

namespace DiskCleaner.Helpers
{
    public static class FileSizeFormatter
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>
        /// 将字节数格式化为可读字符串，如 "1.23 GB"
        /// </summary>
        public static string Format(long bytes)
        {
            if (bytes < 0) return "0 B";
            if (bytes == 0) return "0 B";

            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < Units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{(long)size} {Units[unitIndex]}"
                : $"{size.ToString("F2", CultureInfo.InvariantCulture)} {Units[unitIndex]}";
        }

        /// <summary>
        /// 将字节数格式化为简短形式，如 "1.2G"
        /// </summary>
        public static string FormatShort(long bytes)
        {
            if (bytes <= 0) return "0";
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < Units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size.ToString("F1", CultureInfo.InvariantCulture)}{Units[unitIndex][0]}";
        }
    }
}
