using System.Collections.ObjectModel;
using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 磁盘空间分析用的文件/文件夹节点
    /// </summary>
    public class FileNode : ViewModelBase
    {
        private long _sizeBytes;
        private bool _isExpanded;

        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public ObservableCollection<FileNode> Children { get; set; } = new ObservableCollection<FileNode>();

        public long SizeBytes
        {
            get => _sizeBytes;
            set
            {
                Set(ref _sizeBytes, value);
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(PercentageDisplay));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(SizeBytes);

        /// <summary>占父目录的百分比（需外部计算后设置）</summary>
        public double Percentage { get; set; }
        public string PercentageDisplay => Percentage > 0 ? $"{Percentage:F1}%" : "";

        /// <summary>最后修改时间</summary>
        public string LastModified { get; set; }

        /// <summary>文件扩展名（仅文件有）</summary>
        public string Extension { get; set; }

        /// <summary>安全评级</summary>
        public string SafetyIcon => string.IsNullOrEmpty(FullPath)
            ? "" : FileSafetyAnalyzer.Analyze(FullPath).Icon;

        /// <summary>安全评级提示</summary>
        public string SafetyTooltip => string.IsNullOrEmpty(FullPath)
            ? "" : GetSafetyTooltip();

        private string GetSafetyTooltip()
        {
            var si = FileSafetyAnalyzer.Analyze(FullPath);
            return $"{si.LevelText}\n{si.Description}\n{si.Suggestion}";
        }
    }
}
