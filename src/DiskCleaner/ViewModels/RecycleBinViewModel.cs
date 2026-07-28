using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public enum RecycleCategoryMode { Type, Location, Time, Size, Source }

    public class CategoryOption
    {
        public string Label { get; set; }
        public RecycleCategoryMode Mode { get; set; }
    }

    public class RecycleBinViewModel : ViewModelBase
    {
        private readonly RecycleBinManager _manager = new RecycleBinManager();
        private RecycleBinInfo _cDriveInfo;
        private ObservableCollection<RecycleBinDriveOption> _drives;
        private RecycleBinDriveOption _selectedDrive;
        private bool _isBusy;
        private string _resultMessage;
        private int _progress;
        private string _progressText;
        private ObservableCollection<RecycleBinItem> _items = new ObservableCollection<RecycleBinItem>();
        private bool _selectAll;
        private RecycleCategoryMode _categoryMode = RecycleCategoryMode.Type;
        private ICollectionView _itemsView;
        private string _sortColumn = nameof(RecycleBinItem.DeletedAtUtc);
        private string _sortHeader = "删除时间";
        private ListSortDirection _sortDirection = ListSortDirection.Descending;

        /// <summary>当前排序列的中文名（用于表头三角指示）</summary>
        public string CurrentSortColumnHeader => _sortHeader;
        /// <summary>当前排序方向</summary>
        public ListSortDirection CurrentSortDirection => _sortDirection;

        public RecycleBinInfo CDriveInfo
        {
            get => _cDriveInfo;
            set
            {
                Set(ref _cDriveInfo, value);
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(ItemDisplay));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public string SizeDisplay => CDriveInfo?.SizeDisplay ?? "0 B";
        public string ItemDisplay => CDriveInfo?.ItemDisplay ?? "空";
        public bool IsEmpty => CDriveInfo?.IsEmpty ?? true;

        public ObservableCollection<RecycleBinItem> Items
        {
            get => _items;
            set
            {
                Set(ref _items, value);
                OnPropertyChanged(nameof(ListCount));
                OnPropertyChanged(nameof(HasItems));
            }
        }

        /// <summary>分组后的视图（列表按当前分类维度分组展示）</summary>
        public ICollectionView ItemsView
        {
            get => _itemsView;
            set => Set(ref _itemsView, value);
        }

        /// <summary>当前分类维度</summary>
        public RecycleCategoryMode CategoryMode
        {
            get => _categoryMode;
            set
            {
            if (Set(ref _categoryMode, value))
            {
                RefreshView();
                OnPropertyChanged(nameof(CategoryModeSummary));
            }
            }
        }

        public List<CategoryOption> CategoryOptions { get; } = new List<CategoryOption>
        {
            new CategoryOption { Label = "按文件类型", Mode = RecycleCategoryMode.Type },
            new CategoryOption { Label = "按原位置", Mode = RecycleCategoryMode.Location },
            new CategoryOption { Label = "按删除时间", Mode = RecycleCategoryMode.Time },
            new CategoryOption { Label = "按文件大小", Mode = RecycleCategoryMode.Size },
            new CategoryOption { Label = "按清理来源", Mode = RecycleCategoryMode.Source },
        };

        public string CategoryModeSummary => CategoryMode switch
        {
            RecycleCategoryMode.Type => "已按文件类型分组",
            RecycleCategoryMode.Location => "已按原位置分组",
            RecycleCategoryMode.Time => "已按删除时间分组",
            RecycleCategoryMode.Size => "已按文件大小分组",
            RecycleCategoryMode.Source => "已按清理来源分组",
            _ => ""
        };

        public int ListCount => _items?.Count ?? 0;
        public bool HasItems => ListCount > 0;

        public bool SelectAll
        {
            get => _selectAll;
            set
            {
                if (Set(ref _selectAll, value))
                    foreach (var it in _items) it.IsSelected = value;
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        public int Progress
        {
            get => _progress;
            set => Set(ref _progress, value);
        }

        public string ProgressText
        {
            get => _progressText;
            set => Set(ref _progressText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand EmptyCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand RestoreAllCommand { get; }

        public RecycleBinViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
            EmptyCommand = new RelayCommand(async () => await EmptyAsync(), () => !IsBusy && !IsEmpty);
            RestoreCommand = new AsyncRelayCommand(async () => await RestoreAsync(false), () => !IsBusy && HasItems);
            RestoreAllCommand = new AsyncRelayCommand(async () => await RestoreAsync(true), () => !IsBusy && HasItems);

            _manager.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct;
                    ProgressText = msg;
                });
            };

            // 下拉选项：默认“全部回收站” + 各固定本地盘
            _drives = new ObservableCollection<RecycleBinDriveOption>
            {
                new RecycleBinDriveOption { Label = "全部回收站", Root = null }
            };
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed))
                {
                    _drives.Add(new RecycleBinDriveOption
                    {
                        Label = $"{d.Name.TrimEnd('\\')} 盘 ({DriveLabel(d)})",
                        Root = d.RootDirectory.FullName
                    });
                }
            }
            catch { /* 枚举磁盘失败不影响启动 */ }

            _selectedDrive = _drives[0];

            Refresh();
        }

        /// <summary>当前选择范围（全部或某盘）的展示名，用于状态卡片标题</summary>
        public string ScopeLabel => SelectedDrive?.IsAll == true
            ? "全部回收站"
            : $"{SelectedDrive.Label} 回收站";

        /// <summary>可选回收站范围（全部 + 各固定盘）</summary>
        public ObservableCollection<RecycleBinDriveOption> Drives
        {
            get => _drives;
            set => Set(ref _drives, value);
        }

        /// <summary>当前选中的回收站范围；改变时自动刷新统计与列表</summary>
        public RecycleBinDriveOption SelectedDrive
        {
            get => _selectedDrive;
            set
            {
                if (Set(ref _selectedDrive, value))
                {
                    OnPropertyChanged(nameof(ScopeLabel));
                    Refresh();
                }
            }
        }

        public void Refresh()
        {
            var root = SelectedDrive?.Root;   // null = 全部
            CDriveInfo = _manager.Query(root);
            LoadItems();
        }

        private static string DriveLabel(DriveInfo d)
        {
            try { return string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel; }
            catch { return "本地磁盘"; }
        }

        private void LoadItems()
        {
            var list = _manager.Enumerate(SelectedDrive?.Root);
            var vms = new ObservableCollection<RecycleBinItem>(list);
            foreach (var it in vms) it.IsSelected = _selectAll;
            Items = vms;

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            RefreshView();
        }

        private void RefreshView()
        {
            if (ItemsView == null) return;
            using (ItemsView.DeferRefresh())
            {
                string groupProp = CategoryMode switch
                {
                    RecycleCategoryMode.Type => nameof(RecycleBinItem.TypeCategory),
                    RecycleCategoryMode.Location => nameof(RecycleBinItem.LocationCategory),
                    RecycleCategoryMode.Time => nameof(RecycleBinItem.TimeCategory),
                    RecycleCategoryMode.Size => nameof(RecycleBinItem.SizeCategory),
                    RecycleCategoryMode.Source => nameof(RecycleBinItem.SourceCategory),
                    _ => nameof(RecycleBinItem.TypeCategory)
                };
                ItemsView.GroupDescriptions.Clear();
                ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(groupProp));
                ItemsView.SortDescriptions.Clear();
                if (!string.IsNullOrEmpty(_sortColumn))
                    ItemsView.SortDescriptions.Add(new SortDescription(_sortColumn, _sortDirection));
            }
        }

        public void ToggleSort(string columnHeader)
        {
            string prop = columnHeader switch
            {
                "大小" => nameof(RecycleBinItem.SizeBytes),
                "删除时间" => nameof(RecycleBinItem.DeletedAtUtc),
                "原路径" => nameof(RecycleBinItem.OriginalPath),
                "清理来源" => nameof(RecycleBinItem.SourceCategory),
                _ => null
            };
            if (prop == null) return;

            if (_sortColumn == prop)
                _sortDirection = _sortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            else
            {
                _sortColumn = prop;
                _sortHeader = columnHeader;
                _sortDirection = ListSortDirection.Ascending;
            }
            RefreshView();
            OnPropertyChanged(nameof(CurrentSortColumnHeader));
            OnPropertyChanged(nameof(CurrentSortDirection));
        }

        private async Task RestoreAsync(bool all)
        {
            var targets = all ? Items.ToList() : Items.Where(x => x.IsSelected).ToList();
            if (targets.Count == 0) { ResultMessage = "没有可恢复的文件"; return; }

            IsBusy = true;
            int ok = 0;
            await Task.Run(() => ok = _manager.RestoreAll(targets));
            ResultMessage = $"已恢复 {ok}/{targets.Count} 个文件";
            IsBusy = false;
            Refresh();
        }

        private async Task EmptyAsync()
        {
            var opt = SelectedDrive;
            if (opt == null) return;

            string scope = opt.IsAll ? "所有盘的回收站" : $"{opt.Label} 回收站";
            var confirm = MessageBox.Show(
                $"即将永久清空{scope}中的 {SizeDisplay} 数据（共 {ItemDisplay}）。\n\n" +
                "此操作不可恢复！\n\n确认继续？",
                "清空回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            Progress = 0;

            try
            {
                bool success = await Task.Run(() => _manager.Empty(opt.Root));

                ResultMessage = success ? "回收站已清空" : "清空失败，可能部分文件被占用";
                Refresh();
            }
            finally
            {
                IsBusy = false;
                Progress = 100;
            }
        }
    }

    /// <summary>回收站清空范围选项：Root 为 null 表示“全部”，否则为具体盘根目录（如 "C:\"）。</summary>
    public class RecycleBinDriveOption
    {
        public string Label { get; set; }
        public string Root { get; set; }
        public bool IsAll => Root == null;
    }
}
