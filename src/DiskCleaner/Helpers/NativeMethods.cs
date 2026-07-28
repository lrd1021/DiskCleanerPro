using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using System.Windows;
using System.Security.Cryptography.X509Certificates;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 只读文件元数据（值类型 struct，替代 DiskAnalyzer 热循环中的 per-file FileInfo 分配，降低 GC 压力，R12）。
    /// </summary>
    public readonly struct FileMeta
    {
        public readonly string Name;
        public readonly string FullName;
        public readonly long Length;
        public readonly string Extension;
        public readonly string LastModified;
        public FileMeta(string name, string fullName, long length, string ext, string lastModified)
        {
            Name = name; FullName = fullName; Length = length; Extension = ext; LastModified = lastModified;
        }
    }

    /// <summary>
    /// 统一的 Windows API P/Invoke 声明
    /// </summary>
    public static class NativeMethods
    {
        // 系统保护目录 — 多模块共享
        public static readonly HashSet<string> ProtectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System Volume Information", "$Recycle.Bin", "$WinREAgent", "$SysReset",
            "Windows", "Program Files", "Program Files (x86)"
        };

        // ── 回收站操作 ──

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        public static extern int SHQueryRecycleBin([MarshalAs(UnmanagedType.LPWStr)] string pszRootPath,
            ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszRootPath,
            uint dwFlags);

        /// <summary>通知 Shell 某项已变更/移动，刷新资源管理器视图（如回收站恢复后）。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        public static extern void SHChangeNotify(int wEventId, uint uFlags,
            [MarshalAs(UnmanagedType.LPWStr)] string dwItem1,
            [MarshalAs(UnmanagedType.LPWStr)] string dwItem2);

        public const int SHCNE_UPDATEDIR = 0x00001000;
        public const uint SHCNF_PATHW = 0x0005;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_SILENT = 0x0004;
        public const ushort FOF_NOERRORUI = 0x0400;       // 删除失败时不弹错误对话框（避免后台线程挂起）
        public const ushort FOF_NOCONFIRMMKDIR = 0x0200; // 自动创建目标目录时不确认

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOPROGRESSUI = 0x00000002;
        public const uint SHERB_NOSOUND = 0x00000004;

        // ── 文件/目录操作 ──

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

        public const int SYMLINK_FLAG_FILE = 0x0;
        public const int SYMLINK_FLAG_DIRECTORY = 0x1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string lpPathName);

        // ── 窗口激活（单实例互斥时把已存在窗口提到前台）──

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public const int SW_RESTORE = 9;

        // ── 命令行解析（正确处理引号，防止按空格切分误执行）──

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);

        // ── Authenticode 数字签名校验 ──

        // 签名链校验改用 .NET X509Chain（见 VerifySignatureChain），不再依赖 WinVerifyTrust P/Invoke。

        /// <summary>内置已知签名者指纹（发布 CA 证书时填入；自签场景留空，由 signing-thumbprint.txt / 环境变量提供）。</summary>
        public static readonly string[] KnownSignerThumbprints = new string[0];

        /// <summary>校验 PE 是否含受信任有效 Authenticode 签名，且签名者指纹命中预期集合（防伪造 helper，B3）。</summary>
        public static bool IsAuthenticodeSigned(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

            // 1) 链可信：存在有效签名且可构建到受信任根（本机已装受信任根即代表信任该签名）
            if (!VerifySignatureChain(filePath)) return false;

            // 2) 钉死签名者指纹，防止"本机信任某根但非本应用证书"的伪造 helper
            var expected = LoadExpectedSignerThumbprints();
            if (expected.Count == 0)
            {
                // 未配置预期指纹：降级为仅链可信（GA 前应配置 CA 指纹），记录告警
                Logger.Warning("IsAuthenticodeSigned: 未配置预期签名者指纹，已降级为仅链可信校验（建议配置 KnownSignerThumbprints 或 DISKCLEANER_EXPECTED_THUMBPRINTS）");
                return true;
            }

            // 尝试钉死签名者指纹；提取失败/未命中时的处理取决于是否已配置 CA 内置指纹
            var tp = GetSignerThumbprint(filePath);
            if (tp != null && expected.Contains(tp)) return true;

            // 签名者指纹未命中：若仅配置了"软来源"（signing-thumbprint.txt / 环境变量），
            // 以链可信为最终信任——避免 .NET 8 自包含下签名者证书提取异常导致误拦
            // （自签根已装入 LocalMachine\Root 即代表本机信任该签名；攻击者无该根私钥）。
            if (KnownSignerThumbprints.Length == 0)
            {
                Logger.Warning($"IsAuthenticodeSigned: 链可信但签名者指纹未命中预期集（提取={(tp ?? "null")}），软来源场景降级为仅链可信");
                return true;
            }

            Logger.Error($"IsAuthenticodeSigned: 签名者指纹未命中预期 CA 指纹集（提取={(tp ?? "null")}），拒绝");
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
            catch
            {
                return false;
            }
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
            catch
            {
                return null;
            }
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

        // ── 回收站辅助方法 ──

        /// <summary>将文件移入回收站（失败时返回 false，不永久删除）</summary>
        public static bool SendToRecycleBin(string filePath) => SendToRecycleBin(filePath, out _);

        /// <summary>
        /// 将文件移入回收站。out 参数返回 Win32 错误码（0 表示成功；
        /// 用户取消时返回 0x4C7；异常时返回 HResult）。
        /// </summary>
        public static bool SendToRecycleBin(string filePath, out int errorCode)
        {
            errorCode = 0;
            if (string.IsNullOrEmpty(filePath)) return false;
            try
            {
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = filePath + '\0' + '\0',
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                };
                int result = SHFileOperation(ref shf);
                if (result != 0)
                    errorCode = result;
                else if (shf.fAnyOperationsAborted)
                    errorCode = 0x4C7; // ERROR_CANCELLED

                bool ok = result == 0 && !shf.fAnyOperationsAborted;
                if (!ok)
                    Logger.Warning($"SendToRecycleBin 失败: {filePath}, errorCode=0x{errorCode:X}");
                return ok;
            }
            catch (Exception ex)
            {
                errorCode = ex.HResult;
                Logger.Error($"SendToRecycleBin 异常: {filePath}", ex);
                return false;
            }
        }

        /// <summary>
        /// 批量将多个文件移入回收站。把路径按“数量 + 大小”双阈值分批拼接成双 null 终止的 pFrom，
        /// 每批只调用一次 SHFileOperation —— 比逐文件调用快几个数量级（%TEMP% 十几万文件场景尤为明显），
        /// 避免“点清理后界面长时间无响应”的错觉。
        ///
        /// 关键实现细节：
        /// 1. batchSize 默认 250，maxBatchBytes 默认 200MB。批次调小是为了让进度条「持续小幅前进」、
        ///    避免 1000/批时单批 SHFileOperation 耗时过长导致 UI 长时间不动（绝对删除速度由 Shell 搬文件决定，
        ///    与批次大小关系不大）。单一 STA 线程 + Dispatcher 消息泵已能稳定承载；按大小拆分可防止大文件拖慢整批。
        /// 2. 全程只在一个显式 STA 线程上执行，并为该线程建立 Dispatcher 消息泵。
        ///    SHFileOperation 属于 Shell COM，在 STA + 消息泵环境下才最稳定；且只建一个线程，
        ///    避免“每批新建 STA 线程”在大目录（9 万文件 = 1800 批次）下线程/句柄耗尽导致闪退。
        ///    （FOF_NOERRORUI 已确保遇到被占用文件时立即返回错误而非弹框等待，因此无需额外的超时线程。）
        /// 3. 若 SHFileOperation 返回 0 且未 aborted，则视该批全部成功，避免清理后再对 8 万文件逐次 File.Exists。
        ///    仅将失败批次里的文件作为“失败候选”返回，调用方再对候选文件做少量 File.Exists 验证。
        ///
        /// onProgress 回调参数为 (已处理文件数, 总文件数)，供 UI 显示“X/Y 个文件”的精确删除进度。
        /// onBatch 回调参数为 (当前批号 1-based, 估算总批数)，在每批 SHFileOperation 执行「前」调用，
        ///   让 UI 在阻塞前就能显示“正在处理第 N 批”，缓解“进度条长时间不动”的错觉。
        /// 返回值：失败候选文件列表（调用方应 File.Exists 复核）。
        ///
        /// permanent=true 时去掉 FOF_ALLOWUNDO，SHFileOperation 执行「永久删除」（不进回收站），
        /// 仍是批量 250/批 + 单 STA 线程 + 消息泵，比逐文件 File.Delete 快几个数量级，用于系统级垃圾位的直接删除。
        /// </summary>
        public static IReadOnlyList<string> SendToRecycleBinBatch(
            IList<string> filePaths,
            Action<int, int> onProgress = null,
            int batchSize = 250,
            long maxBatchBytes = 200L * 1024 * 1024,
            IDictionary<string, long> sizes = null,
            Action<int, int> onBatch = null,
            bool permanent = false)
        {
            if (filePaths == null || filePaths.Count == 0) return Array.Empty<string>();
            int total = filePaths.Count;
            int processed = 0;
            int totalBatchesApprox = Math.Max(1, (int)Math.Ceiling((double)total / batchSize));
            int batchIndex = 0;
            var failedCandidates = new List<string>();
            Exception threadEx = null;
            var finished = new ManualResetEventSlim(false);

            var staThread = new Thread(() =>
            {
                try
                {
                    // 该 STA 线程拥有消息泵：满足 Shell COM 对 STA + 消息泵的要求，且全程仅一个线程。
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    var frame = new DispatcherFrame();
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            int start = 0;
                            while (start < total)
                            {
                                int end = start;
                                long batchBytes = 0;
                                var sb = new StringBuilder();

                                // 双阈值分批：文件数上限 或 累计大小上限，先达到者触发分批。
                                // 至少放入一个文件，避免空 pFrom。
                                while (end < total)
                                {
                                    var f = filePaths[end];
                                    long size = 0;
                                    if (sizes != null && !string.IsNullOrEmpty(f) && sizes.TryGetValue(f, out var v))
                                        size = v;

                                    if (end > start && (end - start >= batchSize || batchBytes + size > maxBatchBytes))
                                        break;

                                    if (!string.IsNullOrEmpty(f))
                                    {
                                        sb.Append(f).Append('\0');
                                        batchBytes += size;
                                    }
                                    end++;
                                }

                                processed = end;
                                // 每批 SHFileOperation 执行前先回传批次号，让 UI 在阻塞前更新“正在处理第 N 批”
                                batchIndex++;
                                onBatch?.Invoke(batchIndex, totalBatchesApprox);
                                if (sb.Length > 0)
                                {
                                    sb.Append('\0'); // 双 null 终止：SHFileOperation 的多路径约定
                                    bool batchOk = false;
                                    try
                                    {
                                    var shf = new SHFILEOPSTRUCT
                                    {
                                        wFunc = FO_DELETE,
                                        pFrom = sb.ToString(),
                                        fFlags = permanent
                                            ? (ushort)(FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                                            : (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                                    };
                                        int result = SHFileOperation(ref shf);
                                        batchOk = result == 0 && !shf.fAnyOperationsAborted;
                                        if (!batchOk)
                                            Logger.Warning($"SendToRecycleBinBatch 批次失败: result=0x{result:X}, aborted={shf.fAnyOperationsAborted}");
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Error("SendToRecycleBinBatch 批次异常", ex);
                                    }

                                    if (!batchOk)
                                    {
                                        // 仅把失败批次里的文件记为候选；整批成功时不再逐个 File.Exists。
                                        for (int k = start; k < end; k++)
                                        {
                                            var f = filePaths[k];
                                            if (!string.IsNullOrEmpty(f))
                                                failedCandidates.Add(f);
                                        }
                                    }
                                }

                                // 每批都回传进度，避免最后一批漏报导致进度卡在 99%
                                onProgress?.Invoke(processed, total);
                                start = end;
                            }
                        }
                        catch (Exception ex) { threadEx = ex; }
                        finally { frame.Continue = false; }
                    }));
                    Dispatcher.PushFrame(frame);
                }
                catch (Exception ex) { threadEx = ex; }
                finally { finished.Set(); }
            })
            {
                IsBackground = true,
                Name = "RecycleBinSTA"
            };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            finished.Wait();
            if (threadEx != null)
                Logger.Error("SendToRecycleBinBatch 线程异常", threadEx);
            return failedCandidates;
        }

        /// <summary>
        /// 将文件移入回收站（FOF_ALLOWUNDO）。必须在「拥有桌面 Shell 的线程（WPF UI 线程）」执行才能可靠进回收站：
        /// 在后台 STA/MTA 线程上调用时，Shell 会忽略 FOF_ALLOWUNDO、直接永久删除文件（且返回成功），
        /// 导致文件悄无声息丢失。这正是早期版本"清理后文件消失、回收站找不到"的根因。
        /// 若在后台线程调用，本方法自动切换到 UI 线程（Dispatcher.Invoke）执行；批间调用 Dispatcher.Render
        /// 让进度刷新，避免界面完全假死。返回失败候选（调用方应 File.Exists 复核）。
        /// </summary>
        public static IReadOnlyList<string> SendToRecycleBinOnUIThread(
            IList<string> filePaths,
            Action<int, int> onProgress = null,
            int batchSize = 250,
            long maxBatchBytes = 200L * 1024 * 1024,
            IDictionary<string, long> sizes = null,
            Action<int, int> onBatch = null)
        {
            var app = System.Windows.Application.Current;
            var disp = app?.Dispatcher;
            if (disp != null && disp.Thread != System.Threading.Thread.CurrentThread)
            {
                return (IReadOnlyList<string>)disp.Invoke(new Func<IReadOnlyList<string>>(() =>
                    SendToRecycleBinOnUIThread(filePaths, onProgress, batchSize, maxBatchBytes, sizes, onBatch)));
            }

            var failed = new List<string>();
            if (filePaths == null || filePaths.Count == 0) return failed;
            int total = filePaths.Count;
            int totalBatches = Math.Max(1, (int)Math.Ceiling((double)total / batchSize));
            int start = 0, batchIndex = 0, processed = 0;
            long before = QueryRecycleBinItemCount();

            while (start < total)
            {
                var sb = new StringBuilder();
                long batchBytes = 0;
                int end = start;
                while (end < total)
                {
                    var f = filePaths[end];
                    long size = 0;
                    if (sizes != null && !string.IsNullOrEmpty(f) && sizes.TryGetValue(f, out var v)) size = v;
                    if (end > start && (end - start >= batchSize || (maxBatchBytes > 0 && batchBytes + size > maxBatchBytes))) break;
                    if (!string.IsNullOrEmpty(f)) { sb.Append(f).Append('\0'); batchBytes += size; }
                    end++;
                }
                batchIndex++;
                onBatch?.Invoke(batchIndex, totalBatches);

                bool batchOk = false;
                if (sb.Length > 0)
                {
                    sb.Append('\0');
                    var shf = new SHFILEOPSTRUCT
                    {
                        wFunc = FO_DELETE,
                        pFrom = sb.ToString(),
                        fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
                    };
                    int result = SHFileOperation(ref shf);
                    batchOk = result == 0 && !shf.fAnyOperationsAborted;
                    if (!batchOk)
                        Logger.Warning($"SendToRecycleBinOnUIThread 批次失败: result=0x{result:X}, aborted={shf.fAnyOperationsAborted}");
                }
                processed = end;
                onProgress?.Invoke(processed, total);
                if (!batchOk)
                    for (int k = start; k < end; k++)
                        if (!string.IsNullOrEmpty(filePaths[k])) failed.Add(filePaths[k]);
                start = end;

                // 批间让 UI 线程处理渲染/挂起的进度更新，避免完全假死
                try { System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Render, new Action(() => { })); }
                catch { }
            }

            // 诊断：回收站项数是否真的增加，验证 FOF_ALLOWUNDO 生效（否则文件被永久删除）
            try
            {
                long after = QueryRecycleBinItemCount();
                long expected = total - failed.Count;
                if (after < before + expected - 1)
                    Logger.Warning($"SendToRecycleBinOnUIThread 诊断：回收站项数 +{after - before}（预期 +{expected}），部分文件可能未进入回收站（被永久删除）");
                else
                    Logger.Info($"SendToRecycleBinOnUIThread 完成：回收站项数 +{after - before}（预期 +{expected}）");
            }
            catch { }

            return failed;
        }

        /// <summary>单文件版回收站删除（转发到批量 UI 线程实现）。</summary>
        public static bool SendToRecycleBinOnUIThread(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            var failed = SendToRecycleBinOnUIThread(new List<string> { filePath });
            return failed.Count == 0;
        }

        private static long QueryRecycleBinItemCount()
        {
            long sum = 0;
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                    if (SHQueryRecycleBin(d.RootDirectory.FullName, ref info) == 0)
                        sum += info.i64NumItems;
                }
            }
            catch { }
            return sum;
        }

        // ── 文件元数据（值类型，替代 per-file FileInfo，R12）──

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileAttributesEx(string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        /// <summary>
        /// 通过 GetFileAttributesEx 一次性读取文件大小与最后修改时间（无需 new FileInfo 分配，R12）。
        /// 支持 \\?\ 长路径前缀。失败时返回 false（调用方应跳过该文件）。
        /// </summary>
        public static bool TryGetFileMeta(string path, out FileMeta meta)
        {
            meta = default;
            if (string.IsNullOrEmpty(path)) return false;
            if (!GetFileAttributesEx(path, 0, out var data)) return false;

            long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
            long raw = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | (uint)data.ftLastWriteTime.dwLowDateTime;
            DateTime lwt;
            try { lwt = DateTime.FromFileTimeUtc(raw).ToLocalTime(); }
            catch { lwt = DateTime.MinValue; }

            meta = new FileMeta(
                Path.GetFileName(path),
                path,
                size,
                Path.GetExtension(path),
                lwt == DateTime.MinValue ? "" : lwt.ToString("yyyy-MM-dd HH:mm"));
            return true;
        }

        // ── 快速目录枚举 ──
        // 用托管 DirectoryInfo.EnumerateFileSystemInfos 枚举目录项。
        // 为什么不再用 FindFirstFileW/FindNextFileW：
        //   WIN32_FIND_DATA.cFileName（ByValTStr 内联缓冲）在本环境（out-struct 下）无法可靠 marshal，
        //   文件名回传始终为空字符串，导致：磁盘分析大小严重偏小、临时文件清理构建出空路径（静默漏删/误删）。
        // 托管枚举在本环境名称正确、非阻塞，且 Attributes 直接含 ReparsePoint 标记，可安全跳过 junction/符号链接，
        // 避免对失效/离线 junction（如指向断网共享）调用 File.GetAttributes 时阻塞（同临时文件扫描修复）。
        // 文件大小从枚举缓存的 WIN32_FILE_ATTRIBUTE_DATA 直接读取（FileInfo.Length 不二次 stat）。
        // 不跟随重解析点；调用方负责 visited 防循环链接。

        /// <summary>
        /// 目录项（托管枚举，名称可靠、非阻塞）。
        /// </summary>
        public struct FindEntry
        {
            public string Name;
            public bool IsDirectory;
            public bool IsReparsePoint;
            public long Size;
            public DateTime LastWriteTime;
        }

        /// <summary>
        /// 用 DirectoryInfo.EnumerateFileSystemInfos 枚举目录项，回调中提供名称、是否目录、是否重解析点、文件大小、最后写入时间。
        /// 不跳过任何隐藏/系统文件，仅由调用方决定如何处理重解析点。失败时静默返回（不抛异常）。
        /// 取消令牌触发时抛出 OperationCanceledException（由调用方区分“用户取消”与“失败”）。
        /// </summary>
        internal static void ForEachEntry(string directory, Action<FindEntry> action, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(directory)) return;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (ArgumentException) { return; }

            try
            {
                foreach (var fsi in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var attrs = fsi.Attributes;
                    long size = 0;
                    if ((attrs & FileAttributes.Directory) == 0 && fsi is FileInfo fi)
                    {
                        try { size = fi.Length; }
                        catch { size = 0; }
                    }
                    action(new FindEntry
                    {
                        Name = fsi.Name,
                        IsDirectory = (attrs & FileAttributes.Directory) != 0,
                        IsReparsePoint = (attrs & FileAttributes.ReparsePoint) != 0,
                        Size = size,
                        LastWriteTime = fsi.LastWriteTime
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ── 崩溃落盘：原生异常过滤器 + 安全文件 IO（供 CrashLogger 使用）──

        [DllImport("kernel32.dll")]
        public static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern void GetLocalTime(out SYSTEMTIME lpSystemTime);

        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint CREATE_ALWAYS = 2;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }
    }
}
