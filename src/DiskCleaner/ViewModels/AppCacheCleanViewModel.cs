using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Models;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class AppCacheCleanViewModel : ViewModelBase
    {
        private readonly AppCacheCleaner _cleaner = new AppCacheCleaner();
        private ObservableCollection<AppCacheGroup> _groups;
        private bool _isScanning;
        private bool _isCleaning;
        private int _progress;
        private string _progressText;
        private long _totalReclaimable;
        private long _selectedReclaimable;
        private string _resultMessage;
        private bool _useRecycleBin;
        private CancellationTokenSource _cts;
        private bool _selectAllChecked;

        public ObservableCollection<AppCacheGroup> Groups
        {
            get => _groups;
            set => Set(ref _groups, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set { Set(ref _isScanning, value); OnPropertyChanged(nameof(IsBusy)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }

        public bool IsCleaning
        {
            get => _isCleaning;
            set { Set(ref _isCleaning, value); OnPropertyChanged(nameof(IsBusy)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
        }

        public bool IsBusy => IsScanning || IsCleaning;

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

        public long TotalReclaimable
        {
            get => _totalReclaimable;
            set
            {
                Set(ref _totalReclaimable, value);
                OnPropertyChanged(nameof(TotalReclaimableDisplay));
            }
        }

        public long SelectedReclaimable
        {
            get => _selectedReclaimable;
            set
            {
                Set(ref _selectedReclaimable, value);
                OnPropertyChanged(nameof(SelectedReclaimableDisplay));
            }
        }

        public string TotalReclaimableDisplay => FileSizeFormatter.Format(TotalReclaimable);
        public string SelectedReclaimableDisplay => FileSizeFormatter.Format(SelectedReclaimable);

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        /// <summary>
        /// 应用缓存属用户数据，清理默认移入「保险箱」软删除（可恢复、快、不黑屏）；
        /// 勾选「改用系统回收站」后走系统回收站（慢、可能拖慢 Explorer）。
        /// </summary>
        public bool UseRecycleBin
        {
            get => _useRecycleBin;
            set => Set(ref _useRecycleBin, value);
        }

        public ICommand ScanCommand { get; }
        public ICommand CleanCommand { get; }
        public ICommand CancelCommand { get; }

        /// <summary>未检测到微信/QQ 缓存数据时显示提示（true=无数据）。</summary>
        public bool IsEmpty { get; private set; }

        /// <summary>「全选」复选框：true=全部勾选，false=未全选/全不选。点击已选状态执行反向操作。</summary>
        public bool SelectAllChecked
        {
            get => _selectAllChecked;
            set
            {
                if (value == _selectAllChecked)
                {
                    SetSelection(!value);
                    return;
                }
                SetSelection(value);
            }
        }

        public AppCacheCleanViewModel()
        {
            Groups = new ObservableCollection<AppCacheGroup>(_cleaner.GetGroups());
            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning && !IsCleaning);
            CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsScanning && !IsCleaning);
            CancelCommand = new RelayCommand(() => Cancel());

            _cleaner.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };

            _cleaner.OnTargetScanProgress = (t, size, cnt) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    t.SizeBytes = size;
                    t.FileCount = cnt;
                    long total = 0, sel = 0;
                    foreach (var x in AllTargets())
                    {
                        total += x.SizeBytes;
                        if (x.IsSelected) sel += x.SizeBytes;
                    }
                    TotalReclaimable = total;
                    SelectedReclaimable = sel;
                });
            };

            foreach (var t in AllTargets())
                t.PropertyChanged += OnTargetSelectionChanged;

            var all = AllTargets();
            _selectAllChecked = all.Any(x => x.IsSelected) && all.All(x => x.IsSelected);
            IsEmpty = !Groups.Any(g => g.Targets.Count > 0);
        }

        private List<CleanTarget> AllTargets() => Groups.SelectMany(g => g.Targets).ToList();

        private void OnTargetSelectionChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CleanTarget.IsSelected)) return;
            UpdateSelectedSize();
            var all = AllTargets();
            bool any = all.Any(x => x.IsSelected);
            bool every = all.All(x => x.IsSelected);
            _selectAllChecked = any && every;
            OnPropertyChanged(nameof(SelectAllChecked));
        }

        private void SetSelection(bool selected)
        {
            foreach (var t in AllTargets())
                t.IsSelected = selected;
            UpdateSelectedSize();
            _selectAllChecked = selected;
            OnPropertyChanged(nameof(SelectAllChecked));
        }

        private void UpdateSelectedSize()
        {
            long total = 0;
            foreach (var t in AllTargets())
                if (t.IsSelected) total += t.SizeBytes;
            SelectedReclaimable = total;
        }

        private async Task ScanAsync()
        {
            IsScanning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                await _cleaner.ScanAsync(new List<CleanTarget>(AllTargets()), _cts.Token);
                TotalReclaimable = AllTargets().Sum(t => t.SizeBytes);
                UpdateSelectedSize();
                ResultMessage = $"扫描完成，共可释放 {TotalReclaimableDisplay}";
            }
            catch (System.OperationCanceledException) { ResultMessage = "扫描已取消"; }
            catch (System.Exception ex)
            {
                ResultMessage = $"扫描中断：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
                Progress = 100;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task CleanAsync()
        {
            var targets = AllTargets();
            long totalSelected = targets.Where(t => t.IsSelected).Sum(t => t.SizeBytes);

            if (totalSelected == 0)
            {
                ResultMessage = "请先扫描并选择要清理的项目";
                return;
            }

            var confirm = MessageBox.Show(
                $"即将清理 {FileSizeFormatter.Format(totalSelected)} 的应用缓存数据。\n\n" +
                (UseRecycleBin
                    ? "文件将移至系统回收站，可恢复（速度较慢，大量文件时可能拖慢资源管理器）。"
                    : "应用缓存/媒体默认移入 DiskCleaner 保险箱，可随时从『保险箱』页恢复。\n" +
                      "注意：图片/视频/接收的文件被清理后需重新从聊天对方下载，文字聊天记录不受影响。\n" +
                      "建议清理前先退出微信/QQ，避免文件被占用无法删除。") +
                "\n\n确认继续？",
                "应用专清确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            IsCleaning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var (freed, recycled, direct, quar, failed) = await _cleaner.CleanAsync(targets, UseRecycleBin, _cts.Token);
                string msg;
                if (UseRecycleBin)
                    msg = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，{recycled} 个文件已移入回收站";
                else
                    msg = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，{direct} 个文件已直接删除、{quar} 个文件已移入保险箱（可在『保险箱』页恢复）";
                if (failed > 0)
                    msg += $"（{failed} 个文件未能删除：可能被微信/QQ占用，建议退出应用后重试）";
                ResultMessage = msg;

                TotalReclaimable = AllTargets().Sum(t => t.SizeBytes);
                UpdateSelectedSize();
            }
            catch (System.OperationCanceledException) { ResultMessage = "清理已取消"; }
            catch (System.Exception ex)
            {
                ResultMessage = $"清理中断：{ex.Message}";
            }
            finally
            {
                IsCleaning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Progress = 100;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "正在取消...";
        }
    }
}
