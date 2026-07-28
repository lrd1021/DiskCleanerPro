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

        /// <summary>本组是否含有经 AI 判定为危险（Danger）的文件（含 AI 覆盖本地判定的情况）。</summary>
        public bool ContainsAiDanger => Files.Any(f => f.HasAiSafety && f.IsCritical);

        /// <summary>组级警告文案：当含有被锁定保留的关键文件时显示。</summary>
        public string CriticalWarning =>
            ContainsCritical ? "⚠️ 本组含系统/程序必需文件，已自动锁定保留（显示🔒的行不可删除）" : "";

        /// <summary>组级 AI 危险警告：AI 将本组某些文件判为危险时显示（区别于本地系统关键文件)。</summary>
        public string AiDangerWarning =>
            ContainsAiDanger ? "⚠️ AI 判定本组含危险文件，已自动锁定保留（显示🔒的行不可删除）" : "";

        /// <summary>订阅组内每个文件的 KeepThis 变化，实时刷新 WasteBytes/WasteDisplay。</summary>
        public void HookFileChanges()
        {
            foreach (var f in Files)
                f.PropertyChanged += OnFilePropertyChanged;
        }

        private void OnFilePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicateFile.KeepThis) ||
                e.PropertyName == nameof(DuplicateFile.IsCritical))
            {
                OnPropertyChanged(nameof(WasteBytes));
                OnPropertyChanged(nameof(WasteDisplay));
                OnPropertyChanged(nameof(ContainsCritical));
                OnPropertyChanged(nameof(CriticalWarning));
                OnPropertyChanged(nameof(ContainsAiDanger));
                OnPropertyChanged(nameof(AiDangerWarning));
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
        private FileSafetyInfo _aiSafety;

        /// <summary>安全信息：AI 已分析时优先返回 AI 结果，否则回退本地规则缓存。</summary>
        private FileSafetyInfo Safety => _aiSafety ?? (_safety ??= FileSafetyAnalyzer.Analyze(FilePath));

        /// <summary>安全评级（Safe/Caution/Danger/Unknown）。</summary>
        public FileSafetyLevel SafetyLevel => Safety.Level;

        /// <summary>是否系统/程序必需文件（Danger 级）。这类文件即使被用户取消「保留」也强制保留，不可删除。</summary>
        public bool IsCritical => SafetyLevel == FileSafetyLevel.Danger;

        /// <summary>该文件是否锁定保留（= IsCritical）。UI 据此禁用「保留」复选框并提示。</summary>
        public bool KeepThisLocked => IsCritical;

        /// <summary>是否已用 AI 分析覆盖本地安全判定（用于 UI 区分来源）。</summary>
        public bool HasAiSafety => _aiSafety != null;

        /// <summary>AI 分析结果短句（仅 AI 已分析时非空），如「AI：Chrome 缓存｜可安全删除」。
        /// 当前主要用于 Tooltip/详情；行内改用小徽章 AiBadgeText。</summary>
        public string AiAnalysisText => _aiSafety == null
            ? ""
            : $"AI：{_aiSafety.Description}｜{_aiSafety.Suggestion}";

        /// <summary>AI 判定等级简短标签（行内小徽章），如「AI·安全」「AI·谨慎」「AI·危险」。</summary>
        public string AiBadgeText => _aiSafety == null
            ? ""
            : $"AI·{Safety.LevelText}";

        /// <summary>AI 分析完整 Tooltip：描述、归属/原因、建议。</summary>
        public string AiAnalysisToolTip => _aiSafety == null
            ? ""
            : $"AI 分析：{_aiSafety.Description}\n{_aiSafety.Reason}\n建议：{_aiSafety.Suggestion}";

        /// <summary>AI 徽章颜色，与当前安全图标颜色一致。</summary>
        public System.Windows.Media.Brush AiBadgeBrush => SafetyIconBrush;

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

        /// <summary>将 AI 分析结果写回，覆盖本地安全判定（优先级更高）。
        /// AI 判为 Danger 时本文件会变红并被锁定保留；判 Safe 时图标变绿。
        /// 防御性规则：AI 返回 Unknown 不提供有效信息，一律不写入 AI 覆盖，
        /// 避免满屏“AI·无法识别”却无实际帮助；保留本地结论。</summary>
        public void ApplyAiSafety(FileSafetyLevel level, string description, string belongsTo, string suggestion)
        {
            // AI 返回 Unknown 没有信息量，不应生成“AI·无法识别”徽章；保持本地结论即可
            if (level == FileSafetyLevel.Unknown)
                return;

            _aiSafety = new FileSafetyInfo
            {
                Level = level,
                Description = description ?? "",
                Reason = string.IsNullOrWhiteSpace(belongsTo) ? "AI 分析判定" : $"AI 分析：属于 {belongsTo}",
                Suggestion = suggestion ?? ""
            };
            foreach (var p in new[]
            {
                nameof(SafetyIcon), nameof(SafetyIconBrush), nameof(SafetyTooltip),
                nameof(SafetyLevel), nameof(SafetyLevelText), nameof(SafetySuggestion),
                nameof(IsCritical), nameof(KeepThisLocked), nameof(HasAiSafety),
                nameof(AiAnalysisText), nameof(AiBadgeText), nameof(AiAnalysisToolTip), nameof(AiBadgeBrush)
            })
            {
                RaisePropertyChanged(p);
            }
        }
    }
}
