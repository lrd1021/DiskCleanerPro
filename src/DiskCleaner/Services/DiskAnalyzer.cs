using System;
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
    /// 磁盘空间分析服务
    /// 递归扫描目录，计算各子目录/文件大小，构建可视化文件树
    /// </summary>
    public class DiskAnalyzer
    {
        public Action<int, string> OnProgress { get; set; }

        // 跳过这些系统目录（无意义且极慢）
        private static readonly HashSet<string> SkipDirs = NativeMethods.ProtectedDirectories;

        /// <summary>
        /// 分析指定根目录，返回文件树
        /// </summary>
        public async Task<FileNode> AnalyzeAsync(string rootPath, CancellationToken ct = default)
        {
            OnProgress?.Invoke(0, $"正在分析 {rootPath}...");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootNode = await Task.Run(() => BuildNode(rootPath, true, rootPath, ct, visited), ct);

            // 计算百分比
            CalculatePercentages(rootNode);

            OnProgress?.Invoke(100, "分析完成");
            return rootNode;
        }

        /// <summary>
        /// 分析C盘根目录下的主要一级文件夹
        /// </summary>
        public async Task<List<FileNode>> AnalyzeDriveAsync(string driveRoot, CancellationToken ct = default)
        {
            var result = new List<FileNode>();
            var entries = SafeEnumerateDirectories(driveRoot);

            int total = entries.Count;
            int done = 0;

            foreach (var dir in entries)
            {
                ct.ThrowIfCancellationRequested();
                OnProgress?.Invoke((int)((float)done / Math.Max(total, 1) * 100), $"扫描：{dir}");

                var node = await Task.Run(() => BuildNode(dir, true, dir, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), ct);
                result.Add(node);
                done++;
            }

            // 按大小降序排列
            result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

            // 计算百分比
            long totalSize = result.Sum(n => n.SizeBytes);
            foreach (var node in result)
            {
                if (totalSize > 0)
                    node.Percentage = (double)node.SizeBytes / totalSize * 100;
            }

            OnProgress?.Invoke(100, "分析完成");
            return result;
        }

        /// <summary>
        /// 迭代式（显式栈）后序遍历构建目录树。
        /// - 用显式栈替代递归，避免深目录（如 node_modules）爆栈（R7）
        /// - 子节点先收集到普通 List，最后一次性赋给 ObservableCollection，避免构建期频繁 CollectionChanged 通知风暴（R11）
        /// - 树完全构建完成、返回 UI 线程后才被绑定，杜绝后台线程修改 OC 引发的竞态崩溃（R9）
        /// - 用托管 DirectoryInfo.EnumerateFileSystemInfos 枚举（名称可靠、非阻塞，文件大小从枚举缓存读取），避免对子目录
        ///   调用 File.GetAttributes 在失效/离线 junction（如指向断网共享）上阻塞导致"卡死"（同临时文件扫描修复）。
        /// - 不跟随重解析点（junction/符号链接）；visited 集合兜底防循环链接，杜绝无限遍历（同临时文件扫描修复）。
        /// </summary>
        private FileNode BuildNode(string path, bool isDir, string rootPath, CancellationToken ct, HashSet<string> visited)
        {
            var rootFrame = new BuildFrame { Node = CreateNode(path, isDir), RootPath = rootPath };
            var stack = new Stack<BuildFrame>();
            stack.Push(rootFrame);
            long totalFiles = 0;

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var frame = stack.Peek();

                if (!frame.Expanded)
                {
                    frame.Expanded = true;
                    frame.SubDirs = new List<string>();

                    if (frame.Node.IsDirectory)
                    {
                        var dirPath = frame.Node.FullPath;
                        try
                        {
                            NativeMethods.ForEachEntry(dirPath, e =>
                            {
                                ct.ThrowIfCancellationRequested();
                                if (e.Name == "." || e.Name == "..") return;
                                if (e.IsReparsePoint) return;            // 不跟随重解析点（安全，且避免指向系统目录/断网共享时阻塞）
                                if (e.IsDirectory)
                                {
                                    var full = Path.Combine(dirPath, e.Name);
                                    if (SkipDirs.Contains(e.Name)) return;   // 跳过系统保护目录
                                    if (!visited.Add(full))                    // 防循环链接兜底
                                    {
                                        Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{full}");
                                        return;
                                    }
                                    frame.SubDirs.Add(full);
                                }
                                else
                                {
                                    frame.FileSizeSum += e.Size;
                                    frame.Children.Add(new FileNode
                                    {
                                        Name = e.Name,
                                        FullPath = Path.Combine(dirPath, e.Name),
                                        IsDirectory = false,
                                        SizeBytes = e.Size,
                                        Extension = Path.GetExtension(e.Name)
                                    });
                                    totalFiles++;
                                    if (totalFiles % 5000 == 0)
                                        OnProgress?.Invoke(-1, $"正在扫描 {frame.Node.Name}... ({FileSizeFormatter.Format(frame.FileSizeSum)})");
                                }
                            }, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { /* 目录无权限 */ }
                        catch (UnauthorizedAccessException) { /* 目录无权限 */ }
                    }

                }

                if (frame.SubDirIndex < frame.SubDirs.Count)
                {
                    var dir = frame.SubDirs[frame.SubDirIndex++];
                    stack.Push(new BuildFrame { Node = CreateNode(dir, true), RootPath = rootPath });
                    continue;
                }

                // 所有子目录已处理完，结算本帧
                FinalizeFrame(frame);
                stack.Pop();
                if (stack.Count > 0) AttachChild(stack.Peek(), frame.Node);
            }

            return rootFrame.Node;
        }

        private sealed class BuildFrame
        {
            public FileNode Node;
            public List<FileNode> Children = new List<FileNode>();
            public long FileSizeSum;
            public long DirSizeSum;
            public List<string> SubDirs;
            public int SubDirIndex;
            public bool Expanded;
            public string RootPath;
        }

        private static FileNode CreateNode(string path, bool isDir)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) && isDir) name = path; // 根目录
            return new FileNode
            {
                Name = name,
                FullPath = path,
                IsDirectory = isDir,
                Extension = isDir ? null : Path.GetExtension(path)
            };
        }

        private static void AttachChild(BuildFrame parent, FileNode childNode)
        {
            parent.Children.Add(childNode);
            parent.DirSizeSum += childNode.SizeBytes;
        }

        private void FinalizeFrame(BuildFrame frame)
        {
            frame.Node.SizeBytes = frame.FileSizeSum + frame.DirSizeSum;
            if (frame.Node.IsDirectory)
                frame.Node.LastModified = SafeGetLastModified(frame.Node.FullPath);
            frame.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            // 一次性赋给 OC（后台构建期不再触发 CollectionChanged 通知）
            frame.Node.Children = new ObservableCollection<FileNode>(frame.Children);
        }

        private void CalculatePercentages(FileNode node)
        {
            if (node.SizeBytes == 0 || node.Children.Count == 0) return;
            foreach (var child in node.Children)
            {
                child.Percentage = node.SizeBytes > 0
                    ? (double)child.SizeBytes / node.SizeBytes * 100
                    : 0;
                CalculatePercentages(child);
            }
        }

        private List<string> SafeEnumerateDirectories(string path)
        {
            var result = new List<string>();
            try
            {
                NativeMethods.ForEachEntry(path, e =>
                {
                    if (e.Name == "." || e.Name == "..") return;
                    if (!e.IsDirectory) return;
                    if (e.IsReparsePoint) return;        // 不扫描指向别处的 junction/符号链接（避免重复统计 C:\Users 等）
                    if (SkipDirs.Contains(e.Name)) return;
                    result.Add(Path.Combine(path, e.Name));
                });
            }
            catch (IOException ex)
            {
                Logger.Warning($"枚举目录失败 [{path}]: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warning($"无权限访问目录 [{path}]: {ex.Message}");
            }
            return result;
        }

        private string SafeGetLastModified(string path)
        {
            try { return Directory.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); }
            catch (IOException ex)
            {
                Logger.Warning($"获取目录最后修改时间失败 [{path}]: {ex.Message}");
                return "";
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Warning($"无权限获取目录最后修改时间 [{path}]: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 快速获取目录大小（不构建完整树，用于性能优化场景）
        /// 同样用原生枚举 + 不跟随重解析点 + visited 防循环，避免卡死与无限遍历。
        /// </summary>
        public long GetDirectorySizeFast(string path, CancellationToken ct = default)
        {
            if (!Directory.Exists(path)) return 0;
            long size = 0;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(path);
            visited.Add(path);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();

                try
                {
                    NativeMethods.ForEachEntry(current, e =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (e.Name == "." || e.Name == "..") return;
                        if (e.IsReparsePoint) return;
                        if (e.IsDirectory)
                        {
                            var full = Path.Combine(current, e.Name);
                            if (SkipDirs.Contains(e.Name)) return;
                            if (!visited.Add(full)) return;   // 防循环链接兜底
                            stack.Push(full);
                        }
                        else
                        {
                            size += e.Size;
                        }
                    }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { /* 无权限 */ }
                catch (UnauthorizedAccessException) { /* 无权限 */ }
            }

            return size;
        }
    }
}
