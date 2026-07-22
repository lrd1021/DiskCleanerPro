using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class BrowserCacheViewModel : ViewModelBase
    {
        private readonly BrowserCacheCleaner _cleaner = new BrowserCacheCleaner();
        private ObservableCollection<BrowserInfo> _browsers;
        private bool _isScanning;
        private bool _isCleaning;
        private int _progress;
        private string _progressText;
        private long _totalCache;
        private string _resultMessage;
        private bool _permanentDelete;
        private CancellationTokenSource _cts;

        public ObservableCollection<BrowserInfo> Browsers
        {
            get => _browsers;
            set => Set(ref _browsers, value);
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

        public long TotalCache
        {
            get => _totalCache;
            set
            {
                Set(ref _totalCache, value);
                OnPropertyChanged(nameof(TotalCacheDisplay));
            }
        }

        public string TotalCacheDisplay => FileSizeFormatter.Format(TotalCache);

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
        public ICommand CancelCommand { get; }

        public BrowserCacheViewModel()
        {
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

            RefreshBrowsers();
        }

        public void RefreshBrowsers()
        {
            var browsers = _cleaner.GetSupportedBrowsers();
            Browsers = new ObservableCollection<BrowserInfo>(browsers);
        }

        private async Task ScanAsync()
        {
            if (Browsers.Count == 0)
            {
                ResultMessage = "未检测到支持的浏览器";
                return;
            }

            IsScanning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                await _cleaner.ScanAsync(new System.Collections.Generic.List<BrowserInfo>(Browsers), _cts.Token);
                TotalCache = 0;
                foreach (var b in Browsers)
                    TotalCache += b.CacheSizeBytes;
                ResultMessage = $"扫描完成，共可释放 {TotalCacheDisplay}";
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"扫描中断：{ex.Message}";
            }
            finally
            {
                IsScanning = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Progress = 100;
            }
        }

        private async Task CleanAsync()
        {
            long totalSelected = 0;
            foreach (var b in Browsers)
                if (b.IsSelected) totalSelected += b.CacheSizeBytes;

            if (totalSelected == 0)
            {
                ResultMessage = "请先选择要清理的浏览器";
                return;
            }

            // 建议关闭浏览器
            var confirm = MessageBox.Show(
                $"即将清理 {FileSizeFormatter.Format(totalSelected)} 的浏览器缓存。\n\n" +
                "建议先关闭正在运行的浏览器，否则部分缓存文件可能被占用无法删除。\n\n" +
                (PermanentDelete ? "⚠️ 永久删除不可恢复！" : "文件将移至回收站。") +
                "\n\n确认继续？",
                "清理确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsCleaning = true;
            Progress = 0;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var (freed, deleted) = await _cleaner.CleanAsync(
                    new System.Collections.Generic.List<BrowserInfo>(Browsers), PermanentDelete, _cts.Token);
                ResultMessage = $"清理完成！释放 {FileSizeFormatter.Format(freed)}，删除 {deleted} 个文件";

                TotalCache = 0;
                foreach (var b in Browsers)
                    TotalCache += b.CacheSizeBytes;
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
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
