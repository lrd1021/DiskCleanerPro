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
    public class SoftwareViewModel : ViewModelBase
    {
        private readonly SoftwareManager _manager = new SoftwareManager();
        private ObservableCollection<SoftwareInfo> _softwareList;
        private ObservableCollection<SoftwareInfo> _filteredSoftware;
        private bool _isLoading;
        private int _progress;
        private string _progressText;
        private string _searchText;
        private string _resultMessage;
        private SoftwareInfo _selectedSoftware;
        private CancellationTokenSource _cts;

        public ObservableCollection<SoftwareInfo> SoftwareList
        {
            get => _softwareList;
            set => Set(ref _softwareList, value);
        }

        public ObservableCollection<SoftwareInfo> FilteredSoftware
        {
            get => _filteredSoftware;
            set => Set(ref _filteredSoftware, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
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

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value))
                    ApplyFilter();
            }
        }

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        public SoftwareInfo SelectedSoftware
        {
            get => _selectedSoftware;
            set => Set(ref _selectedSoftware, value);
        }

        public ICommand LoadCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand OpenLocationCommand { get; }
        public ICommand CancelCommand { get; }

        public SoftwareViewModel()
        {
            LoadCommand = new RelayCommand(async () => await LoadAsync(), () => !IsLoading);
            UninstallCommand = new RelayCommand<SoftwareInfo>(async (s) => await UninstallAsync(s));
            OpenLocationCommand = new RelayCommand<SoftwareInfo>(s => OpenLocation(s));

            _manager.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct;
                    ProgressText = msg;
                });
            };
        }

        private async Task LoadAsync()
        {
            IsLoading = true;
            Progress = 0;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var list = await _manager.GetInstalledSoftwareAsync(_cts.Token);
                SoftwareList = new ObservableCollection<SoftwareInfo>(list);
                ApplyFilter();
                ResultMessage = $"共 {list.Count} 个已安装软件";
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"加载失败：{ex.Message}";
            }
            finally
            {
                IsLoading = false;
                Progress = 100;
            }
        }

        private void ApplyFilter()
        {
            if (SoftwareList == null)
            {
                FilteredSoftware = new ObservableCollection<SoftwareInfo>();
                return;
            }

            if (string.IsNullOrEmpty(SearchText))
            {
                FilteredSoftware = new ObservableCollection<SoftwareInfo>(SoftwareList);
            }
            else
            {
                var filtered = new ObservableCollection<SoftwareInfo>();
                foreach (var s in SoftwareList)
                {
                    if ((s.Name?.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase) == true) ||
                        (s.Publisher?.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase) == true))
                        filtered.Add(s);
                }
                FilteredSoftware = filtered;
            }
        }

        private async Task UninstallAsync(SoftwareInfo software)
        {
            if (software == null) return;

            var confirm = MessageBox.Show(
                $"确定要卸载 {software.Name} 吗？\n\n" +
                $"发布者：{software.Publisher}\n" +
                $"版本：{software.Version}\n" +
                $"大小：{software.SizeDisplay}\n\n" +
                "系统将启动该软件的卸载程序，请按照提示完成卸载。",
                "卸载确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsLoading = true;
            ProgressText = $"正在启动卸载程序：{software.Name}";

            await Task.Run(() =>
            {
                bool success = _manager.Uninstall(software);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ResultMessage = success ? $"已启动 {software.Name} 的卸载程序" : $"卸载 {software.Name} 失败";
                });
            });

            IsLoading = false;
            Progress = 100;

            // 提示用户卸载完成后刷新列表
            var refresh = MessageBox.Show(
                "卸载程序已启动。卸载完成后是否刷新软件列表？",
                "刷新列表", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (refresh == MessageBoxResult.Yes)
                await LoadAsync();
        }

        private void OpenLocation(SoftwareInfo software)
        {
            if (software == null) return;
            bool success = _manager.OpenInstallLocation(software);
            if (!success)
                ResultMessage = $"未找到 {software.Name} 的安装目录";
        }

        private void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "正在取消...";
        }
    }
}
