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
            var helperPath = GetHelperPath(out var diagnostic);
            if (string.IsNullOrEmpty(helperPath) || !File.Exists(helperPath))
            {
                var pathHint = string.IsNullOrEmpty(helperPath) ? "(未知)" : helperPath;
                MessageBoxHelper.Show(
                    $"未找到 ElevatedHelper 或其未通过完整性校验，无法执行需要管理员权限的操作。\n\n" +
                    $"期望路径：{pathHint}\n" +
                    $"诊断信息：{diagnostic ?? "无"}\n\n" +
                    $"常见原因与处理：\n" +
                    $"1. 文件缺失：请确保 DiskCleaner.Elevated.exe 与 DiskCleanerPro.exe 位于同一目录。\n" +
                    $"2. 未签名：Release 版需要有效的 Authenticode 签名。请以管理员身份运行：\n" +
                    $"   powershell -ExecutionPolicy Bypass -File scripts\\rebuild-sign.ps1\n" +
                    $"3. 签名已过期/根证书未受信：重装信任根后重签；或临时在系统环境变量中设置 DISKCLEANER_SKIP_CALLER_CHECK=1 跳过门禁（仅调试）。",
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

        private static string GetHelperPath(out string diagnostic)
        {
            diagnostic = null;
            try
            {
                // 多层兜底探测应用部署目录（避免某些启动方式下 AppContext.BaseDirectory 为空）。
                // 顺序：AppContext.BaseDirectory -> 当前进程主模块所在目录 -> 执行程序集所在目录。
                var dirs = new System.Collections.Generic.List<string>();
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir)) dirs.Add(baseDir);

                try
                {
                    var mainModule = Process.GetCurrentProcess().MainModule;
                    if (mainModule != null)
                    {
                        var mainDir = Path.GetDirectoryName(mainModule.FileName);
                        if (!string.IsNullOrWhiteSpace(mainDir)) dirs.Add(mainDir);
                    }
                }
                catch (Exception ex) { Logger.Warning($"无法获取当前进程主模块目录: {ex.Message}"); }

                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    var asmLocation = asm?.Location;
                    if (!string.IsNullOrWhiteSpace(asmLocation))
                    {
                        var asmDir = Path.GetDirectoryName(asmLocation);
                        if (!string.IsNullOrWhiteSpace(asmDir)) dirs.Add(asmDir);
                    }
                }
                catch (Exception ex) { Logger.Warning($"无法获取执行程序集目录: {ex.Message}"); }

                if (dirs.Count == 0)
                {
                    diagnostic = "无法通过 AppContext.BaseDirectory / MainModule / Assembly.Location 任何方式定位应用目录。";
                    Logger.Error($"ElevatedHelper: {diagnostic}");
                    return null;
                }

                string helperPath = null;
                var tried = new StringBuilder();
                foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(dir, "DiskCleaner.Elevated.exe");
                    if (tried.Length > 0) tried.Append("; ");
                    tried.Append(candidate);
                    Logger.Info($"ElevatedHelper 候选路径: {candidate}");

                    // N2：位置校验——helper 必须位于应用部署目录，防止路径注入/替换
                    var helperDir = Path.GetFullPath(Path.GetDirectoryName(candidate) ?? "");
                    var baseDirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                    if (!string.Equals(helperDir, baseDirFull, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Warning($"ElevatedHelper 路径异常，跳过: helperDir={helperDir}, baseDir={baseDirFull}");
                        continue;
                    }

                    if (File.Exists(candidate))
                    {
                        helperPath = candidate;
                        break;
                    }
                }

                if (helperPath == null)
                {
                    diagnostic = $"在以下路径均未找到 DiskCleaner.Elevated.exe：{tried}。";
                    Logger.Error($"ElevatedHelper: {diagnostic}");
                    return null;
                }

                // N2：完整性/签名校验。Release 构建下签名失败直接阻断，防止 helper 被替换/篡改；
                // 调试环境可在沙箱中无有效证书链，DEBUG 下仅告警不阻断。
                if (!NativeMethods.IsAuthenticodeSigned(helperPath))
                {
#if DEBUG
                    diagnostic = $"{helperPath} 未通过 Authenticode 校验（沙箱/调试环境常见）。";
                    Logger.Warning($"ElevatedHelper: {diagnostic}");
#else
                    diagnostic = $"{helperPath} 未通过 Authenticode 校验。可能未签名，或签名根证书未受信。请以管理员运行 scripts\\rebuild-sign.ps1 重签。";
                    Logger.Error($"ElevatedHelper: {diagnostic}");
                    return null;
#endif
                }

                return helperPath;
            }
            catch (Exception ex)
            {
                diagnostic = $"路径/完整性校验异常：{ex.Message}";
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
