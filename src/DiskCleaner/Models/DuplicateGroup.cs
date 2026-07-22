using System.Collections.ObjectModel;
using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 一组重复文件
    /// </summary>
    public class DuplicateGroup : ViewModelBase
    {
        private bool _isSelected;
        private long _wasteBytes;

        public string Hash { get; set; }
        public long FileSize { get; set; }
        public ObservableCollection<DuplicateFile> Files { get; set; } = new ObservableCollection<DuplicateFile>();

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        /// <summary>可释放的空间 = (文件数-1) * 单个文件大小</summary>
        public long WasteBytes
        {
            get => _wasteBytes;
            set
            {
                Set(ref _wasteBytes, value);
                OnPropertyChanged(nameof(WasteDisplay));
            }
        }

        public string WasteDisplay => FileSizeFormatter.Format(WasteBytes);
        public string SizeDisplay => FileSizeFormatter.Format(FileSize);
        public int Count => Files.Count;
    }

    public class DuplicateFile : ViewModelBase
    {
        private bool _keepThis;

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public string LastModified { get; set; }

        /// <summary>是否保留此文件（不删除）。默认保留第一个</summary>
        public bool KeepThis
        {
            get => _keepThis;
            set => Set(ref _keepThis, value);
        }

        public string SafetyIcon => string.IsNullOrEmpty(FilePath)
            ? "" : FileSafetyAnalyzer.Analyze(FilePath).Icon;

        public string SafetyTooltip => string.IsNullOrEmpty(FilePath)
            ? "" : FileSafetyAnalyzer.Analyze(FilePath).Description;
    }
}
