using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Models;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class TempCleanViewModel : ViewModelBase
    {
        private readonly TempFileCleaner _cleaner = new TempFileCleaner();
        private ObservableCollection<CleanTarget> _targets;
        private bool _isScanning;
        private bool _isCleaning;
        private int _progress;
        private string _progressText;
        private long _totalReclaimable;
        private long _selectedReclaimable;
        private string _resultMessage;
        private bool _useRecycleBin;
        private CancellationTokenSource _cts;

        public ObservableCollection<CleanTarget> Targets
        {
            get => _targets;
            set => Set(ref _targets, value);
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

        /// <summary>扫描或清理进行中（用于显示进度区）</summary>
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
        /// 分类删除策略：临时文件默认移入「保险箱」软删除（QuarantineService，速度快、不触发桌面外壳刷新/黑屏、可恢复）。
        /// 勾选「移入系统回收站」后改为走系统回收站（SHFileOperation），代价是极慢且可能拖垮 Explorer。
        /// </summary>
        public bool UseRecycleBin
        {
            get => _useRecycleBin;
            set => Set(ref _useRecycleBin, value);
        }

        public ICommand ScanCommand { get; }
        public ICommand CleanCommand { get; }
        public ICommand CancelCommand { get; }

        /// <summary>
        /// 「全选」复选框状态（二态）：true=全部勾选，false=未全选/全不选。
        /// 点击一次全选，再点一次全不选；下方某项手动取消后自动变为未勾选。
        /// </summary>
        private bool _selectAllChecked = false;
        public bool SelectAllChecked
        {
            get => _selectAllChecked;
            set
            {
                if (value == _selectAllChecked)
                {
                    // 用户点击已处于全选/全不选状态的框：再次点击执行反向操作
                    SetSelection(!value);
                    return;
                }
                SetSelection(value);
            }
        }

        public TempCleanViewModel()
        {
            Targets = new ObservableCollection<CleanTarget>(_cleaner.GetDefaultTargets());
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

            // 分类扫描实时进度：把累计已扫字节/文件数写回对应卡片，并实时刷新总/已选可释放量，
            // 让用户直观看到“下面分类里 MB 数字在随扫描增长”。
            _cleaner.OnTargetScanProgress = (t, size, cnt) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    t.SizeBytes = size;
                    t.FileCount = cnt;
                    long total = 0, sel = 0;
                    foreach (var x in Targets)
                    {
                        total += x.SizeBytes;
                        if (x.IsSelected) sel += x.SizeBytes;
                    }
                    TotalReclaimable = total;
                    SelectedReclaimable = sel;
                });
            };

            // 勾选变化时更新统计
            foreach (var t in Targets)
                t.PropertyChanged += OnTargetSelectionChanged;

            // 初始化「全选」复选框状态：只有全部勾选才显示勾选，否则未勾选
            bool anyInit = false, allInit = true;
            foreach (var t in Targets)
            {
                if (t.IsSelected) anyInit = true; else allInit = false;
            }
            _selectAllChecked = anyInit && allInit;
        }

        private void OnTargetSelectionChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(CleanTarget.IsSelected)) return;
            UpdateSelectedSize();
            // 用户手动改某项勾选后，重新计算「全选」复选框：只有全勾才显示勾选
            bool any = false, all = true;
            foreach (var t in Targets)
            {
                if (t.IsSelected) any = true; else all = false;
            }
            _selectAllChecked = any && all;
            OnPropertyChanged(nameof(SelectAllChecked));
        }

        private void SetSelection(bool selected)
        {
            foreach (var t in Targets)
                t.IsSelected = selected;
            UpdateSelectedSize();
            // 同步「全选」复选框为确定态，避免与下方逐项勾选产生反馈循环
            _selectAllChecked = selected;
            OnPropertyChanged(nameof(SelectAllChecked));
        }

        private void UpdateSelectedSize()
        {
            long total = 0;
            foreach (var t in Targets)
            {
                if (t.IsSelected)
                    total += t.SizeBytes;
            }
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
                await _cleaner.ScanAsync(new System.Collections.Generic.List<CleanTarget>(Targets), _cts.Token);
                TotalReclaimable = 0;
                foreach (var t in Targets)
                    TotalReclaimable += t.SizeBytes;
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
            var selected = new System.Collections.Generic.List<CleanTarget>(Targets);
            var totalSelected = 0L;
            foreach (var t in selected)
                if (t.IsSelected) totalSelected += t.SizeBytes;

            if (totalSelected == 0)
            {
                ResultMessage = "请先选择要清理的项目";
                return;
            }

            var confirm = MessageBox.Show(
                $"即将清理 {FileSizeFormatter.Format(totalSelected)} 的数据。\n\n" +
                (UseRecycleBin
                    ? "文件将移至系统回收站，可恢复（速度较慢，大量文件时可能拖慢资源管理器）。"
                    : "系统级临时文件（系统临时文件/更新缓存/错误报告等）将直接删除（不可恢复，最快）；\n用户临时文件将移入 DiskCleaner 保险箱，可随时从『保险箱』页恢复。\n二者均不触发桌面刷新。") +
                "\n\n确认继续？",
                "清理确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            IsCleaning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var (freed, recycled, direct, quar, failed) = await _cleaner.CleanAsync(selected, UseRecycleBin, _cts.Token);
                string msg;
                if (UseRecycleBin)
                    msg = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，{recycled} 个文件已移入回收站";
                else
                    msg = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，{direct} 个文件已直接删除、{quar} 个文件已移入保险箱（可在『保险箱』页恢复）";
                if (failed > 0)
                    msg += $"（{failed} 个文件未能删除：可能被占用或需要管理员权限）";
                ResultMessage = msg;

                // 重新计算统计
                TotalReclaimable = 0;
                foreach (var t in Targets)
                    TotalReclaimable += t.SizeBytes;
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
