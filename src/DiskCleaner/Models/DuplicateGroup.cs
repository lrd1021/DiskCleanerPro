using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using DiskCleaner.Helpers;

namespace DiskCleaner.Models
{
    /// <summary>
    /// 一组重复文件
    /// </summary>
    public class DuplicateGroup : ViewModelBase
    {
        private bool _isSelected;

        public DuplicateGroup()
        {
            Files.CollectionChanged += OnFilesCollectionChanged;
        }

        public string Hash { get; set; }
        public long FileSize { get; set; }
        public ObservableCollection<DuplicateFile> Files { get; set; } = new ObservableCollection<DuplicateFile>();

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        /// <summary>可释放的空间 = 未勾选「保留」的文件数 * 单个文件大小（随用户选择实时变化）</summary>
        public long WasteBytes => FileSize * Files.Count(f => !f.KeepThis);

        public string WasteDisplay => FileSizeFormatter.Format(WasteBytes);
        public string SizeDisplay => FileSizeFormatter.Format(FileSize);
        public int Count => Files.Count;

        /// <summary>本组是否含有系统/程序必需（Danger）文件。这类文件已被锁定保留、不可删。</summary>
        public bool ContainsCritical => Files.Any(f => f.IsCritical);

        /// <summary>组级警告文案：当含有被锁定保留的关键文件时显示。</summary>
        public string CriticalWarning =>
            ContainsCritical ? "⚠️ 本组含系统/程序必需文件，已自动锁定保留（显示🔒的行不可删除）" : "";

        /// <summary>订阅组内每个文件的 KeepThis 变化，实时刷新 WasteBytes/WasteDisplay。</summary>
        public void HookFileChanges()
        {
            foreach (var f in Files)
                f.PropertyChanged += OnFilePropertyChanged;
        }

        private void OnFilePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateFile.KeepThis))
            {
                OnPropertyChanged(nameof(WasteBytes));
                OnPropertyChanged(nameof(WasteDisplay));
            }
        }

        private void OnFilesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(WasteBytes));
            OnPropertyChanged(nameof(WasteDisplay));
            OnPropertyChanged(nameof(Count));
        }
    }

    public class DuplicateFile : ViewModelBase
    {
        private bool _keepThis;

        /// <summary>所属重复组的标识（通常用哈希），曾用于同一组内 RadioButton 分组；现控件已改为 CheckBox，保留字段无运行时用途。</summary>
        public string GroupKey { get; set; }

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public string Directory { get; set; }
        public string LastModified { get; set; }

        private FileSafetyInfo _safety;
        private FileSafetyInfo Safety => _safety ??= FileSafetyAnalyzer.Analyze(FilePath);

        /// <summary>安全评级（Safe/Caution/Danger/Unknown）。</summary>
        public FileSafetyLevel SafetyLevel => Safety.Level;

        /// <summary>是否系统/程序必需文件（Danger 级）。这类文件即使被用户取消「保留」也强制保留，不可删除。</summary>
        public bool IsCritical => SafetyLevel == FileSafetyLevel.Danger;

        /// <summary>该文件是否锁定保留（= IsCritical）。UI 据此禁用「保留」复选框并提示。</summary>
        public bool KeepThisLocked => IsCritical;

        public string SafetyLevelText => Safety.LevelText;
        public string SafetySuggestion => Safety.Suggestion;

        /// <summary>是否保留此文件（不删除）。默认保留第一个；支持同组保留多个。
        /// 若是关键文件（IsCritical），强制为 true，拒绝取消——避免误删系统/程序必需文件。</summary>
        public bool KeepThis
        {
            get => _keepThis;
            set
            {
                if (IsCritical && !value) return; // 关键文件：拒绝取消保留
                Set(ref _keepThis, value);
            }
        }

        public string SafetyIcon => string.IsNullOrEmpty(FilePath)
            ? "" : Safety.Icon;

        public System.Windows.Media.Brush SafetyIconBrush => string.IsNullOrEmpty(FilePath)
            ? null : Safety.IconBrush;

        public string SafetyTooltip => string.IsNullOrEmpty(FilePath)
            ? "" : Safety.Description + "\n" + Safety.Reason + "\n建议：" + Safety.Suggestion;
    }
}
