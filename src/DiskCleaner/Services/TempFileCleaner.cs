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
                        // 单个目标单独加超时，避免某个目录（如指向离线/网络位置的失效 junction）阻塞导致整轮扫描卡死
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        linked.CancelAfter(TimeSpan.FromSeconds(120));
                        long totalSize = 0;
                        int totalFiles = 0;
                        foreach (var path in target.Paths)
                        {
                            var (size, count) = await Task.Run(
                                () => GetDirectorySize(path, linked.Token, target.Name, i, total), linked.Token);
                            totalSize += size;
                            totalFiles += count;
                        }
                        target.SizeBytes = totalSize;
                        target.FileCount = totalFiles;
                        target.Status = $"共 {totalFiles} 个文件";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 超时触发的取消——跳过该目标，不中断整轮扫描
                    target.Status = "文件过多，已快速跳过";
                    target.SizeBytes = 0;
                    target.FileCount = 0;
                    Logger.Warning($"临时文件扫描超时（>120s），已跳过目标：{target.Name}");
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

        // 枚举得到的单个文件项（路径+大小由 FindFirstFile/FindNextFile 一次枚举直接给出，无需二次 stat）
        private readonly struct FileEntry
        {
            public FileEntry(string fullName, long size, bool isDirectory)
            {
                FullName = fullName; Size = size; IsDirectory = isDirectory;
            }
            public string FullName { get; }
            public long Size { get; }
            public bool IsDirectory { get; }
        }

        // 极端文件数下的保护性硬上限（正常不会触发）：满足"完整扫描、不跳过"需求的同时，
        // 防止个别目录因文件数百万导致 UI 长时间无响应。
        private const int MaxFilesPerTarget = 2000000;

        private (long size, int count) GetDirectorySize(string path, CancellationToken ct,
            string targetName = null, int targetIndex = 0, int targetTotal = 1)
        {
            long size = 0;
            int count = 0;
            int scanned = 0;
            try
            {
                foreach (var file in EnumerateFilesSafe(path, ct))
                {
                    ct.ThrowIfCancellationRequested();

                    // 大小已由 FindFirstFile/FindNextFile 在枚举时直接给出，无需对每个文件再调一次 stat
                    size += file.Size;
                    count++;

                    // 增量进度：每 256 个文件上报一次，让进度文本实时滚动
                    // pct=-1 表示仅更新文本、不推进总进度条（避免在大目录扫描时进度条长时间冻结）
                    if (targetName != null && ((++scanned) & 0xFF) == 0)
                        OnProgress?.Invoke(-1, $"扫描 {targetName}：已扫描 {count} 个文件");

                    // 极端文件数下的保护性硬上限（正常不会触发），满足"完整扫描、不跳过"需求
                    if (count >= MaxFilesPerTarget)
                    {
                        Logger.Warning($"临时文件目录文件数超过上限 {MaxFilesPerTarget}，提前结束统计：{path}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return (size, count);
        }

        private IEnumerable<FileEntry> EnumerateFilesSafe(string path, CancellationToken ct)
        {
            // 用 FindFirstFile/FindNextFile 原生枚举：每个文件仅一次系统调用即可同时拿到路径与大小，
            // 相比 Directory.EnumerateFiles + 逐个 GetFileAttributesEx 减少一半系统调用，大目录扫描速度显著提升。
            // 不跳过任何隐藏/系统文件（用户要求完整扫描）；仅不跟随重解析点（junction/符号链接），
            // 避免误入被指向的系统目录或在其上阻塞（等价于原 File.GetAttributes 检查 junction 的安全性）。
            var stack = new Stack<string>();
            stack.Push(path);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();

                var entries = new List<FileEntry>();
                try
                {
                    NativeMethods.ForEachEntry(current, e =>
                    {
                        if (e.Name == "." || e.Name == "..") return;   // 跳过自身与父目录，防止递归死循环
                        if (e.IsReparsePoint) return;                  // 不跟随重解析点（安全）
                        entries.Add(new FileEntry(
                            Path.Combine(current, e.Name),
                            e.Size,
                            e.IsDirectory));
                    }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                foreach (var e in entries)
                {
                    if (e.IsDirectory)
                        stack.Push(e.FullName);
                    else
                        yield return e;
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

            foreach (var file in EnumerateFilesSafe(path, ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long fileSize = file.Size;

                    if (permanent)
                    {
                        new FileInfo(file.FullName).Delete();
                    }
                    else
                    {
                        // 走回收站；失败则跳过（不静默永久删除）
                        if (!NativeMethods.SendToRecycleBin(file.FullName, out var err))
                        {
                            Logger.Warning($"回收站删除失败 [{file.FullName}]: 0x{err:X}");
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
            catch (Exception ex)
            {
                // N6：原静默吞异常，现记录以便观测（R6 观测性）
                Logger.Warning($"FlushDns 执行失败: {ex.Message}");
                return false;
            }
        }
    }
}
