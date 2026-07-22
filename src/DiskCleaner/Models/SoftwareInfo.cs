using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 已安装软件信息
    /// </summary>
    public class SoftwareInfo : ViewModelBase
    {
        private bool _isSelected;

        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallDate { get; set; }
        public string InstallLocation { get; set; }
        public long EstimatedSizeKB { get; set; }
        public string UninstallString { get; set; }
        public bool IsSystemComponent { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string SizeDisplay => EstimatedSizeKB > 0
            ? FileSizeFormatter.Format(EstimatedSizeKB * 1024)
            : "未知";

        public string DisplayName => string.IsNullOrEmpty(Publisher)
            ? Name
            : $"{Name}  —  {Publisher}";
    }
}
