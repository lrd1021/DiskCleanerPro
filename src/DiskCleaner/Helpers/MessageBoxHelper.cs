using System;
using System.Threading;
using System.Windows;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 统一的 MessageBox 封装。
    /// 解决两类问题：
    /// 1) 后台线程（如 Task.Run 内）直接弹 MessageBox 时，窗口没有 owner 且跨线程，
    ///    在 z-order 上会偶尔落在主窗口后面，用户看不到（表现成“弹窗没出现/点了没反应”）。
    /// 2) 此处始终以 Application.Current.MainWindow 作为 owner，保证对话框永远位于主窗口之上；
    ///    后台线程通过 Dispatcher.Invoke 切回 UI 线程再弹，避免跨线程创建窗口的问题。
    /// </summary>
    public static class MessageBoxHelper
    {
        public static MessageBoxResult Show(
            string messageBoxText, string caption,
            MessageBoxButton button, MessageBoxImage icon)
            => Show(messageBoxText, caption, button, icon, MessageBoxResult.None);

        public static MessageBoxResult Show(
            string messageBoxText, string caption,
            MessageBoxButton button, MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            var app = Application.Current;
            if (app == null)
                return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);

            if (app.Dispatcher.Thread == Thread.CurrentThread)
                return MessageBox.Show(app.MainWindow, messageBoxText, caption, button, icon, defaultResult);

            return (MessageBoxResult)app.Dispatcher.Invoke(new Func<MessageBoxResult>(() =>
                MessageBox.Show(app.MainWindow, messageBoxText, caption, button, icon, defaultResult)));
        }
    }
}
