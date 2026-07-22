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
        private bool _permanentDelete;
        private CancellationTokenSource _cts;

        public ObservableCollection<CleanTarget> Targets
        {
            get => _targets;
            set => Set(ref _targets, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => Set(ref _isScanning, value);
        }

        public bool IsCleaning
        {
            get => _isCleaning;
            set => Set(ref _isCleaning, value);
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

        public bool PermanentDelete
        {
            get => _permanentDelete;
            set => Set(ref _permanentDelete, value);
        }

        public ICommand ScanCommand { get; }
        public ICommand CleanCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand SelectNoneCommand { get; }
        public ICommand CancelCommand { get; }

        public TempCleanViewModel()
        {
            Targets = new ObservableCollection<CleanTarget>(_cleaner.GetDefaultTargets());
            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning && !IsCleaning);
            CleanCommand = new RelayCommand(async () => await CleanAsync(), () => !IsScanning && !IsCleaning);
            SelectAllCommand = new RelayCommand(() => SetSelection(true));
            SelectNoneCommand = new RelayCommand(() => SetSelection(false));
            CancelCommand = new RelayCommand(() => Cancel());

            _cleaner.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };

            // 勾选变化时更新统计
            foreach (var t in Targets)
                t.PropertyChanged += OnTargetSelectionChanged;
        }

        private void OnTargetSelectionChanged(object s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CleanTarget.IsSelected))
                UpdateSelectedSize();
        }

        private void SetSelection(bool selected)
        {
            foreach (var t in Targets)
                t.IsSelected = selected;
            UpdateSelectedSize();
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
            catch (System.OperationCanceledException) { /* 用户取消 — 正常流程 */ }
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
                (PermanentDelete ? "⚠️ 您选择了永久删除，文件不可恢复！" : "文件将移至回收站，可恢复。") +
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
                var (freed, deleted) = await _cleaner.CleanAsync(selected, PermanentDelete, _cts.Token);
                ResultMessage = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，删除 {deleted} 个文件";

                // 重新计算统计
                TotalReclaimable = 0;
                foreach (var t in Targets)
                    TotalReclaimable += t.SizeBytes;
                UpdateSelectedSize();
            }
            catch (System.OperationCanceledException) { /* 用户取消 — 正常流程 */ }
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
