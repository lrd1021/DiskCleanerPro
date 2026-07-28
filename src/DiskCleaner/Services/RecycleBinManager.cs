using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using DiskCleaner.Helpers;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 回收站管理服务 — 使用 Windows Shell API 查询/清空回收站，并枚举/恢复其中的文件。
    /// 枚举与恢复基于 $Recycle.Bin\{SID}\$I* 索引文件解析（Windows Vista+ 格式），
    /// 不依赖 Shell 属性键，跨版本稳定。
    /// </summary>
    public class RecycleBinManager
    {
        public Action<int, string> OnProgress { get; set; }
        public static RecycleBinSourceTracker SourceTracker { get; } = new RecycleBinSourceTracker();

        /// <summary>查询回收站信息（总量，不含明细）</summary>
        public RecycleBinInfo Query(string drive = null)
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
            };
            int result = NativeMethods.SHQueryRecycleBin(drive, ref info);

            if (result == 0)
            {
                return new RecycleBinInfo
                {
                    SizeBytes = info.i64Size,
                    ItemCount = info.i64NumItems,
                    IsEmpty = info.i64NumItems == 0
                };
            }
            return new RecycleBinInfo { SizeBytes = 0, ItemCount = 0, IsEmpty = true };
        }

        /// <summary>清空回收站</summary>
        public bool Empty(string drive = null, bool silent = true)
        {
            OnProgress?.Invoke(0, "正在清空回收站...");
            uint flags = NativeMethods.SHERB_NOCONFIRMATION;
            if (silent) flags |= NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND;

            int result = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, drive, flags);
            OnProgress?.Invoke(100, result == 0 ? "回收站已清空" : $"清空失败 (错误码: {result})");
            return result == 0;
        }

        /// <summary>
        /// 枚举当前用户在各固定盘回收站中的文件。返回明细列表（原路径/大小/删除时间/数据路径）。
        /// 解析失败时跳过单个文件而非整体失败。
        /// </summary>
        /// <param name="drive">指定盘根目录（如 "C:\"）；传 null 枚举所有固定盘。</param>
        public List<RecycleBinItem> Enumerate(string drive = null)
        {
            var result = new List<RecycleBinItem>();
            string sid = GetCurrentSid();
            if (string.IsNullOrEmpty(sid)) return result;

            IEnumerable<DriveInfo> drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed);
            if (!string.IsNullOrEmpty(drive))
            {
                string root = drive.EndsWith("\\", StringComparison.Ordinal) ? drive : drive + "\\";
                drives = drives.Where(d => string.Equals(d.RootDirectory.FullName, root, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var d in drives)
            {
                string userBin = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(userBin)) continue;
                try
                {
                    foreach (var idx in Directory.GetFiles(userBin, "$I*"))
                    {
                        var item = ParseIndexFile(idx);
                        if (item != null)
                        {
                            item.Source = SourceTracker.GetSource(item.OriginalPath);
                            result.Add(item);
                        }
                    }
                    SourceTracker.KeepOnly(result.Select(i => i.OriginalPath));
                }
                catch { /* 某个 SID 文件夹无权限则跳过 */ }
            }
            return result;
        }

        /// <summary>
        /// 解析回收站 $I 索引文件。返回 null 表示解析失败。
        /// 格式：
        ///   版本1 (Win10 1809 前)：8字节头 + 8字节删除时间(FILETIME) + 8字节大小 + UTF-16 原路径(偏移24)
        ///   版本2 (Win10 1809+/Win11)：8字节头 + 8字节删除时间 + 8字节大小 + 4字节属性 + UTF-16 原路径(偏移28)
        /// </summary>
        public static RecycleBinItem ParseIndexFile(string indexPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(indexPath);
                if (bytes.Length < 24) return null;

                long size = BitConverter.ToInt64(bytes, 8);
                long fileTime = BitConverter.ToInt64(bytes, 16);
                int pathOffset = (bytes[0] == 2) ? 28 : 24;
                if (bytes.Length < pathOffset + 2) return null;

                // 路径以 2 字节 null 结尾
                int pathByteLen = bytes.Length - pathOffset;
                if (pathByteLen >= 2 && bytes[bytes.Length - 2] == 0 && bytes[bytes.Length - 1] == 0)
                    pathByteLen -= 2;
                string originalPath = Encoding.Unicode.GetString(bytes, pathOffset, pathByteLen);
                if (string.IsNullOrEmpty(originalPath)) return null;

                string fileName = Path.GetFileName(indexPath);
                string dataFile = "$R" + fileName.Substring(2); // 跳过 "$I" 前缀 -> $Rxxx
                string dataPath = Path.Combine(Path.GetDirectoryName(indexPath), dataFile);
                bool isDir = false;
                try
                {
                    var attrs = File.GetAttributes(dataPath);
                    isDir = attrs.HasFlag(FileAttributes.Directory);
                }
                catch { }

                return new RecycleBinItem
                {
                    DisplayName = Path.GetFileName(originalPath),
                    OriginalPath = originalPath,
                    SizeBytes = size,
                    DeletedAtUtc = DateTime.FromFileTimeUtc(fileTime),
                    DataPath = dataPath,
                    IndexPath = indexPath,
                    IsDirectory = isDir
                };
            }
            catch { return null; }
        }

        /// <summary>将单个回收站文件恢复到原路径（移回 + 删除索引记录）。</summary>
        public bool Restore(RecycleBinItem item)
        {
            if (item == null) return false;
            try
            {
                string dest = item.OriginalPath;
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (item.IsDirectory)
                {
                    if (Directory.Exists(dest)) dest = dest + " (已恢复)";
                    Directory.Move(item.DataPath, dest);
                }
                else
                {
                    if (File.Exists(dest))
                    {
                        string dn = Path.GetDirectoryName(dest);
                        dest = Path.Combine(dn,
                            Path.GetFileNameWithoutExtension(dest) + " (已恢复)" + Path.GetExtension(dest));
                    }
                    File.Move(item.DataPath, dest);
                }

                try { if (File.Exists(item.IndexPath)) File.Delete(item.IndexPath); } catch { }

                Notify(dir);
                Notify(Path.GetDirectoryName(item.DataPath));
                SourceTracker.Remove(item.OriginalPath);
                return true;
            }
            catch { return false; }
        }

        /// <summary>批量恢复。</summary>
        public int RestoreAll(IEnumerable<RecycleBinItem> items)
        {
            int ok = 0;
            foreach (var it in items) if (Restore(it)) ok++;
            return ok;
        }

        private static void Notify(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { NativeMethods.SHChangeNotify(NativeMethods.SHCNE_UPDATEDIR, NativeMethods.SHCNF_PATHW, dir, null); }
            catch { }
        }

        private static string GetCurrentSid()
        {
            try { return WindowsIdentity.GetCurrent().User?.Value ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>回收站中的单个文件明细</summary>
    public class RecycleBinItem
    {
        public string DisplayName { get; set; }
        public string OriginalPath { get; set; }
        public long SizeBytes { get; set; }
        public DateTime DeletedAtUtc { get; set; }
        public string DataPath { get; set; }   // $R 数据文件
        public string IndexPath { get; set; }  // $I 索引文件
        public bool IsDirectory { get; set; }
        public string Source { get; set; }     // 清理来源（DiskCleaner 哪个功能删的）

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => _isSelected = value;
        }

        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
        public string DeletedAtDisplay => DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string LocationDisplay => Path.GetDirectoryName(OriginalPath) ?? "";
        public string SourceCategory => Source ?? "系统/未知";

        // —— 分类维度（从文件自身属性推导，无需额外数据）——
        public string TypeCategory => ClassifyByType();
        public string LocationCategory => ClassifyByLocation();
        public string TimeCategory => ClassifyByTime();
        public string SizeCategory => ClassifyBySize();

        private static readonly HashSet<string> ImageExts = new() { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".svg", ".heic" };
        private static readonly HashSet<string> VideoExts = new() { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
        private static readonly HashSet<string> AudioExts = new() { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a" };
        private static readonly HashSet<string> DocExts = new() { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".csv", ".rtf", ".odt" };
        private static readonly HashSet<string> ArchiveExts = new() { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".iso" };

        private string ClassifyByType()
        {
            if (IsDirectory) return "文件夹";
            string ext = Path.GetExtension(OriginalPath).ToLowerInvariant();
            if (ImageExts.Contains(ext)) return "图片";
            if (VideoExts.Contains(ext)) return "视频";
            if (AudioExts.Contains(ext)) return "音频";
            if (DocExts.Contains(ext)) return "文档";
            if (ArchiveExts.Contains(ext)) return "压缩包";
            if (string.IsNullOrEmpty(ext)) return "无扩展名";
            return "其他";
        }

        private string ClassifyByLocation()
        {
            try
            {
                string root = Path.GetPathRoot(OriginalPath);
                return string.IsNullOrEmpty(root) ? "其他位置" : root.TrimEnd('\\');
            }
            catch { return "其他位置"; }
        }

        private string ClassifyByTime()
        {
            int days = (DateTime.Now.Date - DeletedAtUtc.ToLocalTime().Date).Days;
            if (days <= 0) return "今天";
            if (days <= 7) return "近 7 天";
            if (days <= 30) return "近 30 天";
            return "更早";
        }

        private string ClassifyBySize()
        {
            if (IsDirectory) return "文件夹";
            if (SizeBytes < 1L * 1024 * 1024) return "小文件 (<1 MB)";
            if (SizeBytes < 100L * 1024 * 1024) return "中等 (1–100 MB)";
            return "大文件 (>100 MB)";
        }
    }

    public class RecycleBinInfo
    {
        public long SizeBytes { get; set; }
        public long ItemCount { get; set; }
        public bool IsEmpty { get; set; }
        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
        public string ItemDisplay => ItemCount == 0 ? "空" : $"{ItemCount} 项";
    }
}
