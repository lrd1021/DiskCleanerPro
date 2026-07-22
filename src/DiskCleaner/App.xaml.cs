using System;
using System.Windows;
using System.Windows.Threading;
using DiskCleaner.Helpers;

namespace DiskCleaner
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // —— DLL 劫持防护：禁止从当前目录搜索 DLL ——
            NativeMethods.SetDllDirectory("");

            // 全局未处理异常捕获 — DEBUG + 运行时调试器双重防护
            DispatcherUnhandledException += (s, args) =>
            {
                bool showDetail = false;
#if DEBUG
                showDetail = true;
#else
                showDetail = System.Diagnostics.Debugger.IsAttached;
#endif
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
    }
}
