using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        /// 分类扫描实时进度回调：参数为 (该分类, 累计已扫字节, 累计文件数)。
        /// 由 ScanAsync 在枚举每个文件时按批次触发，ViewModel 经 Dispatcher 写回卡片的
        /// SizeBytes/FileCount，实现“下面分类实时显示已扫出多少 MB”的直观反馈。
        /// target 为 null（如删除前的受保护目录统计）时不触发。
        /// </summary>
        public Action<CleanTarget, long, long> OnTargetScanProgress { get; set; }

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
                    Category = "临时文件", Icon = "",
                    Paths = { Path.Combine(localApp, "Temp") }, IsSelected = true, IsSystemSafe = false
                },
                new CleanTarget
                {
                    Name = "系统临时文件", Description = "Windows 系统临时目录",
                    Category = "临时文件", Icon = "",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") },
                    IsSelected = true, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "缩略图缓存", Description = "资源管理器缩略图缓存，清理后首次打开文件夹会重新生成",
                    Category = "缓存", Icon = "",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\Explorer") }, IsSelected = false, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "Windows 错误报告", Description = "程序崩溃时生成的错误报告队列",
                    Category = "日志", Icon = "",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\WER") }, IsSelected = true, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "Windows 更新缓存", Description = "已下载的更新安装包，清理后不影响已安装的更新",
                    Category = "更新", Icon = "",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download") },
                    IsSelected = true, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "预取文件", Description = "程序预读取数据，系统会自动重新生成",
                    Category = "系统", Icon = "",
                    Paths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch") },
                    IsSelected = false, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "字体缓存", Description = "Windows 字体缓存服务数据",
                    Category = "缓存", Icon = "",
                    Paths = { Path.Combine(localApp, @"Microsoft\Windows\Explorer"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ServiceProfiles") },
                    IsSelected = false, IsSystemSafe = true
                },
                new CleanTarget
                {
                    Name = "DNS 缓存", Description = "DNS 解析缓存记录",
                    Category = "缓存", Icon = "",
                    Paths = { "dns:" }, IsSelected = false, IsSystemSafe = true
                }
            };
        }

        /// <summary>
        /// 扫描指定清理目标，计算可释放空间
        /// </summary>
        public async Task ScanAsync(List<CleanTarget> targets, CancellationToken ct = default)
        {
            int total = targets.Count;
            int completed = 0;
            var targetProgress = new int[total]; // 每个目标当前的子进度（0-100），用于总进度平滑

            // 并行扫描各目标目录：每个目录树相互独立，IO 可重叠，显著缩短整体扫描时间。
            // 注意：async lambda 捕获 UI 同步上下文，写回 target 属性（SizeBytes/Status 等）仍在 UI 线程，
            // 与 WPF 绑定线程一致；内部 GetDirectorySize 运行于线程池，其 OnProgress 已由 ViewModel 经 Dispatcher 转发，安全。
            // 仍完整扫描每个目录、不超时跳过（用户要求），仅响应主动取消。
            var tasks = targets.Select(async (target, idx) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    target.IsScanning = true;
                    target.Status = "扫描中...";
                    target.ScannedFiles?.Clear();
                    OnProgress?.Invoke(-1, $"扫描：{target.Name}");

                    if (target.Paths.Any(p => p == "dns:"))
                    {
                        target.SizeBytes = 0;
                        target.FileCount = 0;
                        target.Status = "运行时清理";
                    }
                    else
                    {
                        // 累计变量在多个路径/整轮扫描间持续累加；GetDirectorySize 每扫 2048 个文件
                        // 通过 OnTargetScanProgress 把累计值实时回传 ViewModel（经 Dispatcher 写回卡片），
                        // 于是“下面分类”里的 MB 数字会随扫描推进实时增长，给用户直观的进度感。
                        // 同时把文件列表缓存到 target.ScannedFiles，清理时直接用，避免二次枚举。
                        long runningSize = 0;
                        long runningCount = 0;
                        var cachedFiles = new List<(string FullName, long Size)>();
                        foreach (var path in target.Paths)
                        {
                            var (size, count, files) = await Task.Run(
                                () => GetDirectorySize(path, ct, target.Name, target, ref runningSize, ref runningCount,
                                    (fileCount) =>
                                    {
                                        // 让进度条在扫描巨型单目标时也缓慢前进，避免长时间卡在 87% 造成“卡死”错觉。
                                        // 子进度按“每 1000 个文件推进约 3%，上限 90%”，完成时再跳到 100%。
                                        int sub = Math.Min(90, (int)(fileCount / 1000.0 * 3));
                                        int old = targetProgress[idx];
                                        if (sub > old)
                                        {
                                            targetProgress[idx] = sub;
                                            ReportSmoothProgress(targetProgress, completed, total, target.Name, fileCount);
                                        }
                                    }), ct);
                            cachedFiles.AddRange(files);
                        }
                        target.ScannedFiles = cachedFiles;
                        // 整轮扫完的最终兜底赋值
                        target.SizeBytes = runningSize;
                        target.FileCount = runningCount;
                        target.Status = $"共 {runningCount} 个文件";
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
                    int done = Interlocked.Increment(ref completed);
                    targetProgress[idx] = 100; // 当前目标完成
                    ReportSmoothProgress(targetProgress, done, total, target.Name, target.FileCount);
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { throw; }
            catch (AggregateException ae) when (ae.InnerExceptions.Any(e => e is OperationCanceledException))
            {
                // 用户取消：向上传播取消信号，让 ViewModel 走“已取消”分支
                throw ae.InnerExceptions.OfType<OperationCanceledException>().First();
            }
            catch (Exception) { /* 单个目标失败已记录到 Status，整轮不失败 */ }
        }

        private void ReportSmoothProgress(int[] targetProgress, int completed, int total, string targetName, long fileCount)
        {
            if (total <= 0) return;
            // 总进度 = 已完成目标占完整份额 + 未完成目标的子进度按份额分摊
            double sum = 0;
            foreach (var p in targetProgress)
                sum += p / 100.0;
            int pct = (int)(sum / total * 100);
            OnProgress?.Invoke(pct, $"扫描 {targetName}：已扫描 {fileCount} 个文件");
        }

    /// <summary>分类删除模式：系统回收站（可恢复、慢）/ 直接永久删除（最快、系统垃圾位安全）/ 保险箱软删除（可恢复、快）。</summary>
    public enum DeleteMode
    {
        RecycleBin,
        DirectDelete,
        Quarantine
    }

    /// <summary>
    /// 清理选中的目标。
    /// useRecycleBin=true 时全部走系统回收站；否则按分类性质分级：系统级垃圾位直接永久删除，
    /// 用户空间（如用户临时文件）移入保险箱软删除（可恢复）。
    /// </summary>
    public async Task<(long freedBytes, int recycled, int directDeleted, int quarantined, int failedCount)> CleanAsync(
        List<CleanTarget> targets, bool useRecycleBin, CancellationToken ct = default)
    {
        long totalFreed = 0;
        int totalRecycled = 0;
        int totalDirect = 0;
        int totalQuar = 0;
        int totalFailed = 0;
        var selected = targets.Where(t => t.IsSelected && (t.SizeBytes > 0 || t.Paths.Contains("dns:"))).ToList();

        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var target = selected[i];
            // 该目标在整体进度中的基准位置与所占份额，用于把“删除内子进度”映射到绝对进度，
            // 避免每个目标开始时进度条跳回 0。
            double baseFrac = (double)i / selected.Count;
            double slice = 1.0 / selected.Count;
            OnProgress?.Invoke((int)(baseFrac * 100), $"清理：{target.Name}（{i + 1}/{selected.Count}）");

            // 特殊处理 DNS 缓存
            if (target.Paths.Contains("dns:"))
            {
                bool ok = await FlushDnsAsync();
                target.Status = ok ? "已清理" : "清理失败（DNS 服务不可用或无权限）";
                OnProgress?.Invoke((int)((i + 1) / selected.Count * 100), $"已完成：{target.Name}");
                continue;
            }

            // 清理时直接使用扫描缓存的文件列表，避免二次枚举（用户反馈"扫描时不是已经枚举一遍了吗"）。
            // 按 path 分组传入 DeleteDirectoryContents；空缓存时 fallback 到重新枚举。
            // 分组规则：取最长匹配路径，并确保路径边界（如 C:\Temp 不能匹配 C:\TempFoo\file）。
            var filesByPath = target.ScannedFiles
                .GroupBy(f => GetMatchingPath(f.FullName, target.Paths))
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var path in target.Paths)
            {
                if (ct.IsCancellationRequested) break;
                var mode = useRecycleBin
                    ? DeleteMode.RecycleBin
                    : (target.IsSystemSafe ? DeleteMode.DirectDelete : DeleteMode.Quarantine);
                filesByPath.TryGetValue(path, out var pathFiles);
                var (freed, recycled, direct, quar, failed) = await Task.Run(
                    () => DeleteDirectoryContents(path, pathFiles, mode, target.Name, ct,
                        (frac, msg) => OnProgress?.Invoke((int)((baseFrac + frac * slice) * 100), msg)), ct);
                totalFreed += freed;
                totalRecycled += recycled;
                totalDirect += direct;
                totalQuar += quar;
                totalFailed += failed;
            }
            target.Status = "已清理";
            target.SizeBytes = 0;
            target.ScannedFiles?.Clear();

            OnProgress?.Invoke((int)((i + 1) / selected.Count * 100), $"已完成：{target.Name}");
        }

        return (totalFreed, totalRecycled, totalDirect, totalQuar, totalFailed);
    }

        /// <summary>
        /// 为文件路径选择最精确匹配的 target path：取最长前缀，且前缀后必须是路径分隔符或字符串结尾。
        /// 避免 C:\Temp 前缀误匹配 C:\TempFoo\file.txt。
        /// </summary>
        private static string GetMatchingPath(string filePath, ObservableCollection<string> paths)
        {
            string best = null;
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (filePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    int len = p.Length;
                    if (filePath.Length == len ||
                        filePath[len] == Path.DirectorySeparatorChar ||
                        filePath[len] == Path.AltDirectorySeparatorChar)
                    {
                        if (best == null || p.Length > best.Length)
                            best = p;
                    }
                }
            }
            return best;
        }

        // 枚举得到的单个文件项（路径+大小由 ForEachEntry 一次枚举直接给出，无需二次 stat）
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

        // 扫描不做文件数上限、不超时跳过：按用户要求完整扫描每个目录（仅用户主动取消可中断）。

        private (long size, long count, List<(string FullName, long Size)> files) GetDirectorySize(
            string path, CancellationToken ct,
            string targetName, CleanTarget target,
            ref long runningSize, ref long runningCount,
            Action<long> onHeartbeat = null)
        {
            // 并行遍历：固定数量 worker 从并发队列取目录、各自用 ForEachEntry 枚举，把一棵巨型目录树
            // （如 SoftwareDistribution\Download）的不同分支分散到多核/并发 IO 上。
            // 本次修复两大并行效率问题：
            //  1) 完成判定：原 while(TryDequeue) 在队列“短暂为空”时 worker 会提前退出，导致大目录树退化为单线程遍历
            //     （这正是“并行 BFS 和修复前一样慢”的根因）。改用 inFlight 计数——仅在“队列空且再无正在处理/待处理的目录”
            //     时才让 worker 退出，保证所有核在整个扫描期间都满负荷。
            //  2) 伪共享：原热循环对每个文件都 Interlocked.Add 到同一组共享 long（totalSize/count/live* 紧邻同一缓存行），
            //     多核同时写造成 cache line 弹跳，吞吐随文件数骤降。改为每 worker 本地累加器，仅每 2048 文件一次性
            //     Interlocked 合并到共享计数供 UI 显示。
            // 安全铁律不变：仍只用 ForEachEntry 的枚举层 Attributes 判重解析点，不跟随、不访问 junction 目标。
            long totalSize = 0;
            long totalCount = 0;

            var visited = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var queue = new ConcurrentQueue<string>();
            queue.Enqueue(path);
            visited.TryAdd(path, 0);

            // IO 密集型遍历：目录枚举多为等待磁盘，提高并发度比按 CPU 核数更能打满吞吐。
            // 下限 16、上限 64，避免极端机器上线程过多反而增加调度开销。
            int cores = Math.Min(64, Math.Max(Environment.ProcessorCount * 2, 16));
            // 每 worker 本地累加器：避免热循环里 per-file Interlocked 在共享 long 上的伪共享（cache line 弹跳）。
            var localSize = new long[cores];
            var localCount = new long[cores];
            var localLiveSize = new long[cores];
            var localLiveCount = new long[cores];
            var localProgress = new int[cores];
            // 每 worker 本地文件列表：避免热循环里向 ConcurrentBag 并发 Add 的争用开销
            // （9 万文件场景下 ConcurrentBag 的跨线程 steal 会成为明显瓶颈）。合并阶段一次性拼接。
            var localFiles = new List<(string FullName, long Size)>[cores];
            for (int li = 0; li < cores; li++)
                localFiles[li] = new List<(string, long)>();
            // 跨路径累计的 UI 共享计数（继承上一路径已扫值），供 OnTargetScanProgress 实时显示累计 MB。
            long liveSizeGlobal = runningSize;
            long liveCountGlobal = runningCount;
            // inFlight：仍在队列中或正在处理的目录数；为 0 且队列空才说明整棵树遍历完。
            long inFlight = 1;

            var workers = new Task[cores];
            for (int w = 0; w < cores; w++)
            {
                int wid = w;
                workers[w] = Task.Run(() =>
                {
                    while (true)
                    {
                        if (queue.TryDequeue(out var current))
                        {
                            try
                            {
                                ct.ThrowIfCancellationRequested();
                                NativeMethods.ForEachEntry(current, e =>
                                {
                                    if (e.Name == "." || e.Name == "..") return;
                                    if (e.IsReparsePoint) return;            // 不跟随重解析点（安全）

                                    var full = Path.Combine(current, e.Name);
                                    if (e.IsDirectory)
                                    {
                                        if (visited.TryAdd(full, 0))
                                        {
                                            queue.Enqueue(full);
                                            Interlocked.Increment(ref inFlight);
                                        }
                                        return;
                                    }

                                    // 本地累加（无锁），仅每 2048 文件合并一次到共享计数
                                    localSize[wid] += e.Size;
                                    localCount[wid]++;
                                    localLiveSize[wid] += e.Size;
                                    localLiveCount[wid]++;
                                    localFiles[wid].Add((full, e.Size));     // 缓存文件路径+大小，清理时直接用

                                    int n = ++localProgress[wid];
                                    if ((n & 0x7FF) == 0 && targetName != null)
                                    {
                                        // 一次性把本 worker 本地累计合并到共享计数（仅 1 次 Interlocked），
                                        // 既供 UI 实时显示，又避免热循环里每文件 Interlocked 的伪共享。
                                        long flushSize = Interlocked.Add(ref liveSizeGlobal, localLiveSize[wid]);
                                        long flushCount = Interlocked.Add(ref liveCountGlobal, localLiveCount[wid]);
                                        localLiveSize[wid] = 0;
                                        localLiveCount[wid] = 0;
                                        OnProgress?.Invoke(-1, $"扫描 {targetName}：已扫描 {flushCount} 个文件");
                                        OnTargetScanProgress?.Invoke(target, flushSize, flushCount);
                                        onHeartbeat?.Invoke(flushCount);
                                    }
                                }, ct);
                            }
                            catch (OperationCanceledException) { Interlocked.Decrement(ref inFlight); throw; }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            Interlocked.Decrement(ref inFlight);
                        }
                        else
                        {
                            // 队列空：仅当确实再无“正在处理/待处理”的目录时才退出，否则让出 CPU 等待生产者。
                            ct.ThrowIfCancellationRequested();
                            if (Interlocked.Read(ref inFlight) == 0) break;
                            Thread.Yield();
                        }
                    }
                }, ct);
            }

            try
            {
                Task.WhenAll(workers).GetAwaiter().GetResult();
            }
            catch (AggregateException ae)
            {
                foreach (var ex in ae.InnerExceptions)
                    if (ex is OperationCanceledException) throw ex;
                // 单目录异常已在内部记录，整轮不失败
            }

            // 合并各 worker 本地累加器（含最后不足 2048 的零头）；runningSize/Count 反映跨路径累计的最终结果。
            long mergeLive = liveSizeGlobal;
            long mergeCount = liveCountGlobal;
            for (int i = 0; i < cores; i++)
            {
                totalSize += localSize[i];
                totalCount += localCount[i];
                mergeLive += localLiveSize[i];
                mergeCount += localLiveCount[i];
            }
            runningSize = mergeLive;
            runningCount = mergeCount;
            var allFiles = new List<(string FullName, long Size)>(totalCount > 0 ? (int)Math.Min(totalCount, int.MaxValue) : 0);
            for (int i = 0; i < cores; i++)
                allFiles.AddRange(localFiles[i]);
            return (totalSize, totalCount, allFiles);
        }

        private IEnumerable<FileEntry> EnumerateFilesSafe(string path, CancellationToken ct, Action<int> onCount = null)
        {
            // 用托管 DirectoryInfo.EnumerateFileSystemInfos 枚举：名称可靠、非阻塞，文件大小从枚举缓存读取，
            // 避免对每文件再调 GetFileAttributesEx；不跳过任何隐藏/系统文件（用户要求完整扫描）；仅不跟随重解析点（junction/符号链接），
            // 避免误入被指向的系统目录或在其上阻塞（等价于原 File.GetAttributes 检查 junction 的安全性）。
            // 防环：同一目录只压栈一次（大小写不敏感）。目录树若出现循环链接（symlink/junction 指回祖先），
            // 没有此集合会无限重复遍历、文件计数暴涨到数亿且永不结束——这正是"扫了几亿文件仍不结束"的根因。
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(path);
            visited.Add(path);
            int count = 0;

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
                    {
                        // 防环：已访问过的目录不再压栈（应对重解析点判定万一失效或特殊挂载点导致的循环）
                        if (!visited.Add(e.FullName))
                        {
                            Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{e.FullName}");
                            continue;
                        }
                        stack.Push(e.FullName);
                    }
                    else
                    {
                        yield return e;
                        count++;
                        onCount?.Invoke(count);
                    }
                }
            }
        }

        private static string FormatEta(double elapsedSeconds, int processed, int total)
        {
            if (processed <= 0 || elapsedSeconds < 0.5 || processed >= total) return "";
            double remain = elapsedSeconds * (total - processed) / processed;
            if (remain < 60) return $"，约剩 {remain:F0} 秒";
            if (remain < 3600) return $"，约剩 {(int)(remain / 60)} 分 {(int)(remain % 60)} 秒";
            return $"，约剩 {(int)(remain / 3600)} 小时 {(int)((remain % 3600) / 60)} 分";
        }

        private (long freed, int recycled, int directDeleted, int quarantined, int failed) DeleteDirectoryContents(
            string path,
            IList<(string FullName, long Size)> cachedFiles,
            DeleteMode mode,
            string sourceName,
            CancellationToken ct,
            Action<double, string> onSubProgress = null)
        {
            if (!Directory.Exists(path)) return (0, 0, 0, 0, 0);

            // 受保护目录：非回收站模式下整体走 ElevatedHelper 永久删除（避免 asInvoker 下逐个文件失败）
            if (mode != DeleteMode.RecycleBin && ElevationHelper.IsProtectedPath(path))
            {
                long size = 0;
                try
                {
                    long dSize = 0, dCount = 0;
                    size = GetDirectorySize(path, ct, null, null, ref dSize, ref dCount).size;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.Warning($"临时文件清理无法统计受保护目录大小 [{path}]: {ex.Message}");
                }
                if (ElevationHelper.DeleteElevated(path))
                    return (size, 0, 1, 0, 0);
                return (0, 0, 0, 0, 0);
            }

            // 优先使用扫描阶段已缓存的文件列表，避免二次枚举（用户反馈"扫描时不是已经枚举一遍了吗"）。
            // 若缓存为空（如未扫描直接清理），则 fallback 现场枚举，枚举进度映射到 0-20%。
            var files = new List<string>();
            var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (cachedFiles != null && cachedFiles.Count > 0)
            {
                foreach (var (fullName, size) in cachedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(fullName);
                    sizeMap[fullName] = size;
                }
                onSubProgress?.Invoke(0.20, $"已就绪：{files.Count} 个文件");
            }
            else
            {
                foreach (var file in EnumerateFilesSafe(path, ct,
                    c => onSubProgress?.Invoke(0.20 * Math.Min(1.0, c / 100000.0), $"正在枚举文件：{c} 个")))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file.FullName);
                    sizeMap[file.FullName] = file.Size;
                }
            }

            if (files.Count == 0)
            {
                if (mode != DeleteMode.RecycleBin) CleanEmptyDirs(path, ct);
                return (0, 0, 0, 0, 0);
            }

            int recycled = 0, directDeleted = 0, quarantined = 0, failed = 0;
            long freed = 0;

            if (mode == DeleteMode.RecycleBin)
            {
                // 回收站删除：批量 SHFileOperation（一次调用处理一批），并实时回传“已处理/总数”精确进度。
                // batchSize=250 + maxBatchBytes=200MB：单批耗时可控（数百毫秒级），进度条持续小幅前进；
                // 遇到大文件自动单独成批。回收站删除绝对速度受 Windows Shell 移动文件本身限制，
                // batch 大小主要影响“进度反馈频率”而非总耗时。
                int total = files.Count;
                int totalBatchesApprox = (int)Math.Ceiling((double)total / 250.0);
                int currentBatch = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var failedCandidates = NativeMethods.SendToRecycleBinOnUIThread(files,
                    (processed, totalFiles) =>
                    {
                        double frac = 0.20 + 0.80 * ((double)processed / totalFiles);
                        string eta = "";
                        if (processed > 0 && sw.Elapsed.TotalSeconds > 0.5)
                        {
                            double remain = sw.Elapsed.TotalSeconds * (totalFiles - processed) / processed;
                            if (remain < 60) eta = $"，约剩 {remain:F0}秒";
                            else if (remain < 3600) eta = $"，约剩 {(int)(remain / 60)}分{(int)(remain % 60)}秒";
                            else eta = $"，约剩 {(int)(remain / 3600)}小时{(int)((remain % 3600) / 60)}分";
                        }
                        onSubProgress?.Invoke(frac, $"正在移入回收站：{processed}/{totalFiles} 个文件（第 {currentBatch}/{totalBatchesApprox} 批）{eta}");
                    },
                    onBatch: (idx, _) => { currentBatch = idx; },
                    batchSize: 250,
                    maxBatchBytes: 200L * 1024 * 1024,
                    sizes: sizeMap);

                // 精确统计：失败候选文件仍存在于磁盘才算真正失败
                foreach (var f in failedCandidates)
                {
                    if (File.Exists(f)) { failed++; }
                    else { recycled++; freed += sizeMap[f]; }
                }
                RecycleBinManager.SourceTracker.Record(files, sourceName);
                return (freed, recycled, 0, 0, failed);
            }
            else if (mode == DeleteMode.DirectDelete)
            {
                // 系统级垃圾位：批量永久删除（SHFileOperation，不带 FOF_ALLOWUNDO）。
                // 为什么不用逐文件 File.Delete：8 万文件 = 8 万次独立删除调用，每个文件都被 Windows Defender
                // 实时防护逐个扫描 + 逐次 MFT 写入，墙钟时间可达数分钟，且前 1024 文件只占进度条 1%，
                // 表现为“卡在 20% 不动”。改为复用 SendToRecycleBinBatch 的批量机制（permanent 模式）：
                // 一次 COM 调用删 250 个、单一 STA 线程 + 消息泵不阻塞 UI、每批精确回传进度（进度条平滑前进）。
                int total = files.Count;
                int totalBatchesApprox = (int)Math.Ceiling((double)total / 250.0);
                int currentBatch = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var failedCandidates = NativeMethods.SendToRecycleBinBatch(files,
                    (processed, totalFiles) =>
                    {
                        double frac = 0.20 + 0.80 * ((double)processed / totalFiles);
                        string eta = FormatEta(sw.Elapsed.TotalSeconds, processed, totalFiles);
                        onSubProgress?.Invoke(frac, $"正在删除：{processed}/{totalFiles} 个文件（第 {currentBatch}/{totalBatchesApprox} 批）{eta}");
                    },
                    onBatch: (idx, _) => { currentBatch = idx; },
                    batchSize: 250,
                    maxBatchBytes: 200L * 1024 * 1024,
                    sizes: sizeMap,
                    permanent: true);

                // 失败统计（与回收站分支一致，且避免对 8 万文件逐次 File.Exists 的 N 次 IO）：
                // 仅对“失败批候选”复核，整批成功的文件默认已删、直接计入 directDeleted。
                var candSet = new HashSet<string>(failedCandidates, StringComparer.OrdinalIgnoreCase);
                long freedBytes = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    if (candSet.Contains(f) && File.Exists(f))
                        failed++;           // 失败批里仍存在的文件 = 未能删除
                    else
                    {
                        directDeleted++;
                        freedBytes += sizeMap.TryGetValue(f, out var s) ? s : 0;
                    }
                }
                CleanEmptyDirs(path, ct);
                return (freedBytes, 0, directDeleted, 0, failed);
            }
            else // Quarantine
            {
                // 用户空间：移入保险箱软删除（可恢复、不黑屏、速度快）。
                // 先批量预建整棵目标目录树，避免每文件一次 Directory.CreateDirectory（深层目录逐级 stat 累积开销）。
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var dp = Path.GetDirectoryName(QuarantineService.MapToQuarantinePath(f));
                    if (!string.IsNullOrEmpty(dp)) dirSet.Add(dp);
                }

                // 并行预建目录树并实时汇报进度，避免 8 万文件时"已就绪"阶段长时间无反馈被误判卡死。
                var dirList = dirSet.ToList();
                var prepSw = System.Diagnostics.Stopwatch.StartNew();
                long prepDone = 0;
                long prepLastReported = 0;
                if (dirList.Count > 0)
                {
                    Parallel.ForEach(dirList,
                        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(32, Environment.ProcessorCount * 2) },
                        d =>
                        {
                            Directory.CreateDirectory(d);
                            long done = Interlocked.Increment(ref prepDone);
                            bool byCount = (done & 0x3FF) == 0;
                            bool byTime = prepSw.ElapsedMilliseconds - prepLastReported >= 500;
                            if (byCount || byTime || done == dirList.Count)
                            {
                                prepLastReported = prepSw.ElapsedMilliseconds;
                                double frac = 0.20 + 0.80 * ((double)done / dirList.Count);
                                string eta = FormatEta(prepSw.Elapsed.TotalSeconds, (int)done, dirList.Count);
                                onSubProgress?.Invoke(frac, $"正在准备保险箱目录：{done}/{dirList.Count} 个目录{eta}");
                            }
                        });
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastReported = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    try
                    {
                        // 目录已预建，跳过 MoveToQuarantine 内部的 CreateDirectory 调用，避免每文件一次 stat
                        QuarantineService.MoveToQuarantine(f, out long sz, createDirectory: false);
                        if (File.Exists(f)) { failed++; }   // 仍在原处 → 移动失败
                        else { quarantined++; freed += sz; }
                    }
                    catch (IOException) { failed++; }
                    catch (UnauthorizedAccessException) { failed++; }

                    bool byCount = (i & 0x3FF) == 0;
                    bool byTime = sw.ElapsedMilliseconds - lastReported >= 500;
                    if (byCount || byTime || i == files.Count - 1)
                    {
                        lastReported = sw.ElapsedMilliseconds;
                        double frac = 0.20 + 0.80 * ((double)(i + 1) / files.Count);
                        string eta = FormatEta(sw.Elapsed.TotalSeconds, i + 1, files.Count);
                        onSubProgress?.Invoke(frac, $"正在移入保险箱：{i + 1}/{files.Count} 个文件{eta}");
                    }
                }
                CleanEmptyDirs(path, ct);
                return (freed, 0, 0, quarantined, failed);
            }
        }

        /// <summary>
        /// 仅永久删除模式下清理已空的子目录（不跟随重解析点，避免误删其目标目录）。
        /// </summary>
        private void CleanEmptyDirs(string path, CancellationToken ct)
        {
            try
            {
                var allDirs = new List<string>();
                var dirStack = new Stack<string>();
                dirStack.Push(path);
                while (dirStack.Count > 0)
                {
                    var cur = dirStack.Pop();
                    try
                    {
                        // 用 ForEachEntry 读取枚举层 Attributes（非阻塞，不访问 junction 目标），
                        // 避免对子目录调用 File.GetAttributes 在失效/离线 junction 上阻塞（同 EnumerateFilesSafe 修复）。
                        NativeMethods.ForEachEntry(cur, e =>
                        {
                            if (e.Name == "." || e.Name == "..") return;
                            if (!e.IsDirectory) return;
                            if (e.IsReparsePoint) return;      // 不跟随重解析点
                            var full = Path.Combine(cur, e.Name);
                            allDirs.Add(full);
                            dirStack.Push(full);
                        }, ct);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
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
