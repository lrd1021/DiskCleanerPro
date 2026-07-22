using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiskCleaner.Helpers;
using DiskCleaner.Services;

namespace DiskCleaner.ViewModels
{
    public class RecycleBinViewModel : ViewModelBase
    {
        private readonly RecycleBinManager _manager = new RecycleBinManager();
        private RecycleBinInfo _cDriveInfo;
        private bool _isBusy;
        private string _resultMessage;
        private int _progress;
        private string _progressText;

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
        public ICommand EmptyAllCommand { get; }

        public RecycleBinViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
            EmptyCommand = new RelayCommand(async () => await EmptyAsync(false), () => !IsBusy && !IsEmpty);
            EmptyAllCommand = new RelayCommand(async () => await EmptyAsync(true), () => !IsBusy && !IsEmpty);

            _manager.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct;
                    ProgressText = msg;
                });
            };

            Refresh();
        }

        public void Refresh()
        {
            CDriveInfo = _manager.Query("C:\\");
        }

        private async Task EmptyAsync(bool allDrives)
        {
            var msg = allDrives ? "所有盘的回收站" : "C盘回收站";
            var confirm = MessageBox.Show(
                $"即将永久清空{msg}中的 {SizeDisplay} 数据。\n\n" +
                "⚠️ 此操作不可恢复！\n\n确认继续？",
                "清空回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            IsBusy = true;
            Progress = 0;

            try
            {
                bool success = await Task.Run(() =>
                    allDrives ? _manager.EmptyAll() : _manager.Empty("C:\\"));

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
}
