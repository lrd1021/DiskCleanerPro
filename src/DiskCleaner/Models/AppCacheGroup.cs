using System.Collections.ObjectModel;
using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 应用专清的分组容器：一个应用（微信 / QQ）含多个可清理类别（CleanTarget）。
    /// </summary>
    public class AppCacheGroup : ViewModelBase
    {
        public string AppName { get; set; }
        public string Icon { get; set; }
        public ObservableCollection<CleanTarget> Targets { get; set; } = new ObservableCollection<CleanTarget>();
    }
}
