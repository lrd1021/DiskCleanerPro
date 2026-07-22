using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 临时文件清理服务
    /// 安全扫描标准临时目录，删除时走回收站
    /// </summary>
    public class TempFileCleaner
    {
        /// <summary>进度回调 (0-100, 消息)；传 -1 为心跳</summary>
        public Action<int, string> OnProgress { get; set; }

        /// <summary>
        /// 初始化所有可清理类别
        /// </summary>
        public List<CleanTarget> GetDefaultTargets()
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return new List<CleanTarget>
            {
                new CleanTarget
                {
                    Name = "用户临时文件", Description = "用户目录下的临时文件 (%TEMP%)",
                    Category = "临时文件", Icon = "📄",
                    Paths = { Path.Combine(localApp, "Temp") }, IsSelected = true
                },
                new CleanTarget
                {
                    Name = "系统临时文件", Description = "Windows 系统临时目录",
                    Category = "临时文件", Icon = "📄",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") },
                    IsSelected = true
                },
                new CleanTarget
                {
                    Name = "缩略图缓存", Description = "资源管理器缩略图缓存，清理后首次打开文件夹会重新生成",
                    Category = "缓存", Icon = "🖼️",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\Explorer") }, IsSelected = false
                },
                new CleanTarget
                {
                    Name = "Windows 错误报告", Description = "程序崩溃时生成的错误报告队列",
                    Category = "日志", Icon = "📋",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\WER") }, IsSelected = true
                },
                new CleanTarget
                {
                    Name = "Windows 更新缓存", Description = "已下载的更新安装包，清理后不影响已安装的更新",
                    Category = "更新", Icon = "🔄",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download") },
                    IsSelected = true
                },
                new CleanTarget
                {
                    Name = "预取文件", Description = "程序预读取数据，系统会自动重新生成",
                    Category = "系统", Icon = "⚡",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch") },
                    IsSelected = false
                },
                new CleanTarget
                {
                    Name = "字体缓存", Description = "Windows 字体缓存服务数据",
                    Category = "缓存", Icon = "🔤",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\Explorer"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles") },
                    IsSelected = false
                },
                new CleanTarget
                {
                    Name = "DNS 缓存", Description = "DNS 解析缓存记录",
                    Category = "缓存", Icon = "🌐",
                    Paths = { "dns:" }, IsSelected = false
                }
            };
        }

        /// <summary>
        /// 扫描指定清理目标，计算可释放空间
        /// </summary>
        public async Task ScanAsync(List<CleanTarget> targets, CancellationToken ct = default)
        {
            int total = targets.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = targets[i];
                target.IsScanning = true;
                target.Status = "扫描中...";
                OnProgress?.Invoke((int)((float)i / total * 100), $"扫描：{target.Name}");

                try
                {
                    if (target.Paths.Any(p => p == "dns:"))
                    {
                        target.SizeBytes = 0;
                        target.FileCount = 0;
                        target.Status = "运行时清理";
                    }
                    else
                    {
                        long totalSize = 0;
                        int totalFiles = 0;
                        foreach (var path in target.Paths)
                        {
                            var (size, count) = await Task.Run(() => GetDirectorySize(path, ct), ct);
                            totalSize += size;
                            totalFiles += count;
                        }
                        target.SizeBytes = totalSize;
                        target.FileCount = totalFiles;
                        target.Status = $"共 {totalFiles} 个文件";
                    }
                }
                catch (OperationCanceledException)
                {
                    target.Status = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    target.Status = $"扫描失败：{ex.Message}";
                }
                finally
                {
                    target.IsScanning = false;
                }

                OnProgress?.Invoke((int)((float)(i + 1) / total * 100), $"扫描完成：{target.Name}");
            }
        }

        /// <summary>
        /// 清理选中的目标（文件删除走回收站，允许撤销）
        /// </summary>
        public async Task<(long freedBytes, int deletedCount)> CleanAsync(
            List<CleanTarget> targets, bool permanentDelete, CancellationToken ct = default)
        {
            long totalFreed = 0;
            int totalDeleted = 0;
            var selected = targets.Where(t => t.IsSelected && (t.SizeBytes > 0 || t.Paths.Contains("dns:"))).ToList();

            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = selected[i];
                OnProgress?.Invoke((int)((float)i / selected.Count * 100), $"清理：{target.Name}");

                // 特殊处理 DNS 缓存
                if (target.Paths.Contains("dns:"))
                {
                    bool ok = await FlushDnsAsync();
                    target.Status = ok ? "已清理" : "清理失败（DNS 服务不可用或无权限）";
                    continue;
                }

                foreach (var path in target.Paths)
                {
                    if (ct.IsCancellationRequested) break;
                    var (freed, deleted) = await Task.Run(
                        () => DeleteDirectoryContents(path, permanentDelete, ct), ct);
                    totalFreed += freed;
                    totalDeleted += deleted;
                }
                target.Status = "已清理";
                target.SizeBytes = 0;

                OnProgress?.Invoke((int)((float)(i + 1) / selected.Count * 100), $"已完成：{target.Name}");
            }

            return (totalFreed, totalDeleted);
        }

        private (long size, int count) GetDirectorySize(string path, CancellationToken ct)
        {
            long size = 0;
            int count = 0;
            if (!Directory.Exists(path)) return (0, 0);

            try
            {
                foreach (var file in EnumerateFilesSafe(path))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        size += new FileInfo(file).Length;
                        count++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return (size, count);
        }

        private IEnumerable<string> EnumerateFilesSafe(string path)
        {
            var stack = new Stack<string>();
            stack.Push(path);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                string[] files = null, dirs = null;

                try { files = Directory.GetFiles(current); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (files != null)
                    foreach (var f in files) yield return f;

                try { dirs = Directory.GetDirectories(current); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (dirs != null)
                    foreach (var d in dirs)
                    {
                        // 不跟随交接点/符号链接，避免枚举并删除其指向的目标
                        // （管理员权限下可能被 junction 指向系统目录，造成不可逆删除）
                        try
                        {
                            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                                continue;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        stack.Push(d);
                    }
            }
        }

        private (long freed, int deleted) DeleteDirectoryContents(string path, bool permanent, CancellationToken ct)
        {
            long freed = 0;
            int deleted = 0;
            if (!Directory.Exists(path)) return (0, 0);

            // 永久删除受保护目录时，整体走 ElevatedHelper（避免 asInvoker 下逐个文件失败）
            if (permanent && ElevationHelper.IsProtectedPath(path))
            {
                long size = 0;
                try { size = GetDirectorySize(path, ct).size; } catch { }
                if (ElevationHelper.DeleteElevated(path))
                    return (size, 1);
                return (0, 0);
            }

            foreach (var file in EnumerateFilesSafe(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    long fileSize = fi.Length;

                    if (permanent)
                    {
                        fi.Delete();
                    }
                    else
                    {
                        // 走回收站；失败则跳过（不静默永久删除）
                        if (!NativeMethods.SendToRecycleBin(file, out var err))
                        {
                            Logger.Warning($"回收站删除失败 [{file}]: 0x{err:X}");
                            continue;
                        }
                    }

                    freed += fileSize;
                    deleted++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // 清理空子目录（仅永久删除模式）；不跟随交接点，避免误删其目标目录
            if (permanent)
            {
                try
                {
                    var allDirs = new List<string>();
                    var dirStack = new Stack<string>();
                    dirStack.Push(path);
                    while (dirStack.Count > 0)
                    {
                        var cur = dirStack.Pop();
                        string[] sub = null;
                        try { sub = Directory.GetDirectories(cur); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        if (sub == null) continue;
                        foreach (var d in sub)
                        {
                            try
                            {
                                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                                    continue;
                            }
                            catch (IOException) { continue; }
                            catch (UnauthorizedAccessException) { continue; }
                            allDirs.Add(d);
                            dirStack.Push(d);
                        }
                    }

                    foreach (var dir in allDirs.OrderByDescending(d => d.Length))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                                Directory.Delete(dir, false);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return (freed, deleted);
        }

        private async Task<bool> FlushDnsAsync()
        {
            try
            {
                return await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "ipconfig.exe"),
                        Arguments = "/flushdns",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p == null) return false;
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                });
            }
            catch { return false; }
        }
    }
}
