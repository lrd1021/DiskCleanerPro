using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace DiskCleaner.Elevated
{
    /// <summary>
    /// 独立 Elevated Helper：以管理员身份执行少量高权限操作。
    /// 主程序默认以 asInvoker 运行，仅在需要时通过 UAC 拉起本 helper。
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("用法: DiskCleaner.Elevated <symlink|delete|uninstall|verifyaudit> ...");
                return 1;
            }

            var verb = args[0].ToLowerInvariant();

            // §4-2 调用方鉴权：仅允许由本应用（同证书签名）进程拉起，
            // 拒绝任意第三方 / 手动双击方式复用本提权原语。失败闭锁并写审计。
            if (!AuthorizeCaller())
            {
                Console.Error.WriteLine("调用方鉴权失败：Elevated Helper 仅可由本应用（同签名）拉起");
                Audit("caller-auth", string.Join(" ", args), "blocked-unauthorized-caller", 1);
                return 0x4C9; // 自定义退出码：调用方未授权
            }

            // 自检：写操作必须以管理员运行；verifyaudit 为只读验证，无需提权
            if (verb != "verifyaudit" && !IsElevated())
            {
                Console.Error.WriteLine("Elevated helper 必须以管理员权限运行");
                return 0x4C7; // ERROR_CANCELLED
            }

            try
            {
                return verb switch
                {
                    "symlink" => Symlink(args),
                    "delete" => Delete(args),
                    "uninstall" => Uninstall(args),
                    "verifyaudit" => VerifyAudit(args),
                    _ => UnknownCommand()
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"操作失败: {ex.Message}");
			Audit("main", string.Join(" ", args), "exception: " + ex.Message, 1);
			return 1;
            }
        }

        private static int UnknownCommand()
        {
            Console.Error.WriteLine("未知命令");
            return 1;
        }

        // ── symlink <linkPath> <targetPath> ──
        private static int Symlink(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("用法: symlink <linkPath> <targetPath>");
                return 1;
            }

            var linkPath = args[1];
            var targetPath = args[2];

            if (!IsLocalPath(linkPath) || !IsLocalPath(targetPath))
            {
                Console.Error.WriteLine("拒绝创建指向远程/URL 路径的符号链接");
                Audit("symlink", $"{linkPath} -> {targetPath}", "blocked-remote", 1);
                return 1;
            }

            if (IsProtectedRoot(linkPath))
            {
                Console.Error.WriteLine("拒绝在受保护根目录创建符号链接");
                Audit("symlink", $"{linkPath} -> {targetPath}", "blocked-protected", 1);
                return 1;
            }

            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                Console.Error.WriteLine("链接路径已存在");
                return 1;
            }

            Audit("symlink", $"{linkPath} -> {targetPath}", "start", 0);
			if (!CreateSymbolicLink(linkPath, targetPath, SYMLINK_FLAG_FILE))
            {
                var err = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"CreateSymbolicLink 失败: {err}");
				Audit("symlink", $"{linkPath} -> {targetPath}", "fail", err);
			return 1;
            }

            Audit("symlink", $"{linkPath} -> {targetPath}", "success", 0);
			return 0;
		}

		// ── delete <path> ──
        private static int Delete(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("用法: delete <path>");
                return 1;
            }

            var path = args[1];
            if (!IsLocalPath(path))
            {
                Console.Error.WriteLine("拒绝删除远程/URL 路径");
                return 1;
            }

            if (IsProtectedRoot(path))
            {
                Console.Error.WriteLine("拒绝删除受保护根目录");
			Audit("delete", path, "blocked-protected", 1);
			return 1;
            }

            Audit("delete", path, "start", 0);
			DeleteRecursive(path);
			Audit("delete", path, "success", 0);
			return 0;
        }

        private static void DeleteRecursive(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }

            if (!Directory.Exists(path)) return;

            // 顶层路径若是重解析点（junction/符号链接），整体不处理：不删除链接本身，也不跟随其目标
            if (IsReparsePoint(path)) return;

            DeleteDirectoryTree(path);
        }

        /// <summary>
        /// 递归删除目录树，但不跟随重解析点。用枚举层 Attributes 判断重解析点（非阻塞，不访问 junction/符号链接目标），
        /// 避免对子目录调用 File.GetAttributes 在失效/离线 junction 上阻塞（同主程序各扫描模块修复）。
        /// </summary>
        private static void DeleteDirectoryTree(string path)
        {
            var di = new DirectoryInfo(path);
            foreach (var fsi in di.EnumerateFileSystemInfos())
            {
                if (fsi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;   // 不删除、不递归重解析点

                if (fsi is DirectoryInfo subDir)
                    DeleteDirectoryTree(subDir.FullName);
                else
                {
                    try { File.Delete(fsi.FullName); }
                    catch (Exception ex)
                    {
                        // 删除失败不静默：记录审计，但继续删除其余文件，尽力而为（R5）
                        Audit("delete-file", fsi.FullName, "fail: " + ex.GetType().Name, Marshal.GetLastWin32Error());
                    }
                }
            }

            try { Directory.Delete(path, false); }
            catch (Exception ex)
            {
                // 目录非空（通常为前述文件删除失败所致）：记录审计，不抛异常（R5）
                Audit("delete-dir", path, "fail: " + ex.GetType().Name, Marshal.GetLastWin32Error());
            }
        }

        /// <summary>
        /// 非阻塞判断 path 是否为重解析点：从其父目录枚举中取该条目的 Attributes（不访问目标，不跟随 junction）。
        /// 替代 File.GetAttributes(path)（在失效/离线 junction 上可能阻塞）。
        /// </summary>
        private static bool IsReparsePoint(string path)
        {
            try
            {
                var parent = Directory.GetParent(path);
                if (parent == null) return false;
                var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(leaf)) return false;
                foreach (var fsi in parent.EnumerateFileSystemInfos(leaf))
                {
                    return fsi.Attributes.HasFlag(FileAttributes.ReparsePoint);
                }
                return false;
            }
            catch { return false; }
        }

        // ── uninstall [--force] "<full uninstall command line>" ──
        private static int Uninstall(string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
            {
                Console.Error.WriteLine("用法: uninstall [--force] \"<full command line>\"");
                return 1;
            }

            bool force = args.Length == 3 && args[1].Equals("--force", StringComparison.OrdinalIgnoreCase);
            var commandLine = args[args.Length - 1];

            if (!TryParseCommandLine(commandLine, out var fileName, out var arguments))
            {
                Console.Error.WriteLine("无法解析卸载命令");
                return 1;
            }

            if (!IsLocalPath(fileName))
            {
                Console.Error.WriteLine("拒绝启动远程/URL 路径的卸载程序");
                Audit("uninstall", commandLine, "blocked-remote", 1);
                return 1;
            }

            // 无论是否强制，都拒绝脚本宿主 / Shell 解释器，防止借 uninstall 执行任意命令
            if (IsInterpreter(Path.GetFileName(fileName)))
            {
                Console.Error.WriteLine("拒绝将脚本解释器作为卸载程序启动");
                Audit("uninstall", commandLine, "blocked-interpreter", 1);
                return 1;
            }

            string resolvedFile;
            // N2 加固：同时识别无扩展名的 "msiexec"，避免其误落入信任检查分支（报告 N2）
            bool isMsi = Path.GetFileName(fileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase);
            if (isMsi)
            {
                resolvedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                if (!IsSafeMsiUninstall(arguments))
                {
                    Console.Error.WriteLine("MsiExec 参数未通过安全校验");
                    return 1;
                }
            }
            else
            {
                try { resolvedFile = Path.GetFullPath(fileName); }
                catch { resolvedFile = fileName; }

                if (!File.Exists(resolvedFile))
                {
                    Console.Error.WriteLine($"未找到卸载程序: {resolvedFile}");
                    Audit("uninstall", commandLine, "not-found", 1);
                    return 1;
                }

                if (!force && !IsTrustworthyUninstaller(resolvedFile))
                {
                    Console.Error.WriteLine("卸载程序未通过受信任目录/签名校验");
                    Audit("uninstall", commandLine, "blocked-trust", 1);
                    return 1;
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = resolvedFile,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            Audit("uninstall", commandLine, force ? "start-forced" : "start", 0);
            using var process = Process.Start(psi);
            if (process == null)
            {
                Audit("uninstall", commandLine, "start-failed", 1);
                return 1;
            }
            process.WaitForExit();
            int code = process.ExitCode;
            bool ok = code == 0 || code == 3010;
            Audit("uninstall", commandLine, ok ? "success" : "fail", code);
            return ok ? 0 : code;
        }

        // ── 工具方法 ──

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>§4-2 调用方鉴权：取父进程，校验其 Authenticode 签名者指纹与自身一致（或 dev 下路径匹配），否则拒绝。</summary>
        private static bool AuthorizeCaller()
        {
            try
            {
                // 排障逃生口：极少数环境若 UAC 提权后父进程非主程序（如被 appinfo 链路改写），
                // 可设置此环境变量临时跳过鉴权（仅用于诊断，不应用于正式发布）。
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISKCLEANER_SKIP_CALLER_CHECK")))
                    return true;

                var ownPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(ownPath)) return false;

                var ownTp = GetSignerThumbprint(ownPath);
                var parentPid = GetParentPid();
                if (parentPid == 0) return false; // 无法确认父进程，fail-closed

                string parentPath = null;
                try
                {
                    using var p = Process.GetProcessById((int)parentPid);
                    parentPath = p.MainModule?.FileName;
                }
                catch { parentPath = null; }
                if (string.IsNullOrEmpty(parentPath)) return false;

                if (ownTp != null)
                {
                    // 已签名发布：要求父进程同证书签名（指纹一致），仅同证书的主程序可拉起本 helper。
                    var parentTp = GetSignerThumbprint(parentPath);
                    return parentTp != null && parentTp.Equals(ownTp, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // 未签名开发构建：要求父进程为预期主程序可执行文件（按路径/文件名匹配）。
                    var expected = Path.Combine(AppContext.BaseDirectory, "DiskCleanerPro.exe");
                    return parentPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(parentPath).Equals("DiskCleanerPro.exe", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        /// <summary>遍历进程快照取当前进程的父进程 PID；失败返回 0。</summary>
        private static uint GetParentPid()
        {
            var self = (uint)Process.GetCurrentProcess().Id;
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == (IntPtr)(-1)) return 0;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref entry))
                {
                    do
                    {
                        if (entry.th32ProcessID == self) return entry.th32ParentProcessID;
                    } while (Process32Next(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
            return 0;
        }

        internal static bool IsLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("\\\\")) return false;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        internal static bool IsProtectedRoot(string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd('\\');
                var root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;

                // 受保护的系统根目录（拒绝清单止血，B2）：
                // - protectedTreeNames：整棵子树受保护（系统目录，删除其下任何内容都危险）。
                // - protectedRootOnlyNames：仅根本身受保护（用户/数据卷根，防"误删整个卷"灾难性操作，
                //   但允许其下正常清理，如 C:\Users\xxx\AppData\Local\Temp）。
                // 例外：Windows 下的已知安全临时目录（如 Windows\Temp）经 IsAllowedUnderWindows 放行。
                var protectedTreeNames = new[] {
                    "Windows", "Program Files", "Program Files (x86)", "System Volume Information", "$Recycle.Bin",
                    "Boot", "PerfLogs", "Intel", "Config.Msi", "Recovery", "EFI", "MSOCache", "OEM"
                };
                var protectedRootOnlyNames = new[] { "Users", "ProgramData", "All Users", "Documents and Settings" };

                // 直接位于根目录下的受保护目录（如 C:\Windows）
                // 注意：root 与 dir 都需 TrimEnd('\\')，否则 "C:" 与 "C:\" 永不相等的旧 bug 会让守卫失效
                var dir = (Path.GetDirectoryName(full) ?? "").TrimEnd('\\');
                if (dir.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(full);
                    if (protectedTreeNames.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    if (protectedRootOnlyNames.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // 防御纵深：任何位于受保护目录之下的路径（如 C:\Windows\System32）同样拒绝
                foreach (var p in protectedTreeNames)
                {
                    var prefix = root + "\\" + p;
                    if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        // 例外：Windows 下的可清理临时目录（如 C:\Windows\Temp）不应被过度拦截，
                        // 否则临时文件清理、软件管家等合法功能无法操作。仅放开明确安全的临时目录。
                        if (p.Equals("Windows", StringComparison.OrdinalIgnoreCase) && IsAllowedUnderWindows(full, root))
                            continue;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // fail-closed：路径解析异常时保守视为受保护，拒绝操作（防止敌手利用异常绕过守卫）
                Console.Error.WriteLine($"IsProtectedRoot 解析异常，按受保护处理: {ex.Message}");
                return true;
            }
            return false;
        }

        // Windows 下允许操作的子目录白名单（仅限已知安全的临时/缓存目录，递归生效）
        private static readonly HashSet<string> AllowedUnderWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Temp"
        };

        private static bool IsAllowedUnderWindows(string full, string root)
        {
            var winDir = root + "\\Windows";
            if (!full.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
            var rel = full.Substring(winDir.Length + 1).TrimEnd('\\');
            foreach (var allowed in AllowedUnderWindows)
            {
                if (rel.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(allowed + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool TryParseCommandLine(string commandLine, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;
            if (string.IsNullOrWhiteSpace(commandLine)) return false;

            // 展开环境变量（%ProgramFiles%/%APPDATA% 等），注册表 UninstallString 常含此类变量，
            // 不展开会导致后续 File.Exists 误判“找不到文件”。主程序已尽量用修正命令，此处兜底。
            commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());

            var ptr = CommandLineToArgvW(commandLine, out int argc);
            if (ptr != IntPtr.Zero && argc > 0)
            {
                try
                {
                    var argv = new IntPtr[argc];
                    Marshal.Copy(ptr, argv, 0, argc);
                    var first = Marshal.PtrToStringUni(argv[0]);
                    // 修复：未加引号的含空格路径会被 CommandLineToArgvW 误切成多段
                    // （如 "C:\Program Files (x86)\Netease\UU\uninstall.exe" → 首段 "C:\Program"）。
                    // 若首段并非真实存在的文件，则贪心取磁盘上存在的最长可执行前缀作为文件名。
                    if (!File.Exists(first) && !commandLine.Trim().StartsWith("\""))
                    {
                        if (ResolveUnquotedExecutable(commandLine.Trim(), out var gName, out var gArgs))
                        {
                            fileName = gName;
                            arguments = gArgs;
                            return true;
                        }
                    }
                    fileName = first;
                    var sb = new StringBuilder();
                    for (int i = 1; i < argc; i++)
                        sb.Append(Marshal.PtrToStringUni(argv[i])).Append(' ');
                    arguments = sb.ToString().Trim();
                    return !string.IsNullOrEmpty(fileName);
                }
                finally
                {
                    LocalFree(ptr);
                }
            }

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

        // 当 CommandLineToArgvW 把"未加引号的含空格路径"误切成多段时，
        // 贪心取磁盘上真实存在的最长可执行前缀作为文件名（常见于卸载字符串：C:\Program Files (x86)\X\uninstall.exe /S）。
        private static bool ResolveUnquotedExecutable(string commandLine, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;
            if (commandLine.StartsWith("\"")) return false;
            var parts = commandLine.Split(' ');
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(parts[i]);
                var candidate = sb.ToString();
                var ext = Path.GetExtension(candidate);
                if ((ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) &&
                    File.Exists(candidate))
                {
                    fileName = candidate;
                    arguments = (i + 1 < parts.Length)
                        ? string.Join(" ", parts, i + 1, parts.Length - i - 1)
                        : "";
                    return true;
                }
            }
            return false;
        }

        internal static bool IsTrustworthyUninstaller(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return false; }

            // 收紧：脚本宿主/Shell 解释器绝不可被当作"卸载器"以管理员启动，
            // 否则可借 uninstall verb 执行 "cmd.exe /c del /q /s C:\Windows" 等任意命令
            if (IsInterpreter(Path.GetFileName(fullPath))) return false;

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

            return IsAuthenticodeSigned(fullPath);
        }

        // 脚本宿主 / Shell 解释器黑名单：绝不允许 uninstall verb 以管理员身份启动它们
        private static readonly HashSet<string> InterpreterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "pwsh.exe",
            "wscript.exe", "cscript.exe", "bash.exe", "sh",
            "mshta.exe", "rundll32.exe", "regsvr32.exe", "certutil.exe"
        };

        private static bool IsInterpreter(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return InterpreterNames.Contains(fileName);
        }

        internal static bool IsSafeMsiUninstall(string msiArgs)
        {
            if (string.IsNullOrWhiteSpace(msiArgs)) return false;
            var ptr = CommandLineToArgvW(msiArgs.Trim(), out int argc);
            if (ptr == IntPtr.Zero || argc == 0) return false;
            try
            {
                var argv = new IntPtr[argc];
                Marshal.Copy(ptr, argv, 0, argc);
                var tokens = new string[argc];
                for (int i = 0; i < argc; i++) tokens[i] = Marshal.PtrToStringUni(argv[i]);

                // 形式一：动作与目标连写，如 /X{GUID}（注册表常见形态，仅一个 token）
                if (tokens.Length == 1)
                {
                    return Regex.IsMatch(tokens[0] ?? "",
                        @"^[-/][xX]\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
                }

                // 形式二：卸载动作 + 目标 [+ 可选安全静默开关]
                // 第一个 token 必须是卸载动作（/x /uninstall），拒绝任何安装动作（/i /package 等）
                var action = tokens[0].ToLowerInvariant();
                if (action != "/x" && action != "-x" &&
                    action != "/uninstall" && action != "-uninstall")
                    return false;

                // 第二个 token 是目标：合法产品 GUID 或本地 .msi 文件（拒绝远程/URL 目标）
                var target = tokens[1].Trim('"', '\'');
                bool targetOk;
                if (action == "/x" || action == "-x")
                {
                    targetOk = Regex.IsMatch(target,
                        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
                }
                else // /uninstall：本地 .msi
                {
                    targetOk = target.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                               Regex.IsMatch(target, @"^[A-Za-z]:\\") &&
                               !target.StartsWith("\\\\") &&
                               !target.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                               !target.StartsWith("https", StringComparison.OrdinalIgnoreCase) &&
                               !target.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
                }
                if (!targetOk) return false;

                // 允许已知安全静默/日志开关；任何未知或潜在危险开关（如 /i /t /gv /package）一律拒绝。
                // 这样合法卸载（常带 /qn /quiet /norestart 等）不会被误拒，仍守住 RCE 防线。
                var safeSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "/qn", "/quiet", "/passive", "/qb", "/q", "/qr",
                    "/norestart", "/forcerestart", "/promptrestart", "/noreboot", "/nui"
                };
                for (int i = 2; i < tokens.Length; i++)
                {
                    var sw = tokens[i].Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(sw)) continue;
                    if (safeSwitches.Contains(sw)) continue;
                    if (sw.StartsWith("/l", StringComparison.OrdinalIgnoreCase)) continue;   // 日志：/l*v /le /lv 等
                    if (sw.StartsWith("/log", StringComparison.OrdinalIgnoreCase)) continue;
                    return false; // 未知/潜在危险开关
                }
                return true;
            }
            finally { LocalFree(ptr); }
        }

                // ── 审计日志（N1） + #13 哈希链完整性 ──
        private static readonly object _auditLock = new object();

        private static void Audit(string op, string detail, string result, int code)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanerPro", "logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "elevated-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var q = (char)0x22;
                var body = q + "ts" + q + ":" + q + DateTime.Now.ToString("O") + q + ","
                         + q + "op" + q + ":" + q + EscapeJson(op) + q + ","
                         + q + "detail" + q + ":" + q + EscapeJson(detail) + q + ","
                         + q + "result" + q + ":" + q + EscapeJson(result) + q + ","
                         + q + "code" + q + ":" + code;
                var canonical = "{" + body + "}";

                // #13：哈希链 —— 每行携带前一行的 SHA256，任何篡改均可被 verifyaudit 检测
                string prevHash;
                try
                {
                    var last = File.ReadLines(file).LastOrDefault();
                    prevHash = (last != null && TryExtractHash(last, out var h)) ? h : "";
                }
                catch { prevHash = ""; }

                var hash = ComputeChainHash(prevHash, canonical);
                var fullLine = "{" + body + "," + q + "_h" + q + ":" + q + hash + q + "}" + Environment.NewLine;

                lock (_auditLock)
                {
                    File.AppendAllText(file, fullLine);
                }
            }
            catch { }
        }

        private static bool TryExtractHash(string line, out string hash)
        {
            hash = null;
            var m = Regex.Match(line, "\"_h\"\\s*:\\s*\"([0-9a-fA-F]{64})\"");
            if (m.Success) { hash = m.Groups[1].Value; return true; }
            return false;
        }

        private static string ComputeChainHash(string prevHash, string canonical)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(prevHash + canonical));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // #13 验证：重算哈希链，返回是否完整；firstBrokenLine 为首个不匹配行号（从 1 起，-1 表示完整）。
        // 无 _h 字段的遗留行（升级前日志）跳过校验，兼容过渡期。供真机 R16 #13 点验。
        internal static bool VerifyAuditLog(string file, out int firstBrokenLine)
        {
            firstBrokenLine = -1;
            if (!File.Exists(file)) return true;
            string prev = "";
            int idx = 0;
            foreach (var line in File.ReadLines(file))
            {
                idx++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryExtractHash(line, out var h)) continue;   // 遗留行跳过
                var canonical = StripHashField(line);
                var expected = ComputeChainHash(prev, canonical);
                if (!expected.Equals(h, StringComparison.OrdinalIgnoreCase))
                {
                    firstBrokenLine = idx;
                    return false;
                }
                prev = h;
            }
            return true;
        }

        private static string StripHashField(string line)
        {
            // line = {... ,"_h":"<64hex>"}；去掉末尾的 ,"_h":"..." 段，复原写入时的 canonical。
            // 用字符串定位（而非正则）以规避边界匹配歧义。
            const string marker = ",\"_h\":";
            int idx = line.LastIndexOf(marker);
            if (idx < 0) return line;
            return line.Substring(0, idx) + "}";
        }

        private static int VerifyAudit(string[] args)
        {
            string file;
            if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
                file = args[1];
            else
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanerPro", "logs");
                file = Path.Combine(dir, "elevated-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            }

            if (!File.Exists(file))
            {
                Console.WriteLine("审计日志不存在，无需验证: " + file);
                return 0;
            }

            if (VerifyAuditLog(file, out int bad))
            {
                Console.WriteLine("OK: 审计日志哈希链完整 -> " + file);
                return 0;
            }
            Console.WriteLine($"BROKEN: 第 {bad} 行哈希不匹配，日志可能已被篡改 -> " + file);
            return 2;
        }

        // #12 修复：原 EscapeJson 仅转义 \ " CR LF TAB，未覆盖 BS(0x08)/FF(0x0C)
        // 及其它 <0x20 控制字符，极端情况下会破坏审计 JSON Lines。改为与 Logger.Escape
        // 一致的完整转义：\" \\ 以及所有控制字符（命名转义 + \uXXXX 兜底）。
        private static string EscapeJson(string s)
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
                        if (c < 0x20)
                        {
                            sb.Append((char)0x5C); sb.Append((char)0x75);
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ── P/Invoke ──

        private const int SYMLINK_FLAG_FILE = 0x0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ── 调用方鉴权 P/Invoke（§4-2）──
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        // 签名链校验改用 .NET X509Chain（见 VerifySignatureChain），不再依赖 WinVerifyTrust P/Invoke。

        /// <summary>内置已知签名者指纹（发布 CA 证书时填入；自签场景留空，由 signing-thumbprint.txt / 环境变量提供）。</summary>
        private static readonly string[] KnownSignerThumbprints = new string[0];

        private static bool IsAuthenticodeSigned(string filePath)
        {
            if (!File.Exists(filePath)) return false;

            // 1) 链可信：存在有效签名且可构建到受信任根
            if (!VerifySignatureChain(filePath)) return false;

            // 2) 钉死签名者指纹，防止"本机信任某根但非本应用证书"的伪造 helper（B3）
            var expected = LoadExpectedSignerThumbprints();
            if (expected.Count == 0)
            {
                Console.Error.WriteLine("IsAuthenticodeSigned: 未配置预期签名者指纹，已降级为仅链可信校验（建议配置 KnownSignerThumbprints 或 DISKCLEANER_EXPECTED_THUMBPRINTS）");
                return true;
            }

            var tp = GetSignerThumbprint(filePath);
            if (tp != null && expected.Contains(tp)) return true;

            // 软来源场景（仅 signing-thumbprint.txt / 环境变量）：链可信即通过，
            // 避免 .NET 8 自包含下签名者指纹提取异常导致误拦。
            if (KnownSignerThumbprints.Length == 0)
            {
                Console.Error.WriteLine($"IsAuthenticodeSigned: 链可信但签名者指纹未命中预期集（提取={(tp ?? "null")}），软来源场景降级为仅链可信");
                return true;
            }
            Console.Error.WriteLine($"IsAuthenticodeSigned: 签名者指纹未命中预期 CA 指纹集（提取={(tp ?? "null")}），拒绝");
            return false;
        }

        /// <summary>仅校验签名链是否可信（.NET X509Chain 构建到受信任根），不涉及指纹。</summary>
        private static bool VerifySignatureChain(string filePath)
        {
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(filePath);
                using var x2 = new X509Certificate2(cert);
                var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // 自签无 CRL，避免联网超时
                return chain.Build(x2);
            }
            catch { return false; }
        }

        /// <summary>提取 PE 文件 Authenticode 签名者证书指纹（大写十六进制）；无签名/失败返回 null。</summary>
        private static string GetSignerThumbprint(string filePath)
        {
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(filePath);
                using var x2 = new X509Certificate2(cert);
                return x2.Thumbprint;
            }
            catch { return null; }
        }

        /// <summary>加载预期签名者指纹集合：内置常量 ∪ 环境变量 DISKCLEANER_EXPECTED_THUMBPRINTS ∪ signing-thumbprint.txt（自签产物，gitignore）。</summary>
        private static HashSet<string> LoadExpectedSignerThumbprints()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in KnownSignerThumbprints)
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            var env = Environment.GetEnvironmentVariable("DISKCLEANER_EXPECTED_THUMBPRINTS");
            if (!string.IsNullOrWhiteSpace(env))
                foreach (var t in env.Split(';'))
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
            try
            {
                var f = Path.Combine(AppContext.BaseDirectory, "signing-thumbprint.txt");
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                        if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
            }
            catch { }
            return set;
        }
    }
}
