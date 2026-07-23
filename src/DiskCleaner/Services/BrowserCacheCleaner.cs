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
                    Icon = "🌐",
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
                    Icon = "🌐",
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
                        Icon = "🦊",
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
        /// 清理浏览器缓存
        /// </summary>
        public async Task<(long freed, int deleted)> CleanAsync(
            List<BrowserInfo> browsers, bool permanent, CancellationToken ct = default)
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
                            DeleteCacheContents(cachePath, permanent, ct), ct);
                        totalFreed += freed;
                        totalDeleted += deleted;
                    }
                }
                browser.CacheSizeBytes = 0;
            }

            return (totalFreed, totalDeleted);
        }

        private (long freed, int deleted) DeleteCacheContents(string path, bool permanent, CancellationToken ct)
        {
            long freed = 0;
            int deleted = 0;
            if (!Directory.Exists(path)) return (0, 0);

            // 永久删除受保护目录时，整体走 ElevatedHelper
            if (permanent && ElevationHelper.IsProtectedPath(path))
            {
                long size = 0;
                try
                {
                    foreach (var f in SafeGetAllFiles(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { size += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                if (ElevationHelper.DeleteElevated(path))
                    return (size, 1);
                return (0, 0);
            }

            var files = SafeGetAllFiles(path);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    long size = fi.Length;

                    if (permanent)
                    {
                        fi.Delete();
                    }
                    else
                    {
                        // 走回收站；失败则跳过，不静默永久删除
                        if (!NativeMethods.SendToRecycleBin(file, out var err))
                        {
                            Logger.Warning($"回收站删除失败 [{file}]: 0x{err:X}");
                            continue;
                        }
                    }

                    freed += size;
                    deleted++;
                }
                catch { /* 文件可能被浏览器占用 */ }
            }

            // 清理空目录（仅永久删除模式）；不跟随交接点，避免误删其目标目录
            if (permanent)
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
                        catch { /* */ }
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
                        catch { /* */ }
                    }
                }
                catch { /* */ }
            }

            return (freed, deleted);
        }

        private (long size, int count) GetDirectorySize(string path, CancellationToken ct)
        {
            if (!Directory.Exists(path)) return (0, 0);
            long size = 0;
            int count = 0;

            foreach (var file in SafeGetAllFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    size += new FileInfo(file).Length;
                    count++;
                }
                catch { /* */ }
            }
            return (size, count);
        }

        private IEnumerable<string> SafeGetAllFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();

                // 文件枚举结果先取出（yield 不能位于带 catch 的 try 内）
                string[] files = null;
                try { files = Directory.GetFiles(current); }
                catch { /* 无权限 */ }
                if (files != null)
                {
                    foreach (var f in files)
                        yield return f;
                }

                // 子目录收集（跳过重解析点，避免误删其指向的目标目录）
                // 用 ForEachEntry 读取枚举层 Attributes（非阻塞，不访问 junction 目标），
                // 避免对子目录调用 File.GetAttributes 在失效/离线 junction 上阻塞（同其他模块修复）。
                try
                {
                    NativeMethods.ForEachEntry(current, e =>
                    {
                        if (e.Name == "." || e.Name == "..") return;
                        if (!e.IsDirectory) return;
                        if (e.IsReparsePoint) return;      // 不跟随重解析点
                        stack.Push(Path.Combine(current, e.Name));
                    });
                }
                catch { /* 无权限 */ }
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
