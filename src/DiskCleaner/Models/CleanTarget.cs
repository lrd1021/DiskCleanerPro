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
        private int _fileCount;
        private bool _isScanning;
        private string _status;

        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }

        /// <summary>该类别包含的目录路径列表</summary>
        public ObservableCollection<string> Paths { get; set; } = new ObservableCollection<string>();

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

        public int FileCount
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
