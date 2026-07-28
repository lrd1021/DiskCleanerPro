using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DiskCleaner.Services
{
    /// <summary>
    /// 记录「哪些原始路径是通过 DiskCleaner 的哪个清理功能进入回收站」。
    /// 因为 Windows 回收站 $I 索引不含清理来源，本类在删除时写一份轻量清单。
    /// 清单保存在 %LocalAppData%\DiskCleanerPro\RecycleBinManifest.json。
    /// </summary>
    public class RecycleBinSourceTracker
    {
        private static readonly string ManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanerPro");
        private static readonly string ManifestPath = Path.Combine(ManifestDir, "RecycleBinManifest.json");

        private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public RecycleBinSourceTracker()
        {
            Load();
        }

        public void Record(IEnumerable<string> originalPaths, string source)
        {
            if (string.IsNullOrEmpty(source)) return;
            bool changed = false;
            lock (_lock)
            {
                foreach (var p in originalPaths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!_map.TryGetValue(p, out var cur) || cur != source)
                    {
                        _map[p] = source;
                        changed = true;
                    }
                }
            }
            if (changed) Save();
        }

        public string GetSource(string originalPath)
        {
            lock (_lock)
            {
                if (originalPath != null && _map.TryGetValue(originalPath, out var src))
                    return src;
                return "系统/未知";
            }
        }

        public void Remove(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return;
            bool changed;
            lock (_lock)
            {
                changed = _map.Remove(originalPath);
            }
            if (changed) Save();
        }

        public void Remove(IEnumerable<string> originalPaths)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var p in originalPaths)
                {
                    if (p != null && _map.Remove(p))
                        changed = true;
                }
            }
            if (changed) Save();
        }

        public void KeepOnly(IEnumerable<string> originalPaths)
        {
            var keep = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            lock (_lock)
            {
                var toRemove = _map.Keys.Where(k => !keep.Contains(k)).ToList();
                foreach (var k in toRemove)
                {
                    _map.Remove(k);
                    changed = true;
                }
            }
            if (changed) Save();
        }

        public void Clear()
        {
            bool changed;
            lock (_lock)
            {
                changed = _map.Count > 0;
                _map.Clear();
            }
            if (changed) Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(ManifestPath)) return;
                var json = File.ReadAllText(ManifestPath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        if (!string.IsNullOrEmpty(kv.Key))
                            _map[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(ManifestDir);
                var json = JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ManifestPath, json);
            }
            catch { }
        }
    }
}
