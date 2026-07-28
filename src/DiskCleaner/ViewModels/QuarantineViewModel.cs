using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class QuarantineItemVm : ViewModelBase
    {
        public string QuarantinePath { get; set; }
        public string OriginalPath { get; set; }
        public long Size { get; set; }
        public DateTime QuarantinedAt { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string SizeDisplay => FileSizeFormatter.Format(Size);
        public string TimeDisplay => QuarantinedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public class QuarantineViewModel : ViewModelBase
    {
        private ObservableCollection<QuarantineItemVm> _items = new ObservableCollection<QuarantineItemVm>();
        private bool _isBusy;
        private string _status = "加载中...";
        private long _totalSize;

        public ObservableCollection<QuarantineItemVm> Items
        {
            get => _items;
            set => Set(ref _items, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (Set(ref _isBusy, value))
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        public long TotalSize
        {
            get => _totalSize;
            private set
            {
                if (Set(ref _totalSize, value))
                    OnPropertyChanged(nameof(TotalSizeDisplay));
            }
        }

        public string TotalSizeDisplay => FileSizeFormatter.Format(TotalSize);
        public int TotalCount => Items?.Count ?? 0;

        public ICommand RefreshCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand RestoreAllCommand { get; }
        public ICommand PurgeOldCommand { get; }
        public ICommand PurgeAllCommand { get; }

        public QuarantineViewModel()
        {
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsBusy);
            RestoreCommand = new RelayCommand<QuarantineItemVm>(async (it) => await RestoreAsync(it), _ => !IsBusy);
            RestoreAllCommand = new RelayCommand(async () => await RestoreAllAsync(), () => !IsBusy);
            PurgeOldCommand = new RelayCommand(async () => await PurgeOldAsync(), () => !IsBusy);
            PurgeAllCommand = new RelayCommand(async () => await PurgeAllAsync(), () => !IsBusy);
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            IsBusy = true;
            Status = "加载中...";
            await Task.Run(() =>
            {
                var list = QuarantineService.List();
                var vms = new ObservableCollection<QuarantineItemVm>();
                long total = 0;
                foreach (var it in list)
                {
                    vms.Add(new QuarantineItemVm
                    {
                        QuarantinePath = it.QuarantinePath,
                        OriginalPath = it.OriginalPath,
                        Size = it.Size,
                        QuarantinedAt = it.QuarantinedAt
                    });
                    total += it.Size;
                }
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    Items = vms;
                    TotalSize = total;
                    Status = vms.Count == 0 ? "保险箱为空" : $"共 {vms.Count} 个文件，{TotalSizeDisplay}";
                });
            });
            IsBusy = false;
        }

        private async Task RestoreAsync(QuarantineItemVm it)
        {
            if (it == null) return;
            IsBusy = true;
            bool ok = false;
            await Task.Run(() => ok = QuarantineService.Restore(it.QuarantinePath));
            Status = ok ? $"已恢复：{it.OriginalPath}" : $"恢复失败：{it.OriginalPath}";
            IsBusy = false;
            await RefreshAsync();
        }

        private async Task RestoreAllAsync()
        {
            var selected = Items.Where(x => x.IsSelected).ToList();
            var targets = selected.Count > 0 ? selected : Items.ToList();
            if (targets.Count == 0) { Status = "没有可恢复的文件"; return; }

            IsBusy = true;
            int okCount = 0;
            await Task.Run(() =>
            {
                foreach (var t in targets)
                    if (QuarantineService.Restore(t.QuarantinePath)) okCount++;
            });
            Status = $"已恢复 {okCount}/{targets.Count} 个文件";
            IsBusy = false;
            await RefreshAsync();
        }

        private async Task PurgeOldAsync()
        {
            IsBusy = true;
            (int count, long bytes) = (0, 0);
            await Task.Run(() => (count, bytes) = QuarantineService.PurgeOlderThan(TimeSpan.FromDays(30)));
            Status = count > 0
                ? $"已自动清理 {count} 个超过30天的文件（{FileSizeFormatter.Format(bytes)}）"
                : "没有超过30天的文件";
            IsBusy = false;
            await RefreshAsync();
        }

        private async Task PurgeAllAsync()
        {
            if (Items.Count == 0) { Status = "保险箱为空"; return; }
            var r = MessageBox.Show(
                $"确定要彻底删除保险箱中的 {Items.Count} 个文件吗？\n此操作不可恢复！",
                "清空保险箱", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            IsBusy = true;
            (int count, long bytes) = (0, 0);
            await Task.Run(() => (count, bytes) = QuarantineService.PurgeAll());
            Status = $"已彻底删除 {count} 个文件（{FileSizeFormatter.Format(bytes)}）";
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
