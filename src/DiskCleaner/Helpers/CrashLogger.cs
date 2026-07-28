using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 崩溃落盘：原生（Access Violation 等 corrupted-state）与托管未处理异常，
    /// 均写入 %TEMP%/DiskCleaner/crashes。
    /// 原生层只用纯 kernel32 IO，避免在异常过滤器上下文中调用托管/CRT 引发二次崩溃。
    /// </summary>
    public static class CrashLogger
    {
        private const uint EXCEPTION_EXECUTE_HANDLER = 1;
        private static readonly object _installLock = new object();
        private static bool _installed;
        private static ExceptionFilterDelegate _filter;
        private static IntPtr _prevFilter;
        private static string _crashDir = "";

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr ExceptionFilterDelegate(IntPtr pExceptionInfo);

        public static void Install()
        {
            if (_installed) return;
            lock (_installLock)
            {
                if (_installed) return;

                try
                {
                    _crashDir = Path.Combine(Path.GetTempPath(), "DiskCleaner", "crashes");
                    Directory.CreateDirectory(_crashDir);
                }
                catch { _crashDir = Path.GetTempPath(); }

                // 托管层
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    LogManaged(e.ExceptionObject as Exception,
                        "AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""));

                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    LogManaged(e.Exception, "TaskScheduler.UnobservedTaskException");
                    e.SetObserved();
                };

                // 原生层：注册顶层异常过滤器，并保留 .NET 运行时原有过滤器（如生成 dump）
                _filter = NativeFilter;
                _prevFilter = NativeMethods.SetUnhandledExceptionFilter(
                    Marshal.GetFunctionPointerForDelegate(_filter));

                _installed = true;
            }
        }

        private static IntPtr NativeFilter(IntPtr pExceptionInfo)
        {
            try { WriteNativeLog(pExceptionInfo); } catch { }
            if (_prevFilter != IntPtr.Zero)
            {
                try
                {
                    var prev = Marshal.GetDelegateForFunctionPointer<ExceptionFilterDelegate>(_prevFilter);
                    return prev(pExceptionInfo);
                }
                catch { }
            }
            return new IntPtr(EXCEPTION_EXECUTE_HANDLER);
        }

        /// <summary>托管未处理异常：写人类可读文本日志（含类型/消息/堆栈/线程）</summary>
        public static void LogManaged(Exception ex, string context)
        {
            try
            {
                var ts = DateTime.Now;
                var file = Path.Combine(_crashDir, $"exception-{ts:yyyyMMdd-HHmmss.fff}.log");
                var sb = new StringBuilder();
                sb.AppendLine($"[{ts:yyyy-MM-dd HH:mm:ss.fff}] {context}");
                sb.AppendLine($"CLR: {Environment.Version}  OS: {Environment.OSVersion}");
                sb.AppendLine($"Thread: {(Thread.CurrentThread.IsBackground ? "background" : "foreground")} Apartment={Thread.CurrentThread.GetApartmentState()}");
                var e = ex;
                int i = 0;
                while (e != null && i < 5)
                {
                    sb.AppendLine($"--- Exception[{i}] {e.GetType().FullName}: {e.Message}");
                    sb.AppendLine(e.StackTrace);
                    e = e.InnerException;
                    i++;
                }
                File.WriteAllText(file, sb.ToString());
            }
            catch { }
        }

        /// <summary>原生崩溃：仅用 kernel32 写一行文本（异常上下文安全）</summary>
        private static void WriteNativeLog(IntPtr pExceptionInfo)
        {
            uint code = 0;
            long addr = 0;
            if (pExceptionInfo != IntPtr.Zero)
            {
                // EXCEPTION_POINTERS.ExceptionRecord 位于 offset 0
                IntPtr pRec = Marshal.ReadIntPtr(pExceptionInfo, 0);
                if (pRec != IntPtr.Zero)
                {
                    code = (uint)Marshal.ReadInt32(pRec, 0);            // ExceptionCode
                    addr = Marshal.ReadIntPtr(pRec, 16).ToInt64();      // ExceptionAddress (x64 offset 0x10)
                }
            }

            NativeMethods.GetLocalTime(out NativeMethods.SYSTEMTIME st);
            string ts = $"{st.wYear:D4}-{st.wMonth:D2}-{st.wDay:D2} {st.wHour:D2}:{st.wMinute:D2}:{st.wSecond:D2}.{st.wMilliseconds:D3}";
            string msg = $"[{ts}] NATIVE CRASH ExceptionCode=0x{code:X8} FaultingAddress=0x{addr:X16}\n";

            // 纯 ASCII 转 byte[]，避免调用 Encoding（异常上下文安全）
            byte[] bytes = new byte[msg.Length];
            for (int i = 0; i < msg.Length; i++) bytes[i] = (byte)msg[i];

            string file = Path.Combine(_crashDir,
                $"native-{st.wYear:D4}{st.wMonth:D2}{st.wDay:D2}-{st.wHour:D2}{st.wMinute:D2}{st.wSecond:D2}.log");

            IntPtr h = NativeMethods.CreateFileW(file, NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ,
                IntPtr.Zero, NativeMethods.CREATE_ALWAYS, NativeMethods.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (h != (IntPtr)(-1))
            {
                NativeMethods.WriteFile(h, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
                NativeMethods.CloseHandle(h);
            }
        }
    }
}
