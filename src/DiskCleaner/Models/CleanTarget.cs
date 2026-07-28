using System.Collections.Generic;
using System.Collections.ObjectModel;
using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 可清理的项目（临时文件/缓存等）
    /// </summary>
    public class CleanTarget : ViewModelBase
    {
        private bool _isSelected;
        private long _sizeBytes;
        private long _fileCount;
        private bool _isScanning;
        private string _status;

        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }

        /// <summary>该类别包含的目录路径列表</summary>
        public ObservableCollection<string> Paths { get; set; } = new ObservableCollection<string>();

        /// <summary>
        /// 是否系统级垃圾位（如系统临时文件/更新缓存/错误报告/预取/字体/缩略图/DNS）。
        /// true=可直接永久删除且安全（cleanmgr 也清这些）；false=用户空间数据（如用户临时文件），
        /// 删除时默认移入保险箱软删除以便恢复。
        /// </summary>
        public bool IsSystemSafe { get; set; }

        /// <summary>
        /// 扫描时缓存的文件列表（路径+大小），清理时直接使用，避免二次枚举。
        /// 键为文件完整路径，值为扫描时统计的大小。
        /// </summary>
        public List<(string FullName, long Size)> ScannedFiles { get; set; } = new List<(string, long)>();

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public long SizeBytes
        {
            get => _sizeBytes;
            set
            {
                Set(ref _sizeBytes, value);
                OnPropertyChanged(nameof(SizeDisplay));
            }
        }

        public long FileCount
        {
            get => _fileCount;
            set => Set(ref _fileCount, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => Set(ref _isScanning, value);
        }

        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);
    }
}
