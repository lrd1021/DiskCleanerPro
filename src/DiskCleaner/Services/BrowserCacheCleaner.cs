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
    /// 浏览器缓存清理服务
    /// 支持 Chrome / Edge / Firefox，只清理缓存，不碰密码/书签/扩展
    /// </summary>
    public class BrowserCacheCleaner
    {
        public Action<int, string> OnProgress { get; set; }

        private string _localApp;

        public BrowserCacheCleaner()
        {
            _localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        /// <summary>
        /// 获取支持清理的浏览器列表
        /// </summary>
        public List<BrowserInfo> GetSupportedBrowsers()
        {
            var browsers = new List<BrowserInfo>();

            // Chrome
            var chromePath = Path.Combine(_localApp, @"Google\Chrome\User Data");
            if (Directory.Exists(chromePath))
            {
                browsers.Add(new BrowserInfo
                {
                    Name = "Google Chrome",
                    Icon = "",
                    Profiles = GetChromeProfiles(chromePath),
                    CacheDirs = new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" }
                });
            }

            // Edge
            var edgePath = Path.Combine(_localApp, @"Microsoft\Edge\User Data");
            if (Directory.Exists(edgePath))
            {
                browsers.Add(new BrowserInfo
                {
                    Name = "Microsoft Edge",
                    Icon = "",
                    Profiles = GetChromeProfiles(edgePath),
                    CacheDirs = new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" }
                });
            }

            // Firefox
            var firefoxPath = Path.Combine(_localApp, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(firefoxPath))
            {
                var profiles = new List<string>();
                foreach (var d in Directory.GetDirectories(firefoxPath))
                    profiles.Add(d);
                if (profiles.Count > 0)
                    browsers.Add(new BrowserInfo
                    {
                        Name = "Mozilla Firefox",
                        Icon = "",
                        Profiles = profiles,
                        CacheDirs = new[] { "cache2" }
                    });
            }

            return browsers;
        }

        private List<string> GetChromeProfiles(string userDataPath)
        {
            var profiles = new List<string>();
            if (Directory.Exists(Path.Combine(userDataPath, "Default")))
                profiles.Add(Path.Combine(userDataPath, "Default"));
            // 多配置文件
            foreach (var d in Directory.GetDirectories(userDataPath, "Profile *"))
                profiles.Add(d);
            return profiles;
        }

        /// <summary>
        /// 扫描浏览器缓存大小
        /// </summary>
        public async Task ScanAsync(List<BrowserInfo> browsers, CancellationToken ct = default)
        {
            int total = browsers.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var browser = browsers[i];
                OnProgress?.Invoke((int)((float)i / total * 100), $"扫描：{browser.Name}");

                browser.CacheSizeBytes = 0;
                browser.CacheFileCount = 0;

                await Task.Run(() =>
                {
                    foreach (var profile in browser.Profiles)
                    {
                        foreach (var cacheDir in browser.CacheDirs)
                        {
                            var cachePath = Path.Combine(profile, cacheDir);
                            var (size, count) = GetDirectorySize(cachePath, ct);
                            browser.CacheSizeBytes += size;
                            browser.CacheFileCount += count;
                        }
                    }
                }, ct);

                browser.IsScanned = true;
                OnProgress?.Invoke((int)((float)(i + 1) / total * 100), $"完成：{browser.Name}");
            }
        }

        /// <summary>
        /// 清理浏览器缓存。useRecycleBin=true 走系统回收站；否则默认移入保险箱软删除（可恢复、不黑屏）。
        /// </summary>
        public async Task<(long freed, int deleted)> CleanAsync(
            List<BrowserInfo> browsers, bool useRecycleBin, CancellationToken ct = default)
        {
            long totalFreed = 0;
            int totalDeleted = 0;

            foreach (var browser in browsers)
            {
                ct.ThrowIfCancellationRequested();
                if (!browser.IsSelected) continue;

                foreach (var profile in browser.Profiles)
                {
                    foreach (var cacheDir in browser.CacheDirs)
                    {
                        var cachePath = Path.Combine(profile, cacheDir);
                        if (!Directory.Exists(cachePath)) continue;

            var (freed, deleted) = await Task.Run(() =>
                DeleteCacheContents(cachePath, useRecycleBin, browser.Name, ct), ct);
                        totalFreed += freed;
                        totalDeleted += deleted;
                    }
                }
                browser.CacheSizeBytes = 0;
            }

            return (totalFreed, totalDeleted);
        }

        private (long freed, int deleted) DeleteCacheContents(string path, bool useRecycleBin, string browserName, CancellationToken ct)
        {
            long freed = 0;
            int deleted = 0;
            if (!Directory.Exists(path)) return (0, 0);

            // 保险箱模式删除受保护目录时，整体走 ElevatedHelper（避免 asInvoker 下逐个文件失败）
            if (!useRecycleBin && ElevationHelper.IsProtectedPath(path))
            {
                long size = 0;
                try
                {
                    foreach (var f in SafeGetAllFiles(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { size += new FileInfo(f).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.Warning($"统计受保护目录大小失败 [{path}]: {ex.Message}");
                }
                if (ElevationHelper.DeleteElevated(path))
                    return (size, 1);
                return (0, 0);
            }

            var files = SafeGetAllFiles(path).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastReported = 0;
            var recycledPaths = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (useRecycleBin)
                    {
                        // 走回收站（必须在 UI 线程执行才能保证 FOF_ALLOWUNDO 进回收站）；
                        // 先收集路径，循环结束统一批量删除，避免逐文件切线程。
                        recycledPaths.Add(file);
                    }
                    else
                    {
                        // 默认走"保险箱"软删除（可恢复、不黑屏）；软删失败 best-effort 回退 File.Delete。
                        QuarantineService.MoveToQuarantine(file, out long size);
                        if (!File.Exists(file)) { freed += size; deleted++; }
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }

                bool byCount = (i & 0x3FF) == 0;
                bool byTime = sw.ElapsedMilliseconds - lastReported >= 500;
                if (byCount || byTime || i == files.Count - 1)
                {
                    lastReported = sw.ElapsedMilliseconds;
                    string eta = "";
                    if (i > 0 && sw.Elapsed.TotalSeconds > 0.5 && i < files.Count - 1)
                    {
                        double remain = sw.Elapsed.TotalSeconds * (files.Count - i - 1) / (i + 1);
                        if (remain < 60) eta = $"，约剩 {remain:F0} 秒";
                        else if (remain < 3600) eta = $"，约剩 {(int)(remain / 60)} 分 {(int)(remain % 60)} 秒";
                        else eta = $"，约剩 {(int)(remain / 3600)} 小时 {(int)((remain % 3600) / 60)} 分";
                    }
                OnProgress?.Invoke((int)((double)(i + 1) / files.Count * 100), $"正在{(useRecycleBin ? "移入回收站" : "移入保险箱")}：{i + 1}/{files.Count} 个文件{eta}");
            }

            if (useRecycleBin && recycledPaths.Count > 0)
            {
                var failed = NativeMethods.SendToRecycleBinOnUIThread(recycledPaths,
                    (p, t) => OnProgress?.Invoke((int)((double)p / t * 100), $"正在移入回收站：{p}/{t}"),
                    batchSize: 250, maxBatchBytes: 200L * 1024 * 1024);
                var stillExist = new HashSet<string>(failed.Where(File.Exists));
                foreach (var f in recycledPaths)
                    if (!stillExist.Contains(f)) { freed += new FileInfo(f).Length; deleted++; }
                RecycleBinManager.SourceTracker.Record(recycledPaths, browserName);
            }
            }

            // 清理空目录（仅保险箱模式）；不跟随交接点，避免误删其目标目录
            if (!useRecycleBin)
            {
                try
                {
                    // 深度优先收集目录（跳过交接点），再从最深开始删除空目录
                    var ordered = new List<string>();
                    var dirStack = new Stack<string>();
                    dirStack.Push(path);
                    while (dirStack.Count > 0)
                    {
                        var cur = dirStack.Pop();
                        try
                        {
                            // 用 ForEachEntry 读取枚举层 Attributes（非阻塞，不访问 junction 目标），
                            // 避免对子目录调用 File.GetAttributes 在失效/离线 junction 上阻塞（同其他模块修复）。
                            NativeMethods.ForEachEntry(cur, e =>
                            {
                                if (e.Name == "." || e.Name == "..") return;
                                if (!e.IsDirectory) return;
                                if (e.IsReparsePoint) return;      // 不跟随重解析点
                                var full = Path.Combine(cur, e.Name);
                                ordered.Add(full);
                                dirStack.Push(full);
                            });
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    ordered.Sort((a, b) => b.Length.CompareTo(a.Length));
                    foreach (var dir in ordered)
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
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Logger.Warning($"清理空目录失败 [{path}]: {ex.Message}");
                }
            }

            return (freed, deleted);
        }

        private (long size, int count) GetDirectorySize(string path, CancellationToken ct)
        {
            if (!Directory.Exists(path)) return (0, 0);
            long size = 0;
            int count = 0;
            var stack = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push(path);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                {
                    Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{current}");
                    continue;
                }
                ct.ThrowIfCancellationRequested();

                try
                {
                    NativeMethods.ForEachEntry(current, e =>
                    {
                        if (e.Name == "." || e.Name == "..") return;
                        if (e.IsReparsePoint) return;           // 不跟随重解析点
                        var full = Path.Combine(current, e.Name);
                        if (e.IsDirectory)
                        {
                            if (!visited.Add(full))
                            {
                                Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{full}");
                                return;
                            }
                            stack.Push(full);
                        }
                        else
                        {
                            size += e.Size;
                            count++;
                        }
                    }, ct);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (size, count);
        }

        private IEnumerable<string> SafeGetAllFiles(string root)
        {
            var stack = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current))
                {
                    Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{current}");
                    continue;
                }

                // 文件与子目录一次枚举完成（ForEachEntry 从枚举缓存取大小/属性，非阻塞）
                var files = new List<string>();
                try
                {
                    NativeMethods.ForEachEntry(current, e =>
                    {
                        if (e.Name == "." || e.Name == "..") return;
                        if (e.IsReparsePoint) return;      // 不跟随重解析点
                        var full = Path.Combine(current, e.Name);
                        if (e.IsDirectory)
                        {
                            if (!visited.Add(full))
                            {
                                Logger.Warning($"检测到重复遍历目录（疑似循环链接），已防止无限枚举：{full}");
                                return;
                            }
                            stack.Push(full);
                        }
                        else
                        {
                            files.Add(full);
                        }
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                foreach (var f in files) yield return f;
            }
        }
    }

    /// <summary>浏览器信息</summary>
    public class BrowserInfo : ViewModelBase
    {
        private bool _isSelected = true;
        private bool _isScanned;
        private long _cacheSizeBytes;
        private int _cacheFileCount;

        public string Name { get; set; }
        public string Icon { get; set; }
        public List<string> Profiles { get; set; } = new List<string>();
        public string[] CacheDirs { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public bool IsScanned
        {
            get => _isScanned;
            set => Set(ref _isScanned, value);
        }

        public long CacheSizeBytes
        {
            get => _cacheSizeBytes;
            set
            {
                Set(ref _cacheSizeBytes, value);
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(Status));
            }
        }

        public int CacheFileCount
        {
            get => _cacheFileCount;
            set => Set(ref _cacheFileCount, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(CacheSizeBytes);
        public string Status => !IsScanned ? "未扫描" : CacheSizeBytes > 0 ? $"可释放 {SizeDisplay}" : "无需清理";
    }
}
