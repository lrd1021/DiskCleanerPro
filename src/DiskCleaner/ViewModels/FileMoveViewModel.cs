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
    public class FileMoveViewModel : ViewModelBase
    {
        private readonly FileMoverService _service = new FileMoverService();
        private ObservableCollection<LargeFileInfo> _largeFiles;
        private ObservableCollection<MoveTask> _tasks;
        private bool _isScanning;
        private bool _isMoving;
        private int _progress;
        private string _progressText;
        private long _minFileSizeGB = 1;
        private string _scanPath;
        private string _targetDrive;
        private bool _createSymlink = true;
        private string _resultMessage;
        private CancellationTokenSource _cts;

        public ObservableCollection<LargeFileInfo> LargeFiles
        {
            get => _largeFiles;
            set => Set(ref _largeFiles, value);
        }

        public ObservableCollection<MoveTask> Tasks
        {
            get => _tasks;
            set => Set(ref _tasks, value);
        }

        public bool IsScanning
        {
            get => _isScanning;
            set => Set(ref _isScanning, value);
        }

        public bool IsMoving
        {
            get => _isMoving;
            set => Set(ref _isMoving, value);
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

        public long MinFileSizeGB
        {
            get => _minFileSizeGB;
            set => Set(ref _minFileSizeGB, value);
        }

        public string ScanPath
        {
            get => _scanPath ??= System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            set => Set(ref _scanPath, value);
        }

        public string TargetDrive
        {
            get => _targetDrive;
            set => Set(ref _targetDrive, value);
        }

        public bool CreateSymlink
        {
            get => _createSymlink;
            set => Set(ref _createSymlink, value);
        }

        public string ResultMessage
        {
            get => _resultMessage;
            set => Set(ref _resultMessage, value);
        }

        public ObservableCollection<string> AvailableDrives { get; } = new ObservableCollection<string>();

        public ICommand ScanCommand { get; }
        public ICommand MoveCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand BrowseCommand { get; }

        public FileMoveViewModel()
        {
            ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning && !IsMoving);
            MoveCommand = new RelayCommand(async () => await MoveAsync(), () => !IsScanning && !IsMoving);
            CancelCommand = new RelayCommand(() => Cancel());
            BrowseCommand = new RelayCommand(() =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    SelectedPath = ScanPath,
                    Description = "选择要扫描大文件的目录"
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    ScanPath = dialog.SelectedPath;
            });

            _service.OnProgress = (pct, msg) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    Progress = pct < 0 ? Progress : pct;
                    ProgressText = msg;
                });
            };

            Tasks = new ObservableCollection<MoveTask>();
            LoadAvailableDrives();
        }

        private void LoadAvailableDrives()
        {
            AvailableDrives.Clear();
            foreach (var drive in _service.GetAvailableTargetDrives())
            {
                AvailableDrives.Add(drive.Name);
            }
            if (AvailableDrives.Count > 0)
                TargetDrive = AvailableDrives[0];
        }

        private async Task ScanAsync()
        {
            if (!System.IO.Directory.Exists(ScanPath))
            {
                ResultMessage = "目录不存在";
                return;
            }

            IsScanning = true;
            Progress = 0;
            ResultMessage = "";
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            LargeFiles = new ObservableCollection<LargeFileInfo>();

            try
            {
                var files = await _service.ScanLargeFilesAsync(ScanPath, (long)MinFileSizeGB * 1024 * 1024 * 1024, _cts.Token);
                LargeFiles = new ObservableCollection<LargeFileInfo>(files);
                ResultMessage = $"找到 {files.Count} 个大于 {MinFileSizeGB}GB 的文件";
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

        private async Task MoveAsync()
        {
            if (LargeFiles == null || LargeFiles.Count == 0)
            {
                ResultMessage = "请先扫描大文件";
                return;
            }

            if (string.IsNullOrEmpty(TargetDrive))
            {
                ResultMessage = "请选择目标盘";
                return;
            }

            var selected = new System.Collections.Generic.List<LargeFileInfo>();
            foreach (var f in LargeFiles)
                if (f.IsSelected) selected.Add(f);

            if (selected.Count == 0)
            {
                ResultMessage = "请勾选要搬移的文件";
                return;
            }

            long totalSize = 0;
            foreach (var f in selected) totalSize += f.SizeBytes;

            string targetDir = System.IO.Path.Combine(TargetDrive, "MovedFromC");
            var confirm = MessageBox.Show(
                $"即将搬移 {selected.Count} 个文件（共 {FileSizeFormatter.Format(totalSize)}）到 {targetDir}\n\n" +
                (CreateSymlink
                    ? "✅ 将创建符号链接，原路径仍可访问"
                    : "⚠️ 不创建符号链接，原路径将失效，依赖这些文件的应用可能出错") +
                "\n\n确认继续？",
                "搬移确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            IsMoving = true;
            Progress = 0;
            Tasks.Clear();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            long totalMoved = 0;
            int successCount = 0;

            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Progress = (int)((float)i / selected.Count * 100);
                    ProgressText = $"搬移 {i + 1}/{selected.Count}：{selected[i].FileName}";

                    var task = await _service.MoveFileAsync(selected[i], targetDir, CreateSymlink, _cts.Token);
                    Tasks.Add(task);
                    if (task.Status == MoveTask.MoveStatus.Completed)
                    {
                        totalMoved += task.FileSizeBytes;
                        successCount++;
                    }
                }

                ResultMessage = $"搬移完成！成功 {successCount}/{selected.Count} 个文件，共释放 {FileSizeFormatter.Format(totalMoved)}";
                LoadAvailableDrives(); // 刷新可用空间
            }
            catch (System.OperationCanceledException) { /* 用户取消 */ }
            catch (System.Exception ex)
            {
                ResultMessage = $"搬移中断：{ex.Message}";
            }
            finally
            {
                IsMoving = false;
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
