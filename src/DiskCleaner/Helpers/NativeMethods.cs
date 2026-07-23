using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_SILENT = 0x0004;

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

        // ── 命令行解析（正确处理引号，防止按空格切分误执行）──

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr hMem);

        // ── Authenticode 数字签名校验 ──

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("{00AAC56B-CD44-11d3-8A2E-00902781C19B}");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;          // 2 = WTD_UI_NONE
            public int fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public int dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int WinVerifyTrust(
            IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

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

        /// <summary>仅校验签名链是否可信（WinVerifyTrust == 0），不涉及指纹。</summary>
        private static bool VerifySignatureChain(string filePath)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            var trustData = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>()),
                dwStateAction = 0,
                dwProvFlags = 0,
                dwUIContext = 0
            };

            try
            {
                Marshal.StructureToPtr(fileInfo, trustData.pFile, false);
                int result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, ref trustData);
                return result == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (trustData.pFile != IntPtr.Zero)
                    Marshal.FreeHGlobal(trustData.pFile);
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
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT)
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
    }
}
