using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DiskCleaner.Helpers;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 保险箱（软删除）服务：把"永久删除"改为先移入专用目录，可随时恢复，且
    /// 不触发 Windows Shell 通知（不会黑屏/拖慢资源管理器）。
    ///
    /// 设计要点：
    /// 1. 同卷下 File.Move 是纯 rename（元数据操作），速度快、原子、可恢复；跨卷时回退 copy+delete。
    /// 2. 不写逐文件 .dqmeta 侧卡：目标路径即 `Quarantine\盘符\原相对路径` 镜像，恢复时由路径反推
    ///    原位置，移入时间用 CreationTimeUtc。这样 9 万文件省掉 9 万次随机小文件写入，速度逼近直删。
    ///    （旧版本曾写 .dqmeta；ReadOriginal/ReadTime 仍兼容读取残留侧卡。）
    /// 3. 软删仍失败时 best-effort 回退为 File.Delete（保证清理能完成），此时返回 false 表示不可恢复。
    /// 4. 目录结构按 卷字母\剩余相对路径 镜像，恢复时原样还原；同名冲突自动加序号。
    /// </summary>
    public static class QuarantineService
    {
        public static string RootPath { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskCleaner", "Quarantine");

        /// <summary>单个保险箱条目</summary>
        public class QuarantineItem
        {
            public string QuarantinePath { get; set; }
            public string OriginalPath { get; set; }
            public long Size { get; set; }
            public DateTime QuarantinedAt { get; set; }
        }

        private static string MetaPathFor(string qPath) => qPath + ".dqmeta";

        /// <summary>
        /// 将文件移入保险箱。返回 true=已移入（可恢复）；false=已永久删除或失败。
        /// out freedBytes 为文件大小（用于"已释放"统计）。
        /// 性能：仅做一次 File.Move（同卷 rename），不写任何侧卡文件。
        /// </summary>
        public static bool MoveToQuarantine(string source, out long freedBytes)
            => MoveToQuarantine(source, out freedBytes, true);

        /// <summary>
        /// 将文件移入保险箱（可控制是否自动创建目标目录）。
        /// 批量清理场景已由调用方预建目录树，将 createDirectory 设为 false 可省掉每文件一次的 CreateDirectory 开销。
        /// </summary>
        public static bool MoveToQuarantine(string source, out long freedBytes, bool createDirectory)
        {
            freedBytes = 0;
            try
            {
                if (!File.Exists(source)) return false;
                freedBytes = new FileInfo(source).Length;

                var dest = MapToQuarantinePath(source);
                if (createDirectory)
                    Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
                dest = EnsureUnique(dest);

                return TryMove(source, dest);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Warning($"移入保险箱失败 [{source}]: {ex.Message}");
                // best-effort 永久删除，保证清理能完成（此时不可恢复）
                try
                {
                    if (File.Exists(source)) { File.Delete(source); return false; }
                }
                catch { }
                return false;
            }
        }

        /// <summary>计算某源文件在保险箱中的目标路径（供调用方批量预建目录树，减少每文件 CreateDirectory 开销）。</summary>
        public static string MapToQuarantinePath(string source)
        {
            var root = Path.GetPathRoot(source) ?? "";
            var relative = source.Substring(root.Length).TrimStart('\\', '/');
            var drive = root.Replace(":", "").Replace("\\", "").Replace("/", "");
            if (string.IsNullOrEmpty(drive)) drive = "X";
            return Path.Combine(RootPath, drive, relative);
        }

        private static bool TryMove(string source, string dest)
        {
            try
            {
                File.Move(source, dest);
                return true;
            }
            catch (IOException) when (!SameVolume(source, dest))
            {
                // 跨卷 Move 不被支持 → 复制后删除原文件
                try
                {
                    File.Copy(source, dest, false);
                    File.Delete(source);
                    return true;
                }
                catch { return false; }
            }
            catch { return false; }
        }

        private static string EnsureUnique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            int i = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}.{i}{ext}");
                i++;
            } while (File.Exists(candidate));
            return candidate;
        }

        private static bool SameVolume(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static List<QuarantineItem> List()
        {
            var items = new List<QuarantineItem>();
            if (!Directory.Exists(RootPath)) return items;
            foreach (var file in EnumerateQuarantineFiles(RootPath))
            {
                items.Add(new QuarantineItem
                {
                    QuarantinePath = file,
                    OriginalPath = ReadOriginal(file),
                    Size = SafeSize(file),
                    QuarantinedAt = ReadTime(file)
                });
            }
            return items;
        }

        private static List<string> EnumerateQuarantineFiles(string root)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(cur))
                    {
                        if (entry.EndsWith(".dqmeta", StringComparison.OrdinalIgnoreCase)) continue;
                        FileAttributes attr;
                        try { attr = File.GetAttributes(entry); }
                        catch { continue; }
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue; // 不跟随重解析点
                        if ((attr & FileAttributes.Directory) != 0) stack.Push(entry);
                        else result.Add(entry);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }

        private static string ReadOriginal(string qPath)
        {
            try
            {
                var meta = MetaPathFor(qPath);
                if (File.Exists(meta))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    if (doc.RootElement.TryGetProperty("orig", out var v))
                        return v.GetString() ?? UnmapOriginalPath(qPath);
                }
            }
            catch { }
            return UnmapOriginalPath(qPath);
        }

        private static DateTime ReadTime(string qPath)
        {
            try
            {
                var meta = MetaPathFor(qPath);
                if (File.Exists(meta))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    if (doc.RootElement.TryGetProperty("t", out var v))
                        return v.GetDateTime();
                }
            }
            catch { }
            try { return File.GetCreationTimeUtc(qPath); }
            catch { return DateTime.UtcNow; }
        }

        private static string UnmapOriginalPath(string qPath)
        {
            var rel = qPath.Substring(RootPath.Length).TrimStart('\\', '/');
            var idx = rel.IndexOf('\\');
            if (idx < 0) return qPath;
            var drive = rel.Substring(0, idx);
            var rest = rel.Substring(idx + 1);
            return drive + ":\\" + rest;
        }

        private static long SafeSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        /// <summary>恢复单个文件到原路径；原位置已存在则加 .restoredN 后缀避免覆盖。</summary>
        public static bool Restore(string qPath)
        {
            try
            {
                if (!File.Exists(qPath)) return false;
                var orig = ReadOriginal(qPath);
                Directory.CreateDirectory(Path.GetDirectoryName(orig) ?? ".");
                var dest = orig;
                if (File.Exists(dest))
                {
                    var dir = Path.GetDirectoryName(dest);
                    var name = Path.GetFileNameWithoutExtension(dest);
                    var ext = Path.GetExtension(dest);
                    int i = 1;
                    string cand;
                    do { cand = Path.Combine(dir, $"{name}.restored{i}{ext}"); i++; }
                    while (File.Exists(cand));
                    dest = cand;
                }
                File.Move(qPath, dest);
                try { File.Delete(MetaPathFor(qPath)); } catch { }
                return true;
            }
            catch { return false; }
        }

        public static void PurgeFile(string qPath)
        {
            try { File.Delete(qPath); } catch { }
            try { File.Delete(MetaPathFor(qPath)); } catch { }
        }

        /// <summary>清理超过指定时长的保险箱文件（自动留存策略）。</summary>
        public static (int count, long bytes) PurgeOlderThan(TimeSpan maxAge)
        {
            int count = 0;
            long bytes = 0;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var it in List())
            {
                if (it.QuarantinedAt < cutoff)
                {
                    PurgeFile(it.QuarantinePath);
                    count++;
                    bytes += it.Size;
                }
            }
            return (count, bytes);
        }

        /// <summary>彻底清空保险箱（不可恢复）。</summary>
        public static (int count, long bytes) PurgeAll()
        {
            int count = 0;
            long bytes = 0;
            foreach (var it in List())
            {
                PurgeFile(it.QuarantinePath);
                count++;
                bytes += it.Size;
            }
            return (count, bytes);
        }

        /// <summary>保险箱当前占用（用于状态展示）。</summary>
        public static (int count, long bytes) Stats()
        {
            int count = 0;
            long bytes = 0;
            foreach (var it in List())
            {
                count++;
                bytes += it.Size;
            }
            return (count, bytes);
        }
    }
}
