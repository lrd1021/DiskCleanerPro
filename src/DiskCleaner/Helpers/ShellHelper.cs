using System.Diagnostics;
using System.IO;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// Shell 相关辅助：在资源管理器中打开文件或目录（含“选中文件”）。
    /// </summary>
    public static class ShellHelper
    {
        /// <summary>
        /// 在资源管理器中定位到指定路径：
        /// ① 路径本身是目录 → 直接打开该目录；
        /// ② 路径是文件且存在 → 打开所在目录并选中该文件（/select）；
        /// ③ 路径是文件但已不存在 → 退而打开其所在目录。
        /// 任何异常静默忽略，不影响主流程。
        /// </summary>
        public static void RevealInExplorer(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                        {
                            UseShellExecute = true
                        });
                }
            }
            catch { /* 资源管理器未能打开则忽略 */ }
        }
    }
}
