using System;
using System.Diagnostics;
using System.IO;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 安全打开文件资源管理器：拒绝 UNC/URL 路径，使用显式 ProcessStartInfo。
    /// </summary>
    public static class ExplorerHelper
    {
        public static bool OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var trimmed = path.Trim('"', ' ').TrimEnd('\\', '/');

            // 拒绝网络/远程目标与 URL 协议
            if (trimmed.StartsWith("\\\\")) return false;
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var fullPath = Path.GetFullPath(trimmed);
                if (!Directory.Exists(fullPath)) return false;

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
