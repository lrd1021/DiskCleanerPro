using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DiskCleaner.Helpers;
using DiskCleaner.Services;

namespace DiskCleaner
{
    public partial class App : Application
    {
        // 单实例互斥：已存在实例时，再次双击不再开新窗口，而是激活已有窗口
        private static readonly string SingleInstanceMutexName =
            @"Global\DiskCleanerPro_SingleInstance_a1b2c3d4-5e6f-7890-abcd-ef1234567890";
        private static System.Threading.Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单实例检测：先尝试获取命名互斥量，已存在则说明程序已在运行
            bool createdNew;
            _singleInstanceMutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex = null; // 不拥有它，避免 OnExit 释放时抛异常
                ActivateExistingInstance();
                Shutdown();
                return;
            }

            // —— 崩溃落盘：原生 + 托管异常均写入 %TEMP%/DiskCleaner/crashes ——
            CrashLogger.Install();

            // —— DLL 劫持防护：禁止从当前目录搜索 DLL ——
            NativeMethods.SetDllDirectory("");

            // 启动时为保险箱做留存清理（超过30天自动清，best-effort，不阻塞 UI）
            _ = Task.Run(() =>
            {
                try { QuarantineService.PurgeOlderThan(TimeSpan.FromDays(30)); }
                catch { /* 静默失败，不影响启动 */ }
            });

            // 全局未处理异常捕获 — DEBUG + 运行时调试器双重防护
            DispatcherUnhandledException += (s, args) =>
            {
                bool showDetail = false;
#if DEBUG
                showDetail = true;
#else
                showDetail = System.Diagnostics.Debugger.IsAttached;
#endif
                CrashLogger.LogManaged(args.Exception, "DispatcherUnhandledException");
                MessageBox.Show(
                    showDetail
                        ? $"发生未处理异常：\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}"
                        : $"程序遇到意外错误，请重试。\n\n错误详情：{args.Exception.Message}",
                    "DiskCleaner Pro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var msg = args.ExceptionObject is Exception ex ? ex.Message : "未知错误";
#if DEBUG
                MessageBox.Show($"发生致命错误：\n\n{msg}", "致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
#else
                MessageBox.Show($"程序遇到致命错误，即将退出。\n\n{msg}", "DiskCleaner Pro", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
            };

            base.OnStartup(e);
        }

        /// <summary>找到已运行的 DiskCleaner 主窗口，还原并提到前台。</summary>
        private static void ActivateExistingInstance()
        {
            try
            {
                var current = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id == current.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { /* 激活失败则忽略，直接退出本实例 */ }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
