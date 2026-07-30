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
    /// 应用专清：针对微信、QQ 等应用的用户数据缓存清理。
    ///
    /// 安全策略（白名单）：
    /// 只清理已知的缓存 / 媒体子目录（Cache / Temp / Image / Video / File / Sns / PublicMsg /
    /// Applet / FileRecv / AudioMsg / WebEngine / GPUCache / Logs 等）；
    /// 聊天记录数据库（Msg 目录、*.db、Config、BackupFiles、Fav 库、登录态等）绝对不在清理范围。
    ///
    /// 扫描与删除逻辑全部复用 TempFileCleaner 已验证的实现（保险箱软删 / 系统回收站 / 批量永久删），
    /// 本类只负责探测路径并生成 CleanTarget 列表。
    /// </summary>
    public class AppCacheCleaner
    {
        public Action<int, string> OnProgress { get; set; }
        public Action<CleanTarget, long, long> OnTargetScanProgress { get; set; }

        private readonly TempFileCleaner _inner = new TempFileCleaner();

        public AppCacheCleaner()
        {
            _inner.OnProgress = (p, m) => OnProgress?.Invoke(p, m);
            _inner.OnTargetScanProgress = (t, s, c) => OnTargetScanProgress?.Invoke(t, s, c);
        }

        #region 清理类别规则（白名单）
        private sealed class CategoryRule
        {
            public string Name;
            public string Description;
            public string RelativeDir;   // 相对“账号根”或“应用根”的目录
            public bool DefaultSelected; // 默认是否勾选（缓存/临时默认勾，媒体默认不勾）
            public string Category;
        }

        // 微信：账号根 = %USERPROFILE%\Documents\WeChat Files\<wxid>\
        private static readonly CategoryRule[] WeChatCategories =
        {
            new CategoryRule { Name = "微信缓存", Description = "缩略图等临时缓存，删除后自动重建", RelativeDir = @"FileStorage\Cache", DefaultSelected = true, Category = "缓存" },
            new CategoryRule { Name = "微信临时文件", Description = "微信运行产生的临时文件", RelativeDir = @"FileStorage\Temp", DefaultSelected = true, Category = "临时" },
            new CategoryRule { Name = "微信图片缓存", Description = "看过的图片本地副本，删除不影响文字聊天记录", RelativeDir = @"FileStorage\Image", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "微信视频缓存", Description = "看过的视频本地副本", RelativeDir = @"FileStorage\Video", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "微信接收的文件", Description = "微信接收的文档/文件，删除后需重新下载", RelativeDir = @"FileStorage\File", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "微信朋友圈缓存", Description = "朋友圈图片/视频缓存", RelativeDir = @"FileStorage\Sns", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "微信公众号缓存", Description = "公众号图文缓存", RelativeDir = @"FileStorage\PublicMsg", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "微信小程序缓存", Description = "小程序运行缓存，删除仅影响首次启动速度", RelativeDir = @"Applet", DefaultSelected = true, Category = "缓存" },
        };

        // QQ 旧版 / TIM：账号根 = %USERPROFILE%\Documents\Tencent Files\<qq>\
        private static readonly CategoryRule[] QqCategories =
        {
            new CategoryRule { Name = "QQ缓存", Description = "各类缓存，删除后自动重建", RelativeDir = @"Cache", DefaultSelected = true, Category = "缓存" },
            new CategoryRule { Name = "QQ接收的文件", Description = "QQ接收的文档/文件，删除后需重新下载（建议谨慎）", RelativeDir = @"FileRecv", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "QQ图片缓存", Description = "聊天图片本地副本", RelativeDir = @"Image", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "QQ视频缓存", Description = "聊天视频本地副本", RelativeDir = @"Video", DefaultSelected = false, Category = "媒体" },
            new CategoryRule { Name = "QQ语音消息", Description = "语音消息本地副本", RelativeDir = @"AudioMsg", DefaultSelected = false, Category = "媒体" },
        };

        // QQ NT：固定路径 = %LOCALAPPDATA%\Tencent\QQ\（聊天库在 nt_qq 子目录，本规则不触碰）
        private static readonly CategoryRule[] QqNtCategories =
        {
            new CategoryRule { Name = "QQ内置浏览器缓存", Description = "QQ内置浏览器内核缓存", RelativeDir = @"WebEngine", DefaultSelected = true, Category = "缓存" },
            new CategoryRule { Name = "QQ GPU缓存", Description = "渲染缓存，可安全清理", RelativeDir = @"GPUCache", DefaultSelected = true, Category = "缓存" },
            new CategoryRule { Name = "QQ日志", Description = "运行日志", RelativeDir = @"Logs", DefaultSelected = true, Category = "日志" },
            new CategoryRule { Name = "QQ临时文件", Description = "QQ运行临时文件", RelativeDir = @"Temp", DefaultSelected = true, Category = "临时" },
        };
        #endregion

        /// <summary>
        /// 探测微信 / QQ 是否安装并登录过，枚举各账号下的白名单缓存目录，生成分组清理目标。
        /// 未安装或未登录过的应用不会出现在结果中（避免显示无关项）。
        /// </summary>
        public List<AppCacheGroup> GetGroups()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var groups = new List<AppCacheGroup>();

            // 微信：WeChat Files\<wxid>\
            var wechatRoot = Path.Combine(docs, "WeChat Files");
            var wechatGroup = new AppCacheGroup { AppName = "微信", Icon = "💬" };
            groups.Add(wechatGroup);
            if (Directory.Exists(wechatRoot))
            {
                foreach (var acctDir in SafeEnumerateDirs(wechatRoot))
                {
                    string acct = Path.GetFileName(acctDir);
                    // 跳过 WeChat Files 下的非账号目录（小程序框架、公共数据等）
                    if (IsWeChatNonAccountDir(acct))
                        continue;
                    // 额外保险：账号目录通常包含 FileStorage 或 Msg
                    if (!Directory.Exists(Path.Combine(acctDir, "FileStorage")) &&
                        !Directory.Exists(Path.Combine(acctDir, "Msg")))
                        continue;

                    foreach (var cat in WeChatCategories)
                    {
                        var full = Path.Combine(acctDir, cat.RelativeDir);
                        if (Directory.Exists(full))
                            wechatGroup.Targets.Add(MakeTarget($"{cat.Name}（{acct}）", cat.Description, full, cat.DefaultSelected, cat.Category));
                    }
                }
            }

            // QQ：旧版/TIM + NT 缓存统一归入“QQ”分组；即使未探测到也显示分组（空状态提示）
            var qqGroup = new AppCacheGroup { AppName = "QQ", Icon = "🐧" };
            groups.Add(qqGroup);

            // QQ 旧版 / TIM：Tencent Files\<qq>\
            var qqRoot = Path.Combine(docs, "Tencent Files");
            if (Directory.Exists(qqRoot))
            {
                foreach (var acctDir in SafeEnumerateDirs(qqRoot))
                {
                    string acct = Path.GetFileName(acctDir);
                    foreach (var cat in QqCategories)
                    {
                        var full = Path.Combine(acctDir, cat.RelativeDir);
                        if (Directory.Exists(full))
                            qqGroup.Targets.Add(MakeTarget($"{cat.Name}（{acct}）", cat.Description, full, cat.DefaultSelected, cat.Category));
                    }
                }
            }

            // QQ NT 固定缓存目录：%LOCALAPPDATA%\Tencent\QQ\
            var qqNtRoot = Path.Combine(localApp, "Tencent", "QQ");
            if (Directory.Exists(qqNtRoot))
            {
                foreach (var cat in QqNtCategories)
                {
                    var full = Path.Combine(qqNtRoot, cat.RelativeDir);
                    if (Directory.Exists(full))
                        qqGroup.Targets.Add(MakeTarget(cat.Name, cat.Description, full, cat.DefaultSelected, cat.Category));
                }
            }

            return groups;
        }

        public Task ScanAsync(List<CleanTarget> targets, CancellationToken ct = default)
            => _inner.ScanAsync(targets, ct);

        public Task<(long freedBytes, int recycled, int directDeleted, int quarantined, int failedCount)> CleanAsync(
            List<CleanTarget> targets, bool useRecycleBin, CancellationToken ct = default)
            => _inner.CleanAsync(targets, useRecycleBin, ct);

        private static CleanTarget MakeTarget(string name, string desc, string path, bool selected, string category)
        {
            return new CleanTarget
            {
                Name = name,
                Description = desc,
                Category = category,
                Paths = { path },
                IsSelected = selected,
                IsSystemSafe = false, // 应用用户数据：默认进保险箱软删除，可恢复
                Icon = ""
            };
        }

        private static IEnumerable<string> SafeEnumerateDirs(string root)
        {
            try { return Directory.EnumerateDirectories(root); }
            catch (IOException) { return Enumerable.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        /// <summary>
        /// WeChat Files 根目录下存在一些非账号目录（如 WMPF 小程序框架、All Users 公共数据），
        /// 这些目录不应被当作微信号目录遍历。
        /// </summary>
        private static bool IsWeChatNonAccountDir(string name)
        {
            var nonAccount = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "All Users",    // 公共配置
                "WMPF",         // WeChat Mini Program Framework
                "xlog",         // 日志目录
                "Applet",       // 旧版小程序根目录（若存在）
                "CrashDump",    // 崩溃转储
                "HDImage"       // 高清图片公共缓存
            };
            return nonAccount.Contains(name);
        }
    }
}
