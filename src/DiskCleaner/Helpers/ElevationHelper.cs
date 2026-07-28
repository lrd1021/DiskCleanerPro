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
                var pathHint = string.IsNullOrEmpty(helperPath) ? "(未知)" : helperPath;
                MessageBoxHelper.Show(
                    $"未找到 ElevatedHelper 或其未通过完整性校验，无法执行需要管理员权限的操作。\n\n" +
                    $"期望路径：{pathHint}\n\n" +
                    $"请确保 DiskCleaner.Elevated.exe 与主程序位于同一目录；Release 版还需有效的 Authenticode 签名。",
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
                MessageBoxHelper.Show($"启动 ElevatedHelper 失败：{ex.Message}",
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

        /// <summary>
        /// 尝试以管理员权限执行卸载命令。
        /// <param name="uninstallCommandLine">完整卸载命令行。</param>
        /// <param name="userConfirmedOverride">用户已在主程序安全警告弹窗中点“是”确认跳过签名校验。</param>
        /// </summary>
        public static bool UninstallElevated(string uninstallCommandLine, bool userConfirmedOverride = false)
        {
            if (string.IsNullOrWhiteSpace(uninstallCommandLine)) return false;
            if (userConfirmedOverride)
                return RunElevated("uninstall", "--force", uninstallCommandLine) == 0;
            return RunElevated("uninstall", uninstallCommandLine) == 0;
        }

        private static string GetHelperPath()
        {
            try
            {
                // 使用 AppContext.BaseDirectory（应用部署目录）而非 Process.MainModule：
                // 通过 dotnet 宿主启动（dotnet DiskCleanerPro.dll / dotnet run）时，
                // MainModule 指向 dotnet.exe 所在目录，会把 helper 路径解析到错误位置。
                var dir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(dir))
                {
                    Logger.Error("ElevatedHelper: AppContext.BaseDirectory 为空");
                    return null;
                }
                var helperPath = Path.Combine(dir, "DiskCleaner.Elevated.exe");
                Logger.Info($"ElevatedHelper 候选路径: {helperPath}");

                // N2：位置校验——helper 必须位于应用部署目录，防止路径注入/替换
                var helperDir = Path.GetFullPath(Path.GetDirectoryName(helperPath) ?? "");
                var baseDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(helperDir, baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"ElevatedHelper 路径异常，拒绝启动: helperDir={helperDir}, baseDir={baseDir}");
                    return null;
                }

                // 先区分"文件不存在"与"签名校验失败"，避免误导性的 Authenticode 报错
                if (!File.Exists(helperPath))
                {
                    Logger.Error($"未找到 ElevatedHelper: {helperPath}");
                    return null;
                }

                // N2：完整性/签名校验。Release 构建下签名失败直接阻断，防止 helper 被替换/篡改；
                // 调试环境可在沙箱中无有效证书链，DEBUG 下仅告警不阻断。
                if (!NativeMethods.IsAuthenticodeSigned(helperPath))
                {
#if DEBUG
                    Logger.Warning($"ElevatedHelper 未通过 Authenticode 校验（沙箱/调试环境常见）: {helperPath}");
#else
                    Logger.Error($"ElevatedHelper 未通过 Authenticode 校验，拒绝启动: {helperPath}。如重新 publish 过，请重新运行 scripts/self-sign.ps1 -InstallTrust 签名。");
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
