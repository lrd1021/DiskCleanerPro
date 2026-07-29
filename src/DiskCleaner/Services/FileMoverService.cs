using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiskCleaner.Helpers;
using DiskCleaner.Models;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 应用数据搬家服务（目录级）
    /// 将 C 盘用户目录下占用空间较大的“应用数据目录”整体迁移到其它盘，
    /// 并在原位创建目录 junction（无需管理员权限），使依赖原路径的应用无感知、可正常使用。
    /// </summary>
    public class FileMoverService
    {
        public Action<int, string> OnProgress { get; set; }

        private static readonly string ManifestPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskCleanerPro", "MovedDirs.json");

        // ── 三级搬家黑名单 ──
        // 1) 根层系统目录：整块屏蔽（不显示、不深入）——既无意义也危险
        private static readonly HashSet<string> HardBlockSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData",
            "$Recycle.Bin", "Recovery", "System Volume Information", "$SysReset",
            "Documents and Settings", "Config.Msi", "Users"
        };

        // 2) 软屏蔽容器：本身不显示在列表，但继续深入其子目录，
        //    以便露出内部真正可搬的应用缓存/数据子目录（如 AppData 下的 Edge/Tuanjie/Unity 缓存）
        private static readonly HashSet<string> SoftBlockPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AppData",
            "AppData\\Local",
            "AppData\\Roaming",
            "AppData\\LocalLow",
            "Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos",
            "Favorites", "Links", "Saved Games", "OneDrive"
        };

        // 3) 深层危险路径：整块屏蔽（不显示、不深入）——
        //    AppData 下的系统/程序相关目录，搬动会导致应用或系统异常
        private static readonly string[] HardBlockPaths = new[]
        {
            "AppData\\Local\\Programs",
            "AppData\\Roaming\\Programs",
            "AppData\\Local\\Microsoft\\Windows",
            "AppData\\Roaming\\Microsoft\\Windows",
            "AppData\\LocalLow\\Microsoft\\Windows",
            "AppData\\Local\\Microsoft\\WindowsApps"
        };

        private static string NormalizeRoot(string root) => root.EndsWith("\\") ? root : root + "\\";

        private static string GetRelPath(string rootNorm, string full)
        {
            if (full.Length > rootNorm.Length && full.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase))
                return full.Substring(rootNorm.Length).TrimStart('\\');
            return Path.GetFileName(full);
        }

        /// <summary>通用（不依赖 root）的受保护路径判断：用于搬家前最后兜底拦截。</summary>
        private static bool IsPathProtected(string path)
        {
            var p = path.Replace('/', '\\').TrimEnd('\\');
            foreach (var seg in HardBlockSegments)
                if (p.IndexOf("\\" + seg + "\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.EndsWith("\\" + seg, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var hb in HardBlockPaths)
                if (p.IndexOf("\\" + hb + "\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.EndsWith("\\" + hb, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var sp in SoftBlockPaths)
                if (p.IndexOf("\\" + sp + "\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.EndsWith("\\" + sp, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 按目录体积聚合扫描：一次迭代后序遍历算出每个目录体积，
        /// 返回体积 >= minSize 的目录（降序、截断到 top 300）。
        /// </summary>
        public async Task<List<DirectorySizeInfo>> ScanLargeDirectoriesAsync(
            string rootPath, long minSize, CancellationToken ct = default)
        {
            OnProgress?.Invoke(0, "正在扫描目录体积...");
            var result = new List<DirectorySizeInfo>();

            await Task.Run(() =>
            {
                // 1) 收集所有目录（前序），显式栈遍历，visited 防循环
                var order = new List<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var stack = new Stack<string>();
                var rootNorm = NormalizeRoot(rootPath);
                var rootNoTail = rootPath.TrimEnd('\\');
                visited.Add(rootPath);
                stack.Push(rootPath);
                long dirCount = 0;

                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var cur = stack.Pop();
                    order.Add(cur);

                    NativeMethods.ForEachEntry(cur, e =>
                    {
                        if (e.IsReparsePoint) return;          // 不跟随 junction/符号链接
                        if (!e.IsDirectory) return;
                        var full = Path.Combine(cur, e.Name);
                        var rel = GetRelPath(rootNorm, full);

                        // 根层系统目录整块屏蔽（Users 也在内，避免搬整个用户目录）
                        if (string.Equals(cur.TrimEnd('\\'), rootNoTail, StringComparison.OrdinalIgnoreCase) &&
                            HardBlockSegments.Contains(e.Name))
                            return;

                        // 深层危险路径整块屏蔽（不显示、不深入）
                        if (HardBlockPaths.Any(p => rel.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                                   rel.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)))
                            return;

                        // 软屏蔽容器：本身不加入列表，但继续深入其子目录，露出内部可搬的子目录
                        if (SoftBlockPaths.Contains(rel))
                        {
                            if (visited.Add(full)) stack.Push(full);
                            return;
                        }

                        if (visited.Add(full))
                            stack.Push(full);
                    }, ct);

                    if (++dirCount % 200 == 0)
                        OnProgress?.Invoke(-1, $"已枚举 {dirCount} 个目录...");
                }

                // 2) 后序计算体积：从 order 末尾向前，父目录累加已算好的子目录体积
                var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var fileMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = order.Count - 1; i >= 0; i--)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = order[i];
                    long size = 0;
                    int files = 0;
                    NativeMethods.ForEachEntry(dir, e =>
                    {
                        if (e.IsReparsePoint) return;
                        var full = Path.Combine(dir, e.Name);
                        if (e.IsDirectory)
                        {
                            if (sizeMap.TryGetValue(full, out var childSize))
                                size += childSize;
                        }
                        else
                        {
                            size += e.Size;
                            files++;
                        }
                    }, ct);
                    sizeMap[dir] = size;
                    fileMap[dir] = files;
                }

                // 3) 过滤 + 排序 + 截断
                var rootNorm2 = NormalizeRoot(rootPath);
                foreach (var dir in order)
                {
                    if (dir.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = GetRelPath(rootNorm2, dir);
                    // 软屏蔽容器本身不显示（其内部可搬子目录已单独入列）
                    if (SoftBlockPaths.Contains(rel)) continue;
                    if (sizeMap.TryGetValue(dir, out var sz) && sz >= minSize)
                        result.Add(new DirectorySizeInfo
                        {
                            DirectoryPath = dir,
                            SizeBytes = sz,
                            FileCount = fileMap.TryGetValue(dir, out var fc) ? fc : 0
                        });
                }

                result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                if (result.Count > 300)
                    result = result.GetRange(0, 300);

                OnProgress?.Invoke(100, $"扫描完成，找到 {result.Count} 个大于 {FileSizeFormatter.Format(minSize)} 的目录");
            }, ct);

            return result;
        }

        /// <summary>
        /// 获取可用目标盘（非 C 盘的本地固定盘）。
        /// </summary>
        public List<DriveInfo> GetAvailableTargetDrives()
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.Equals("C:\\", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 整体搬移一个目录到目标盘，并在原位创建 junction（若 createJunction=true）。
        /// 失败会尽量回滚到搬移前状态。
        /// </summary>
        public async Task<MoveTask> MoveDirectoryAsync(
            DirectorySizeInfo dir, string targetDrive, bool createJunction, CancellationToken ct = default)
        {
            var task = new MoveTask
            {
                FileName = dir.DirectoryName,
                SourcePath = dir.DirectoryPath,
                FileSizeBytes = dir.SizeBytes,
                CreateSymlink = createJunction,
                Status = MoveTask.MoveStatus.Moving,
                Progress = 0
            };

            await Task.Run(() =>
            {
                string src = dir.DirectoryPath;
                string baseName = new DirectoryInfo(src).Name;
                string targetBase = Path.Combine(targetDrive, "MovedFromC");
                string targetDir = Path.Combine(targetBase, baseName);

                try
                {
                    if (IsPathProtected(src))
                        throw new IOException("该目录受保护，不允许搬家（系统/用户关键目录）");
                    if (!Directory.Exists(src))
                        throw new IOException("源目录不存在");
                    if (JunctionHelper.IsJunction(src))
                        throw new IOException("源已是 junction/符号链接，跳过以防循环");

                    // 重名处理
                    int dup = 1;
                    string candidate = targetDir;
                    while (Directory.Exists(candidate) || File.Exists(candidate))
                        candidate = $"{targetDir} ({dup++})";
                    targetDir = candidate;

                    Directory.CreateDirectory(targetBase);

                    // 跨盘移动（同盘为 rename、跨盘为复制+删除；复制失败会抛异常）
                    Directory.Move(src, targetDir);

                    if (createJunction)
                    {
                        if (!JunctionHelper.CreateJunction(src, targetDir))
                        {
                            // 建链失败：把目录移回原位，保证“要么完整搬家+可用，要么原样不动”
                            try { if (!Directory.Exists(src) && Directory.Exists(targetDir)) Directory.Move(targetDir, src); }
                            catch { }
                            throw new IOException("创建 junction 失败，已回滚到搬移前状态");
                        }
                        RecordMoved(src, targetDir);
                    }

                    task.TargetPath = targetDir;
                    task.Status = MoveTask.MoveStatus.Completed;
                    task.Progress = 100;
                    OnProgress?.Invoke(100, createJunction
                        ? $"已完成（已建 junction，应用无感知）：{baseName}"
                        : $"已完成（纯移动，原路径已失效）：{baseName}");
                }
                catch (OperationCanceledException)
                {
                    task.Status = MoveTask.MoveStatus.Skipped;
                    try { if (!Directory.Exists(src) && Directory.Exists(targetDir)) Directory.Move(targetDir, src); } catch { }
                }
                catch (Exception ex)
                {
                    task.Status = MoveTask.MoveStatus.Failed;
                    // 回滚：源消失而目标存在时搬回
                    try { if (!Directory.Exists(src) && Directory.Exists(targetDir)) Directory.Move(targetDir, src); } catch { }
                    OnProgress?.Invoke(100, $"失败：{ex.Message}");
                }
            }, ct);

            return task;
        }

        /// <summary>
        /// 把之前搬走的目录搬回原位：删除原位的 junction，把目标目录移回。
        /// </summary>
        public async Task<MoveTask> MoveBackAsync(DirectorySizeInfo dir, CancellationToken ct = default)
        {
            var task = new MoveTask
            {
                FileName = dir.DirectoryName,
                SourcePath = dir.DirectoryPath,
                FileSizeBytes = dir.SizeBytes,
                Status = MoveTask.MoveStatus.Moving,
                Progress = 0
            };

            var manifest = LoadMovedManifest();
            if (!manifest.TryGetValue(dir.DirectoryPath, out var target) || !Directory.Exists(target))
            {
                task.Status = MoveTask.MoveStatus.Failed;
                task.Progress = 100;
                OnProgress?.Invoke(100, "找不到搬家记录或目标已不存在，无法搬回");
                return task;
            }

            await Task.Run(() =>
            {
                try
                {
                    if (!JunctionHelper.IsJunction(dir.DirectoryPath))
                        throw new IOException("原位已不是 junction，无法安全搬回（以免误删目标内容）");

                    JunctionHelper.DeleteJunction(dir.DirectoryPath);
                    Directory.Move(target, dir.DirectoryPath);

                    manifest.Remove(dir.DirectoryPath);
                    SaveMovedManifest(manifest);

                    task.TargetPath = target;
                    task.Status = MoveTask.MoveStatus.Completed;
                    task.Progress = 100;
                    OnProgress?.Invoke(100, $"已搬回：{dir.DirectoryName}");
                }
                catch (OperationCanceledException)
                {
                    task.Status = MoveTask.MoveStatus.Skipped;
                }
                catch (Exception ex)
                {
                    task.Status = MoveTask.MoveStatus.Failed;
                    OnProgress?.Invoke(100, $"搬回失败：{ex.Message}");
                }
            }, ct);

            return task;
        }

        // ── Manifest：记录 原路径 -> 目标路径 ──

        public Dictionary<string, string> LoadMovedManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(ManifestPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return dict ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Logger.Warning($"读取搬家清单失败: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void RecordMoved(string original, string target)
        {
            var dict = LoadMovedManifest();
            dict[original] = target;
            SaveMovedManifest(dict);
        }

        private void SaveMovedManifest(Dictionary<string, string> dict)
        {
            try
            {
                var dir = Path.GetDirectoryName(ManifestPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ManifestPath, json);
            }
            catch (Exception ex)
            {
                Logger.Warning($"写入搬家清单失败: {ex.Message}");
            }
        }
    }

    /// <summary>目录体积信息（按目录聚合）</summary>
    public class DirectorySizeInfo : ViewModelBase
    {
        private bool _isSelected;

        public string DirectoryPath { get; set; }
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }

        public bool IsMoved { get; set; }
        public string MovedToPath { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string DirectoryName => string.IsNullOrEmpty(DirectoryPath)
            ? ""
            : Path.GetFileName(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
        public string FileCountDisplay => $"{FileCount:N0} 个文件";
        public string StatusDisplay => IsMoved ? $"已搬至 {MovedToPath}" : "原地";
    }
}
