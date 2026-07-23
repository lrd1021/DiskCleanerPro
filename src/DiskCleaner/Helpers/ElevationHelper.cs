using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 按需提权帮助类：主程序默认 asInvoker，需要高权限时拉起 ElevatedHelper。
    /// </summary>
    public static class ElevationHelper
    {
        /// <summary>当前进程是否已以管理员运行</summary>
        public static bool IsElevated
        {
            get
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>
        /// 运行 ElevatedHelper 命令。若当前未提权，会通过 UAC 弹窗请求用户授权。
        /// 返回 helper 进程的退出码（0 通常表示成功）。
        /// </summary>
        public static int RunElevated(string command, params string[] args)
        {
            var helperPath = GetHelperPath();
            if (string.IsNullOrEmpty(helperPath) || !File.Exists(helperPath))
            {
                MessageBox.Show("未找到 ElevatedHelper，无法执行需要管理员权限的操作。",
                    "DiskCleaner Pro", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }

            var arguments = new StringBuilder(command);
            foreach (var a in args)
                arguments.Append(' ').Append(EscapeArgument(a));

            Logger.Info($"elevated request: {command} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = arguments.ToString(),
                UseShellExecute = true,
                Verb = IsElevated ? null : "runas",
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return 1;
                process.WaitForExit();
                Logger.Info($"elevated result: {command} exit={process.ExitCode}");
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 用户取消 UAC
                Logger.Info($"elevated cancelled (UAC): {command}");
                return 1223;
            }
            catch (Exception ex)
            {
                Logger.Error($"启动 ElevatedHelper 失败: {command}", ex);
                MessageBox.Show($"启动 ElevatedHelper 失败：{ex.Message}",
                    "DiskCleaner Pro", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        /// <summary>判断路径是否位于 Windows / Program Files 等受保护根目录下</summary>
        public static bool IsProtectedPath(string path)
        {
            try
            {
                // 委托给 Elevated helper 的权威实现，统一为「根目录或受保护目录之下」语义（N3）。
                return DiskCleaner.Elevated.Program.IsProtectedRoot(path);
            }
            catch { return false; }
        }

        public static bool DeleteElevated(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return RunElevated("delete", path) == 0;
        }

        /// <summary>尝试以管理员权限创建文件符号链接</summary>
        public static bool CreateSymlinkElevated(string linkPath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath)) return false;
            return RunElevated("symlink", linkPath, targetPath) == 0;
        }

        /// <summary>尝试以管理员权限执行卸载命令</summary>
        public static bool UninstallElevated(string uninstallCommandLine)
        {
            if (string.IsNullOrWhiteSpace(uninstallCommandLine)) return false;
            return RunElevated("uninstall", uninstallCommandLine) == 0;
        }

        private static string GetHelperPath()
        {
            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(mainModule)) return null;
                var dir = Path.GetDirectoryName(mainModule);
                var helperPath = Path.Combine(dir, "DiskCleaner.Elevated.exe");

                // N2：位置校验——helper 必须位于主程序同目录，防止路径注入/替换
                if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(helperPath)),
                                    Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"ElevatedHelper 路径异常，拒绝启动: {helperPath}");
                    return null;
                }

                // N2：完整性/签名校验。Release 构建下签名失败直接阻断，防止 helper 被替换/篡改；
                // 调试环境可在沙箱中无有效证书链，DEBUG 下仅告警不阻断。
                if (!NativeMethods.IsAuthenticodeSigned(helperPath))
                {
#if DEBUG
                    Logger.Warning($"ElevatedHelper 未通过 Authenticode 校验（沙箱/调试环境常见）: {helperPath}");
#else
                    Logger.Error($"ElevatedHelper 未通过 Authenticode 校验，拒绝启动: {helperPath}");
                    return null;
#endif
                }

                return helperPath;
            }
            catch (Exception ex)
            {
                Logger.Error("ElevatedHelper 路径/完整性校验异常", ex);
                return null;
            }
        }

        private static string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
