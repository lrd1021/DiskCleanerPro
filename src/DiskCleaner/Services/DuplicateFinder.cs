using System;
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

        private static readonly HashSet<string> SkipDirs = NativeMethods.ProtectedDirectories;

        public long MinFileSize { get; set; } = 1 * 1024 * 1024;

        public async Task<List<DuplicateGroup>> FindDuplicatesAsync(
            string rootPath, CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                ReportProgress(0, "正在收集文件列表...");

                // 第1步：收集所有符合条件的文件
                var allFiles = new List<FileMeta>();
                var stack = new Stack<string>();
                var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                stack.Push(rootPath);
                int dirsScanned = 0;

                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = stack.Pop();
                    if (!visitedDirs.Add(current)) continue;
                    dirsScanned++;

                    if (dirsScanned % 2000 == 0)
                        ReportProgress(-1, $"正在扫描... {dirsScanned} 目录, {allFiles.Count} 文件");

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(current))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                if (NativeMethods.TryGetFileMeta(file, out var meta) && meta.Length >= MinFileSize)
                                allFiles.Add(meta);
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(current))
                        {
                            var name = Path.GetFileName(dir);
                            if (SkipDirs.Contains(name)) continue;

                            try
                            {
                                var attr = File.GetAttributes(dir);
                                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }

                            stack.Push(dir);
                        }
                    }
                    catch { }
                }

                ReportProgress(20, $"共收集 {allFiles.Count} 个文件，开始分组...");

                // 第2步：按文件大小分组
                var sizeGroups = allFiles
                    .GroupBy(f => f.Length)
                    .Where(g => g.Count() > 1)
                    .ToList();

                ReportProgress(40, $"大小相同的组：{sizeGroups.Count}，计算哈希...");

                // 第3步：同大小计算 MD5
                var result = new List<DuplicateGroup>();
                int processed = 0;
                int totalGroups = sizeGroups.Count;

                foreach (var group in sizeGroups)
                {
                    ct.ThrowIfCancellationRequested();

                    var hashGroups = new Dictionary<string, List<FileMeta>>();
                    foreach (var fi in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            string hash = ComputeHashChunked(fi.FullName, ct);
                            if (!hashGroups.ContainsKey(hash))
                                hashGroups[hash] = new List<FileMeta>();
                            hashGroups[hash].Add(fi);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }

                    foreach (var kv in hashGroups.Where(g => g.Value.Count > 1))
                    {
                        var dupGroup = new DuplicateGroup
                        {
                            Hash = kv.Key,
                            FileSize = group.Key,
                            WasteBytes = group.Key * (kv.Value.Count - 1)
                        };
                        bool first = true;
                        foreach (var fi in kv.Value.OrderBy(f => f.LastModified))
                        {
                            dupGroup.Files.Add(new DuplicateFile
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                Directory = Path.GetDirectoryName(fi.FullName),
                                LastModified = fi.LastModified,
                                KeepThis = first
                            });
                            first = false;
                        }
                        result.Add(dupGroup);
                    }

                    processed++;
                    ReportProgress(40 + (int)((float)processed / totalGroups * 55),
                        $"已处理 {processed}/{totalGroups} 组，发现 {result.Count} 组重复");
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
    }
}
