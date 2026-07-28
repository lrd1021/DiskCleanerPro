using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Services
{
    public class DuplicateFinder
    {
        public Action<int, string> OnProgress { get; set; }

        /// <summary>
        /// 实时结果回调：每确认出一组重复文件即触发（参数为该组）。
        /// 由 FindDuplicatesAsync 在阶段B（全量哈希确认）发现重复时调用，
        /// ViewModel 经 Dispatcher 把该组实时加入结果列表，实现“检测结果随扫描逐步出现”的直观反馈。
        /// </summary>
        public Action<DuplicateGroup> OnGroupFound { get; set; }

        /// <summary>
        /// 实时文件名回传：收集阶段每枚举到一个文件即触发（参数为完整路径）。
        /// 由 FindDuplicatesAsync 在并行收集目录树时调用，ViewModel 用它驱动“正在扫描的文件”实时滚动列表，
        /// 让用户直观看到扫描在进行。回调频率等于文件枚举频率（很高），ViewModel 仅保留“最新一个”并节流刷新 UI。
        /// </summary>
        public Action<string> OnFileScanned { get; set; }

        /// <summary>本扫描周期内已实时回传过的重复组（key = 桶键 size:hash），避免重复回传。</summary>
        private readonly ConcurrentDictionary<string, DuplicateGroup> _emitted =
            new ConcurrentDictionary<string, DuplicateGroup>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SkipDirs = NativeMethods.ProtectedDirectories;

        public long MinFileSize { get; set; } = 1 * 1024 * 1024;

        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(
            string rootPath, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                _emitted.Clear();
                ReportProgress(0, "正在收集文件列表...");

                // 第1步：并行 BFS 收集所有符合条件的文件。
                // 原单线程 DFS 在目录树极深/目录数量巨大时（如 npm/node_modules、WinSxS 等）成为瓶颈，
                // 改为多核并发遍历，并用 ConcurrentQueue + ConcurrentDictionary 保证线程安全。
                // 安全铁律保留：仍只通过 ForEachEntry 的枚举层 Attributes 判重解析点，不跟随 junction，
                // 不访问目标路径，避免在失效/离线 junction 上阻塞。
                var queue = new ConcurrentQueue<string>();
                var visitedDirs = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                visitedDirs.TryAdd(rootPath, true);
                queue.Enqueue(rootPath);
                var bagFiles = new ConcurrentBag<FileMeta>();
                long dirsScanned = 0;
                long filesCollected = 0;

                int collectCores = Math.Max(1, Environment.ProcessorCount);
                // 每 worker 本地计数，避免热循环里 per-file Interlocked 在共享 long 上的伪共享（cache line 弹跳）。
                var localFiles = new long[collectCores];
                var localDirs = new long[collectCores];
                // inFlight：仍在队列中或正在处理的目录数；为 0 且队列空才说明整棵树遍历完，
                // 修复此前 Parallel.For + while(TryDequeue) 在队列短暂为空时 worker 提前退出、大目录树退化为单线程的 bug
                // （这正是“文件数到十几万就卡住”的根因：少数巨型目录被单个 worker 串行扫，其余核闲置）。
                long inFlight = 1;

                try
                {
                    var workers = new Task[collectCores];
                    for (int w = 0; w < collectCores; w++)
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
                                        // 单目录枚举一次拿到大小（ForEachEntry 枚举缓存含 Length），
                                        // 替代原先 Directory.EnumerateFiles + TryGetFileMeta 的“二次 stat”，每文件少一次系统调用。
                                        NativeMethods.ForEachEntry(current, e =>
                                        {
                                            if (e.Name == "." || e.Name == "..") return;
                                            if (e.IsReparsePoint) return;            // 不跟随重解析点
                                            if (e.IsDirectory)
                                            {
                                                if (SkipDirs.Contains(e.Name)) return;
                                                var full = Path.Combine(current, e.Name);
                                                if (visitedDirs.TryAdd(full, true))
                                                {
                                                    queue.Enqueue(full);
                                                    Interlocked.Increment(ref inFlight);
                                                }
                                                return;
                                            }
                                            // 实时文件名回传（每文件一次，仅供 UI 滚动展示，频率高但只存最新一个）
                                            OnFileScanned?.Invoke(Path.Combine(current, e.Name));
                                            if (e.Size >= MinFileSize)
                                            {
                                                bagFiles.Add(new FileMeta(
                                                    e.Name,
                                                    Path.Combine(current, e.Name),
                                                    e.Size,
                                                    Path.GetExtension(e.Name),
                                                    e.LastWriteTime == DateTime.MinValue ? "" : e.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                                                localFiles[wid]++;
                                            }
                                        }, ct);
                                    }
                                    catch (OperationCanceledException) { Interlocked.Decrement(ref inFlight); throw; }
                                    catch (IOException) { }
                                    catch (UnauthorizedAccessException) { }
                                    Interlocked.Decrement(ref inFlight);
                                    // 每 2048 个目录把本地计数合并到全局（仅 1 次 Interlocked），用于进度显示
                                    localDirs[wid]++;
                                    if ((localDirs[wid] & 0x7FF) == 0)
                                    {
                                        long fd = Interlocked.Add(ref filesCollected, localFiles[wid]); localFiles[wid] = 0;
                                        long dd = Interlocked.Add(ref dirsScanned, localDirs[wid]); localDirs[wid] = 0;
                                        // 收集阶段进度：用软曲线把目录数映射到 0~18%（下一个里程碑是收集完的 20%），
                                        // 单调递增、不回退，避免进度条停在 0 或显示“来回滚动”的不确定动画。
                                        int collectPct = (int)(18.0 * (1.0 - Math.Exp(-(double)dd / 3000.0)));
                                        ReportProgress(collectPct, $"正在扫描... {dd} 目录, {fd} 文件");
                                    }
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
                    Task.WhenAll(workers).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { throw; }
                catch (AggregateException ae)
                {
                    foreach (var ex in ae.InnerExceptions)
                        if (ex is OperationCanceledException) throw ex;
                }
                // 收尾：把各 worker 尚未 flush 的本地计数并入全局
                for (int i = 0; i < collectCores; i++)
                {
                    Interlocked.Add(ref filesCollected, localFiles[i]);
                    Interlocked.Add(ref dirsScanned, localDirs[i]);
                }

                var allFiles = new List<FileMeta>(bagFiles);
                ReportProgress(20, $"共收集 {allFiles.Count} 个文件，开始分组...");

                // 第2步：按文件大小分组，仅保留多成员组（唯一大小的文件不可能是重复，直接排除，避免后续任何 IO）
                var sizeGroups = allFiles
                    .GroupBy(f => f.Length)
                    .Where(g => g.Count() > 1)
                    .ToList();

                // 展平为多成员组内的候选文件（这些才需要指纹计算）；之后 allFiles 不再需要，释放引用助 GC
                var candidates = new List<FileMeta>(sizeGroups.Sum(g => g.Count()));
                foreach (var g in sizeGroups)
                    foreach (var fi in g)
                        candidates.Add(fi);
                allFiles = null;

                int totalCandidates = candidates.Count;
                ReportProgress(40, $"候选文件：{totalCandidates}，计算快速指纹（头尾 8KB）...");

                // 第3步：两阶段哈希，但改为「两次全局并行扫描」而非「逐组嵌套并行」。
                // 旧实现对每一个大小组都单独 Parallel.ForEach，当组数成千上万时 TPL 调度开销与外层同步迭代成本陡增，
                // 表现为“文件越多越慢”。全局并行把阶段A（head+tail 指纹）与阶段B（全量 MD5 确认）各收敛为单次并行扫描，
                // 消除逐组调度开销并改善负载均衡；桶键 = 大小:指纹，保证仅真正同大小且头尾相同的文件才进阶段B。
                var result = new List<DuplicateGroup>();
                var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
                int cores = Math.Max(1, Environment.ProcessorCount);

                // —— 阶段A：全局并行计算 head+tail 快速指纹 ——
                var quickBuckets = new ConcurrentDictionary<string, List<FileMeta>>(StringComparer.OrdinalIgnoreCase);
                long quickDone = 0;
                int quickRange = Math.Max(1, totalCandidates / (cores * 4) + 1);
                try
                {
                    Parallel.ForEach(Partitioner.Create(0, totalCandidates, quickRange), po, range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fi = candidates[i];
                            try
                            {
                                var q = ComputeQuickHash(fi.FullName, ct);
                                var bucket = quickBuckets.GetOrAdd(fi.Length + ":" + q, _ => new List<FileMeta>());
                                lock (bucket) bucket.Add(fi);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Logger.Warning($"重复文件扫描快速指纹失败 [{fi.FullName}]: {ex.Message}");
                            }
                        }
                        var done = Interlocked.Add(ref quickDone, range.Item2 - range.Item1);
                        ReportProgress(40 + (int)((float)done / totalCandidates * 25),
                            $"快速指纹 {done}/{totalCandidates}");
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* 单文件异常已在内部记录，继续 */ }

                // 仅头尾相同（>1）的候选进入阶段B 全量确认
                var fullCandidates = quickBuckets
                    .Where(kv => kv.Value.Count > 1)
                    .SelectMany(kv => kv.Value)
                    .ToList();
                int totalFull = fullCandidates.Count;
                var fullBuckets = new ConcurrentDictionary<string, List<FileMeta>>(StringComparer.OrdinalIgnoreCase);
                long fullDone = 0;
                int fullRange = Math.Max(1, totalFull / (cores * 4) + 1);
                ReportProgress(65, $"快速指纹完成，{totalFull} 个候选文件进入全量 MD5 确认...");

                // —— 阶段B：全局并行全量 MD5 确认 ——
                try
                {
                    Parallel.ForEach(Partitioner.Create(0, totalFull, fullRange), po, range =>
                    {
                        for (int i = range.Item1; i < range.Item2; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fi = fullCandidates[i];
                            try
                            {
                            var h = ComputeHashChunked(fi.FullName, ct);
                            var fullKey = fi.Length + ":" + h;
                            var bucket = fullBuckets.GetOrAdd(fullKey, _ => new List<FileMeta>());
                            bool firstPair = false;
                            lock (bucket)
                            {
                                bucket.Add(fi);
                                if (bucket.Count == 2) firstPair = true;
                            }
                            // 该桶刚形成第一对重复：立即实时回传，让用户看到“结果随扫描逐步出现”，
                            // 而非等阶段B全部哈希跑完才一次性弹出。
                            if (firstPair)
                            {
                                var grp = BuildDuplicateGroup(bucket, fi.Length, h);
                                _emitted[fullKey] = grp;
                                OnGroupFound?.Invoke(grp);
                            }
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Logger.Warning($"重复文件扫描无法计算哈希 [{fi.FullName}]: {ex.Message}");
                            }
                        }
                        var done = Interlocked.Add(ref fullDone, range.Item2 - range.Item1);
                        ReportProgress(65 + (int)((float)done / Math.Max(1, totalFull) * 30),
                            $"全量确认 {done}/{totalFull}");
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* 单文件异常已在内部记录，继续 */ }

                // 产出完整重复组（按浪费空间降序）。实时回传已在阶段B内进行（桶刚达2即 OnGroupFound）；
                // 此处仅用完整桶构建最终结果列表，供 ViewModel 在扫描结束后一次性替换，保证成员完整、统计准确。
                foreach (var kv in fullBuckets.Where(k => k.Value.Count > 1))
                {
                    var colonIdx = kv.Key.IndexOf(':');
                    var hashOnly = colonIdx >= 0 ? kv.Key.Substring(colonIdx + 1) : kv.Key;
                    result.Add(BuildDuplicateGroup(kv.Value, kv.Value[0].Length, hashOnly));
                }

                result.Sort((a, b) => b.WasteBytes.CompareTo(a.WasteBytes));
                ReportProgress(100, $"检测完成，共 {result.Count} 组重复文件");
                return result;
            }, ct);
        }

        private void ReportProgress(int pct, string msg)
        {
            OnProgress?.Invoke(pct, msg);
        }

        /// <summary>从同一桶的候选文件构建重复组（按修改时间升序，最旧的标为保留）。</summary>
        private static DuplicateGroup BuildDuplicateGroup(List<FileMeta> bucket, long size, string hashOnly)
        {
            var grp = new DuplicateGroup
            {
                Hash = hashOnly,
                FileSize = size
            };
            bool first = true;
            foreach (var fi in bucket.OrderBy(f => f.LastModified))
            {
                // 关键文件（系统/程序必需，Danger 级）无论是否“第一个”都强制保留，避免误删
                bool isCritical = FileSafetyAnalyzer.Analyze(fi.FullName).Level == FileSafetyLevel.Danger;
                grp.Files.Add(new DuplicateFile
                {
                    GroupKey = hashOnly,
                    FilePath = fi.FullName,
                    FileName = fi.Name,
                    Directory = Path.GetDirectoryName(fi.FullName),
                    LastModified = fi.LastModified,
                    KeepThis = first || isCritical
                });
                first = false;
            }
            grp.HookFileChanges();
            return grp;
        }

        private static string ComputeHashChunked(string filePath, CancellationToken ct)
        {
            const int headTailSize = 4096;
            const int chunkSize = 65536;

            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            long fileLen = stream.Length;

            // 快速预滤：先算头 4KB + 尾 4KB
            if (fileLen > headTailSize * 2)
            {
                var head = new byte[headTailSize];
                stream.Read(head, 0, headTailSize);
                ct.ThrowIfCancellationRequested();
                md5.TransformBlock(head, 0, headTailSize, null, 0);

                stream.Seek(-headTailSize, SeekOrigin.End);
                var tail = new byte[headTailSize];
                stream.Read(tail, 0, headTailSize);
                ct.ThrowIfCancellationRequested();
                md5.TransformBlock(tail, 0, headTailSize, null, 0);

                // 中间部分分块读取（用 long 计数，支持 >2GB 文件，避免 int 溢出崩溃）
                stream.Seek(headTailSize, SeekOrigin.Begin);
                long remaining = fileLen - headTailSize * 2;
                var buffer = new byte[chunkSize];
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(remaining, chunkSize);
                    int read = stream.Read(buffer, 0, toRead);
                    if (read == 0) break;
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    remaining -= read;
                }
            }
            else
            {
                // 小文件直接读
                var smallBuffer = new byte[chunkSize];
                int read;
                while ((read = stream.Read(smallBuffer, 0, chunkSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5.TransformBlock(smallBuffer, 0, read, null, 0);
                }
            }

            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 快速指纹：仅读取文件头 4KB + 尾 4KB 计算 MD5（不读中间部分）。
        /// 不同文件头尾大概率不同，可在不读取整文件的前提下快速排除绝大多数非重复文件，
        /// 仅当两文件“大小相同且头尾相同”时才需 ComputeHashChunked 做全量确认。
        /// </summary>
        private static string ComputeQuickHash(string filePath, CancellationToken ct)
        {
            const int sampleSize = 4096;
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            long fileLen = stream.Length;

            if (fileLen <= sampleSize * 2)
            {
                // 小文件（≤8KB）：直接整文件哈希
                var small = new byte[65536];
                int read;
                while ((read = stream.Read(small, 0, small.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5.TransformBlock(small, 0, read, null, 0);
                }
            }
            else
            {
                // 仅取头 4KB + 尾 4KB 作为快速指纹
                var head = new byte[sampleSize];
                stream.Read(head, 0, sampleSize);
                ct.ThrowIfCancellationRequested();
                md5.TransformBlock(head, 0, sampleSize, null, 0);

                stream.Seek(-sampleSize, SeekOrigin.End);
                var tail = new byte[sampleSize];
                stream.Read(tail, 0, sampleSize);
                ct.ThrowIfCancellationRequested();
                md5.TransformBlock(tail, 0, sampleSize, null, 0);
            }

            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
