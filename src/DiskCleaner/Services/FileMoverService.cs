using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 文件搬家服务
    /// 将C盘大文件迁移到其他磁盘，可选创建符号链接保持原路径可用
    /// </summary>
    public class FileMoverService
    {
        public Action<int, string> OnProgress { get; set; }

        /// <summary>
        /// 扫描C盘指定目录下的大文件
        /// </summary>
        public async Task<List<LargeFileInfo>> ScanLargeFilesAsync(
            string rootPath, long minSize, CancellationToken ct = default)
        {
            var result = new List<LargeFileInfo>();
            OnProgress?.Invoke(0, "正在扫描大文件...");

            await Task.Run(() =>
            {
                var stack = new Stack<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                stack.Push(rootPath);
                long scanned = 0;

                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = stack.Pop();
                    if (!visited.Add(current))
                    {
                        Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{current}");
                        continue;
                    }

                    try
                    {
                        // 用 ForEachEntry 一次枚举同时拿到文件路径与大小，避免 Directory.GetFiles + new FileInfo() 二次 stat
                        NativeMethods.ForEachEntry(current, e =>
                        {
                            if (e.Name == "." || e.Name == "..") return;
                            if (e.IsReparsePoint) return;            // 不跟随重解析点
                            var full = Path.Combine(current, e.Name);
                            if (e.IsDirectory)
                            {
                                if (e.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                                    e.Name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase))
                                    return;
                                if (!visited.Add(full))
                                {
                                    Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{full}");
                                    return;
                                }
                                stack.Push(full);
                            }
                            else
                            {
                                if (e.Size >= minSize)
                                {
                                    result.Add(new LargeFileInfo
                                    {
                                        FilePath = full,
                                        FileName = e.Name,
                                        Directory = current,
                                        SizeBytes = e.Size,
                                        LastModified = e.LastWriteTime,
                                        Extension = Path.GetExtension(e.Name)
                                    });
                                }
                                scanned++;
                            }
                        }, ct);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Logger.Warning($"大文件扫描目录失败: {ex.Message}");
                    }

                    if (scanned % 500 == 0)
                        OnProgress?.Invoke(-1, $"已扫描 {scanned} 个文件，找到 {result.Count} 个大文件");
                }
            }, ct);

            result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            OnProgress?.Invoke(100, $"扫描完成，找到 {result.Count} 个大文件");
            return result;
        }

        /// <summary>
        /// 获取可用目标盘
        /// </summary>
        public List<DriveInfo> GetAvailableTargetDrives()
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.Name != "C:\\")
                .ToList();
        }

        /// <summary>
        /// 执行文件搬家
        /// </summary>
        public async Task<MoveTask> MoveFileAsync(
            LargeFileInfo file, string targetDir, bool createSymlink, CancellationToken ct = default)
        {
            var task = new MoveTask
            {
                FileName = file.FileName,
                SourcePath = file.FilePath,
                FileSizeBytes = file.SizeBytes,
                CreateSymlink = createSymlink,
                Status = MoveTask.MoveStatus.Moving,
                Progress = 0
            };

            string targetFileName = Path.GetFileName(file.FilePath);
            string targetPath = Path.Combine(targetDir, targetFileName);

            // 处理重名
            int dup = 1;
            while (File.Exists(targetPath))
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(targetFileName);
                var ext = Path.GetExtension(targetFileName);
                targetPath = Path.Combine(targetDir, $"{nameNoExt} ({dup}){ext}");
                dup++;
            }

            task.TargetPath = targetPath;

            await Task.Run(() =>
            {
                try
                {
                    // 确保目标目录存在
                    Directory.CreateDirectory(targetDir);

                    // 复制文件（带进度）
                    CopyFileWithProgress(file.FilePath, targetPath, task, ct);

                    // 验证复制完整性
                    if (new FileInfo(targetPath).Length != file.SizeBytes)
                        throw new IOException("文件大小不匹配，复制可能不完整");

                    // 如果需要符号链接：先删除源文件释放路径，再创建指向目标的符号链接
                    // （CreateSymbolicLink 要求目标路径不存在，否则返回 ERROR_ALREADY_EXISTS）
                    if (createSymlink)
                    {
                        // 检测源路径是否为 ReparsePoint（防止符号链接攻击）
                        if ((File.GetAttributes(file.FilePath) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("源文件是符号链接/交接点，拒绝操作");

                        // 复制已校验，源可安全删除
                        File.Delete(file.FilePath);

                        if (!ElevationHelper.CreateSymlinkElevated(file.FilePath, targetPath))
                        {
                            // 建链失败（通常缺少管理员权限或用户取消 UAC）：文件已安全移动到目标，按纯移动处理
                            task.Status = MoveTask.MoveStatus.Completed;
                            task.Progress = 100;
                            OnProgress?.Invoke(100, $"已完成（符号链接创建失败，已移动文件到目标）：{file.FileName}");
                            return;
                        }
                    }
                    else
                    {
                        // 纯移动：直接删除源文件
                        File.Delete(file.FilePath);
                    }

                    task.Status = MoveTask.MoveStatus.Completed;
                    task.Progress = 100;
                    OnProgress?.Invoke(100, $"已完成：{file.FileName}");
                }
                catch (OperationCanceledException)
                {
                    task.Status = MoveTask.MoveStatus.Skipped;
                    // 清理半成品
                    try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                catch (Exception ex)
                {
                    task.Status = MoveTask.MoveStatus.Failed;
                    OnProgress?.Invoke(100, $"失败：{ex.Message}");
                    // 清理半成品
                    try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }, ct);

            return task;
        }

        private void CopyFileWithProgress(string source, string target, MoveTask task, CancellationToken ct)
        {
            const int bufferSize = 1024 * 1024; // 1MB buffer
            byte[] buffer = new byte[bufferSize];
            long totalBytes = new FileInfo(source).Length;
            long copiedBytes = 0;

            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                int read;
                while ((read = src.Read(buffer, 0, bufferSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    copiedBytes += read;

                    int pct = (int)((double)copiedBytes / totalBytes * 100);
                    task.Progress = pct;
                    OnProgress?.Invoke(pct, $"搬移中... {pct}%");
                }
            }
        }
    }

    /// <summary>大文件信息</summary>
    public class LargeFileInfo : ViewModelBase
    {
        private bool _isSelected;

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public string Extension { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
        public string LastModifiedDisplay => LastModified.ToString("yyyy-MM-dd");
    }
}
