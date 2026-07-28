using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 目录 junction（交接点）帮助类。
    /// junction 与符号链接不同：它不需要管理员权限，且对应用程序完全透明
    /// （应用访问原路径时，操作系统静默重定向到目标路径）。
    /// 这正是“把应用默认存 C 盘的数据目录搬到其它盘、又不影响应用使用”的标准做法
    /// （Steam 搬家、Windows 用户目录迁移均用此技术）。
    /// 底层调用系统内置的 `cmd /c mklink /J`，无需 UAC。
    /// </summary>
    public static class JunctionHelper
    {
        /// <summary>
        /// 在原位置创建一个指向 targetPath 的 junction（目录交接点）。
        /// junctionPath 必须尚不存在；targetPath 必须已存在。
        /// 成功返回 true。
        /// </summary>
        public static bool CreateJunction(string junctionPath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(junctionPath) || string.IsNullOrWhiteSpace(targetPath))
                return false;
            if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
                return false;
            if (!Directory.Exists(targetPath))
                return false;

            try
            {
                // 确保父目录存在（junction 本身不能创建父目录）
                var parent = Path.GetDirectoryName(junctionPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                var psi = new ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;
                process.WaitForExit();

                // 退出码 0 且 junction 确实存在
                return process.ExitCode == 0 && IsJunction(junctionPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"创建 junction 失败: {junctionPath} -> {targetPath}", ex);
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否为重解析点（junction / 符号链接 / 挂载点）。
        /// 不存在的路径返回 false。
        /// </summary>
        public static bool IsJunction(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!Directory.Exists(path) && !File.Exists(path)) return false;
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 删除一个 junction（交接点）本身，不跟随、不删除目标内容。
        /// 若路径不是 junction 或删除失败返回 false。
        /// </summary>
        public static bool DeleteJunction(string junctionPath)
        {
            if (string.IsNullOrWhiteSpace(junctionPath)) return false;
            if (!IsJunction(junctionPath)) return false;
            try
            {
                // 对 junction 调用 Directory.Delete 只删链接自身，不会删除目标目录内容
                Directory.Delete(junctionPath);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"删除 junction 失败: {junctionPath}", ex);
                return false;
            }
        }
    }
}
