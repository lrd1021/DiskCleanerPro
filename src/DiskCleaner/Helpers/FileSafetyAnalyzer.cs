using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// 文件安全评级
    /// </summary>
    public enum FileSafetyLevel
    {
        Safe,       // 可以安全删除
        Caution,    // 需谨慎，删除可能影响某些功能
        Danger,     // 系统或关键应用文件，不建议删除
        Unknown     // 无法识别
    }

    /// <summary>
    /// 文件安全分析结果
    /// </summary>
    public class FileSafetyInfo
    {
        private static readonly Brush SafeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
        private static readonly Brush CautionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
        private static readonly Brush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        private static readonly Brush UnknownBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)));

        private static Brush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        public FileSafetyLevel Level { get; set; }
        public string Description { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Suggestion { get; set; } = "";

        public string Icon => Level switch
        {
            FileSafetyLevel.Safe => "✅",
            FileSafetyLevel.Caution => "⚠️",
            FileSafetyLevel.Danger => "🚫",
            _ => "❓"
        };

        public Brush IconBrush => Level switch
        {
            FileSafetyLevel.Safe => SafeBrush,
            FileSafetyLevel.Caution => CautionBrush,
            FileSafetyLevel.Danger => DangerBrush,
            _ => UnknownBrush
        };

        public string LevelText => Level switch
        {
            FileSafetyLevel.Safe => "可安全删除",
            FileSafetyLevel.Caution => "建议保留",
            FileSafetyLevel.Danger => "请勿删除",
            _ => "无法识别"
        };
    }

    /// <summary>
    /// 文件安全分析器
    /// 基于文件扩展名、路径上下文、用途数据库来判断文件是否可安全删除
    /// </summary>
    public static class FileSafetyAnalyzer
    {
        // 建议保留的关键路径片段
        private static readonly string[] ProtectedPaths =
        {
            @"\Windows\", @"\Program Files\", @"\Program Files (x86)\",
            @"\ProgramData\Microsoft\", @"\System32\", @"\SysWOW64\",
            @"\drivers\", @"\Boot\",
            @"\dotnet\shared\"           // .NET 共享运行时（PresentationFramework.dll 等），供多个程序共用
        };

        // 明确安全的目录（临时/缓存类）
        private static readonly string[] SafePaths =
        {
            @"\Temp\", @"\tmp\", @"\cache\", @"\Cache\",
            @"\CrashDumps\", @"\Logs\", @"\logs\",
            @"\Download\", @"\Downloads\",
            @"\Microsoft\Windows\WER\", @"\thumbnails\",
            @"\PackageCache\", @"\packages.tuanjie.cn\", @"\Burst\Cache\"
        };

        // 扩展名分类
        private static readonly Dictionary<string, (FileSafetyLevel Level, string Desc)> ExtDatabase = new()
        {
            // —— 系统/驱动 ——
            { ".sys",   (FileSafetyLevel.Danger, "Windows 系统驱动程序文件") },
            { ".dll",   (FileSafetyLevel.Danger, "动态链接库，程序运行依赖") },
            { ".ocx",   (FileSafetyLevel.Danger, "ActiveX 控件，程序组件") },
            { ".dylib", (FileSafetyLevel.Caution, "类 Unix 动态链接库，可能是程序组件") },
            { ".drv",   (FileSafetyLevel.Danger, "设备驱动文件") },
            { ".inf",   (FileSafetyLevel.Danger, "驱动安装信息文件") },
            { ".cat",   (FileSafetyLevel.Danger, "安全编录签名文件") },
            { ".mui",   (FileSafetyLevel.Danger, "语言资源文件") },
            { ".efi",   (FileSafetyLevel.Danger, "EFI/UEFI 固件文件") },

            // —— 程序/配置 ——
            { ".exe",   (FileSafetyLevel.Caution, "可执行程序，删除将无法使用该软件") },
            { ".msi",   (FileSafetyLevel.Caution, "软件安装包，删除后无法卸载/修复") },
            { ".dat",   (FileSafetyLevel.Caution, "程序数据文件，可能是配置或资源") },
            { ".ini",   (FileSafetyLevel.Caution, "配置文件，删除可能导致软件异常") },
            { ".cfg",   (FileSafetyLevel.Caution, "配置文件") },
            { ".conf",  (FileSafetyLevel.Caution, "配置文件") },
            { ".json",  (FileSafetyLevel.Caution, "JSON数据文件，可能是应用配置") },
            { ".xml",   (FileSafetyLevel.Caution, "XML数据文件，可能是应用配置") },
            { ".db",    (FileSafetyLevel.Caution, "数据库文件，包含用户数据") },
            { ".sqlite",(FileSafetyLevel.Caution, "SQLite 数据库文件") },

            // —— 日志/临时（安全删除） ——
            { ".log",   (FileSafetyLevel.Safe, "日志文件，记录程序运行信息") },
            { ".tmp",   (FileSafetyLevel.Safe, "临时文件，程序关闭后可安全删除") },
            { ".temp",  (FileSafetyLevel.Safe, "临时文件") },
            { ".dmp",   (FileSafetyLevel.Safe, "崩溃转储文件，调试用途") },
            { ".dump",  (FileSafetyLevel.Safe, "内存转储文件") },
            { ".etl",   (FileSafetyLevel.Safe, "事件跟踪日志") },
            { ".wer",   (FileSafetyLevel.Safe, "Windows 错误报告文件") },

            // —— 缓存 ——
            { ".cache", (FileSafetyLevel.Safe, "缓存文件") },
            { ".pdb",   (FileSafetyLevel.Safe, "程序调试符号文件") },

            // —— 媒体/文档（用户数据） ——
            { ".jpg",   (FileSafetyLevel.Caution, "图片文件，属于用户数据") },
            { ".jpeg",  (FileSafetyLevel.Caution, "图片文件") },
            { ".png",   (FileSafetyLevel.Caution, "图片文件") },
            { ".gif",   (FileSafetyLevel.Caution, "动态图片文件") },
            { ".bmp",   (FileSafetyLevel.Caution, "位图图片") },
            { ".mp4",   (FileSafetyLevel.Caution, "视频文件，属于用户数据") },
            { ".avi",   (FileSafetyLevel.Caution, "视频文件") },
            { ".mkv",   (FileSafetyLevel.Caution, "视频文件") },
            { ".mov",   (FileSafetyLevel.Caution, "视频文件") },
            { ".mp3",   (FileSafetyLevel.Caution, "音频文件") },
            { ".wav",   (FileSafetyLevel.Caution, "音频文件") },
            { ".flac",  (FileSafetyLevel.Caution, "无损音频文件") },
            { ".doc",   (FileSafetyLevel.Caution, "Word 文档") },
            { ".docx",  (FileSafetyLevel.Caution, "Word 文档") },
            { ".xls",   (FileSafetyLevel.Caution, "Excel 表格") },
            { ".xlsx",  (FileSafetyLevel.Caution, "Excel 表格") },
            { ".ppt",   (FileSafetyLevel.Caution, "演示文稿") },
            { ".pptx",  (FileSafetyLevel.Caution, "演示文稿") },
            { ".pdf",   (FileSafetyLevel.Caution, "PDF 文档") },
            { ".zip",   (FileSafetyLevel.Caution, "压缩文件，属于用户数据") },
            { ".rar",   (FileSafetyLevel.Caution, "压缩文件") },
            { ".7z",    (FileSafetyLevel.Caution, "压缩文件") },
            { ".psd",   (FileSafetyLevel.Caution, "Photoshop 设计文件") },
            { ".ai",    (FileSafetyLevel.Caution, "Illustrator 设计文件") },
            { ".svg",   (FileSafetyLevel.Caution, "矢量图形文件") },

            // —— 安全删除 ——
            { ".old",   (FileSafetyLevel.Safe, "旧版本备份文件") },
            { ".bak",   (FileSafetyLevel.Safe, "备份文件，确认不需要后可删除") },
            { ".chk",   (FileSafetyLevel.Safe, "磁盘扫描恢复的碎片文件") },
        };

        // 已知软件/游戏的本地文件模式
        private static readonly (string Pattern, string Description)[] KnownPatterns =
        {
            (@"\Steam\steamapps\", "Steam 游戏平台文件"),
            (@"\Epic Games\", "Epic 游戏平台文件"),
            (@"\Tencent\QQ\", "腾讯 QQ 运行文件"),
            (@"\Tencent\WeChat\", "微信运行文件"),
            (@"\Google\Chrome\User Data\Default\Cache", "Chrome 浏览器缓存（安全删除）"),
            (@"\Microsoft\Edge\User Data\Default\Cache", "Edge 浏览器缓存（安全删除）"),
            (@"\AppData\Local\Temp", "应用程序临时文件（安全删除）"),
            (@"\AppData\Local\Microsoft\Windows\INetCache", "IE 浏览器缓存（安全删除）"),
            (@"\AppData\Roaming\Microsoft\Windows\Recent", "最近文件快捷方式（安全删除）"),
            (@"\pip\", "Python pip 包缓存（可安全清理）"),
            (@"\npm-cache\", "NPM 缓存（可安全清理）"),
            (@"\.gradle\caches\", "Gradle 构建缓存（可安全清理）"),
            (@"\node_modules\", "Node.js 依赖（项目级，删除需谨慎）"),
        };

        /// <summary>分析单个文件的安全性</summary>
        public static FileSafetyInfo Analyze(string filePath)
        {
            var info = new FileSafetyInfo();
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath);

            // 1. 先按文件名特殊规则
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                info.Level = FileSafetyLevel.Safe;
                info.Description = "桌面配置显示文件";
                info.Reason = "系统会自动重建，删除无影响";
                info.Suggestion = "可以安全删除，不影响系统运行";
                return info;
            }

            if (fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
            {
                info.Level = FileSafetyLevel.Safe;
                info.Description = "缩略图缓存数据库";
                info.Reason = "系统会自动重新生成";
                info.Suggestion = "可以安全删除";
                return info;
            }

            // 2. 路径上下文分析 — 保护关键目录
            // 注意：ProtectedPaths 片段形如 "\Windows\"，而 dir 是完整路径 "C:\Windows\..."；
            // 不能用 StartsWith（会漏匹配），改为规范化末尾加 '\' 后 IndexOf 判定。
            foreach (var p in ProtectedPaths)
            {
                if (DirContainsSegment(dir, p))
                {
                    info.Level = FileSafetyLevel.Danger;
                    info.Description = ExtDatabase.TryGetValue(ext, out var e) ? e.Desc : "位于系统关键目录";
                    info.Reason = $"文件位于 {GetShortPath(p)}，删除可能导致系统或软件故障";
                    info.Suggestion = "不建议删除系统目录中的文件";
                    return info;
                }
            }

            // 3. 安全的临时/缓存目录
            foreach (var p in SafePaths)
            {
                if (dir.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    info.Level = FileSafetyLevel.Safe;
                    info.Description = ExtDatabase.TryGetValue(ext, out var e) ? e.Desc : "临时/缓存目录中的文件";
                    info.Reason = $"位于缓存/临时目录";
                    info.Suggestion = "可以安全删除";
                    return info;
                }
            }

            // 4. 已知软件模式匹配
            foreach (var (pattern, desc) in KnownPatterns)
            {
                if (filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var isCache = pattern.Contains("Cache") || pattern.Contains("Temp") || pattern.Contains("cache");
                    info.Level = isCache ? FileSafetyLevel.Safe : FileSafetyLevel.Caution;
                    info.Description = desc;
                    info.Reason = isCache ? "属于缓存数据" : "属于应用数据";
                    info.Suggestion = isCache ? "可以安全删除" : "确认不再使用该软件后可删除";
                    return info;
                }
            }

            // 5. 按扩展名判断
            if (ExtDatabase.TryGetValue(ext, out var entry))
            {
                info.Level = entry.Level;
                info.Description = entry.Desc;
                info.Reason = $"根据文件类型 (.{ext}) 自动判断";
                info.Suggestion = entry.Level switch
                {
                    FileSafetyLevel.Safe => "可以安全删除",
                    FileSafetyLevel.Caution => "请确认您不再需要此文件后再删除",
                    FileSafetyLevel.Danger => "不建议删除，可能影响系统或软件运行",
                    _ => "请谨慎判断"
                };
                return info;
            }

            // 6. GUID/哈希命名的应用缓存（如 Unity/Tuanjie SpriteAtlas 缓存、图集缓存）
            // 文件名形如 A7-24-...-EB-41-00-EC-56-0-512-512，无扩展名，常出现在用户 AppData 或项目 Library 缓存目录
            if (IsGuidCacheFileName(fileName))
            {
                info.Level = FileSafetyLevel.Caution;
                info.Description = "应用缓存文件（GUID/哈希命名）";
                info.Reason = "文件名由 GUID/哈希组成，通常是图集、纹理或资源缓存";
                info.Suggestion = "若对应项目或应用已不使用，可删除；仍在使用时会自动重建";
                return info;
            }

            // 7. 无法识别
            info.Level = FileSafetyLevel.Unknown;
            info.Description = $"未知文件类型 (.{ext})";
            info.Reason = "无法自动识别此文件类型";
            info.Suggestion = "如果您不认识此文件，建议保留或搜索后再决定";
            return info;
        }

        /// <summary>分析文件夹的安全性</summary>
        public static FileSafetyInfo AnalyzeDirectory(string dirPath)
        {
            var name = Path.GetFileName(dirPath);
            if (string.IsNullOrEmpty(name)) name = dirPath;

            // 系统关键目录
            foreach (var p in ProtectedPaths)
            {
                if (DirContainsSegment(dirPath, p))
                {
                    return new FileSafetyInfo
                    {
                        Level = FileSafetyLevel.Danger,
                        Description = "系统关键目录",
                        Reason = $"'{name}' 是 Windows 系统目录",
                        Suggestion = "请勿删除系统目录"
                    };
                }
            }

            // 临时/缓存目录
            foreach (var p in SafePaths)
            {
                if (dirPath.Contains(p, StringComparison.OrdinalIgnoreCase))
                {
                    return new FileSafetyInfo
                    {
                        Level = FileSafetyLevel.Safe,
                        Description = "临时/缓存目录",
                        Reason = $"'{name}' 是临时或缓存目录",
                        Suggestion = "可以安全删除"
                    };
                }
            }

            // 检查已知模式
            foreach (var (pattern, desc) in KnownPatterns)
            {
                if (dirPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var isCache = pattern.Contains("Cache") || pattern.Contains("Temp");
                    return new FileSafetyInfo
                    {
                        Level = isCache ? FileSafetyLevel.Safe : FileSafetyLevel.Caution,
                        Description = desc,
                        Reason = isCache ? "属于缓存数据" : "属于应用数据",
                        Suggestion = isCache ? "可以安全删除" : "确认不再使用该软件后可删除"
                    };
                }
            }

            return new FileSafetyInfo
            {
                Level = FileSafetyLevel.Unknown,
                Description = $"目录：{name}",
                Reason = "无法自动判断安全性，建议先查看目录内容",
                Suggestion = "请在查看内部文件后决定"
            };
        }

        /// <summary>
        /// 识别 GUID/UUID 被拆分为 2 位十六进制段、并以连字符连接的应用缓存文件名。
        /// 典型如 Unity/Tuanjie SpriteAtlas 缓存：A7-24-26-...-EB-41-00-EC-56-0-512-512。
        /// 这类文件无扩展名，靠哈希/GUID 定位资源，删除后应用通常会按需重建。
        /// </summary>
        private static bool IsGuidCacheFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            // 无扩展名（或整个名字被当成一个段）
            if (Path.GetExtension(fileName) != string.Empty) return false;

            // 必须全部由 [0-9A-F]{2} 段和连字符组成；末尾可接 -<数字>-<数字> 的尺寸后缀
            // 至少 10 段 2 位十六进制，避免误伤普通短横线文件名
            return Regex.IsMatch(fileName,
                @"^([0-9A-Fa-f]{2}-){10,}[0-9A-Fa-f]{2}(-\d+-\d+)?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        private static string GetShortPath(string path)
        {
            var parts = path.Trim('\\').Split('\\');
            return parts.Length >= 2 ? $"{parts[parts.Length - 2]}\\{parts.Last()}" : parts.Last();
        }

        /// <summary>
        /// 判断完整目录路径是否包含某个受保护目录片段（如 "\Windows\"）。
        /// ProtectedPaths 片段以反斜杠包裹（"\Windows\"），而传入的 dir 是完整路径 "C:\Windows\..."，
        /// 必须先把 dir 末尾补上反斜杠再做 IndexOf，否则 StartsWith 对带盘符的路径匹配不上。
        /// </summary>
        private static bool DirContainsSegment(string dirPath, string segment)
        {
            if (string.IsNullOrEmpty(dirPath)) return false;
            var norm = dirPath.EndsWith("\\") ? dirPath : dirPath + "\\";
            return norm.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
